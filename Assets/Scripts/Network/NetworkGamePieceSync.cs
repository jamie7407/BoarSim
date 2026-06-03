using FishNet.Object;
using FishNet.Connection;
using UnityEngine;
using Util;

/// <summary>
/// Server-authoritative game piece networking.
///
/// Ownership flow:
///   World  (server owns) ──► intake contact ──► robot client owns ──► outake ──► server owns
///
///   1. All pieces spawn with the server as owner.  Server physics is authoritative.
///   2. When a robot's intake trigger fires ON THE SERVER, the robot calls
///      RequestIntake() which validates the claim and calls GiveOwnership(robotConn).
///   3. The owning client attaches the piece kinematically to the correct node —
///      no simulation needed while held; NetworkTransform syncs its world position.
///   4. On outake, the client calls ReleaseToWorld() which re-parents to the field,
///      removes ownership back to the server, and re-enables physics.
///
/// Setup in Unity Editor:
///   1. Add NetworkObject to every game piece prefab.
///   2. Add NetworkTransform to each piece prefab.
///   3. Add this component to each piece prefab.
///   4. Game pieces must be registered in FishNet's NetworkObject Prefabs list
///      so the server can track and spawn them.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class NetworkGamePieceSync : NetworkBehaviour
{
    // ── Ownership guard ───────────────────────────────────────────────────────
    // Prevents two robots racing to claim the same piece.
    private bool _claimed;

    // Max pieces a robot's hopper can hold.  Checked server-side before granting.
    public int maxHopperCapacity = 5;

    // ── References ────────────────────────────────────────────────────────────
    private GamePiece  _piece;
    private Rigidbody  _rb;

    private void Awake()
    {
        _piece = GetComponent<GamePiece>();
        _rb    = GetComponent<Rigidbody>();
    }

    // ── Intake: any client (robot owner) asks the server to transfer ownership ─

    /// <summary>
    /// Called by the robot's intake trigger on the robot's owning client.
    /// The server validates capacity and uniqueness before granting.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestIntake(NetworkConnection requester, int currentHopperCount)
    {
        if (_claimed)                         return; // already taken
        if (currentHopperCount >= maxHopperCapacity) return; // hopper full
        if (_piece == null)                   return;
        if (_piece.state != GamePieceState.World) return; // already inside a robot

        _claimed = true;
        NetworkObject.GiveOwnership(requester);

        // Make kinematic on the server side immediately — the owning client
        // will position it; server just stops simulating it.
        if (_rb != null) _rb.isKinematic = true;
        _piece.state = GamePieceState.Stationary;

        // Tell all clients to stop simulating this piece (ObserversRpc).
        SetKinematicObservers(true);
    }

    // ── Outake: owning client fires the piece back into the world ─────────────

    /// <summary>
    /// Called by the robot's outake mechanism on the owning client.
    /// Velocity is the launch velocity in world space.
    /// </summary>
    [ServerRpc]
    public void ReleaseToWorld(Vector3 velocity)
    {
        _claimed = false;
        NetworkObject.RemoveOwnership(); // back to server

        // Re-enable physics on server and all clients.
        SetKinematicObservers(false);
        if (_rb != null)
        {
            _rb.isKinematic = false;
            _rb.velocity    = velocity;
        }

        if (_piece != null)
        {
            _piece.state = GamePieceState.World;
            _piece.owner = null;
            _piece.transform.SetParent(_piece.originalParent);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    [ObserversRpc(BufferLast = true)]
    private void SetKinematicObservers(bool kinematic)
    {
        if (_rb != null) _rb.isKinematic = kinematic;
    }

    // ── Owner callback: position the piece inside the robot once we have it ───
    public override void OnOwnershipClient(NetworkConnection prevOwner)
    {
        base.OnOwnershipClient(prevOwner);

        if (!IsOwner) return;

        // The owning client now controls this piece kinematically.
        // The actual node-attachment is handled by the robot's existing
        // JointController / BuildArm logic — this just ensures physics is off.
        if (_rb != null) _rb.isKinematic = true;
    }
}
