using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class MoveLineConnector : MonoBehaviour
{
    private LineRenderer _lineRenderer;
    private MoveTargetAnchor _target;

    public void SetTarget(MoveTargetAnchor target)
    {
        _target = target;
    }

    private void Awake()
    {
        _lineRenderer = GetComponent<LineRenderer>();
        InitLineRenderer();
    }

    private void OnEnable()
    {
        if (_lineRenderer == null)
            _lineRenderer = GetComponent<LineRenderer>();
        InitLineRenderer();
    }

    private void InitLineRenderer()
    {
        _lineRenderer.positionCount = 2;
        _lineRenderer.useWorldSpace = true;
        _lineRenderer.startWidth    = 0.01f;
        _lineRenderer.endWidth      = 0.01f;

        // Initialize both positions to this object's location
        // so there's never a frame with uninitialized positions
        _lineRenderer.SetPosition(0, transform.position);
        _lineRenderer.SetPosition(1, transform.position);

        if (_lineRenderer.sharedMaterial == null)
        {
            _lineRenderer.material     = new Material(Shader.Find("Sprites/Default"));
            _lineRenderer.startColor   = Color.cyan;
            _lineRenderer.endColor     = Color.cyan;
        }
    }

    private void Update()
    {
        if (_lineRenderer == null || _lineRenderer.positionCount < 2) return;

        if (_target == null)
            _target = FindObjectOfType<MoveTargetAnchor>();

        if (_target != null)
        {
            _lineRenderer.SetPosition(0, transform.position);
            _lineRenderer.SetPosition(1, _target.transform.position);
        }
    }
}