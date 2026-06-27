using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using NativeWebSocket;
using Unity.WebRTC;
using Meta.XR.MRUtilityKit;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using UnityEngine.Rendering;
using Unity.Collections;
using System.Reflection;
using UnityEngine.UI;
using TMPro;
using System.Text.RegularExpressions;

public class QuestPassthroughSender : MonoBehaviour
{
    [Header("Signaling")]
    public string signalingUrl = "ws://192.168.100.90:3000";

    [Header("Capture Setup")]
    public PassthroughCameraAccess cameraAccess;

    [Header("Environment Depth")]
    [SerializeField] EnvironmentDepthManager environmentDepthManager;
    
    [Header("Camera Rig")]
    
    [SerializeField] OVRCameraRig rig;

    [Header("References")]
    [SerializeField] QuestInstructionReceiver instructionReceiver;


    [Header("Retry Settings")]
    [SerializeField] float retryIntervalSeconds = 10f;

    private Coroutine _retryLoopCoroutine = null;

    [Header("Command Input Field")]
    [SerializeField] TMP_InputField commandInputField;

    [Tooltip("Drag the DepthToFloatMat here from your Project window")]
    public Material depthToFloatMaterial;

    [Header("Trigger Settings")]
    [Tooltip("Analog axis threshold for the index trigger (0.0-1.0). 0.7 = firm squeeze without needing full click.")]
    [Range(0.1f, 1.0f)]
    public float triggerThreshold = 0.7f;

    [Header("Capture State")]
    public Vector3 lastCaptureHeadPosition; // <-- ADD THIS
    private string _lastCaptureId = null;
    public bool triggerJustPressed= false;
    [Header("Pipeline Settings")]
    [SerializeField] Toggle useYoloeToggle;        // assign in Inspector
    [SerializeField] Toggle useDinoToggle;        // assign in Inspector

    [SerializeField] Toggle taskToggle;
    [SerializeField] Toggle queryToggle;

    [SerializeField] AudioSource CaptureClip;

    private WebSocket _ws;
    private RTCPeerConnection _pc;
    private RTCDataChannel _dataChannel;
    private bool _dataChannelReady = false;
    private bool _capturing = false;
    private bool _triggerWasDown = false;
    private string preCommand = "";
    // ROOT CAUSE FIX: CHUNK_SIZE was 16000, which is right at (or over, once you
    // add the "IMG|<timestamp>|<index>|<total>|" prefix) the safe usable max
    // message size most WebRTC/SCTP data channel implementations support
    // (commonly ~16KB, sometimes less after channel/DTLS overhead). When a
    // Send() call exceeds that limit, many backends silently drop the message
    // rather than throwing — which is exactly why the Quest logged "fully sent"
    // with no errors, while the PC only ever completed small early captures and
    // every larger capture (especially ones with depth attached) sat forever
    // incomplete in _chunkBuffers with nothing logged.
    //
    // Dropping this well under the limit leaves headroom for the prefix and any
    // transport overhead.

    public event Action<string> OnAppMessageReceived;
    private const int CHUNK_SIZE = 12000;
    private readonly Queue<string> _msgQueue = new Queue<string>();
    private bool _hasRemoteDescription = false;
    private List<SignalingMessage> _pendingIceCandidates = new List<SignalingMessage>();
    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

   private void Start()
    {
        StartCoroutine(WebRTC.Update());
        ConnectSignaling();
    }

    private void OnEnable()
    {
        if (instructionReceiver != null)
            instructionReceiver.OnRetryComplete += StopRetryLoop;
    }

    private void OnDisable()
    {
        if (instructionReceiver != null)
            instructionReceiver.OnRetryComplete -= StopRetryLoop;
    }
    

    
    private async void ConnectSignaling()
    {
        _ws = new WebSocket(signalingUrl);

        _ws.OnOpen += () => {
            Debug.Log("[Quest] Connected to signaling. Registering...");
            _ws.SendText(JsonUtility.ToJson(new SignalingMessage { type = "register", role = "quest" }));
        };
        _ws.OnMessage += (bytes) => {
            string raw = System.Text.Encoding.UTF8.GetString(bytes);
            lock (_msgQueue) { _msgQueue.Enqueue(raw); }
        };
        _ws.OnError   += (e)    => Debug.LogError($"[Quest] WS error: {e}");
        _ws.OnClose   += (code) => Debug.Log($"[Quest] WS closed: {code}");

        await _ws.Connect();
    }

