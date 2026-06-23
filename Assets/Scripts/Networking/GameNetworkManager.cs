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
        }
    }

    // Sets every non-root, non-GamePiece Rigidbody on every loaded robot kinematic.
    // Called on the client after role config is applied.  The root Rigidbody is already
    // handled by RebindForNetworkPlay; we only need the joint children here.
    private void MakeChildRigidbodiesKinematic()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            var robot = _loadMatch.GetRobotLoaded(slot);
            if (robot == null) continue;
            var rootRb = robot.GetComponent<Rigidbody>();
            foreach (var rb in robot.GetComponentsInChildren<Rigidbody>())
            {
                if (rb == rootRb) continue;
                if (rb.GetComponent<GamePiece>() != null) continue;
                rb.isKinematic = true;
            }
        }
    }

    // ── Server: apply client joystick input to the correct robot ─────────────

    [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
    public void SendRobotInputServerRpc(int slot, float tx, float ty, float rx)
    {
        if (_loadMatch == null) return;
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;
        robot.GetComponent<SwerveController>()?.overideInputs(tx, ty, rx, disruptable: false);
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
                bool triggered = ((triggeredMask >> bit) & 1UL) != 0;
                bool held      = ((heldMask      >> bit) & 1UL) != 0;
                // Always call SetNetworkInput so held=false clears the held state
                // on the JointController when the button is released.
                jc.SetNetworkInput(sp, triggered, held);
            }
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
    // into a single unreliable custom message.  Client receives and sets kinematic joints.
    private void SendJointSync()
    {
        // First pass: collect filtered Rigidbody lists so we know exact buffer size.
        var perSlot = new System.Collections.Generic.List<Rigidbody>[4];
        int totalBytes = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            var robot = _loadMatch.GetRobotLoaded(slot);
            if (robot == null) continue;
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
            totalBytes += 2 + clamped * 28; // header (slot+count) + joints (pos+rot)
        }
        if (totalBytes == 0) return;

        using var writer = new FastBufferWriter(totalBytes, Allocator.Temp);
        for (int slot = 0; slot < 4; slot++)
        {
            var buf = perSlot[slot];
            if (buf == null) continue;
            writer.WriteValueSafe((byte)slot);
            writer.WriteValueSafe((byte)Mathf.Min(buf.Count, 255));
            int limit = Mathf.Min(buf.Count, 255);
            for (int j = 0; j < limit; j++)
            {
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
    private void OnJointSyncReceived(ulong senderId, FastBufferReader reader)
    {
        if (_loadMatch == null) return;

        while (reader.Position < reader.Length)
        {
            reader.ReadValueSafe(out byte slot);
            reader.ReadValueSafe(out byte jointCount);

            var robot  = _loadMatch.GetRobotLoaded(slot);
            var rootRb = robot != null ? robot.GetComponent<Rigidbody>() : null;

            // Pre-build the filtered list in the same order as SendJointSync (O(n) total).
            Rigidbody[] filtered = System.Array.Empty<Rigidbody>();
            if (robot != null)
            {
                var allRbs = robot.GetComponentsInChildren<Rigidbody>();
                var buf = new System.Collections.Generic.List<Rigidbody>(allRbs.Length);
                foreach (var rb in allRbs)
                {
                    if (rb == rootRb) continue;
                    if (rb.GetComponent<GamePiece>() != null) continue;
                    buf.Add(rb);
                }
                filtered = buf.ToArray();
            }

            for (int j = 0; j < jointCount; j++)
            {
                reader.ReadValueSafe(out float px); reader.ReadValueSafe(out float py); reader.ReadValueSafe(out float pz);
                reader.ReadValueSafe(out float qx); reader.ReadValueSafe(out float qy); reader.ReadValueSafe(out float qz); reader.ReadValueSafe(out float qw);

                if (j >= filtered.Length) continue;
                var rb = filtered[j];
                if (rb == null || !rb.isKinematic) continue;
                rb.position = new Vector3(px, py, pz);
                rb.rotation = new Quaternion(qx, qy, qz, qw);
            }
        }
    }

    // ── Client: read local input and forward to host ──────────────────────────

    private IEnumerator InputSendLoop()
    {
        var waitFixed = new WaitForFixedUpdate();
        int tick = 0;

        while (IsClient && !IsHost)
        {
            yield return waitFixed;
            if (++tick < robotSyncEveryNFixed) continue;
            tick = 0;

            // Use local settings (same propagation-delay fix as ApplyRoleToLoadedMatch).
            var mode = _loadMatch != null
                ? (PlayMode)_loadMatch.GetSettingsCopy().playMode
                : (PlayMode)_netPlayMode.Value;
            var (first, last) = ClientSlots(mode);
            for (int i = first; i <= last; i++)
                SendInputForSlot(i);
        }
    }

    private void SendInputForSlot(int slot)
    {
        if (slot < 0 || _loadMatch == null) return;
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;

        var pi = robot.GetComponent<PlayerInput>();
        if (pi == null) return;

        // ── Drivetrain ──────────────────────────────────────────────────────
        var left  = pi.actions.FindAction("LeftStick")?.ReadValue<Vector2>()  ?? Vector2.zero;
        var right = pi.actions.FindAction("RightStick")?.ReadValue<Vector2>() ?? Vector2.zero;
        SendRobotInputServerRpc(slot, left.x, left.y, right.x);

        // ── Mechanisms (JointController setpoints) ──────────────────────────
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

        // Always send so the server sees held=false when the button is released.
        // The server-side _netHeld[] reflects the current held state, not accumulated.
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
