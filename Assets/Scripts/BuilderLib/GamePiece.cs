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
    // True when this ball was spawned inside a BuildNode (i.e. preloaded into the robot).
    // Used by PieceSyncManager to decide whether to include this ball in MSG_REG — preloaded
    // balls are at the same world position on both host and client so they proxy-match
    // correctly; field balls that were intaked before T=2s have mismatched positions.
    [HideInInspector] public bool isPreloaded;

    private bool hasId;

    private void Start()
    {
        hasId = false;
        if (!rb) rb = GetComponent<Rigidbody>();

        // Detect preload state before any parenting changes can occur.
        isPreloaded = GetComponentInParent<BuildNode>() != null;

        // On non-host clients, preloaded balls must be kinematic from spawn — they sit
        // at their designed hopper position, driven by joint-sync, not local physics.
        // Field balls stay dynamic until OnRegistrationReceived (T≈2s) makes them kinematic.
        var gnm = GameNetworkManager.Instance;
        if (rb != null && gnm != null && gnm.IsClient && !gnm.IsHost)
        {
            if (isPreloaded)
            {
                rb.isKinematic   = true;
                rb.interpolation = RigidbodyInterpolation.None;
            }
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
