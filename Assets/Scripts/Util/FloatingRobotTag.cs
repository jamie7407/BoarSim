using UnityEngine;
using UnityEngine.Rendering;

// A world-space label that hovers above a robot, billboards toward whichever
// camera is rendering, and HIDES itself from its own robot's camera (so P1
// doesn't see "P1" on their own screen, but everyone else does).
//
// Works in URP splitscreen via RenderPipelineManager.beginCameraRendering:
// once per camera, right before that camera draws, we orient + toggle
// visibility for that specific camera.
public class FloatingRobotTag : MonoBehaviour
{
    private Transform target;
    private float height = 1.5f;
    private Renderer[] renderers;

    public void Follow(Transform robot, float heightAboveRobot)
    {
        target = robot;
        height = heightAboveRobot;
    }

    private void OnEnable() => RenderPipelineManager.beginCameraRendering += OnBeginCamera;
    private void OnDisable() => RenderPipelineManager.beginCameraRendering -= OnBeginCamera;

    private void LateUpdate()
    {
        if (target == null)        // robot destroyed (ResetField) -> clean up
        {
            Destroy(gameObject);
            return;
        }

        transform.position = target.position + Vector3.up * height;
    }

    private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam == null || target == null) return;

        if (renderers == null)
            renderers = GetComponentsInChildren<Renderer>(true);

        // The camera that follows this robot is parented under it, so if the
        // rendering camera lives inside this robot's hierarchy, it's "our" camera.
        bool isOwnCamera = cam.transform.IsChildOf(target);

        SetVisible(!isOwnCamera);

        if (!isOwnCamera)
            transform.rotation = Quaternion.LookRotation(
                transform.position - cam.transform.position,
                cam.transform.up);
    }

    private void SetVisible(bool visible)
    {
        if (renderers == null) return;
        foreach (var r in renderers)
            if (r != null) r.enabled = visible;
    }
}