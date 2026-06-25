using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Alpha.Parsers;
using Meta.XR.BuildingBlocks.AIBlocks;

// -------------------------------------------------------------------------
// Main Receiver Script
// -------------------------------------------------------------------------
public class QuestInstructionReceiver : MonoBehaviour
{
    [Header("References")]
    public QuestPassthroughSender sender;

    [Header("Arrow Prefabs")]
    public GameObject arrowUpPrefab;
    public GameObject arrowDownPrefab;
    public GameObject arrowFrontPrefab;
    public GameObject arrowBackPrefab;
    public GameObject arrowLeftPrefab;
    public GameObject arrowRightPrefab;

    public float arrowOffset = 0.3f;
    public float objectScale = 0.15f;


    [Header("Ghost Hand Prefabs")]
    public GameObject ghostHandPokePrefab;
    public GameObject ghostHandGrabPrefab;
    public GameObject ghostHandCleanPrefab;
    public float ghostHandApproachDistance = 0.25f;
    public float ghostHandSurfaceOffset    = 0.05f;


    [Header("Navigation Buttons")]
    public Button nextButton;
    public Button previousButton;

    [Header("Instruction Panel")]
    public TMP_Text instructionText;
    public TMP_InputField commandInputField;
    public TMP_Text stepCounterText;
    public AudioSource audioSource;
    [SerializeField] AudioClip clip;


    [Header("TTS agent")]
    [SerializeField] TextToSpeechAgent ttsAgent;
    [Header("Debug — Bounding Boxes")]
    public bool showDebugBoundingBoxes = false;
    public Button debugToggleButton;
    [SerializeField] private Material drawMaterial;


    
    private readonly Dictionary<string, string[]> _chunkBuffers = new Dictionary<string, string[]>();
    private readonly Dictionary<int, List<GameObject>> _overlaysByStep = new Dictionary<int, List<GameObject>>();
    private readonly Dictionary<int, List<GameObject>> _bboxEdgesByStep = new Dictionary<int, List<GameObject>>();
    
    // The Tool Registry

    private InstructionResponse _currentResponse;
    private int _currentStepIndex = 0;
    private int _maxStep = 0;
    private Dictionary<string, IToolParser> _parsers = new Dictionary<string, IToolParser>();

    private void Awake()
    {
        if (drawMaterial == null)
        {
            Shader fallback = Shader.Find("Sprites/Default");
            if (fallback != null) drawMaterial = new Material(fallback);
        }

        // Register tools into the dictionary
        _parsers.Add("indicator_arrow", new IndicatorArrowParser(
            arrowUpPrefab, arrowDownPrefab, arrowFrontPrefab, 
            arrowBackPrefab, arrowLeftPrefab, arrowRightPrefab, arrowOffset));
        
        _parsers.Add("ghost_hand", new GhostHandParser(
            ghostHandPokePrefab,
            ghostHandGrabPrefab,
            ghostHandCleanPrefab,
            ghostHandApproachDistance,
            ghostHandSurfaceOffset));
            
        // Future tools go here:
        // _parsers.Add("ghost_hand", new GhostHandParser(...));
        // _parsers.Add("timer_clock", new TimerClockParser(...));
    }

    private void OnEnable()
    {
        if (sender != null) sender.OnAppMessageReceived += HandleAppMessage;
        if (nextButton != null) nextButton.onClick.AddListener(AdvanceToNextStep);
        if (previousButton != null) previousButton.onClick.AddListener(ReturnToPreviousStep);
        if (debugToggleButton != null) debugToggleButton.onClick.AddListener(ToggleDebugBoundingBoxes);
    }

    private void OnDisable()
    {
        if (sender != null) sender.OnAppMessageReceived -= HandleAppMessage;
        if (nextButton != null) nextButton.onClick.RemoveListener(AdvanceToNextStep);
        if (previousButton != null) previousButton.onClick.RemoveListener(ReturnToPreviousStep);
        if (debugToggleButton != null) debugToggleButton.onClick.RemoveListener(ToggleDebugBoundingBoxes);
    }

    private void HandleAppMessage(string message)
    {
        if (!message.StartsWith("INSTR|")) return;

        string[] parts = message.Split(new char[] { '|' }, 5);
        if (parts.Length < 5) return;

        string id = parts[1];
        int index = int.Parse(parts[2]);
        int total = int.Parse(parts[3]);
        string data = parts[4];

        if (!_chunkBuffers.ContainsKey(id)) _chunkBuffers[id] = new string[total];
        _chunkBuffers[id][index] = data;

        if (Array.TrueForAll(_chunkBuffers[id], c => c != null))
        {
            string fullBase64 = string.Concat(_chunkBuffers[id]);
            _chunkBuffers.Remove(id);
            DecodeAndApply(fullBase64);
        }
    }

