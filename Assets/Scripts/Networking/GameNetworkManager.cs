using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Util;

// Single NetworkObject that handles:
//   - Robot transform sync   (ClientRpc at ~20 Hz)
//   - Client input routing   (unreliable ServerRpc)
//   - FMS state sync         (NetworkVariables)
//
// Supports 1v1 and 2v2:
//   1v1  — host controls slot 0 (blue), client controls slot 1 (red)
//   2v2  — host controls slots 0-1 (blue), client controls slots 2-3 (red)
//
// The host publishes the active play mode via _netPlayMode so both sides
// always agree on which slots the client owns, regardless of local settings.

[RequireComponent(typeof(NetworkObject))]
public class GameNetworkManager : NetworkBehaviour
{
    [Tooltip("Broadcast robot transforms every N FixedUpdate ticks (3 ≈ 20 Hz at 50 Hz fixed rate)")]
    [SerializeField] private int robotSyncEveryNFixed = 3;

    // FMS state — server writes, clients read
    private readonly NetworkVariable<float> _netMatchTimer = new(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> _netMatchState = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> _netRobotState = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Play mode broadcast so the client knows which slots it owns.
    // Host sets this on spawn; value is already present when the client's
    // OnNetworkSpawn fires because NGO syncs NetworkVariables before calling it.
    private readonly NetworkVariable<byte> _netPlayMode = new(
        (byte)PlayMode.OneVsOne,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private LoadMatch _loadMatch;
    private int _robotSyncTick;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        _loadMatch = FindFirstObjectByType<LoadMatch>();
        if (_loadMatch == null) { StartCoroutine(WaitForLoadMatch()); return; }
        ConfigureForRole();
    }

    private IEnumerator WaitForLoadMatch()
    {
        while (_loadMatch == null)
        {
            yield return new WaitForSeconds(0.5f);
            _loadMatch = FindFirstObjectByType<LoadMatch>();
        }
        ConfigureForRole();
    }

    private void ConfigureForRole()
    {
        if (IsHost)
        {
            // Publish the current play mode so the client can determine its slots.
            var playMode = _loadMatch.GetSettingsCopy().playMode;
            _netPlayMode.Value = (byte)playMode;

            // Deactivate local input on whichever slots the client will drive.
            var (first, last) = ClientSlots(playMode);
            for (int i = first; i <= last; i++)
            {
                var robot = _loadMatch.GetRobotLoaded(i);
                robot?.GetComponent<PlayerInput>()?.DeactivateInput();
            }
        }

        if (IsClient && !IsHost)
        {
            // Make all client-side robots kinematic — host owns all physics.
            for (int i = 0; i < 4; i++)
            {
                var robot = _loadMatch.GetRobotLoaded(i);
                if (robot == null) continue;
                var rb = robot.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
            }

            StartCoroutine(InputSendLoop());
        }
    }

    // Returns the inclusive slot range the CLIENT controls for a given play mode.
    // (-1, -1) means no client-controlled slots (practice modes).
    private static (int first, int last) ClientSlots(PlayMode mode) => mode switch
    {
        PlayMode.OneVsOne => (1, 1),
        PlayMode.TwoVsTwo => (2, 3),
        _                 => (-1, -1)
    };

    // ── Server: push FMS state into NetworkVariables each frame ──────────────

    private void Update()
    {
        if (!IsServer) return;
        _netMatchTimer.Value = FMS.MatchTimer;
        _netMatchState.Value = (byte)FMS.MatchState;
        _netRobotState.Value = (byte)FMS.RobotState;
    }

    // ── Client: keep local FMS timer display accurate (runs after FMS.Update) ─

    private void LateUpdate()
    {
        if (IsClient && !IsHost)
            FMS.MatchTimer = _netMatchTimer.Value;
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

            var mode = (PlayMode)_netPlayMode.Value;
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
}
