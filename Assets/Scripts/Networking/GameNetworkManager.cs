using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using PlayMode = Util.PlayMode;

// Single NetworkObject that handles:
//   - Robot transform sync   (ClientRpc at ~20 Hz)
//   - Client input routing   (unreliable ServerRpc)
//   - FMS state sync         (NetworkVariables)
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

    // Tracks whether we've applied role config for the current set of loaded robots.
    // Resets to false when robots are destroyed (field reset) so we re-apply next load.
    private bool _roleApplied;

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
            StartCoroutine(InputSendLoop());
            // Re-apply role config if the host publishes the play mode after
            // we've already run (e.g. mode was still default when robots loaded).
            _netPlayMode.OnValueChanged += (_, _) =>
            {
                if (_roleApplied && _loadMatch != null)
                    ApplyRoleToLoadedMatch();
            };
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            NetworkManager.OnClientConnectedCallback -= OnClientConnected;
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
        if (_loadMatch != null)
        {
            bool robotsReady = _loadMatch.GetRobotLoaded(0) != null && _loadMatch.IsInputReady;
            if (robotsReady && !_roleApplied)
            {
                _roleApplied = true;
                ApplyRoleToLoadedMatch();
            }
            else if (!robotsReady)
            {
                _roleApplied = false;
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

    // ── Server: broadcast all robot transforms to clients ────────────────────

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
        robot.transform.SetPositionAndRotation(pos, rot);
        var rb = robot.GetComponent<Rigidbody>();
        if (rb != null && rb.isKinematic) { rb.position = pos; rb.rotation = rot; }
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

        var left  = pi.actions.FindAction("LeftStick")?.ReadValue<Vector2>()  ?? Vector2.zero;
        var right = pi.actions.FindAction("RightStick")?.ReadValue<Vector2>() ?? Vector2.zero;

        SendRobotInputServerRpc(slot, left.x, left.y, right.x);
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

        OnNetworkMatchStart?.Invoke(settings);
    }
}
