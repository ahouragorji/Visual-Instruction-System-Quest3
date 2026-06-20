using UnityEngine;

// Reminds you to add a collider so the Physics raycast actually hits the Canvas!
[RequireComponent(typeof(Collider))]
public class canvasDragger : MonoBehaviour
{
    [Header("Anchors & Input")]
    [SerializeField] private Transform handAnchor; // Assign Left or Right Hand Anchor
    [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.RTouch; 

    [Header("Drag Settings")]
    [SerializeField] private float smoothSpeed = 10f;
    [SerializeField] private float rotateSpeed = 90f;
    [SerializeField] private float depthSpeed = 2f;
    [SerializeField] bool autoFaceUser = true; 

    private bool _isDragging = false;
    private float _dragDistance;
    private Vector3 _grabOffset;
    private Camera _mainCamera;

    private void Start() => _mainCamera = Camera.main;

    private void Update()
    {
        // Get inputs based on the assigned controller
        bool triggerPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, controller);
        bool triggerHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller);

        if (!_isDragging && triggerPressed)
            TryStartDrag();

        if (_isDragging)
        {
            if (!triggerHeld)
            {
                _isDragging = false;
                return;
            }

            HandleThumbstick();
            DragCanvas();
        }
    }

    private void TryStartDrag()
    {
        Ray ray = new Ray(handAnchor.position, handAnchor.forward);
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            if (hit.transform.IsChildOf(transform) || hit.transform == transform)
            {
                _isDragging = true;
                _dragDistance = hit.distance;
                _grabOffset = transform.position - hit.point;
                Debug.Log("[CanvasMover] Drag started");
            }
        }
    }

    private void DragCanvas()
    {
        Ray ray = new Ray(handAnchor.position, handAnchor.forward);
        Vector3 targetPosition = ray.GetPoint(_dragDistance) + _grabOffset;
        transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * smoothSpeed);

        if (autoFaceUser)
        {
            Vector3 dirToUser = _mainCamera.transform.position - transform.position;
            
            // Lock the Y axis so the canvas stays perfectly level/upright
            dirToUser.y = 0; 
            
            if (dirToUser != Vector3.zero) 
            {
                Quaternion targetRotation = Quaternion.LookRotation(-dirToUser.normalized);
                transform.rotation = Quaternion.Lerp(transform.rotation, targetRotation, Time.deltaTime * smoothSpeed);
            }
        }
    }

    private void HandleThumbstick()
    {
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, controller);

        if (!autoFaceUser && Mathf.Abs(stick.x) > 0.1f)
        {
            Vector3 center = GetWorldCenter();
            transform.RotateAround(center, Vector3.up, stick.x * rotateSpeed * Time.deltaTime);
        }

        if (Mathf.Abs(stick.y) > 0.1f)
        {
            _dragDistance += stick.y * depthSpeed * Time.deltaTime;
            _dragDistance = Mathf.Max(0.2f, _dragDistance);
        }
    }

    private Vector3 GetWorldCenter()
    {
        var col = GetComponent<Collider>();
        if (col != null) return col.bounds.center;
        return transform.position;
    }
}