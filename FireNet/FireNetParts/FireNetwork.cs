using Firebase;
using Firebase.Auth;
using Firebase.Database;
using Firebase.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Text;

public class FireNetwork : MonoBehaviour
{
    public static FireNetwork Instance { get; private set; }

    [Header("Settings")]
    public bool autoConnect = true;
    public int maxPlayersPerRoom = 4;
    public string gameVersion = "1.0";
    public string firebaseUrl = "https://put-YOUR-realtime-database-URL-here-diddy-blud-default-rtdb.firebaseio.com/";

    [Header("Performance")]
    public float playerSyncInterval = 2f;
    public float rpcPollInterval = 0.1f;
    public int maxRpcBatchSize = 20;

    // Events
    public static event System.Action OnConnectedToMaster;
    public static event System.Action<string> OnJoinedRoom;
    public static event System.Action OnLeftRoom;
    public static event System.Action<NetPlayer> OnPlayerEnteredRoom;
    public static event System.Action<NetPlayer> OnPlayerLeftRoom;
    public static event System.Action<string> OnJoinRoomFailed;
    public static event System.Action OnDisconnected;

    // State
    public static ConnectionState connectionState { get; private set; } = ConnectionState.Disconnected;
    public static string currentRoom { get; private set; }
    public static NetPlayer localPlayer { get; private set; }
    public static Dictionary<string, NetPlayer> playerList { get; private set; } = new Dictionary<string, NetPlayer>();
    public static bool isMasterClient => localPlayer?.isMasterClient ?? false;
    public static bool inRoom => connectionState == ConnectionState.ConnectedToRoom;
    public static int playerCountInRoom => playerList.Count;

    // Firebase
    public FirebaseAuth auth;
    public DatabaseReference database;
    public FirebaseUser user;

    // Network objects
    private Dictionary<string, INetworkBehaviour> networkObjects = new Dictionary<string, INetworkBehaviour>();

    // RPC system
    private Queue<RpcData> incomingRpcQueue = new Queue<RpcData>();
    private long lastRpcTimestamp = 0;

    // References
    private DatabaseReference roomPlayersRef;
    private DatabaseReference roomRpcRef;
    private DatabaseReference roomRef;

    // Async worker
    private OptimizedFirebaseAsyncWorker asyncWorker;
    private CancellationTokenSource cancellationTokenSource;

    private struct RpcData
    {
        public string methodName;
        public string senderId;
        public string targetId;
        public object[] parameters;
        public long timestamp;
        public RpcTarget target;
        public bool buffered;
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        if (autoConnect) ConnectToMaster();
    }

    private void Update()
    {
        ProcessIncomingRpcs();
    }

    private void ProcessIncomingRpcs()
    {
        if (!inRoom) return;

        int processed = 0;
        while (processed < maxRpcBatchSize && incomingRpcQueue.Count > 0)
        {
            var rpcData = incomingRpcQueue.Dequeue();

            if (string.IsNullOrEmpty(rpcData.targetId) || rpcData.targetId == localPlayer.userId)
            {
                ExecuteRPC(rpcData.methodName, rpcData.parameters);
            }
            processed++;
        }
    }

    private void OnApplicationQuit()
    {
        if (inRoom)
        {
            LeaveRoomImmediate();
        }
    }

    // Connection Management
    public static void ConnectToMaster()
    {
        if (connectionState != ConnectionState.Disconnected) return;
        connectionState = ConnectionState.Connecting;
        Instance.StartCoroutine(Instance.InitializeFirebase());
    }

    public static void Disconnect()
    {
        Instance?.LeaveRoomImmediate();
        connectionState = ConnectionState.Disconnected;
        OnDisconnected?.Invoke();
    }