    private void DecodeAndApply(string base64Json)
    {
        string json = null;
        InstructionResponse parsed = null;


        
        try
        {
            byte[] bytes = Convert.FromBase64String(base64Json);
            json = System.Text.Encoding.UTF8.GetString(bytes);
            Debug.LogWarning($"[QIR:Decode] Successfully reassembled and decoded JSON: {json}");
            parsed = JsonUtility.FromJson<InstructionResponse>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[QIR:Decode] Failed: {e.Message}");
            return;
        }

        if (parsed == null || parsed.ar_overlays == null || parsed.ar_overlays.Length == 0)
        {
            Debug.LogWarning("[QIR:Decode] Empty or invalid payload.");
            return;
        }

        var uniqueSteps = new List<int>();
        foreach (var p in parsed.ar_overlays)
            if (!uniqueSteps.Contains(p.step)) uniqueSteps.Add(p.step);
        uniqueSteps.Sort();

        var stepRemap = new Dictionary<int, int>();
        for (int i = 0; i < uniqueSteps.Count; i++) stepRemap[uniqueSteps[i]] = i + 1;

        foreach (var p in parsed.ar_overlays) p.step = stepRemap[p.step];

        _currentResponse = parsed;
        _currentStepIndex = 0;
        _maxStep = uniqueSteps.Count;

        SpawnAllOverlays();
        RefreshVisibility();
        ShowCurrentStepText();
    }

