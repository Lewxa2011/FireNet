using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

public class NetworkStats : MonoBehaviour
{
    [Header("Display Settings")]
    public bool showStats = true;
    public KeyCode toggleKey = KeyCode.F1;
    public bool showDetailedStats = false;
    public KeyCode detailToggleKey = KeyCode.F2;

    [Header("Performance Monitoring")]
    public bool trackPerformance = true;
    public float updateInterval = 1f; // Update stats every second

    private GUIStyle headerStyle;
    private GUIStyle textStyle;
    private GUIStyle errorStyle;

    // Network statistics
    public static int bytesSentPerSecond { get; private set; }
    public static int bytesReceivedPerSecond { get; private set; }
    public static int messagesSentPerSecond { get; private set; }
    public static int messagesReceivedPerSecond { get; private set; }
    public static int rpcsSentPerSecond { get; private set; }
    public static int rpcsReceivedPerSecond { get; private set; }
    public static float averagePing { get; private set; }

    // Performance statistics
    public static float firebaseOperationsPerSecond { get; private set; }
    public static int failedOperationsPerSecond { get; private set; }
    public static float memoryUsageMB { get; private set; }
    public static int activeNetworkObjects { get; private set; }

    // Internal tracking
    private int bytesSent, bytesReceived, messagesSent, messagesReceived;
    private int rpcsSent, rpcsReceived;
    private int firebaseOperations, failedOperations;
    private float lastStatUpdate;
    private List<float> pingHistory = new List<float>(10);

    // Performance tracking
    private List<float> frameTimeHistory = new List<float>(60);
    private float lastFrameTime;

    // Memory tracking
    private float lastMemoryCheck;
    private const float MEMORY_CHECK_INTERVAL = 5f;

    private Color orange = new Color(1f, 165f / 255f, 0f, 1f);

    private void Start()
    {
        InitializeStyles();
        lastStatUpdate = Time.time;
        lastFrameTime = Time.time;
        lastMemoryCheck = Time.time;
    }

    private void InitializeStyles()
    {
        headerStyle = new GUIStyle
        {
            normal = { textColor = Color.yellow },
            fontSize = 16,
            fontStyle = FontStyle.Bold
        };

        textStyle = new GUIStyle
        {
            normal = { textColor = Color.white },
            fontSize = 12
        };

        errorStyle = new GUIStyle
        {
            normal = { textColor = Color.red },
            fontSize = 12,
            fontStyle = FontStyle.Bold
        };
    }

    private void Update()
    {
        HandleInput();
        TrackPerformance();

        if (Time.time - lastStatUpdate >= updateInterval)
        {
            UpdateStats();
            lastStatUpdate = Time.time;
        }
    }

    private void HandleInput()
    {
        if (Keyboard.current[Key.F1].wasPressedThisFrame)
        {
            showStats = !showStats;
        }

        if (Keyboard.current[Key.F2].wasPressedThisFrame)
        {
            showDetailedStats = !showDetailedStats;
        }
    }

    private void TrackPerformance()
    {
        if (!trackPerformance) return;

        // Track frame times
        float currentFrameTime = Time.deltaTime * 1000f; // Convert to milliseconds
        frameTimeHistory.Add(currentFrameTime);
        if (frameTimeHistory.Count > 60)
        {
            frameTimeHistory.RemoveAt(0);
        }

        // Check memory usage periodically
        if (Time.time - lastMemoryCheck >= MEMORY_CHECK_INTERVAL)
        {
            memoryUsageMB = System.GC.GetTotalMemory(false) / (1024f * 1024f);
            lastMemoryCheck = Time.time;
        }

        // Track active network objects
        if (FireNetwork.Instance != null)
        {
            activeNetworkObjects = FireNetwork.GetNetworkStats().NetworkObjects;
        }
    }

