using System;
using UnityEngine;

namespace FireNet
{
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
            networkView = GetComponent<NetworkView>();

            if (rb == null || networkView == null)
            {
                Debug.LogError("NetworkRigidbody requires both Rigidbody and NetworkView components!");
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
            object[] data = new object[]
            {
                    rb.velocity.x, rb.velocity.y, rb.velocity.z,
                    rb.angularVelocity.x, rb.angularVelocity.y, rb.angularVelocity.z,
                    rb.isKinematic
            };

            networkView.RPC("UpdateRigidbodyState", RpcTarget.Others, data);
        }

        private void UpdateRigidbodyState(object[] data)
        {
            if (data.Length >= 7)
            {
                if (syncVelocity)
                {
                    rb.velocity = new Vector3(
                        Convert.ToSingle(data[0]),
                        Convert.ToSingle(data[1]),
                        Convert.ToSingle(data[2])
                    );
                }

                if (syncAngularVelocity)
                {
                    rb.angularVelocity = new Vector3(
                        Convert.ToSingle(data[3]),
                        Convert.ToSingle(data[4]),
                        Convert.ToSingle(data[5])
                    );
                }

                if (syncIsKinematic)
                {
                    rb.isKinematic = Convert.ToBoolean(data[6]);
                }
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
}