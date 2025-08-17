#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections;
using UnityEngine;

public class NetworkManager : MonoBehaviour
{
    private const string logTag = "[NetworkManager] ";

    public static NetworkManager Instance;

    public Transform rigHead;
    public Transform rigLeftHand;
    public Transform rigRightHand;

    public GameObject netPlayerPrefab;
    [HideInInspector]
    public NetworkPlayer localNetP;

    private void Log(string message)
    {
        Debug.Log(logTag + message);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(Instance);
        }
        else
        {
            Instance = this;
        }
    }

    private void Start()
    {
        FireNetwork.OnConnectedToMaster += FireNetwork_OnConnectedToMaster;
        FireNetwork.OnJoinedRoom += FireNetwork_OnJoinedRoom;
        FireNetwork.OnLeftRoom += FireNetwork_OnLeftRoom;

        Log("Connecting to Master");

        FireNetwork.ConnectToMaster();
    }

    private void FireNetwork_OnLeftRoom()
    {
        Log("Left room event received.");

        if (localNetP != null) // it's getting destroyed before we're leaving the room with the cleanup shit so do this checkkk
        {
            FireNetwork.NetDestroy(localNetP.gameObject);
        }
    }

    private void FireNetwork_OnJoinedRoom(string roomName)
    {
        Log("Joined " + roomName);

        localNetP = FireNetwork.NetInstantiate(netPlayerPrefab, GorillaLocomotion.Player.Instance.transform.position, GorillaLocomotion.Player.Instance.transform.rotation).GetComponent<NetworkPlayer>();
    }

    private void FireNetwork_OnConnectedToMaster()
    {
        Log("Connected To Master!!");

        FireNetwork.JoinOrCreateRoom("SussyBlud");
    }
}