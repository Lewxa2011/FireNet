using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FireNet
{
    public class NetworkLobby : MonoBehaviour
    {
        [Header("Lobby Settings")]
        public int maxPlayersInLobby = 20;
        public string lobbyName = "MainLobby";

        public static event System.Action<NetPlayer> OnPlayerJoinedLobby;
        public static event System.Action<NetPlayer> OnPlayerLeftLobby;
        public static event System.Action<Dictionary<string, NetPlayer>> OnLobbyPlayerListUpdate;

        public static Dictionary<string, NetPlayer> lobbyPlayers { get; private set; } = new Dictionary<string, NetPlayer>();
        public static bool inLobby { get; private set; } = false;

        private void Start()
        {
            FireNetwork.OnConnectedToMaster += JoinLobby;
            FireNetwork.OnDisconnected += LeaveLobby;
        }

        private void OnDestroy()
        {
            FireNetwork.OnConnectedToMaster -= JoinLobby;
            FireNetwork.OnDisconnected -= LeaveLobby;
        }

        public static void JoinLobby()
        {
            if (FireNetwork.connectionState != ConnectionState.ConnectedToMaster) return;
            FireNetwork.Instance.StartCoroutine(JoinLobbyCoroutine());
        }

        public static void LeaveLobby()
        {
            if (!inLobby) return;
            FireNetwork.Instance.StartCoroutine(LeaveLobbyCoroutine());
        }

        private static IEnumerator JoinLobbyCoroutine()
        {
            var lobbyTask = FireNetwork.Instance.database.Child($"lobby/{FireNetwork.Instance.GetComponent<NetworkLobby>().lobbyName}/players/{FireNetwork.localPlayer.userId}")
                .SetValueAsync(FireNetwork.localPlayer.ToDict());

            yield return new WaitUntil(() => lobbyTask.IsCompleted);

            if (lobbyTask.IsCompletedSuccessfully)
            {
                inLobby = true;
                FireNetwork.Instance.StartCoroutine(LobbyPlayerSync());
            }
        }

        private static IEnumerator LeaveLobbyCoroutine()
        {
            var leaveTask = FireNetwork.Instance.database.Child($"lobby/{FireNetwork.Instance.GetComponent<NetworkLobby>().lobbyName}/players/{FireNetwork.localPlayer.userId}")
                .RemoveValueAsync();

            yield return new WaitUntil(() => leaveTask.IsCompleted);

            inLobby = false;
            lobbyPlayers.Clear();
        }

        private static IEnumerator LobbyPlayerSync()
        {
            while (inLobby)
            {
                var playersTask = FireNetwork.Instance.database.Child($"lobby/{FireNetwork.Instance.GetComponent<NetworkLobby>().lobbyName}/players").GetValueAsync();
                yield return new WaitUntil(() => playersTask.IsCompleted);

                if (playersTask.IsCompletedSuccessfully)
                {
                    var oldPlayers = new Dictionary<string, NetPlayer>(lobbyPlayers);
                    lobbyPlayers.Clear();

                    foreach (var child in playersTask.Result.Children)
                    {
                        var player = NetPlayer.FromSnapshot(child);
                        if (player != null)
                        {
                            lobbyPlayers[player.userId] = player;
                            if (!oldPlayers.ContainsKey(player.userId) && player.userId != FireNetwork.localPlayer.userId)
                            {
                                OnPlayerJoinedLobby?.Invoke(player);
                            }
                        }
                    }

                    foreach (var oldPlayer in oldPlayers.Values)
                    {
                        if (!lobbyPlayers.ContainsKey(oldPlayer.userId))
                        {
                            OnPlayerLeftLobby?.Invoke(oldPlayer);
                        }
                    }

                    OnLobbyPlayerListUpdate?.Invoke(lobbyPlayers);
                }
                yield return new WaitForSeconds(1f);
            }
        }
    }
}