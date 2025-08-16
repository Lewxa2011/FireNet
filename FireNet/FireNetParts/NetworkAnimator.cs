using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FireNet
{
    public class NetworkAnimator : MonoBehaviour, INetworkBehaviour
    {
        private Animator animator;
        private NetworkView networkView;
        private Dictionary<string, object> lastSentParams = new Dictionary<string, object>();

        [Header("Sync Settings")]
        public float sendRate = 10f;
        public bool syncParameters = true;
        public bool syncTriggers = true;
        public bool syncLayers = false;

        public string NetworkId => networkView?.viewId ?? "";
        private float lastSendTime;

        private void Start()
        {
            animator = GetComponent<Animator>();
            networkView = GetComponent<NetworkView>();

            if (animator == null || networkView == null)
            {
                Debug.LogError("NetworkAnimator requires both Animator and NetworkView components!");
                enabled = false;
            }
        }

        private void Update()
        {
            if (networkView.isMine && Time.time - lastSendTime > 1f / sendRate)
            {
                SyncAnimatorState();
                lastSendTime = Time.time;
            }
        }

        private void SyncAnimatorState()
        {
            if (!syncParameters) return;

            var currentParams = new Dictionary<string, object>();

            for (int i = 0; i < animator.parameterCount; i++)
            {
                var param = animator.parameters[i];
                object value = null;

                switch (param.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        value = animator.GetBool(param.nameHash);
                        break;
                    case AnimatorControllerParameterType.Float:
                        value = animator.GetFloat(param.nameHash);
                        break;
                    case AnimatorControllerParameterType.Int:
                        value = animator.GetInteger(param.nameHash);
                        break;
                }

                if (value != null)
                {
                    currentParams[param.name] = value;
                }
            }


            if (!DictionariesEqual(currentParams, lastSentParams))
            {
                networkView.RPC("UpdateAnimatorParams", RpcTarget.Others, currentParams);
                lastSentParams = new Dictionary<string, object>(currentParams);
            }
        }

        private bool DictionariesEqual(Dictionary<string, object> dict1, Dictionary<string, object> dict2)
        {
            if (dict1.Count != dict2.Count) return false;

            foreach (var kvp in dict1)
            {
                if (!dict2.ContainsKey(kvp.Key) || !dict2[kvp.Key].Equals(kvp.Value))
                    return false;
            }
            return true;
        }

        private void UpdateAnimatorParams(Dictionary<string, object> parameters)
        {
            if (!syncParameters) return;

            foreach (var param in parameters)
            {
                if (animator.parameters.Any(p => p.name == param.Key))
                {
                    var animParam = animator.parameters.First(p => p.name == param.Key);

                    switch (animParam.type)
                    {
                        case AnimatorControllerParameterType.Bool:
                            animator.SetBool(param.Key, Convert.ToBoolean(param.Value));
                            break;
                        case AnimatorControllerParameterType.Float:
                            animator.SetFloat(param.Key, Convert.ToSingle(param.Value));
                            break;
                        case AnimatorControllerParameterType.Int:
                            animator.SetInteger(param.Key, Convert.ToInt32(param.Value));
                            break;
                    }
                }
            }
        }

        public void SetTrigger(string triggerName)
        {
            if (networkView.isMine && syncTriggers)
            {
                animator.SetTrigger(triggerName);
                networkView.RPC("OnTrigger", RpcTarget.Others, triggerName);
            }
        }

        private void OnTrigger(string triggerName)
        {
            if (syncTriggers)
            {
                animator.SetTrigger(triggerName);
            }
        }

        public void OnRPC(string methodName, object[] parameters)
        {
            if (methodName.StartsWith(NetworkId + ":"))
            {
                string actualMethod = methodName.Substring(NetworkId.Length + 1);
                if (actualMethod == "UpdateAnimatorParams" && parameters.Length > 0)
                {
                    UpdateAnimatorParams(parameters[0] as Dictionary<string, object>);
                }
                else if (actualMethod == "OnTrigger" && parameters.Length > 0)
                {
                    OnTrigger(parameters[0].ToString());
                }
            }
        }
    }
}