    private IEnumerator InitializeFirebase()
    {
        var dependencyTask = FirebaseApp.CheckAndFixDependenciesAsync();
        yield return new WaitUntil(() => dependencyTask.IsCompleted);

        if (dependencyTask.Result == DependencyStatus.Available)
        {
            FirebaseApp app = FirebaseApp.DefaultInstance;
            auth = FirebaseAuth.DefaultInstance;
            database = FirebaseDatabase.GetInstance(app, firebaseUrl).RootReference;

            cancellationTokenSource = new CancellationTokenSource();
            asyncWorker = new OptimizedFirebaseAsyncWorker(cancellationTokenSource.Token);

            yield return StartCoroutine(AuthenticateUser());
        }
        else
        {
            connectionState = ConnectionState.Disconnected;
            Debug.LogError($"Firebase dependency error: {dependencyTask.Result}");
        }
    }

    private IEnumerator AuthenticateUser()
    {
        string email = GenerateUniqueEmail();
        string password = GenerateSecurePassword();

        var createTask = auth.CreateUserWithEmailAndPasswordAsync(email, password);
        yield return new WaitUntil(() => createTask.IsCompleted);

        if (createTask.IsCompletedSuccessfully)
        {
            user = createTask.Result.User;
            localPlayer = new NetPlayer(user.UserId, $"Player_{user.UserId.Substring(0, 6)}", false);
            connectionState = ConnectionState.ConnectedToMaster;
            OnConnectedToMaster?.Invoke();
            Debug.Log("Connected to Firebase");
        }
        else
        {
            connectionState = ConnectionState.Disconnected;
            Debug.LogError("Authentication failed");
        }
    }

    private string GenerateUniqueEmail()
    {
        string deviceId = SystemInfo.deviceUniqueIdentifier;
        string timestamp = System.DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        return $"user_{deviceId.Substring(0, 8)}_{timestamp}@temp.org";
    }

    private string GenerateSecurePassword()
    {
        return System.Guid.NewGuid().ToString("N");
    }

    // Room Management
    public static void CreateRoom(string roomName, RoomOptions options = null)
    {
        if (connectionState != ConnectionState.ConnectedToMaster) return;
        Instance.StartCoroutine(Instance.CreateRoomCoroutine(roomName, options ?? new RoomOptions()));
    }

    public static void JoinRoom(string roomName)
    {
        if (connectionState != ConnectionState.ConnectedToMaster) return;
        Instance.StartCoroutine(Instance.JoinRoomCoroutine(roomName));
    }

    public static void JoinOrCreateRoom(string roomName, RoomOptions options = null)
    {
        if (connectionState != ConnectionState.ConnectedToMaster) return;
        Instance.StartCoroutine(Instance.JoinOrCreateRoomCoroutine(roomName, options ?? new RoomOptions()));
    }

    public static void JoinRandomRoom()
    {
        if (connectionState != ConnectionState.ConnectedToMaster) return;
        Instance.StartCoroutine(Instance.JoinRandomRoomCoroutine());
    }

    public static void LeaveRoom()
    {
        if (connectionState != ConnectionState.ConnectedToRoom) return;
        Instance.LeaveRoomImmediate();
    }

    private IEnumerator CreateRoomCoroutine(string roomName, RoomOptions options)
    {
        connectionState = ConnectionState.JoiningRoom;

        localPlayer.isMasterClient = true;
        var roomData = new Dictionary<string, object>
        {
            ["name"] = roomName,
            ["masterClientId"] = localPlayer.userId,
            ["maxPlayers"] = options.maxPlayers,
            ["playerCount"] = 0,
            ["isOpen"] = options.isOpen,
            ["isVisible"] = options.isVisible,
            ["gameVersion"] = gameVersion
        };

        bool success = false;
        asyncWorker.EnqueueOperation(async () =>
        {
            try
            {
                await database.Child($"rooms/{roomName}").SetValueAsync(roomData);
                success = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to create room: {e}");
            }
        });

        yield return new WaitUntil(() => success);
        yield return StartCoroutine(JoinRoomCoroutine(roomName));
    }

