using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public enum InteractionPlaneMode
{
    /// <summary>Plane facing the camera at planeDistance along its forward axis.</summary>
    CameraFacing = 0,

    /// <summary>World XZ plane through the origin.</summary>
    GroundXZ = 1,
}

/// <summary>
/// Converts touches (device) or the mouse (editor/standalone) into world-space
/// TouchForce entries. SimulationWorld calls Sample once per frame and uploads
/// the result to the GPU TouchBuffer.
/// </summary>
public sealed class InputRouter : MonoBehaviour
{
    public const int MaxTouches = 8;

    [SerializeField] private Camera targetCamera;
    [SerializeField] private InteractionPlaneMode planeMode = InteractionPlaneMode.CameraFacing;
    [SerializeField, Min(0.01f)] private float planeDistance = 6f;
    [SerializeField, Min(0f)] private float touchRadius = 1f;
    [SerializeField] private float touchStrength = 10f;

    private readonly Vector2[] screenPositions = new Vector2[MaxTouches];
    private readonly Vector3[] previousWorldPositions = new Vector3[MaxTouches];
    private readonly bool[] activeThisFrame = new bool[MaxTouches];
    private readonly bool[] activeLastFrame = new bool[MaxTouches];

    /// <summary>Fills output with active pointers, returns their count.</summary>
    public int Sample(TouchForce[] output)
    {
        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null)
        {
            return 0;
        }

        for (int i = 0; i < MaxTouches; i++)
        {
            activeThisFrame[i] = false;
        }

        CollectPointers();

        int count = 0;
        for (int slot = 0; slot < MaxTouches && count < output.Length; slot++)
        {
            if (!activeThisFrame[slot])
            {
                activeLastFrame[slot] = false;
                continue;
            }

            Vector3 world = ProjectToPlane(cam, screenPositions[slot]);
            Vector3 delta = activeLastFrame[slot] ? world - previousWorldPositions[slot] : Vector3.zero;
            previousWorldPositions[slot] = world;
            activeLastFrame[slot] = true;

            output[count] = new TouchForce
            {
                Position = world,
                Delta = delta,
                Radius = touchRadius,
                Strength = touchStrength,
            };
            count++;
        }

        return count;
    }

    private void CollectPointers()
    {
#if ENABLE_INPUT_SYSTEM
        bool anyTouch = false;
        Touchscreen touchscreen = Touchscreen.current;
        if (touchscreen != null)
        {
            int slot = 0;
            foreach (UnityEngine.InputSystem.Controls.TouchControl touch in touchscreen.touches)
            {
                if (slot >= MaxTouches)
                {
                    break;
                }

                if (touch.press.isPressed)
                {
                    activeThisFrame[slot] = true;
                    screenPositions[slot] = touch.position.ReadValue();
                    anyTouch = true;
                }

                slot++;
            }
        }

        if (!anyTouch)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed)
            {
                activeThisFrame[0] = true;
                screenPositions[0] = mouse.position.ReadValue();
            }
        }
#else
        if (Input.touchCount > 0)
        {
            int touches = Mathf.Min(Input.touchCount, MaxTouches);
            for (int i = 0; i < touches; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    continue;
                }

                activeThisFrame[i] = true;
                screenPositions[i] = touch.position;
            }
        }
        else if (Input.GetMouseButton(0))
        {
            activeThisFrame[0] = true;
            screenPositions[0] = Input.mousePosition;
        }
#endif
    }

    private Vector3 ProjectToPlane(Camera cam, Vector2 screenPosition)
    {
        Ray ray = cam.ScreenPointToRay(screenPosition);
        Plane plane = planeMode == InteractionPlaneMode.GroundXZ
            ? new Plane(Vector3.up, Vector3.zero)
            : new Plane(-cam.transform.forward, cam.transform.position + cam.transform.forward * planeDistance);

        return plane.Raycast(ray, out float enter) ? ray.GetPoint(enter) : ray.GetPoint(planeDistance);
    }
}
