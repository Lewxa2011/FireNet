using System;
using UnityEngine;

public class NetworkTransform : MonoBehaviour, INetworkBehaviour
{
    [Header("Sync Options")]
    public bool syncPosition = true;
    public bool syncRotation = true;
    public bool syncScale = false;

    [Header("Performance")]
    public float sendRate = 10f;
    public float lerpRate = 15f;
    public float snapDistance = 5f;

    [Header("Thresholds")]
    public float positionThreshold = 0.1f;
    public float rotationThreshold = 5f;

    private NetworkView networkView;
    private TransformState lastSentState;
    private TransformState networkState;
    private float nextSendTime;

    [System.Serializable]
    private struct TransformState
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public float timestamp;

        public TransformState(Transform t, float time)
        {
            position = t.position;
            rotation = t.rotation;
            scale = t.localScale;
            timestamp = time;
        }

        public bool HasSignificantChange(TransformState other, float posThreshold, float rotThreshold)
        {
            return Vector3.Distance(position, other.position) > posThreshold ||
                   Quaternion.Angle(rotation, other.rotation) > rotThreshold;
        }
    }

    public string NetworkId => networkView?.viewId ?? "";

    private void Start()
    {
        networkView = GetComponentInParent<NetworkView>();
        if (networkView == null)
        {
            Debug.LogError("NetworkView not found on this object or parent");
            enabled = false;
            return;
        }

        var currentState = new TransformState(transform, Time.time);
        networkState = currentState;
        lastSentState = currentState;
    }

    private void Update()
    {
        if (networkView.isMine)
        {
            if (Time.time >= nextSendTime && ShouldSendUpdate())
            {
                SendTransform();
                nextSendTime = Time.time + (1f / sendRate);
            }
        }
        else
        {
            InterpolateTransform();
        }
    }

    private bool ShouldSendUpdate()
    {
        var currentState = new TransformState(transform, Time.time);
        return currentState.HasSignificantChange(lastSentState, positionThreshold, rotationThreshold);
    }

    private void SendTransform()
    {
        var currentState = new TransformState(transform, Time.time);

        // Use the new serialization system for efficient transfer
        object[] data = BuildTransformData(currentState);
        networkView.RPC("ReceiveTransform", RpcTarget.Others, data);

        lastSentState = currentState;
    }

    private object[] BuildTransformData(TransformState state)
    {
        var data = new System.Collections.Generic.List<object>();

        if (syncPosition)
        {
            data.Add(state.position);
        }

        if (syncRotation)
        {
            data.Add(state.rotation);
        }

        if (syncScale)
        {
            data.Add(state.scale);
        }

        data.Add(state.timestamp);
        return data.ToArray();
    }

    private void ReceiveTransform(object[] data)
    {
        if (data == null || data.Length < 1) return;

        int index = 0;
        var receivedState = new TransformState();

        if (syncPosition && data.Length > index && data[index] is Vector3 pos)
        {
            receivedState.position = pos;
            index++;
        }

        if (syncRotation && data.Length > index && data[index] is Quaternion rot)
        {
            receivedState.rotation = rot;
            index++;
        }

        if (syncScale && data.Length > index && data[index] is Vector3 scale)
        {
            receivedState.scale = scale;
            index++;
        }

        if (data.Length > index && data[index] is float timestamp)
        {
            receivedState.timestamp = timestamp;
        }

        networkState = receivedState;
    }

    private void InterpolateTransform()
    {
        if (syncPosition)
        {
            Vector3 targetPosition = networkState.position;
            float distance = Vector3.Distance(transform.position, targetPosition);

            if (distance > snapDistance)
            {
                transform.position = targetPosition;
            }
            else
            {
                transform.position = Vector3.Lerp(transform.position, targetPosition, lerpRate * Time.deltaTime);
            }
        }

        if (syncRotation)
        {
            transform.rotation = Quaternion.Lerp(transform.rotation, networkState.rotation, lerpRate * Time.deltaTime);
        }

        if (syncScale)
        {
            transform.localScale = Vector3.Lerp(transform.localScale, networkState.scale, lerpRate * Time.deltaTime);
        }
    }

    public void OnRPC(string methodName, object[] parameters)
    {
        if (methodName.StartsWith(NetworkId + ":"))
        {
            string actualMethod = methodName.Substring(NetworkId.Length + 1);
            if (actualMethod == "ReceiveTransform")
            {
                ReceiveTransform(parameters);
            }
        }
    }
}