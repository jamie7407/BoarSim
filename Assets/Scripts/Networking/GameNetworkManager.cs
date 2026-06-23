using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Single NetworkObject that handles:
//   - Robot transform sync   (ClientRpc at ~20 Hz — only 4 robots so this is cheap)
//   - Client input routing   (unreliable ServerRpc so the host can drive red robots)
//   - FMS state sync         (NetworkVariables: timer, matchState, robotState)
//
// Alliance split:
//   Host   = Blue alliance (robot slots 0-1). Reads local PlayerInput normally.
//   Client = Red alliance  (robot slots 2-3). Reads local PlayerInput, forwards via ServerRpc.
//
// On the HOST:
//   - PlayerInput is deactivated on slots 2-3; those robots are driven by client ServerRpcs.
//
// On the CLIENT:
//   - All four robots are kinematic (host owns physics); GameNetworkManager teleports them
//     each frame to the positions the host broadcasts.
//   - SwerveController still runs FixedUpdate on kinematic bodies — no effect on physics,
//     so no modification of SwerveController is needed.
//   - Local PlayerInput on slots 2-3 is read and forwarded to the host each ~20 Hz.
//
// FMS timer: host drives NetworkVariable; client overrides FMS.MatchTimer in LateUpdate
// so the display stays accurate. The client's local FMS state machine runs independently
// (state transitions happen at roughly the right time based on the corrected timer).

[RequireComponent(typeof(NetworkObject))]
public class GameNetworkManager : NetworkBehaviour
{
    [Tooltip("Broadcast robot transforms every N FixedUpdate ticks (3 ≈ 20 Hz at 50 Hz)")]
    [SerializeField] private int robotSyncEveryNFixed = 3;

    // FMS NetworkVariables — server writes, all clients read
    private readonly NetworkVariable<float> _netMatchTimer = new(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> _netMatchState = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> _netRobotState = new(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private LoadMatch _loadMatch;
    private int _robotSyncTick;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        _loadMatch = FindFirstObjectByType<LoadMatch>();
        if (_loadMatch == null)
        {
            StartCoroutine(WaitForLoadMatch());
            return;
        }
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
            // Deactivate local input on the red alliance robots (slots 2-3).
            // The host will receive these inputs from the client via ServerRpc.
            for (int i = 2; i < 4; i++)
            {
                var robot = _loadMatch.GetRobotLoaded(i);
                if (robot == null) continue;
                robot.GetComponent<PlayerInput>()?.DeactivateInput();
            }
        }

        if (IsClient && !IsHost)
        {
            // Make all client-side robots kinematic; host drives their physics.
            for (int i = 0; i < 4; i++)
            {
                var robot = _loadMatch.GetRobotLoaded(i);
                if (robot == null) continue;
                var rb = robot.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
            }

            // Forward red alliance (local) input to the host at ~20 Hz.
            StartCoroutine(InputSendLoop());
        }
    }

    // ── Server: FMS state → NetworkVariables (Update) ─────────────────────────

    private void Update()
    {
        if (!IsServer) return;
        _netMatchTimer.Value = FMS.MatchTimer;
        _netMatchState.Value = (byte)FMS.MatchState;
        _netRobotState.Value = (byte)FMS.RobotState;
    }

    // ── Client: apply server FMS timer (LateUpdate runs after FMS.Update) ────

    private void LateUpdate()
    {
        if (IsClient && !IsHost)
            FMS.MatchTimer = _netMatchTimer.Value;
    }

    // ── Server: apply red robot inputs received from client ───────────────────

    // RequireOwnership=false so any client can call this RPC.
    // Delivery=Unreliable keeps input lag minimal; stale frames are harmless.
    [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
    public void SendRobotInputServerRpc(int slot, float tx, float ty, float rx)
    {
        if (_loadMatch == null) return;
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;
        // disruptable:false prevents the host's own gamepad from overriding network input
        robot.GetComponent<SwerveController>()?.overideInputs(tx, ty, rx, disruptable: false);
    }

    // ── Server: broadcast robot transforms to clients ─────────────────────────

    private void FixedUpdate()
    {
        if (!IsServer) return;
        if (++_robotSyncTick < robotSyncEveryNFixed) return;
        _robotSyncTick = 0;

        if (_loadMatch == null) return;
        if (NetworkManager.ConnectedClientsList.Count <= 1) return;

        var p0 = GetRobotPos(0); var r0 = GetRobotRot(0); var v0 = GetRobotVel(0);
        var p1 = GetRobotPos(1); var r1 = GetRobotRot(1); var v1 = GetRobotVel(1);
        var p2 = GetRobotPos(2); var r2 = GetRobotRot(2); var v2 = GetRobotVel(2);
        var p3 = GetRobotPos(3); var r3 = GetRobotRot(3); var v3 = GetRobotVel(3);

        SyncRobotsClientRpc(p0, r0, v0, p1, r1, v1, p2, r2, v2, p3, r3, v3);
    }

    private Vector3    GetRobotPos(int slot) { var r = _loadMatch.GetRobotLoaded(slot); return r != null ? r.transform.position    : Vector3.zero; }
    private Quaternion GetRobotRot(int slot) { var r = _loadMatch.GetRobotLoaded(slot); return r != null ? r.transform.rotation    : Quaternion.identity; }
    private Vector3    GetRobotVel(int slot) { var r = _loadMatch.GetRobotLoaded(slot); if (r == null) return Vector3.zero; var rb = r.GetComponent<Rigidbody>(); return rb != null ? rb.velocity : Vector3.zero; }

    [ClientRpc]
    private void SyncRobotsClientRpc(
        Vector3 p0, Quaternion r0, Vector3 v0,
        Vector3 p1, Quaternion r1, Vector3 v1,
        Vector3 p2, Quaternion r2, Vector3 v2,
        Vector3 p3, Quaternion r3, Vector3 v3)
    {
        if (IsHost || _loadMatch == null) return;

        ApplyRobot(0, p0, r0, v0);
        ApplyRobot(1, p1, r1, v1);
        ApplyRobot(2, p2, r2, v2);
        ApplyRobot(3, p3, r3, v3);
    }

    private void ApplyRobot(int slot, Vector3 pos, Quaternion rot, Vector3 vel)
    {
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;

        robot.transform.SetPositionAndRotation(pos, rot);

        var rb = robot.GetComponent<Rigidbody>();
        if (rb != null && rb.isKinematic)
        {
            rb.position = pos;
            rb.rotation = rot;
        }
    }

    // ── Client: read local red-alliance input and forward to host ─────────────

    private IEnumerator InputSendLoop()
    {
        var waitFixed = new WaitForFixedUpdate();
        int tick = 0;

        while (IsClient && !IsHost)
        {
            yield return waitFixed;

            if (++tick < robotSyncEveryNFixed) continue;
            tick = 0;

            // Client controls red alliance: slots 2-3
            SendInputForSlot(2);
            SendInputForSlot(3);
        }
    }

    private void SendInputForSlot(int slot)
    {
        if (_loadMatch == null) return;
        var robot = _loadMatch.GetRobotLoaded(slot);
        if (robot == null) return;

        var pi = robot.GetComponent<PlayerInput>();
        if (pi == null) return;

        var left  = pi.actions.FindAction("LeftStick")?.ReadValue<Vector2>()  ?? Vector2.zero;
        var right = pi.actions.FindAction("RightStick")?.ReadValue<Vector2>() ?? Vector2.zero;

        SendRobotInputServerRpc(slot, left.x, left.y, right.x);
    }
}
