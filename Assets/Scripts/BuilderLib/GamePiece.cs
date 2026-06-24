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

        // All client balls are kinematic from spawn — host is authoritative for all physics.
        // Keeping them kinematic prevents the kinematic robot (moved by joint sync) from
        // pushing dynamic balls to wrong positions, which caused registration mismatches
        // and "weird" collision behaviour on the client screen.
        // Field balls are registered by proximity to their spawn positions (which match the
        // host's spawn positions for correctly-placed field pieces) and then driven by delta.
        var gnm = GameNetworkManager.Instance;
        if (rb != null && gnm != null && gnm.IsClient && !gnm.IsHost)
        {
            rb.isKinematic = true;
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
