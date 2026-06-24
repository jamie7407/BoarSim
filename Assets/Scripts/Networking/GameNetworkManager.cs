using System;
using System.Collections;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using PlayMode = Util.PlayMode;

// Single NetworkObject that handles:
//   - Robot root transform sync   (ClientRpc at ~20 Hz)
//   - Robot joint transform sync  (CustomMessage MSG_JOINT_SYNC at ~20 Hz)
//   - Client drivetrain input     (unreliable ServerRpc)
//   - Client mechanism input      (ServerRpc — JointController button states)
//   - FMS state / score sync      (NetworkVariables)
//
// Supports 1v1 and 2v2:
//   1v1  — host controls slot 0 (blue), client controls slot 1 (red)
//   2v2  — host controls slots 0-1 (blue), client controls slots 2-3 (red)

[RequireComponent(typeof(NetworkObject))]
public class GameNetworkManager : NetworkBehaviour
{
    public static GameNetworkManager Instance { get; private set; }

    // Fires on the client machine when the host clicks Apply.
    // OptionsMenuController subscribes to apply host settings and start the match automatically.
    public static event Action<MatchSettings> OnNetworkMatchStart;

    [Tooltip("Broadcast robot transforms every N FixedUpdate ticks (3 ≈ 20 Hz at 50 Hz fixed rate)")]
    [SerializeField] private int robotSyncEveryNFixed = 3;

    private const string MSG_JOINT_SYNC = "BoarSim.JointSync";

