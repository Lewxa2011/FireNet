using System;
using UnityEngine;

public class NetworkRigidbody : MonoBehaviour, INetworkBehaviour
{
    private Rigidbody rb;
    private NetworkView networkView;

    [Header("Sync Settings")]
    public bool syncVelocity = true;
    public bool syncAngularVelocity = true;
    public bool syncIsKinematic = false;
    public float sendRate = 10f;

    public string NetworkId => networkView?.viewId ?? "";
    private float lastSendTime;

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        networkView = GetComponentInParent<NetworkView>();

        if (rb == null || networkView == null)
        {
            Debug.LogError("NetworkRigidbody requires both Rigidbody component and NetworkView on this object or parent!");
            enabled = false;
        }
    }

    private void FixedUpdate()
    {
        if (networkView.isMine && Time.time - lastSendTime > 1f / sendRate)
        {
            SendRigidbodyState();
            lastSendTime = Time.time;
        }
    }

    private void SendRigidbodyState()
    {
        var data = new System.Collections.Generic.List<object>();

        if (syncVelocity)
            data.Add(rb.velocity);

        if (syncAngularVelocity)
            data.Add(rb.angularVelocity);

        if (syncIsKinematic)
            data.Add(rb.isKinematic);

        networkView.RPC("UpdateRigidbodyState", RpcTarget.Others, data.ToArray());
    }

    private void UpdateRigidbodyState(object[] data)
    {
        if (data == null || data.Length == 0) return;

        int index = 0;

        if (syncVelocity && data.Length > index && data[index] is Vector3 velocity)
        {
            rb.velocity = velocity;
            index++;
        }

        if (syncAngularVelocity && data.Length > index && data[index] is Vector3 angularVelocity)
        {
            rb.angularVelocity = angularVelocity;
            index++;
        }

        if (syncIsKinematic && data.Length > index && data[index] is bool isKinematic)
        {
            rb.isKinematic = isKinematic;
            index++;
        }
    }

    public void OnRPC(string methodName, object[] parameters)
    {
        if (methodName.StartsWith(NetworkId + ":"))
        {
            string actualMethod = methodName.Substring(NetworkId.Length + 1);
            if (actualMethod == "UpdateRigidbodyState")
            {
                UpdateRigidbodyState(parameters);
            }
        }
    }
}