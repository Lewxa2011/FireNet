using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class NetworkPlayer : MonoBehaviour
{
    public Transform netPlayerHead;
    public Transform netPlayerLeftHand;
    public Transform netPlayerRightHand;

    private void Update()
    {
        netPlayerHead.position = NetworkManager.Instance.rigHead.position;
        netPlayerHead.rotation = NetworkManager.Instance.rigHead.rotation;

        netPlayerLeftHand.position = NetworkManager.Instance.rigLeftHand.position;
        netPlayerLeftHand.rotation = NetworkManager.Instance.rigLeftHand.rotation;

        netPlayerRightHand.position = NetworkManager.Instance.rigRightHand.position;
        netPlayerRightHand.rotation = NetworkManager.Instance.rigRightHand.rotation;
    }
}