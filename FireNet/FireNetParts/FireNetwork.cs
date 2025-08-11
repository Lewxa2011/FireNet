using Firebase;
using Firebase.Auth;
using Firebase.Database;
using Firebase.Extensions;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class FireNetwork : MonoBehaviour
{
    private struct PlayerChange
    {
        public NetPlayer player;
        public ChangeType type;
    }

    private enum ChangeType
    {
        Joined,
        Left
    }

    public static FireNetwork Instance { get; private set; }

    [Header("Settings")]
    public bool autoConnect = true;
    public int maxPlayersPerRoom = 4;
    public string gameVersion = "1.0";
    public string firebaseUrl = "https://put-YOUR-realtime-database-URL-here-diddy-blud-default-rtdb.firebaseio.com/";

    [Header("Performance Settings")]
    public float playerSyncInterval = 2f;
    public float rpcPollInterval = 0.1f;
    public float presenceUpdateInterval = 15f;
    public int maxRpcBatchSize = 30;
    public int maxMainThreadActionsPerFrame = 16;

    [Header("Graceful Disconnection")]
    public bool enableGracefulDisconnection = true;
    public float disconnectionTimeout = 5f;


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
    public static Dictionary<string, RoomInfo> roomList { get; private set; } = new Dictionary<string, RoomInfo>();
    public static bool isMasterClient => localPlayer?.isMasterClient ?? false;
    public static bool inRoom => connectionState == ConnectionState.ConnectedToRoom;
    public static int playerCountInRoom => playerList.Count;


    public FirebaseAuth auth;
    public DatabaseReference database;
    public FirebaseUser user;


    private Dictionary<string, INetworkBehaviour> networkObjects = new Dictionary<string, INetworkBehaviour>();


    private readonly ConcurrentQueue<RpcData> incomingRpcQueue = new ConcurrentQueue<RpcData>();
    private readonly Dictionary<string, DatabaseReference> rpcReferences = new Dictionary<string, DatabaseReference>();
    private readonly Dictionary<string, RpcCoalesceData> coalescedRpcs = new Dictionary<string, RpcCoalesceData>();
    private long lastRpcTimestamp = 0;


    private bool isGracefullyDisconnecting = false;
    private DatabaseReference presenceRef;
    private DatabaseReference playerRef;


    private readonly ConcurrentQueue<System.Action> mainThreadActions = new ConcurrentQueue<System.Action>();
    private readonly object syncLock = new object();


    private FirebaseAsyncWorker asyncWorker;
    private CancellationTokenSource cancellationTokenSource;


    private DatabaseReference roomPlayersRef;
    private DatabaseReference roomRpcRef;
    private DatabaseReference roomRef;


    private float lastPerformanceLog = 0f;
    private int processedRpcsThisFrame = 0;
    private readonly Queue<float> frameTimes = new Queue<float>();

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

    private struct RpcCoalesceData
    {
        public Dictionary<string, object> data;
        public DatabaseReference reference;
        public float lastUpdateTime;
    }


    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            SetupGracefulDisconnection();
            InitializePerformanceMonitoring();
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
        float frameStart = Time.realtimeSinceStartup;
        processedRpcsThisFrame = 0;


        ProcessMainThreadActions();


        ProcessIncomingRpcs();


        float frameTime = Time.realtimeSinceStartup - frameStart;
        UpdatePerformanceMetrics(frameTime);
    }

    private void ProcessMainThreadActions()
    {
        const float maxBudgetMs = 1f;
        const int maxActionsPerFrame = 8;

        int processed = 0;
        float budgetStart = Time.realtimeSinceStartup;

        while (processed < maxActionsPerFrame &&
               mainThreadActions.TryDequeue(out var action) &&
               (Time.realtimeSinceStartup - budgetStart) * 1000f < maxBudgetMs)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError($"MainThreadAction failed: {e}");
            }
            processed++;
        }
    }

    private void ProcessIncomingRpcs()
    {
        if (!inRoom) return;

        int processed = 0;
        float budgetStart = Time.realtimeSinceStartup;
        const float maxBudgetMs = 1f;

        while (processed < maxRpcBatchSize &&
               incomingRpcQueue.TryDequeue(out var rpcData) &&
               (Time.realtimeSinceStartup - budgetStart) * 1000f < maxBudgetMs)
        {
            if (string.IsNullOrEmpty(rpcData.targetId) || rpcData.targetId == localPlayer.userId)
            {
                ExecuteRPC(rpcData.methodName, rpcData.parameters);
            }
            processed++;
        }

        processedRpcsThisFrame = processed;
    }

    private void UpdatePerformanceMetrics(float frameTime)
    {
        frameTimes.Enqueue(frameTime);
        if (frameTimes.Count > 60) frameTimes.Dequeue();


        if (Time.time - lastPerformanceLog > 30f)
        {
            LogPerformanceMetrics();
            lastPerformanceLog = Time.time;
        }
    }

    private void InitializePerformanceMonitoring()
    {
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 1;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            StartCoroutine(GracefulShutdown());
        }
    }

    private void OnApplicationQuit()
    {
        if (Instance == this && enableGracefulDisconnection)
        {
            SetupFirebaseOnDisconnectCleanup();
        }
    }


    private void SetupGracefulDisconnection()
    {
        Application.wantsToQuit += OnWantsToQuit;

#if UNITY_ANDROID || UNITY_IOS
        Application.lowMemory += OnLowMemory;
#endif

#if UNITY_EDITOR
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
    }