    private IEnumerator JoinRoomCoroutine(string roomName)
    {
        connectionState = ConnectionState.JoiningRoom;

        bool success = false;
        asyncWorker.EnqueueOperation(async () =>
        {
            try
            {
                var playerData = localPlayer.ToDict();
                await database.Child($"rooms/{roomName}/players/{localPlayer.userId}").SetValueAsync(playerData);
                success = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to join room: {e}");
            }
        });

        yield return new WaitUntil(() => success);

        if (success)
        {
            currentRoom = roomName;
            connectionState = ConnectionState.ConnectedToRoom;

            roomRef = database.Child($"rooms/{roomName}");
            roomPlayersRef = roomRef.Child("players");
            roomRpcRef = roomRef.Child("rpc");

            StartCoroutine(PlayerSyncLoop());
            StartCoroutine(RpcPollingLoop());

            OnJoinedRoom?.Invoke(roomName);
        }
        else
        {
            OnJoinRoomFailed?.Invoke("Failed to join room");
            connectionState = ConnectionState.ConnectedToMaster;
        }
    }

    private IEnumerator JoinOrCreateRoomCoroutine(string roomName, RoomOptions options)
    {
        // First try to join the room
        yield return StartCoroutine(JoinRoomCoroutine(roomName));

        // If joining failed, try to create it
        if (connectionState != ConnectionState.ConnectedToRoom)
        {
            connectionState = ConnectionState.ConnectedToMaster; // Reset state for creation attempt
            yield return StartCoroutine(CreateRoomCoroutine(roomName, options));
        }
    }