    // FMS state — server writes, clients read
    private readonly NetworkVariable<float> _netMatchTimer = new(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> _netMatchState = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> _netRobotState = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<byte> _netPlayMode = new(
        (byte)PlayMode.OneVsOne,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int> _netBlueScore = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _netRedScore = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private LoadMatch _loadMatch;
    private int _robotSyncTick;

    // Per-slot filtered joint RB arrays built once in MakeChildRigidbodiesKinematic.
    // Avoids GetComponentsInChildren + List allocation on every incoming joint-sync packet.
    private readonly Rigidbody[][] _jointRbCache = new Rigidbody[4][];

    // Tracks the LoadMatch.SetupVersion for which we last applied role config.
    // Using version instead of a bool means a new ResetField() always triggers
    // re-application even when the setup coroutine completes in the same frame.
    private int _lastAppliedSetupVersion = -1;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake() { Instance = this; }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public override void OnNetworkSpawn()
    {
        _loadMatch = FindFirstObjectByType<LoadMatch>();
        if (_loadMatch == null) { StartCoroutine(WaitForLoadMatch()); return; }
        OnLoadMatchReady();
    }

    private IEnumerator WaitForLoadMatch()
    {
        while (_loadMatch == null)
        {
            yield return new WaitForSeconds(0.5f);
            _loadMatch = FindFirstObjectByType<LoadMatch>();
        }
        OnLoadMatchReady();
    }

    private void OnLoadMatchReady()
    {
        if (IsHost)
        {
            _netPlayMode.Value = (byte)_loadMatch.GetSettingsCopy().playMode;
            NetworkManager.OnClientConnectedCallback += OnClientConnected;
        }

        if (IsClient && !IsHost)
        {
            NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(
                MSG_JOINT_SYNC, OnJointSyncReceived);

            StartCoroutine(InputSendLoop());

            // Re-apply role config if the host publishes the play mode after
            // we've already applied (e.g. mode was still default when robots loaded).
            _netPlayMode.OnValueChanged += (_, _) =>
            {
                if (_lastAppliedSetupVersion >= 0 && _loadMatch != null)
                    ApplyRoleToLoadedMatch();
            };
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            NetworkManager.OnClientConnectedCallback -= OnClientConnected;

        if (IsClient && !IsHost)
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_JOINT_SYNC);
    }

    // Called on the host whenever a new client joins.
    // If robots are already loaded (match in progress), rebind immediately.
    private void OnClientConnected(ulong clientId)
    {
        if (clientId == NetworkManager.LocalClientId) return;
        if (_loadMatch != null && _loadMatch.GetRobotLoaded(0) != null)
            _loadMatch.RebindForNetworkPlay(true, (PlayMode)_netPlayMode.Value);
    }

    // ── Per-frame ─────────────────────────────────────────────────────────────

    private void Update()
    {
        if (!IsSpawned) return;

        // Server: push FMS + score state into NetworkVariables
        if (IsServer)
        {
            _netMatchTimer.Value = FMS.MatchTimer;
            _netMatchState.Value = (byte)FMS.MatchState;
            _netRobotState.Value = (byte)FMS.RobotState;
            _netBlueScore.Value  = ScoreHolder.BlueScore;
            _netRedScore.Value   = ScoreHolder.RedScore;
        }

        // Wait for IsInputReady (PairInputs done) before applying role config —
        // otherwise SetupInputsWhenReady finishes after us and overwrites our bindings.
        // We track SetupVersion rather than a simple bool so that a new ResetField()
        // always triggers re-application even when the setup coroutine completes in
        // the same frame and IsInputReady never momentarily becomes false.
        if (_loadMatch != null)
        {
            bool robotsReady = _loadMatch.GetRobotLoaded(0) != null && _loadMatch.IsInputReady;
            int  version     = _loadMatch.SetupVersion;
            if (robotsReady && version != _lastAppliedSetupVersion)
            {
                _lastAppliedSetupVersion = version;
                ApplyRoleToLoadedMatch();
            }
        }
    }

    private void LateUpdate()
    {
        if (IsClient && !IsHost)
        {
            FMS.MatchTimer        = _netMatchTimer.Value;
            ScoreHolder.BlueScore = _netBlueScore.Value;
            ScoreHolder.RedScore  = _netRedScore.Value;
        }
    }

    private void ApplyRoleToLoadedMatch()
    {
        if (IsHost)
        {
            // Re-publish play mode in case it changed since OnNetworkSpawn
            _netPlayMode.Value = (byte)_loadMatch.GetSettingsCopy().playMode;
            var mode = (PlayMode)_netPlayMode.Value;

            // Only rebind cameras/input when a client is actually connected
            if (NetworkManager.ConnectedClientsList.Count > 1)
                _loadMatch.RebindForNetworkPlay(true, mode);
        }
        else
        {
            // Read mode from local settings applied by SyncAndStartMatchClientRpc rather than
            // from _netPlayMode, which may not have propagated from the host yet at this point.
            var mode = (PlayMode)_loadMatch.GetSettingsCopy().playMode;
            _loadMatch.RebindForNetworkPlay(false, mode);

            // Make all child Rigidbodies kinematic on the client so the host-authoritative
            // joint sync (MSG_JOINT_SYNC) can drive them without fighting local physics.
            MakeChildRigidbodiesKinematic();

            // Host is authoritative for ALL robot physics. Make every root Rigidbody
            // kinematic so SyncRobotsClientRpc drives it cleanly via rb.position/rb.rotation.
            // Without this P2's non-kinematic root fights the host position every frame,
            // causing the glitch-then-revert behaviour.
            // Disable interpolation so rb.position assignments snap immediately; interpolation
            // causes the visual transform to drift toward the target rather than snapping.
            for (int slot = 0; slot < 4; slot++)
            {
                var robot = _loadMatch.GetRobotLoaded(slot);
                if (robot == null) continue;
                var rb = robot.GetComponent<Rigidbody>();
                if (rb == null) continue;
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.None;
            }
        }
    }

    // Sets every non-root, non-GamePiece Rigidbody on every loaded robot kinematic and
    // caches the filtered list so OnJointSyncReceived doesn't rebuild it every packet.
    // GamePiece RBs are excluded here because they're handled by PieceSyncManager and
    // made kinematic in GamePiece.Start() on the client.
    private void MakeChildRigidbodiesKinematic()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            _jointRbCache[slot] = null;
            var robot = _loadMatch.GetRobotLoaded(slot);
            if (robot == null) continue;
            var rootRb = robot.GetComponent<Rigidbody>();
            var buf = new System.Collections.Generic.List<Rigidbody>();
            foreach (var rb in robot.GetComponentsInChildren<Rigidbody>())
            {
                if (rb == rootRb) continue;
                if (rb.GetComponent<GamePiece>() != null) continue;
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.None;
                buf.Add(rb);
            }
            _jointRbCache[slot] = buf.ToArray();
        }
    }

