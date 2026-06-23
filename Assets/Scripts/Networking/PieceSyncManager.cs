using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Util;

// Single NetworkObject that batch-syncs all 500+ game pieces from host to clients.
// No per-piece NetworkObject is created — all state is packed into custom named messages.
//
// Protocol:
//   MSG_REG  (Reliable, every 5s)   — maps server piece IDs to world positions+type so
//                                     clients can build an id→localPiece lookup table.
//   MSG_DELTA (Unreliable, ~10 Hz)  — per changed piece: id, pos, rot, flags, vel.
//                                     Sleeping/unchanged pieces are skipped entirely.
//
// Piece identification: host assigns sequential IDs at registration time and broadcasts
// each piece's position+type. Clients find the nearest local piece of the same type
// within 2 m to claim that ID. Works because both host and client run the same
// SpawnGamePiece logic and start pieces at the same world positions.
//
// Client pieces are set kinematic so they don't fight the received positions.
// Stationary pieces (held by robots) are excluded from the delta — they
// follow robot transforms which are synced separately by GameNetworkManager.

[RequireComponent(typeof(NetworkObject))]
public class PieceSyncManager : NetworkBehaviour
{
    [Tooltip("Send piece deltas every N FixedUpdate ticks (3 ≈ 20 Hz at 50 Hz fixed rate)")]
    [SerializeField] private int syncEveryNFixed = 3;
    [Tooltip("Re-send full registration so newly spawned pieces get mapped")]
    [SerializeField] private float registrationInterval = 5f;

    private const string MSG_REG    = "BoarSim.PieceReg";
    private const string MSG_DELTA  = "BoarSim.PieceDelta";
    private const string MSG_DELETE = "BoarSim.PieceDelete";

    // Server-side state
    private readonly Dictionary<GamePiece, ushort> _serverIds = new();
    private ushort _nextId;
    private GamePiece[] _serverPieces = System.Array.Empty<GamePiece>();
    private readonly Dictionary<ushort, Vector3>    _lastSentPos = new();
    private readonly Dictionary<ushort, Quaternion> _lastSentRot = new();
    private readonly HashSet<ushort> _pendingDeleteIds = new();
    private int _fixedTick;

