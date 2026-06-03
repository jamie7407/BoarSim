using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Owner-authoritative robot networking.
///
/// Architecture:
///   • The owning client runs the full physics simulation (SwerveController,
///     ModuleBehaviour, WheelBehaviour) — nothing changes for the owner.
///   • A FishNet NetworkTransform (added in the Editor on the same GameObject)
///     broadcasts the robot's Rigidbody position/rotation to all other clients.
///   • Non-owners see an interpolated copy of the robot; their SwerveController
///     and PlayerInput are disabled so they don't read local input.
///
/// Setup in Unity Editor:
///   1. Add NetworkObject to the robot prefab root.
///   2. Add NetworkTransform to the robot prefab root (set to "Owner" authority).
///   3. Add this component to the robot prefab root.
///   4. In LoadMatch.SpawnRobot (or equivalent), call ServerManager.Spawn()
///      and then GiveOwnership(conn) for the correct player connection.
/// </summary>
public class NetworkRobotSync : NetworkBehaviour
{
    // ── Synced cosmetic state (FishNet 4.x SyncVar<T>) ───────────────────────
    private readonly SyncVar<string> _robotName = new SyncVar<string>();
    private readonly SyncVar<bool>   _isRed     = new SyncVar<bool>();

    // ── References (cached on Start) ─────────────────────────────────────────
    private SwerveController _swerve;
    private PlayerInput      _playerInput;
    private Rigidbody        _rb;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Awake()
    {
        _robotName.OnChange += (prev, next, asServer) => gameObject.name = next;
        _isRed.OnChange     += (prev, next, asServer) =>
        {
            var frame = GetComponent<BuildFrame>();
            // bumper colour re-apply handled by LoadMatch on the owning client
        };

        _swerve      = GetComponent<SwerveController>();
        _playerInput = GetComponent<PlayerInput>();
        _rb          = GetComponent<Rigidbody>();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        if (IsOwner)
        {
            // This is our robot — full simulation runs as normal.
            EnableLocalSimulation(true);
        }
        else
        {
            // Remote robot — disable local physics driver; NetworkTransform
            // will move it via interpolation from the owning client's state.
            EnableLocalSimulation(false);
        }
    }

    // ── Server-side init helpers called by the spawning code ──────────────────

    /// <summary>
    /// Called by the server after spawning to broadcast the robot's display name
    /// and alliance to all clients.
    /// </summary>
    [Server]
    public void InitialiseRobot(string robotName, bool isRed)
    {
        _robotName.Value = robotName;
        _isRed.Value     = isRed;
    }

    // ── Owner input gate ───────────────────────────────────────────────────────
    private void EnableLocalSimulation(bool enable)
    {
        if (_swerve      != null) _swerve.enabled      = enable;
        if (_playerInput != null) _playerInput.enabled = enable;

        // On non-owners keep the Rigidbody kinematic so local physics don't
        // fight the incoming NetworkTransform position updates.
        if (_rb != null)
            _rb.isKinematic = !enable;
    }

    // ── Owner → Server: request ownership transfer (used when handing a robot
    //    to a newly connected player mid-session). ─────────────────────────────
    [ServerRpc(RequireOwnership = false)]
    public void RequestOwnership(NetworkConnection requester)
    {
        NetworkObject.GiveOwnership(requester);
    }
}