    // ── Server: apply client joystick input to the correct robot ─────────────

    [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
    public void SendRobotInputServerRpc(int slot, float tx, float ty, float rx)
    {
        if (_loadMatch == null) return;
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;
        var swerve = robot.GetComponent<SwerveController>();
        if (swerve == null) return;

        float ndx = tx, ndy = ty; // default: robot-centric pass-through

        if (swerve.fieldCentric)
        {
            // Apply field-centric rotation on the host using the robot's actual heading.
            // Mirrors SwerveController.FixedUpdate's field-centric path so P2 gets the same
            // driving feel as P1: forward on the stick always moves toward the far wall.
            // driveInput matches SwerveController's construction from the translate action.
            Vector3 driveInput = new Vector3(ty, 0f, tx);
            float angle = swerve.isRed
                ? robot.transform.localRotation.eulerAngles.y + 90f
                : robot.transform.localRotation.eulerAngles.y + 270f;
            Vector3 fr = Quaternion.AngleAxis(angle, Vector3.up) * driveInput;
            // SetNetworkDrive(x, y, r) → _translateValue=(x,y) → driveInput=(y,0,x) → fwd=y, str=x
            ndx = fr.z; ndy = fr.x;
        }

        swerve.SetNetworkDrive(ndx, ndy, rx);
    }

    // Server: apply client mechanism button presses to JointControllers.
    // triggeredMask / heldMask pack one bit per setpoint across all JointControllers
    // in GetComponentsInChildren order (same prefab hierarchy → same order on both machines).
    [ServerRpc(RequireOwnership = false)]
    public void SendRobotActionsServerRpc(int slot, ulong triggeredMask, ulong heldMask)
    {
        if (_loadMatch == null) return;
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;

        var joints = robot.GetComponentsInChildren<JointController>();
        int bit = 0;
        foreach (var jc in joints)
        {
            if (jc.setPoints == null) continue;
            for (int sp = 0; sp < jc.setPoints.Length && bit < 64; sp++, bit++)
            {
                bool trig = ((triggeredMask >> bit) & 1UL) != 0;
                bool held = ((heldMask      >> bit) & 1UL) != 0;
                // Always call SetNetworkInput so held=false clears the held state
                // on the JointController when the button is released.
                jc.SetNetworkInput(sp, trig, held);
            }
        }

        // BuildNode actions — must use the same traversal order as SendMechInputForSlot.
        var buildNodes = robot.GetComponentsInChildren<BuildNode>();
        foreach (var bn in buildNodes)
        {
            if (bn.Actions == null) continue;
            for (int a = 0; a < bn.Actions.Length && bit < 64; a++, bit++)
            {
                bool trig = ((triggeredMask >> bit) & 1UL) != 0;
                bool held = ((heldMask      >> bit) & 1UL) != 0;
                bn.SetNetworkInput(a, trig, held);
            }
        }

        // AutoAim — one bit per component; held=true means the client's button is pressed.
        var autoAims = robot.GetComponentsInChildren<AutoAim>();
        foreach (var aa in autoAims)
        {
            if (bit >= 64) break;
            bool held = ((heldMask >> bit) & 1UL) != 0;
            aa.SetNetworkActivate(held);
            bit++;
        }
    }

    // ── Server: broadcast all robot transforms + joint positions to clients ────

    private void FixedUpdate()
    {
        if (!IsServer) return;
        if (++_robotSyncTick < robotSyncEveryNFixed) return;
        _robotSyncTick = 0;

        if (_loadMatch == null) return;
        if (NetworkManager.ConnectedClientsList.Count <= 1) return;

        SyncRobotsClientRpc(
            GetRobotPos(0), GetRobotRot(0), GetRobotVel(0),
            GetRobotPos(1), GetRobotRot(1), GetRobotVel(1),
            GetRobotPos(2), GetRobotRot(2), GetRobotVel(2),
            GetRobotPos(3), GetRobotRot(3), GetRobotVel(3)
        );

        SendJointSync();
    }

    private Vector3    GetRobotPos(int s) { var r = _loadMatch.GetRobotLoaded(s); return r != null ? r.transform.position : Vector3.zero; }
    private Quaternion GetRobotRot(int s) { var r = _loadMatch.GetRobotLoaded(s); return r != null ? r.transform.rotation : Quaternion.identity; }
    private Vector3    GetRobotVel(int s) { var r = _loadMatch.GetRobotLoaded(s); if (r == null) return Vector3.zero; var rb = r.GetComponent<Rigidbody>(); return rb != null ? rb.velocity : Vector3.zero; }

    [ClientRpc]
    private void SyncRobotsClientRpc(
        Vector3 p0, Quaternion r0, Vector3 v0,
        Vector3 p1, Quaternion r1, Vector3 v1,
        Vector3 p2, Quaternion r2, Vector3 v2,
        Vector3 p3, Quaternion r3, Vector3 v3)
    {
        if (IsHost || _loadMatch == null) return;
        ApplyRobot(0, p0, r0);
        ApplyRobot(1, p1, r1);
        ApplyRobot(2, p2, r2);
        ApplyRobot(3, p3, r3);
    }

    private void ApplyRobot(int slot, Vector3 pos, Quaternion rot)
    {
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;
        var rb = robot.GetComponent<Rigidbody>();
        if (rb != null && rb.isKinematic)
        {
            rb.position = pos;
            rb.rotation = rot;
        }
        else
        {
            robot.transform.SetPositionAndRotation(pos, rot);
        }
    }

    // Packs all non-root, non-GamePiece child Rigidbody positions for all loaded robots
    // into a single unreliable custom message.  Root pos/rot is embedded alongside the joints
    // so the client always applies joints relative to the exact root sent in the same packet —
    // eliminating the race condition between the reliable ClientRpc root sync and this message.
    private void SendJointSync()
    {
        // First pass: collect filtered Rigidbody lists and store root transforms.
        var perSlot       = new System.Collections.Generic.List<Rigidbody>[4];
        var slotTransform = new Transform[4];
        int totalBytes = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            var robot = _loadMatch.GetRobotLoaded(slot);
            if (robot == null) continue;
            slotTransform[slot] = robot.transform;
            var rootRb = robot.GetComponent<Rigidbody>();
            var buf = new System.Collections.Generic.List<Rigidbody>();
            foreach (var rb in robot.GetComponentsInChildren<Rigidbody>())
            {
                if (rb == rootRb) continue;
                if (rb.GetComponent<GamePiece>() != null) continue;
                buf.Add(rb);
            }
            if (buf.Count == 0) continue;
            perSlot[slot] = buf;
            int clamped = Mathf.Min(buf.Count, 255);
            // slot(1) + rootPos(12) + rootRot(16) + count(1) + joints(count*28)
            totalBytes += 30 + clamped * 28;
        }
        if (totalBytes == 0) return;

        using var writer = new FastBufferWriter(totalBytes, Allocator.Temp);
        for (int slot = 0; slot < 4; slot++)
        {
            var buf     = perSlot[slot];
            var robotTx = slotTransform[slot];
            if (buf == null || robotTx == null) continue;

            // Embed root world pos/rot so the client uses exactly this root, not one from a
            // separately-timed reliable message (which may arrive in a different network tick).
            var rp = robotTx.position;
            var rq = robotTx.rotation;
            writer.WriteValueSafe((byte)slot);
            writer.WriteValueSafe(rp.x); writer.WriteValueSafe(rp.y); writer.WriteValueSafe(rp.z);
            writer.WriteValueSafe(rq.x); writer.WriteValueSafe(rq.y); writer.WriteValueSafe(rq.z); writer.WriteValueSafe(rq.w);
            writer.WriteValueSafe((byte)Mathf.Min(buf.Count, 255));

            int limit = Mathf.Min(buf.Count, 255);
            for (int j = 0; j < limit; j++)
            {
                // Send world-space positions directly — no local-space conversion.
                // The embedded root ensures these are always from the same physics tick,
                // so clients apply them without needing to reconstruct from local space.
                var p = buf[j].position;
                var q = buf[j].rotation;
                writer.WriteValueSafe(p.x); writer.WriteValueSafe(p.y); writer.WriteValueSafe(p.z);
                writer.WriteValueSafe(q.x); writer.WriteValueSafe(q.y); writer.WriteValueSafe(q.z); writer.WriteValueSafe(q.w);
            }
        }

        NetworkManager.CustomMessagingManager.SendNamedMessageToAll(
            MSG_JOINT_SYNC, writer, NetworkDelivery.UnreliableSequenced);
    }

