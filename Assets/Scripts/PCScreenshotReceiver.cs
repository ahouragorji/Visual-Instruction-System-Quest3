using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Unity.WebRTC;
using NativeWebSocket;

public class PCScreenShotReceiver : MonoBehaviour
{
    [Header("Signaling")]
    public string signalingUrl = "ws://127.0.0.1:3000";

    [Tooltip("Check this if your Python server is running inside WSL (Windows Subsystem for Linux).")]
    public bool serverIsOnWSL = true;

    [Header("Storage")]
    public string saveFolder = "C:/QuestSnapshots";

    [Header("Detection + Reprojection Server")]
    [Tooltip("Local Python server that runs YOLOE/SAM detection and reprojection.")]
    public string detectionServerUrl = "http://127.0.0.1:5000/process";

    [Header("Retry Settings")]
    [Tooltip("URL for the /retry endpoint on the Python server.")]
    public string retryServerUrl = "http://127.0.0.1:5000/retry";
    [Tooltip("How often (seconds) the PC polls /retry after a /process call.")]
    public float retryIntervalSeconds = 10f;
    
    [Header("Error Handling")]
    [Tooltip("How many times to attempt the server request before giving up.")]
    public int maxServerRetries = 3;
    [Tooltip("How long to wait (in seconds) between failed server attempts.")]
    public float errorRetryDelaySeconds = 2f;

    [Serializable]
    private class RetryRequestBody
    {
        public string id;
        public string rgbPath;
        public string depthPath;
        public string metaPath;
    }

    [Serializable]
    private class RetryResponse
    {
        public string      id;
        public AROverlay[] ar_overlays;
        public bool        retry_complete;
    }

    [Tooltip("Default task command sent to the vision pipeline. Changeable per-request later if needed.")]
    public string defaultCommand = "Clean your room";
    
    private WebSocket _ws;
    private RTCPeerConnection _pc;
    private RTCDataChannel _remoteDataChannel;

    private string  _lastCaptureId   = null;
    private bool _hasRemoteDescription = false;
    private List<SignalingMessage> _pendingIceCandidates = new List<SignalingMessage>();
    
    private readonly Queue<string> _msgQueue = new Queue<string>();
    private Dictionary<string, string[]> _chunkBuffers = new Dictionary<string, string[]>();

    private readonly Dictionary<string, float> _chunkLastUpdateTime = new Dictionary<string, float>();
    private const float STALL_TIMEOUT_SECONDS = 5f;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    private void Start()
    {
        StartCoroutine(WebRTC.Update());
        if (!Directory.Exists(saveFolder)) Directory.CreateDirectory(saveFolder);
        ConnectSignaling();
    }

    private async void ConnectSignaling()
    {
        Debug.Log($"[PC] Connecting to signaling: {signalingUrl}");
        _ws = new WebSocket(signalingUrl);

        _ws.OnOpen += () => {
            Debug.Log("[PC] Connected. Registering as 'pc'...");
            _ws.SendText(JsonUtility.ToJson(new SignalingMessage { type = "register", role = "pc" }));
        };
        _ws.OnMessage += (bytes) => {
            string raw = System.Text.Encoding.UTF8.GetString(bytes);
            lock (_msgQueue) { _msgQueue.Enqueue(raw); }
        };
        _ws.OnError   += (e)    => Debug.LogError($"[PC] WS error: {e}");
        _ws.OnClose   += (code) => Debug.Log($"[PC] WS closed: {code}");

        await _ws.Connect();
    }

    private void Update()
    {
        _ws?.DispatchMessageQueue();

        while (_msgQueue.Count > 0)
        {
            string raw;
            lock (_msgQueue) { raw = _msgQueue.Dequeue(); }
            RouteMessage(raw);
        }

        CheckForStalledTransfers();
    }

