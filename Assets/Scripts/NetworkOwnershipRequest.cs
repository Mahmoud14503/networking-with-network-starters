using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Leap.PhysicalHands;

// Per-object ownership arbitration on the PC server. Grasp and release are Ultraleap's own
// Physical Hands grab (IPhysicalHandGrab events); Ultraleap moves and throws the object on its owner.
//   Free    --local Ultraleap grab (claim)-------------> Held     (immediate grant)
//   Held    --other user grabs too --------------------> Held     (other user armed, waits)
//   Held    --holder releases, other still grabbing ----> Pending  (grant sent to armed user)
//   Held    --holder releases, nobody else grabbing ---> Free     (Ultraleap's throw / gravity)
//   Pending --armed user still grabbing (accept) ------> Held     (ChangeOwnership)
//   Pending --armed user let go / timeout -------------> Free
// Every request carries the state's seq; stale ones are rejected.
public class NetworkOwnershipRequest : NetworkBehaviour, IPhysicalHandGrab
{
    public enum Phase : byte { Free, Held, Pending }
    public struct HandoverState : INetworkSerializable, System.IEquatable<HandoverState>
    {
        public Phase Phase; public ulong Holder, Armed; public uint Seq;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Phase); s.SerializeValue(ref Holder); s.SerializeValue(ref Armed); s.SerializeValue(ref Seq); }
        public bool Equals(HandoverState o) => Phase == o.Phase && Holder == o.Holder && Armed == o.Armed && Seq == o.Seq;
    }
    const ulong None = ulong.MaxValue;

    public float armTimeout = 0.1f, pendingTimeout = 0.25f;

    readonly NetworkVariable<HandoverState> state = new NetworkVariable<HandoverState>(
        new HandoverState { Holder = None, Armed = None, Seq = 1 });
    public HandoverState State => state.Value;

    Rigidbody rb;
    // client
    readonly HashSet<ContactHand> grabbing = new HashSet<ContactHand>();   // local hands grabbing this object
    bool claimPending, wasArmed;
    double claimTime, releaseTime = double.NegativeInfinity;
    uint holdSeq;
    Vector3 heldVel, heldAngVel;
    // server
    readonly Dictionary<ulong, (double t, bool armed)> arms = new Dictionary<ulong, (double t, bool armed)>();
    double pendingSince;

    bool Grabbing => grabbing.Count > 0;

    void Awake() => rb = GetComponent<Rigidbody>();

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            NetworkObject.DontDestroyWithOwner = true;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnect;
        }
    }

    // ---------------------------------------------------------------- Ultraleap grab events (local hands)

    public void OnHandGrab(ContactHand hand)
    {
        if (!IsSpawned || !IsClient) return;
        grabbing.Add(hand);
        if (State.Phase == Phase.Free && !claimPending) SendClaim();
    }

    public void OnHandGrabExit(ContactHand hand)
    {
        if (!IsSpawned || !IsClient || !grabbing.Remove(hand) || Grabbing) return;
        var s = State;
        if (s.Holder == NetworkManager.LocalClientId && s.Phase == Phase.Held)
        {
            releaseTime = ClockSync.LocalTime;
            ReleaseRpc(holdSeq > s.Seq ? holdSeq : s.Seq, ClockSync.Now);
        }
    }

    void SendClaim()
    {
        claimPending = true; claimTime = ClockSync.LocalTime;
        ClaimRpc(State.Seq);
    }

    // ---------------------------------------------------------------- client

    void Update()
    {
        if (!IsSpawned) return;
        if (IsServer && State.Phase == Phase.Pending && ClockSync.Now - pendingSince > pendingTimeout) ServerFree("pending_timeout");
        if (!IsClient) return;
        var s = State;
        ulong me = NetworkManager.LocalClientId;
        if (claimPending && ClockSync.LocalTime - claimTime > 0.3) claimPending = false;
        if (s.Phase == Phase.Free && Grabbing && !claimPending) SendClaim();   // e.g. still grabbing after the other user let go

        // held by someone else while I grab it too: armed, reported every frame
        bool armed = s.Phase != Phase.Free && s.Holder != me && Grabbing;
        if (armed || wasArmed) ArmRpc(armed, ClockSync.Now);
        wasArmed = armed;
    }

    // Exactly one simulator: the owner's copy is dynamic (Ultraleap moves/throws it); all others are
    // kinematic and follow the network. Re-applied every step because Ultraleap's grab toggles isKinematic.
    void FixedUpdate()
    {
        if (!IsSpawned) return;
        bool kinematic = !IsOwner;
        if (rb.isKinematic != kinematic)
        {
            rb.collisionDetectionMode = kinematic ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.ContinuousDynamic;
            rb.isKinematic = kinematic;
            if (!kinematic && ClockSync.LocalTime - releaseTime < 0.1)
            {   // Ultraleap won't throw an object it thinks was kinematic: keep the hand's motion
                rb.linearVelocity = heldVel;
                rb.angularVelocity = heldAngVel;
            }
        }
        if (IsOwner && Grabbing) { heldVel = rb.linearVelocity; heldAngVel = rb.angularVelocity; }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void GrantRpc(uint seq, bool handover, RpcParams p)
    {
        claimPending = false;
        if (handover)
        {
            if (Grabbing) { holdSeq = seq + 1; AcceptRpc(seq); }   // server increments seq on Accept
            else DeclineRpc(seq);
            return;
        }
        holdSeq = seq;
        if (!Grabbing) ReleaseRpc(seq, ClockSync.Now);           // let go before the grant arrived
    }

    // ---------------------------------------------------------------- server

    [Rpc(SendTo.Server)]
    void ClaimRpc(uint seq, RpcParams p = default)
    {
        ulong c = p.Receive.SenderClientId;
        var s = State;
        if (s.Phase != Phase.Free || seq != s.Seq) return;
        if (OwnerClientId != c) NetworkObject.ChangeOwnership(c);
        state.Value = s = new HandoverState { Phase = Phase.Held, Holder = c, Armed = None, Seq = s.Seq + 1 };
        GrantRpc(s.Seq, false, RpcTarget.Single(c, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
    void ArmRpc(bool armed, double t, RpcParams p = default) => arms[p.Receive.SenderClientId] = (t, armed);

    [Rpc(SendTo.Server)]
    void ReleaseRpc(uint seq, double t, RpcParams p = default)
    {
        ulong c = p.Receive.SenderClientId;
        var s = State;
        if (s.Phase != Phase.Held || s.Holder != c || seq != s.Seq) return;
        // the receiver must be grabbing at the giver's release instant
        foreach (var a in arms)
            if (a.Key != c && a.Value.armed && t - a.Value.t <= armTimeout && NetworkManager.ConnectedClients.ContainsKey(a.Key))
            {
                state.Value = s = new HandoverState { Phase = Phase.Pending, Holder = c, Armed = a.Key, Seq = s.Seq + 1 };
                pendingSince = ClockSync.Now;
                GrantRpc(s.Seq, true, RpcTarget.Single(a.Key, RpcTargetUse.Temp));
                return;
            }
        ServerFree("no_receiver");
    }

    [Rpc(SendTo.Server)]
    void AcceptRpc(uint seq, RpcParams p = default)
    {
        ulong c = p.Receive.SenderClientId;
        var s = State;
        if (s.Phase != Phase.Pending || s.Armed != c || seq != s.Seq) return;
        NetworkObject.ChangeOwnership(c);
        state.Value = s = new HandoverState { Phase = Phase.Held, Holder = c, Armed = None, Seq = s.Seq + 1 };
    }

    [Rpc(SendTo.Server)]
    void DeclineRpc(uint seq, RpcParams p = default)
    {
        if (State.Phase == Phase.Pending && State.Armed == p.Receive.SenderClientId && seq == State.Seq) ServerFree("receiver_let_go");
    }

    // Free: the last holder keeps ownership and simulates the drop/throw
    void ServerFree(string reason)
    {
        var s = State;
        state.Value = new HandoverState { Phase = Phase.Free, Holder = None, Armed = None, Seq = s.Seq + 1 };
    }

    void OnClientDisconnect(ulong c)
    {
        arms.Remove(c);
        var s = State;
        if (s.Holder != c && s.Armed != c && OwnerClientId != c) return;
        state.Value = new HandoverState { Phase = Phase.Free, Holder = None, Armed = None, Seq = s.Seq + 1 };
        if (OwnerClientId != NetworkManager.ServerClientId) NetworkObject.RemoveOwnership();
    }
}