    // Client: receive joint positions from host and apply to kinematic joint Rigidbodies.
    // Each slot packet includes the root world pos/rot sent in the same host FixedUpdate,
    // so the client uses exactly that root — no dependency on separately-timed root sync.
    private void OnJointSyncReceived(ulong senderId, FastBufferReader reader)
    {
        if (_loadMatch == null) return;

        while (reader.Position < reader.Length)
        {
            reader.ReadValueSafe(out byte slot);

            // Root position embedded in this packet — apply it immediately.
            reader.ReadValueSafe(out float rpx); reader.ReadValueSafe(out float rpy); reader.ReadValueSafe(out float rpz);
            reader.ReadValueSafe(out float rqx); reader.ReadValueSafe(out float rqy); reader.ReadValueSafe(out float rqz); reader.ReadValueSafe(out float rqw);
            var rootPos = new Vector3(rpx, rpy, rpz);
            var rootRot = new Quaternion(rqx, rqy, rqz, rqw);

            reader.ReadValueSafe(out byte jointCount);

            var robot  = _loadMatch.GetRobotLoaded(slot);
            var rootRb = robot != null ? robot.GetComponent<Rigidbody>() : null;

            // Apply root from this packet so joints use the same world position.
            if (rootRb != null)
            {
                rootRb.position = rootPos;
                rootRb.rotation = rootRot;
            }

            // Use the cached filtered list built in MakeChildRigidbodiesKinematic.
            // Avoids GetComponentsInChildren + List allocation at 20 Hz.
            Rigidbody[] filtered = (slot < _jointRbCache.Length ? _jointRbCache[slot] : null)
                                   ?? System.Array.Empty<Rigidbody>();

            for (int j = 0; j < jointCount; j++)
            {
                // Joint positions are sent in world space — apply directly, no conversion.
                reader.ReadValueSafe(out float px); reader.ReadValueSafe(out float py); reader.ReadValueSafe(out float pz);
                reader.ReadValueSafe(out float qx); reader.ReadValueSafe(out float qy); reader.ReadValueSafe(out float qz); reader.ReadValueSafe(out float qw);

                if (j >= filtered.Length) continue;
                var rb = filtered[j];
                if (rb == null) continue;

                var worldPos = new Vector3(px, py, pz);
                var worldRot = new Quaternion(qx, qy, qz, qw);
                // Update physics position and also set transform directly for immediate visual
                // (rb.position alone defers the visual update until the next FixedUpdate).
                rb.transform.SetPositionAndRotation(worldPos, worldRot);
                if (rb.isKinematic)
                {
                    rb.position = worldPos;
                    rb.rotation = worldRot;
                }
            }
        }
    }

