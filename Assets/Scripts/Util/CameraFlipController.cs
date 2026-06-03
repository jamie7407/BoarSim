using UnityEngine;
using UnityEngine.InputSystem;

public class CameraFlipController : MonoBehaviour
{
    private CameraHoldAngle _holdAngle;
    private Transform _flipTarget;
    private SwerveController _swerveController;
    private PlayerInput _playerInput;

    private void Start()
    {
        // CameraHoldAngle may be on the root or a child — search the whole prefab
        _holdAngle = GetComponentInChildren<CameraHoldAngle>(true);

        // Flip the object that actually carries the spatial offset.
        // ThirdPerson: CameraHoldAngle is on the root (offset is on Camera child,
        //   so rotating the root orbits the Camera around the robot).
        // FirstPerson: no CameraHoldAngle, so fall back to the root — same result.
        _flipTarget = _holdAngle != null ? _holdAngle.transform : transform;

        _swerveController = GetComponentInParent<SwerveController>();
        _playerInput    = GetComponentInParent<PlayerInput>();
    }

    private void Update()
    {
        if (_playerInput == null) return;

        // Poll the device that is actually paired to this robot's PlayerInput.
        // Using the InputUser avoids InputAction callback routing issues with
        // PlayerInput in InvokeUnityEvents mode.
        if (!_playerInput.user.valid) return;

        foreach (var device in _playerInput.user.pairedDevices)
        {
            if (device is Gamepad gp && gp.rightStickButton.wasPressedThisFrame)
            {
                Flip();
                return;
            }
            if (device is Keyboard kb && kb.rightShiftKey.wasPressedThisFrame)
            {
                Flip();
                return;
            }
        }
    }

    private void Flip()
    {
        // Rotating the flip target 180° around its parent's Y axis moves the Camera
        // child (which carries the actual offset) to the opposite side of the robot.
        Vector3 p = _flipTarget.localPosition;
        _flipTarget.localPosition = new Vector3(-p.x, p.y, -p.z);
        _flipTarget.localRotation = Quaternion.Euler(0f, 180f, 0f) * _flipTarget.localRotation;

        // CameraHoldAngle must re-lock to the new world rotation or it will snap back
        _holdAngle?.RefreshTargetRotation();

        if (_swerveController != null)
            _swerveController.reversed = !_swerveController.reversed;
    }
}
