using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Leap.PhysicalHands;

// Ownership follows Ultraleap's grab: the moment a local hand grabs the cube (IPhysicalHandGrab),
// this headset asks the server for ownership and gets it, even if the other user is holding it
// (latest grab wins). Letting go changes nothing on the network: the owner keeps the cube and
// Ultraleap's throw / gravity runs on its copy.
// Cooldown: a headset that just lost the cube (the giver) can't take it back for
// regrabCooldown seconds, so the two hands can't keep pulling it back and forth.
public class NetworkOwnershipRequest : NetworkBehaviour, IPhysicalHandGrab
{
    Rigidbody rb;
    readonly HashSet<ContactHand> grabbing = new HashSet<ContactHand>();   // local hands grabbing this cube
    Vector3 lastVel, lastAngVel;

    [Tooltip("Seconds after losing ownership before the same headset may take the cube back.")]
    public float regrabCooldown = 2f;
    readonly Dictionary<ulong, float> lostOwnershipAt = new Dictionary<ulong, float>();   // server only

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; // valid for kinematic and dynamic
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer) NetworkObject.DontDestroyWithOwner = true;   // owner disconnects -> cube returns to the server
    }

    public override void OnGainedOwnership() { lastVel = lastAngVel = Vector3.zero; }

    // ---------------------------------------------------------------- Ultraleap grab events (local hands)

    public void OnHandGrab(ContactHand hand)
    {
        grabbing.Add(hand);
        if (IsSpawned && IsClient && !IsOwner) RequestOwnershipRpc();
    }

    public void OnHandGrabExit(ContactHand hand) => grabbing.Remove(hand);

    [Rpc(SendTo.Server)]
    void RequestOwnershipRpc(RpcParams p = default)
    {
        ulong c = p.Receive.SenderClientId;
        if (OwnerClientId == c) return;
        if (lostOwnershipAt.TryGetValue(c, out float t) && Time.time - t < regrabCooldown) return;   // giver still cooling down
        lostOwnershipAt[OwnerClientId] = Time.time;   // the current owner becomes the giver
        NetworkObject.ChangeOwnership(c);
    }

    // One simulator: the owner's copy is dynamic, every other copy kinematic and driven by the network.
    // Re-applied every step because Ultraleap's grab switches isKinematic itself; when it has to be
    // switched back on for the owner, the last motion is kept so Ultraleap's throw isn't lost.
    void FixedUpdate()
    {
        if (!IsSpawned) return;
        bool kinematic = !IsOwner;
        if (rb.isKinematic != kinematic)
        {
            rb.isKinematic = kinematic;
            if (!kinematic) { rb.linearVelocity = lastVel; rb.angularVelocity = lastAngVel; }
        }
        if (!rb.isKinematic) { lastVel = rb.linearVelocity; lastAngVel = rb.angularVelocity; }
    }
}
