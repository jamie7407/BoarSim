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

    private bool hasId;

    private void Start()
    {
        hasId = false;
        if (!rb) rb = GetComponent<Rigidbody>();
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
