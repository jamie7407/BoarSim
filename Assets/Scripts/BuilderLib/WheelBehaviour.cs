using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class WheelBehaviour : MonoBehaviour
{
    [HideInInspector] public float wheelDiameter;
    
    [HideInInspector] public List<Vector3> collisionPoints = new List<Vector3>();
    [HideInInspector] public List<Vector3> collisionNormals = new List<Vector3>();
    
    // Directions in local space — pure forward/backward removed (only detect walls, not ground)
    private Vector3[] directions;
    private LayerMask _groundMask;

    // Start is called before the first frame update
    private void Start()
    {
        directions = new Vector3[]
        {
            -transform.up,
            (-transform.up * 2 + transform.forward).normalized,
            (-transform.up * 2 - transform.forward).normalized,
            (-transform.up + transform.forward).normalized,
            (-transform.up - transform.forward).normalized,
            (-transform.up + transform.forward * 2).normalized,
            (-transform.up - transform.forward * 2).normalized,
        };

        // Exclude game pieces from wheel contact raycasts
        int pieceLayer = LayerMask.NameToLayer("Piece");
        _groundMask = pieceLayer >= 0 ? ~(1 << pieceLayer) : Physics.DefaultRaycastLayers;
    }

    // Fixed Update is called every Physics Tick
    private void FixedUpdate()
    {
        collisionPoints.Clear();
        collisionNormals.Clear();

        Vector3 axle = transform.right;
        float castDist = wheelDiameter / 2f;

        for (int i = 0; i < directions.Length; i++)
        {
            Vector3 worldDir = transform.TransformDirection(directions[i]);

            if (Physics.Raycast(transform.position, worldDir, out RaycastHit hit, castDist, _groundMask))
            {
                collisionPoints.Add(hit.point);
                collisionNormals.Add(hit.normal);   // world-space surface normal, used for slope grip
            }
        }
    }
}