    // Client-side state
    private readonly Dictionary<ushort, GamePiece> _clientMap = new();
    private GamePiece[] _clientPieces = System.Array.Empty<GamePiece>();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            StartCoroutine(RegistrationLoop());
        }

        if (IsClient && !IsServer)
        {
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_REG,    OnRegistrationReceived);
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DELTA,  OnDeltaReceived);
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DELETE, OnDeleteReceived);
            StartCoroutine(MakeClientPiecesKinematic());
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsClient && !IsServer)
        {
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_REG);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DELTA);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DELETE);
        }
    }

    // ── Server: registration ──────────────────────────────────────────────────

    private IEnumerator RegistrationLoop()
    {
        // Wait for LoadMatch to finish spawning field + initial pieces
        yield return new WaitForSeconds(2f);

        while (IsServer)
        {
            RefreshServerPieces();
            if (NetworkManager.ConnectedClientsList.Count > 1)
            {
                SendRegistration();
                SendDeletions();
            }
            yield return new WaitForSeconds(registrationInterval);
        }
    }

    private void RefreshServerPieces()
    {
        _serverPieces = FindObjectsOfType<GamePiece>();
        var found = new HashSet<GamePiece>(_serverPieces);

        // Detect pieces that were registered but have since been destroyed (e.g. scored).
        var stale = new List<GamePiece>();
        foreach (var kvp in _serverIds)
        {
            if (kvp.Key == null || !found.Contains(kvp.Key))
            {
                _pendingDeleteIds.Add(kvp.Value);
                stale.Add(kvp.Key);
            }
        }
        foreach (var k in stale) _serverIds.Remove(k);

        foreach (var piece in _serverPieces)
        {
            if (!_serverIds.ContainsKey(piece))
                _serverIds[piece] = _nextId++;
        }
    }

    private void SendDeletions()
    {
        if (_pendingDeleteIds.Count == 0) return;
        int bufSize = 2 + _pendingDeleteIds.Count * 2;
        using var writer = new FastBufferWriter(bufSize, Allocator.Temp);
        writer.WriteValueSafe((ushort)_pendingDeleteIds.Count);
        foreach (var id in _pendingDeleteIds)
            writer.WriteValueSafe(id);
        NetworkManager.CustomMessagingManager.SendNamedMessageToAll(MSG_DELETE, writer, NetworkDelivery.Reliable);
        _pendingDeleteIds.Clear();
    }

    private void SendRegistration()
    {
        // Per entry: ushort id (2) + byte type (1) + 3 floats pos (12) = 15 bytes
        int bufSize = 2 + _serverPieces.Length * 15;
        using var writer = new FastBufferWriter(bufSize, Allocator.Temp);

        writer.WriteValueSafe((ushort)_serverPieces.Length);

        foreach (var piece in _serverPieces)
        {
            if (!_serverIds.TryGetValue(piece, out ushort id)) continue;
            var pos = piece.rb != null ? piece.rb.position : piece.transform.position;

            writer.WriteValueSafe(id);
            writer.WriteValueSafe((byte)piece.pieceType);
            writer.WriteValueSafe(pos.x);
            writer.WriteValueSafe(pos.y);
            writer.WriteValueSafe(pos.z);
        }

        NetworkManager.CustomMessagingManager.SendNamedMessageToAll(MSG_REG, writer, NetworkDelivery.Reliable);
    }

    // ── Server: delta sync ────────────────────────────────────────────────────

    private void FixedUpdate()
    {
        if (!IsServer) return;
        if (++_fixedTick < syncEveryNFixed) return;
        _fixedTick = 0;
        SendDelta();
    }

    private void SendDelta()
    {
        if (NetworkManager.ConnectedClientsList.Count <= 1) return;

        // Detect pieces destroyed since the last registration scan and immediately
        // broadcast their IDs. Without this, clients see scored/consumed pieces for
        // up to the full 5-second RegistrationLoop interval.
        bool anyDestroyed = false;
        foreach (var kvp in _serverIds)
            if (kvp.Key == null) { anyDestroyed = true; break; }
        if (anyDestroyed)
        {
            var stale = new System.Collections.Generic.List<GamePiece>();
            foreach (var kvp in _serverIds)
                if (kvp.Key == null) { _pendingDeleteIds.Add(kvp.Value); stale.Add(kvp.Key); }
            foreach (var k in stale) _serverIds.Remove(k);
            _serverPieces = FindObjectsOfType<GamePiece>();
            SendDeletions();
        }

        if (_serverPieces.Length == 0) return;

        // Worst case per entry: id(2) + pos(12) + rot(16) + flags(1) + vel(12) + angVel(12) = 55
        int maxBuf = _serverPieces.Length * 56;
        using var writer = new FastBufferWriter(maxBuf, Allocator.Temp);

        for (int i = 0; i < _serverPieces.Length; i++)
        {
            var piece = _serverPieces[i];
            if (piece == null || piece.rb == null) continue;
            if (!_serverIds.TryGetValue(piece, out ushort id)) continue;

            bool isStationary = piece.state == GamePieceState.Stationary;

            // Stationary pieces (held by robot) are no longer skipped — they need to be
            // synced so the other screen sees the pickup. They are sent as "sleeping"
            // (no velocity data) since they move with the robot, not independently.
            bool sleeping = !isStationary && piece.rb.IsSleeping();

            // Skip pieces whose position/rotation hasn't changed: sleeping world pieces
            // and stationary pieces on a non-moving robot both qualify.
            bool posUnchanged =
                _lastSentPos.TryGetValue(id, out var lp) &&
                _lastSentRot.TryGetValue(id, out var lr) &&
                (piece.rb.position - lp).sqrMagnitude < 0.0001f &&
                Quaternion.Dot(piece.rb.rotation, lr) > 0.9999f;

            if (posUnchanged && (sleeping || isStationary)) continue;

            var pos = piece.rb.position;
            var rot = piece.rb.rotation;
            _lastSentPos[id] = pos;
            _lastSentRot[id] = rot;

            byte flags = sleeping ? (byte)1 : (byte)0;
            writer.WriteValueSafe(id);
            writer.WriteValueSafe(pos.x); writer.WriteValueSafe(pos.y); writer.WriteValueSafe(pos.z);
            writer.WriteValueSafe(rot.x); writer.WriteValueSafe(rot.y); writer.WriteValueSafe(rot.z); writer.WriteValueSafe(rot.w);
            writer.WriteValueSafe(flags);

            if (!sleeping)
            {
                var vel    = piece.rb.velocity;
                var angVel = piece.rb.angularVelocity;
                writer.WriteValueSafe(vel.x);    writer.WriteValueSafe(vel.y);    writer.WriteValueSafe(vel.z);
                writer.WriteValueSafe(angVel.x); writer.WriteValueSafe(angVel.y); writer.WriteValueSafe(angVel.z);
            }
        }

        if (writer.Position == 0) return;
        NetworkManager.CustomMessagingManager.SendNamedMessageToAll(MSG_DELTA, writer, NetworkDelivery.UnreliableSequenced);
    }

    // ── Client: make pieces kinematic ─────────────────────────────────────────

    private IEnumerator MakeClientPiecesKinematic()
    {
        while (IsClient)
        {
            yield return new WaitForSeconds(2f);
            _clientPieces = FindObjectsOfType<GamePiece>();
            foreach (var p in _clientPieces)
            {
                if (p.rb != null) p.rb.isKinematic = true;
            }
        }
    }

    // ── Client: receive registration ──────────────────────────────────────────

    private void OnRegistrationReceived(ulong senderId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort count);

        _clientPieces = FindObjectsOfType<GamePiece>();

        // Track which local pieces have already been claimed this registration pass
        var claimed = new HashSet<GamePiece>();

        // Collect server entries first so we can do a single-pass proximity match
        var entries = new (ushort id, PieceNames type, Vector3 pos)[count];
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out byte typeB);
            reader.ReadValueSafe(out float px);
            reader.ReadValueSafe(out float py);
            reader.ReadValueSafe(out float pz);
            entries[i] = (id, (PieceNames)typeB, new Vector3(px, py, pz));
        }

        // Match each server piece to the nearest unclaimed local piece of the same type
        foreach (var (id, pieceType, regPos) in entries)
        {
            if (_clientMap.ContainsKey(id)) continue; // already mapped from a previous registration

            GamePiece best     = null;
            float     bestSqDist = 4f; // 2 m radius

            foreach (var p in _clientPieces)
            {
                if (p.pieceType != pieceType) continue;
                if (claimed.Contains(p)) continue;

                float d = (p.transform.position - regPos).sqrMagnitude;
                if (d < bestSqDist) { bestSqDist = d; best = p; }
            }

            if (best != null)
            {
                _clientMap[id] = best;
                claimed.Add(best);
            }
        }

        Debug.Log($"[Net] Piece registration complete: {_clientMap.Count}/{count} pieces mapped.");
    }

    // ── Client: receive and apply delta ──────────────────────────────────────

    private void OnDeltaReceived(ulong senderId, FastBufferReader reader)
    {
        // Entries are variable-length — read until the buffer is exhausted.
        while (reader.Position < reader.Length)
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out float px); reader.ReadValueSafe(out float py); reader.ReadValueSafe(out float pz);
            reader.ReadValueSafe(out float rx); reader.ReadValueSafe(out float ry); reader.ReadValueSafe(out float rz); reader.ReadValueSafe(out float rw);
            reader.ReadValueSafe(out byte flags);
            bool sleeping = (flags & 1) != 0;

            var vel    = Vector3.zero;
            var angVel = Vector3.zero;
            if (!sleeping)
            {
                reader.ReadValueSafe(out float vx); reader.ReadValueSafe(out float vy); reader.ReadValueSafe(out float vz);
                reader.ReadValueSafe(out float ax); reader.ReadValueSafe(out float ay); reader.ReadValueSafe(out float az);
                vel    = new Vector3(vx, vy, vz);
                angVel = new Vector3(ax, ay, az);
            }

            if (!_clientMap.TryGetValue(id, out var piece) || piece == null || piece.rb == null)
                continue;

            piece.rb.position        = new Vector3(px, py, pz);
            piece.rb.rotation        = new Quaternion(rx, ry, rz, rw);
            piece.rb.velocity        = vel;
            piece.rb.angularVelocity = angVel;
        }
    }

    // ── Client: receive piece deletions (scored/removed on server) ────────────

    private void OnDeleteReceived(ulong senderId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ushort id);
            if (_clientMap.TryGetValue(id, out var piece))
            {
                _clientMap.Remove(id);
                if (piece != null) Destroy(piece.gameObject);
            }
        }
    }
}
