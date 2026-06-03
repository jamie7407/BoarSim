using System.Collections;
using UnityEngine;

/// <summary>
/// Stamps every collider on the robot with a zero-friction PhysicMaterial.
/// All traction comes from the custom wheel force model in ModuleBehaviour —
/// PhysX friction on the chassis is what pins the robot against bump geometry.
/// Waits one frame so ModuleBehaviour.Start() can add WheelBehaviour first.
/// </summary>
public class RobotSlideSetup : MonoBehaviour
{
    private static PhysicMaterial _slideMat;

    private IEnumerator Start()
    {
        // Wait several frames — BuildFrame adds colliders procedurally and they may not
        // all exist on the first frame after the robot is spawned.
        yield return null;
        yield return null;
        yield return null;

        if (_slideMat == null)
            _slideMat = new PhysicMaterial("RobotSlide")
            {
                dynamicFriction = 0.1f,
                staticFriction  = 0.1f,
                frictionCombine = PhysicMaterialCombine.Minimum,
                bounciness      = 0f,
                bounceCombine   = PhysicMaterialCombine.Minimum,
            };

        foreach (var col in GetComponentsInChildren<Collider>(true))
            col.sharedMaterial = _slideMat;

        Destroy(this);
    }
}
