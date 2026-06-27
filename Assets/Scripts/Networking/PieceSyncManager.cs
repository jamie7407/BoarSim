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
//   MSG_REG    (Reliable, every 5s)   — deterministic ID→ball mapping, no proximity match.
//   MSG_DELTA  (Unreliable-Seq, ~20Hz)— per changed piece: id, pos, rot, flags, vel.
//   MSG_ATTACH (Reliable)             — fallback for balls intaked before T=2s registration.
//   MSG_DETACH (Reliable)             — ball released/shot with velocity.
//
// ID assignment (deterministic): host sorts ALL GamePieces by their Unity hierarchy path
// (chain of sibling indices from root). Both machines run the same scene so the sort
// order is identical — the server's sorted_pieces[i] is the same object as the client's
// sorted_pieces[i], allowing index-based matching with NO proximity math. Held balls,
// preloaded balls, and field balls are all registered correctly regardless of position.
//
// Primary hold mechanism: when MSG_DELTA arrives with stationary=true for a registered
// ball, the client immediately parents it to the nearest BuildNode (auto-attach), which
// fires faster than the reliable MSG_ATTACH. MSG_ATTACH is a redundant fallback.

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
    // Rotating start index for delta sends: we process at most kMaxPacketsPerDelta UDP packets
    // per tick, then resume from this ball next tick. All 472 balls are covered in ~6 ticks.
    private int _deltaStartIndex;

    // Client-side state
    private readonly Dictionary<ushort, GamePiece> _clientMap = new();
    private GamePiece[] _clientPieces = System.Array.Empty<GamePiece>();
    // IDs of balls currently parented to a robot node on client — skip delta sync for these.
    private readonly HashSet<ushort> _clientAttached = new();
    // MSG_ATTACH messages buffered when robots weren't loaded yet; retried each Update.
    private readonly List<(ushort id, byte slot, byte nodeIdx, Vector3 lp, PieceNames pieceType)> _pendingAttaches = new();
    private LoadMatch _loadMatch;
    // Cached BuildNode list across all loaded robots; refreshed every 0.5 s.
    private BuildNode[] _cachedNodes;
    private float       _nodeCacheTime;
    // Cached robot colliders for manual ball-push; refreshed every 1 s.
    // Kinematic robots don't generate reliable CCD contacts when teleported, so we
    // manually depenetrate dynamic field balls in FixedUpdate instead.
    private Collider[] _cachedRobotColliders = System.Array.Empty<Collider>();
    private float      _robotColCacheTime = -99f;
    // Dead-reckoning state: last authoritative position + velocity received from server.
    // Used in client FixedUpdate to extrapolate ball positions between 20 Hz server updates.
    private readonly Dictionary<ushort, Vector3> _recvPos  = new();
    private readonly Dictionary<ushort, Vector3> _recvVel  = new();
    private readonly Dictionary<ushort, float>   _recvTime = new();

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
            Debug.Log("[Net][Client] PieceSyncManager spawned — message handlers registered");
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsClient && !IsServer && NetworkManager.CustomMessagingManager != null)
        {
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_REG);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DELTA);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DELETE);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_ATTACH);
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DETACH);
        }
    }

    // ── Deterministic piece ordering ──────────────────────────────────────────

    // Builds a sortable key from the chain of sibling indices up the transform hierarchy.
    // Two transforms on any two machines running the same scene will produce identical keys
    // for the same object, because Unity's scene load preserves sibling order.
    // Zero-padded to 5 digits so lexicographic sort == numeric sort at each level.
    private static string HierarchyKey(Transform t)
    {
        var parts = new List<string>();
        while (t != null) { parts.Insert(0, t.GetSiblingIndex().ToString("D5")); t = t.parent; }
        return string.Join("/", parts);
    }

    // ── Server: registration ──────────────────────────────────────────────────

    private IEnumerator RegistrationLoop()
    {
        // Wait for LoadMatch to finish spawning field + initial pieces
        yield return new WaitForSeconds(2f);

        while (IsServer)
        {
            RefreshServerPieces();
            int clientCount = NetworkManager.ConnectedClientsList.Count;
            Debug.Log($"[Net][Host] RegistrationLoop tick: {_serverPieces.Length} pieces, {clientCount} clients");
            if (clientCount > 1)
            {
                Debug.Log($"[Net][Host] Sending registration to {clientCount - 1} client(s)");
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

        // Sort by deterministic hierarchy key before assigning IDs so that the client,
        // which sorts its own pieces by the same key, gets the same ordering and can
        // match server_entries[i] → client_sorted[i] without any proximity math.
        System.Array.Sort(_serverPieces, (a, b) =>
        {
            if (a == null && b == null) return  0;
            if (a == null)             return  1;
            if (b == null)             return -1;
            return string.Compare(HierarchyKey(a.transform), HierarchyKey(b.transform),
                                  System.StringComparison.Ordinal);
        });

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
        // Deterministic registration: _serverPieces is already sorted by HierarchyKey.
        // Send (id, type) for every piece in that sorted order — no positions needed.
        // Chunked into ≤350 entries per MSG_REG so each packet stays under NGO's ~1400-byte MTU.
        // Each chunk carries its startOffset so the client maps server_sorted[startOffset+i]
        // → client_sorted[startOffset+i] correctly.
        var toSend = new List<(ushort id, byte type)>();
        foreach (var piece in _serverPieces)
        {
            if (piece == null) continue;
            if (!_serverIds.TryGetValue(piece, out ushort id)) continue;
            toSend.Add((id, (byte)piece.pieceType));
        }

        // startOffset(2) + chunkCount(2) + entries(3 each): 4 + 350×3 = 1054 bytes max per chunk.
        const int kChunk = 350;
        for (int start = 0; start < toSend.Count; start += kChunk)
        {
            int count   = Mathf.Min(kChunk, toSend.Count - start);
            int bufSize = 4 + count * 3;
            using var writer = new FastBufferWriter(bufSize, Allocator.Temp);
            writer.WriteValueSafe((ushort)start);  // startOffset into the global sorted list
            writer.WriteValueSafe((ushort)count);  // entries in this chunk
            for (int i = start; i < start + count; i++)
            {
                writer.WriteValueSafe(toSend[i].id);
                writer.WriteValueSafe(toSend[i].type);
            }
            NetworkManager.CustomMessagingManager.SendNamedMessageToAll(MSG_REG, writer, NetworkDelivery.Reliable);
        }
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

        // Refresh robot collider cache once per second so newly loaded robots are picked up.
        if (Time.time - _robotColCacheTime > 1f)
        {
            RefreshRobotColliderCache();
            _robotColCacheTime = Time.time;
        }

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

        // Dead-reckoning: extrapolate each moving ball forward using its last known velocity.
        // Called every FixedUpdate (50 Hz) so balls move continuously between 20 Hz server
        // updates. Without this, kinematic MovePosition only moves the ball once then holds it
        // still for ~50ms until the next delta arrives — visually choppy at any ball speed.
        // Capped at 0.15 s to avoid large errors when packets are late or lost.
        foreach (var kvp in _recvPos)
        {
            var id = kvp.Key;
            if (!_recvTime.TryGetValue(id, out float t0)) continue;
            float dt = Time.fixedTime - t0;
            if (dt <= 0f || dt > 0.15f) continue;

            if (!_clientMap.TryGetValue(id, out var piece) || piece == null || piece.rb == null) continue;
            if (_clientAttached.Contains(id)) continue;

            var vel = _recvVel.TryGetValue(id, out var v) ? v : Vector3.zero;
            var projected = kvp.Value + vel * dt + 0.5f * Physics.gravity * dt * dt;
            piece.rb.MovePosition(projected);
        }

        // Ball-robot collision is now handled by Rigidbody.MovePosition in GameNetworkManager:
        // kinematic joints sweep to their target each FixedUpdate, generating contacts with
        // dynamic balls natively. ClientManualBallPush is no longer called.
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

        // Each packet's payload is capped at kMaxPayload bytes (NGO MTU limit).
        // To prevent the Unity Transport receive queue (128 packets) from overflowing at
        // match startup (472 active balls = 24 packets/tick × 50 Hz = 1200/s), we cap the
        // total packets sent per tick at kMaxPacketsPerDelta and rotate _deltaStartIndex so
        // every ball is eventually covered. At 4 packets/tick × 50 Hz = 200 packets/sec max,
        // all 472 balls are covered in ~6 ticks (120 ms) during the initial settling period.
        // After settling, most balls sleep and the actual packet count drops to 0-2/tick.
        const int kMaxPayload       = 1100;
        const int kMaxPacketsPerDelta = 4;

        int  n            = _serverPieces.Length;
        int  packetsSent  = 0;
        bool limitReached = false;

        var writer = new FastBufferWriter(kMaxPayload, Allocator.Temp);

        for (int j = 0; j < n; j++)
        {
            int i = (_deltaStartIndex + j) % n;

            var piece = _serverPieces[i];
            if (piece == null || piece.rb == null) continue;
            if (!_serverIds.TryGetValue(piece, out ushort id)) continue;

            bool isStationary = piece.state == GamePieceState.Stationary;

            // Detect hold/release transitions and notify clients so they can parent or
            // un-parent the ball. State detection runs for every ball regardless of the
            // packet limit — MSG_ATTACH/DETACH are reliable and not subject to the cap.
            bool wasStationary = _lastPieceState.TryGetValue(id, out var prevState) &&
                                 prevState == GamePieceState.Stationary;
            if (isStationary && !wasStationary)
            {
                Debug.Log($"[Net][Host] Ball {id} World→Stationary, owner={piece.owner?.name ?? "null"}");
                SendPieceAttach(id, piece);
            }
            else if (!isStationary && wasStationary) SendPieceDetach(id, piece);
            _lastPieceState[id] = piece.state;

            // Stationary balls are parented to robot nodes via MSG_ATTACH on the client.
            // Parent-child transform already tracks the robot at 60 fps — no delta needed.
            if (isStationary) continue;

            bool sleeping = !isStationary && piece.rb.IsSleeping();

            bool posUnchanged =
                _lastSentPos.TryGetValue(id, out var lp) &&
                _lastSentRot.TryGetValue(id, out var lr) &&
                (piece.rb.position - lp).sqrMagnitude < 0.0001f &&
                Quaternion.Dot(piece.rb.rotation, lr) > 0.9999f;

            if (posUnchanged && sleeping) continue;

            var pos = piece.rb.position;
            var rot = piece.rb.rotation;

            byte flags = 0;
            if (sleeping)     flags |= 1;
            if (isStationary) flags |= 2;

            int entrySize = sleeping ? 31 : 55;

            // Flush and enforce the per-tick packet cap.
            if (writer.Position > 0 && writer.Position + entrySize > kMaxPayload)
            {
                NetworkManager.CustomMessagingManager.SendNamedMessageToAll(MSG_DELTA, writer, NetworkDelivery.UnreliableSequenced);
                writer.Dispose();
                writer = new FastBufferWriter(kMaxPayload, Allocator.Temp);
                packetsSent++;

                if (packetsSent >= kMaxPacketsPerDelta)
                {
                    // Resume from this ball next tick; skip writing it to the new writer.
                    _deltaStartIndex = i;
                    limitReached = true;
                    break;
                }
            }

            // Record and write the ball only if we're still within the packet budget.
            _lastSentPos[id] = pos;
            _lastSentRot[id] = rot;

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

        if (!limitReached) _deltaStartIndex = 0; // completed a full pass; restart next tick

        if (writer.Position > 0)
            NetworkManager.CustomMessagingManager.SendNamedMessageToAll(MSG_DELTA, writer, NetworkDelivery.UnreliableSequenced);
        writer.Dispose();
    }

    // ── Client: receive registration ──────────────────────────────────────────

    private void OnRegistrationReceived(ulong senderId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ushort startOffset);
        reader.ReadValueSafe(out ushort count);
        var entries = new (ushort id, PieceNames type)[count];
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out byte typeB);
            entries[i] = (id, (PieceNames)typeB);
        }

        // Sort client pieces on the first chunk of each registration cycle (startOffset == 0).
        // Server sorted its pieces, assigned IDs in that order, and sent them in order.
        // client_sorted[startOffset + i] is the same physical ball as server_sorted[startOffset + i].
        // Reliable delivery guarantees chunk 0 arrives before chunk 1, so the sort
        // happens exactly once per registration before any entries are mapped.
        if (startOffset == 0)
        {
            _clientPieces = FindObjectsOfType<GamePiece>();
            System.Array.Sort(_clientPieces, (a, b) =>
            {
                if (a == null && b == null) return  0;
                if (a == null)             return  1;
                if (b == null)             return -1;
                return string.Compare(HierarchyKey(a.transform), HierarchyKey(b.transform),
                                      System.StringComparison.Ordinal);
            });
        }

        if (_clientPieces == null) return; // chunk 0 hasn't arrived yet (shouldn't happen with Reliable)

        int mapped = 0;
        for (int i = 0; i < count; i++)
        {
            var (id, type) = entries[i];
            int clientIdx  = startOffset + i;
            if (clientIdx >= _clientPieces.Length) break;
            var piece = _clientPieces[clientIdx];
            if (piece == null) continue;
            if (_clientMap.ContainsKey(id)) continue; // re-registration; already mapped

            if (piece.pieceType != type)
            {
                Debug.LogWarning($"[Net][Client] Registration index {clientIdx}: server type={type} " +
                                 $"client type={piece.pieceType}. Scene hierarchy may differ between machines.");
                continue;
            }

            _clientMap[id] = piece;
            if (piece.rb != null)
            {
                // All client balls are kinematic — the host runs all physics and sends authoritative
                // positions via delta. Making dynamic balls kinematic here prevents local gravity and
                // contact forces from fighting against the 50 Hz delta snap corrections, which caused
                // visible jitter. Kinematic-vs-kinematic (robot and balls) generates no contacts,
                // so the client robot can drive through balls — the host handles actual collision
                // response and the results are reflected in subsequent delta packets.
                piece.rb.isKinematic   = true;
                piece.rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
            mapped++;
        }

        Debug.Log($"[Net][Client] Registration chunk offset={startOffset} count={count}: {mapped} new, {_clientMap.Count} total, client pieces={_clientPieces.Length}");
    }

    // ── Client: nearest-node lookup ───────────────────────────────────────────

    // Returns the BuildNode (across ALL scene robots) whose world position is closest
    // to worldPos and within maxDist metres, or null if none is found.
    // Uses FindObjectsOfType so it always sees every node regardless of slot assignment.
    // The result is cached for 0.5 s to avoid the per-frame cost.
    private BuildNode FindNearestBuildNode(Vector3 worldPos, float maxDist)
    {
        if (_cachedNodes == null || Time.time - _nodeCacheTime > 0.5f)
        {
            _cachedNodes   = FindObjectsOfType<BuildNode>();
            _nodeCacheTime = Time.time;
        }

        BuildNode nearest    = null;
        float     bestSqDist = maxDist * maxDist;
        foreach (var n in _cachedNodes)
        {
            if (n == null) continue;
            float sq = (n.transform.position - worldPos).sqrMagnitude;
            if (sq < bestSqDist) { bestSqDist = sq; nearest = n; }
        }
        return nearest;
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

            Vector3 vel = Vector3.zero;
            if (!sleeping)
            {
                reader.ReadValueSafe(out float vx); reader.ReadValueSafe(out float vy); reader.ReadValueSafe(out float vz);
                reader.ReadValueSafe(out float _ax); reader.ReadValueSafe(out float _ay); reader.ReadValueSafe(out float _az);
                vel = new Vector3(vx, vy, vz);
            }

            if (!_clientMap.TryGetValue(id, out var piece) || piece == null || piece.rb == null)
                continue;

            // Ball is parented to a robot node — its transform tracks the joint already.
            // Applying the 20 Hz delta would fight against the smooth parent-child update.
            if (_clientAttached.Contains(id)) continue;

            // Re-assert kinematic in case this delta arrives before MSG_REG.
            if (stationary && piece.rb != null && !piece.rb.isKinematic)
            {
                piece.rb.isKinematic   = true;
                piece.rb.interpolation = RigidbodyInterpolation.Interpolate;
            }

            var pos = new Vector3(px, py, pz);
            var rot = new Quaternion(rx, ry, rz, rw);

            if (sleeping)
            {
                // Sleeping ball: teleport to authoritative rest position and clear dead-reckoning.
                piece.rb.position = pos;
                piece.rb.rotation = rot;
                piece.transform.SetPositionAndRotation(pos, rot);
                _recvPos.Remove(id);
                _recvVel.Remove(id);
                _recvTime.Remove(id);
            }
            else
            {
                // Moving ball: store authoritative state for dead-reckoning in FixedUpdate.
                // FixedUpdate will extrapolate pos + vel * dt + gravity * dt² each tick so
                // the ball moves continuously between 20 Hz server updates instead of snapping.
                _recvPos[id]  = pos;
                _recvVel[id]  = vel;
                _recvTime[id] = Time.fixedTime;
                piece.rb.MovePosition(pos);
                piece.rb.MoveRotation(rot);
            }
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

        // Find robot slot by hierarchy membership — IsChildOf covers both the root
        // itself and any child, so this works regardless of which component is on root.
        int slot = -1;
        GameObject slotRobot = null;
        for (int s = 0; s < 4; s++)
        {
            var r = _loadMatch.GetRobotLoaded(s);
            if (r != null && ownNode.transform.IsChildOf(r.transform)) { slot = s; slotRobot = r; break; }
        }
        if (slot < 0 || slotRobot == null)
        {
            // Ball is held by a field node (e.g. Tunnel scoring zone), not a robot — no MSG_ATTACH needed.
            return;
        }

        // Use the robot root for node enumeration so index matches ApplyPieceAttach on client.
        var nodes = slotRobot.GetComponentsInChildren<BuildNode>();
        int nodeIdx = System.Array.IndexOf(nodes, ownNode);
        if (nodeIdx < 0 || nodeIdx > 255) return;

        // Local position of ball in node space (usually near zero after teleportTo).
        var lp = ownNode.transform.InverseTransformPoint(piece.transform.position);
        Debug.Log($"[Net][Host] SendPieceAttach id={id} slot={slot} nodeIdx={nodeIdx} type={piece.pieceType}");

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

    // ── Client: manual ball-robot collision (bypasses CCD on kinematic bots) ────

    private void RefreshRobotColliderCache()
    {
        if (_loadMatch == null) return;
        var cols = new List<Collider>();
        for (int s = 0; s < 4; s++)
        {
            var robot = _loadMatch.GetRobotLoaded(s);
            if (robot == null) continue;
            foreach (var col in robot.GetComponentsInChildren<Collider>())
            {
                if (col.isTrigger) continue;
                if (col.GetComponentInParent<GamePiece>() != null) continue;
                cols.Add(col);
            }
        }
        _cachedRobotColliders = cols.ToArray();
    }

    // Every FixedUpdate: scan dynamic field balls and push any that overlap a
    // kinematic robot collider outward. Unity's speculative CCD is unreliable for
    // teleporting kinematic bodies (zero PhysX velocity computed → no AABB expansion),
    // so we resolve ball-robot penetrations here instead of relying on the physics engine.
    private void ClientManualBallPush()
    {
        if (_cachedRobotColliders.Length == 0) return;

        foreach (var kvp in _clientMap)
        {
            var piece = kvp.Value;
            if (piece == null || piece.rb == null || piece.rb.isKinematic) continue;
            if (_clientAttached.Contains(kvp.Key)) continue;

            // Determine ball radius from its SphereCollider; fall back to a sensible default.
            float ballRadius = 0.08f;
            if (piece.colliderParent != null)
            {
                var sc = piece.colliderParent.GetComponentInChildren<SphereCollider>();
                if (sc != null)
                    ballRadius = sc.radius * Mathf.Max(sc.transform.lossyScale.x,
                                                       sc.transform.lossyScale.y,
                                                       sc.transform.lossyScale.z);
            }

            var ballPos = piece.rb.position;

            foreach (var rc in _cachedRobotColliders)
            {
                if (rc == null) continue;

                // Cheap pre-filter: bounding sphere of the robot collider + ball radius.
                var rBounds = rc.bounds;
                float rReach = rBounds.extents.magnitude + ballRadius + 0.05f;
                if ((rBounds.center - ballPos).sqrMagnitude > rReach * rReach) continue;

                // Nearest point on (or inside) the robot collider surface to the ball centre.
                Vector3 closest = rc.ClosestPoint(ballPos);
                Vector3 sep     = ballPos - closest;
                float   dist    = sep.magnitude;

                Vector3 pushDir;
                float   overlap;

                if (dist < 0.001f)
                {
                    // Ball centre is inside the collider — ClosestPoint returns ballPos.
                    // Push away from the attached rigidbody's centre of mass.
                    var arb = rc.attachedRigidbody;
                    pushDir = (ballPos - (arb != null ? arb.worldCenterOfMass : rBounds.center)).normalized;
                    if (pushDir.sqrMagnitude < 0.01f) pushDir = Vector3.up;
                    overlap = ballRadius + 0.01f;
                }
                else if (dist < ballRadius)
                {
                    pushDir = sep / dist;
                    overlap = ballRadius - dist;
                }
                else
                {
                    continue; // no overlap with this collider
                }

                if (piece.rb.IsSleeping()) piece.rb.WakeUp();

                // Snap ball out of overlap so it isn't re-penetrated next frame.
                var corrected = ballPos + pushDir * (overlap + 0.01f);
                piece.rb.position        = corrected;
                piece.transform.position = corrected;

                // Ensure the ball moves away from the robot at least at pushSpeed.
                float outward = Vector3.Dot(piece.rb.velocity, pushDir);
                if (outward < 2f)
                    piece.rb.velocity += pushDir * (2f - outward);

                break; // one robot collider resolved per ball per tick is enough
            }
        }
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

        // Find the ball in _clientMap. Two tiers:
        //  1. Normal: ball was registered by proximity at T=2s → found by id.
        //  2. Proximity fallback: field ball intaked before T=2s (server registration
        //     position was inside the robot; client ball still on the field) →
        //     search all client pieces for the closest unclaimed piece of the right type,
        //     excluding balls already parented to this node (those are preloaded balls
        //     belonging to a different server ID and will receive their own MSG_ATTACH).
        //
        // Note: we intentionally do NOT use a "preloaded-in-node" tier-2 fallback.
        // Tier-2 caused preloaded balls to be matched to field-ball MSG_ATTACH events,
        // adding them to _clientAttached and freezing them at localPosition=zero.
        // Preloaded-ball MSG_ATTACH events that arrive before MSG_REG are left pending
        // (_pendingAttaches) and retried once registration populates _clientMap.
        if (!_clientMap.TryGetValue(id, out var piece) || piece == null)
        {
            GamePiece candidate = null;

            // Proximity fallback: closest unclaimed ball of the right type near the node,
            // excluding balls already inside the node hierarchy (those are preloaded).
            if (_clientPieces == null || _clientPieces.Length == 0)
                _clientPieces = FindObjectsOfType<GamePiece>();

            var nodePos = node.transform.position;
            float bestSqDist = float.MaxValue;
            foreach (var cp in _clientPieces)
            {
                if (cp == null || cp.pieceType != pieceType) continue;
                if (_clientMap.ContainsValue(cp)) continue;
                // Skip balls that are already children of this node — they're preloaded
                // pieces with their own server IDs and should not be hijacked here.
                if (cp.transform.IsChildOf(node.transform)) continue;
                float d = (cp.transform.position - nodePos).sqrMagnitude;
                if (d < bestSqDist) { bestSqDist = d; candidate = cp; }
            }
            if (candidate != null)
                Debug.Log($"[Net][Client] MSG_ATTACH id={id}: proximity fallback found {candidate.name} at dist={Mathf.Sqrt(bestSqDist):F1}m");

            if (candidate == null)
            {
                // No ball found. If registration hasn't happened yet (_clientMap empty),
                // return false so the caller buffers this in _pendingAttaches and retries
                // once MSG_REG arrives and populates the map.  If registration has already
                // run and we still can't find anything, the ball is genuinely absent.
                return _clientMap.Count > 0;
            }

            _clientMap[id] = candidate;
            piece = candidate;
            if (piece.rb != null && !piece.rb.isKinematic)
            {
                piece.rb.isKinematic   = true;
                piece.rb.interpolation = RigidbodyInterpolation.None;
            }
        }

        // Clear stale reference from any previous node so its stowing maintenance
        // doesn't fight against the new attachment target.
        var prevAttachNode = piece.transform.parent?.GetComponent<BuildNode>();
        if (prevAttachNode != null && prevAttachNode != node && prevAttachNode.currentGamePiece == piece)
            prevAttachNode.currentGamePiece = null;

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
