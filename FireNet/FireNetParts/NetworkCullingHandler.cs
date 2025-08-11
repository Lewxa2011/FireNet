using System;
using UnityEngine;

namespace FireNet
{
    public class NetworkCullingHandler : MonoBehaviour, INetworkBehaviour
    {
        private NetworkView networkView;

        [Header("Culling Settings")]
        public float cullDistance = 50f;
        public LayerMask observerLayers = -1;
        public bool cullByDistance = true;
        public bool cullByVisibility = false;

        public string NetworkId => networkView?.viewId ?? "";
        private bool isVisible = true;
        private Camera observerCamera;

        private void Start()
        {
            networkView = GetComponent<NetworkView>();
            observerCamera = Camera.main;

            if (networkView == null)
            {
                Debug.LogError("NetworkCullingHandler requires NetworkView component!");
                enabled = false;
            }
        }

        private void Update()
        {
            if (!networkView.isMine) return;

            bool shouldBeVisible = ShouldBeVisible();

            if (shouldBeVisible != isVisible)
            {
                isVisible = shouldBeVisible;
                networkView.RPC("SetVisibility", RpcTarget.Others, isVisible);
            }
        }

        private bool ShouldBeVisible()
        {
            if (observerCamera == null) return true;

            if (cullByDistance)
            {
                float distance = Vector3.Distance(transform.position, observerCamera.transform.position);
                if (distance > cullDistance) return false;
            }

            if (cullByVisibility)
            {
                var renderer = GetComponent<Renderer>();
                if (renderer != null && !renderer.isVisible) return false;
            }

            return true;
        }

        private void SetVisibility(bool visible)
        {
            gameObject.SetActive(visible);
        }

        public void OnRPC(string methodName, object[] parameters)
        {
            if (methodName.StartsWith(NetworkId + ":"))
            {
                string actualMethod = methodName.Substring(NetworkId.Length + 1);
                if (actualMethod == "SetVisibility" && parameters.Length > 0)
                {
                    SetVisibility(Convert.ToBoolean(parameters[0]));
                }
            }
        }
    }
}