    public void ToggleTrigger()
    {
        CaptureClip.Play();

        StartCoroutine(ToggleDelayed());
    }

    private IEnumerator ToggleDelayed()
    {
        yield return new WaitForSeconds(2f);
        triggerJustPressed = true;
    }


    private void StopRetryLoop()
        {
            if (_retryLoopCoroutine != null)
            {
                StopCoroutine(_retryLoopCoroutine);
                _retryLoopCoroutine = null;
                Debug.Log("[Quest] Retry loop stopped.");
            }
        }


  private void Update()
    {
        _ws?.DispatchMessageQueue();

        while (_msgQueue.Count > 0)
        {
            string raw;
            lock (_msgQueue) { raw = _msgQueue.Dequeue(); }
            HandleSignalingMessage(raw);
        }

        // Use Button.One for the 'A' button, or Button.PrimaryHandTrigger for the Grip.
        // Button.Start is the Left Menu button.
        if(OVRInput.GetDown(OVRInput.Button.One)) triggerJustPressed = true;

        bool isChannelOpen = _dataChannel != null && _dataChannel.ReadyState == RTCDataChannelState.Open;

        bool sessionActive = instructionReceiver != null && instructionReceiver.IsSessionActive;


        if (triggerJustPressed && isChannelOpen && !_capturing && !sessionActive)
        {
           
            preCommand = commandInputField.text;

           commandInputField.text = "Capturing environment...";
            Debug.Log($"[Quest] Trigger pressed. Starting capture.");
            triggerJustPressed = false;
            StartCoroutine(CaptureImages());
        }

        else if (triggerJustPressed && sessionActive)
        {
            triggerJustPressed = false;
            commandInputField.text = "Finish current steps first.";
            Debug.LogWarning("[Quest] Trigger ignored — instruction session still active.");
        }
        bool retryPressed = OVRInput.GetDown(OVRInput.Button.Three);

        if (retryPressed && isChannelOpen && !_capturing)
        {
            Debug.Log($"[Quest] Retry pressed. Starting capture.");
            if (string.IsNullOrEmpty(_lastCaptureId))
            {
                Debug.LogWarning("[Quest] Cannot retry: No previous capture ID found. Take a normal capture first.");
                commandInputField.text = "Take a normal capture first!";
            }
            else
            {
                Debug.Log($"[Quest] Retry pressed. Starting background capture for '{_lastCaptureId}'.");
                StartCoroutine(CaptureRetryImage(_lastCaptureId));
            }
        }

        else if (triggerJustPressed && !isChannelOpen)
        {
            string stateMsg = _dataChannel != null ? _dataChannel.ReadyState.ToString() : "Null";
            triggerJustPressed = false;
            Debug.LogWarning($"[Quest] Trigger ignored. DataChannel state is: {stateMsg}. Wait for connection.");
        }
        else if (triggerJustPressed && _capturing)
        {
            triggerJustPressed  =false;
           commandInputField.text = "Already asked something, please wait...";
            Debug.LogWarning("[Quest] Trigger ignored. A capture is already in progress (_capturing=true).");
        }
    }

    // -------------------------------------------------------------------------
    // Signaling
    // --------------- ----------------------------------------------------------

    public string PasspreCommand()
    {
        return preCommand;
    }
   private void HandleSignalingMessage(string raw)
    {
        if (raw.StartsWith("DC|"))
        {
            OnAppMessageReceived?.Invoke(raw.Substring(3));
            return;
        }

        var msg = JsonUtility.FromJson<SignalingMessage>(raw);
        if (msg == null) return;
        
        switch (msg.type)
        {
            case "ready":
                Debug.Log("[Quest] Both peers ready. Creating offer.");
                StartCoroutine(CreateOffer());
                break;
            case "answer":
                Debug.Log("[Quest] Received answer from PC.");
                StartCoroutine(SetRemoteDescription(msg.sdp, RTCSdpType.Answer));
                break;
            case "ice":
                if (!string.IsNullOrEmpty(msg.candidate))
                {
                    // FIX: Queue candidates if remote description isn't set yet
                    if (!_hasRemoteDescription) 
                    {
                        _pendingIceCandidates.Add(msg);
                    }
                    else 
                    {
                        StartCoroutine(AddIceCandidate(msg.candidate, msg.sdpMid, msg.sdpMLineIndex));
                    }
                }
                break;
        }

    }

