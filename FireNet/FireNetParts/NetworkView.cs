using System;
using UnityEngine;

public class NetworkView : MonoBehaviour, INetworkBehaviour
{
    [HideInInspector]
    public string viewId;
    public bool isMine = true;
    public string NetworkId => viewId;

    private void Start()
    {
        if (string.IsNullOrEmpty(viewId))
            viewId = Guid.NewGuid().ToString();

        FireNetwork.RegisterNetworkObject(this);
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