    private IEnumerator JoinRandomRoomCoroutine()
    {
        List<string> availableRooms = null;
        bool searchComplete = false;

        asyncWorker.EnqueueOperation(async () =>
        {
            try
            {
                var snapshot = await database.Child("rooms").GetValueAsync();
                if (snapshot.Exists && snapshot.ChildrenCount > 0)
                {
                    availableRooms = new List<string>();

                    foreach (var child in snapshot.Children)
                    {
                        var roomData = child.Value as Dictionary<string, object>;
                        if (roomData == null) continue;

                        var isOpen = roomData.GetValueOrDefault("isOpen", "true") == "true";
                        var playerCount = int.Parse(roomData.GetValueOrDefault("playerCount", "0"));
                        var maxPlayers = int.Parse(roomData.GetValueOrDefault("maxPlayers", maxPlayersPerRoom.ToString()));

                        if (isOpen && playerCount < maxPlayers)
                        {
                            availableRooms.Add(child.Key);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to search rooms: {e}");
            }
            searchComplete = true;
        });

        yield return new WaitUntil(() => searchComplete);

        if (availableRooms?.Count > 0)
        {
            string randomRoom = availableRooms[UnityEngine.Random.Range(0, availableRooms.Count)];
            yield return StartCoroutine(JoinRoomCoroutine(randomRoom));
        }
        else
        {
            OnJoinRoomFailed?.Invoke("No available rooms");
            connectionState = ConnectionState.ConnectedToMaster;
        }
    }

    private void LeaveRoomImmediate()
    {
        if (!inRoom) return;

        string roomToLeave = currentRoom;
        currentRoom = null;

        asyncWorker?.EnqueueOperation(async () =>
        {
            try
            {
                await database.Child($"rooms/{roomToLeave}/players/{localPlayer.userId}").RemoveValueAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to clean up on leave: {e}");
            }
        });

        CleanupLocalState();
    }

    private void CleanupLocalState()
    {
        playerList.Clear();
        networkObjects.Clear();
        incomingRpcQueue.Clear();

        roomRef = null;
        roomPlayersRef = null;
        roomRpcRef = null;
        lastRpcTimestamp = 0;

        if (localPlayer != null)
            localPlayer.isMasterClient = false;

        connectionState = ConnectionState.ConnectedToMaster;
        OnLeftRoom?.Invoke();
    }

    // Network Sync
    private IEnumerator PlayerSyncLoop()
    {
        var wait = new WaitForSeconds(playerSyncInterval);

        while (inRoom)
        {
            asyncWorker?.EnqueueOperation(async () =>
            {
                try
                {
                    var snapshot = await roomPlayersRef.GetValueAsync();
                    if (!snapshot.Exists) return;

                    var newPlayers = new Dictionary<string, NetPlayer>();
                    var joinedPlayers = new List<NetPlayer>();
                    var leftPlayers = new List<NetPlayer>();

                    foreach (var child in snapshot.Children)
                    {
                        var player = NetPlayer.FromSnapshot(child);
                        if (player != null)
                        {
                            newPlayers[player.userId] = player;
                            if (!playerList.ContainsKey(player.userId))
                            {
                                joinedPlayers.Add(player);
                            }
                        }
                    }

                    foreach (var existingPlayer in playerList.Values)
                    {
                        if (!newPlayers.ContainsKey(existingPlayer.userId))
                        {
                            leftPlayers.Add(existingPlayer);
                        }
                    }

                    // Update on main thread
                    if (joinedPlayers.Count > 0 || leftPlayers.Count > 0)
                    {
                        UnityMainThreadDispatcher.Instance().Enqueue(() =>
                        {
                            playerList = newPlayers;

                            foreach (var player in joinedPlayers)
                            {
                                if (player.userId != localPlayer?.userId)
                                    OnPlayerEnteredRoom?.Invoke(player);
                            }

                            foreach (var player in leftPlayers)
                            {
                                OnPlayerLeftRoom?.Invoke(player);
                            }

                            // Update local player master status
                            if (localPlayer != null && newPlayers.ContainsKey(localPlayer.userId))
                            {
                                localPlayer.isMasterClient = newPlayers[localPlayer.userId].isMasterClient;
                            }
                        });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Player sync failed: {e}");
                }
            });

            yield return wait;
        }
    }

    private IEnumerator RpcPollingLoop()
    {
        var wait = new WaitForSeconds(rpcPollInterval);

        while (inRoom)
        {
            asyncWorker?.EnqueueOperation(async () =>
            {
                try
                {
                    var snapshot = await roomRpcRef
                        .OrderByChild("timestamp")
                        .StartAt(lastRpcTimestamp + 1)
                        .LimitToFirst(maxRpcBatchSize)
                        .GetValueAsync();

                    if (!snapshot.Exists) return;

                    var rpcsToProcess = new List<RpcData>();
                    long maxTimestamp = lastRpcTimestamp;

                    foreach (var child in snapshot.Children)
                    {
                        var rpcData = child.Value as Dictionary<string, object>;
                        if (rpcData == null) continue;

                        string senderId = rpcData["senderId"].ToString();
                        if (senderId == localPlayer.userId) continue;

                        // Deserialize parameters using our serialization system
                        object[] parameters = Array.Empty<object>();
                        if (rpcData.ContainsKey("params") && rpcData["params"] is string serializedParams)
                        {
                            parameters = NetworkSerializer.DeserializeParameters(serializedParams);
                        }

                        var rpc = new RpcData
                        {
                            methodName = rpcData["method"].ToString(),
                            senderId = senderId,
                            targetId = rpcData.GetValueOrDefault("targetId", ""),
                            parameters = parameters,
                            timestamp = Convert.ToInt64(rpcData["timestamp"]),
                            target = (RpcTarget)Enum.Parse(typeof(RpcTarget), rpcData["target"].ToString()),
                            buffered = Convert.ToBoolean(rpcData.GetValueOrDefault("buffered", false))
                        };

                        rpcsToProcess.Add(rpc);
                        maxTimestamp = Math.Max(maxTimestamp, rpc.timestamp);

                        // Clean up non-buffered RPCs
                        if (!rpc.buffered && isMasterClient)
                        {
                            _ = child.Reference.RemoveValueAsync();
                        }
                    }

                    lastRpcTimestamp = maxTimestamp;

                    // Queue RPCs for main thread processing
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        foreach (var rpc in rpcsToProcess)
                        {
                            incomingRpcQueue.Enqueue(rpc);
                        }
                    });
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"RPC polling failed: {e}");
                }
            });

            yield return wait;
        }
    }

    // RPC System
    public static void RPC(string methodName, RpcTarget target, params object[] parameters)
    {
        if (!inRoom) return;

        bool isBuffered = target == RpcTarget.AllBuffered || target == RpcTarget.OthersBuffered;
        string targetId = "";

        if (target == RpcTarget.Host)
        {
            var master = playerList.Values.FirstOrDefault(p => p.isMasterClient);
            targetId = master?.userId ?? "";
        }

        // Serialize parameters for efficient transfer
        string serializedParams = NetworkSerializer.SerializeParameters(parameters);

        var rpcData = new Dictionary<string, object>
        {
            ["method"] = methodName,
            ["senderId"] = localPlayer.userId,
            ["params"] = serializedParams,
            ["timestamp"] = ServerValue.Timestamp,
            ["target"] = target.ToString(),
            ["targetId"] = targetId,
            ["buffered"] = isBuffered
        };

        var rpcRef = Instance.roomRpcRef.Push();
        Instance.asyncWorker?.EnqueueOperation(async () =>
        {
            await rpcRef.SetValueAsync(rpcData);
        });

        // Execute locally if needed
        if (target == RpcTarget.All || target == RpcTarget.AllBuffered)
        {
            ExecuteRPC(methodName, parameters);
        }
    }

    private static void ExecuteRPC(string methodName, object[] parameters)
    {
        foreach (var networkBehaviour in Instance.networkObjects.Values)
        {
            try
            {
                networkBehaviour.OnRPC(methodName, parameters);
            }
            catch (Exception e)
            {
                Debug.LogError($"RPC execution failed for {methodName}: {e}");
            }
        }
    }

    // Network Object Management
    public static GameObject NetInstantiate(GameObject prefab, Vector3 position, Quaternion rotation, byte group = 0, object[] data = null)
    {
        if (!inRoom) return null;

        string networkId = System.Guid.NewGuid().ToString();
        var instance = UnityEngine.Object.Instantiate(prefab, position, rotation);

        var networkView = instance.GetComponent<NetworkView>();
        if (networkView == null)
            networkView = instance.AddComponent<NetworkView>();

        networkView.viewId = networkId;
        networkView.isMine = true;
        RegisterNetworkObject(networkView);

        RPC("OnInstantiate", RpcTarget.Others, prefab.name, position.x, position.y, position.z,
            rotation.x, rotation.y, rotation.z, rotation.w, networkId, group, data);

        return instance;
    }

    public static void NetDestroy(GameObject target)
    {
        var networkView = target.GetComponent<NetworkView>();
        if (networkView != null && networkView.isMine)
        {
            RPC("OnDestroy", RpcTarget.Others, networkView.viewId);
            UnregisterNetworkObject(networkView.viewId);
        }
        UnityEngine.Object.Destroy(target);
    }

    public static void RegisterNetworkObject(INetworkBehaviour networkBehaviour)
    {
        if (!string.IsNullOrEmpty(networkBehaviour.NetworkId))
        {
            Instance.networkObjects[networkBehaviour.NetworkId] = networkBehaviour;
        }
    }

    public static void UnregisterNetworkObject(string networkId)
    {
        Instance?.networkObjects.Remove(networkId);
    }

    // Utility
    public static void SetCustomPlayerProperties(Dictionary<string, object> properties)
    {
        if (localPlayer != null && properties != null && inRoom)
        {
            foreach (var prop in properties)
            {
                localPlayer.customProperties[prop.Key] = prop.Value;
            }

            var updates = new Dictionary<string, object>();
            foreach (var prop in properties)
            {
                updates[$"customProperties/{prop.Key}"] = prop.Value;
            }

            Instance.asyncWorker?.EnqueueOperation(async () =>
            {
                await Instance.roomPlayersRef.Child(localPlayer.userId).UpdateChildrenAsync(updates);
            });
        }
    }
}

// Network Serialization System
public static class NetworkSerializer
{
    private const char SEPARATOR = '|';
    private const char TYPE_SEPARATOR = ':';

