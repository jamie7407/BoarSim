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
    // Reliable attach/detach: tells clients to parent a ball to a robot node so it
    // tracks the hopper at 60 fps instead of drifting at 20 Hz position snapshots.
    private const string MSG_ATTACH = "BoarSim.PieceAttach";
    private const string MSG_DETACH = "BoarSim.PieceDetach";

    // Server-side state
    private readonly Dictionary<GamePiece, ushort> _serverIds = new();
    private ushort _nextId;
    private GamePiece[] _serverPieces = System.Array.Empty<GamePiece>();
    private readonly Dictionary<ushort, Vector3>    _lastSentPos = new();
    private readonly Dictionary<ushort, Quaternion> _lastSentRot = new();
    private readonly HashSet<ushort> _pendingDeleteIds = new();
    // Tracks last-known piece state per ID to detect hold/release transitions.
    private readonly Dictionary<ushort, GamePieceState> _lastPieceState = new();
    private int _fixedTick;

    // Client-side state
    private readonly Dictionary<ushort, GamePiece> _clientMap = new();
    private GamePiece[] _clientPieces = System.Array.Empty<GamePiece>();
    // IDs of balls currently parented to a robot node on client — skip delta sync for these.
    private readonly HashSet<ushort> _clientAttached = new();
    // MSG_ATTACH messages buffered when robots weren't loaded yet; retried each Update.
    private readonly List<(ushort id, byte slot, byte nodeIdx, Vector3 lp, PieceNames pieceType)> _pendingAttaches = new();
    private LoadMatch _loadMatch;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        _loadMatch = FindFirstObjectByType<LoadMatch>();

        if (IsServer)
        {
            StartCoroutine(RegistrationLoop());
        }

        if (IsClient && !IsServer)
        {
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_REG,    OnRegistrationReceived);
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DELTA,  OnDeltaReceived);
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DELETE, OnDeleteReceived);
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_ATTACH, OnPieceAttachReceived);
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DETACH, OnPieceDetachReceived);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsClient && !IsServer)
        {
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_REG);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DELTA);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DELETE);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_ATTACH);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DETACH);
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
                // Re-send attach messages for all currently-held balls so that a client
                // joining mid-match (or reconnecting after registration) gets the correct
                // parenting state. MSG_ATTACH is reliable so duplicates are harmless.
                SendAttachForStationary();
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
        if (IsServer)
        {
            if (++_fixedTick < syncEveryNFixed) return;
            _fixedTick = 0;
            SendDelta();
            return;
        }

        if (!IsClient) return;
        // Keep rb.position in sync with transform.position for kinematic attached balls.
        // When the robot drives, the child transform moves but the physics engine's rb.position
        // would otherwise lag — stale physics position breaks collisions and the detach-to-dynamic
        // transition starts from a wrong physics origin.
        foreach (var id in _clientAttached)
        {
            if (!_clientMap.TryGetValue(id, out var ap) || ap == null || ap.rb == null) continue;
            ap.rb.position = ap.transform.position;
            ap.rb.rotation = ap.transform.rotation;
        }
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

            // Detect hold/release transitions and notify clients so they can parent or
            // un-parent the ball, giving smooth 60 fps tracking instead of 20 Hz snaps.
            // If the piece isn't in _lastPieceState yet (first delta tick after connection
            // or after a new match) treat it as "was not stationary" so preloaded balls that
            // are already held get their MSG_ATTACH on the first tick.
            bool wasStationary = _lastPieceState.TryGetValue(id, out var prevState) &&
                                 prevState == GamePieceState.Stationary;
            if (isStationary && !wasStationary) SendPieceAttach(id, piece);
            else if (!isStationary && wasStationary) SendPieceDetach(id, piece);
            _lastPieceState[id] = piece.state;

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

            // Stationary (held) pieces are always re-sent even when position is unchanged:
            // MSG_DELTA is unreliable, so the single "ball entered hopper" packet may be
            // dropped, and the client would never get corrected if we skip on posUnchanged.
            // Sleeping world pieces (not held) are still skipped — they don't move.
            if (posUnchanged && sleeping) continue;

            var pos = piece.rb.position;
            var rot = piece.rb.rotation;
            _lastSentPos[id] = pos;
            _lastSentRot[id] = rot;

            // bit 0 = sleeping, bit 1 = stationary (held by robot on server).
            // The stationary bit lets the client make the ball kinematic immediately
            // from the delta, before MSG_ATTACH arrives — preventing gravity from
            // pulling a dynamic ball through the robot's kinematic colliders.
            byte flags = 0;
            if (sleeping)     flags |= 1;
            if (isStationary) flags |= 2;
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
                // Make kinematic immediately so delta positions aren't fought by local physics.
                if (best.rb != null)
                {
                    best.rb.isKinematic = true;
                    best.rb.interpolation = RigidbodyInterpolation.None;
                }
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
            bool sleeping   = (flags & 1) != 0;
            bool stationary = (flags & 2) != 0;

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

            // Ball is parented to a robot node — its transform tracks the joint already.
            // Applying the 20 Hz delta would fight against the smooth parent-child update.
            if (_clientAttached.Contains(id)) continue;

            // Server is holding this ball. Make it kinematic immediately so gravity cannot
            // pull it through the robot's kinematic colliders while MSG_ATTACH is in flight.
            // Without this, a ball snapped to the robot's position via rb.position is still
            // subject to physics depenetration — it gets pushed downward by robot geometry
            // and falls through the hopper before MSG_ATTACH arrives to parent it properly.
            if (stationary && piece.rb != null && !piece.rb.isKinematic)
            {
                piece.rb.isKinematic   = true;
                piece.rb.interpolation = RigidbodyInterpolation.None;
            }

            var pos = new Vector3(px, py, pz);
            var rot = new Quaternion(rx, ry, rz, rw);

            if (piece.rb.isKinematic)
            {
                // Kinematic ball: update both transform and physics position.
                // transform.SetPositionAndRotation is needed for immediate visual correctness
                // because kinematic rb.position only takes effect at the next physics step.
                piece.transform.SetPositionAndRotation(pos, rot);
                piece.rb.position = pos;
                piece.rb.rotation = rot;
            }
            else
            {
                // Dynamic shot ball: snap physics to the authoritative server position.
                // Do NOT touch transform directly — for dynamic RBs, the physics engine
                // owns the transform and direct assignment breaks physics integration.
                piece.rb.position = pos;
                piece.rb.rotation = rot;
            }

            piece.rb.velocity        = vel;      // no-op for kinematic; applied for dynamic
            piece.rb.angularVelocity = angVel;
        }
    }

    // ── Server: send ball attach/detach to clients ────────────────────────────

    private void SendAttachForStationary()
    {
        foreach (var piece in _serverPieces)
        {
            if (piece == null || piece.state != GamePieceState.Stationary) continue;
            if (!_serverIds.TryGetValue(piece, out ushort id)) continue;
            SendPieceAttach(id, piece);
        }
    }

    private void SendPieceAttach(ushort id, GamePiece piece)
    {
        if (piece.owner == null) return;
        if (_loadMatch == null) _loadMatch = FindFirstObjectByType<LoadMatch>();
        if (_loadMatch == null) return;

        // Walk up to find the owning BuildNode and which robot slot it belongs to.
        var ownNode = piece.owner.GetComponent<BuildNode>()
                   ?? piece.owner.GetComponentInParent<BuildNode>();
        if (ownNode == null) return;

        var swerve = ownNode.GetComponentInParent<SwerveController>();
        if (swerve == null) return;

        int slot = -1;
        for (int s = 0; s < 4; s++)
            if (_loadMatch.GetRobotLoaded(s) == swerve.gameObject) { slot = s; break; }
        if (slot < 0) return;

        var nodes = swerve.GetComponentsInChildren<BuildNode>();
        int nodeIdx = System.Array.IndexOf(nodes, ownNode);
        if (nodeIdx < 0 || nodeIdx > 255) return;

        // Local position of ball in node space (usually near zero after teleportTo).
        var lp = ownNode.transform.InverseTransformPoint(piece.transform.position);

        // pieceType is included so the client can do a proximity fallback search when
        // the ball isn't in _clientMap (e.g. intaked before the T=2s registration window).
        using var writer = new FastBufferWriter(2 + 1 + 1 + 1 + 12, Allocator.Temp);
        writer.WriteValueSafe(id);
        writer.WriteValueSafe((byte)slot);
        writer.WriteValueSafe((byte)nodeIdx);
        writer.WriteValueSafe((byte)piece.pieceType);
        writer.WriteValueSafe(lp.x); writer.WriteValueSafe(lp.y); writer.WriteValueSafe(lp.z);
        NetworkManager.CustomMessagingManager.SendNamedMessageToAll(
            MSG_ATTACH, writer, NetworkDelivery.Reliable);
    }

    private void SendPieceDetach(ushort id, GamePiece piece)
    {
        // Include the launch velocity so clients can immediately make the ball dynamic
        // with the correct velocity — without this the client would have to wait for the
        // first MSG_DELTA (up to 60 ms later) and the ball would appear to hover.
        var vel = piece.rb != null ? piece.rb.velocity : Vector3.zero;
        using var writer = new FastBufferWriter(2 + 12, Allocator.Temp);
        writer.WriteValueSafe(id);
        writer.WriteValueSafe(vel.x); writer.WriteValueSafe(vel.y); writer.WriteValueSafe(vel.z);
        NetworkManager.CustomMessagingManager.SendNamedMessageToAll(
            MSG_DETACH, writer, NetworkDelivery.Reliable);
    }

    // ── Client: per-frame maintenance + pending attach retry ─────────────────

    private void Update()
    {
        if (!IsClient || IsServer) return;

        // Mirror host BuildNode.Update() stowing behaviour: the host calls
        // teleportTo(currentGamePiece, transform) every frame while holding a ball,
        // which (a) disables colliders, (b) forces the ball to the node's world
        // position (localPosition ≈ zero), and (c) re-asserts owner/state every frame.
        // Without this continuous enforcement, anything that touches the ball's
        // transform between delta ticks (physics glitch, late joint-sync tick, etc.)
        // can visibly drift the ball away from the intake.
        foreach (var id in _clientAttached)
        {
            if (!_clientMap.TryGetValue(id, out var ap) || ap == null) continue;

            // Re-assert kinematic every frame — if anything reset isKinematic between
            // frames (e.g. a poorly-ordered script or physics callback), gravity would
            // immediately start pulling the held ball downward through robot geometry.
            if (ap.rb != null && !ap.rb.isKinematic)
            {
                ap.rb.isKinematic   = true;
                ap.rb.interpolation = RigidbodyInterpolation.None;
            }

            // Keep colliders disabled while held — host disables them every frame.
            if (ap.colliderParent != null && ap.colliderParent.activeSelf)
                ap.colliderParent.SetActive(false);

            // Keep ball anchored to the node's local origin — host forces this via
            // teleportTo every frame. Kinematic ball; no physics will fight us.
            ap.transform.localPosition = Vector3.zero;
            ap.transform.localRotation = Quaternion.identity;
        }

        if (_pendingAttaches.Count == 0) return;
        for (int i = _pendingAttaches.Count - 1; i >= 0; i--)
        {
            var (id, slot, nodeIdx, lp, pieceType) = _pendingAttaches[i];
            if (ApplyPieceAttach(id, slot, nodeIdx, lp, pieceType))
                _pendingAttaches.RemoveAt(i);
        }
    }

    // ── Client: ball attachment / detachment ──────────────────────────────────

    private void OnPieceAttachReceived(ulong senderId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort id);
        reader.ReadValueSafe(out byte slot);
        reader.ReadValueSafe(out byte nodeIdx);
        reader.ReadValueSafe(out byte pieceTypeByte);
        reader.ReadValueSafe(out float lx); reader.ReadValueSafe(out float ly); reader.ReadValueSafe(out float lz);
        var lp = new Vector3(lx, ly, lz);
        var pieceType = (PieceNames)pieceTypeByte;

        bool inMap = _clientMap.ContainsKey(id);
        if (!ApplyPieceAttach(id, slot, nodeIdx, lp, pieceType))
        {
            _pendingAttaches.Add((id, slot, nodeIdx, lp, pieceType));
            Debug.Log($"[Net][Client] MSG_ATTACH id={id} slot={slot} node={nodeIdx} type={pieceType} → PENDING (robot not loaded yet). inMap={inMap}");
        }
        else if (!_clientAttached.Contains(id))
        {
            Debug.Log($"[Net][Client] MSG_ATTACH id={id} slot={slot} node={nodeIdx} type={pieceType} → silently skipped (no local ball). inMap={inMap}");
        }
    }

    // Returns true when the attach was definitively handled (success or skippable failure).
    // Returns false when robots aren't loaded yet — caller should buffer and retry.
    private bool ApplyPieceAttach(ushort id, byte slot, byte nodeIdx, Vector3 localPos, PieceNames pieceType)
    {
        if (_loadMatch == null) _loadMatch = FindFirstObjectByType<LoadMatch>();
        if (_loadMatch == null) return false;

        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return false; // robots not loaded yet — buffer and retry

        var nodes = robot.GetComponentsInChildren<BuildNode>();
        if (nodeIdx >= nodes.Length)
        {
            Debug.LogWarning($"[Net][Client] MSG_ATTACH id={id}: nodeIdx={nodeIdx} out of range (robot has {nodes.Length} BuildNodes)");
            return true;
        }

        var node = nodes[nodeIdx];

        // Find the ball in _clientMap. Three fallback tiers:
        //  1. Normal: ball was registered by proximity at T=2s → found by id.
        //  2. Preloaded fallback: ball was spawned inside this node but didn't get
        //     proximity-matched (robot moved > 2m before T=2s window) → look for any
        //     unregistered GamePiece already parented inside the target node.
        //  3. Proximity fallback: field ball intaked before T=2s so server registration
        //     position was inside the robot while client ball was still on the field →
        //     search all client pieces for the closest unclaimed piece of the right type.
        if (!_clientMap.TryGetValue(id, out var piece) || piece == null)
        {
            GamePiece candidate = null;

            // Tier 2: preloaded ball already in node hierarchy
            var inNode = node.GetComponentInChildren<GamePiece>();
            if (inNode != null && !_clientMap.ContainsValue(inNode))
                candidate = inNode;

            // Tier 3: search all client pieces by type for closest unclaimed match
            if (candidate == null)
            {
                // Refresh piece list if stale (normally populated by OnRegistrationReceived)
                if (_clientPieces == null || _clientPieces.Length == 0)
                    _clientPieces = FindObjectsOfType<GamePiece>();

                var nodePos = node.transform.position;
                float bestSqDist = float.MaxValue;
                foreach (var cp in _clientPieces)
                {
                    if (cp == null || cp.pieceType != pieceType) continue;
                    if (_clientMap.ContainsValue(cp)) continue;
                    float d = (cp.transform.position - nodePos).sqrMagnitude;
                    if (d < bestSqDist) { bestSqDist = d; candidate = cp; }
                }
                if (candidate != null)
                    Debug.Log($"[Net][Client] MSG_ATTACH id={id}: proximity fallback found {candidate.name} at dist={Mathf.Sqrt(bestSqDist):F1}m");
            }

            if (candidate == null)
                return true; // no local ball to attach — nothing to do

            _clientMap[id] = candidate;
            piece = candidate;
            if (piece.rb != null && !piece.rb.isKinematic)
            {
                piece.rb.isKinematic   = true;
                piece.rb.interpolation = RigidbodyInterpolation.None;
            }
        }

        // Mirror host: disable colliders so the ball doesn't clip through joints.
        if (piece.colliderParent != null) piece.colliderParent.SetActive(false);

        // Ensure the ball is kinematic before parenting — it may have been made dynamic
        // by a previous OnPieceDetachReceived (shot then re-intaked).
        if (piece.rb != null && !piece.rb.isKinematic)
        {
            piece.rb.isKinematic   = true;
            piece.rb.interpolation = RigidbodyInterpolation.None;
        }

        // Parent to the node — ball now tracks the hopper at 60 fps via the
        // already-synced joint hierarchy instead of 20 Hz position snapshots.
        piece.transform.SetParent(node.transform, false);
        piece.transform.localPosition = localPos;
        piece.transform.localRotation = Quaternion.identity;

        if (piece.rb != null && piece.rb.isKinematic)
        {
            piece.rb.position = piece.transform.position;
            piece.rb.rotation = piece.transform.rotation;
        }

        // Mirror host changeParent: set owner and state so any script that reads
        // these fields (scoring triggers, aiming logic, etc.) sees the correct values.
        piece.owner = node.transform;
        piece.state = GamePieceState.Stationary;

        // Mirror host BuildNode: keep currentGamePiece up-to-date so other scripts
        // that query which ball the robot is holding (e.g. shooter, scoring zone) work
        // correctly on the client without needing the host-only intake logic to run.
        node.currentGamePiece = piece;
        node.currentState     = NodeState.Stowing;

        _clientAttached.Add(id);
        Debug.Log($"[Net][Client] Ball {id} attached to slot node[{nodeIdx}]. map={_clientMap.Count} attached={_clientAttached.Count}");
        return true;
    }

    private void OnPieceDetachReceived(ulong senderId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort id);
        reader.ReadValueSafe(out float vx); reader.ReadValueSafe(out float vy); reader.ReadValueSafe(out float vz);
        var shotVelocity = new Vector3(vx, vy, vz);

        // Cancel any buffered attach — the ball was released before robots finished loading.
        for (int i = _pendingAttaches.Count - 1; i >= 0; i--)
            if (_pendingAttaches[i].id == id) _pendingAttaches.RemoveAt(i);

        if (!_clientMap.TryGetValue(id, out var piece) || piece == null) return;
        if (!_clientAttached.Contains(id)) return;

        _clientAttached.Remove(id);

        // Mirror host ReleaseToWorld: clear owner and state before unparenting.
        var prevNode = piece.transform.parent?.GetComponent<BuildNode>();
        if (prevNode != null && prevNode.currentGamePiece == piece)
        {
            prevNode.currentGamePiece = null;
            prevNode.currentState     = NodeState.Stowing;
        }
        piece.owner = null;
        piece.state = GamePieceState.World;

        // Re-enable colliders and restore to field parent so delta sync can drive it again.
        if (piece.colliderParent != null) piece.colliderParent.SetActive(true);

        var parent = piece.originalParent != null ? piece.originalParent : null;
        piece.transform.SetParent(parent, true);

        // Make the ball dynamic so it extrapolates between 20 Hz delta ticks.
        // A kinematic rb ignores rb.velocity — the shot ball would be effectively invisible
        // (strobing once per 50 ms tick) without this. Interpolation smooths the visual
        // between physics frames after the velocity is applied.
        if (piece.rb != null)
        {
            piece.rb.isKinematic   = false;
            piece.rb.interpolation = RigidbodyInterpolation.Interpolate;
            piece.rb.velocity      = shotVelocity;
        }
        Debug.Log($"[Net][Client] Ball {id} detached. shotVel={shotVelocity} dynamic={piece.rb != null && !piece.rb.isKinematic}");
    }

    // ── Client: receive piece deletions (scored/removed on server) ────────────

    private void OnDeleteReceived(ulong senderId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ushort id);
            _clientAttached.Remove(id);
            if (_clientMap.TryGetValue(id, out var piece))
            {
                _clientMap.Remove(id);
                if (piece != null) Destroy(piece.gameObject);
            }
        }
    }
}
