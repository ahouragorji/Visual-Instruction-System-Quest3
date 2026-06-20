using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Listens for instruction/placement payloads relayed back from the PC over
/// the same DataChannel used for image capture. Reassembles chunked "INSTR|..."
/// messages, then spawns one numbered ball per detected object at its
/// world-space point.
///
/// Step navigation: only the balls for the *current* step are visible at a
/// time. Previous/Next buttons (or AdvanceToNextStep / ReturnToPreviousStep
/// calls) slide the visible window forward and backward through the steps.
///
/// Bounding boxes: drawn only when showDebugBoundingBoxes is true. Toggle it
/// in the Inspector at edit-time or at runtime via the debug toggle button.
/// Bounding box GameObjects are pooled under each ball so toggling visibility
/// is a simple SetActive call, not a re-spawn.
/// </summary>
public class QuestInstructionReceiver : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector fields
    // -------------------------------------------------------------------------

    [Header("References")]
    [Tooltip("The QuestPassthroughSender in the scene — we subscribe to its OnAppMessageReceived event.")]
    public QuestPassthroughSender sender;

    [Header("Ball Prefab")]
    [Tooltip("A simple sphere prefab. A TextMesh or Canvas child showing the step number is recommended.")]
    public GameObject ballPrefab;

    [Tooltip("Scale applied to spawned balls.")]
    public float ballScale = 0.12f;

    [Header("Navigation Buttons")]
    [Tooltip("Button that calls AdvanceToNextStep. Wire this in the Inspector.")]
    public Button nextButton;

    [Tooltip("Button that calls ReturnToPreviousStep. Wire this in the Inspector.")]
    public Button previousButton;

    [Header("Instruction Panel")]
    [Tooltip("World-space Canvas Text component that displays the current step's instruction.")]
    public TMP_Text instructionText;

    [Tooltip("Optional label showing e.g. 'Step 2 / 4'. Leave null to skip.")]
    public TMP_Text stepCounterText;

    [Header("Debug — Bounding Boxes")]
    [Tooltip("When true, 3-D wireframe boxes are drawn around each detected object. " +
             "Disable in production builds to save draw calls.")]
    public bool showDebugBoundingBoxes = false;

    [Tooltip("Toggle button shown during play that flips showDebugBoundingBoxes at runtime. " +
             "Assign a UI Button; leave null to skip.")]
    public Button debugToggleButton;

    [Tooltip("Unlit line material used to render the bounding-box edges. " +
             "If left null, falls back to Sprites/Default.")]
    [SerializeField] private Material drawMaterial;

    // -------------------------------------------------------------------------
    // Private state
    // -------------------------------------------------------------------------

    private readonly Dictionary<string, string[]> _chunkBuffers = new Dictionary<string, string[]>();

    // All spawned ball GameObjects, keyed by step number.
    private readonly Dictionary<int, List<GameObject>> _ballsByStep = new Dictionary<int, List<GameObject>>();

    // All bounding-box edge GameObjects, keyed by step number.
    // Kept separate so we can toggle them independently of the balls.
    private readonly Dictionary<int, List<GameObject>> _bboxEdgesByStep = new Dictionary<int, List<GameObject>>();

    private InstructionResponse _currentResponse;
    private int _currentStepIndex = 0; // 0-based; displayed step = _currentStepIndex + 1
    private int _maxStep = 0;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        // Guarantee we always have *some* line material.
        if (drawMaterial == null)
        {
            Shader fallback = Shader.Find("Sprites/Default");
            if (fallback != null)
                drawMaterial = new Material(fallback);
            else
                Debug.LogWarning("[QuestInstructionReceiver] 'drawMaterial' is null and Sprites/Default was not found. " +
                                 "Bounding-box edges may be invisible.");
        }
    }

    private void OnEnable()
    {
        if (sender != null)
            sender.OnAppMessageReceived += HandleAppMessage;
        else
            Debug.LogError("[QuestInstructionReceiver] 'sender' reference not set in Inspector.");

        if (nextButton != null)
            nextButton.onClick.AddListener(AdvanceToNextStep);

        if (previousButton != null)
            previousButton.onClick.AddListener(ReturnToPreviousStep);

        if (debugToggleButton != null)
            debugToggleButton.onClick.AddListener(ToggleDebugBoundingBoxes);
    }

    private void OnDisable()
    {
        if (sender != null)
            sender.OnAppMessageReceived -= HandleAppMessage;

        if (nextButton != null)
            nextButton.onClick.RemoveListener(AdvanceToNextStep);

        if (previousButton != null)
            previousButton.onClick.RemoveListener(ReturnToPreviousStep);

        if (debugToggleButton != null)
            debugToggleButton.onClick.RemoveListener(ToggleDebugBoundingBoxes);
    }

    // -------------------------------------------------------------------------
    // Message handling
    // -------------------------------------------------------------------------

    private void HandleAppMessage(string message)
    {
        if (!message.StartsWith("INSTR|")) return;

        string[] parts = message.Split(new char[] { '|' }, 5);
        if (parts.Length < 5)
        {
            Debug.LogError($"[Quest] Malformed instruction chunk: expected 5 parts, got {parts.Length}");
            return;
        }

        string id    = parts[1];
        int    index = int.Parse(parts[2]);
        int    total = int.Parse(parts[3]);
        string data  = parts[4];

        if (!_chunkBuffers.ContainsKey(id))
        {
            _chunkBuffers[id] = new string[total];
            Debug.Log($"[Quest] Started receiving instruction set '{id}': expecting {total} chunks.");
        }

        _chunkBuffers[id][index] = data;

        if (Array.TrueForAll(_chunkBuffers[id], chunk => chunk != null))
        {
            string fullBase64 = string.Concat(_chunkBuffers[id]);
            _chunkBuffers.Remove(id);
            Debug.Log($"[Quest] Instruction set '{id}' complete ({fullBase64.Length} base64 chars). Decoding.");
            DecodeAndApply(fullBase64);
        }
    }

    private void DecodeAndApply(string base64Json)
    {
        InstructionResponse parsed;
        try
        {
            byte[] jsonBytes = Convert.FromBase64String(base64Json);
            string json      = System.Text.Encoding.UTF8.GetString(jsonBytes);
            parsed           = JsonUtility.FromJson<InstructionResponse>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Quest] Failed to decode instruction payload: {e.Message}");
            return;
        }

        if (parsed == null || parsed.placements == null || parsed.placements.Length == 0)
        {
            Debug.LogWarning("[Quest] Decoded instruction payload had no placements.");
            return;
        }

        _currentResponse  = parsed;
        _currentStepIndex = 0;

        // Cache max step once here so navigation never needs to re-scan.
        _maxStep = 0;
        foreach (var p in _currentResponse.placements)
            if (p.step > _maxStep) _maxStep = p.step;

        SpawnAllBalls();
        RefreshVisibility();
        ShowCurrentStepText();
    }

    // -------------------------------------------------------------------------
    // Ball spawning — all steps, hidden by default except the first
    // -------------------------------------------------------------------------

    private void SpawnAllBalls()
    {
        ClearAllSpawnedObjects();

        if (ballPrefab == null)
        {
            Debug.LogError("[Quest] No ballPrefab assigned — cannot spawn instruction balls.");
            return;
        }

        foreach (var placement in _currentResponse.placements)
        {
            int stepNum = placement.step;

            // Ensure dictionary entries exist for this step.
            if (!_ballsByStep.ContainsKey(stepNum))
                _ballsByStep[stepNum] = new List<GameObject>();
            if (!_bboxEdgesByStep.ContainsKey(stepNum))
                _bboxEdgesByStep[stepNum] = new List<GameObject>();

            // Spawn the ball.
            Vector3 worldPos = new Vector3(placement.worldX, placement.worldY, placement.worldZ);
            GameObject ball  = Instantiate(ballPrefab, worldPos, Quaternion.identity);
            ball.transform.localScale = Vector3.one * ballScale;
            ball.name = $"InstructionBall_Step{stepNum}_{placement.label}";

            // Update any embedded step-number labels on the prefab.
            Text legacyLabel = ball.GetComponentInChildren<Text>();
            if (legacyLabel != null) legacyLabel.text = stepNum.ToString();

            TextMesh meshLabel = ball.GetComponentInChildren<TextMesh>();
            if (meshLabel != null) meshLabel.text = stepNum.ToString();

            _ballsByStep[stepNum].Add(ball);

            // Spawn bounding-box edges as children of the ball so they move
            // with it. Store references so we can toggle them separately.
            if (placement.bboxCorners != null && placement.bboxCorners.Length == 8)
            {
                List<GameObject> edges = SpawnBoundingBoxEdges(ball, placement.bboxCorners);
                _bboxEdgesByStep[stepNum].AddRange(edges);
            }
        }

        Debug.Log($"[Quest] Spawned balls for {_ballsByStep.Count} steps " +
                  $"({_currentResponse.placements.Length} total placements).");
    }

    private void ClearAllSpawnedObjects()
    {
        foreach (var list in _ballsByStep.Values)
            foreach (var go in list)
                if (go != null) Destroy(go);

        // Edges are children of the balls, so destroying the balls also
        // destroys the edges — but clear the dictionaries explicitly to
        // avoid stale references if ClearAllSpawnedObjects is called twice.
        _ballsByStep.Clear();
        _bboxEdgesByStep.Clear();
    }

    // -------------------------------------------------------------------------
    // Step visibility — show only balls (and optionally boxes) for current step
    // -------------------------------------------------------------------------

    private void RefreshVisibility()
    {
        int activeStep = _currentStepIndex + 1; // 1-based step number

        foreach (var kvp in _ballsByStep)
        {
            bool isActiveStep = (kvp.Key == activeStep);
            foreach (var ball in kvp.Value)
                if (ball != null) ball.SetActive(isActiveStep);
        }

        // Bounding-box edges are visible only when the step is active AND
        // the debug flag is on.
        foreach (var kvp in _bboxEdgesByStep)
        {
            bool edgesVisible = (kvp.Key == activeStep) && showDebugBoundingBoxes;
            foreach (var edge in kvp.Value)
                if (edge != null) edge.SetActive(edgesVisible);
        }

        RefreshNavButtonState();
    }

    // Dim/enable nav buttons at the first and last steps so the user gets
    // clear feedback that they've reached an endpoint.
    private void RefreshNavButtonState()
    {
        if (previousButton != null)
            previousButton.interactable = (_currentStepIndex > 0);

        if (nextButton != null)
            nextButton.interactable = (_currentStepIndex < _maxStep - 1);
    }

    // -------------------------------------------------------------------------
    // Instruction text panel
    // -------------------------------------------------------------------------

    private void ShowCurrentStepText()
    {
        if (_currentResponse == null) return;

        int activeStep = _currentStepIndex + 1;

        // All placements on the same step share the same instruction string,
        // so the first match is enough.
        string text = null;
        foreach (var p in _currentResponse.placements)
        {
            if (p.step == activeStep) { text = p.instruction; break; }
        }

        if (instructionText != null)
            instructionText.text = text ?? "(no instruction for this step)";

        if (stepCounterText != null)
            stepCounterText.text = $"Step {activeStep} / {_maxStep}";
    }

    // -------------------------------------------------------------------------
    // Public navigation API (also wired to buttons in OnEnable)
    // -------------------------------------------------------------------------

    /// <summary>Advance to the next instruction step.</summary>
    public void AdvanceToNextStep()
    {
        if (_currentResponse == null || _currentStepIndex >= _maxStep - 1) return;
        _currentStepIndex++;
        RefreshVisibility();
        ShowCurrentStepText();
    }

    /// <summary>Return to the previous instruction step.</summary>
    public void ReturnToPreviousStep()
    {
        if (_currentResponse == null || _currentStepIndex <= 0) return;
        _currentStepIndex--;
        RefreshVisibility();
        ShowCurrentStepText();
    }

    /// <summary>
    /// Toggle debug bounding-box visibility at runtime. Wire this to a UI
    /// button or call it programmatically from a debug menu. Has no effect in
    /// production when showDebugBoundingBoxes starts as false and you never
    /// call this.
    /// </summary>
    public void ToggleDebugBoundingBoxes()
    {
        showDebugBoundingBoxes = !showDebugBoundingBoxes;
        // Only touch the *active* step — inactive steps stay dormant regardless.
        int activeStep = _currentStepIndex + 1;
        if (_bboxEdgesByStep.TryGetValue(activeStep, out var edges))
            foreach (var edge in edges)
                if (edge != null) edge.SetActive(showDebugBoundingBoxes);

        Debug.Log($"[Quest] Debug bounding boxes: {(showDebugBoundingBoxes ? "ON" : "OFF")}");
    }

    // -------------------------------------------------------------------------
    // Bounding-box edge spawning (debug only)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates 12 LineRenderer edge objects as children of <paramref name="parent"/>
    /// and returns them. They start inactive; <see cref="RefreshVisibility"/>
    /// enables them only when both the step is active and the debug flag is on.
    /// </summary>
    private List<GameObject> SpawnBoundingBoxEdges(GameObject parent, Corner3D[] corners)
    {
        var spawnedEdges = new List<GameObject>();

        if (corners == null || corners.Length != 8)
        {
            Debug.LogWarning("[Quest] SpawnBoundingBoxEdges: expected 8 corners, got " +
                             (corners?.Length.ToString() ?? "null"));
            return spawnedEdges;
        }

        Vector3[] pts = new Vector3[8];
        for (int i = 0; i < 8; i++) pts[i] = corners[i].ToVector3();

        Color boxColor = Color.cyan;

        // Front face
        spawnedEdges.Add(CreateEdge(parent, pts[0], pts[1], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[1], pts[2], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[2], pts[3], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[3], pts[0], boxColor));

        // Back face
        spawnedEdges.Add(CreateEdge(parent, pts[4], pts[5], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[5], pts[6], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[6], pts[7], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[7], pts[4], boxColor));

        // Depth edges connecting front to back
        spawnedEdges.Add(CreateEdge(parent, pts[0], pts[4], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[1], pts[5], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[2], pts[6], boxColor));
        spawnedEdges.Add(CreateEdge(parent, pts[3], pts[7], boxColor));

        // Start hidden; RefreshVisibility will activate them if appropriate.
        foreach (var edge in spawnedEdges)
            edge.SetActive(false);

        return spawnedEdges;
    }

    private GameObject CreateEdge(GameObject parent, Vector3 start, Vector3 end, Color color)
    {
        var edgeObj = new GameObject("BBoxEdge");
        edgeObj.transform.SetParent(parent.transform);

        var lr = edgeObj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);
        lr.startWidth  = 0.005f;
        lr.endWidth    = 0.005f;
        lr.material    = drawMaterial;
        lr.startColor  = color;
        lr.endColor    = color;
        lr.useWorldSpace = true;

        return edgeObj;
    }

    // -------------------------------------------------------------------------
    // Serialization types
    // -------------------------------------------------------------------------

    [Serializable]
    private class InstructionPlacement
    {
        public int       step;
        public string    instruction;
        public string    label;
        public float     worldX, worldY, worldZ;
        public Corner3D[] bboxCorners;
    }

    [Serializable]
    public class Corner3D
    {
        public float x, y, z;
        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    private class InstructionResponse
    {
        public string               id;
        public InstructionPlacement[] placements;
    }
}