    public static string SerializeParameters(object[] parameters)
    {
        if (parameters == null || parameters.Length == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) sb.Append(SEPARATOR);

            var param = parameters[i];
            if (param == null)
            {
                sb.Append("null:");
            }
            else
            {
                string typeName = param.GetType().Name;
                string value = SerializeValue(param);
                sb.Append($"{typeName}{TYPE_SEPARATOR}{value}");
            }
        }
        return sb.ToString();
    }

    public static object[] DeserializeParameters(string serializedData)
    {
        if (string.IsNullOrEmpty(serializedData))
            return Array.Empty<object>();

        var parts = serializedData.Split(SEPARATOR);
        var parameters = new object[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == "null:")
            {
                parameters[i] = null;
                continue;
            }

            int typeIndex = part.IndexOf(TYPE_SEPARATOR);
            if (typeIndex == -1) continue;

            string typeName = part.Substring(0, typeIndex);
            string value = part.Substring(typeIndex + 1);

            parameters[i] = DeserializeValue(typeName, value);
        }

        return parameters;
    }

    private static string SerializeValue(object value)
    {
        switch (value)
        {
            case Vector3 v3:
                return $"{v3.x},{v3.y},{v3.z}";
            case Vector2 v2:
                return $"{v2.x},{v2.y}";
            case Quaternion q:
                return $"{q.x},{q.y},{q.z},{q.w}";
            case Color c:
                return $"{c.r},{c.g},{c.b},{c.a}";
            case bool b:
                return b ? "1" : "0";
            case float f:
                return f.ToString("G9");
            case double d:
                return d.ToString("G17");
            default:
                return value.ToString();
        }
    }

    private static object DeserializeValue(string typeName, string value)
    {
        try
        {
            switch (typeName)
            {
                case "Vector3":
                    var v3Parts = value.Split(',');
                    return new Vector3(float.Parse(v3Parts[0]), float.Parse(v3Parts[1]), float.Parse(v3Parts[2]));

                case "Vector2":
                    var v2Parts = value.Split(',');
                    return new Vector2(float.Parse(v2Parts[0]), float.Parse(v2Parts[1]));

                case "Quaternion":
                    var qParts = value.Split(',');
                    return new Quaternion(float.Parse(qParts[0]), float.Parse(qParts[1]), float.Parse(qParts[2]), float.Parse(qParts[3]));

                case "Color":
                    var cParts = value.Split(',');
                    return new Color(float.Parse(cParts[0]), float.Parse(cParts[1]), float.Parse(cParts[2]), float.Parse(cParts[3]));

                case "Boolean":
                    return value == "1";

                case "Single":
                    return float.Parse(value);

                case "Double":
                    return double.Parse(value);

                case "Int32":
                    return int.Parse(value);

                case "Int64":
                    return long.Parse(value);

                case "String":
                    return value;

                default:
                    return value;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to deserialize {typeName} value '{value}': {e}");
            return null;
        }
    }
}

// Main Thread Dispatcher
public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher _instance;
    private readonly Queue<System.Action> _actions = new Queue<System.Action>();

    public static UnityMainThreadDispatcher Instance()
    {
        if (_instance == null)
        {
            var go = new GameObject("MainThreadDispatcher");
            _instance = go.AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }
        return _instance;
    }

    public void Enqueue(System.Action action)
    {
        lock (_actions)
        {
            _actions.Enqueue(action);
        }
    }

    private void Update()
    {
        lock (_actions)
        {
            while (_actions.Count > 0)
            {
                try
                {
                    _actions.Dequeue()?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"MainThreadDispatcher action failed: {e}");
                }
            }
        }
    }
}

// Extension methods
public static class DictionaryExtensions
{
    public static string GetValueOrDefault(this Dictionary<string, object> dictionary, string key, string defaultValue = "")
    {
        return dictionary.TryGetValue(key, out object value) ? value?.ToString() ?? defaultValue : defaultValue;
    }
}

public static class ServerValue
{
    public static Dictionary<string, object> Timestamp => new Dictionary<string, object> { [".sv"] = "timestamp" };
}