    // -------------------------------------------------------------------------
    // WebRTC handshake
    // -------------------------------------------------------------------------

    private IEnumerator CreateOffer()
    {

        if (_pc != null)
        {
            Debug.Log("[Quest] Cleaning up old peer connection before creating new offer.");
            _dataChannel?.Close();
            _dataChannel?.Dispose();
            _dataChannel = null;
            _pc.Close();
            _pc.Dispose();
            _pc = null;
        }

        var config = new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };
        _pc = new RTCPeerConnection(ref config);

        _pc.OnIceCandidate = candidate => {
            _ws.SendText(JsonUtility.ToJson(new SignalingMessage
            {
                type = "ice", candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid, sdpMLineIndex = candidate.SdpMLineIndex ?? 0
            }));
        };
        _pc.OnConnectionStateChange = state => Debug.Log($"[Quest] Peer state: {state}");

        _dataChannel = _pc.CreateDataChannel("ImageChannel");
        _dataChannel.OnOpen  = () => { Debug.Log("[Quest] DataChannel OPEN."); _dataChannelReady = true; };
        _dataChannel.OnClose = () => { Debug.LogWarning("[Quest] DataChannel closed."); _dataChannelReady = false; };
        _dataChannel.OnError = e  => { Debug.LogError($"[Quest] DataChannel error: {e}"); _dataChannelReady = false; };


        _dataChannel.OnMessage = bytes => {
            string msg = System.Text.Encoding.UTF8.GetString(bytes);
            lock (_msgQueue) { _msgQueue.Enqueue("DC|" + msg); }
        };

        var offerOp = _pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError) { Debug.LogError("[Quest] CreateOffer: " + offerOp.Error.message); yield break; }

        var localDesc = offerOp.Desc;
        yield return _pc.SetLocalDescription(ref localDesc);
        _ws.SendText(JsonUtility.ToJson(new SignalingMessage { type = "offer", sdp = localDesc.sdp }));
        Debug.Log("[Quest] Offer sent.");
    }

    private IEnumerator SetRemoteDescription(string sdp, RTCSdpType sdpType)
    {
        var desc = new RTCSessionDescription { type = sdpType, sdp = sdp };
        var op = _pc.SetRemoteDescription(ref desc);
        yield return op;

        if (op.IsError)
        {
            Debug.LogError($"[Quest] SetRemoteDescription Error: {op.Error.message}");
            yield break;
        }

        // FIX: Mark as ready and flush the queue
        _hasRemoteDescription = true;
        foreach (var c in _pendingIceCandidates)
        {
            StartCoroutine(AddIceCandidate(c.candidate, c.sdpMid, c.sdpMLineIndex));
        }
        _pendingIceCandidates.Clear();
    }

    private IEnumerator AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
    {
        _pc.AddIceCandidate(new RTCIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate, sdpMid = sdpMid, sdpMLineIndex = sdpMLineIndex
        }));
        yield return null;
    }

    // -------------------------------------------------------------------------
    // Reconnect
    // -------------------------------------------------------------------------

    public void UpdateSignalingAddressAndReconnect(string newAddress)
    {
        signalingUrl = newAddress;
        StartCoroutine(RestartConnectionRoutine());
    }

    private IEnumerator RestartConnectionRoutine()
    {
        Debug.Log("[Quest] Restarting connection...");
        _dataChannelReady = false;
        _capturing        = false;
        _triggerWasDown   = false;

        _hasRemoteDescription = false;
        _pendingIceCandidates.Clear();

        _dataChannel?.Close();
        _dataChannel?.Dispose();
        _dataChannel = null;

        _pc?.Close();
        _pc?.Dispose();
        _pc = null;

        _dataChannel?.Close();
        _pc?.Close();
        if (_ws != null && _ws.State != NativeWebSocket.WebSocketState.Closed)
            _ = _ws.Close();

        yield return new WaitForSeconds(0.5f);
        _msgQueue.Clear();
        Debug.Log($"[Quest] Reconnecting to: {signalingUrl}");

        ConnectSignaling();
    }

    // -------------------------------------------------------------------------
    // capture
    // -------------------------------------------------------------------------


