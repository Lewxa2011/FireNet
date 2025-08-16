using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class NetworkStats : MonoBehaviour
{
    [Header("Display Settings")]
    public bool showStats = true;
    public KeyCode toggleKey = KeyCode.F1;

    private GUIStyle textStyle;

    // Static stats for easy access from other scripts
    public static int bytesSentPerSecond { get; private set; }
    public static int bytesReceivedPerSecond { get; private set; }
    public static int messagesSentPerSecond { get; private set; }
    public static int messagesReceivedPerSecond { get; private set; }
    public static float averagePing { get; private set; }

    // Internal tracking
    private int bytesSent, bytesReceived, messagesSent, messagesReceived;
    private float lastStatUpdate;
    private List<float> pingHistory = new List<float>();

    private void Start()
    {
        textStyle = new GUIStyle
        {
            normal = { textColor = Color.white },
            fontSize = 14
        };
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            showStats = !showStats;
        }

        if (Time.time - lastStatUpdate >= 1f)
        {
            UpdateStats();
            lastStatUpdate = Time.time;
        }
    }

    private void UpdateStats()
    {
        bytesSentPerSecond = bytesSent;
        bytesReceivedPerSecond = bytesReceived;
        messagesSentPerSecond = messagesSent;
        messagesReceivedPerSecond = messagesReceived;

        if (pingHistory.Count > 0)
            averagePing = pingHistory.Average();

        // Reset counters
        bytesSent = bytesReceived = messagesSent = messagesReceived = 0;
    }

    // Static methods for recording stats
    public static void RecordBytesSent(int bytes)
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance != null) instance.bytesSent += bytes;
    }

    public static void RecordBytesReceived(int bytes)
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance != null) instance.bytesReceived += bytes;
    }

    public static void RecordMessageSent()
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance != null) instance.messagesSent++;
    }

    public static void RecordMessageReceived()
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance != null) instance.messagesReceived++;
    }

    public static void RecordPing(float ping)
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance != null)
        {
            instance.pingHistory.Add(ping);
            if (instance.pingHistory.Count > 10)
                instance.pingHistory.RemoveAt(0);
        }
    }

    private void OnGUI()
    {
        if (!showStats) return;

        float y = 10f;
        float lineHeight = 20f;

        GUI.Label(new Rect(10, y, 300, lineHeight), $"Connection: {FireNetwork.connectionState}", textStyle);
        y += lineHeight;

        if (FireNetwork.inRoom)
        {
            GUI.Label(new Rect(10, y, 300, lineHeight), $"Room: {FireNetwork.currentRoom}", textStyle);
            y += lineHeight;
            GUI.Label(new Rect(10, y, 300, lineHeight), $"Players: {FireNetwork.playerCountInRoom}", textStyle);
            y += lineHeight;
            GUI.Label(new Rect(10, y, 300, lineHeight), $"Master Client: {FireNetwork.isMasterClient}", textStyle);
            y += lineHeight;
        }

        GUI.Label(new Rect(10, y, 300, lineHeight), $"Bytes Out/s: {bytesSentPerSecond}", textStyle);
        y += lineHeight;
        GUI.Label(new Rect(10, y, 300, lineHeight), $"Bytes In/s: {bytesReceivedPerSecond}", textStyle);
        y += lineHeight;
        GUI.Label(new Rect(10, y, 300, lineHeight), $"Messages Out/s: {messagesSentPerSecond}", textStyle);
        y += lineHeight;
        GUI.Label(new Rect(10, y, 300, lineHeight), $"Messages In/s: {messagesReceivedPerSecond}", textStyle);
        y += lineHeight;
        GUI.Label(new Rect(10, y, 300, lineHeight), $"Avg Ping: {averagePing:F1}ms", textStyle);
    }
}