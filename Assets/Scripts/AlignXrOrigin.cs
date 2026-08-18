using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

public class NetworkedOriginSpawner : MonoBehaviour
{

    void Start()
    {
        // 1. Force tracking origin to Device/Eye-level for consistency
        List<XRInputSubsystem> subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);

        foreach (var subsystem in subsystems)
        {
            subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
            subsystem.TryRecenter();
        }

        // 2. Set Position and Rotation based on Player ID
        if (NetworkManager.Singleton.IsHost)
        {
            // Player 1: Spawns 1 meter back on the Z axis, facing forward (0 degrees)
            transform.position = new Vector3(0, 0, -1f);
            transform.rotation = Quaternion.Euler(0, 0, 0);
        }
        else
        {
            // Player 2: Spawns 1 meter forward on the Z axis, facing backward (180 degrees)
            transform.position = new Vector3(0, 0, 1f);
            transform.rotation = Quaternion.Euler(0, 180f, 0);
        }
    }
}