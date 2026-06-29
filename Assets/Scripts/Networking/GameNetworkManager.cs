using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
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

    // Set by NetworkLobbyUI when the host assigns this machine's slot.
    // -1 means unset (use legacy mode-based range). Persists across match resets.
    public int LocalClientSlot = -1;

    // Fires on the client machine when the host clicks Apply.
    // OptionsMenuController subscribes to apply host settings and start the match automatically.
    public static event Action<MatchSettings> OnNetworkMatchStart;

    // Sync rate: broadcast every N FixedUpdate ticks (50 Hz / N).
    // N=2 = 25 Hz; rb.position= teleport + Rigidbody.interpolation keeps visuals smooth.
    private readonly int robotSyncEveryNFixed = 2;

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

    // -1=hidden, 3/2/1=counting, 0=GO!
    private readonly NetworkVariable<sbyte> _netCountdown = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private LoadMatch _loadMatch;
    private FMS _fms;
    private Canvas _countdownCanvas;
    private TMP_Text _countdownLabel;
    private bool _countdownRunning;
    private Canvas _disconnectCanvas;
    private TMP_Text _disconnectLabel;
    private bool _weAreLeaving;
    private int _robotSyncTick;

    // Tracks the LoadMatch.SetupVersion for which we last applied role config.
    // Using version instead of a bool means a new ResetField() always triggers
    // re-application even when the setup coroutine completes in the same frame.
    private int _lastAppliedSetupVersion = -1;

    // Triggered-event accumulator: BuildNode Tap actions (and JC Sequence/Toggle) fire a
    // one-frame "triggered" event.  SendMechInputForSlot only polls every robotSyncEveryNFixed
    // FixedUpdates (~60 ms), so pressing a Tap-type button between polls silently drops the
    // event.  AccumulateClientTriggeredInputs() runs every Update to capture these events and
    // ORs them into the next mech send so no tap is ever missed.
    private readonly ulong[] _pendingClientTriggeredMask = new ulong[4];

    // Buffered MovePosition targets for kinematic robot Rigidbodies on the client.
    // OnJointSyncReceived (NGO EarlyUpdate) populates this; FixedUpdate consumes it.
    // Buffering ensures exactly one MovePosition per physics step regardless of how many
    // sync packets arrive per EarlyUpdate — prevents velocity amplification that caused
    // ball-pile explosions when multiple packets were batched before one FixedUpdate.
    private readonly Dictionary<Rigidbody, (Vector3 pos, Quaternion rot)> _kinematicTargets = new();

    // Client-side receive cache: filtered joint RB list per slot.
    // Invalidated when SetupVersion changes (robot swapped or field reset).
    private readonly Rigidbody[][] _cachedJointRbs = new Rigidbody[4][];
    private int _jointCacheSetupVersion = -1;

    // Per-slot JointController and BuildNode caches for AccumulateClientTriggeredInputs.
    // Built once in ApplyRoleToLoadedMatch to avoid GetComponentsInChildren every Update frame.
    private readonly JointController[][] _cachedClientJcs = new JointController[4][];
    private readonly BuildNode[][]       _cachedClientBns = new BuildNode[4][];

    // Host-side send cache: same data, rebuilt on SetupVersion change.
    // Eliminates GetComponentsInChildren calls at 50 Hz from SendJointSync.
    private readonly Rigidbody[][] _cachedSendJointRbs = new Rigidbody[4][];
    private readonly Transform[]   _cachedSendTransforms = new Transform[4];
    private int _sendCacheSetupVersion = -1;
    private readonly List<Rigidbody> _rbScratch = new List<Rigidbody>();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake() { Instance = this; }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public override void OnNetworkSpawn()
    {
        _weAreLeaving = false;
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
            NetworkManager.OnClientConnectedCallback    += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback   += OnRemoteClientDisconnected;
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

            _netCountdown.OnValueChanged += (_, v) => UpdateCountdownOverlay(v);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            NetworkManager.OnClientConnectedCallback  -= OnClientConnected;
            NetworkManager.OnClientDisconnectCallback -= OnRemoteClientDisconnected;
        }

        if (IsClient && !IsHost && NetworkManager.CustomMessagingManager != null)
            NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_JOINT_SYNC);
    }

    // Called on the host whenever a new client joins.
    // If robots are already loaded (match in progress), rebind immediately.
    private void OnClientConnected(ulong clientId)
    {
        if (clientId == NetworkManager.LocalClientId) return;
        if (_loadMatch != null && _loadMatch.GetRobotLoaded(0) != null)
            _loadMatch.RebindForNetworkPlay(true, (PlayMode)_netPlayMode.Value, 0);
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

        // Capture one-frame "triggered" events every Update so Tap-type BuildNode actions
        // (and JC Sequence/Toggle setpoints) aren't missed by the 3-tick mech-input poll.
        if (IsClient && !IsHost)
            AccumulateClientTriggeredInputs();
    }

    // Traverses client-owned robots with the same bit layout as SendMechInputForSlot and
    // ORs any triggered events into _pendingClientTriggeredMask.  Consumed + cleared at the
    // next SendMechInputForSlot call so nothing is double-counted.
    private void AccumulateClientTriggeredInputs()
    {
        if (_loadMatch == null) return;
        var mode = (PlayMode)_netPlayMode.Value;
        int first, last;
        if (LocalClientSlot >= 0) { first = LocalClientSlot; last = LocalClientSlot; }
        else { (first, last) = ClientSlots(mode); }

        for (int slot = first; slot <= last; slot++)
        {
            var robot = _loadMatch.GetRobotLoaded(slot);
            if (robot == null) continue;
            var pi = robot.GetComponent<PlayerInput>();
            if (pi == null) continue;
            var actionMap = pi.actions.FindActionMap("Robot");
            if (actionMap == null) continue;

            int bit = 0;

            var jcs = _cachedClientJcs[slot];
            if (jcs != null) foreach (var jc in jcs)
            {
                if (jc.setPoints == null) continue;
                for (int sp = 0; sp < jc.setPoints.Length && bit < 64; sp++, bit++)
                {
                    var spData = jc.setPoints[sp];
                    var ca = actionMap.FindAction(spData.controllerButton.ToString());
                    var ka = actionMap.FindAction(spData.keyboardButton.ToString());
                    bool t = (ca?.triggered ?? false) || (ka?.triggered ?? false);
                    if (t) _pendingClientTriggeredMask[slot] |= 1UL << bit;
                }
            }

            var bns = _cachedClientBns[slot];
            if (bns != null) foreach (var bn in bns)
            {
                if (bn.Actions == null) continue;
                for (int a = 0; a < bn.Actions.Length && bit < 64; a++, bit++)
                {
                    var act = bn.Actions[a];
                    if (!act.InputRequired) continue;
                    var ca = actionMap.FindAction(act.ControllerButton.ToString());
                    var ka = actionMap.FindAction(act.KeyboardButton.ToString());
                    bool t = (ca?.triggered ?? false) || (ka?.triggered ?? false);
                    if (t) _pendingClientTriggeredMask[slot] |= 1UL << bit;
                }
            }
            // AutoAim uses hold-state only — no triggered accumulation needed.
        }
    }

    private void LateUpdate()
    {
        if (IsClient && !IsHost)
        {
            // Override whatever FMS.Update() set this frame with the host-authoritative values.
            // LateUpdate runs after Update, so this is the final value each frame.
            float prevTimer = FMS.MatchTimer;
            float netTimer  = _netMatchTimer.Value;

            FMS.MatchTimer        = netTimer;
            FMS.MatchState        = (MatchState)_netMatchState.Value;
            FMS.RobotState        = (RobotState)_netRobotState.Value;
            ScoreHolder.BlueScore = _netBlueScore.Value;
            ScoreHolder.RedScore  = _netRedScore.Value;

            // The network timer can jump by more than one frame's deltaTime (network tick rate
            // is lower than frame rate), skipping over a threshold that CrossedTime in
            // FMS.Update() would have caught. Check explicitly with the actual transition values.
            if (prevTimer != netTimer)
            {
                if (_fms == null) _fms = FindFirstObjectByType<FMS>();
                _fms?.CheckTimerCrossings(prevTimer, netTimer);
            }
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
            {
                // Host always controls slot 0; disable input on all other slots.
                _loadMatch.RebindForNetworkPlay(true, mode, 0);
                if (!_countdownRunning)
                {
                    FMS.RobotState = RobotState.disabled;
                    StartCoroutine(CountdownCoroutine());
                }
            }
        }
        else
        {
            // Read mode from local settings applied by SyncAndStartMatchClientRpc rather than
            // from _netPlayMode, which may not have propagated from the host yet at this point.
            var mode = (PlayMode)_loadMatch.GetSettingsCopy().playMode;
            _loadMatch.RebindForNetworkPlay(false, mode, LocalClientSlot);

            // Cache JointControllers and BuildNodes so AccumulateClientTriggeredInputs
            // doesn't call GetComponentsInChildren every Update frame.
            for (int s = 0; s < 4; s++)
            {
                var r = _loadMatch.GetRobotLoaded(s);
                _cachedClientJcs[s] = r?.GetComponentsInChildren<JointController>();
                _cachedClientBns[s] = r?.GetComponentsInChildren<BuildNode>();
            }

            // Make all child Rigidbodies kinematic on the client so the host-authoritative
            // joint sync (MSG_JOINT_SYNC) can drive them without fighting local physics.
            MakeChildRigidbodiesKinematic();

            // Host is authoritative for ALL robot physics. Make every root Rigidbody
            // kinematic so MSG_JOINT_SYNC drives it via rb.position= each FixedUpdate.
            for (int slot = 0; slot < 4; slot++)
            {
                var robot = _loadMatch.GetRobotLoaded(slot);
                if (robot == null) continue;
                var rb = robot.GetComponent<Rigidbody>();
                if (rb == null) continue;
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
        }
    }

    // Makes every non-root, non-GamePiece Rigidbody on every loaded robot kinematic.
    // Called from ApplyRoleToLoadedMatch as early as possible so joints are ready before
    // the first joint-sync packet arrives. OnJointSyncReceived also sets kinematic inline
    // as a belt-and-suspenders in case this runs after the first packets.
    // GamePiece RBs are excluded — they're handled by PieceSyncManager.
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
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
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
        // Client: apply buffered kinematic teleport targets. Buffering in a dict means
        // multiple packets arriving in the same EarlyUpdate are deduplicated — only the
        // latest position per Rigidbody is applied.
        if (IsClient && !IsHost && _kinematicTargets.Count > 0)
        {
            foreach (var kvp in _kinematicTargets)
            {
                if (kvp.Key == null || !kvp.Key.isKinematic) continue;
                KinematicMove(kvp.Key, kvp.Value.pos, kvp.Value.rot);
            }
            _kinematicTargets.Clear();
        }

        if (!IsServer) return;
        if (++_robotSyncTick < robotSyncEveryNFixed) return;
        _robotSyncTick = 0;

        if (_loadMatch == null) return;
        if (NetworkManager.ConnectedClientsList.Count <= 1) return;

        // Root transforms are embedded in SendJointSync — no separate ClientRpc needed.
        SendJointSync();
    }

    private static void KinematicMove(Rigidbody rb, Vector3 pos, Quaternion rot)
    {
        rb.position = pos;
        // Normalize before assigning: half-precision float16 loses enough mantissa bits
        // that the reconstructed quaternion is no longer exactly unit-length, causing
        // Unity to reject it with "Rotation quaternions must be unit length" every FixedUpdate.
        rb.rotation = Quaternion.Normalize(rot);
    }

    // Packs all non-root, non-GamePiece child Rigidbody positions for all loaded robots
    // into a single unreliable custom message using half-precision floats (float16).
    // Root pos/rot is embedded per slot so clients need no separate root sync message.
    // Joint lists are cached per SetupVersion to avoid GetComponentsInChildren at 50 Hz.
    private void SendJointSync()
    {
        // Rebuild joint cache when the field resets (new robots spawned).
        int setupVer = _loadMatch.SetupVersion;
        if (setupVer != _sendCacheSetupVersion)
        {
            _sendCacheSetupVersion = setupVer;
            for (int slot = 0; slot < 4; slot++)
            {
                var robot = _loadMatch.GetRobotLoaded(slot);
                _cachedSendTransforms[slot] = robot != null ? robot.transform : null;
                _cachedSendJointRbs[slot]   = null;
                if (robot == null) continue;
                var rootRb = robot.GetComponent<Rigidbody>();
                _rbScratch.Clear();
                foreach (var rb in robot.GetComponentsInChildren<Rigidbody>())
                {
                    if (rb == rootRb) continue;
                    if (rb.GetComponent<GamePiece>() != null) continue;
                    _rbScratch.Add(rb);
                }
                if (_rbScratch.Count > 0)
                    _cachedSendJointRbs[slot] = _rbScratch.ToArray();
            }
        }

        // Calculate buffer size. Half-precision: pos=6 B, rot=8 B per transform.
        // Per slot: slot(1) + rootPos(6) + rootRot(8) + count(1) + joints(count×14)
        int totalBytes = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            var buf = _cachedSendJointRbs[slot];
            if (buf == null) continue;
            totalBytes += 16 + Mathf.Min(buf.Length, 255) * 14;
        }
        if (totalBytes == 0) return;

        using var writer = new FastBufferWriter(totalBytes, Allocator.Temp);
        for (int slot = 0; slot < 4; slot++)
        {
            var buf     = _cachedSendJointRbs[slot];
            var robotTx = _cachedSendTransforms[slot];
            if (buf == null || robotTx == null) continue;

            var rp = robotTx.position;
            var rq = robotTx.rotation;
            writer.WriteValueSafe((byte)slot);
            writer.WriteValueSafe(Mathf.FloatToHalf(rp.x)); writer.WriteValueSafe(Mathf.FloatToHalf(rp.y)); writer.WriteValueSafe(Mathf.FloatToHalf(rp.z));
            writer.WriteValueSafe(Mathf.FloatToHalf(rq.x)); writer.WriteValueSafe(Mathf.FloatToHalf(rq.y)); writer.WriteValueSafe(Mathf.FloatToHalf(rq.z)); writer.WriteValueSafe(Mathf.FloatToHalf(rq.w));

            int limit = Mathf.Min(buf.Length, 255);
            writer.WriteValueSafe((byte)limit);
            for (int j = 0; j < limit; j++)
            {
                var p = buf[j].position;
                var q = buf[j].rotation;
                writer.WriteValueSafe(Mathf.FloatToHalf(p.x)); writer.WriteValueSafe(Mathf.FloatToHalf(p.y)); writer.WriteValueSafe(Mathf.FloatToHalf(p.z));
                writer.WriteValueSafe(Mathf.FloatToHalf(q.x)); writer.WriteValueSafe(Mathf.FloatToHalf(q.y)); writer.WriteValueSafe(Mathf.FloatToHalf(q.z)); writer.WriteValueSafe(Mathf.FloatToHalf(q.w));
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

            // Root position — half-precision (float16), 6+8 bytes instead of 12+16.
            reader.ReadValueSafe(out ushort rpx); reader.ReadValueSafe(out ushort rpy); reader.ReadValueSafe(out ushort rpz);
            reader.ReadValueSafe(out ushort rqx); reader.ReadValueSafe(out ushort rqy); reader.ReadValueSafe(out ushort rqz); reader.ReadValueSafe(out ushort rqw);
            var rootPos = new Vector3(Mathf.HalfToFloat(rpx), Mathf.HalfToFloat(rpy), Mathf.HalfToFloat(rpz));
            var rootRot = new Quaternion(Mathf.HalfToFloat(rqx), Mathf.HalfToFloat(rqy), Mathf.HalfToFloat(rqz), Mathf.HalfToFloat(rqw));

            reader.ReadValueSafe(out byte jointCount);

            var robot  = _loadMatch.GetRobotLoaded(slot);
            var rootRb = robot != null ? robot.GetComponent<Rigidbody>() : null;

            // Buffer the target for FixedUpdate — do not call MovePosition here (EarlyUpdate).
            // Calling it from EarlyUpdate means multiple packets batched before one FixedUpdate
            // each overwrite the target; only the last survives, but the sweep covers N ticks
            // of displacement in 1 step → N× implied velocity → ball-pile explosion.
            if (rootRb != null)
                _kinematicTargets[rootRb] = (rootPos, rootRot);

            // Use cached filtered joint list — GetComponentsInChildren called 50×/sec otherwise.
            // Cache is invalidated when SetupVersion changes (robot swapped or field reset).
            int setupVer = _loadMatch != null ? _loadMatch.SetupVersion : -1;
            if (setupVer != _jointCacheSetupVersion)
            {
                System.Array.Clear(_cachedJointRbs, 0, _cachedJointRbs.Length);
                _jointCacheSetupVersion = setupVer;
            }

            Rigidbody[] filtered;
            if (robot != null)
            {
                if (_cachedJointRbs[slot] == null)
                {
                    var allRbs = robot.GetComponentsInChildren<Rigidbody>();
                    var fbuf = new List<Rigidbody>(allRbs.Length);
                    foreach (var rb in allRbs)
                    {
                        if (rb == rootRb) continue;
                        if (rb.GetComponent<GamePiece>() != null) continue;
                        if (!rb.isKinematic)
                        {
                            rb.isKinematic = true;
                            rb.interpolation = RigidbodyInterpolation.Interpolate;
                            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                        }
                        fbuf.Add(rb);
                    }
                    _cachedJointRbs[slot] = fbuf.ToArray();
                }
                filtered = _cachedJointRbs[slot];
            }
            else
            {
                filtered = System.Array.Empty<Rigidbody>();
            }

            for (int j = 0; j < jointCount; j++)
            {
                reader.ReadValueSafe(out ushort px); reader.ReadValueSafe(out ushort py); reader.ReadValueSafe(out ushort pz);
                reader.ReadValueSafe(out ushort qx); reader.ReadValueSafe(out ushort qy); reader.ReadValueSafe(out ushort qz); reader.ReadValueSafe(out ushort qw);

                if (j >= filtered.Length) continue;
                var rb = filtered[j];
                if (rb == null) continue;

                var worldPos = new Vector3(Mathf.HalfToFloat(px), Mathf.HalfToFloat(py), Mathf.HalfToFloat(pz));
                var worldRot = new Quaternion(Mathf.HalfToFloat(qx), Mathf.HalfToFloat(qy), Mathf.HalfToFloat(qz), Mathf.HalfToFloat(qw));
                _kinematicTargets[rb] = (worldPos, worldRot);
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
            int first, last;
            if (LocalClientSlot >= 0) { first = LocalClientSlot; last = LocalClientSlot; }
            else { (first, last) = ClientSlots(mode); }

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

        // The host's _isNetworkControlled path skips the reversed check in SwerveController,
        // so we must apply it here before sending — mirrors the local singleplayer path.
        var swerve = robot.GetComponent<SwerveController>();
        if (swerve != null && swerve.reversed)
            left = -left;

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

                jc.SetNetworkInput(sp, t, h);

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

                bn.SetNetworkInput(a, t, h);
                
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

        // Flush triggers captured between polls (Update runs every frame, this runs every
        // robotSyncEveryNFixed ticks — without this, Tap-type actions pressed mid-gap are lost).
        triggered |= _pendingClientTriggeredMask[slot];
        _pendingClientTriggeredMask[slot] = 0;

        // Always send so the server sees held=false when the button is released.
        SendRobotActionsServerRpc(slot, triggered, held);
    }

    private static (int first, int last) ClientSlots(PlayMode mode) => mode switch
    {
        PlayMode.OneVsOne  => (1, 1),
        PlayMode.TwoVsTwo  => (2, 3),
        PlayMode.TwoVsZero => (1, 1),
        _                  => (-1, -1)
    };

    // ── Host → Client: sync match settings and start the match ───────────────

    // Call from OptionsMenuController.ApplyAndClose() when hosting.
    // Sends host's settings to all clients so they start the same match automatically.
    // Must be called AFTER the host's ResetField() so FMS.MatchTimer is already at matchTime.
    public void BroadcastMatchStart(MatchSettings settings)
    {
        if (!IsHost || !IsSpawned) return;

        // Pre-flush reset state into NetworkVariables before the RPC so both updates
        // land in the same NGO tick. Without this, the client's LateUpdate can override
        // the freshly-reset FMS timer/score with the stale end-of-previous-match values
        // for 1–2 frames, which re-triggers MatchEndPause and freezes the second match.
        _netMatchTimer.Value = FMS.MatchTimer;        // matchTime, after ResetField
        _netMatchState.Value = (byte)MatchState.auto;
        _netRobotState.Value = (byte)RobotState.disabled;
        _netBlueScore.Value  = 0;
        _netRedScore.Value   = 0;

        // Trigger an early re-registration ~2.5s after match start so clients see new balls
        // without waiting up to 5s for the normal RegistrationLoop interval to fire.
        FindFirstObjectByType<PieceSyncManager>()?.TriggerEarlyRegistration();

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
            (byte)(settings.useBlueAlliance ? 1 : 0),
            settings.activeSlotMask
        );
    }

    [ClientRpc]
    private void SyncAndStartMatchClientRpc(
        byte playMode, byte r1, byte r2, byte r3, byte r4,
        byte bs1, byte bs2, byte rs1, byte rs2,
        byte view, byte useBlue, byte activeSlotMask)
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
        settings.activeSlotMask  = activeSlotMask;

        // Pre-apply settings so the mode is correct when ApplyRoleToLoadedMatch
        // reads it. HandleNetworkMatchStart may call ApplySettings again (idempotent).
        _loadMatch.ApplySettings(settings);

        int versionAtRpc = _loadMatch.SetupVersion;
        OnNetworkMatchStart?.Invoke(settings);

        // Block the start sound until the host enables robots after the countdown.
        // FMS.Restart() (called inside ResetField above) sets RobotState=enabled — this
        // immediately overrides it to disabled so HandleSounds() can't fire before GO!.
        FMS.RobotState = RobotState.disabled;

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

    // ── Disconnect / leave match ──────────────────────────────────────────────

    // Called when host or client intentionally ends the match via ESC.
    public void LeaveMatch()
    {
        if (!IsSpawned || !NetworkManager.Singleton.IsListening) return;
        _weAreLeaving = true;

        if (IsHost)
        {
            NotifyMatchEndedClientRpc();
            StartCoroutine(ShutdownAfterNotify());
        }
        else
        {
            NetworkManager.Singleton.Shutdown();
            ResetFieldAsSingleplayer();
        }
    }

    // Resets back to singleplayer so ESC/disconnect doesn't flash splitscreen layout.
    private void ResetFieldAsSingleplayer()
    {
        if (_loadMatch == null) return;
        var s = _loadMatch.GetSettingsCopy();
        s.playMode = PlayMode.OneVsZero;
        _loadMatch.ApplySettings(s);
        _loadMatch.ResetField();
    }

    private IEnumerator ShutdownAfterNotify()
    {
        // Extra time so the RPC reliably arrives before we tear down the network session.
        yield return new WaitForSecondsRealtime(0.5f);
        ResetFieldAsSingleplayer();
        NetworkManager.Singleton.Shutdown();
    }

    [ClientRpc]
    private void NotifyMatchEndedClientRpc()
    {
        if (IsHost) return;
        ShowDisconnectOverlay("Host ended the game");
        ResetFieldAsSingleplayer();
    }

    // Host: show a message when a remote client drops during a match, then end it.
    private void OnRemoteClientDisconnected(ulong clientId)
    {
        if (!IsHost) return;
        if (clientId == NetworkManager.LocalClientId) return;
        if (_weAreLeaving) return;
        if (_loadMatch?.GetRobotLoaded(0) == null) return;
        ShowDisconnectOverlay("Opponent disconnected");
        StartCoroutine(ResetAfterDelay(1.5f));
    }

    private IEnumerator ResetAfterDelay(float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        ResetFieldAsSingleplayer();
    }

    public void ShowDisconnectOverlay(string message)
    {
        EnsureDisconnectUI();
        _disconnectLabel.text = message;
        _disconnectCanvas.gameObject.SetActive(true);
        StartCoroutine(HideDisconnectOverlayAfter(5f));
    }

    private IEnumerator HideDisconnectOverlayAfter(float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        if (_disconnectCanvas != null) _disconnectCanvas.gameObject.SetActive(false);
    }

    private void EnsureDisconnectUI()
    {
        if (_disconnectCanvas != null) return;

        var go = new GameObject("[DisconnectOverlay]");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 998;
        go.AddComponent<CanvasScaler>();

        var panelGo = new GameObject("Panel");
        panelGo.transform.SetParent(go.transform, false);
        var panelRect = panelGo.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.15f, 0.38f);
        panelRect.anchorMax = new Vector2(0.85f, 0.62f);
        panelRect.sizeDelta = Vector2.zero;
        var panelImg = panelGo.AddComponent<Image>();
        panelImg.color = new Color(0f, 0f, 0f, 0.75f);

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var textRect = textGo.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.sizeDelta = Vector2.zero;
        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = 48;
        tmp.color = Color.white;

        _disconnectCanvas = canvas;
        _disconnectLabel = tmp;
        go.SetActive(false);
    }

    // ── Countdown ─────────────────────────────────────────────────────────────

    private IEnumerator CountdownCoroutine()
    {
        _countdownRunning = true;

        _netCountdown.Value = 3; UpdateCountdownOverlay(3);
        yield return new WaitForSecondsRealtime(1f);
        _netCountdown.Value = 2; UpdateCountdownOverlay(2);
        yield return new WaitForSecondsRealtime(1f);
        _netCountdown.Value = 1; UpdateCountdownOverlay(1);
        yield return new WaitForSecondsRealtime(1f);

        // Enable robots the moment GO! appears so FMS timer and start sound fire together.
        FMS.RobotState = RobotState.enabled;
        _netCountdown.Value = 0; UpdateCountdownOverlay(0);
        yield return new WaitForSecondsRealtime(1f);

        _netCountdown.Value = -1; UpdateCountdownOverlay(-1);
        _countdownRunning = false;
    }

    private void EnsureCountdownUI()
    {
        if (_countdownCanvas != null) return;

        var go = new GameObject("[CountdownOverlay]");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999;
        go.AddComponent<CanvasScaler>();

        // Dark semi-transparent backing panel for readability over any background.
        var panelGo = new GameObject("Panel");
        panelGo.transform.SetParent(go.transform, false);
        var panelRect = panelGo.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.3f, 0.35f);
        panelRect.anchorMax = new Vector2(0.7f, 0.65f);
        panelRect.sizeDelta = Vector2.zero;
        var panelImg = panelGo.AddComponent<Image>();
        panelImg.color = new Color(0f, 0f, 0f, 0.6f);

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var textRect = textGo.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.sizeDelta = Vector2.zero;
        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = 120;
        tmp.color = Color.white;

        _countdownCanvas = canvas;
        _countdownLabel = tmp;
        go.SetActive(false);
    }

    private void UpdateCountdownOverlay(sbyte value)
    {
        EnsureCountdownUI();
        if (value < 0)
        {
            _countdownCanvas.gameObject.SetActive(false);
            return;
        }
        _countdownCanvas.gameObject.SetActive(true);
        _countdownLabel.text = value == 0 ? "GO!" : value.ToString();
    }
}
