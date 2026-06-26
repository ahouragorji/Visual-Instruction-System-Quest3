using Meta.XR.BuildingBlocks.AIBlocks;
using TMPro;
using UnityEngine;

public class QuestSpeechController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private SpeechToTextAgent speechToTextAgent;
    [SerializeField] private TMP_InputField commandInputField;

    [Header("Controls")]
    [SerializeField] private float triggerThreshold = 0.7f;
    [SerializeField] private float releaseThreshold = 0.3f;

    private bool _isRecording;
    private string _previousCommand;

    private void Start()
    {
        if (speechToTextAgent == null)
        {
            Debug.LogError("[SpeechToText] SpeechToTextAgent is not assigned.");
            enabled = false;
            return;
        }

        if (commandInputField == null)
        {
            Debug.LogError("[SpeechToText] Command Input Field is not assigned.");
            enabled = false;
            return;
        }

        speechToTextAgent.onTranscript.AddListener(OnTranscript);
    }

    private void OnDestroy()
    {
        if (speechToTextAgent != null)
        {
            speechToTextAgent.onTranscript.RemoveListener(OnTranscript);
        }
    }

    private void Update()
    {
        float trigger =
            OVRInput.Get(
                OVRInput.Axis1D.PrimaryIndexTrigger,
                OVRInput.Controller.LTouch);

        // Start recording
        if (trigger > triggerThreshold && !_isRecording)
        {
            StartRecording();
        }
        // Stop recording
        else if (trigger < releaseThreshold && _isRecording)
        {
            StopRecording();
        }
    }

    public void StartRecording()
    {
        _isRecording = true;

        Debug.Log("[SpeechToText] Started listening...");
        

        _previousCommand = commandInputField.text;
        commandInputField.text = "Recording...";

        speechToTextAgent.StartListening();
    }

    public void StopRecording()
    {
        _isRecording = false;

        Debug.Log("[SpeechToText] Stopped listening. Processing...");

        commandInputField.text = "Processing...";

        speechToTextAgent.StopNow();
    }

    private void OnTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            commandInputField.text = _previousCommand;
            Debug.LogWarning("[SpeechToText] Empty transcript received.");
            return;
        }

        text = text.Trim();

        Debug.Log($"[SpeechToText] Transcript: {text}");

        commandInputField.text = text;

        // TODO:
        // Send text to your NLP parser
        // Send text to Python server
        // Execute command pipeline
    }
}