using UnityEngine;

[RequireComponent(typeof(Camera))]
public class TrainingCameraController : MonoBehaviour
{
    [SerializeField] private float panSpeed = 12f;
    [SerializeField] private float fastPanMultiplier = 3f;
    [SerializeField] private float zoomSpeed = 4f;
    [SerializeField] private float minOrthographicSize = 2f;
    [SerializeField] private float maxOrthographicSize = 40f;

    private Camera controlledCamera;
    private float fixedZ;

    private void Awake()
    {
        controlledCamera = GetComponent<Camera>();
        controlledCamera.orthographic = true;
        fixedZ = transform.position.z;
    }

    private void Update()
    {
        float horizontal = GetHorizontalInput();
        float vertical = GetVerticalInput();
        Vector3 movement = new Vector3(horizontal, vertical, 0f);

        if (movement.sqrMagnitude > 1f)
        {
            movement.Normalize();
        }

        float speed = Input.GetKey(KeyCode.LeftShift)
            ? panSpeed * fastPanMultiplier
            : panSpeed;

        Vector3 position = transform.position + movement * speed * Time.unscaledDeltaTime;
        position.z = fixedZ;
        transform.position = position;

        float zoomInput = Input.mouseScrollDelta.y;
        if (!Mathf.Approximately(zoomInput, 0f))
        {
            controlledCamera.orthographicSize = Mathf.Clamp(
                controlledCamera.orthographicSize - zoomInput * zoomSpeed,
                minOrthographicSize,
                maxOrthographicSize
            );
        }
    }

    private float GetHorizontalInput()
    {
        float horizontal = 0f;

        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
        {
            horizontal -= 1f;
        }

        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
        {
            horizontal += 1f;
        }

        return horizontal;
    }

    private float GetVerticalInput()
    {
        float vertical = 0f;

        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
        {
            vertical -= 1f;
        }

        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
        {
            vertical += 1f;
        }

        return vertical;
    }
}
