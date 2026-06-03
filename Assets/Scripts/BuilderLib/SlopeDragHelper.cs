using System.Collections;
using UnityEngine;

/// <summary>
/// Drops Rigidbody drag to near-zero whenever the robot is airborne or on a slope.
///
/// Why: rb.drag = 0.5 removes ~40% of speed per second.  On flat ground the motor
/// force compensates, so drag is invisible.  But during a bump crossing the wheels
/// can be fully or partially off the ground — motor forces aren't applied
/// (WheelBehaviour has no contacts) yet drag keeps eating velocity every tick.
/// That's the dominant cause of speed loss over bumps that material changes can't fix.
///
/// On flat ground (all wheels grounded, normals all vertical) drag is unchanged,
/// so normal deceleration / braking behaviour is preserved.
/// </summary>
public class SlopeDragHelper : MonoBehaviour
{
    private Rigidbody _rb;
    private float _defaultDrag;
    private WheelBehaviour[] _wheels;

    private IEnumerator Start()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb == null) yield break;
        _defaultDrag = _rb.drag;

        // Wait for ModuleBehaviour.Start() to finish adding WheelBehaviour components
        yield return null;
        yield return null;
        yield return null;

        _wheels = GetComponentsInChildren<WheelBehaviour>(true);
    }

    private void FixedUpdate()
    {
        if (_rb == null || _wheels == null || _wheels.Length == 0) return;

        int totalContacts = 0;
        float minNormalY   = 1f;

        foreach (var w in _wheels)
        {
            totalContacts += w.collisionPoints.Count;
            var normals = w.collisionNormals;
            for (int i = 0; i < normals.Count; i++)
                minNormalY = Mathf.Min(minNormalY, normals[i].y);
        }

        bool airborne = totalContacts == 0;
        bool onSlope  = minNormalY < 0.95f;   // any wheel on a surface > ~13° tilt

        // Near-zero drag preserves momentum; motor braking handles intentional stops.
        _rb.drag = (airborne || onSlope) ? 0.02f : _defaultDrag;
    }
}
