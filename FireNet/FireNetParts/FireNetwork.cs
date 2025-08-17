using Firebase;
using Firebase.Auth;
using Firebase.Database;
using Firebase.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class FireNetwork : MonoBehaviour
{
    public static FireNetwork Instance { get; private set; }

    [Header("Settings")]
    public bool autoConnect = true;
    public int maxPlayersPerRoom = 4;
    public string gameVersion = "1.0";
    public string firebaseUrl = "https://put-YOUR-realtime-database-URL-here-diddy-blud-default-rtdb.firebaseio.com/";

    [Header("Performance - Optimized")]
    public float playerSyncInterval = 3f; // Increased from 2f to reduce calls
    public float rpcPollInterval = 0.15f; // Slightly increased for better batching
    public int maxRpcBatchSize = 30; // Increased batch size

    [Header("Cleanup Settings - Optimized")]
    public float rpcCleanupInterval = 20f; // More frequent cleanup
    public float rpcMaxAge = 30f; // Shorter retention time
    public bool autoCleanupEmptyRooms = true;
    public int maxRpcQueueSize = 500; // Prevent memory overflow

    public static event System.Action OnConnectedToMaster;
    public static event System.Action<string> OnJoinedRoom;
    public static event System.Action OnLeftRoom;
    public static event System.Action<NetPlayer> OnPlayerEnteredRoom;
    public static event System.Action<NetPlayer> OnPlayerLeftRoom;
    public static event System.Action<string> OnJoinRoomFailed;
    public static event System.Action OnDisconnected;

    public static ConnectionState connectionState { get; private set; } = ConnectionState.Disconnected;
    public static string currentRoom { get; private set; }
    public static NetPlayer localPlayer { get; private set; }
    public static Dictionary<string, NetPlayer> playerList { get; private set; } = new Dictionary<string, NetPlayer>();
    public static bool isMasterClient => localPlayer?.isMasterClient ?? false;
    public static bool inRoom => connectionState == ConnectionState.ConnectedToRoom;
    public static int playerCountInRoom => playerList.Count;

    public FirebaseAuth auth;
    public DatabaseReference database;
    public FirebaseUser user;

    // Device security
    private string deviceId;
    private string hashedDeviceId;
    private DeviceAuthData deviceAuthData;
    private DatabaseReference deviceAuthRef;

    private Dictionary<string, INetworkBehaviour> networkObjects = new Dictionary<string, INetworkBehaviour>();
    private Queue<RpcData> incomingRpcQueue = new Queue<RpcData>();
    private long lastRpcTimestamp = 0;
    private float lastRpcCleanup = 0;

    private DatabaseReference roomPlayersRef;
    private DatabaseReference roomRpcRef;
    private DatabaseReference roomRef;
    private DatabaseReference playerPresenceRef;

    private OptimizedFirebaseAsyncWorker asyncWorker;
    private CancellationTokenSource cancellationTokenSource;
    private bool hasSetupDisconnectHandlers = false;

    // Coroutine references for proper cleanup
    private Coroutine playerSyncCoroutine;
    private Coroutine rpcPollingCoroutine;
    private Coroutine rpcCleanupCoroutine;

    private bool isCleaningUp = false;
    private bool applicationQuitting = false;

    // Performance tracking
    private int rpcsSentThisSecond = 0;
    private float lastRpcCountReset = 0;

    // Object pooling for reduced allocations
    private static readonly StringBuilder stringBuilder = new StringBuilder(256);
    private readonly Dictionary<string, object> reusableDict = new Dictionary<string, object>();

    private struct RpcData
    {
        public string methodName;
        public string senderId;
        public string targetId;
        public object[] parameters;
        public long timestamp;
        public RpcTarget target;
        public bool buffered;
        public string rpcId;
    }

    [System.Serializable]
    private class DeviceAuthData
    {
        public string hashedDeviceId;
        public string encryptedEmail;
        public string encryptedPassword;
        public string encryptedPlayerName;
        public string userId;
        public long createdAt;
        public long lastLoginAt;

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                ["hashedDeviceId"] = hashedDeviceId ?? "",
                ["encryptedEmail"] = encryptedEmail ?? "",
                ["encryptedPassword"] = encryptedPassword ?? "",
                ["encryptedPlayerName"] = encryptedPlayerName ?? "",
                ["userId"] = userId ?? "",
                ["createdAt"] = createdAt,
                ["lastLoginAt"] = ServerValue.Timestamp
            };
        }

        public static DeviceAuthData FromSnapshot(DataSnapshot snapshot)
        {
            if (!snapshot.Exists) return null;

            var data = snapshot.Value as Dictionary<string, object>;
            if (data == null) return null;

            return new DeviceAuthData
            {
                hashedDeviceId = data.GetValueOrDefault("hashedDeviceId"),
                encryptedEmail = data.GetValueOrDefault("encryptedEmail"),
                encryptedPassword = data.GetValueOrDefault("encryptedPassword"),
                encryptedPlayerName = data.GetValueOrDefault("encryptedPlayerName"),
                userId = data.GetValueOrDefault("userId"),
                createdAt = data.GetValueOrDefault("createdAt", 0L),
                lastLoginAt = data.GetValueOrDefault("lastLoginAt", 0L)
            };
        }
    }

    #region Device Security

    private void InitializeDeviceSecurity()
    {
        deviceId = SystemInfo.deviceUniqueIdentifier;
        hashedDeviceId = HashString(deviceId);

        Debug.Log($"Device initialized - Hashed ID: {hashedDeviceId.Substring(0, 8)}...");
    }

    private string HashString(string input)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToBase64String(hashedBytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }
    }

    private string EncryptString(string plaintext, string seed)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";

        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        byte[] key = Encoding.UTF8.GetBytes(seed.PadRight(32).Substring(0, 32));

        // Simple XOR encryption (you might want to use AES for production)
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(data[i] ^ key[i % key.Length]);
        }

        return Convert.ToBase64String(data);
    }

    private string DecryptString(string ciphertext, string seed)
    {
        if (string.IsNullOrEmpty(ciphertext)) return "";

        try
        {
            byte[] data = Convert.FromBase64String(ciphertext);
            byte[] key = Encoding.UTF8.GetBytes(seed.PadRight(32).Substring(0, 32));

            // Simple XOR decryption
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(data[i] ^ key[i % key.Length]);
            }

            return Encoding.UTF8.GetString(data);
        }
        catch (Exception e)
        {
            Debug.LogError($"Decryption failed: {e}");
            return "";
        }
    }

    private async Task LoadDeviceAuthData()
    {
        try
        {
            deviceAuthRef = database.Child($"deviceAuth/{hashedDeviceId}");
            var snapshot = await deviceAuthRef.GetValueAsync();

            if (snapshot.Exists)
            {
                deviceAuthData = DeviceAuthData.FromSnapshot(snapshot);
                Debug.Log("Found existing device auth data");
            }
            else
            {
                Debug.Log("No existing device auth data found");
                deviceAuthData = null;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load device auth data: {e}");
            deviceAuthData = null;
        }
    }

    private async Task SaveDeviceAuthData(string email, string password, string playerName, string userId)
    {
        try
        {
            deviceAuthData = new DeviceAuthData
            {
                hashedDeviceId = hashedDeviceId,
                encryptedEmail = EncryptString(email, deviceId),
                encryptedPassword = EncryptString(password, deviceId),
                encryptedPlayerName = EncryptString(playerName, deviceId),
                userId = userId,
                createdAt = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            await deviceAuthRef.SetValueAsync(deviceAuthData.ToDict());
            Debug.Log("Device auth data saved successfully");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save device auth data: {e}");
        }
    }

    private (string email, string password, string playerName) DecryptAuthData()
    {
        if (deviceAuthData == null) return ("", "", "");

        string email = DecryptString(deviceAuthData.encryptedEmail, deviceId);
        string password = DecryptString(deviceAuthData.encryptedPassword, deviceId);
        string playerName = DecryptString(deviceAuthData.encryptedPlayerName, deviceId);

        return (email, password, playerName);
    }

    public static void ClearDeviceAuthData()
    {
        if (Instance?.deviceAuthRef != null && Instance.asyncWorker != null)
        {
            Instance.asyncWorker.EnqueueOperation(async () =>
            {
                try
                {
                    await Instance.deviceAuthRef.RemoveValueAsync();
                    Debug.Log("Device auth data cleared from Firebase");
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to clear device auth data: {e}");
                }
            });
        }

        if (Instance != null)
        {
            Instance.deviceAuthData = null;
        }

        Debug.Log("Device auth data cleared locally");
    }

    #endregion

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeDeviceSecurity();
        }
        else
        {
            Destroy(gameObject);
            return;
        }
    }

    private void Start()
    {
        if (autoConnect) ConnectToMaster();
    }

    private void Update()
    {
        ProcessIncomingRpcs();

        // Reset RPC counter for rate limiting
        if (Time.time - lastRpcCountReset >= 1f)
        {
            rpcsSentThisSecond = 0;
            lastRpcCountReset = Time.time;
        }

        // Prevent memory leak if RPC queue gets out of control
        if (incomingRpcQueue.Count > maxRpcQueueSize)
        {
            Debug.LogWarning($"RPC queue exceeded {maxRpcQueueSize}, clearing oldest entries");
            while (incomingRpcQueue.Count > maxRpcQueueSize / 2)
            {
                incomingRpcQueue.Dequeue();
            }
        }
    }

    private void ProcessIncomingRpcs()
    {
        if (!inRoom || incomingRpcQueue.Count == 0) return;

        int processed = 0;
        while (processed < maxRpcBatchSize && incomingRpcQueue.Count > 0)
        {
            var rpcData = incomingRpcQueue.Dequeue();

            // Skip if targeted to someone else
            if (!string.IsNullOrEmpty(rpcData.targetId) && rpcData.targetId != localPlayer.userId)
            {
                processed++;
                continue;
            }

            try
            {
                ExecuteRPC(rpcData.methodName, rpcData.parameters);
            }
            catch (Exception e)
            {
                Debug.LogError($"RPC execution failed for {rpcData.methodName}: {e}");
            }
            processed++;
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus && inRoom)
        {
            SetupDisconnectCleanup();
        }
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && inRoom)
        {
            SetupDisconnectCleanup();
        }
    }

    private void OnApplicationQuit()
    {
        applicationQuitting = true;
        CleanupOnExit();
    }

    private void OnDestroy()
    {
        if (!applicationQuitting)
            CleanupOnExit();
    }

    private void OnDisable()
    {
        if (!applicationQuitting)
            CleanupOnExit();
    }

    private void CleanupOnExit()
    {
        if (isCleaningUp) return;
        isCleaningUp = true;

        Debug.Log("Starting cleanup on exit");

        // Stop coroutines first with null checks
        if (playerSyncCoroutine != null)
        {
            StopCoroutine(playerSyncCoroutine);
            playerSyncCoroutine = null;
        }
        if (rpcPollingCoroutine != null)
        {
            StopCoroutine(rpcPollingCoroutine);
            rpcPollingCoroutine = null;
        }
        if (rpcCleanupCoroutine != null)
        {
            StopCoroutine(rpcCleanupCoroutine);
            rpcCleanupCoroutine = null;
        }
        if (applicationQuitting && inRoom)
        {
            Debug.Log("Application quitting");

            LeaveRoomImmediate();

            currentRoom = null;
            connectionState = ConnectionState.Disconnected;
            CleanupLocalState();
        }

        // Proper disposal with error handling
        if (cancellationTokenSource != null)
        {
            try
            {
                if (!cancellationTokenSource.Token.IsCancellationRequested)
                    cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
            }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Debug.LogError($"Error disposing cancellation token: {e}"); }
            cancellationTokenSource = null;
        }

        // Proper async worker cleanup
        if (asyncWorker != null)
        {
            try
            {
                asyncWorker.Stop();
            }
            catch (Exception e) { Debug.LogError($"Error stopping async worker: {e}"); }
            asyncWorker = null;
        }

        // Clear collections safely
        networkObjects?.Clear();
        incomingRpcQueue?.Clear();
        playerList?.Clear();

        Debug.Log("Cleanup on exit completed");
    }

    public static void ConnectToMaster()
    {
        if (connectionState != ConnectionState.Disconnected) return;
        connectionState = ConnectionState.Connecting;
        Instance.StartCoroutine(Instance.InitializeFirebase());
    }

    public static void Disconnect()
    {
        Instance?.CleanupOnExit();
        connectionState = ConnectionState.Disconnected;
        OnDisconnected?.Invoke();
    }

    private IEnumerator InitializeFirebase()
    {
        var dependencyTask = FirebaseApp.CheckAndFixDependenciesAsync();
        yield return new WaitUntil(() => dependencyTask.IsCompleted);

        if (dependencyTask.Result == DependencyStatus.Available)
        {
            try
            {
                FirebaseApp app = FirebaseApp.DefaultInstance;
                auth = FirebaseAuth.DefaultInstance;
                database = FirebaseDatabase.GetInstance(app, firebaseUrl).RootReference;

                cancellationTokenSource = new CancellationTokenSource();
                asyncWorker = new OptimizedFirebaseAsyncWorker(cancellationTokenSource.Token);
            }
            catch (Exception e)
            {
                Debug.LogError($"Firebase initialization failed: {e}");
                connectionState = ConnectionState.Disconnected;
                yield break;
            }

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
        // Load device auth data first
        bool loadComplete = false;
        asyncWorker.EnqueueOperation(async () =>
        {
            await LoadDeviceAuthData();
            loadComplete = true;
        });
        yield return new WaitUntil(() => loadComplete);

        string email, password, playerName;
        bool isExistingUser = deviceAuthData != null;

        if (isExistingUser)
        {
            // Decrypt existing credentials
            (email, password, playerName) = DecryptAuthData();
            Debug.Log($"Using existing credentials for device");
        }
        else
        {
            // Generate new credentials
            email = GenerateUniqueEmail();
            password = GenerateSecurePassword();
            playerName = $"Player_{hashedDeviceId.Substring(0, 6)}";
            Debug.Log($"Generated new credentials for device");
        }

        // Authenticate with Firebase
        Task<AuthResult> authTask;
        if (isExistingUser)
        {
            authTask = auth.SignInWithEmailAndPasswordAsync(email, password);
        }
        else
        {
            authTask = auth.CreateUserWithEmailAndPasswordAsync(email, password);
        }

        yield return new WaitUntil(() => authTask.IsCompleted);

        if (authTask.IsCompletedSuccessfully)
        {
            user = authTask.Result.User;
            localPlayer = new NetPlayer(user.UserId, playerName, false);

            // Save device auth data if new user
            if (!isExistingUser)
            {
                bool saveComplete = false;
                asyncWorker.EnqueueOperation(async () =>
                {
                    await SaveDeviceAuthData(email, password, playerName, user.UserId);
                    saveComplete = true;
                });
                yield return new WaitUntil(() => saveComplete);
            }
            else
            {
                // Update last login time
                asyncWorker.EnqueueOperation(async () =>
                {
                    await deviceAuthRef.Child("lastLoginAt").SetValueAsync(ServerValue.Timestamp);
                });
            }

            connectionState = ConnectionState.ConnectedToMaster;
            OnConnectedToMaster?.Invoke();
            Debug.Log($"Connected to Firebase as {playerName}");
        }
        else
        {
            connectionState = ConnectionState.Disconnected;
            Debug.LogError($"Authentication failed: {authTask.Exception}");

            // If existing user auth failed, might need to create new account
            if (isExistingUser)
            {
                Debug.Log("Existing auth failed, will try creating new account");
                ClearDeviceAuthData();
                yield return StartCoroutine(AuthenticateUser());
            }
        }
    }

    private string GenerateUniqueEmail()
    {
        string timestamp = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        return $"device_{hashedDeviceId.Substring(0, 12)}_{timestamp}@device.local";
    }

    private string GenerateSecurePassword()
    {
        return HashString(deviceId + DateTimeOffset.Now.ToUnixTimeSeconds().ToString());
    }

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
            ["playerCount"] = 1,
            ["isOpen"] = options.isOpen,
            ["isVisible"] = options.isVisible,
            ["gameVersion"] = gameVersion,
            ["createdAt"] = ServerValue.Timestamp
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

        if (success)
        {
            yield return StartCoroutine(JoinRoomCoroutine(roomName));
        }
        else
        {
            localPlayer.isMasterClient = false;
            OnJoinRoomFailed?.Invoke("Failed to create room");
            connectionState = ConnectionState.ConnectedToMaster;
        }
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
            playerPresenceRef = roomPlayersRef.Child(localPlayer.userId);

            SetupDisconnectCleanup();

            // Start sync loops
            playerSyncCoroutine = StartCoroutine(PlayerSyncLoop());
            rpcPollingCoroutine = StartCoroutine(RpcPollingLoop());
            rpcCleanupCoroutine = StartCoroutine(RpcCleanupLoop());

            OnJoinedRoom?.Invoke(roomName);
        }
        else
        {
            OnJoinRoomFailed?.Invoke("Failed to join room");
            connectionState = ConnectionState.ConnectedToMaster;
        }
    }

    private void SetupDisconnectCleanup()
    {
        if (hasSetupDisconnectHandlers || !inRoom) return;

        asyncWorker?.EnqueueOperation(async () =>
        {
            try
            {
                await playerPresenceRef.OnDisconnect().RemoveValue();

                if (isMasterClient && autoCleanupEmptyRooms)
                {
                    await roomRef.OnDisconnect().UpdateChildren(new Dictionary<string, object>
                    {
                        ["playerCount"] = 0,
                        ["isEmpty"] = true,
                        ["lastActivity"] = ServerValue.Timestamp
                    });
                }

                hasSetupDisconnectHandlers = true;
                Debug.Log("OnDisconnect cleanup handlers set up successfully");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to setup disconnect cleanup: {e}");
            }
        });
    }

    private IEnumerator JoinOrCreateRoomCoroutine(string roomName, RoomOptions options)
    {
        yield return StartCoroutine(JoinRoomCoroutine(roomName));

        if (connectionState != ConnectionState.ConnectedToRoom)
        {
            connectionState = ConnectionState.ConnectedToMaster;
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
                var snapshot = await database.Child("rooms")
                    .OrderByChild("playerCount")
                    .LimitToFirst(20) // Limit results to reduce bandwidth
                    .GetValueAsync();

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

    private async void LeaveRoomImmediate()
    {
        if (!inRoom) return;

        Debug.Log($"LeaveRoomImmediate called - Current player count: {playerCountInRoom}");

        string roomToLeave = currentRoom;
        bool wasMasterClient = isMasterClient;
        string currentUserId = localPlayer.userId;

        if (playerCountInRoom == 0)
        {
            await database.Child($"rooms/{roomToLeave}").RemoveValueAsync();
        }

        // Stop coroutines immediately
        if (playerSyncCoroutine != null)
        {
            StopCoroutine(playerSyncCoroutine);
            playerSyncCoroutine = null;
        }
        if (rpcPollingCoroutine != null)
        {
            StopCoroutine(rpcPollingCoroutine);
            rpcPollingCoroutine = null;
        }
        if (rpcCleanupCoroutine != null)
        {
            StopCoroutine(rpcCleanupCoroutine);
            rpcCleanupCoroutine = null;
        }

        connectionState = ConnectionState.Disconnecting;

        try
        {
            Debug.Log($"Starting Firebase cleanup for room: {roomToLeave}");

            // Cancel disconnect handlers
            if (playerPresenceRef != null)
            {
                await playerPresenceRef.OnDisconnect().Cancel();
            }

            if (wasMasterClient && roomRef != null)
            {
                await roomRef.OnDisconnect().Cancel();
            }

            // Clean up our RPCs first (saves storage)
            await CleanupPlayerRpcs(currentUserId, roomToLeave);

            // Remove ourselves from the room
            await database.Child($"rooms/{roomToLeave}/players/{currentUserId}").RemoveValueAsync();

            await Task.Delay(100); // Brief delay for Firebase propagation

            // Check remaining players and handle cleanup
            var playersSnapshot = await database.Child($"rooms/{roomToLeave}/players").GetValueAsync();
            long remainingPlayerCount = playersSnapshot.Exists ? playersSnapshot.ChildrenCount : 0;

            if (playerCountInRoom == 0 && autoCleanupEmptyRooms)
            {
                // Clean up entire room to save storage
                await database.Child($"rooms/{roomToLeave}").RemoveValueAsync();
                Debug.Log($"Cleaned up empty room: {roomToLeave}");
            }
            else if (wasMasterClient && remainingPlayerCount > 0)
            {
                // Transfer master client
                var firstPlayerSnapshot = playersSnapshot.Children.First();
                string newMasterUserId = firstPlayerSnapshot.Key;

                await database.Child($"rooms/{roomToLeave}").UpdateChildrenAsync(new Dictionary<string, object>
                {
                    ["masterClientId"] = newMasterUserId,
                    [$"players/{newMasterUserId}/isMasterClient"] = true,
                    ["playerCount"] = remainingPlayerCount
                });
                Debug.Log($"Transferred master client to: {newMasterUserId}");
            }
            else if (remainingPlayerCount > 0)
            {
                // Just update player count
                await database.Child($"rooms/{roomToLeave}/playerCount").SetValueAsync(remainingPlayerCount);
            }

            Debug.Log("Firebase cleanup completed successfully");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to clean up on leave: {e}");
        }
        finally
        {
            currentRoom = null;
            connectionState = ConnectionState.ConnectedToMaster;
            CleanupLocalState();
            OnLeftRoom?.Invoke();
        }
    }

    private void CleanupLocalState()
    {
        playerList.Clear();
        networkObjects.Clear();
        incomingRpcQueue.Clear();

        roomRef = null;
        roomPlayersRef = null;
        roomRpcRef = null;
        playerPresenceRef = null;
        lastRpcTimestamp = 0;
        lastRpcCleanup = 0;
        hasSetupDisconnectHandlers = false;

        if (localPlayer != null)
            localPlayer.isMasterClient = false;

        Debug.Log("Local state cleaned up");
    }

    // Optimized RPC cleanup - runs more frequently to save storage
    private IEnumerator RpcCleanupLoop()
    {
        var wait = new WaitForSeconds(rpcCleanupInterval);

        while (inRoom)
        {
            yield return wait;
            CleanupOwnRpcs();
        }
    }

    private void CleanupOwnRpcs()
    {
        if (!inRoom || localPlayer == null) return;

        asyncWorker?.EnqueueOperation(async () =>
        {
            await CleanupPlayerRpcs(localPlayer.userId, currentRoom);
        });
    }

    // Optimized RPC cleanup with strict buffered retention
    private async Task CleanupPlayerRpcs(string playerId, string roomName)
    {
        try
        {
            var currentTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var cutoffTime = currentTime - (rpcMaxAge * 1000);

            // Pull all RPCs older than cutoff
            var snapshot = await database.Child($"rooms/{roomName}/rpc")
                .OrderByChild("timestamp")
                .EndAt(cutoffTime)
                .GetValueAsync();

            if (!snapshot.Exists) return;

            var deleteTasks = new List<Task>();
            int rpcCount = 0;

            foreach (var child in snapshot.Children)
            {
                var rpcData = child.Value as Dictionary<string, object>;
                if (rpcData == null) continue;

                var senderId = rpcData.GetValueOrDefault("senderId", "")?.ToString();
                var isBuffered = Convert.ToBoolean(rpcData.GetValueOrDefault("buffered", false));

                // Rule 1: NEVER delete buffered RPCs unless the room is gone
                if (isBuffered)
                    continue;

                // Rule 2: Non-buffered RPCs from a given player should be deleted after expiry
                if (senderId == playerId)
                {
                    deleteTasks.Add(child.Reference.RemoveValueAsync());
                    rpcCount++;
                }
            }

            // Batch execute deletions
            if (deleteTasks.Count > 0)
            {
                await Task.WhenAll(deleteTasks);
                deleteTasks.Clear();
            }

            if (rpcCount > 0)
            {
                Debug.Log($"Cleaned up {rpcCount} expired non-buffered RPCs for player {playerId}");
            }

            // Final: if we're leaving the room, delete *all* of our RPCs (buffered + non-buffered)
            if (playerCountInRoom == 0 && playerId == localPlayer?.userId && !inRoom)
            {
                Debug.Log("Player leaving room, removing all RPCs including buffered.");
                await database.Child($"rooms/{roomName}/rpc")
                    .OrderByChild("senderId").EqualTo(playerId)
                    .Reference.RemoveValueAsync();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"RPC cleanup failed for player {playerId}: {e}");
        }
    }

    // Optimized player sync with reduced Firebase calls
    private IEnumerator PlayerSyncLoop()
    {
        var wait = new WaitForSeconds(playerSyncInterval);

        while (inRoom)
        {
            asyncWorker?.EnqueueOperation(async () =>
            {
                try
                {
                    // Only get player data that's actually changed
                    var snapshot = await roomPlayersRef.GetValueAsync();
                    if (!snapshot.Exists) return;

                    var newPlayers = new Dictionary<string, NetPlayer>();
                    var joinedPlayers = new List<NetPlayer>();
                    var leftPlayers = new List<NetPlayer>();

                    // Get current master client ID
                    var roomSnapshot = await roomRef.Child("masterClientId").GetValueAsync();
                    string currentMasterClientId = roomSnapshot.Value?.ToString();

                    // Process players efficiently
                    foreach (var child in snapshot.Children)
                    {
                        var player = NetPlayer.FromSnapshot(child);
                        if (player != null)
                        {
                            // Set master client status based on room data
                            player.isMasterClient = player.userId == currentMasterClientId;
                            newPlayers[player.userId] = player;

                            if (!playerList.ContainsKey(player.userId))
                            {
                                joinedPlayers.Add(player);
                            }
                        }
                    }

                    // Find who left
                    foreach (var existingPlayer in playerList.Values)
                    {
                        if (!newPlayers.ContainsKey(existingPlayer.userId))
                        {
                            leftPlayers.Add(existingPlayer);
                        }
                    }

                    // Handle master client assignment if needed
                    bool masterClientExists = !string.IsNullOrEmpty(currentMasterClientId) &&
                                            newPlayers.ContainsKey(currentMasterClientId);

                    if (!masterClientExists && newPlayers.Count > 0)
                    {
                        var newMaster = newPlayers.Values.First();

                        // Update master client atomically
                        await database.Child($"rooms/{currentRoom}").UpdateChildrenAsync(new Dictionary<string, object>
                        {
                            ["masterClientId"] = newMaster.userId,
                            [$"players/{newMaster.userId}/isMasterClient"] = true,
                            ["playerCount"] = newPlayers.Count
                        });

                        newMaster.isMasterClient = true;
                        Debug.Log($"Assigned new master client: {newMaster.userId}");
                    }
                    else if (newPlayers.Count > 0)
                    {
                        // Just update player count
                        await roomRef.Child("playerCount").SetValueAsync(newPlayers.Count);
                    }

                    // Update on main thread
                    if (joinedPlayers.Count > 0 || leftPlayers.Count > 0 || !DictionariesEqual(playerList, newPlayers))
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
                                bool wasLocalMaster = localPlayer.isMasterClient;
                                localPlayer.isMasterClient = newPlayers[localPlayer.userId].isMasterClient;

                                if (!wasLocalMaster && localPlayer.isMasterClient)
                                {
                                    Debug.Log("We became the master client");
                                }
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

    private bool DictionariesEqual(Dictionary<string, NetPlayer> dict1, Dictionary<string, NetPlayer> dict2)
    {
        if (dict1.Count != dict2.Count) return false;

        foreach (var kvp in dict1)
        {
            if (!dict2.ContainsKey(kvp.Key)) return false;

            var player1 = kvp.Value;
            var player2 = dict2[kvp.Key];

            if (player1.isMasterClient != player2.isMasterClient ||
                player1.nickName != player2.nickName)
            {
                return false;
            }
        }
        return true;
    }

    // Optimized RPC polling with better batching
    private IEnumerator RpcPollingLoop()
    {
        var wait = new WaitForSeconds(rpcPollInterval);

        while (inRoom)
        {
            asyncWorker?.EnqueueOperation(async () =>
            {
                try
                {
                    // More efficient RPC polling with larger batches
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

                        string senderId = rpcData.GetValueOrDefault("senderId", "");
                        if (senderId == localPlayer.userId) continue; // Skip our own RPCs

                        // Use optimized deserialization
                        object[] parameters = Array.Empty<object>();
                        if (rpcData.ContainsKey("params") && rpcData["params"] is string serializedParams)
                        {
                            try
                            {
                                parameters = NetworkSerializer.DeserializeParameters(serializedParams);
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"Failed to deserialize RPC parameters: {e}");
                                continue;
                            }
                        }

                        var rpc = new RpcData
                        {
                            methodName = rpcData.GetValueOrDefault("method", ""),
                            senderId = senderId,
                            targetId = rpcData.GetValueOrDefault("targetId", ""),
                            parameters = parameters,
                            timestamp = Convert.ToInt64(rpcData.GetValueOrDefault("timestamp", "0")),
                            target = (RpcTarget)Enum.Parse(typeof(RpcTarget), rpcData.GetValueOrDefault("target", "All")),
                            buffered = Convert.ToBoolean(rpcData.GetValueOrDefault("buffered", false)),
                            rpcId = child.Key
                        };

                        rpcsToProcess.Add(rpc);
                        maxTimestamp = Math.Max(maxTimestamp, rpc.timestamp);
                    }

                    lastRpcTimestamp = maxTimestamp;

                    // Queue RPCs for main thread processing
                    if (rpcsToProcess.Count > 0)
                    {
                        UnityMainThreadDispatcher.Instance().Enqueue(() =>
                        {
                            foreach (var rpc in rpcsToProcess)
                            {
                                incomingRpcQueue.Enqueue(rpc);
                            }
                        });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"RPC polling failed: {e}");
                }
            });

            yield return wait;
        }
    }

    // Optimized RPC sending with rate limiting and compression
    public static void RPC(string methodName, RpcTarget target, params object[] parameters)
    {
        if (!inRoom || Instance?.roomRpcRef == null || Instance.asyncWorker == null)
        {
            Debug.LogWarning("Can't send RPC - not connected to room");
            return;
        }

        // Rate limiting to prevent spam
        if (Instance.rpcsSentThisSecond >= 50) // Max 50 RPCs per second
        {
            Debug.LogWarning("RPC rate limit exceeded, dropping RPC");
            return;
        }

        // Skip unnecessary RPCs
        if (IsSkippableRPC(methodName, parameters))
            return;

        Instance.rpcsSentThisSecond++;

        bool isBuffered = target == RpcTarget.AllBuffered || target == RpcTarget.OthersBuffered;
        string targetId = "";

        if (target == RpcTarget.Host)
        {
            var master = playerList.Values.FirstOrDefault(p => p.isMasterClient);
            targetId = master?.userId ?? "";
            if (string.IsNullOrEmpty(targetId))
            {
                Debug.LogWarning("No master client found for Host RPC");
                return;
            }
        }

        // Optimized serialization
        string serializedParams = "";
        try
        {
            serializedParams = NetworkSerializer.SerializeParameters(parameters);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to serialize RPC parameters: {e}");
            return;
        }

        // Compressed RPC data structure
        var rpcData = new Dictionary<string, object>
        {
            ["method"] = methodName,
            ["senderId"] = localPlayer.userId,
            ["params"] = serializedParams,
            ["timestamp"] = ServerValue.Timestamp,
            ["target"] = target.ToString(),
            ["buffered"] = isBuffered
        };

        // Only add targetId if needed to save space
        if (!string.IsNullOrEmpty(targetId))
        {
            rpcData["targetId"] = targetId;
        }

        var rpcRef = Instance.roomRpcRef.Push();
        Instance.asyncWorker?.EnqueueOperation(async () =>
        {
            try
            {
                await rpcRef.SetValueAsync(rpcData);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to send RPC: {e}");
            }
        });

        // Execute locally if needed
        if (target == RpcTarget.All || target == RpcTarget.AllBuffered)
        {
            ExecuteRPC(methodName, parameters);
        }
    }

    // Check if RPC can be skipped to reduce bandwidth
    private static bool IsSkippableRPC(string methodName, object[] parameters)
    {
        // Skip frequent transform updates if position hasn't changed significantly
        if (methodName.Contains("Transform") || methodName.Contains("Position"))
        {
            // Add logic to check if transform changed significantly
            return false; // For now, don't skip transform updates
        }

        // Skip empty parameter RPCs for certain methods
        if ((parameters == null || parameters.Length == 0) &&
            (methodName == "Heartbeat" || methodName == "Ping"))
        {
            return true;
        }

        return false;
    }

    private static void ExecuteRPC(string methodName, object[] parameters)
    {
        if (Instance?.networkObjects == null) return;

        foreach (var networkBehaviour in Instance.networkObjects.Values)
        {
            try
            {
                networkBehaviour?.OnRPC(methodName, parameters);
            }
            catch (Exception e)
            {
                Debug.LogError($"RPC execution failed for {methodName}: {e}");
            }
        }
    }

    // Optimized network instantiation
    public static GameObject NetInstantiate(GameObject prefab, Vector3 position, Quaternion rotation, byte group = 0, object[] data = null)
    {
        if (!inRoom || prefab == null) return null;

        string networkId = Guid.NewGuid().ToString("N")[..16]; // Shorter IDs to save space
        var instance = UnityEngine.Object.Instantiate(prefab, position, rotation);

        var networkView = instance.GetComponent<NetworkView>();
        if (networkView == null)
            networkView = instance.AddComponent<NetworkView>();

        networkView.viewId = networkId;
        networkView.isMine = true;
        RegisterNetworkObject(networkView);

        // More efficient instantiation RPC
        RPC("OnInstantiate", RpcTarget.OthersBuffered,
            prefab.name,
            position.x, position.y, position.z,
            rotation.x, rotation.y, rotation.z, rotation.w,
            networkId,
            group,
            data);

        return instance;
    }

    public static void NetDestroy(GameObject target)
    {
        if (target == null) return;

        var networkView = target.GetComponent<NetworkView>();
        if (networkView != null && networkView.isMine)
        {
            RPC("OnDestroy", RpcTarget.OthersBuffered, networkView.viewId);
            UnregisterNetworkObject(networkView.viewId);
        }
        UnityEngine.Object.Destroy(target);
    }

    public static void RegisterNetworkObject(INetworkBehaviour networkBehaviour)
    {
        if (networkBehaviour == null || string.IsNullOrEmpty(networkBehaviour.NetworkId) || Instance == null)
            return;

        Instance.networkObjects[networkBehaviour.NetworkId] = networkBehaviour;
    }

    public static void UnregisterNetworkObject(string networkId)
    {
        if (Instance?.networkObjects != null && !string.IsNullOrEmpty(networkId))
        {
            Instance.networkObjects.Remove(networkId);
        }
    }

    // Optimized player property updates
    public static void SetCustomPlayerProperties(Dictionary<string, object> properties)
    {
        if (localPlayer == null || properties == null || !inRoom || Instance?.asyncWorker == null)
            return;

        // Only update properties that actually changed
        var changedProperties = new Dictionary<string, object>();
        foreach (var prop in properties)
        {
            if (!localPlayer.customProperties.ContainsKey(prop.Key) ||
                !localPlayer.customProperties[prop.Key].Equals(prop.Value))
            {
                localPlayer.customProperties[prop.Key] = prop.Value;
                changedProperties[prop.Key] = prop.Value;
            }
        }

        if (changedProperties.Count == 0) return; // Nothing changed

        var updates = new Dictionary<string, object>();
        foreach (var prop in changedProperties)
        {
            updates[$"customProperties/{prop.Key}"] = prop.Value;
        }

        Instance.asyncWorker?.EnqueueOperation(async () =>
        {
            try
            {
                await Instance.roomPlayersRef.Child(localPlayer.userId).UpdateChildrenAsync(updates);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to update player properties: {e}");
            }
        });
    }

    // Utility methods for external cleanup
    public static void ForceCleanupRoom(string roomName)
    {
        if (isMasterClient && Instance?.asyncWorker != null && !string.IsNullOrEmpty(roomName))
        {
            Instance.asyncWorker?.EnqueueOperation(async () =>
            {
                try
                {
                    await Instance.database.Child($"rooms/{roomName}").RemoveValueAsync();
                    Debug.Log($"Force cleaned up room: {roomName}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to force cleanup room: {e}");
                }
            });
        }
    }

    public static void ForceCleanupOldRpcs()
    {
        Instance?.CleanupOwnRpcs();
    }

    // Get network statistics
    public static NetworkStatistics GetNetworkStats()
    {
        return new NetworkStatistics
        {
            PlayersInRoom = playerCountInRoom,
            IsMasterClient = isMasterClient,
            ConnectionState = connectionState,
            RoomName = currentRoom,
            QueuedRpcs = Instance?.incomingRpcQueue.Count ?? 0,
            NetworkObjects = Instance?.networkObjects.Count ?? 0
        };
    }
}

// Network statistics structure
public struct NetworkStatistics
{
    public int PlayersInRoom;
    public bool IsMasterClient;
    public ConnectionState ConnectionState;
    public string RoomName;
    public int QueuedRpcs;
    public int NetworkObjects;
}

// Optimized Network Serialization System with compression
public static class NetworkSerializer
{
    private const char SEPARATOR = '|';
    private const char TYPE_SEPARATOR = ':';
    private static readonly StringBuilder stringBuilder = new StringBuilder(512);

    public static string SerializeParameters(object[] parameters)
    {
        if (parameters == null || parameters.Length == 0)
            return string.Empty;

        stringBuilder.Clear();

        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) stringBuilder.Append(SEPARATOR);

            var param = parameters[i];
            if (param == null)
            {
                stringBuilder.Append("n:");
            }
            else
            {
                string typeName = GetCompressedTypeName(param.GetType());
                string value = SerializeValue(param);
                stringBuilder.Append($"{typeName}{TYPE_SEPARATOR}{value}");
            }
        }
        return stringBuilder.ToString();
    }

    public static object[] DeserializeParameters(string serializedData)
    {
        if (string.IsNullOrEmpty(serializedData))
            return Array.Empty<object>();

        try
        {
            var parts = serializedData.Split(SEPARATOR);
            var parameters = new object[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part == "n:")
                {
                    parameters[i] = null;
                    continue;
                }

                int typeIndex = part.IndexOf(TYPE_SEPARATOR);
                if (typeIndex == -1)
                {
                    Debug.LogWarning($"Malformed RPC parameter: {part}");
                    continue;
                }

                string typeName = part.Substring(0, typeIndex);
                string value = part.Substring(typeIndex + 1);

                parameters[i] = DeserializeValue(typeName, value);
            }

            return parameters;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to deserialize parameters: {e}");
            return Array.Empty<object>();
        }
    }

    // Compressed type names to save bandwidth
    private static string GetCompressedTypeName(Type type)
    {
        return type.Name switch
        {
            "Vector3" => "v3",
            "Vector2" => "v2",
            "Quaternion" => "q",
            "Color" => "c",
            "Boolean" => "b",
            "Single" => "f",
            "Double" => "d",
            "Int32" => "i",
            "Int64" => "l",
            "String" => "s",
            _ => type.Name
        };
    }

    private static string SerializeValue(object value)
    {
        switch (value)
        {
            case Vector3 v3:
                // Compress Vector3 to save space (round to 3 decimal places)
                return $"{v3.x:F3},{v3.y:F3},{v3.z:F3}";
            case Vector2 v2:
                return $"{v2.x:F3},{v2.y:F3}";
            case Quaternion q:
                return $"{q.x:F3},{q.y:F3},{q.z:F3},{q.w:F3}";
            case Color c:
                return $"{c.r:F3},{c.g:F3},{c.b:F3},{c.a:F3}";
            case bool b:
                return b ? "1" : "0";
            case float f:
                return f.ToString("F3"); // Limit precision to save space
            case double d:
                return d.ToString("F6"); // Reduced precision
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
                case "v3": // Vector3
                    var v3Parts = value.Split(',');
                    if (v3Parts.Length != 3) throw new ArgumentException("Invalid Vector3 format");
                    return new Vector3(float.Parse(v3Parts[0]), float.Parse(v3Parts[1]), float.Parse(v3Parts[2]));

                case "v2": // Vector2
                    var v2Parts = value.Split(',');
                    if (v2Parts.Length != 2) throw new ArgumentException("Invalid Vector2 format");
                    return new Vector2(float.Parse(v2Parts[0]), float.Parse(v2Parts[1]));

                case "q": // Quaternion
                    var qParts = value.Split(',');
                    if (qParts.Length != 4) throw new ArgumentException("Invalid Quaternion format");
                    return new Quaternion(float.Parse(qParts[0]), float.Parse(qParts[1]), float.Parse(qParts[2]), float.Parse(qParts[3]));

                case "c": // Color
                    var cParts = value.Split(',');
                    if (cParts.Length != 4) throw new ArgumentException("Invalid Color format");
                    return new Color(float.Parse(cParts[0]), float.Parse(cParts[1]), float.Parse(cParts[2]), float.Parse(cParts[3]));

                case "b": // Boolean
                    return value == "1";

                case "f": // Single/Float
                    return float.Parse(value);

                case "d": // Double
                    return double.Parse(value);

                case "i": // Int32
                    return int.Parse(value);

                case "l": // Int64
                    return long.Parse(value);

                case "s": // String
                    return value;

                // Legacy support for full type names
                case "Vector3":
                    var v3PartsLegacy = value.Split(',');
                    return new Vector3(float.Parse(v3PartsLegacy[0]), float.Parse(v3PartsLegacy[1]), float.Parse(v3PartsLegacy[2]));

                case "Boolean":
                    return value == "1";

                case "Single":
                    return float.Parse(value);

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

// Improved UnityMainThreadDispatcher with better performance
public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher _instance;
    private readonly Queue<System.Action> _actions = new Queue<System.Action>();
    private readonly object _lock = new object();
    private const int MAX_ACTIONS_PER_FRAME = 100; // Increased for better performance

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
        if (action == null) return;

        lock (_lock)
        {
            _actions.Enqueue(action);

            // Prevent memory overflow
            if (_actions.Count > 1000)
            {
                Debug.LogWarning("MainThreadDispatcher queue overflow, clearing old actions");
                while (_actions.Count > 500)
                {
                    _actions.Dequeue();
                }
            }
        }
    }

    private void Update()
    {
        int processed = 0;

        lock (_lock)
        {
            while (_actions.Count > 0 && processed < MAX_ACTIONS_PER_FRAME)
            {
                try
                {
                    _actions.Dequeue()?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"MainThreadDispatcher action failed: {e}");
                }
                processed++;
            }
        }
    }
}

// Extension methods for better performance
public static class DictionaryExtensions
{
    public static string GetValueOrDefault(this Dictionary<string, object> dictionary, string key, string defaultValue = "")
    {
        if (dictionary == null || !dictionary.TryGetValue(key, out object value))
            return defaultValue;

        return value?.ToString() ?? defaultValue;
    }

    public static T GetValueOrDefault<T>(this Dictionary<string, object> dictionary, string key, T defaultValue = default(T))
    {
        if (dictionary?.TryGetValue(key, out object value) == true && value != null)
        {
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }
}

public static class ServerValue
{
    public static Dictionary<string, object> Timestamp => new Dictionary<string, object> { [".sv"] = "timestamp" };
}