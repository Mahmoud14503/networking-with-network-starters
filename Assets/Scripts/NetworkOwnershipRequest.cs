using UnityEngine;
using Unity.Netcode;

public class NetworkOwnershipRequest : NetworkBehaviour
{
    private void OnCollisionEnter(Collision collision)
    {
        Debug.Log($"[GRAB] Collision with: {collision.transform.name}, IsOwner: {IsOwner}");
        CheckContact(collision.transform);
    }

    private void CheckContact(Transform targetTransform)
    {
        bool hasRight = HasNameInHierarchy(targetTransform, "right");
        if (hasRight)
        {
            if(Camera.main != null) Camera.main.backgroundColor = new Color(0, 0, 0.2f);
            Debug.Log($"[GRAB] HasRight: {hasRight}, IsOwner: {IsOwner}");
        }
        if (!IsOwner && hasRight)
        {
            Debug.Log($"[GRAB] Requesting ownership from client {NetworkManager.Singleton.LocalClientId}");
            RequestOwnershipServerRpc(NetworkManager.Singleton.LocalClientId);
        }
    }

    private bool HasNameInHierarchy(Transform t, string keyword)
    {
        Transform current = t;
        while (current != null)
        {
            if (current.name.ToLower().Contains(keyword))
                return true;
            current = current.parent;
        }
        return false;
    }

    [Rpc(SendTo.Server)]
    private void RequestOwnershipServerRpc(ulong clientId)
    {
        var netObj = GetComponent<NetworkObject>();
        if (netObj == null)
        {
            Debug.LogError("[GRAB] NetworkObject is NULL on server side!");
            return;
        }
        Debug.Log($"[GRAB] Server received request, changing owner to {clientId}");
        netObj.ChangeOwnership(clientId);
    }

    public override void OnGainedOwnership()
    {
        Debug.Log($"[GRAB] Client {NetworkManager.Singleton.LocalClientId} gained ownership");
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            Debug.Log($"[GRAB] Rigidbody — isKinematic: {rb.isKinematic}, useGravity: {rb.useGravity}");
        }
    }

    public override void OnLostOwnership()
    {
        Debug.Log($"[GRAB] Client {NetworkManager.Singleton.LocalClientId} lost ownership");
        // No kinematic toggle — let ClientNetworkTransform handle it
    }
}