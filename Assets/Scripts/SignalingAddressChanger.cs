using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SignalingAddressChanger : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag your QuestPassthroughSender (or PCImageReceiver) here")]
    public QuestPassthroughSender webrtcScript; 
    
    [Tooltip("Drag your Input Field here")]
    [SerializeField] private TMP_InputField addressInputField;

    [Tooltip("Drag your UI Connect Button here")]
    [SerializeField] private Button connectButton;

    void Awake()
    {
        if (webrtcScript != null && addressInputField != null)
        {
            addressInputField.text = webrtcScript.signalingUrl;
        }

        // 1. Automatically wire up the button click event
        if (connectButton != null)
        {
            connectButton.onClick.AddListener(OnConnectButtonClicked);
        }
        else
        {
            Debug.LogWarning("[AddressChanger] Connect Button is not assigned in the Inspector!");
        }
    }

    private void OnDestroy()
    {
        // 2. Good practice: Unsubscribe when the object is destroyed to prevent memory leaks
        if (connectButton != null)
        {
            connectButton.onClick.RemoveListener(OnConnectButtonClicked);
        }
    }

    public void OnConnectButtonClicked()
    {
        if (webrtcScript == null || addressInputField == null)
        {
            Debug.LogError("[AddressChanger] Missing references in the Inspector!");
            return;
        }

        string newUrl = addressInputField.text.Trim();

        // Optional Quality of Life: Auto-add "ws://" if the user forgets to type it
        if (!string.IsNullOrEmpty(newUrl) && !newUrl.StartsWith("ws://"))
        {
            newUrl = "ws://" + newUrl;
            
            // Update the input field visually so the user sees the corrected format
            addressInputField.text = newUrl; 
        }

        // Send the new URL to the WebRTC script to trigger the restart
        webrtcScript.UpdateSignalingAddressAndReconnect(newUrl);
    }
}