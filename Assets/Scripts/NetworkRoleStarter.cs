// using UnityEngine;
// using Unity.Netcode.Transports.UTP;
// using Unity.Netcode;

// public class NetworkStarter : MonoBehaviour
// {
//     void Start()
//     {
//         var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        
//         // "0.0.0.0" means listen on ALL network interfaces, not just localhost
//         transport.SetConnectionData("0.0.0.0", 7777, "0.0.0.0");
        
//         Debug.Log("[NET] Starting host, listening on 0.0.0.0:7777");
//         NetworkManager.Singleton.StartHost();
//         Debug.Log($"[NET] host started successfully");
//     }
// }

using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

public class NetworkStarter : MonoBehaviour
{
    void Start()
    {
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        gameObject.AddComponent<ClockSync>();
        if (Application.platform != RuntimePlatform.Android)
        {
            // Lab PC: dedicated server (no player, no hands) and time master
            transport.SetConnectionData("0.0.0.0", 7777, "0.0.0.0");
            NetworkManager.Singleton.StartServer();
            Debug.Log("[NET] Dedicated server listening on 0.0.0.0:7777");
            if (FindAnyObjectByType<VirtualCameraRig>() == null) gameObject.AddComponent<VirtualCameraRig>();
            return;
        }
        transport.SetConnectionData("192.168.0.100", 7777);
        Debug.Log($"[NET] Connecting to {transport.ConnectionData.Address}:{transport.ConnectionData.Port}");
        NetworkManager.Singleton.OnClientConnectedCallback += id => Debug.Log($"[NET] connected as client {id}");
        NetworkManager.Singleton.OnClientDisconnectCallback += id => Debug.Log($"[NET] disconnected: {NetworkManager.Singleton.DisconnectReason}");
        NetworkManager.Singleton.StartClient();   // connect first, so nothing below can block it
    }
}