    private void CheckForStalledTransfers()
    {
        if (_chunkBuffers.Count == 0) return;

        List<string> stalledIds = null;
        foreach (var kvp in _chunkBuffers)
        {
            string id = kvp.Key;
            if (!_chunkLastUpdateTime.TryGetValue(id, out float lastUpdate)) continue;

            if (Time.time - lastUpdate > STALL_TIMEOUT_SECONDS)
            {
                int received = 0;
                foreach (var c in kvp.Value) if (c != null) received++;
                Debug.LogError($"[PC] STALLED TRANSFER: capture '{id}' received {received}/{kvp.Value.Length} " +
                                $"chunks and has been stuck for {Time.time - lastUpdate:F1}s. Discarding.");

                (stalledIds ??= new List<string>()).Add(id);
            }
        }

        if (stalledIds != null)
        {
            foreach (var id in stalledIds)
            {
                _chunkBuffers.Remove(id);
                _chunkLastUpdateTime.Remove(id);
            }
        }
    }

    private void OnDestroy()
    {
        _remoteDataChannel?.Close();
        _pc?.Close();
        if (_ws != null) _ = _ws.Close();
    }

    // -------------------------------------------------------------------------
    // WebRTC handshake
    // -------------------------------------------------------------------------
private IEnumerator HandleOffer(string sdpOffer)
{
            if (_pc != null)
        {
            Debug.Log("[PC] Cleaning up old peer connection before handling new offer.");
            _remoteDataChannel?.Close();
            _remoteDataChannel?.Dispose();
            _remoteDataChannel = null;
            _pc.Close();
            _pc.Dispose();
            _pc = null;
        }
        var config = new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };
        _pc = new RTCPeerConnection(ref config);



        _pc.OnDataChannel = channel => {
            Debug.Log("[PC] DataChannel received from Quest.");
            _remoteDataChannel = channel;
            _remoteDataChannel.OnMessage = bytes => {
                string msg = System.Text.Encoding.UTF8.GetString(bytes);
                lock (_msgQueue) { _msgQueue.Enqueue("DC|" + msg); }
            };
        };

        _pc.OnIceCandidate = candidate => {
            _ws.SendText(JsonUtility.ToJson(new SignalingMessage
            {
                type = "ice", candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid, sdpMLineIndex = candidate.SdpMLineIndex ?? 0
            }));
        };

        var remoteDesc = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = sdpOffer };
        var setRemoteOp = _pc.SetRemoteDescription(ref remoteDesc);
        yield return setRemoteOp;
        
        if (setRemoteOp.IsError) {
            Debug.LogError($"[PC] SetRemoteDescription Error: {setRemoteOp.Error.message}");
            yield break;
        }

        // =====================================================================
        // THE FIX: Mark as ready and flush the queued ICE candidates
        // =====================================================================
        _hasRemoteDescription = true;
        foreach (var c in _pendingIceCandidates)
        {
            StartCoroutine(AddIceCandidate(c.candidate, c.sdpMid, c.sdpMLineIndex));
        }
        _pendingIceCandidates.Clear();

        var answerOp = _pc.CreateAnswer();
        yield return answerOp;

        var answerDesc = answerOp.Desc;
        yield return _pc.SetLocalDescription(ref answerDesc);
        _ws.SendText(JsonUtility.ToJson(new SignalingMessage { type = "answer", sdp = answerDesc.sdp }));
        Debug.Log("[PC] Answer sent.");
    }

    // -------------------------------------------------------------------------
    // Reconnect
    // -------------------------------------------------------------------------
