

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class NetworkTransform : MonoBehaviour, INetworkBehaviour
{
    [Header("Sync Options")]
    public bool syncPosition = true;
    public bool syncRotation = true;
    public bool syncScale = false;

    [Header("Performance")]
    public float sendRate = 8f; 
    public float lerpRate = 20f; 
    public float snapDistance = 5f; 

    [Header("Compression")]
    public float positionThreshold = 0.05f; 
    public float rotationThreshold = 3f; 
    public float velocityThreshold = 0.1f; 

    [Header("Advanced Prediction")]
    public float predictionTime = 0.3f; 
    public float maxPredictionError = 3f; 
    public bool useSmoothing = true;
    public float smoothingFactor = 0.8f;

    private NetworkView networkView;

    
    private TransformState networkState;
    private TransformState predictedState;
    private TransformState lastSentState;

    
    private Vector3 velocitySmoothed;
    private Vector3 accelerationSmoothed;
    private float lastSendTime;
    private float lastReceiveTime;

    
    private bool hasRigidbody;
    private Rigidbody rb;
    private float nextSendTime;

    
    private float baseSendRate;
    private float currentSendRate;
    private float currentMovementMagnitude;
    private readonly Queue<float> movementHistory = new Queue<float>();

    [System.Serializable]
    private struct TransformState
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public Vector3 velocity;
        public Vector3 acceleration;
        public Vector3 angularVelocity;
        public float timestamp;

        public TransformState(Transform t, Vector3 vel, Vector3 accel, Vector3 angVel, float time)
        {
            position = t.position;
            rotation = t.rotation;
            scale = t.localScale;
            velocity = vel;
            acceleration = accel;
            angularVelocity = angVel;
            timestamp = time;
        }

        public bool HasSignificantChange(TransformState other, float posThreshold, float rotThreshold)
        {
            return Vector3.Distance(position, other.position) > posThreshold ||
                   Quaternion.Angle(rotation, other.rotation) > rotThreshold ||
                   Vector3.Distance(velocity, other.velocity) > 0.2f; 
        }
    }

    public string NetworkId => networkView?.viewId ?? "";

    private void Start()
    {
        networkView = transform.root.GetComponent<NetworkView>();
        if (networkView == null)
        {
            Debug.LogError("NetworkView not found on root object");
            enabled = false;
            return;
        }

        
        rb = GetComponent<Rigidbody>();
        hasRigidbody = rb != null;
        baseSendRate = sendRate;

        
        var currentState = new TransformState(transform, Vector3.zero, Vector3.zero, Vector3.zero, Time.time);
        networkState = currentState;
        predictedState = currentState;
        lastSentState = currentState;
    }

    private void Update()
    {
        if (networkView.isMine)
        {
            UpdateMovementTracking();
            AdaptiveSendRate();

            if (Time.time >= nextSendTime && ShouldSendUpdate())
            {
                SendTransformOptimized();
                nextSendTime = Time.time + (1f / currentSendRate);
            }
        }
        else
        {
            UpdatePredictionOptimized();
            InterpolateTransformOptimized();
        }
    }

    private void UpdateMovementTracking()
    {
        
        var currentState = new TransformState(transform, Vector3.zero, Vector3.zero, Vector3.zero, Time.time);

        if (lastSentState.timestamp > 0)
        {
            float deltaTime = currentState.timestamp - lastSentState.timestamp;
            if (deltaTime > 0)
            {
                Vector3 newVelocity = (currentState.position - lastSentState.position) / deltaTime;
                Vector3 newAcceleration = (newVelocity - velocitySmoothed) / deltaTime;

                
                if (useSmoothing)
                {
                    velocitySmoothed = Vector3.Lerp(velocitySmoothed, newVelocity, smoothingFactor);
                    accelerationSmoothed = Vector3.Lerp(accelerationSmoothed, newAcceleration, smoothingFactor);
                }
                else
                {
                    velocitySmoothed = newVelocity;
                    accelerationSmoothed = newAcceleration;
                }

                
                currentMovementMagnitude = velocitySmoothed.magnitude + (accelerationSmoothed.magnitude * 0.1f);

                
                movementHistory.Enqueue(currentMovementMagnitude);
                if (movementHistory.Count > 10)
                    movementHistory.Dequeue();
            }
        }
    }

    private void AdaptiveSendRate()
    {
        float avgMovement = movementHistory.Count > 0 ? movementHistory.Average() : 0f;

        if (avgMovement > 3f)
            currentSendRate = baseSendRate * 1.5f; 
        else if (avgMovement < 0.3f)
            currentSendRate = baseSendRate * 0.5f; 
        else
            currentSendRate = baseSendRate;

        
        currentSendRate = Mathf.Clamp(currentSendRate, 2f, 20f);
    }

    private bool ShouldSendUpdate()
    {
        var currentState = new TransformState(transform, velocitySmoothed, accelerationSmoothed, Vector3.zero, Time.time);

        
        bool significantChange = currentState.HasSignificantChange(lastSentState, positionThreshold, rotationThreshold);

        
        bool predictionError = Vector3.Distance(currentState.position, predictedState.position) > maxPredictionError;

        
        bool timeExpired = Time.time - lastSendTime > (2f / baseSendRate);

        return significantChange || predictionError || timeExpired;
    }

    private void SendTransformOptimized()
    {
        var currentState = new TransformState(transform, velocitySmoothed, accelerationSmoothed, Vector3.zero, Time.time);

        
        var compressedData = CompressTransformData(currentState);

        networkView.RPC("ReceiveTransformOptimized", RpcTarget.Others, compressedData);

        lastSentState = currentState;
        lastSendTime = Time.time;
    }

    private object[] CompressTransformData(TransformState state)
    {
        var data = new List<object>();

        
        if (syncPosition)
        {
            
            data.Add(Mathf.Round(state.position.x * 100f) / 100f);
            data.Add(Mathf.Round(state.position.y * 100f) / 100f);
            data.Add(Mathf.Round(state.position.z * 100f) / 100f);
        }

        
        if (syncRotation)
        {
            data.Add(state.rotation.x);
            data.Add(state.rotation.y);
            data.Add(state.rotation.z);
            data.Add(state.rotation.w);
        }

        
        if (syncScale)
        {
            data.Add(state.scale.x);
            data.Add(state.scale.y);
            data.Add(state.scale.z);
        }

        
        data.Add(state.velocity.x);
        data.Add(state.velocity.y);
        data.Add(state.velocity.z);

        
        data.Add(state.timestamp);

        return data.ToArray();
    }

    private void ReceiveTransformOptimized(object[] data)
    {
        if (data == null || data.Length < 7) return;

        int index = 0;
        var receivedState = new TransformState();

        
        if (syncPosition && data.Length > index + 2)
        {
            receivedState.position = new Vector3(
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++])
            );
        }

        
        if (syncRotation && data.Length > index + 3)
        {
            receivedState.rotation = new Quaternion(
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++])
            );
        }

        
        if (syncScale && data.Length > index + 2)
        {
            receivedState.scale = new Vector3(
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++])
            );
        }

        
        if (data.Length > index + 3)
        {
            receivedState.velocity = new Vector3(
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++]),
                Convert.ToSingle(data[index++])
            );
            receivedState.timestamp = Convert.ToSingle(data[index]);
        }

        
        float networkLag = Time.time - receivedState.timestamp;
        if (networkLag > 0 && networkLag < 1f) 
        {
            receivedState.position += receivedState.velocity * networkLag;
        }

        networkState = receivedState;
        lastReceiveTime = Time.time;
    }

    private void UpdatePredictionOptimized()
    {
        float timeSinceLastUpdate = Time.time - lastReceiveTime;

        if (timeSinceLastUpdate < predictionTime && timeSinceLastUpdate > 0)
        {
            predictedState = networkState;

            
            predictedState.position += networkState.velocity * timeSinceLastUpdate;

            
            if (hasRigidbody && rb.useGravity)
            {
                predictedState.position += Physics.gravity * (timeSinceLastUpdate * timeSinceLastUpdate * 0.5f);
            }
        }
        else
        {
            
            predictedState = networkState;
        }
    }

    private void InterpolateTransformOptimized()
    {
        if (syncPosition)
        {
            Vector3 targetPosition = predictedState.position;
            float distance = Vector3.Distance(transform.position, targetPosition);

            if (distance > snapDistance)
            {
                transform.position = targetPosition; 
            }
            else
            {
                
                float lerpSpeed = lerpRate * Time.deltaTime;
                if (distance > 1f) lerpSpeed *= 2f; 

                transform.position = Vector3.Lerp(transform.position, targetPosition, lerpSpeed);
            }
        }

        if (syncRotation)
        {
            transform.rotation = Quaternion.Lerp(transform.rotation, predictedState.rotation, lerpRate * Time.deltaTime);
        }

        if (syncScale)
        {
            transform.localScale = Vector3.Lerp(transform.localScale, predictedState.scale, lerpRate * Time.deltaTime);
        }
    }

    public void OnRPC(string methodName, object[] parameters)
    {
        if (methodName.StartsWith(NetworkId + ":"))
        {
            string actualMethod = methodName.Substring(NetworkId.Length + 1);
            if (actualMethod == "ReceiveTransformOptimized")
            {
                ReceiveTransformOptimized(parameters);
            }
        }
    }

    
    private void OnDrawGizmos()
    {
        if (!networkView || networkView.isMine) return;

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(networkState.position, 0.15f);

        Gizmos.color = Color.blue;
        Gizmos.DrawWireSphere(predictedState.position, 0.1f);

        Gizmos.color = Color.green;
        if (networkState.velocity.magnitude > 0.1f)
            Gizmos.DrawRay(networkState.position, networkState.velocity * 0.5f);
    }
}