private IEnumerator CaptureImages()
{
    _capturing = true;
    string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
    _lastCaptureId = timestamp;
    // ── 1. EXTRACT DEPTH IMMEDIATELY (While the global texture is still alive) ──
    float depth_fx = 0, depth_fy = 0, depth_cx = 0, depth_cy = 0;
    float depthNearZ = 0.1f, depthFarZ = 20.0f;

    if (Camera.main != null)
    {
        lastCaptureHeadPosition = Camera.main.transform.position;
    }
    
    Vector3 depthPos = Vector3.zero;
    Quaternion depthRot = Quaternion.identity;
    Texture depthTex = Shader.GetGlobalTexture("_PreprocessedEnvironmentDepthTexture");

    try
    {
        FieldInfo frameDescField = typeof(EnvironmentDepthManager).GetField("frameDescriptors", BindingFlags.NonPublic | BindingFlags.Instance);
        if (frameDescField != null)
        {
            Array descriptorsArray = (Array)frameDescField.GetValue(environmentDepthManager);
            if (descriptorsArray != null && descriptorsArray.Length > 0)
            {
                object frameDesc = descriptorsArray.GetValue(0);
                Type descType = frameDesc.GetType();

                depthPos = (Vector3)descType.GetField("createPoseLocation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                depthRot = (Quaternion)descType.GetField("createPoseRotation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);

                float fovLeft  = (float)descType.GetField("fovLeftAngleTangent",  BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                float fovRight = (float)descType.GetField("fovRightAngleTangent", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                float fovTop   = (float)descType.GetField("fovTopAngleTangent",   BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                float fovDown  = (float)descType.GetField("fovDownAngleTangent",  BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);

                //added these two
                depthNearZ  = (float)descType.GetField("nearZ",  BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                depthFarZ  = (float)descType.GetField("farZ",  BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                
                if (depthTex != null)
                {
                    depth_fx = depthTex.width / (fovLeft + fovRight);
                    depth_fy = depthTex.height / (fovTop + fovDown);
                    depth_cx = depth_fx * fovLeft;
                    depth_cy = depth_fy * fovTop;
                }
            }
        }
    }

    catch (Exception e) { Debug.LogError($"[Quest] Reflection failed: {e.Message}"); }

    // =========================================================================
    // THE FIX: Convert Depth Tracking Space to Unity World Space
    // =========================================================================
   
    Transform trackingSpace = rig != null ? rig.trackingSpace : null;

    // if (trackingSpace != null)
    // {
    //     // Transform the local tracking space position/rotation into world space
    //     depthPos = trackingSpace.TransformPoint(depthPos);
    //     depthRot = trackingSpace.rotation * depthRot;
    // }
    // else
    // {
    //     Debug.LogWarning("[Quest] OVRCameraRig trackingSpace not found. Depth poses may be misaligned.");
    // }

    
    RenderTexture cleanDepthRt = null;
    if (depthTex != null && depthToFloatMaterial != null)
    {

        //passing to the material
        depthToFloatMaterial.SetFloat("_DepthNear", depthNearZ);
        depthToFloatMaterial.SetFloat("_DepthFar", depthFarZ);
        
        cleanDepthRt = RenderTexture.GetTemporary(depthTex.width, depthTex.height, 0, RenderTextureFormat.RFloat);
        Graphics.Blit(depthTex, cleanDepthRt, depthToFloatMaterial);
    }

    // ── 2. WAIT FOR END OF FRAME (Required for RGB Camera) ──
    yield return new WaitForEndOfFrame();

    // ── 3. LOCK IN CAMERA POSES AND RGB (Zero spatial desync) ──
    Texture camTex = cameraAccess.GetTexture();
    Pose cameraPose = cameraAccess.GetCameraPose();
    PassthroughCameraAccess.CameraIntrinsics intrinsics = cameraAccess.Intrinsics;
    

    Transform tSpace = rig != null ? rig.trackingSpace : null;
    if (tSpace != null)
    {
        // Convert Passthrough (RGB) camera pose to World Space
        Vector3 worldRgbPos = tSpace.TransformPoint(cameraPose.position);
        Quaternion worldRgbRot = tSpace.rotation * cameraPose.rotation;
        cameraPose = new Pose(worldRgbPos, worldRgbRot);

        // Convert Depth camera pose to World Space
        depthPos = tSpace.TransformPoint(depthPos);
        depthRot = tSpace.rotation * depthRot;
    }
    else
    {
        Debug.LogWarning("[Quest] OVRCameraRig trackingSpace not found. Poses are not in World Space.");
    }

    RenderTexture cleanRgbRt = null;
    if (camTex != null)
    {
        cleanRgbRt = RenderTexture.GetTemporary(camTex.width, camTex.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(camTex, cleanRgbRt);
    }

    // ── 4. RUN GPU READBACKS SIMULTANEOUSLY ──
    string depthBase64 = "";
    if (cleanDepthRt != null)
    {
        var depthReq = AsyncGPUReadback.Request(cleanDepthRt, 0, TextureFormat.RFloat);
        yield return new WaitUntil(() => depthReq.done);
        if (!depthReq.hasError)
        {
            NativeArray<byte> rawDepthBytes = depthReq.GetData<byte>();
            depthBase64 = Convert.ToBase64String(rawDepthBytes.ToArray());
        }
        RenderTexture.ReleaseTemporary(cleanDepthRt);
    }

    byte[] imageJpg = new byte[0];
    if (cleanRgbRt != null)
    {
        var rgbReq = AsyncGPUReadback.Request(cleanRgbRt, 0, TextureFormat.RGBA32);
        yield return new WaitUntil(() => rgbReq.done);
        if (!rgbReq.hasError)
        {
            NativeArray<byte> rawRgbBytes = rgbReq.GetData<byte>();
            int expectedBytes = cleanRgbRt.width * cleanRgbRt.height * 4;
            if (rawRgbBytes.Length == expectedBytes)
            {
                Texture2D snap = new Texture2D(cleanRgbRt.width, cleanRgbRt.height, TextureFormat.RGBA32, false);
                snap.LoadRawTextureData(rawRgbBytes.ToArray());
                snap.Apply();
                imageJpg = snap.EncodeToJPG(60);
                Destroy(snap);
            }
        }
        RenderTexture.ReleaseTemporary(cleanRgbRt);
    }

    if (imageJpg.Length == 0)
    {
        Debug.LogError("[Quest] Failed to encode RGB image. Aborting.");
        _capturing = false;
        yield break;
    }

        if (queryToggle.isOn)
        {
            Debug.Log("query toggle is on");
        }

    // ── 5. BUILD PACKAGE AND SEND ──
    var package = new SnapshotMeta
    {
        timestamp = timestamp,
        fileName = $"Snap_{timestamp}.jpg",
        cameraIndex = 0,

        posX = cameraPose.position.x, posY = cameraPose.position.y, posZ = cameraPose.position.z,
        rotX = cameraPose.rotation.x, rotY = cameraPose.rotation.y, rotZ = cameraPose.rotation.z, rotW = cameraPose.rotation.w,

        imageWidth = cameraAccess.CurrentResolution.x, imageHeight = cameraAccess.CurrentResolution.y,
        fx = intrinsics.FocalLength.x, fy = intrinsics.FocalLength.y, cx = intrinsics.PrincipalPoint.x, cy = cameraAccess.CurrentResolution.y - intrinsics.PrincipalPoint.y,
        distortionParams = new float[0],

        depth_posX = depthPos.x, depth_posY = depthPos.y, depth_posZ = depthPos.z,
        depth_rotX = depthRot.x, depth_rotY = depthRot.y, depth_rotZ = depthRot.z, depth_rotW = depthRot.w,

        depth_fx = depth_fx, depth_fy = depth_fy, depth_cx = depth_cx, depth_cy = depth_cy,
        depthWidth = depthTex != null ? depthTex.width : 0,
        depthHeight = depthTex != null ? depthTex.height : 0,
        depthNearZ = depthNearZ, depthFarZ = depthFarZ,

        command = preCommand,
        useYoloe  = useYoloeToggle != null && useYoloeToggle.isOn,
        intent = (taskToggle != null && taskToggle.isOn) ? "task" : "query",
        imageRGB = Convert.ToBase64String(imageJpg),
        imageDepth = depthBase64

        
    };

    commandInputField.text = "Asking Alpha, please wait ...";
    string json = JsonUtility.ToJson(package);
    byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(json);
    string base64 = Convert.ToBase64String(utf8);

    int total = Mathf.CeilToInt((float)base64.Length / CHUNK_SIZE);
    
    for (int i = 0; i < total; i++)
    {
        if (_dataChannel == null || _dataChannel.ReadyState != RTCDataChannelState.Open) {
            _capturing = false; yield break;
        }
        while (_dataChannel.BufferedAmount > 65536) yield return null;

        int start = i * CHUNK_SIZE;
        int length = Mathf.Min(CHUNK_SIZE, base64.Length - start);
        string msg = $"IMG|{timestamp}|{i}|{total}|{base64.Substring(start, length)}";

        try { _dataChannel.Send(msg); }
        catch (Exception e) { _capturing = false; yield break; }
        yield return null;
    }

    Debug.Log($"[Quest] Capture {timestamp} fully sent ({total} chunks).");
    // Cancel any previous loop in case user fired a second capture
    // StopRetryLoop();

    // if (!string.IsNullOrEmpty(_lastCaptureId))
    // {
    //     _retryLoopCoroutine = StartCoroutine(AutoRetryLoop(_lastCaptureId));
    //     Debug.Log($"[Quest] Auto-retry loop started for '{_lastCaptureId}' every {retryIntervalSeconds}s.");
    // }
    _capturing = false;
}
public void StartAutoRetryLoopForId(string captureId)
    {
        StopRetryLoop(); // Cancel any existing loops just in case
        
        if (!string.IsNullOrEmpty(captureId))
        {
            _lastCaptureId = captureId;
            _retryLoopCoroutine = StartCoroutine(AutoRetryLoop(captureId));
            Debug.Log($"[Quest] Auto-retry loop started for '{captureId}' every {retryIntervalSeconds}s.");
        }
    }
private IEnumerator AutoRetryLoop(string captureId)
{
    while (true)
    {
        yield return new WaitForSeconds(retryIntervalSeconds);

        // Stop if a newer capture has superseded this one
        if (_lastCaptureId != captureId)
        {
            Debug.Log($"[Quest] Retry loop for '{captureId}' superseded by newer capture. Stopping.");
            yield break;
        }

        // Stop if still mid-capture (e.g. user triggered a manual retry at the same time)
        if (_capturing)
        {
            Debug.Log("[Quest] Retry skipped — capture already in progress.");
            continue;
        }
        if (_dataChannel.BufferedAmount > 0)
        {
            Debug.LogWarning($"[Quest] Skipping retry: Network is still busy sending data ({_dataChannel.BufferedAmount} bytes buffered).");
            continue;
        }
        
        bool isChannelOpen = _dataChannel != null && _dataChannel.ReadyState == RTCDataChannelState.Open;
        if (!isChannelOpen)
        {
            Debug.LogWarning("[Quest] Retry skipped — data channel not open.");
            continue;
        }

        Debug.Log($"[Quest] Auto-retry firing for '{captureId}'.");
        yield return StartCoroutine(CaptureRetryImage(captureId));
        // Loop continues — the coroutine will be stopped externally by
        // StopRetryLoop() when OnRetryComplete fires from the receiver.
    }
}
    
    
    /// <summary>
    /// Captures a fresh RGB and Depth frame specifically for the retry loop.
    /// Runs silently in the background and uses the existing captureId.
    /// </summary>
    public IEnumerator CaptureRetryImage(string originalCaptureId)
    {
        // Prevent overlapping captures
        if (_capturing) yield break;
        _capturing = true;

        Debug.Log($"[Quest] Taking background retry capture for '{originalCaptureId}'...");

        // ── 1. EXTRACT DEPTH IMMEDIATELY ──
        float depth_fx = 0, depth_fy = 0, depth_cx = 0, depth_cy = 0;
        float depthNearZ = 0.1f, depthFarZ = 20.0f;
        Vector3 depthPos = Vector3.zero;
        Quaternion depthRot = Quaternion.identity;
        Texture depthTex = Shader.GetGlobalTexture("_PreprocessedEnvironmentDepthTexture");

        try
        {
            FieldInfo frameDescField = typeof(EnvironmentDepthManager).GetField("frameDescriptors", BindingFlags.NonPublic | BindingFlags.Instance);
            if (frameDescField != null)
            {
                Array descriptorsArray = (Array)frameDescField.GetValue(environmentDepthManager);
                if (descriptorsArray != null && descriptorsArray.Length > 0)
                {
                    object frameDesc = descriptorsArray.GetValue(0);
                    Type descType = frameDesc.GetType();

                    depthPos = (Vector3)descType.GetField("createPoseLocation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                    depthRot = (Quaternion)descType.GetField("createPoseRotation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);

                    float fovLeft  = (float)descType.GetField("fovLeftAngleTangent",  BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                    float fovRight = (float)descType.GetField("fovRightAngleTangent", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                    float fovTop   = (float)descType.GetField("fovTopAngleTangent",   BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                    float fovDown  = (float)descType.GetField("fovDownAngleTangent",  BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);

                    depthNearZ  = (float)descType.GetField("nearZ",  BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                    depthFarZ   = (float)descType.GetField("farZ",   BindingFlags.NonPublic | BindingFlags.Instance).GetValue(frameDesc);
                    
                    if (depthTex != null)
                    {
                        depth_fx = depthTex.width / (fovLeft + fovRight);
                        depth_fy = depthTex.height / (fovTop + fovDown);
                        depth_cx = depth_fx * fovLeft;
                        depth_cy = depth_fy * fovTop;
                    }
                }
            }
        }
        catch (Exception e) { Debug.LogError($"[Quest/Retry] Reflection failed: {e.Message}"); }

        Transform tSpace = rig != null ? rig.trackingSpace : null;

        RenderTexture cleanDepthRt = null;
        if (depthTex != null && depthToFloatMaterial != null)
        {
            depthToFloatMaterial.SetFloat("_DepthNear", depthNearZ);
            depthToFloatMaterial.SetFloat("_DepthFar", depthFarZ);
            
            cleanDepthRt = RenderTexture.GetTemporary(depthTex.width, depthTex.height, 0, RenderTextureFormat.RFloat);
            Graphics.Blit(depthTex, cleanDepthRt, depthToFloatMaterial);
        }

        // ── 2. WAIT FOR END OF FRAME ──
        yield return new WaitForEndOfFrame();

        // ── 3. LOCK IN CAMERA POSES AND RGB ──
        Texture camTex = cameraAccess.GetTexture();
        Pose cameraPose = cameraAccess.GetCameraPose();
        PassthroughCameraAccess.CameraIntrinsics intrinsics = cameraAccess.Intrinsics;

        if (tSpace != null)
        {
            Vector3 worldRgbPos = tSpace.TransformPoint(cameraPose.position);
            Quaternion worldRgbRot = tSpace.rotation * cameraPose.rotation;
            cameraPose = new Pose(worldRgbPos, worldRgbRot);

            depthPos = tSpace.TransformPoint(depthPos);
            depthRot = tSpace.rotation * depthRot;
        }

        RenderTexture cleanRgbRt = null;
        if (camTex != null)
        {
            cleanRgbRt = RenderTexture.GetTemporary(camTex.width, camTex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(camTex, cleanRgbRt);
        }

        // ── 4. RUN GPU READBACKS SIMULTANEOUSLY ──
        string depthBase64 = "";
        if (cleanDepthRt != null)
        {
            var depthReq = AsyncGPUReadback.Request(cleanDepthRt, 0, TextureFormat.RFloat);
            yield return new WaitUntil(() => depthReq.done);
            if (!depthReq.hasError)
            {
                NativeArray<byte> rawDepthBytes = depthReq.GetData<byte>();
                depthBase64 = Convert.ToBase64String(rawDepthBytes.ToArray());
            }
            RenderTexture.ReleaseTemporary(cleanDepthRt);
        }

        byte[] imageJpg = new byte[0];
        if (cleanRgbRt != null)
        {
            var rgbReq = AsyncGPUReadback.Request(cleanRgbRt, 0, TextureFormat.RGBA32);
            yield return new WaitUntil(() => rgbReq.done);
            if (!rgbReq.hasError)
            {
                NativeArray<byte> rawRgbBytes = rgbReq.GetData<byte>();
                int expectedBytes = cleanRgbRt.width * cleanRgbRt.height * 4;
                if (rawRgbBytes.Length == expectedBytes)
                {
                    Texture2D snap = new Texture2D(cleanRgbRt.width, cleanRgbRt.height, TextureFormat.RGBA32, false);
                    snap.LoadRawTextureData(rawRgbBytes.ToArray());
                    snap.Apply();
                    imageJpg = snap.EncodeToJPG(60);
                    Destroy(snap);
                }
            }
            RenderTexture.ReleaseTemporary(cleanRgbRt);
        }

        if (imageJpg.Length == 0)
        {
            Debug.LogError("[Quest/Retry] Failed to encode RGB image. Aborting retry.");
            _capturing = false;
            yield break;
        }

        // ── 5. BUILD PACKAGE AND SEND ──
        var package = new SnapshotMeta
        {
            timestamp = originalCaptureId, // Keep the original ID to map to the server's retry session
            fileName = $"Retry_{originalCaptureId}.jpg",
            cameraIndex = 0,

            posX = cameraPose.position.x, posY = cameraPose.position.y, posZ = cameraPose.position.z,
            rotX = cameraPose.rotation.x, rotY = cameraPose.rotation.y, rotZ = cameraPose.rotation.z, rotW = cameraPose.rotation.w,

            imageWidth = cameraAccess.CurrentResolution.x, imageHeight = cameraAccess.CurrentResolution.y,
            fx = intrinsics.FocalLength.x, fy = intrinsics.FocalLength.y, cx = intrinsics.PrincipalPoint.x, cy = cameraAccess.CurrentResolution.y - intrinsics.PrincipalPoint.y,
            distortionParams = new float[0],

            depth_posX = depthPos.x, depth_posY = depthPos.y, depth_posZ = depthPos.z,
            depth_rotX = depthRot.x, depth_rotY = depthRot.y, depth_rotZ = depthRot.z, depth_rotW = depthRot.w,

            depth_fx = depth_fx, depth_fy = depth_fy, depth_cx = depth_cx, depth_cy = depth_cy,
            depthWidth = depthTex != null ? depthTex.width : 0,
            depthHeight = depthTex != null ? depthTex.height : 0,
            depthNearZ = depthNearZ, depthFarZ = depthFarZ,

            command = "Background Retry",
            useYoloe  = useYoloeToggle != null && useYoloeToggle.isOn,
            intent = "retry", // Distinct intent tag
            imageRGB = Convert.ToBase64String(imageJpg),
            imageDepth = depthBase64
        };

        string json = JsonUtility.ToJson(package);
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(json);
        string base64 = Convert.ToBase64String(utf8);

        int total = Mathf.CeilToInt((float)base64.Length / CHUNK_SIZE);
        
        for (int i = 0; i < total; i++)
        {
            if (_dataChannel == null || _dataChannel.ReadyState != RTCDataChannelState.Open) {
                _capturing = false; yield break;
            }
            while (_dataChannel.BufferedAmount > 65536) yield return null;

            int start = i * CHUNK_SIZE;
            int length = Mathf.Min(CHUNK_SIZE, base64.Length - start);
            
            // Notice the "RIMG|" prefix instead of "IMG|"
            string msg = $"RIMG|{originalCaptureId}|{i}|{total}|{base64.Substring(start, length)}";

            try { _dataChannel.Send(msg); }
            catch (Exception e) { _capturing = false; yield break; }
            yield return null;
        }

        Debug.Log($"[Quest] Retry capture {originalCaptureId} fully sent ({total} chunks).");
        _capturing = false;
    }
    
    [Serializable]
    private class SignalingMessage
    {
        public string type;
        public string sdp;
        public string candidate;
        public string sdpMid;
        public int    sdpMLineIndex;
        public string role;
    }
}