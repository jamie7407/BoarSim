using UnityEngine;
using Util;

public class GamePiece : MonoBehaviour
{
    public PieceNames pieceType;
    public Transform owner;
    public Rigidbody rb;
    public GamePieceState state;
    public GameObject colliderParent;

    [HideInInspector] public Vector3 startPosition;
    [HideInInspector] public Transform originalParent;
    [HideInInspector] public float startingDistance;
    // Robot slot (0-3) that last fired this piece into the world; -1 = unknown/spawned
    [HideInInspector] public int lastScoredBySlot = -1;

    private bool hasId;

    private void Start()
    {
        hasId = false;
        if (!rb) rb = GetComponent<Rigidbody>();

        // On non-host clients, ALL balls must be kinematic from the moment they spawn.
        // Host-authoritative physics drives every ball via delta messages; a dynamic client
        // ball would fall under gravity and be depenetrated downward through the robot's
        // kinematic colliders — appearing to "fall through the hopper" — in the window
        // before the first delta or MSG_ATTACH arrives.
        // Field balls don't need gravity settling on the client because they never use
        // local physics; they track host positions via MSG_DELTA.
        // OnPieceDetachReceived explicitly makes balls dynamic for flight after a shot.
        var gnm = GameNetworkManager.Instance;
        if (rb != null && gnm != null && gnm.IsClient && !gnm.IsHost)
        {
            rb.isKinematic   = true;
            rb.interpolation = RigidbodyInterpolation.None;
        }
    }

    private void Update()
    {
        if (hasId) return;

        if (!rb) rb = GetComponent<Rigidbody>();

        var core = Utils.FindParentObjectComponent<LoadMatch>(gameObject);
        if (!core) return;

        var fieldHolder = core.GetFieldHolder();
        if (!fieldHolder || fieldHolder.transform.childCount == 0) return;

        originalParent = fieldHolder.transform.GetChild(0);
        hasId = true;

        // Configure Rigidbody for performance — many pieces on field get expensive fast.
        // Aggressive sleep threshold: balls that stop moving are removed from simulation.
        // Discrete CCD: fastest collision mode, fine for slow-moving game pieces.
        if (rb != null)
        {
            rb.sleepThreshold          = 0.5f;
            rb.collisionDetectionMode  = CollisionDetectionMode.Discrete;
            rb.interpolation           = RigidbodyInterpolation.None;
        }

        // Stop calling Update() — it was only needed for one-time initialization.
        // With 30+ pieces on field, eliminating these dispatch calls adds up.
        enabled = false;
    }
}