    // ── Client: read local input and forward to host ──────────────────────────

    private IEnumerator InputSendLoop()
    {
        var waitFixed = new WaitForFixedUpdate();
        int mechTick = 0;

        while (IsClient && !IsHost)
        {
            yield return waitFixed;

            var mode = _loadMatch != null
                ? (PlayMode)_loadMatch.GetSettingsCopy().playMode
                : (PlayMode)_netPlayMode.Value;
            var (first, last) = ClientSlots(mode);

            // Drivetrain every FixedUpdate: SetNetworkDrive() is consumed once per physics frame
            // (_useNetworkDrive resets after use), so we must call it every tick or the robot
            // coasts to a stop for frames where no new input arrives.
            for (int i = first; i <= last; i++)
                SendDriveInputForSlot(i);

            // Mechanisms throttled — reliable RPC, no need for 50 Hz.
            if (++mechTick >= robotSyncEveryNFixed)
            {
                mechTick = 0;
                for (int i = first; i <= last; i++)
                    SendMechInputForSlot(i);
            }
        }
    }

    private void SendDriveInputForSlot(int slot)
    {
        if (slot < 0 || _loadMatch == null) return;
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;
        var pi = robot.GetComponent<PlayerInput>();
        if (pi == null) return;

        var left  = pi.actions.FindAction("LeftStick")?.ReadValue<Vector2>()  ?? Vector2.zero;
        var right = pi.actions.FindAction("RightStick")?.ReadValue<Vector2>() ?? Vector2.zero;
        SendRobotInputServerRpc(slot, left.x, left.y, right.x);
    }

