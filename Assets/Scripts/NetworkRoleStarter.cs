using UnityEngine;
using Unity.Netcode.Transports.UTP;
using Unity.Netcode;

public class NetworkStarter : MonoBehaviour
{
    void Start()
    {
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        
        // "0.0.0.0" means listen on ALL network interfaces, not just localhost
        transport.SetConnectionData("0.0.0.0", 7777, "0.0.0.0");
        
        Debug.Log("[NET] Starting host, listening on 0.0.0.0:7777");
        NetworkManager.Singleton.StartHost();
        Debug.Log($"[NET] host started successfully");
    }
}

// using Unity.Netcode;
// using Unity.Netcode.Transports.UTP;
// using UnityEngine;

// public class NetworkStarter : MonoBehaviour
// {
//     void Start()
//     {
//         var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
//         transport.SetConnectionData("192.168.0.104", 7777);
//         Debug.Log($"[NET] Connecting to {transport.ConnectionData.Address}:{transport.ConnectionData.Port}");
//         NetworkManager.Singleton.StartClient();
//         Debug.Log($"[NET] client connected successfully");
//     }
// }