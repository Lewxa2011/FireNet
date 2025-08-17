using System;
using UnityEngine;

public class NetworkTransform : MonoBehaviour, INetworkBehaviour
{
    [Header("Sync Options")]
    public bool syncPosition = true;
    public bool syncRotation = true;
    public bool syncScale = false;

    [Header("Performance - Optimized")]
    public float sendRate = 8f; // Slightly reduced from 10f
    public float lerpRate = 12f; // Slightly reduced for smoother movement
    public float snapDistance = 5f;

    [Header("Thresholds - Optimized for Data Reduction")]
    public float positionThreshold = 0.05f; // Reduced from 0.1f for better precision
    public float rotationThreshold = 2f; // Reduced from 5f
    public float velocityThreshold = 0.1f; // New: only sync if moving significantly

    [Header("Compression Settings")]
    public bool useCompression = true;
    public bool useDeltaCompression = true;
    public bool adaptiveSendRate = true; // Adjust send rate based on movement

    private NetworkView networkView;
    private TransformState lastSentState;
    private TransformState networkState;
    private TransformState previousNetworkState;
    private float nextSendTime;

    // Adaptive rate limiting
    private float currentSendRate;
    private Vector3 lastPosition;
    private float stationaryTime = 0f;
    private const float MAX_STATIONARY_TIME = 2f; // Stop sending after 2 seconds of no movement

    // Delta compression
    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation;
    private Vector3 lastSentScale;

    [System.Serializable]
    private struct TransformState
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public float timestamp;
        public Vector3 velocity; // For prediction

        public TransformState(Transform t, float time, Vector3 vel = default)
        {
            position = t.position;
            rotation = t.rotation;
            scale = t.localScale;
            timestamp = time;
            velocity = vel;
        }

