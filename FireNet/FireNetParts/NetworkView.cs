using System;
using UnityEngine;

public class NetworkView : MonoBehaviour, INetworkBehaviour
{
    [HideInInspector]
    public string viewId;
    public bool isMine = true;
    public string NetworkId => viewId;
    public bool isMasterClient = false;

    private void Start()
    {
        if (string.IsNullOrEmpty(viewId))
            viewId = Guid.NewGuid().ToString();

        FireNetwork.RegisterNetworkObject(this);
    }

    private void Update()
    {
        if (!isMine) return;

        isMasterClient = FireNetwork.isMasterClient;
    }

    private void OnDestroy()
    {
        FireNetwork.UnregisterNetworkObject(viewId);
    }

    public void RPC(string methodName, RpcTarget target, params object[] parameters)
    {
        if (!isMine) return;
        FireNetwork.RPC($"{viewId}:{methodName}", target, parameters);
    }

    public void OnRPC(string methodName, object[] parameters)
    {
        if (methodName.StartsWith(viewId + ":"))
        {
            string actualMethod = methodName.Substring(viewId.Length + 1);
            SendMessage(actualMethod, parameters, SendMessageOptions.DontRequireReceiver);
        }
        else if (methodName == "OnInstantiate" && parameters.Length >= 9)
        {
            // Handle instantiation RPC
            string prefabName = parameters[0].ToString();
            Vector3 position = new Vector3(Convert.ToSingle(parameters[1]), Convert.ToSingle(parameters[2]), Convert.ToSingle(parameters[3]));
            Quaternion rotation = new Quaternion(Convert.ToSingle(parameters[4]), Convert.ToSingle(parameters[5]), Convert.ToSingle(parameters[6]), Convert.ToSingle(parameters[7]));
            string networkId = parameters[8].ToString();

            // Find the prefab to instantiate
            GameObject prefab = Resources.Load<GameObject>(prefabName);
            if (prefab == null)
            {
                Debug.LogError($"Prefab '{prefabName}' not found in Resources. Make sure the prefab is in a 'Resources' folder.");
                return;
            }

            // Instantiate the object and configure its NetworkView
            GameObject instance = Instantiate(prefab, position, rotation);
            NetworkView networkView = instance.GetComponent<NetworkView>();
            if (networkView == null)
            {
                networkView = instance.AddComponent<NetworkView>();
            }

            networkView.viewId = networkId;
            networkView.isMine = false; // It's a remote object, not owned by this client
            FireNetwork.RegisterNetworkObject(networkView);
        }
        else if (methodName == "OnDestroy" && parameters.Length >= 1)
        {
            if (parameters[0].ToString() == viewId)
            {
                Destroy(gameObject);
            }
        }
    }
}