    private void SendMechInputForSlot(int slot)
    {
        if (slot < 0 || _loadMatch == null) return;
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;
        var pi = robot.GetComponent<PlayerInput>();
        if (pi == null) return;

        var actionMap = pi.actions.FindActionMap("Robot");
        if (actionMap == null) return;

        var joints = robot.GetComponentsInChildren<JointController>();
        if (joints.Length == 0) return;

        ulong triggered = 0, held = 0;
        int bit = 0;
        foreach (var jc in joints)
        {
            if (jc.setPoints == null) continue;
            for (int sp = 0; sp < jc.setPoints.Length && bit < 64; sp++, bit++)
            {
                var spData = jc.setPoints[sp];
                var ctrlAction = actionMap.FindAction(spData.controllerButton.ToString());
                var kbAction   = actionMap.FindAction(spData.keyboardButton.ToString());

                bool t = (ctrlAction?.triggered ?? false) || (kbAction?.triggered ?? false);
                bool h = (ctrlAction?.IsPressed() ?? false) || (kbAction?.IsPressed() ?? false);

                if (t) triggered |= 1UL << bit;
                if (h) held      |= 1UL << bit;
            }
        }

        // BuildNode actions (intake, transfer, score, etc.)
        var buildNodes = robot.GetComponentsInChildren<BuildNode>();
        foreach (var bn in buildNodes)
        {
            if (bn.Actions == null) continue;
            for (int a = 0; a < bn.Actions.Length && bit < 64; a++, bit++)
            {
                var action = bn.Actions[a];
                if (!action.InputRequired)
                {
                    held |= 1UL << bit; // AlwaysPerform — always held
                    continue;
                }
                var ca = actionMap.FindAction(action.ControllerButton.ToString());
                var ka = actionMap.FindAction(action.KeyboardButton.ToString());
                bool t = (ca?.triggered ?? false) || (ka?.triggered ?? false);
                bool h = (ca?.IsPressed() ?? false) || (ka?.IsPressed() ?? false);
                if (t) triggered |= 1UL << bit;
                if (h) held      |= 1UL << bit;
            }
        }

        // AutoAim — one bit per component: forward the local button-held state so the
        // host can activate its own PID loop even with PlayerInput disabled for P2.
        var autoAims = robot.GetComponentsInChildren<AutoAim>();
        foreach (var aa in autoAims)
        {
            if (bit >= 64) break;
            if (aa.GetButtonHeld()) held |= 1UL << bit;
            bit++;
        }

        // Always send so the server sees held=false when the button is released.
        SendRobotActionsServerRpc(slot, triggered, held);
    }

    private static (int first, int last) ClientSlots(PlayMode mode) => mode switch
    {
        PlayMode.OneVsOne => (1, 1),
        PlayMode.TwoVsTwo => (2, 3),
        _                 => (-1, -1)
    };

    // ── Host → Client: sync match settings and start the match ───────────────