#if UNITY_EDITOR
    private void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
    {
        if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
        {
            SetupFirebaseOnDisconnectCleanup();
        }
    }
#endif

    private bool OnWantsToQuit()
    {
        if (!isGracefullyDisconnecting && enableGracefulDisconnection && inRoom)
        {
            SetupFirebaseOnDisconnectCleanup();
        }
        return true;
    }

    private void OnLowMemory()
    {
        if (enableGracefulDisconnection && inRoom)
        {
            SetupFirebaseOnDisconnectCleanup();
        }
    }

    private void SetupFirebaseOnDisconnectCleanup()
    {
        if (database == null || !inRoom || string.IsNullOrEmpty(currentRoom) || localPlayer == null)
            return;

        asyncWorker?.EnqueueOperation(async () =>
        {
            try
            {
                string playerPath = $"rooms/{currentRoom}/players/{localPlayer.userId}";


                var disconnectOps = new List<Task>
                {
                    database.Child(playerPath).OnDisconnect().RemoveValue(),
                    database.Child($"rooms/{currentRoom}/playerCount").OnDisconnect().SetValue(Math.Max(0, playerCountInRoom - 1))
                };


                foreach (var rpcRef in rpcReferences.Values)
                {
                    disconnectOps.Add(rpcRef.OnDisconnect().RemoveValue());
                }


                if (localPlayer.isMasterClient)
                {
                    if (playerList.Count <= 1)
                    {
                        disconnectOps.Add(database.Child($"rooms/{currentRoom}").OnDisconnect().RemoveValue());
                    }
                    else
                    {
                        disconnectOps.Add(database.Child($"rooms/{currentRoom}/masterClientLeft").OnDisconnect().SetValue(true));
                        disconnectOps.Add(database.Child($"rooms/{currentRoom}/masterClientId").OnDisconnect().RemoveValue());
                    }
                }

                await Task.WhenAll(disconnectOps);
                Debug.Log("Firebase OnDisconnect cleanup configured");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to setup OnDisconnect cleanup: {e.Message}");
            }
        });
    }

    private IEnumerator GracefulShutdown()
    {
        isGracefullyDisconnecting = true;


        cancellationTokenSource?.Cancel();
        asyncWorker?.Stop();

        if (inRoom)
        {
            yield return StartCoroutine(LeaveRoomGracefully());
        }


        if (presenceRef != null)
        {
            presenceRef.OnDisconnect().RemoveValue();
            presenceRef = null;
        }


        if (auth != null && user != null)
        {
            auth.SignOut();
        }

        connectionState = ConnectionState.Disconnected;
        OnDisconnected?.Invoke();
    }

    private IEnumerator LeaveRoomGracefully()
    {
        if (!inRoom || string.IsNullOrEmpty(currentRoom))
            yield break;

        Debug.Log($"Leaving room gracefully: {currentRoom}");

        string roomToLeave = currentRoom;
        string playerIdToRemove = localPlayer.userId;
        bool wasMasterClient = localPlayer.isMasterClient;


        bool cleanupComplete = false;
        asyncWorker?.EnqueueOperation(async () =>
        {
            try
            {
                var cleanupTasks = new List<Task>
                {
                    database.Child($"rooms/{roomToLeave}/players/{playerIdToRemove}").RemoveValueAsync(),
                    database.Child($"rooms/{roomToLeave}/playerCount").SetValueAsync(Math.Max(0, playerCountInRoom - 1))
                };


                foreach (var rpcRef in rpcReferences.Values)
                {
                    cleanupTasks.Add(rpcRef.RemoveValueAsync());
                }

                await Task.WhenAll(cleanupTasks);

                if (wasMasterClient)
                {
                    await TransferMasterClientAsync(roomToLeave, playerIdToRemove);
                }

                cleanupComplete = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Graceful leave failed: {e}");
                cleanupComplete = true;
            }
        });


        float timeout = disconnectionTimeout;
        while (!cleanupComplete && timeout > 0)
        {
            yield return new WaitForSeconds(0.1f);
            timeout -= 0.1f;
        }

        CleanupLocalState();
    }

    private async Task TransferMasterClientAsync(string roomName, string leavingPlayerId)
    {
        var playersSnapshot = await database.Child($"rooms/{roomName}/players").GetValueAsync();

        if (playersSnapshot.ChildrenCount > 1)
        {
            NetPlayer newMaster = null;
            foreach (var child in playersSnapshot.Children)
            {
                if (child.Key != leavingPlayerId)
                {
                    newMaster = NetPlayer.FromSnapshot(child);
                    if (newMaster != null) break;
                }
            }

            if (newMaster != null)
            {
                var masterTransfer = new Dictionary<string, object>
                {
                    [$"rooms/{roomName}/masterClientId"] = newMaster.userId,
                    [$"rooms/{roomName}/players/{newMaster.userId}/isMasterClient"] = true
                };

                await database.UpdateChildrenAsync(masterTransfer);
                Debug.Log($"Master client transferred to: {newMaster.nickName}");
            }
        }
        else
        {
            await database.Child($"rooms/{roomName}").RemoveValueAsync();
            Debug.Log($"Room deleted (no remaining players): {roomName}");
        }
    }


    public static void ConnectToMaster()
    {
        if (connectionState != ConnectionState.Disconnected) return;
        connectionState = ConnectionState.Connecting;
        Instance.StartCoroutine(Instance.InitializeFirebase());
    }

    public static void Disconnect()
    {
        if (connectionState == ConnectionState.Disconnected) return;
        Instance.StartCoroutine(Instance.GracefulShutdown());
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
            asyncWorker = new FirebaseAsyncWorker(cancellationTokenSource.Token);

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
        string email = "";
        string password = "";


        if (PlayerPrefs.HasKey("HasPlayedB4"))
        {
            email = PlayerPrefs.GetString("user_email");
            password = PlayerPrefs.GetString("user_password");
        }
        else
        {
            email = GenerateUniqueEmail();
            password = GenerateSecurePassword();

            PlayerPrefs.SetString("user_email", email);
            PlayerPrefs.SetString("user_password", password);
            PlayerPrefs.SetInt("HasPlayedB4", 1);
            PlayerPrefs.Save();

            Debug.Log($"Generated new user credentials: {email.Substring(0, Math.Min(email.Length, 10))}...");
        }


        yield return StartCoroutine(SignInUser(email, password));


        if (user == null)
        {
            yield return StartCoroutine(CreateUser(email, password));
        }


        if (user == null)
        {
            Debug.LogWarning("Authentication failed, generating new credentials");
            yield return StartCoroutine(RetryWithNewCredentials());
        }

        if (user != null)
        {
            yield return StartCoroutine(FinalizeAuthentication());
        }
        else
        {
            connectionState = ConnectionState.Disconnected;
            Debug.LogError("Failed to authenticate user");
        }
    }

    private string GenerateUniqueEmail()
    {
        string deviceId = SystemInfo.deviceUniqueIdentifier;
        string timestamp = System.DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        string randomSuffix = System.Guid.NewGuid().ToString("N").Substring(0, 8);

        return $"user_{deviceId.Substring(0, 8)}_{timestamp}_{randomSuffix}@tempmail.org";
    }

    private string GenerateSecurePassword()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
        var random = new System.Random();
        var password = new char[16];

        for (int i = 0; i < password.Length; i++)
        {
            password[i] = chars[random.Next(chars.Length)];
        }

        return new string(password);
    }

    private IEnumerator SignInUser(string email, string password)
    {
        var signInTask = auth.SignInWithEmailAndPasswordAsync(email, password);
        yield return new WaitUntil(() => signInTask.IsCompleted);

        if (signInTask.IsCompletedSuccessfully)
        {
            user = signInTask.Result.User;
            Debug.Log($"User signed in successfully: {user.UserId}");
        }
        else
        {
            Debug.LogWarning($"Sign in failed: {GetFirebaseAuthError(signInTask.Exception)}");
        }
    }

    private IEnumerator CreateUser(string email, string password)
    {
        var createTask = auth.CreateUserWithEmailAndPasswordAsync(email, password);
        yield return new WaitUntil(() => createTask.IsCompleted);

        if (createTask.IsCompletedSuccessfully)
        {
            user = createTask.Result.User;
            Debug.Log($"User created successfully: {user.UserId}");
        }
        else
        {
            Debug.LogWarning($"User creation failed: {GetFirebaseAuthError(createTask.Exception)}");
        }
    }

    private IEnumerator RetryWithNewCredentials()
    {
        string newEmail = GenerateUniqueEmail();
        string newPassword = GenerateSecurePassword();

        PlayerPrefs.SetString("user_email", newEmail);
        PlayerPrefs.SetString("user_password", newPassword);
        PlayerPrefs.Save();

        yield return StartCoroutine(CreateUser(newEmail, newPassword));
    }

    private Firebase.Auth.AuthError GetFirebaseAuthError(System.AggregateException ex)
    {
        if (ex?.InnerExceptions != null)
        {
            foreach (var e in ex.InnerExceptions)
            {
                if (e is FirebaseException fe)
                {
                    return (Firebase.Auth.AuthError)fe.ErrorCode;
                }
            }
        }
        return Firebase.Auth.AuthError.Failure;
    }

    private IEnumerator FinalizeAuthentication()
    {
        localPlayer = new NetPlayer(user.UserId, $"Player_{user.UserId.Substring(0, 6)}", false);
        connectionState = ConnectionState.ConnectedToMaster;

        OnConnectedToMaster?.Invoke();
        Debug.Log("Connected to Firebase successfully");
        yield return null;
    }


    public static void CreateRoom(string roomName, RoomOptions options = null)
    {
        if (connectionState != ConnectionState.ConnectedToMaster) return;
        connectionState = ConnectionState.JoiningRoom;
        Instance.StartCoroutine(Instance.CreateRoomCoroutine(roomName, options ?? new RoomOptions()));
    }

    public static void JoinRoom(string roomName)
    {
        if (connectionState != ConnectionState.ConnectedToMaster) return;
        connectionState = ConnectionState.JoiningRoom;
        Instance.StartCoroutine(Instance.JoinRoomCoroutine(roomName));
    }

    public static void JoinRandomRoom()
    {
        if (connectionState != ConnectionState.ConnectedToMaster) return;
        connectionState = ConnectionState.JoiningRoom;
        Instance.StartCoroutine(Instance.JoinRandomRoomCoroutine());
    }

    public static void JoinOrCreateRoom(string roomName, RoomOptions options = null)
    {
        if (connectionState != ConnectionState.ConnectedToMaster) return;
        connectionState = ConnectionState.JoiningRoom;
        Instance.StartCoroutine(Instance.JoinOrCreateRoomCoroutine(roomName, options ?? new RoomOptions()));
    }

    public static void LeaveRoom()
    {
        if (connectionState != ConnectionState.ConnectedToRoom) return;
        Instance.StartCoroutine(Instance.LeaveRoomGracefully());
    }

    private IEnumerator CreateRoomCoroutine(string roomName, RoomOptions options)
    {

        bool roomExists = false;
        bool checkComplete = false;

        asyncWorker.EnqueueOperation(async () =>
        {
            var snapshot = await database.Child($"rooms/{roomName}").GetValueAsync();
            roomExists = snapshot.Exists;
            checkComplete = true;
        });

        yield return new WaitUntil(() => checkComplete);

        if (roomExists)
        {
            OnJoinRoomFailed?.Invoke("Room already exists");
            connectionState = ConnectionState.ConnectedToMaster;
            yield break;
        }


        localPlayer.isMasterClient = true;
        var roomData = new Dictionary<string, object>
        {
            ["name"] = roomName,
            ["masterClientId"] = localPlayer.userId,
            ["maxPlayers"] = options.maxPlayers > 0 ? options.maxPlayers : maxPlayersPerRoom,
            ["playerCount"] = 0,
            ["isOpen"] = options.isOpen,
            ["isVisible"] = options.isVisible,
            ["gameVersion"] = gameVersion,
            ["customProperties"] = options.customRoomProperties ?? new Dictionary<string, object>()
        };

        bool createComplete = false;
        bool createSuccess = false;

        asyncWorker.EnqueueOperation(async () =>
        {
            try
            {
                await database.Child($"rooms/{roomName}").SetValueAsync(roomData);
                createSuccess = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to create room: {e}");
            }
            createComplete = true;
        });

        yield return new WaitUntil(() => createComplete);

        if (createSuccess)
        {
            yield return StartCoroutine(JoinRoomCoroutine(roomName));
        }
        else
        {
            OnJoinRoomFailed?.Invoke("Failed to create room");
            connectionState = ConnectionState.ConnectedToMaster;
        }
    }

    private IEnumerator JoinRoomCoroutine(string roomName)
    {

        Dictionary<string, object> roomData = null;
        bool checkComplete = false;

        asyncWorker.EnqueueOperation(async () =>
        {
            try
            {
                var snapshot = await database.Child($"rooms/{roomName}").GetValueAsync();
                if (snapshot.Exists)
                {
                    roomData = snapshot.Value as Dictionary<string, object>;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to check room: {e}");
            }
            checkComplete = true;
        });

        yield return new WaitUntil(() => checkComplete);

        if (roomData == null)
        {
            OnJoinRoomFailed?.Invoke("Room does not exist");
            connectionState = ConnectionState.ConnectedToMaster;
            yield break;
        }

        var playerCount = Convert.ToInt32(roomData.GetValueOrDefault("playerCount", 0));
        var maxPlayers = Convert.ToInt32(roomData.GetValueOrDefault("maxPlayers", maxPlayersPerRoom));
        var isOpen = Convert.ToBoolean(roomData.GetValueOrDefault("isOpen", true));

        if (playerCount >= maxPlayers || !isOpen)
        {
            OnJoinRoomFailed?.Invoke("Room is full or closed");
            connectionState = ConnectionState.ConnectedToMaster;
            yield break;
        }


        bool joinComplete = false;
        bool joinSuccess = false;

        asyncWorker.EnqueueOperation(async () =>
        {
            try
            {
                var playerData = localPlayer.ToDict();
                await database.Child($"rooms/{roomName}/players/{localPlayer.userId}").SetValueAsync(playerData);
                joinSuccess = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to join room: {e}");
            }
            joinComplete = true;
        });

        yield return new WaitUntil(() => joinComplete);

        if (joinSuccess)
        {
            currentRoom = roomName;
            connectionState = ConnectionState.ConnectedToRoom;


            roomRef = database.Child($"rooms/{roomName}");
            roomPlayersRef = roomRef.Child("players");
            roomRpcRef = roomRef.Child("rpc");

            StartNetworkSystems();
            OnJoinedRoom?.Invoke(roomName);
        }
        else
        {
            OnJoinRoomFailed?.Invoke("Failed to join room");
            connectionState = ConnectionState.ConnectedToMaster;
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
                        var isOpen = Convert.ToBoolean(roomData.GetValueOrDefault("isOpen", true));
                        var playerCount = Convert.ToInt32(roomData.GetValueOrDefault("playerCount", 0));
                        var maxPlayers = Convert.ToInt32(roomData.GetValueOrDefault("maxPlayers", maxPlayersPerRoom));

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

    private IEnumerator JoinOrCreateRoomCoroutine(string roomName, RoomOptions options)
    {
        yield return StartCoroutine(JoinRoomCoroutine(roomName));
        if (connectionState != ConnectionState.ConnectedToRoom)
        {
            yield return StartCoroutine(CreateRoomCoroutine(roomName, options));
        }
    }


    private void StartNetworkSystems()
    {

        StartCoroutine(OptimizedPlayerSyncLoop());


        StartCoroutine(OptimizedRpcPollingLoop());


        SetupOptimizedPresenceTracking();
    }

    private IEnumerator OptimizedPlayerSyncLoop()
    {
        var wait = new WaitForSeconds(playerSyncInterval);
        var lastPlayerHash = 0;
        var consecutiveNoChanges = 0;

        while (inRoom)
        {
            asyncWorker?.EnqueueOperation(async () =>
            {
                try
                {
                    var snapshot = await roomPlayersRef.GetValueAsync();
                    if (!snapshot.Exists) return;


                    int currentHash = snapshot.GetHashCode();
                    if (currentHash == lastPlayerHash)
                    {
                        consecutiveNoChanges++;

                        if (consecutiveNoChanges > 5)
                        {
                            await Task.Delay(1000);
                        }
                        return;
                    }

                    lastPlayerHash = currentHash;
                    consecutiveNoChanges = 0;

                    var newPlayers = new Dictionary<string, NetPlayer>();
                    var changes = new List<PlayerChange>();


                    foreach (var child in snapshot.Children)
                    {
                        var player = NetPlayer.FromSnapshot(child);
                        if (player != null)
                        {
                            newPlayers[player.userId] = player;

                            if (!playerList.ContainsKey(player.userId))
                            {
                                changes.Add(new PlayerChange { player = player, type = ChangeType.Joined });
                            }
                        }
                    }


                    foreach (var existingPlayer in playerList.Values)
                    {
                        if (!newPlayers.ContainsKey(existingPlayer.userId))
                        {
                            changes.Add(new PlayerChange { player = existingPlayer, type = ChangeType.Left });
                        }
                    }


                    if (changes.Count > 0 || newPlayers.Count != playerList.Count)
                    {
                        mainThreadActions.Enqueue(() => ApplyPlayerChangesOptimized(newPlayers, changes));
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Player sync failed: {e.Message}");
                }
            });

            yield return wait;
        }
    }

    private IEnumerator OptimizedRpcPollingLoop()
    {
        const float baseInterval = 0.05f;
        const int maxBatchSize = 15;
        var adaptiveWait = new WaitForSeconds(baseInterval);

        while (inRoom)
        {
            float startTime = Time.realtimeSinceStartup;


            if (incomingRpcQueue.Count > 20)
            {
                adaptiveWait = new WaitForSeconds(0.033f);
            }
            else if (incomingRpcQueue.Count < 5)
            {
                adaptiveWait = new WaitForSeconds(0.1f);
            }
            else
            {
                adaptiveWait = new WaitForSeconds(baseInterval);
            }

            asyncWorker?.EnqueueOperation(async () =>
            {
                try
                {

                    var snapshot = await roomRpcRef
                        .OrderByChild("timestamp")
                        .StartAt(lastRpcTimestamp + 1)
                        .LimitToFirst(maxBatchSize)
                        .GetValueAsync();

                    if (!snapshot.Exists) return;

                    var rpcsToProcess = new List<RpcData>();
                    var rpcsToCleanup = new List<string>();
                    long maxTimestamp = lastRpcTimestamp;


                    foreach (var child in snapshot.Children)
                    {
                        var rpcData = child.Value as Dictionary<string, object>;
                        if (rpcData == null) continue;

                        string senderId = rpcData["senderId"].ToString();
                        if (senderId == localPlayer.userId) continue;

                        var rpc = new RpcData
                        {
                            methodName = rpcData["method"].ToString(),
                            senderId = senderId,
                            targetId = rpcData.GetValueOrDefault("targetId", "").ToString(),
                            parameters = rpcData.ContainsKey("params") ?
                                ((List<object>)rpcData["params"]).ToArray() : Array.Empty<object>(),
                            timestamp = Convert.ToInt64(rpcData["timestamp"]),
                            target = (RpcTarget)Enum.Parse(typeof(RpcTarget), rpcData["target"].ToString()),
                            buffered = Convert.ToBoolean(rpcData.GetValueOrDefault("buffered", false)),
                            rpcId = child.Key
                        };

                        rpcsToProcess.Add(rpc);
                        maxTimestamp = Math.Max(maxTimestamp, rpc.timestamp);

                        if (!rpc.buffered)
                        {
                            rpcsToCleanup.Add(child.Key);
                        }
                    }


                    lastRpcTimestamp = maxTimestamp;


                    foreach (var rpc in rpcsToProcess)
                    {

                        if (incomingRpcQueue.Count < 100)
                        {
                            incomingRpcQueue.Enqueue(rpc);
                        }
                        else
                        {
                            Debug.LogWarning("RPC queue full, dropping message");
                            break;
                        }
                    }


                    if (rpcsToCleanup.Count > 0 && isMasterClient)
                    {
                        _ = CleanupRpcsAsync(rpcsToCleanup);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"RPC polling failed: {e.Message}");
                }
            });

            yield return adaptiveWait;
        }
    }

    private async Task CleanupRpcsAsync(List<string> rpcIds)
    {
        try
        {

            var batch = new Dictionary<string, object>();
            foreach (var rpcId in rpcIds.Take(10))
            {
                batch[rpcId] = null;
            }

            if (batch.Count > 0)
            {
                await roomRpcRef.UpdateChildrenAsync(batch);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"RPC cleanup failed: {e.Message}");
        }
    }

    private void SetupOptimizedPresenceTracking()
    {
        if (database != null && localPlayer != null && inRoom)
        {
            presenceRef = roomPlayersRef.Child(localPlayer.userId).Child("presence");
            playerRef = roomPlayersRef.Child(localPlayer.userId);


            asyncWorker?.EnqueueOperation(async () =>
            {
                try
                {
                    await presenceRef.OnDisconnect().SetValue(false);
                    await playerRef.OnDisconnect().RemoveValue();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Presence setup failed: {e}");
                }
            });


            StartCoroutine(OptimizedPresenceLoop());
        }
    }

    private IEnumerator OptimizedPresenceLoop()
    {

        var wait = new WaitForSeconds(30f);
        var lastPresenceUpdate = 0f;

        while (inRoom && presenceRef != null)
        {

            if (Time.time - lastPresenceUpdate > 25f)
            {
                asyncWorker?.EnqueueOperation(async () =>
                {
                    try
                    {
                        await presenceRef.SetValueAsync(true);
                        lastPresenceUpdate = Time.time;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"Presence update failed: {e.Message}");
                    }
                });
            }

            yield return wait;
        }
    }


    private void ApplyPlayerChangesOptimized(Dictionary<string, NetPlayer> newPlayers, List<PlayerChange> changes)
    {
        try
        {

            var firebaseUpdates = new Dictionary<string, object>();
            bool needsFirebaseUpdate = false;


            playerList.Clear();
            foreach (var kvp in newPlayers)
            {
                playerList[kvp.Key] = kvp.Value;
            }


            if (localPlayer != null && newPlayers.ContainsKey(localPlayer.userId))
            {
                var updatedLocalPlayer = newPlayers[localPlayer.userId];
                localPlayer.isMasterClient = updatedLocalPlayer.isMasterClient;

                if (updatedLocalPlayer.customProperties != null)
                {
                    foreach (var prop in updatedLocalPlayer.customProperties)
                    {
                        localPlayer.customProperties[prop.Key] = prop.Value;
                    }
                }
            }


            var joinedPlayers = changes.Where(c => c.type == ChangeType.Joined).ToList();
            var leftPlayers = changes.Where(c => c.type == ChangeType.Left).ToList();

            foreach (var change in joinedPlayers)
            {
                if (change.player.userId != localPlayer?.userId)
                {
                    OnPlayerEnteredRoom?.Invoke(change.player);
                }
            }

            foreach (var change in leftPlayers)
            {
                OnPlayerLeftRoom?.Invoke(change.player);

                if (change.player.isMasterClient && playerList.Count > 0)
                {
                    var newMaster = HandleMasterClientTransferOptimized(firebaseUpdates);
                    if (newMaster != null)
                    {
                        needsFirebaseUpdate = true;
                    }
                }
            }


            if (isMasterClient)
            {
                firebaseUpdates[$"rooms/{currentRoom}/playerCount"] = playerList.Count;
                needsFirebaseUpdate = true;
            }


            if (needsFirebaseUpdate && firebaseUpdates.Count > 0)
            {
                asyncWorker?.EnqueueOperation(async () =>
                {
                    try
                    {
                        await database.UpdateChildrenAsync(firebaseUpdates);
                        Debug.Log($"Applied {firebaseUpdates.Count} Firebase updates in batch");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"Batch Firebase update failed: {e.Message}");
                    }
                });
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"ApplyPlayerChangesOptimized failed: {e}");
        }
    }


    private NetPlayer HandleMasterClientTransferOptimized(Dictionary<string, object> firebaseUpdates)
    {
        if (playerList.Count == 0) return null;

        var potentialMaster = playerList.Values
            .Where(p => p.userId != null)
            .OrderBy(p => p.userId)
            .FirstOrDefault();

        if (potentialMaster != null)
        {
            potentialMaster.isMasterClient = true;


            firebaseUpdates[$"rooms/{currentRoom}/masterClientId"] = potentialMaster.userId;
            firebaseUpdates[$"rooms/{currentRoom}/players/{potentialMaster.userId}/isMasterClient"] = true;


            if (potentialMaster.userId == localPlayer?.userId)
            {
                localPlayer.isMasterClient = true;
                Debug.Log("We are now the master client");
            }

            Debug.Log($"Master client transferred to: {potentialMaster.nickName}");
            return potentialMaster;
        }

        return null;
    }


    public static void RPC(string methodName, RpcTarget target, params object[] parameters)
    {
        if (!inRoom) return;

        bool isBuffered = target == RpcTarget.AllBuffered || target == RpcTarget.OthersBuffered;
        string targetId = "";

        if (target == RpcTarget.Host)
        {
            var master = playerList.Values.FirstOrDefault(p => p.isMasterClient);
            if (master != null) targetId = master.userId;
        }


        if (!isBuffered)
        {
            string coalescingKey = $"{localPlayer.userId}_{methodName}";

            lock (Instance.syncLock)
            {
                if (Instance.coalescedRpcs.ContainsKey(coalescingKey))
                {

                    var existing = Instance.coalescedRpcs[coalescingKey];
                    existing.data["params"] = parameters?.ToList() ?? new List<object>();
                    existing.data["timestamp"] = ServerValue.Timestamp;
                    existing.lastUpdateTime = Time.realtimeSinceStartup;
                    Instance.coalescedRpcs[coalescingKey] = existing;
                }
                else
                {

                    var rpcData = new Dictionary<string, object>
                    {
                        ["method"] = methodName,
                        ["senderId"] = localPlayer.userId,
                        ["params"] = parameters?.ToList() ?? new List<object>(),
                        ["timestamp"] = ServerValue.Timestamp,
                        ["target"] = target.ToString(),
                        ["targetId"] = targetId,
                        ["buffered"] = false
                    };

                    var rpcRef = Instance.roomRpcRef.Child(coalescingKey);
                    Instance.coalescedRpcs[coalescingKey] = new RpcCoalesceData
                    {
                        data = rpcData,
                        reference = rpcRef,
                        lastUpdateTime = Time.realtimeSinceStartup
                    };
                }
            }


            if (Instance.coalescedRpcs.Count == 1)
            {
                Instance.StartCoroutine(Instance.ProcessCoalescedRpcs());
            }
        }
        else
        {

            var rpcData = new Dictionary<string, object>
            {
                ["method"] = methodName,
                ["senderId"] = localPlayer.userId,
                ["params"] = parameters?.ToList() ?? new List<object>(),
                ["timestamp"] = ServerValue.Timestamp,
                ["target"] = target.ToString(),
                ["targetId"] = targetId,
                ["buffered"] = true
            };

            var rpcRef = Instance.roomRpcRef.Push();
            Instance.asyncWorker?.EnqueueOperation(async () =>
            {
                await rpcRef.SetValueAsync(rpcData);
            });
        }


        if (target == RpcTarget.All || target == RpcTarget.AllBuffered)
        {
            ExecuteRPC(methodName, parameters);
        }
    }

    private IEnumerator ProcessCoalescedRpcs()
    {
        const float coalescingDelay = 0.033f;
        var wait = new WaitForSeconds(coalescingDelay);

        while (coalescedRpcs.Count > 0)
        {
            yield return wait;

            List<RpcCoalesceData> rpcsToSend;
            List<string> keysToRemove = new List<string>();

            lock (syncLock)
            {
                rpcsToSend = new List<RpcCoalesceData>();
                float currentTime = Time.realtimeSinceStartup;

                foreach (var kvp in coalescedRpcs)
                {
                    if (currentTime - kvp.Value.lastUpdateTime >= coalescingDelay)
                    {
                        rpcsToSend.Add(kvp.Value);
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    coalescedRpcs.Remove(key);
                }
            }


            if (rpcsToSend.Count > 0)
            {

                for (int i = 0; i < rpcsToSend.Count; i += 3)
                {
                    var chunk = rpcsToSend.Skip(i).Take(3).ToList();

                    asyncWorker?.EnqueueOperation(async () =>
                    {
                        try
                        {
                            var tasks = chunk.Select(rpc => rpc.reference.SetValueAsync(rpc.data));
                            await Task.WhenAll(tasks);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"Coalesced RPC batch failed: {e.Message}");
                        }
                    });


                    if (i + 3 < rpcsToSend.Count)
                    {
                        yield return new WaitForSeconds(0.01f);
                    }
                }
            }
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
        Instance.networkObjects.Remove(networkId);
    }


    public static void SetCustomPlayerProperties(Dictionary<string, object> properties)
    {
        if (localPlayer != null && properties != null)
        {
            foreach (var prop in properties)
            {
                localPlayer.customProperties[prop.Key] = prop.Value;
            }

            if (inRoom)
            {
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

    public static void SetCustomRoomProperties(Dictionary<string, object> properties)
    {
        if (!inRoom || !isMasterClient || properties == null) return;

        var updates = new Dictionary<string, object>();
        foreach (var prop in properties)
        {
            updates[$"customProperties/{prop.Key}"] = prop.Value;
        }

        Instance.asyncWorker?.EnqueueOperation(async () =>
        {
            await Instance.roomRef.UpdateChildrenAsync(updates);
        });
    }


    public static void GracefulDisconnect()
    {
        if (Instance != null)
        {
            Instance.StartCoroutine(Instance.GracefulShutdown());
        }
    }

    public static bool IsGracefullyDisconnecting => Instance != null && Instance.isGracefullyDisconnecting;

    public static void ForceDisconnect()
    {
        if (Instance != null)
        {
            Instance.StopAllCoroutines();
            Instance.CleanupLocalState();

            if (Instance.auth != null && Instance.user != null)
            {
                Instance.auth.SignOut();
            }

            connectionState = ConnectionState.Disconnected;
            OnDisconnected?.Invoke();
        }
    }

    private void CleanupLocalState()
    {

        cancellationTokenSource?.Cancel();
        asyncWorker?.Stop();


        lock (syncLock)
        {
            coalescedRpcs.Clear();
        }

        while (incomingRpcQueue.TryDequeue(out _)) { }
        while (mainThreadActions.TryDequeue(out _)) { }

        rpcReferences.Clear();
        playerList.Clear();
        networkObjects.Clear();

        if (localPlayer != null)
        {
            localPlayer.isMasterClient = false;
        }


        currentRoom = null;
        presenceRef = null;
        playerRef = null;
        roomRef = null;
        roomPlayersRef = null;
        roomRpcRef = null;
        lastRpcTimestamp = 0;

        connectionState = ConnectionState.ConnectedToMaster;
        OnLeftRoom?.Invoke();

        Debug.Log("Local state cleaned up");
    }


    private void LogPerformanceMetrics()
    {
        if (frameTimes.Count > 0)
        {
            float avgFrameTime = frameTimes.Average();
            float maxFrameTime = frameTimes.Max();
            int queuedActions = 0;
            int queuedRpcs = 0;


            var tempQueue = new Queue<System.Action>();
            while (mainThreadActions.TryDequeue(out var action))
            {
                tempQueue.Enqueue(action);
                queuedActions++;
            }
            while (tempQueue.Count > 0)
            {
                mainThreadActions.Enqueue(tempQueue.Dequeue());
            }

            var tempRpcQueue = new Queue<RpcData>();
            while (incomingRpcQueue.TryDequeue(out var rpc))
            {
                tempRpcQueue.Enqueue(rpc);
                queuedRpcs++;
            }
            while (tempRpcQueue.Count > 0)
            {
                incomingRpcQueue.Enqueue(tempRpcQueue.Dequeue());
            }

            Debug.Log($"FireNetwork Performance:" +
                     $"\nAvg Frame Time: {avgFrameTime * 1000:F2}ms" +
                     $"\nMax Frame Time: {maxFrameTime * 1000:F2}ms" +
                     $"\nQueued Actions: {queuedActions}" +
                     $"\nQueued RPCs: {queuedRpcs}" +
                     $"\nCoalesced RPCs: {coalescedRpcs.Count}" +
                     $"\nNetwork Objects: {networkObjects.Count}" +
                     $"\nRPCs This Frame: {processedRpcsThisFrame}");
        }
    }

    public static void LogPerformanceStats()
    {
        Instance?.LogPerformanceMetrics();
    }


    public static void CleanupOldRpcs()
    {
        if (!inRoom || !isMasterClient) return;

        Instance.asyncWorker?.EnqueueOperation(async () =>
        {
            try
            {
                long cutoffTime = DateTimeOffset.Now.AddMinutes(-5).ToUnixTimeMilliseconds();

                var oldRpcsSnapshot = await Instance.roomRpcRef
                    .OrderByChild("timestamp")
                    .EndAt(cutoffTime)
                    .GetValueAsync();

                if (!oldRpcsSnapshot.Exists) return;

                var batch = new Dictionary<string, object>();
                foreach (var child in oldRpcsSnapshot.Children)
                {
                    var rpcData = child.Value as Dictionary<string, object>;
                    if (rpcData != null && !Convert.ToBoolean(rpcData.GetValueOrDefault("buffered", false)))
                    {
                        batch[child.Key] = null;
                    }
                }

                if (batch.Count > 0)
                {
                    await Instance.roomRpcRef.UpdateChildrenAsync(batch);
                    Debug.Log($"Cleaned up {batch.Count} old RPCs");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"RPC cleanup failed: {e}");
            }
        });
    }
}


public class FirebaseAsyncWorker
{
    private readonly ConcurrentQueue<Func<Task>> operationQueue = new ConcurrentQueue<Func<Task>>();
    private readonly Thread workerThread;
    private readonly CancellationToken cancellationToken;
    private volatile bool isRunning = false;

    public FirebaseAsyncWorker(CancellationToken token)
    {
        cancellationToken = token;
        workerThread = new Thread(WorkerLoop)
        {
            Name = "FirebaseAsyncWorker",
            IsBackground = true,
            Priority = System.Threading.ThreadPriority.BelowNormal
        };
        Start();
    }

    public void Start()
    {
        if (isRunning) return;

        isRunning = true;
        workerThread.Start();
    }

    public void Stop()
    {
        isRunning = false;
        workerThread?.Join(2000);
    }

    public void EnqueueOperation(Func<Task> operation)
    {
        if (isRunning && !cancellationToken.IsCancellationRequested)
        {
            operationQueue.Enqueue(operation);
        }
    }

    private async void WorkerLoop()
    {
        var semaphore = new SemaphoreSlim(3, 3);
        var activeTasks = new List<Task>();

        while (isRunning && !cancellationToken.IsCancellationRequested)
        {
            try
            {

                activeTasks.RemoveAll(t => t.IsCompleted);


                int processed = 0;
                while (processed < 10 && operationQueue.TryDequeue(out var operation))
                {
                    await semaphore.WaitAsync(cancellationToken);

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await operation();
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"Firebase operation failed: {e}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);

                    activeTasks.Add(task);
                    processed++;
                }


                await Task.Delay(10, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogError($"Firebase worker error: {e}");
                await Task.Delay(1000, cancellationToken);
            }
        }


        try
        {
            await Task.WhenAll(activeTasks);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Error waiting for tasks completion: {e}");
        }

        semaphore.Dispose();
    }
}