    private void SpawnAllOverlays()
    {
        ClearAllSpawnedObjects();

        audioSource.clip = clip;
        audioSource.Play();

        foreach (var overlay in _currentResponse.ar_overlays)
        {

            int stepNum = overlay.step;

            if (!_overlaysByStep.ContainsKey(stepNum)) _overlaysByStep[stepNum] = new List<GameObject>();
            if (!_bboxEdgesByStep.ContainsKey(stepNum)) _bboxEdgesByStep[stepNum] = new List<GameObject>();

            // Text-only placement skip
            if (string.IsNullOrEmpty(overlay.guidance_tool) || string.IsNullOrEmpty(overlay.manipulation_tag))
                continue;

            // Route to correct parser based on guidance_tool
            if (_parsers.TryGetValue(overlay.guidance_tool, out IToolParser parser))
            {
                Vector3 headPos = sender != null ? sender.lastCaptureHeadPosition : Camera.main.transform.position;
                ParsedSpawnData spawnData = parser.Parse(overlay, headPos);

                if (spawnData.PrefabToSpawn != null)
                {
                    GameObject spawnedObj = Instantiate(spawnData.PrefabToSpawn, spawnData.Position, spawnData.Rotation);
                    Vector3 originalScale = spawnData.PrefabToSpawn.transform.localScale;

                    spawnedObj.transform.localScale = originalScale * objectScale; // Default scale logic, adjust per parser if needed
                    spawnedObj.name = $"{overlay.guidance_tool}_Step{stepNum}_{overlay.manipulation_tag}";

                    _overlaysByStep[stepNum].Add(spawnedObj);

                    if (overlay.bboxCorners != null && overlay.bboxCorners.Length == 8)
                    {
                        List<GameObject> edges = SpawnBoundingBoxEdges(spawnedObj, overlay.bboxCorners);
                        _bboxEdgesByStep[stepNum].AddRange(edges);
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[QIR:Spawn] Unrecognized guidance tool: {overlay.guidance_tool}");
            }
        }
    }

    private void ClearAllSpawnedObjects()
    {
        foreach (var list in _overlaysByStep.Values)
            foreach (var go in list)
                if (go != null) Destroy(go);

        _overlaysByStep.Clear();
        _bboxEdgesByStep.Clear();
    }

    private void RefreshVisibility()
    {
        int activeStep = _currentStepIndex + 1;
        List<string> visibleObjectNames = new List<string>();

        foreach (var kvp in _overlaysByStep)
        {
            bool active = (kvp.Key == activeStep);
            foreach (var obj in kvp.Value)
                if (obj != null) {
                    
                    obj.SetActive(active);
                    if (active)
                    {
                        visibleObjectNames.Add(obj.name);
                    }
        }
        }

        foreach (var kvp in _bboxEdgesByStep)
        {
            bool edgeActive = (kvp.Key == activeStep) && showDebugBoundingBoxes;
            foreach (var edge in kvp.Value)
                if (edge != null) edge.SetActive(edgeActive);
        }

        if (previousButton != null) previousButton.interactable = (_currentStepIndex > 0);
        if (nextButton != null) nextButton.interactable = (_currentStepIndex < _maxStep - 1);
        string visibleList = visibleObjectNames.Count > 0 ? string.Join(", ", visibleObjectNames) : "None";
        Debug.Log($"[QIR:Visibility] Step {activeStep} active. Visible overlays: {visibleList}");
    }

    private void ShowCurrentStepText()
    {
        commandInputField.text = sender.PasspreCommand();

        if (_currentResponse == null) return;
        int activeStep = _currentStepIndex + 1;
        string text = null;
        foreach (var p in _currentResponse.ar_overlays)
            if (p.step == activeStep) { text = p.instruction; break; }

        if (instructionText != null) instructionText.text = text ?? "(no instruction for this step)";
        if (stepCounterText != null) stepCounterText.text = $"Step {activeStep} / {_maxStep}";
        if (instructionText.text!=null)
        {
        ttsAgent.SpeakText(instructionText.text);
        }

    }

    public void AdvanceToNextStep()
    {
        if (_currentResponse == null || _currentStepIndex >= _maxStep - 1) return;
        _currentStepIndex++;
        RefreshVisibility();
        ShowCurrentStepText();
    }

    public void ReturnToPreviousStep()
    {
        if (_currentResponse == null || _currentStepIndex <= 0) return;
        _currentStepIndex--;
        RefreshVisibility();
        ShowCurrentStepText();
    }

    public void ToggleDebugBoundingBoxes()
    {
        showDebugBoundingBoxes = !showDebugBoundingBoxes;
        int activeStep = _currentStepIndex + 1;
        if (_bboxEdgesByStep.TryGetValue(activeStep, out var edges))
            foreach (var edge in edges)
                if (edge != null) edge.SetActive(showDebugBoundingBoxes);
    }

    private List<GameObject> SpawnBoundingBoxEdges(GameObject parent, Corner3D[] corners)
    {
        var edges = new List<GameObject>();
        if (corners == null || corners.Length != 8) return edges;

        Vector3[] pts = new Vector3[8];
        for (int i = 0; i < 8; i++) pts[i] = corners[i].ToVector3();

        Color c = Color.cyan;
        edges.Add(CreateEdge(parent, pts[0], pts[1], c)); edges.Add(CreateEdge(parent, pts[1], pts[2], c));
        edges.Add(CreateEdge(parent, pts[2], pts[3], c)); edges.Add(CreateEdge(parent, pts[3], pts[0], c));
        edges.Add(CreateEdge(parent, pts[4], pts[5], c)); edges.Add(CreateEdge(parent, pts[5], pts[6], c));
        edges.Add(CreateEdge(parent, pts[6], pts[7], c)); edges.Add(CreateEdge(parent, pts[7], pts[4], c));
        edges.Add(CreateEdge(parent, pts[0], pts[4], c)); edges.Add(CreateEdge(parent, pts[1], pts[5], c));
        edges.Add(CreateEdge(parent, pts[2], pts[6], c)); edges.Add(CreateEdge(parent, pts[3], pts[7], c));

        foreach (var e in edges) e.SetActive(false);
        return edges;
    }

    private GameObject CreateEdge(GameObject parent, Vector3 start, Vector3 end, Color color)
    {
        var obj = new GameObject("BBoxEdge");
        obj.transform.SetParent(parent.transform);
        var lr = obj.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);
        lr.startWidth = 0.005f;
        lr.endWidth = 0.005f;
        lr.material = drawMaterial;
        lr.startColor = color;
        lr.endColor = color;
        lr.useWorldSpace = true;
        return obj;
    }

    // -------------------------------------------------------------------------
    // Serialization types
    // -------------------------------------------------------------------------
    [Serializable]
    public class FeatureParameter
    {
        public string key;
        public string value;
    }

    [Serializable]
    public class AROverlay
    {
        public int step;
        public string instruction;
        public string guidance_tool;
        public string manipulation_tag;
        public FeatureParameter[] tool_settings;
        public float worldX, worldY, worldZ;
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
        public string id;
        public AROverlay[] ar_overlays;
    }
}