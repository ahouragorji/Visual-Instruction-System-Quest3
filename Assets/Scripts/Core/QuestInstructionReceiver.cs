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
    public GameObject ghostHandRotatePrefab; // <-- ADD THIS LINE
    public float ghostHandApproachDistance = 0.25f;
    public float ghostHandSurfaceOffset    = 0.05f;

    
    [Header("Move Tool Prefabs")]
    public GameObject moveSourcePrefab;
    public GameObject moveTargetPrefab;

    private GameObject _moveSourcePrefab => moveSourcePrefab;
    private GameObject _moveTargetPrefab => moveTargetPrefab;

    [Header("Chop Tool")]
    public GameObject chopLinePrefab;


    [Header("Navigation Buttons")]
    public Button nextButton;
    public Button previousButton;

    [Header("Instruction Panel")]
    public TMP_Text instructionText;
    public TMP_InputField commandInputField;
    public TMP_Text stepCounterText;
    public AudioSource audioSource;
    public AudioSource successAudio;
    

    [SerializeField] AudioClip clip;
    [SerializeField] AudioClip successClip;



    [Header("TTS agent")]
    [SerializeField] TextToSpeechAgent ttsAgent;
    [Header("Debug — Bounding Boxes")]
    public bool showDebugBoundingBoxes = false;
    public Button debugToggleButton;
    [SerializeField] private Material drawMaterial;


    public bool IsSessionActive { get; private set; } = false;
    public event Action OnRetryComplete;

    private readonly Dictionary<string, string[]> _chunkBuffers = new Dictionary<string, string[]>();
    private readonly Dictionary<int, List<GameObject>> _overlaysByStep = new Dictionary<int, List<GameObject>>();
    private readonly Dictionary<int, List<GameObject>> _bboxEdgesByStep = new Dictionary<int, List<GameObject>>();
    
    // The Tool Registry

    private InstructionResponse _currentResponse;
    private int _currentStepIndex = 0;
    private int _maxStep = 0;
    private Dictionary<string, IToolParser> _parsers = new Dictionary<string, IToolParser>();
    // step → { "source": GameObject, "target": GameObject }
    private readonly Dictionary<int, Dictionary<string, GameObject>> _moveRolesByStep 
        = new Dictionary<int, Dictionary<string, GameObject>>();
        private void Awake()
    {
        if (drawMaterial == null)
        {
            Shader fallback = Shader.Find("Sprites/Default");
            if (fallback != null) drawMaterial = new Material(fallback);
        }
        _parsers.Add("chop_line", new ChopToolParser(chopLinePrefab, arrowOffset));

        _parsers.Add("move", new MoveToolParser(moveSourcePrefab, moveTargetPrefab, 0.0f));
        // Register tools into the dictionary
        _parsers.Add("indicator_arrow", new IndicatorArrowParser(
            arrowUpPrefab, arrowDownPrefab, arrowFrontPrefab, 
            arrowBackPrefab, arrowLeftPrefab, arrowRightPrefab, arrowOffset));
        
        _parsers.Add("ghost_hand", new GhostHandParser(
            ghostHandPokePrefab,
            ghostHandGrabPrefab,
            ghostHandCleanPrefab,
            ghostHandRotatePrefab, // <-- INSERT THIS HERE
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


    private void HandleChunkedMessage(string message, bool isMerge)
{
    string[] parts = message.Split(new char[] { '|' }, 5);
    if (parts.Length < 5) return;

    string id    = parts[1];
    int    index = int.Parse(parts[2]);
    int    total = int.Parse(parts[3]);
    string data  = parts[4];

    // Use a namespaced key so INSTR and RETRY chunks don't collide
    string bufferKey = $"{(isMerge ? "R" : "I")}_{id}";

    if (!_chunkBuffers.ContainsKey(bufferKey))
        _chunkBuffers[bufferKey] = new string[total];

    _chunkBuffers[bufferKey][index] = data;

    if (Array.TrueForAll(_chunkBuffers[bufferKey], c => c != null))
    {
        string fullBase64 = string.Concat(_chunkBuffers[bufferKey]);
        _chunkBuffers.Remove(bufferKey);
        DecodeAndApply(fullBase64, isMerge);
    }
}

    private void HandleAppMessage(string message)
    {
        if (message.StartsWith("INSTR|"))
    {
        HandleChunkedMessage(message, isMerge: false);
    }
    else if (message.StartsWith("RETRY|"))
    {
        HandleChunkedMessage(message, isMerge: true);
        Debug.Log("[Quest] Received retry package");
    }
        
    }

    private void DecodeAndApply(string base64Json, bool isMerge = false)
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
         if (isMerge)
        {
            if (parsed.ar_overlays != null && parsed.ar_overlays.Length > 0)
            {
                MergeOverlays(parsed.ar_overlays);
                RefreshVisibility();
            }

            if (parsed.retry_complete)
            {
                Debug.Log("[QIR] Retry complete — all objects found.");
                OnRetryComplete?.Invoke();
            }
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

        IsSessionActive = true;
        Debug.Log("[QIR] Session started — new captures blocked.");

        // ADD THIS: Start the auto-retry loop only after the initial results have loaded
        if (sender != null && !string.IsNullOrEmpty(parsed.id))
        {
            sender.StartAutoRetryLoopForId(parsed.id);
        }
    
    }
    

   private void MergeOverlays(AROverlay[] newOverlays)
{
    foreach (var overlay in newOverlays)
    {
        int stepNum = overlay.step;

        if (!_overlaysByStep.ContainsKey(stepNum))  _overlaysByStep[stepNum]  = new List<GameObject>();
        if (!_bboxEdgesByStep.ContainsKey(stepNum)) _bboxEdgesByStep[stepNum] = new List<GameObject>();

        if (string.IsNullOrEmpty(overlay.guidance_tool) || string.IsNullOrEmpty(overlay.manipulation_tag))
            continue;

        if (!_parsers.TryGetValue(overlay.guidance_tool, out IToolParser parser))
        {
            Debug.LogWarning($"[QIR:Merge] Unrecognized guidance tool: {overlay.guidance_tool}");
            continue;
        }

        Vector3 headPos = sender != null ? sender.lastCaptureHeadPosition : Camera.main.transform.position;
        ParsedSpawnData spawnData = parser.Parse(overlay, headPos);

        if (spawnData.PrefabToSpawn == null) continue;

        // ── Instantiate ──────────────────────────────────────────────────────
        GameObject spawnedObj = Instantiate(spawnData.PrefabToSpawn, spawnData.Position, spawnData.Rotation);
        spawnedObj.transform.localScale = spawnData.PrefabToSpawn.transform.localScale * objectScale;
        spawnedObj.name = $"{overlay.guidance_tool}_Step{stepNum}_{overlay.manipulation_tag}";
        spawnedObj.SetActive(stepNum == _currentStepIndex + 1);
        _overlaysByStep[stepNum].Add(spawnedObj);

        if (overlay.bboxCorners != null && overlay.bboxCorners.Length == 8)
        {
            List<GameObject> edges = SpawnBoundingBoxEdges(spawnedObj, overlay.bboxCorners);
            _bboxEdgesByStep[stepNum].AddRange(edges);
        }

        // ── Move wiring — identical to SpawnAllOverlays, spawnedObj exists here
        if (overlay.guidance_tool == "move")
{
    string role = "";
    foreach (var p in overlay.tool_settings)
        if (p.key == "role") { role = p.value; break; }

    if (!_moveRolesByStep.ContainsKey(stepNum))
        _moveRolesByStep[stepNum] = new Dictionary<string, GameObject>();

    _moveRolesByStep[stepNum][role] = spawnedObj;

    string counterpart = (role == "source") ? "target" : "source";
    if (_moveRolesByStep[stepNum].TryGetValue(counterpart, out GameObject counterpartGo)
        && counterpartGo != null)
    {
        // Both halves now present — wire them
        GameObject sourceGo = (role == "source") ? spawnedObj : counterpartGo;
        GameObject targetGo = (role == "target") ? spawnedObj : counterpartGo;
        var connector = sourceGo.GetComponent<MoveLineConnector>();
        var anchor    = targetGo.GetComponent<MoveTargetAnchor>();
        if (connector != null && anchor != null) connector.SetTarget(anchor);
    }
    else
    {
        // Counterpart not in _moveRolesByStep — it may exist as a
        // downgraded indicator_arrow. Find and replace it.
        string arrowName = $"indicator_arrow_Step{stepNum}_";
        if (_overlaysByStep.TryGetValue(stepNum, out var stepObjects))
        {
            for (int idx = 0; idx < stepObjects.Count; idx++)
            {
                GameObject existing = stepObjects[idx];
                if (existing == null || !existing.name.StartsWith(arrowName)) continue;

                // Respawn as the correct move prefab via the move parser
                Vector3 existingPos = existing.transform.position;
                Quaternion existingRot = existing.transform.rotation;
                Destroy(existing);

                // Build a minimal counterpart overlay to re-parse position
                // We reuse the position already computed — just need the prefab
                string counterpartRole = counterpart;
                GameObject counterpartPrefab = (counterpartRole == "source")
                    ? _moveSourcePrefab
                    : _moveTargetPrefab;

                if (counterpartPrefab != null)
                {
                    GameObject rebuilt = Instantiate(counterpartPrefab, existingPos, existingRot);
                    rebuilt.transform.localScale = counterpartPrefab.transform.localScale * objectScale;
                    rebuilt.name = $"move_Step{stepNum}_{counterpartRole}_rebuilt";
                    rebuilt.SetActive(stepNum == _currentStepIndex + 1);
                    stepObjects[idx] = rebuilt;
                    _moveRolesByStep[stepNum][counterpartRole] = rebuilt;

                    // Now wire
                    GameObject sourceGo = (role == "source") ? spawnedObj : rebuilt;
                    GameObject targetGo = (role == "target") ? spawnedObj : rebuilt;
                    var connector = sourceGo.GetComponent<AnimatedCurveConnector>();
                    var anchor    = targetGo.GetComponent<MoveTargetAnchor>();
                    if (connector != null && anchor != null) connector.SetTarget(anchor);
                }
                break;
            }
        }
    }
}}}

    private void SpawnAllOverlays()
    {
        ClearAllSpawnedObjects();

        audioSource.clip = clip;
        successAudio.clip = successClip;
        audioSource.Play();

        foreach (var overlay in _currentResponse.ar_overlays)
    {
        int stepNum = overlay.step;

        if (!_overlaysByStep.ContainsKey(stepNum))  _overlaysByStep[stepNum]  = new List<GameObject>();
        if (!_bboxEdgesByStep.ContainsKey(stepNum)) _bboxEdgesByStep[stepNum] = new List<GameObject>();

        if (string.IsNullOrEmpty(overlay.guidance_tool) || string.IsNullOrEmpty(overlay.manipulation_tag))
            continue;

        if (!_parsers.TryGetValue(overlay.guidance_tool, out IToolParser parser))
        {
            Debug.LogWarning($"[QIR:Spawn] Unrecognized guidance tool: {overlay.guidance_tool}");
            continue;
        }

        Vector3 headPos = sender != null ? sender.lastCaptureHeadPosition : Camera.main.transform.position;
        ParsedSpawnData spawnData = parser.Parse(overlay, headPos);

        if (spawnData.PrefabToSpawn == null) continue;

        // ── Instantiate first ────────────────────────────────────────────────
        GameObject spawnedObj = Instantiate(spawnData.PrefabToSpawn, spawnData.Position, spawnData.Rotation);
        spawnedObj.transform.localScale = spawnData.PrefabToSpawn.transform.localScale * objectScale;
        spawnedObj.name = $"{overlay.guidance_tool}_Step{stepNum}_{overlay.manipulation_tag}";
        _overlaysByStep[stepNum].Add(spawnedObj);

        if (overlay.bboxCorners != null && overlay.bboxCorners.Length == 8)
        {
            List<GameObject> edges = SpawnBoundingBoxEdges(spawnedObj, overlay.bboxCorners);
            _bboxEdgesByStep[stepNum].AddRange(edges);
        }

        // ── Move wiring — only after spawnedObj exists ───────────────────────
        if (overlay.guidance_tool == "move")
        {
            
            string role = "";
            foreach (var p in overlay.tool_settings)
                if (p.key == "role") { role = p.value; break; }

            if (!_moveRolesByStep.ContainsKey(stepNum))
                _moveRolesByStep[stepNum] = new Dictionary<string, GameObject>();

            _moveRolesByStep[stepNum][role] = spawnedObj;

            string counterpart = (role == "source") ? "target" : "source";
            if (_moveRolesByStep[stepNum].TryGetValue(counterpart, out GameObject counterpartGo)
                && counterpartGo != null)
            {
                GameObject sourceGo = (role == "source") ? spawnedObj : counterpartGo;
                GameObject targetGo = (role == "target") ? spawnedObj : counterpartGo;

                var connector = sourceGo.GetComponent<AnimatedCurveConnector>();
                var anchor    = targetGo.GetComponent<MoveTargetAnchor>();
                if (connector != null && anchor != null) connector.SetTarget(anchor);
            }
            // else: counterpart arrives via retry — MergeOverlays handles wiring then
        }
    }}

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
 public void AbandonSession()
    {
        IsSessionActive = false;
        ClearAllSpawnedObjects();
        _currentResponse = null;
        _currentStepIndex = 0;
        _maxStep = 0;
        
        // THE FIX: Kill the retry loop when user abandons
        OnRetryComplete?.Invoke(); 
        successAudio.Play();
        Debug.Log("[QIR] Session abandoned — new captures allowed.");
    }

    public void AdvanceToNextStep()
    {
        if (_currentResponse == null || _currentStepIndex >= _maxStep - 1)
        {
            // Already on the last step — pressing Next means they're done.
            if (_currentResponse != null && _currentStepIndex >= _maxStep - 1)
            {
                IsSessionActive = false;
                
                // THE FIX: Kill the retry loop when user finishes the task
                OnRetryComplete?.Invoke(); 
                successAudio.Play();
                Debug.Log("[QIR] Session complete — new captures allowed.");
            }
            return;
        }
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
        public bool        retry_complete;
    }
}