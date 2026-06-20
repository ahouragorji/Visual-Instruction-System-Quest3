using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Whisper;       // From the com.whisper.unity package
using Whisper.Utils; // For MicrophoneRecord

public class QuestLocalWhisper : MonoBehaviour
{
    [Header("References")]
    public WhisperManager whisperManager;
    public TMP_InputField commandInputField;
    private string prevCommand;
    [Header("Audio")]
    [Tooltip("Use the MicrophoneRecord script provided by the Whisper package")]
    public MicrophoneRecord microphoneRecord;
    
    [Header("Controls")]
    public float triggerThreshold = 0.7f;
    
    private bool _isRecording = false;

    private void OnEnable()
    {
        if (microphoneRecord != null)
            microphoneRecord.OnRecordStop += HandleRecordStop;
    }

    private async void Start()
    {
        if (whisperManager == null)
        {
            Debug.LogError("[Local Whisper] WhisperManager reference is missing. Assign it in the inspector.");
            return;
        }

        if (!whisperManager.IsLoaded && !whisperManager.IsLoading)
        {
            Debug.Log("[Local Whisper] Initializing Whisper model...");
            await whisperManager.InitModel();
        }
    }

    private void OnDisable()
    {
        if (microphoneRecord != null)
            microphoneRecord.OnRecordStop -= HandleRecordStop;
    }

    void Update()
    {
        // Check the right index trigger
        float trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
        
        // PRESS AND HOLD to talk
        if (trigger > triggerThreshold && !_isRecording)
        {
            _isRecording = true;
            Debug.Log("[Local Whisper] Started listening...");
            
            // Start recording audio locally
            microphoneRecord.StartRecord();
            prevCommand = commandInputField.text;
            commandInputField.text = "recording...";
        }
        // RELEASE to transcribe
        else if (trigger < 0.3f && _isRecording)
        {
            _isRecording = false;
            Debug.Log("[Local Whisper] Stopped listening. Transcribing on-device...");
            
            microphoneRecord.StopRecord();
            commandInputField.text = "preocessing...";
        }
    }

    private void HandleRecordStop(AudioChunk recordedAudio)
    {
        var recordedClip = AudioClip.Create(
            "RecordedAudio",
            recordedAudio.Data.Length / recordedAudio.Channels,
            recordedAudio.Channels,
            recordedAudio.Frequency,
            false);
        recordedClip.SetData(recordedAudio.Data, 0);

        TranscribeOnDevice(recordedClip);
    }

    private async void TranscribeOnDevice(AudioClip clip)
    {
        if (whisperManager == null)
        {
            Debug.LogError("[Local Whisper] WhisperManager is not assigned.");
            commandInputField.text = prevCommand;
            return;
        }

        if (!whisperManager.IsLoaded && !whisperManager.IsLoading)
        {
            Debug.LogWarning("[Local Whisper] Whisper model not loaded yet. Initializing now...");
            await whisperManager.InitModel();
        }

        WhisperResult result = await whisperManager.GetTextAsync(clip);
        
        if (result != null && !string.IsNullOrWhiteSpace(result.Result))
        {
            string command = result.Result.Trim();
            Debug.Log($"[Local Whisper] Recognized: {command}");
            
            // Fire your existing pipeline, passing the recognized text to Python!
            commandInputField.text = command;
        }
        else
        {
            commandInputField.text = prevCommand;
            Debug.LogError("[Local Whisper] Could not understand audio.");
        }
    }
}