    private void UpdateStats()
    {
        // Update network stats
        bytesSentPerSecond = bytesSent;
        bytesReceivedPerSecond = bytesReceived;
        messagesSentPerSecond = messagesSent;
        messagesReceivedPerSecond = messagesReceived;
        rpcsSentPerSecond = rpcsSent;
        rpcsReceivedPerSecond = rpcsReceived;

        // Update performance stats
        firebaseOperationsPerSecond = firebaseOperations;
        failedOperationsPerSecond = failedOperations;

        // Calculate average ping
        if (pingHistory.Count > 0)
            averagePing = pingHistory.Average();

        // Reset counters
        bytesSent = bytesReceived = messagesSent = messagesReceived = 0;
        rpcsSent = rpcsReceived = 0;
        firebaseOperations = failedOperations = 0;
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

    public static void RecordRpcSent()
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance != null) instance.rpcsSent++;
    }

    public static void RecordRpcReceived()
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance != null) instance.rpcsReceived++;
    }

    public static void RecordFirebaseOperation(bool success = true)
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance != null)
        {
            instance.firebaseOperations++;
            if (!success) instance.failedOperations++;
        }
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

        float x = 10f;
        float y = 10f;
        float width = showDetailedStats ? 400f : 300f;
        float lineHeight = 18f;

        // Background panel
        GUI.Box(new Rect(x - 5, y - 5, width + 10, GetPanelHeight() + 10), "");

        // Header
        GUI.Label(new Rect(x, y, width, lineHeight), "Network Statistics", headerStyle);
        y += lineHeight + 5;

        // Connection info
        DrawConnectionInfo(ref x, ref y, width, lineHeight);

        if (FireNetwork.inRoom)
        {
            // Controls info
            y += 10;
            GUI.Label(new Rect(x, y, width, lineHeight), $"Toggle: {toggleKey} | Details: {detailToggleKey}", textStyle);
        }
    }

    private void DrawConnectionInfo(ref float x, ref float y, float width, float lineHeight)
    {
        var connectionColor = GetConnectionStateColor();
        var connectionStyle = new GUIStyle(textStyle) { normal = { textColor = connectionColor } };

        GUI.Label(new Rect(x, y, width, lineHeight), $"Status: {FireNetwork.connectionState}", connectionStyle);
        y += lineHeight;

        if (averagePing > 0)
        {
            var pingColor = GetPingColor(averagePing);
            var pingStyle = new GUIStyle(textStyle) { normal = { textColor = pingColor } };
            GUI.Label(new Rect(x, y, width, lineHeight), $"Ping: {averagePing:F1}ms", pingStyle);
            y += lineHeight;
        }
    }

    private void DrawRoomInfo(ref float x, ref float y, float width, float lineHeight)
    {
        GUI.Label(new Rect(x, y, width, lineHeight), $"Room: {FireNetwork.currentRoom}", textStyle);
        y += lineHeight;

        GUI.Label(new Rect(x, y, width, lineHeight), $"Players: {FireNetwork.playerCountInRoom}", textStyle);
        y += lineHeight;

        var masterStyle = FireNetwork.isMasterClient ?
            new GUIStyle(textStyle) { normal = { textColor = Color.green } } : textStyle;
        GUI.Label(new Rect(x, y, width, lineHeight), $"Master: {FireNetwork.isMasterClient}", masterStyle);
        y += lineHeight;

        if (activeNetworkObjects > 0)
        {
            GUI.Label(new Rect(x, y, width, lineHeight), $"Network Objects: {activeNetworkObjects}", textStyle);
            y += lineHeight;
        }
    }

    private void DrawNetworkTraffic(ref float x, ref float y, float width, float lineHeight)
    {
        // Bandwidth
        string outBandwidth = FormatBytes(bytesSentPerSecond);
        string inBandwidth = FormatBytes(bytesReceivedPerSecond);

        GUI.Label(new Rect(x, y, width, lineHeight), $"Out: {outBandwidth}/s", textStyle);
        y += lineHeight;
        GUI.Label(new Rect(x, y, width, lineHeight), $"In: {inBandwidth}/s", textStyle);
        y += lineHeight;

        // Messages/RPCs
        GUI.Label(new Rect(x, y, width, lineHeight), $"Msgs Out: {messagesSentPerSecond}/s", textStyle);
        y += lineHeight;
        GUI.Label(new Rect(x, y, width, lineHeight), $"Msgs In: {messagesReceivedPerSecond}/s", textStyle);
        y += lineHeight;

        if (rpcsSentPerSecond > 0 || rpcsReceivedPerSecond > 0)
        {
            GUI.Label(new Rect(x, y, width, lineHeight), $"RPCs Out: {rpcsSentPerSecond}/s", textStyle);
            y += lineHeight;
            GUI.Label(new Rect(x, y, width, lineHeight), $"RPCs In: {rpcsReceivedPerSecond}/s", textStyle);
            y += lineHeight;
        }
    }

    private void DrawPerformanceInfo(ref float x, ref float y, float width, float lineHeight)
    {
        GUI.Label(new Rect(x, y, width, lineHeight), "Performance:", headerStyle);
        y += lineHeight;

        // FPS and frame time
        float avgFrameTime = frameTimeHistory.Count > 0 ? frameTimeHistory.Average() : 0f;
        float currentFPS = 1f / Time.deltaTime;

        var fpsColor = GetFPSColor(currentFPS);
        var fpsStyle = new GUIStyle(textStyle) { normal = { textColor = fpsColor } };

        GUI.Label(new Rect(x, y, width, lineHeight), $"FPS: {currentFPS:F1} ({avgFrameTime:F1}ms)", fpsStyle);
        y += lineHeight;

        // Memory usage
        var memoryColor = GetMemoryColor(memoryUsageMB);
        var memoryStyle = new GUIStyle(textStyle) { normal = { textColor = memoryColor } };
        GUI.Label(new Rect(x, y, width, lineHeight), $"Memory: {memoryUsageMB:F1} MB", memoryStyle);
        y += lineHeight;

        // Unity-specific stats
        GUI.Label(new Rect(x, y, width, lineHeight), $"Draw Calls: {UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(null) / 1024 / 1024}", textStyle);
        y += lineHeight;
    }

    private void DrawFirebaseStats(ref float x, ref float y, float width, float lineHeight)
    {
        GUI.Label(new Rect(x, y, width, lineHeight), "Firebase:", headerStyle);
        y += lineHeight;

        GUI.Label(new Rect(x, y, width, lineHeight), $"Operations: {firebaseOperationsPerSecond}/s", textStyle);
        y += lineHeight;

        if (failedOperationsPerSecond > 0)
        {
            GUI.Label(new Rect(x, y, width, lineHeight), $"Failures: {failedOperationsPerSecond}/s", errorStyle);
            y += lineHeight;
        }

        // Get async worker stats if available
        if (FireNetwork.Instance != null)
        {
            try
            {
                var networkStats = FireNetwork.GetNetworkStats();
                GUI.Label(new Rect(x, y, width, lineHeight), $"RPC Queue: {networkStats.QueuedRpcs}", textStyle);
                y += lineHeight;
            }
            catch
            {
                // Ignore if stats not available
            }
        }

        // Connection quality indicator
        float successRate = firebaseOperationsPerSecond > 0 ?
            (float)(firebaseOperationsPerSecond - failedOperationsPerSecond) / firebaseOperationsPerSecond : 1f;

        var qualityColor = GetQualityColor(successRate);
        var qualityStyle = new GUIStyle(textStyle) { normal = { textColor = qualityColor } };
        string qualityText = GetQualityText(successRate);

        GUI.Label(new Rect(x, y, width, lineHeight), $"Quality: {qualityText} ({successRate:P0})", qualityStyle);
        y += lineHeight;
    }

    private float GetPanelHeight()
    {
        float baseHeight = 100f; // Header + connection info

        if (FireNetwork.inRoom)
        {
            baseHeight += 120f; // Room info + network traffic

            if (showDetailedStats)
            {
                baseHeight += 140f; // Performance + Firebase stats
            }
        }

        return baseHeight;
    }

    private Color GetConnectionStateColor()
    {
        return FireNetwork.connectionState switch
        {
            ConnectionState.ConnectedToRoom => Color.green,
            ConnectionState.ConnectedToMaster => Color.yellow,
            ConnectionState.Connecting => Color.cyan,
            ConnectionState.JoiningRoom => Color.cyan,
            ConnectionState.Disconnecting => orange,
            _ => Color.red
        };
    }

    private Color GetPingColor(float ping)
    {
        if (ping < 50f) return Color.green;
        if (ping < 100f) return Color.yellow;
        if (ping < 200f) return orange;
        return Color.red;
    }

    private Color GetFPSColor(float fps)
    {
        if (fps >= 55f) return Color.green;
        if (fps >= 40f) return Color.yellow;
        if (fps >= 25f) return orange;
        return Color.red;
    }

    private Color GetMemoryColor(float memoryMB)
    {
        if (memoryMB < 100f) return Color.green;
        if (memoryMB < 200f) return Color.yellow;
        if (memoryMB < 400f) return orange;
        return Color.red;
    }

    private Color GetQualityColor(float successRate)
    {
        if (successRate >= 0.95f) return Color.green;
        if (successRate >= 0.90f) return Color.yellow;
        if (successRate >= 0.80f) return orange;
        return Color.red;
    }

    private string GetQualityText(float successRate)
    {
        if (successRate >= 0.95f) return "Excellent";
        if (successRate >= 0.90f) return "Good";
        if (successRate >= 0.80f) return "Fair";
        if (successRate >= 0.60f) return "Poor";
        return "Critical";
    }

    private string FormatBytes(int bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024f:F1} KB";
        return $"{bytes} B";
    }

    // Advanced statistics for debugging
    public static NetworkStatsData GetAdvancedStats()
    {
        var instance = FindObjectOfType<NetworkStats>();
        if (instance == null) return default;

        return new NetworkStatsData
        {
            BytesSentPerSecond = bytesSentPerSecond,
            BytesReceivedPerSecond = bytesReceivedPerSecond,
            MessagesSentPerSecond = messagesSentPerSecond,
            MessagesReceivedPerSecond = messagesReceivedPerSecond,
            RpcsSentPerSecond = rpcsSentPerSecond,
            RpcsReceivedPerSecond = rpcsReceivedPerSecond,
            AveragePing = averagePing,
            FirebaseOperationsPerSecond = firebaseOperationsPerSecond,
            FailedOperationsPerSecond = failedOperationsPerSecond,
            MemoryUsageMB = memoryUsageMB,
            ActiveNetworkObjects = activeNetworkObjects,
            AverageFrameTimeMs = instance.frameTimeHistory.Count > 0 ? instance.frameTimeHistory.Average() : 0f,
            CurrentFPS = 1f / Time.deltaTime
        };
    }

    // Export stats to file for analysis
    public void ExportStatsToFile(string filename = null)
    {
        if (filename == null)
        {
            filename = $"NetworkStats_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
        }

        var stats = GetAdvancedStats();
        var networkStats = FireNetwork.GetNetworkStats();

        var report = new System.Text.StringBuilder();
        report.AppendLine("=== NETWORK STATISTICS REPORT ===");
        report.AppendLine($"Timestamp: {System.DateTime.Now}");
        report.AppendLine($"Connection State: {FireNetwork.connectionState}");
        report.AppendLine($"Room: {FireNetwork.currentRoom}");
        report.AppendLine($"Players: {FireNetwork.playerCountInRoom}");
        report.AppendLine($"Is Master Client: {FireNetwork.isMasterClient}");
        report.AppendLine();

        report.AppendLine("=== NETWORK TRAFFIC ===");
        report.AppendLine($"Bytes Sent/s: {stats.BytesSentPerSecond}");
        report.AppendLine($"Bytes Received/s: {stats.BytesReceivedPerSecond}");
        report.AppendLine($"Messages Sent/s: {stats.MessagesSentPerSecond}");
        report.AppendLine($"Messages Received/s: {stats.MessagesReceivedPerSecond}");
        report.AppendLine($"RPCs Sent/s: {stats.RpcsSentPerSecond}");
        report.AppendLine($"RPCs Received/s: {stats.RpcsReceivedPerSecond}");
        report.AppendLine($"Average Ping: {stats.AveragePing:F1}ms");
        report.AppendLine();

        report.AppendLine("=== PERFORMANCE ===");
        report.AppendLine($"FPS: {stats.CurrentFPS:F1}");
        report.AppendLine($"Average Frame Time: {stats.AverageFrameTimeMs:F1}ms");
        report.AppendLine($"Memory Usage: {stats.MemoryUsageMB:F1} MB");
        report.AppendLine($"Active Network Objects: {stats.ActiveNetworkObjects}");
        report.AppendLine();

        report.AppendLine("=== FIREBASE ===");
        report.AppendLine($"Operations/s: {stats.FirebaseOperationsPerSecond}");
        report.AppendLine($"Failed Operations/s: {stats.FailedOperationsPerSecond}");
        report.AppendLine($"Success Rate: {(stats.FirebaseOperationsPerSecond > 0 ? (float)(stats.FirebaseOperationsPerSecond - stats.FailedOperationsPerSecond) / stats.FirebaseOperationsPerSecond : 1f):P1}");
        report.AppendLine($"RPC Queue Size: {networkStats.QueuedRpcs}");

        try
        {
            System.IO.File.WriteAllText(filename, report.ToString());
            Debug.Log($"Network stats exported to: {filename}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to export stats: {e}");
        }
    }
}

[System.Serializable]
public struct NetworkStatsData
{
    public int BytesSentPerSecond;
    public int BytesReceivedPerSecond;
    public int MessagesSentPerSecond;
    public int MessagesReceivedPerSecond;
    public int RpcsSentPerSecond;
    public int RpcsReceivedPerSecond;
    public float AveragePing;
    public float FirebaseOperationsPerSecond;
    public int FailedOperationsPerSecond;
    public float MemoryUsageMB;
    public int ActiveNetworkObjects;
    public float AverageFrameTimeMs;
    public float CurrentFPS;
}