        public bool HasSignificantChange(TransformState other, float posThreshold, float rotThreshold, float velThreshold)
        {
            float posDistance = Vector3.Distance(position, other.position);
            float rotAngle = Quaternion.Angle(rotation, other.rotation);
            float velMagnitude = velocity.magnitude;

            return posDistance > posThreshold ||
                   rotAngle > rotThreshold ||
                   velMagnitude > velThreshold;
        }
    }

    // Compressed transform data for network transmission
    [System.Serializable]
    private struct CompressedTransformData
    {
        public ushort x, y, z; // Compressed position (16-bit each)
        public byte rx, ry, rz, rw; // Compressed rotation (8-bit quaternion)
        public byte flags; // Bit flags for what data is included
        public float timestamp;

        // Bit flags
        public const byte POSITION_FLAG = 1 << 0;
        public const byte ROTATION_FLAG = 1 << 1;
        public const byte SCALE_FLAG = 1 << 2;
        public const byte VELOCITY_FLAG = 1 << 3;
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
        previousNetworkState = currentState;

        currentSendRate = sendRate;
        lastPosition = transform.position;

        // Initialize delta compression baseline
        lastSentPosition = transform.position;
        lastSentRotation = transform.rotation;
        lastSentScale = transform.localScale;
    }

    private void Update()
    {
        if (networkView.isMine)
        {
            UpdateAdaptiveSendRate();

            if (Time.time >= nextSendTime && ShouldSendUpdate())
            {
                SendTransform();
                nextSendTime = Time.time + (1f / currentSendRate);
            }
        }
        else
        {
            InterpolateTransform();
        }
    }

    private void UpdateAdaptiveSendRate()
    {
        if (!adaptiveSendRate) return;

        Vector3 currentPosition = transform.position;
        float distanceMoved = Vector3.Distance(currentPosition, lastPosition);

        if (distanceMoved < 0.001f) // Essentially not moving
        {
            stationaryTime += Time.deltaTime;

            // Reduce send rate when stationary
            if (stationaryTime > 1f)
            {
                currentSendRate = Mathf.Max(1f, sendRate * 0.2f); // 20% of normal rate
            }
            else
            {
                currentSendRate = sendRate * 0.5f; // 50% of normal rate
            }

            // Stop sending completely if stationary too long
            if (stationaryTime > MAX_STATIONARY_TIME)
            {
                currentSendRate = 0.5f; // Very low rate
            }
        }
        else
        {
            stationaryTime = 0f;

            // Increase send rate based on speed
            float speed = distanceMoved / Time.deltaTime;
            if (speed > 5f)
            {
                currentSendRate = sendRate * 1.5f; // Fast movement
            }
            else if (speed > 1f)
            {
                currentSendRate = sendRate; // Normal movement
            }
            else
            {
                currentSendRate = sendRate * 0.7f; // Slow movement
            }
        }

        lastPosition = currentPosition;
    }

    private bool ShouldSendUpdate()
    {
        // Don't send if we've been stationary too long
        if (stationaryTime > MAX_STATIONARY_TIME && !HasSignificantRotationChange())
            return false;

        Vector3 currentVelocity = (transform.position - lastPosition) / Time.deltaTime;
        var currentState = new TransformState(transform, Time.time, currentVelocity);

        return currentState.HasSignificantChange(lastSentState, positionThreshold, rotationThreshold, velocityThreshold);
    }

    private bool HasSignificantRotationChange()
    {
        return Quaternion.Angle(transform.rotation, lastSentRotation) > rotationThreshold;
    }

    private void SendTransform()
    {
        Vector3 currentVelocity = (transform.position - lastPosition) / Time.deltaTime;
        var currentState = new TransformState(transform, Time.time, currentVelocity);

        if (useCompression && useDeltaCompression)
        {
            SendDeltaCompressed(currentState);
        }
        else if (useCompression)
        {
            SendCompressed(currentState);
        }
        else
        {
            SendUncompressed(currentState);
        }

        lastSentState = currentState;

        // Update delta baseline
        lastSentPosition = transform.position;
        lastSentRotation = transform.rotation;
        lastSentScale = transform.localScale;
    }

    private void SendDeltaCompressed(TransformState state)
    {
        var deltaData = new System.Collections.Generic.List<object>();
        byte flags = 0;

        // Only send changed components
        if (syncPosition)
        {
            Vector3 deltaPos = state.position - lastSentPosition;
            if (deltaPos.magnitude > positionThreshold)
            {
                flags |= CompressedTransformData.POSITION_FLAG;
                deltaData.Add(CompressVector3(deltaPos, -50f, 50f)); // Delta range is smaller
            }
        }

        if (syncRotation)
        {
            Quaternion deltaRot = Quaternion.Inverse(lastSentRotation) * state.rotation;
            if (Quaternion.Angle(Quaternion.identity, deltaRot) > rotationThreshold)
            {
                flags |= CompressedTransformData.ROTATION_FLAG;
                deltaData.Add(CompressQuaternion(deltaRot));
            }
        }

        if (syncScale)
        {
            Vector3 deltaScale = state.scale - lastSentScale;
            if (deltaScale.magnitude > 0.01f)
            {
                flags |= CompressedTransformData.SCALE_FLAG;
                deltaData.Add(CompressVector3(deltaScale, -5f, 5f));
            }
        }

        // Add velocity for prediction if object is moving
        if (state.velocity.magnitude > velocityThreshold)
        {
            flags |= CompressedTransformData.VELOCITY_FLAG;
            deltaData.Add(CompressVector3(state.velocity, -20f, 20f));
        }

        // Only send if we have data to send
        if (flags > 0)
        {
            deltaData.Insert(0, flags);
            deltaData.Add(state.timestamp);
            networkView.RPC("ReceiveDeltaTransform", RpcTarget.Others, deltaData.ToArray());
        }
    }

    private void SendCompressed(TransformState state)
    {
        var data = new System.Collections.Generic.List<object>();

        if (syncPosition)
        {
            data.Add(CompressVector3(state.position, -1000f, 1000f));
        }

        if (syncRotation)
        {
            data.Add(CompressQuaternion(state.rotation));
        }

        if (syncScale)
        {
            data.Add(CompressVector3(state.scale, 0f, 10f));
        }

        // Add velocity for better prediction
        if (state.velocity.magnitude > velocityThreshold)
        {
            data.Add(CompressVector3(state.velocity, -20f, 20f));
        }

        data.Add(state.timestamp);
        networkView.RPC("ReceiveCompressedTransform", RpcTarget.Others, data.ToArray());
    }

    private void SendUncompressed(TransformState state)
    {
        object[] data = BuildTransformData(state);
        networkView.RPC("ReceiveTransform", RpcTarget.Others, data);
    }

    // Compress Vector3 to 3 shorts (6 bytes instead of 12)
    private Vector3 CompressVector3(Vector3 vector, float min, float max)
    {
        float range = max - min;
        ushort x = (ushort)((vector.x - min) / range * 65535f);
        ushort y = (ushort)((vector.y - min) / range * 65535f);
        ushort z = (ushort)((vector.z - min) / range * 65535f);
        return new Vector3(x, y, z); // Store as Vector3 for RPC compatibility
    }

    private Vector3 DecompressVector3(Vector3 compressed, float min, float max)
    {
        float range = max - min;
        float x = ((ushort)compressed.x / 65535f) * range + min;
        float y = ((ushort)compressed.y / 65535f) * range + min;
        float z = ((ushort)compressed.z / 65535f) * range + min;
        return new Vector3(x, y, z);
    }

    // Compress Quaternion to 4 bytes using smallest-three compression
    private Vector4 CompressQuaternion(Quaternion quat)
    {
        // Find the largest component
        int largestIndex = 0;
        float largestValue = Mathf.Abs(quat.x);

        if (Mathf.Abs(quat.y) > largestValue)
        {
            largestIndex = 1;
            largestValue = Mathf.Abs(quat.y);
        }
        if (Mathf.Abs(quat.z) > largestValue)
        {
            largestIndex = 2;
            largestValue = Mathf.Abs(quat.z);
        }
        if (Mathf.Abs(quat.w) > largestValue)
        {
            largestIndex = 3;
            largestValue = Mathf.Abs(quat.w);
        }

        // Ensure the largest component is positive
        if (quat[largestIndex] < 0)
        {
            quat.x = -quat.x;
            quat.y = -quat.y;
            quat.z = -quat.z;
            quat.w = -quat.w;
        }

        // Pack the three smallest components
        Vector4 compressed = Vector4.zero;
        int compIndex = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i != largestIndex)
            {
                compressed[compIndex] = quat[i];
                compIndex++;
            }
        }
        compressed.w = largestIndex; // Store which component was largest

        return compressed;
    }

    private Quaternion DecompressQuaternion(Vector4 compressed)
    {
        int largestIndex = (int)compressed.w;

        // Reconstruct the quaternion
        Quaternion result = new Quaternion();
        int compIndex = 0;

        for (int i = 0; i < 4; i++)
        {
            if (i != largestIndex)
            {
                result[i] = compressed[compIndex];
                compIndex++;
            }
        }

        // Calculate the largest component
        float sum = result.x * result.x + result.y * result.y + result.z * result.z + result.w * result.w;
        result[largestIndex] = Mathf.Sqrt(1.0f - sum);

        return result;
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

        // Add velocity for client-side prediction
        if (state.velocity.magnitude > velocityThreshold)
        {
            data.Add(state.velocity);
        }

        data.Add(state.timestamp);
        return data.ToArray();
    }

    // Delta compressed receive method
    private void ReceiveDeltaTransform(object[] data)
    {
        if (data == null || data.Length < 2) return;

        byte flags = Convert.ToByte(data[0]);
        int index = 1;
        var receivedState = networkState; // Start with current state

        if ((flags & CompressedTransformData.POSITION_FLAG) != 0 && data.Length > index)
        {
            Vector3 deltaPos = DecompressVector3((Vector3)data[index], -50f, 50f);
            receivedState.position = lastSentPosition + deltaPos;
            index++;
        }

        if ((flags & CompressedTransformData.ROTATION_FLAG) != 0 && data.Length > index)
        {
            Quaternion deltaRot = DecompressQuaternion((Vector4)data[index]);
            receivedState.rotation = lastSentRotation * deltaRot;
            index++;
        }

        if ((flags & CompressedTransformData.SCALE_FLAG) != 0 && data.Length > index)
        {
            Vector3 deltaScale = DecompressVector3((Vector3)data[index], -5f, 5f);
            receivedState.scale = lastSentScale + deltaScale;
            index++;
        }

        if ((flags & CompressedTransformData.VELOCITY_FLAG) != 0 && data.Length > index)
        {
            receivedState.velocity = DecompressVector3((Vector3)data[index], -20f, 20f);
            index++;
        }

        if (data.Length > index && data[index] is float timestamp)
        {
            receivedState.timestamp = timestamp;
        }

        previousNetworkState = networkState;
        networkState = receivedState;

        // Update our baseline for future deltas
        if (syncPosition) lastSentPosition = receivedState.position;
        if (syncRotation) lastSentRotation = receivedState.rotation;
        if (syncScale) lastSentScale = receivedState.scale;
    }

    // Compressed receive method
    private void ReceiveCompressedTransform(object[] data)
    {
        if (data == null || data.Length < 1) return;

        var receivedState = new TransformState();
        int index = 0;

        if (syncPosition && data.Length > index && data[index] is Vector3 compressedPos)
        {
            receivedState.position = DecompressVector3(compressedPos, -1000f, 1000f);
            index++;
        }

        if (syncRotation && data.Length > index && data[index] is Vector4 compressedRot)
        {
            receivedState.rotation = DecompressQuaternion(compressedRot);
            index++;
        }

        if (syncScale && data.Length > index && data[index] is Vector3 compressedScale)
        {
            receivedState.scale = DecompressVector3(compressedScale, 0f, 10f);
            index++;
        }

        // Check if velocity data is included
        if (data.Length > index + 1) // timestamp + velocity
        {
            if (data[index] is Vector3 compressedVel)
            {
                receivedState.velocity = DecompressVector3(compressedVel, -20f, 20f);
                index++;
            }
        }

        if (data.Length > index && data[index] is float timestamp)
        {
            receivedState.timestamp = timestamp;
        }

        previousNetworkState = networkState;
        networkState = receivedState;
    }

    // Legacy uncompressed receive method
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

        // Check for velocity data
        if (data.Length > index + 1 && data[index] is Vector3 velocity)
        {
            receivedState.velocity = velocity;
            index++;
        }

        if (data.Length > index && data[index] is float timestamp)
        {
            receivedState.timestamp = timestamp;
        }

        previousNetworkState = networkState;
        networkState = receivedState;
    }

    private void InterpolateTransform()
    {
        if (syncPosition)
        {
            Vector3 targetPosition = networkState.position;

            // Use prediction if we have velocity data
            if (networkState.velocity.magnitude > 0.1f)
            {
                float timeSinceUpdate = Time.time - networkState.timestamp;
                targetPosition += networkState.velocity * timeSinceUpdate;
            }

            float distance = Vector3.Distance(transform.position, targetPosition);

            if (distance > snapDistance)
            {
                transform.position = targetPosition;
            }
            else
            {
                // Smoother interpolation with velocity consideration
                float interpSpeed = lerpRate;
                if (networkState.velocity.magnitude > 1f)
                {
                    interpSpeed *= 1.5f; // Faster interpolation for moving objects
                }

                transform.position = Vector3.Lerp(transform.position, targetPosition, interpSpeed * Time.deltaTime);
            }
        }

        if (syncRotation)
        {
            // Smooth rotation interpolation
            transform.rotation = Quaternion.Lerp(transform.rotation, networkState.rotation, lerpRate * Time.deltaTime);
        }

        if (syncScale)
        {
            transform.localScale = Vector3.Lerp(transform.localScale, networkState.scale, lerpRate * Time.deltaTime);
        }
    }

    public void OnRPC(string methodName, object[] parameters)
    {
        if (!methodName.StartsWith(NetworkId + ":")) return;

        string actualMethod = methodName.Substring(NetworkId.Length + 1);

        switch (actualMethod)
        {
            case "ReceiveTransform":
                ReceiveTransform(parameters);
                break;
            case "ReceiveCompressedTransform":
                ReceiveCompressedTransform(parameters);
                break;
            case "ReceiveDeltaTransform":
                ReceiveDeltaTransform(parameters);
                break;
        }
    }

    // Force an immediate sync (useful for teleportation, etc.)
    public void ForceSend()
    {
        if (networkView.isMine)
        {
            nextSendTime = 0f;
            stationaryTime = 0f; // Reset stationary timer
            lastSentState = default; // Force a significant change
        }
    }

    // Get current network statistics for debugging
    public NetworkTransformStats GetStats()
    {
        return new NetworkTransformStats
        {
            CurrentSendRate = currentSendRate,
            StationaryTime = stationaryTime,
            LastSentPosition = lastSentPosition,
            LastSentRotation = lastSentRotation,
            NetworkPosition = networkState.position,
            NetworkRotation = networkState.rotation,
            Velocity = networkState.velocity,
            UseCompression = useCompression,
            UseDeltaCompression = useDeltaCompression
        };
    }
}

[System.Serializable]
public struct NetworkTransformStats
{
    public float CurrentSendRate;
    public float StationaryTime;
    public Vector3 LastSentPosition;
    public Quaternion LastSentRotation;
    public Vector3 NetworkPosition;
    public Quaternion NetworkRotation;
    public Vector3 Velocity;
    public bool UseCompression;
    public bool UseDeltaCompression;
}