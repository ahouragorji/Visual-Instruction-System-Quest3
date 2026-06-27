using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class AnimatedCurveConnector : MonoBehaviour
{
    [Header("Curve Settings")]
    public float curveHeight = 0.25f; 
    public int resolution = 20;

    [Header("Animation Settings")]
    public float animationSpeed = -2f; 

    private LineRenderer _lineRenderer;
    private MoveTargetAnchor _target; 

    // 1. This allows the Receiver to explicitly pass the correct partner
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
        if (_lineRenderer == null) _lineRenderer = GetComponent<LineRenderer>();
        InitLineRenderer();
    }

    private void InitLineRenderer()
    {
        _lineRenderer.useWorldSpace = true;
        _lineRenderer.positionCount = resolution;
        _lineRenderer.startWidth = 0.01f;
        _lineRenderer.endWidth = 0.01f;
        
        // Pre-fill the array to prevent 0-frame draw errors
        for(int i = 0; i < resolution; i++) 
        {
            _lineRenderer.SetPosition(i, transform.position);
        }
    }

    private void Update()
    {
        if (_lineRenderer == null || _lineRenderer.positionCount < resolution) return;

        // 2. THE FIX: We removed FindObjectOfType! 
        // Now, if it hasn't been explicitly handed a target, it does nothing.
        if (_target == null) return;

        DrawCurve();
        AnimateLine();
    }

    private void DrawCurve()
    {
        Vector3 start = transform.position;
        Vector3 end = _target.transform.position;
        
        Vector3 mid = Vector3.Lerp(start, end, 0.5f);
        mid.y += curveHeight;

        for (int i = 0; i < resolution; i++)
        {
            float t = i / (float)(resolution - 1);
            Vector3 pointOnCurve = CalculateBezierPoint(t, start, mid, end);
            _lineRenderer.SetPosition(i, pointOnCurve);
        }
    }

    private Vector3 CalculateBezierPoint(float t, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        float u = 1 - t;
        float tt = t * t;
        float uu = u * u;
        Vector3 p = uu * p0; 
        p += 2 * u * t * p1; 
        p += tt * p2; 
        return p;
    }

    private void AnimateLine()
    {
        if (_lineRenderer.material != null)
        {
            float offset = Time.time * animationSpeed;
            _lineRenderer.material.mainTextureOffset = new Vector2(offset, 0);
        }
    }
}