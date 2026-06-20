using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

// FIXED: Now requires the TextMeshPro version of the Input Field
[RequireComponent(typeof(TMP_InputField))]
public class KeyboardHandler : MonoBehaviour, ISelectHandler, IPointerClickHandler
{
    private TouchScreenKeyboard overlayKeyboard;
    private TMP_InputField inputField;

    void Awake()
    {
        inputField = GetComponent<TMP_InputField>();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        OpenKeyboard();
    }

    public void OnSelect(BaseEventData eventData)
    {
        OpenKeyboard();
    }

    private void OpenKeyboard()
    {
        if (overlayKeyboard == null || !overlayKeyboard.active)
        {
            overlayKeyboard = TouchScreenKeyboard.Open(inputField.text, TouchScreenKeyboardType.Default);
        }
    }

    void Update()
    {
        if (overlayKeyboard != null && overlayKeyboard.active)
        {
            inputField.text = overlayKeyboard.text;
        }
    }
}