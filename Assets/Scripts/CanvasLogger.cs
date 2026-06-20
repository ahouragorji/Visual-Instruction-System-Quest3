using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CanvasLogger : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("The Text component where logs will be printed.")]
    public TMP_Text logTextUI;
    
    [Tooltip("Optional: The ScrollRect containing the text so it auto-scrolls.")]
    public ScrollRect scrollRect;

    [Header("Settings")]
    [Tooltip("Maximum number of lines to keep on screen to prevent massive UI lag.")]
    public int maxLogLines = 40;

    // Thread-safe queue to catch logs from WebRTC/Async background threads
    private readonly ConcurrentQueue<string> _pendingLogs = new ConcurrentQueue<string>();
    
    // The actual sliding window of lines currently displayed
    private readonly Queue<string> _displayLines = new Queue<string>();

    private void OnEnable()
    {
        Application.logMessageReceivedThreaded += HandleLog;
        
        if (logTextUI != null)
            logTextUI.text = "Logger Initialized...\n";
    }

    private void OnDisable()
    {
        Application.logMessageReceivedThreaded -= HandleLog;
    }

    private void HandleLog(string logString, string stackTrace, LogType type)
    {
        // Color-code the logs based on severity using rich text
        string colorHex = type switch
        {
            LogType.Error => "#FF5555",     // Red
            LogType.Exception => "#FF5555", // Red
            LogType.Warning => "#FFD500",   // Yellow
            LogType.Log => "#FFFFFF",       // White
            LogType.Assert => "#FF8800",    // Orange
            _ => "#FFFFFF"
        };

        string time = DateTime.Now.ToString("HH:mm:ss.fff");
        string formattedLog = $"<color={colorHex}>[{time}] {logString}</color>";

        // Enqueue safely from any thread
        _pendingLogs.Enqueue(formattedLog);
    }

    private void Update()
    {
        // Only update the UI if we actually have new logs waiting
        if (_pendingLogs.IsEmpty) return;

        bool changed = false;

        // Drain the pending queue into our display queue
        while (_pendingLogs.TryDequeue(out string newLog))
        {
            _displayLines.Enqueue(newLog);
            changed = true;

            // Keep the queue size manageable so the Quest doesn't freeze rendering massive text blocks
            while (_displayLines.Count > maxLogLines)
            {
                _displayLines.Dequeue();
            }
        }

        if (changed && logTextUI != null)
        {
            // Join all lines and update the UI once per frame
            logTextUI.text = string.Join("\n", _displayLines);

            // Force the scroll view to snap to the bottom
            if (scrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                scrollRect.verticalNormalizedPosition = 0f;
            }
        }
    }
}