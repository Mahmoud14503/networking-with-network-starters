using Unity.Netcode;
using UnityEngine;

public class NetworkDebug : MonoBehaviour
{
    void Start()
    {
        NetworkManager.Singleton.OnClientConnectedCallback += id => 
            Debug.Log($"[NGO] Client connected: {id}");
        NetworkManager.Singleton.OnClientDisconnectCallback += id => 
            Debug.Log($"[NGO] Client disconnected: {id}");
        NetworkManager.Singleton.OnServerStarted += () => 
            Debug.Log("[NGO] Server started");
    }
}