    // Call from OptionsMenuController.ApplyAndClose() when hosting.
    // Sends host's settings to all clients so they start the same match automatically.
    public void BroadcastMatchStart(MatchSettings settings)
    {
        if (!IsHost || !IsSpawned) return;
        SyncAndStartMatchClientRpc(
            (byte)settings.playMode,
            (byte)Mathf.Clamp(settings.robotIndex1, 0, 255),
            (byte)Mathf.Clamp(settings.robotIndex2, 0, 255),
            (byte)Mathf.Clamp(settings.robotIndex3, 0, 255),
            (byte)Mathf.Clamp(settings.robotIndex4, 0, 255),
            (byte)Mathf.Clamp(settings.blueSpawnIndex1, 0, 255),
            (byte)Mathf.Clamp(settings.blueSpawnIndex2, 0, 255),
            (byte)Mathf.Clamp(settings.redSpawnIndex1, 0, 255),
            (byte)Mathf.Clamp(settings.redSpawnIndex2, 0, 255),
            (byte)settings.view,
            (byte)(settings.useBlueAlliance ? 1 : 0)
        );
    }

    [ClientRpc]
    private void SyncAndStartMatchClientRpc(
        byte playMode, byte r1, byte r2, byte r3, byte r4,
        byte bs1, byte bs2, byte rs1, byte rs2,
        byte view, byte useBlue)
    {
        if (IsHost) return; // host already applied locally
        if (_loadMatch == null) return;

        var settings = _loadMatch.GetSettingsCopy();
        settings.playMode        = (PlayMode)playMode;
        settings.robotIndex1     = r1;
        settings.robotIndex2     = r2;
        settings.robotIndex3     = r3;
        settings.robotIndex4     = r4;
        settings.blueSpawnIndex1 = bs1;
        settings.blueSpawnIndex2 = bs2;
        settings.redSpawnIndex1  = rs1;
        settings.redSpawnIndex2  = rs2;
        settings.view            = (Util.Cameras)view;
        settings.useBlueAlliance = useBlue != 0;

        // Pre-apply settings so the mode is correct when ApplyRoleToLoadedMatch
        // reads it. HandleNetworkMatchStart may call ApplySettings again (idempotent).
        _loadMatch.ApplySettings(settings);

        int versionAtRpc = _loadMatch.SetupVersion;
        OnNetworkMatchStart?.Invoke(settings);

        // Belt-and-suspenders: start a coroutine that waits for HandleNetworkMatchStart
        // to trigger ResetField() (version increment) and SetupInputsWhenReady to finish,
        // then explicitly applies role config. This handles cases where the Update()-based
        // version check is missed due to same-frame completion of SetupInputsWhenReady.
        StartCoroutine(ApplyRoleWhenReady(versionAtRpc + 1));
    }

    // Waits for LoadMatch to finish resetting the field (SetupVersion >= waitForVersion)
    // and for input setup to complete (IsInputReady), then applies role config.
    // Uses unscaled time so it works correctly while the options menu has timeScale = 0.
    private IEnumerator ApplyRoleWhenReady(int waitForVersion)
    {
        float elapsed = 0f;
        const float kTimeout = 8f;

        // Wait for ResetField() to run — SetupVersion must reach the expected value.
        while (_loadMatch != null && _loadMatch.SetupVersion < waitForVersion)
        {
            elapsed += Time.unscaledDeltaTime;
            if (elapsed > kTimeout) yield break;
            yield return null;
        }
        if (_loadMatch == null) yield break;

        // Wait for PairInputs() to finish.
        elapsed = 0f;
        while (_loadMatch != null && !_loadMatch.IsInputReady)
        {
            elapsed += Time.unscaledDeltaTime;
            if (elapsed > kTimeout) yield break;
            yield return null;
        }
        if (_loadMatch == null) yield break;

        // Apply role config and update the version tracker so Update() doesn't re-apply.
        _lastAppliedSetupVersion = _loadMatch.SetupVersion;
        ApplyRoleToLoadedMatch();
    }
}