private IEnumerator AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
    {
        _pc.AddIceCandidate(new RTCIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate, sdpMid = sdpMid, sdpMLineIndex = sdpMLineIndex
        }));
        yield return null;
    }
    public void UpdateSignalingAddressAndReconnect(string newAddress)
    {
        signalingUrl = newAddress;
        StartCoroutine(RestartConnectionRoutine());
    }

    private IEnumerator RestartConnectionRoutine()
    {
        Debug.Log("[PC] Restarting connection...");
        _remoteDataChannel?.Close();
        _hasRemoteDescription = false;
        _pendingIceCandidates.Clear();

        _remoteDataChannel?.Close();
        _remoteDataChannel?.Dispose();
        _remoteDataChannel = null;

        _pc?.Close();
        _pc?.Dispose();
        if (_ws != null && _ws.State != NativeWebSocket.WebSocketState.Closed)
            _ = _ws.Close();

        yield return new WaitForSeconds(0.5f);
        _msgQueue.Clear();
        _chunkBuffers.Clear();
        _chunkLastUpdateTime.Clear();

        Debug.Log($"[PC] Reconnecting to: {signalingUrl}");
        ConnectSignaling();
    }

    // -------------------------------------------------------------------------
    // Data channel message handling
    // -------------------------------------------------------------------------

    private void RouteMessage(string raw)
    {
        if (raw.StartsWith("DC|"))
        {
            OnDataChannelMessage(raw.Substring(3));
            return;
        }

        var msg = JsonUtility.FromJson<SignalingMessage>(raw);
        if (msg == null) return;

        switch (msg.type)
        {
            case "offer":
                Debug.Log("[PC] Received offer from Quest. Creating answer.");
                StartCoroutine(HandleOffer(msg.sdp));
                break;
            case "ice":
                if (!string.IsNullOrEmpty(msg.candidate))
                {
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

   private void OnDataChannelMessage(string message)
    {
        bool isRetry = message.StartsWith("RIMG|");
        if (!message.StartsWith("IMG|") && !isRetry) return;

        string[] parts = message.Split(new char[]{'|'}, 5);
        if (parts.Length < 5)
        {
            Debug.LogError($"[PC] Malformed chunk: expected 5 parts, got {parts.Length}");
            return;
        }

        string id      = parts[1];
        int    index   = int.Parse(parts[2]);
        int    total   = int.Parse(parts[3]);
        string data    = parts[4];

        // Differentiate buffer keys so regular and retry chunks don't collide
        string bufferKey = (isRetry ? "R_" : "I_") + id;

        if (!_chunkBuffers.ContainsKey(bufferKey))
        {
            _chunkBuffers[bufferKey] = new string[total];
            Debug.Log($"[PC] Started receiving {(isRetry ? "RETRY capture" : "capture")} '{id}': expecting {total} chunks.");
        }

        _chunkBuffers[bufferKey][index] = data;
        _chunkLastUpdateTime[bufferKey] = Time.time;

        if (Array.TrueForAll(_chunkBuffers[bufferKey], chunk => chunk != null))
        {
            string fullBase64 = string.Concat(_chunkBuffers[bufferKey]);
            _chunkBuffers.Remove(bufferKey);
            _chunkLastUpdateTime.Remove(bufferKey);
            Debug.Log($"[PC] {(isRetry ? "RETRY capture" : "Capture")} '{id}' complete: all {total} chunks received. Saving...");
            
            // Pass the isRetry flag to the save function
            SaveSnapshotToDisk(id, fullBase64, isRetry);
        }
    }

    // -------------------------------------------------------------------------
    // Save to disk
    // -------------------------------------------------------------------------

    // Add the optional isRetry parameter here
    private void SaveSnapshotToDisk(string id, string base64Envelope, bool isRetry = false)
    {
        SnapshotMeta metadata = null;
        string rgbPath = null, depthPath = null, metaPath = null;
        
        // CREATE A DISTINCT PREFIX FOR RETRY FILES
        string filePrefix = isRetry ? "Retry_" : "";

        try
        {
            byte[] jsonBytes = Convert.FromBase64String(base64Envelope);
            string json = System.Text.Encoding.UTF8.GetString(jsonBytes);
            metadata = JsonUtility.FromJson<SnapshotMeta>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[PC] FATAL: Failed to parse incoming JSON envelope for {id}. Error: {e.Message}");
            return;
        }

        if (metadata == null) return;

        if (!string.IsNullOrEmpty(metadata.imageRGB))
        {
            try
            {
                byte[] imageBytes = Convert.FromBase64String(metadata.imageRGB);
                // Apply the prefix here
                rgbPath = Path.Combine(saveFolder, $"{filePrefix}RGB_{id}.jpg");
                File.WriteAllBytes(rgbPath, imageBytes);
                Debug.Log($"[PC] RGB saved: {rgbPath}");
            }
            catch (Exception e) { Debug.LogError($"[PC] RGB Decode failed for {id}. Error: {e.Message}"); }
        }

        if (!string.IsNullOrEmpty(metadata.imageDepth))
        {
            try
            {
                byte[] depthBytes = Convert.FromBase64String(metadata.imageDepth);
                // Apply the prefix here
                depthPath = Path.Combine(saveFolder, $"{filePrefix}Depth_{id}.bin");
                File.WriteAllBytes(depthPath, depthBytes);
                Debug.Log($"[PC] Depth saved: {depthPath}");
            }
            catch (Exception e) { Debug.LogError($"[PC] Depth Decode failed for {id}. Error: {e.Message}"); }
        }

        if (!string.IsNullOrEmpty(metadata.command) && !isRetry)
        {
            Debug.LogWarning("[DEFAULT COMMAND ]changing default to what user said");
            defaultCommand = metadata.command;
            Debug.Log($"[PC] Command updated to: '{defaultCommand}'");
        }

        try
        {
            metadata.imageRGB   = "";
            metadata.imageDepth = "";
            // Apply the prefix here
            metaPath = Path.Combine(saveFolder, $"{filePrefix}Meta_{id}.json");
            File.WriteAllText(metaPath, JsonUtility.ToJson(metadata, true));
            Debug.Log($"[PC] Metadata saved: {metaPath}");
        }
        catch (Exception e) { Debug.LogError($"[PC] Failed to save metadata JSON for {id}. Error: {e.Message}"); }

        // =====================================================================
        // Trigger server requests with the specific file paths
        // =====================================================================
        if (rgbPath != null && depthPath != null && metaPath != null)
        {
            if (!isRetry)
            {
                StartCoroutine(RequestInstructionsFromServer(id, rgbPath, depthPath, metaPath, defaultCommand, metadata.useYoloe, metadata.intent));
            }
            else
            {
                Debug.Log($"[PC] Fresh retry capture saved to disk for '{id}'. Sending to server immediately.");
                // Because we pass rgbPath directly, Python will read the "Retry_RGB" file!
                StartCoroutine(RequestRetryFromServer(id, rgbPath, depthPath, metaPath));
            }
        }
        else
        {
            Debug.LogWarning($"[PC] Skipping server request for '{id}': one or more files failed to save.");
        }
    }

    // -------------------------------------------------------------------------
    // Detection + reprojection server integration
    // -------------------------------------------------------------------------

    [Serializable]
    private class ServerRequestBody
    {
        public string id;
        public string rgbPath;
        public string depthPath;
        public string metaPath;
        public string command;
        public bool   useYoloe;   // NEW
        public string intent; 
    }

    // ── Serialization types that match app.py's output schema exactly ────────
    //
    // app.py returns:
    //   { "id": str, "ar_overlays": [ AROverlay, ... ] }
    //
    // Each AROverlay has:
    //   step, instruction, guidance_tool, manipulation_tag,
    //   tool_settings ([{key, value},...]), worldX/Y/Z, bboxCorners ([{x,y,z},...])
    //
    // These must match QuestInstructionReceiver.AROverlay field-for-field so that
    // the raw server JSON can be base64-encoded and relayed directly to the Quest
    // without any transformation. JsonUtility requires the field names to match
    // the JSON keys exactly (it is case-sensitive).

    [Serializable]
    private class FeatureParameter
    {
        public string key;
        public string value;
    }

    [Serializable]
    private class Corner3D
    {
        public float x, y, z;
    }

    [Serializable]
    private class AROverlay
    {
        public int             step;
        public string          instruction;
        public string          guidance_tool;
        public string          manipulation_tag;
        public FeatureParameter[] tool_settings;
        public float           worldX, worldY, worldZ;
        public Corner3D[]      bboxCorners;
    }

    [Serializable]
    private class InstructionResponse
    {
        public string      id;
        public AROverlay[] ar_overlays;
    }

  private IEnumerator RequestInstructionsFromServer(string id, string rgbPath, string depthPath,
                                                        string metaPath, string command, bool useYoloe, string intent)
    {
        var body = new ServerRequestBody
        {
            id        = id,
            rgbPath   = FormatPathForServer(rgbPath),
            depthPath = FormatPathForServer(depthPath),
            metaPath  = FormatPathForServer(metaPath),
            command   = command,
            useYoloe  = useYoloe,               
            intent    = intent 
        };

        string jsonPayload = JsonUtility.ToJson(body);

        for (int attempt = 1; attempt <= maxServerRetries; attempt++)
        {
            Debug.Log($"[PC] Requesting instructions for '{id}' from: {detectionServerUrl} (Attempt {attempt}/{maxServerRetries})");

            using (var req = new UnityWebRequest(detectionServerUrl, "POST"))
            {
                byte[] payload = Encoding.UTF8.GetBytes(jsonPayload);
                req.uploadHandler   = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 120; // Keep this high if YOLOE/SAM takes a while

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    string responseJson = req.downloadHandler.text;
                    Debug.Log($"[PC] Server responded for '{id}': {responseJson.Length} chars.");

                    InstructionResponse parsed = null;
                    try
                    {
                        parsed = JsonUtility.FromJson<InstructionResponse>(responseJson);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[PC] Failed to parse server response for '{id}': {e.Message}");
                        yield break; // A parsing error means the data is fundamentally broken, so we abort.
                    }

                    if (parsed != null && parsed.ar_overlays != null && parsed.ar_overlays.Length > 0)
                    {
                        Debug.Log($"[PC] Relaying {parsed.ar_overlays.Length} overlays to Quest for '{id}'.");
                        RelayInstructionsToQuest(responseJson);
                        _lastCaptureId = id;
                    }
                    else
                    {
                        Debug.LogWarning($"[PC] Server returned no ar_overlays for '{id}'. Nothing to relay.");
                    }
                    
                    yield break; // SUCCESS: Exit the coroutine completely.
                }
                else
                {
                    Debug.LogError($"[PC] Server request failed for '{id}': {req.error}. Response: {req.downloadHandler?.text}");
                    
                    if (attempt < maxServerRetries)
                    {
                        Debug.Log($"[PC] Retrying in {errorRetryDelaySeconds} seconds...");
                        yield return new WaitForSeconds(errorRetryDelaySeconds);
                    }
                    else
                    {
                        Debug.LogError($"[PC] Exhausted all {maxServerRetries} retries for '{id}'. Pipeline stopped for this request.");
                    }
                }
            }
        }
    }
private IEnumerator RequestRetryFromServer(string captureId, string rgbPath, string depthPath, string metaPath)
    {
        var body = new RetryRequestBody
        {
            id        = captureId,
            rgbPath   = FormatPathForServer(rgbPath),
            depthPath = FormatPathForServer(depthPath),
            metaPath  = FormatPathForServer(metaPath),
        };

        string jsonPayload = JsonUtility.ToJson(body);

        for (int attempt = 1; attempt <= maxServerRetries; attempt++)
        {
            Debug.Log($"[PC/Retry] Requesting retry detection for '{captureId}' (Attempt {attempt}/{maxServerRetries})");

            using (var req = new UnityWebRequest(retryServerUrl, "POST"))
            {
                byte[] payload = Encoding.UTF8.GetBytes(jsonPayload);
                req.uploadHandler   = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 30;

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    string responseJson = req.downloadHandler.text;
                    RetryResponse parsed = null;
                    try { parsed = JsonUtility.FromJson<RetryResponse>(responseJson); }
                    catch (Exception e)
                    {
                        Debug.LogError($"[PC/Retry] Failed to parse retry response: {e.Message}");
                        yield break;
                    }

                    if (parsed != null && (parsed.retry_complete || (parsed.ar_overlays != null && parsed.ar_overlays.Length > 0)))
                    {
                        if (parsed.retry_complete)
                            Debug.Log($"[PC/Retry] All objects found for '{captureId}'. Relaying completion to Quest.");
                        else
                            Debug.Log($"[PC/Retry] {parsed.ar_overlays.Length} new overlay(s) for '{captureId}'. Relaying as RETRY.");
                        
                        RelayRetryToQuest(responseJson);
                    }
                    else
                    {
                        Debug.Log($"[PC/Retry] Nothing new found in this retry image for '{captureId}'.");
                    }

                    yield break; // SUCCESS: Exit the coroutine.
                }
                else
                {
                    Debug.LogWarning($"[PC/Retry] Request failed for '{captureId}': {req.error}");
                    
                    if (attempt < maxServerRetries)
                    {
                        Debug.Log($"[PC/Retry] Retrying in {errorRetryDelaySeconds} seconds...");
                        yield return new WaitForSeconds(errorRetryDelaySeconds);
                    }
                    else
                    {
                        Debug.LogError($"[PC/Retry] Exhausted retries for '{captureId}'.");
                    }
                }
            }
        }
    }


private void RelayRetryToQuest(string retryJson)
{
    if (_remoteDataChannel == null || _remoteDataChannel.ReadyState != RTCDataChannelState.Open)
    {
        Debug.LogError("[PC/Retry] Cannot relay: DataChannel is not open.");
        return;
    }

    const int CHUNK_SIZE = 12000;
    byte[] utf8   = Encoding.UTF8.GetBytes(retryJson);
    string base64 = Convert.ToBase64String(utf8);
    int    total  = Mathf.CeilToInt((float)base64.Length / CHUNK_SIZE);
    string relayId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

    for (int i = 0; i < total; i++)
    {
        int    start  = i * CHUNK_SIZE;
        int    length = Mathf.Min(CHUNK_SIZE, base64.Length - start);
        string msg    = $"RETRY|{relayId}|{i}|{total}|{base64.Substring(start, length)}";

        try { _remoteDataChannel.Send(msg); }
        catch (Exception e)
        {
            Debug.LogError($"[PC/Retry] Failed to relay chunk {i}/{total}: {e.Message}");
            return;
        }
    }

    Debug.Log($"[PC/Retry] Relayed to Quest ({total} chunks).");
}
    private void RelayInstructionsToQuest(string instructionJson)
    {
        if (_remoteDataChannel == null || _remoteDataChannel.ReadyState != RTCDataChannelState.Open)
        {
            Debug.LogError("[PC] Cannot relay instructions: DataChannel is not open.");
            return;
        }

        const int CHUNK_SIZE = 12000;
        byte[] utf8   = Encoding.UTF8.GetBytes(instructionJson);
        string base64 = Convert.ToBase64String(utf8);
        int    total  = Mathf.CeilToInt((float)base64.Length / CHUNK_SIZE);
        string relayId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        for (int i = 0; i < total; i++)
        {
            int    start  = i * CHUNK_SIZE;
            int    length = Mathf.Min(CHUNK_SIZE, base64.Length - start);
            string msg    = $"INSTR|{relayId}|{i}|{total}|{base64.Substring(start, length)}";

            try { _remoteDataChannel.Send(msg); }
            catch (Exception e)
            {
                Debug.LogError($"[PC] Failed to relay instruction chunk {i}/{total}: {e.Message}");
                return;
            }
        }

        Debug.Log($"[PC] Relayed to Quest ({total} chunks, {base64.Length} base64 chars).");
    }

    private string FormatPathForServer(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath)) return windowsPath;
        if (!serverIsOnWSL) return windowsPath;

        string path = windowsPath.Replace("\\", "/");
        if (path.Length >= 2 && path[1] == ':')
        {
            char driveLetter = char.ToLower(path[0]);
            string remainder = path.Substring(2);
            if (!remainder.StartsWith("/")) remainder = "/" + remainder;
            path = $"/mnt/{driveLetter}{remainder}";
        }
        return path;
    }

    // -------------------------------------------------------------------------
    // Serialization
    // -------------------------------------------------------------------------

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