using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Leap.PhysicalHands;

// Per-object ownership arbitration on the PC server + grab-in-place link on the holder.
//   Free    --claim (user onset)----------------------> Held     (immediate grant)
//   Held    --other user onset ------------------------> Held     (other user armed, waits)
//   Held    --holder release, other armed -------------> Pending  (grant sent to armed user)
//   Held    --holder release, none armed --------------> Free     (giver drops it with palm velocity)
//   Pending --armed user accepts ----------------------> Held     (ChangeOwnership)
//   Pending --armed user lost grip / timeout ----------> Free
// No contact-triggered ownership. Every request carries the state's seq; stale ones are rejected.
public class NetworkOwnershipRequest : NetworkBehaviour
{
    public enum Phase : byte { Free, Held, Pending }
    public struct HandoverState : INetworkSerializable, System.IEquatable<HandoverState>
    {
        public Phase Phase; public ulong Holder, Armed; public uint Seq;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref Phase); s.SerializeValue(ref Holder); s.SerializeValue(ref Armed); s.SerializeValue(ref Seq); }
        public bool Equals(HandoverState o) => Phase == o.Phase && Holder == o.Holder && Armed == o.Armed && Seq == o.Seq;
        public override string ToString() => $"phase={Phase};holder={Holder};armed={Armed};seq={Seq}";
    }
    const ulong None = ulong.MaxValue;

    public static readonly List<NetworkOwnershipRequest> All = new List<NetworkOwnershipRequest>();
    [Tooltip("Calibrated extra onset frames so onset latency matches across objects (within one frame).")]
    public int onsetExtraFrames = 0;
    public float residualBlendTime = 0.1f, armTimeout = 0.1f, pendingTimeout = 0.25f;

    readonly NetworkVariable<HandoverState> state = new NetworkVariable<HandoverState>(
        new HandoverState { Holder = None, Armed = None, Seq = 1 });
    public HandoverState State => state.Value;

    Rigidbody rb;
    // client
    GraspDetector holder, claim;
    double claimTime, frozenSince, blendStart;
    bool frozen, waitOwnership, wasArmed;
    uint holdSeq;
    Pose offset, releasePose;
    Vector3 resPos, releaseVel, releaseAngVel;
    Quaternion resRot = Quaternion.identity;
    // server
    readonly Dictionary<ulong, (double t, bool armed)> arms = new Dictionary<ulong, (double t, bool armed)>();
    double pendingSince;
    Vector3 srvVel, srvAngVel;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        // Ultraleap built-in grab off, hand collision kept
        var iph = GetComponent<IgnorePhysicalHands>();
        if (iph == null) iph = gameObject.AddComponent<IgnorePhysicalHands>();
        iph.Init(ignoreGrabbing: true, ignoreCollisions: false, ignoreChildCollisions: false);
    }

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        state.OnValueChanged += (a, b) => { NetworkDebug.Event("state", NetworkObjectId, b.ToString()); ApplyPhysics(); };
        if (IsServer)
        {
            NetworkObject.DontDestroyWithOwner = true;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnect;
        }
        ApplyPhysics();
    }

    public override void OnNetworkDespawn() => All.Remove(this);

    public override void OnGainedOwnership()
    {
        NetworkDebug.Event("ownership_gained", NetworkObjectId, State.ToString());
        if (waitOwnership && holder != null) { waitOwnership = false; StartDriving(); }
        ApplyPhysics();
    }

    public override void OnLostOwnership()
    {
        NetworkDebug.Event("ownership_lost", NetworkObjectId, State.ToString());
        frozen = false;
        ApplyPhysics();
    }

    // Only the owner simulates a free object; every other copy is kinematic and interpolated.
    void ApplyPhysics()
    {
        bool simulate = IsSpawned && IsOwner && State.Phase == Phase.Free && holder == null && !frozen;
        SetKinematic(!simulate);
    }

    void SetKinematic(bool kinematic)
    {
        if (rb.isKinematic == kinematic) return;
        rb.collisionDetectionMode = kinematic ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.ContinuousDynamic;
        rb.isKinematic = kinematic;
    }

    // ---------------------------------------------------------------- client

    void Update()
    {
        if (!IsSpawned) return;
        if (IsServer && State.Phase == Phase.Pending && ClockSync.Now - pendingSince > pendingTimeout) ServerFree("pending_timeout");
        if (!IsClient) return;
        var s = State;
        ulong me = NetworkManager.LocalClientId;
        if (claim != null && ClockSync.LocalTime - claimTime > 0.3) claim = null;

        if (s.Phase == Phase.Free && holder == null && claim == null)
            foreach (var d in GraspDetector.Local)
                if (d.OnsetActive(this))
                {
                    claim = d; claimTime = ClockSync.LocalTime;
                    NetworkDebug.Event("claim", NetworkObjectId, $"hand={d.HandIndex};seq={s.Seq}");
                    ClaimRpc(s.Seq);
                    break;
                }

        bool armed = false;
        if (s.Phase != Phase.Free && s.Holder != me && holder == null)
            foreach (var d in GraspDetector.Local) armed |= d.OnsetActive(this);
        if (armed || wasArmed) ArmRpc(armed, ClockSync.Now);   // every frame while armed
        if (armed != wasArmed) NetworkDebug.Event(armed ? "armed" : "disarmed", NetworkObjectId, $"seq={s.Seq}");
        wasArmed = armed;

        // never stay frozen if a release got lost
        if (frozen && ClockSync.LocalTime - frozenSince > 1.0 && s.Phase == Phase.Held && s.Holder == me)
        {
            frozenSince = ClockSync.LocalTime;
            ReleaseRpc(s.Seq, releasePose.position, releasePose.rotation, releaseVel, releaseAngVel, ClockSync.Now);
        }
    }

    // Grab-in-place link: kinematic object follows raw palm x stored offset; residual blended out.
    Pose Target(Pose palm)
    {
        var t = new Pose(palm.position + palm.rotation * offset.position, palm.rotation * offset.rotation);
        float u = (float)((ClockSync.LocalTime - blendStart) / residualBlendTime);
        if (u >= 1f) return t;
        float k = 1f - Mathf.SmoothStep(0f, 1f, u);
        return new Pose(t.position + resPos * k, Quaternion.Slerp(Quaternion.identity, resRot, k) * t.rotation);
    }

    void Link(GraspDetector d, Pose objectPose)
    {
        holder = d;
        var inv = Quaternion.Inverse(d.Palm.rotation);
        offset = new Pose(inv * (objectPose.position - d.Palm.position), inv * objectPose.rotation);
        d.BeginHold(this);
        frozen = false;
        blendStart = double.NegativeInfinity;
        if (IsOwner) StartDriving(); else waitOwnership = true;
    }

    void StartDriving()
    {
        var t = Target(holder.Palm);
        resPos = transform.position - t.position;
        resRot = transform.rotation * Quaternion.Inverse(t.rotation);
        blendStart = ClockSync.LocalTime;
        NetworkDebug.Event("residual", NetworkObjectId, $"pos_m={resPos.magnitude:F5};deg={Quaternion.Angle(Quaternion.identity, resRot):F3}");
        ApplyPhysics();
    }

    bool Driving => holder != null && !waitOwnership && IsOwner && holder.IsTracked;

    void FixedUpdate()
    {
        if (!Driving) return;
        var p = Target(holder.Palm);
        rb.MovePosition(p.position);
        rb.MoveRotation(p.rotation);
    }

    void LateUpdate()
    {
        if (!Driving) return;
        var p = Target(holder.Palm);
        transform.SetPositionAndRotation(p.position, p.rotation);
    }

    void Unlink()
    {
        if (holder != null) holder.EndHold();
        holder = null;
        waitOwnership = false;
    }

    public void OnLocalRelease(GraspDetector d, double t)
    {
        if (holder != d) return;
        holder = null; waitOwnership = false;
        frozen = true; frozenSince = ClockSync.LocalTime;   // stay at the release pose until the server decides
        releasePose = new Pose(transform.position, transform.rotation);
        releaseAngVel = d.PalmAngularVelocity;
        releaseVel = d.PalmVelocity + Vector3.Cross(d.PalmAngularVelocity, transform.position - d.Palm.position);
        ApplyPhysics();
        uint seq = holdSeq > State.Seq ? holdSeq : State.Seq;
        ReleaseRpc(seq, releasePose.position, releasePose.rotation, releaseVel, releaseAngVel, t);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void GrantRpc(uint seq, bool handover, Vector3 pos, Quaternion rot, RpcParams p)
    {
        if (!handover)
        {
            var d = claim; claim = null;
            if (d != null && d.IsTracked && d.Holding == null)
            {
                holdSeq = seq;
                Link(d, new Pose(transform.position, transform.rotation));    // grab in place
                NetworkDebug.Event("link", NetworkObjectId, $"kind=claim;hand={d.HandIndex};seq={seq}");
            }
            else ReleaseRpc(seq, transform.position, transform.rotation, Vector3.zero, Vector3.zero, ClockSync.Now);
            return;
        }
        foreach (var d in GraspDetector.Local)
            if (d.OnsetActive(this))
            {
                holdSeq = seq + 1;                       // server increments seq on Accept
                Link(d, new Pose(pos, rot));             // link built from the release-frame pose
                NetworkDebug.Event("link", NetworkObjectId, $"kind=handover;hand={d.HandIndex};seq={seq};" +
                    $"residual_m={(transform.position - pos).magnitude:F5};residual_deg={Quaternion.Angle(transform.rotation, rot):F3}");
                AcceptRpc(seq);
                return;
            }
        DeclineRpc(seq);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void FreeRpc(Vector3 vel, Vector3 angVel, RpcParams p)
    {
        frozen = false;
        Unlink();
        if (!IsOwner) return;
        SetKinematic(false);                           // the server already decided: Free
        rb.linearVelocity = vel;
        rb.angularVelocity = angVel;
        NetworkDebug.Event("drop", NetworkObjectId, $"vel={vel.x:F3},{vel.y:F3},{vel.z:F3}");
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void RevokeRpc(RpcParams p) => Unlink();

    // ---------------------------------------------------------------- server

    [Rpc(SendTo.Server)]
    void ClaimRpc(uint seq, RpcParams p = default)
    {
        ulong c = p.Receive.SenderClientId;
        var s = State;
        if (s.Phase != Phase.Free || seq != s.Seq) { NetworkDebug.Event("claim_rejected", NetworkObjectId, $"client={c};seq={seq};{s}"); return; }
        if (OwnerClientId != c) NetworkObject.ChangeOwnership(c);
        state.Value = s = new HandoverState { Phase = Phase.Held, Holder = c, Armed = None, Seq = s.Seq + 1 };
        NetworkDebug.Event("grant", NetworkObjectId, $"kind=claim;to={c};{s}");
        GrantRpc(s.Seq, false, transform.position, transform.rotation, RpcTarget.Single(c, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
    void ArmRpc(bool armed, double t, RpcParams p = default) => arms[p.Receive.SenderClientId] = (t, armed);

    [Rpc(SendTo.Server)]
    void ReleaseRpc(uint seq, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel, double t, RpcParams p = default)
    {
        ulong c = p.Receive.SenderClientId;
        var s = State;
        if (s.Phase != Phase.Held || s.Holder != c || seq != s.Seq) { NetworkDebug.Event("release_rejected", NetworkObjectId, $"client={c};seq={seq};{s}"); return; }
        srvVel = vel; srvAngVel = angVel;
        // receiver must meet its own onset rule at the giver's release instant
        foreach (var a in arms)
            if (a.Key != c && a.Value.armed && t - a.Value.t <= armTimeout && NetworkManager.ConnectedClients.ContainsKey(a.Key))
            {
                state.Value = s = new HandoverState { Phase = Phase.Pending, Holder = c, Armed = a.Key, Seq = s.Seq + 1 };
                pendingSince = ClockSync.Now;
                NetworkDebug.Event("pending", NetworkObjectId, $"giver={c};receiver={a.Key};{s}");
                GrantRpc(s.Seq, true, pos, rot, RpcTarget.Single(a.Key, RpcTargetUse.Temp));
                return;
            }
        ServerFree("no_receiver");
    }

    [Rpc(SendTo.Server)]
    void AcceptRpc(uint seq, RpcParams p = default)
    {
        ulong c = p.Receive.SenderClientId;
        var s = State;
        if (s.Phase != Phase.Pending || s.Armed != c || seq != s.Seq) { RevokeRpc(RpcTarget.Single(c, RpcTargetUse.Temp)); return; }
        NetworkObject.ChangeOwnership(c);
        state.Value = s = new HandoverState { Phase = Phase.Held, Holder = c, Armed = None, Seq = s.Seq + 1 };
        NetworkDebug.Event("grant", NetworkObjectId, $"kind=handover;to={c};pending_ms={(ClockSync.Now - pendingSince) * 1000:F2};{s}");
    }

    [Rpc(SendTo.Server)]
    void DeclineRpc(uint seq, RpcParams p = default)
    {
        if (State.Phase == Phase.Pending && State.Armed == p.Receive.SenderClientId && seq == State.Seq) ServerFree("receiver_lost_grip");
    }

    void ServerFree(string reason)
    {
        var s = State;
        ulong giver = s.Holder, receiver = s.Armed;
        state.Value = new HandoverState { Phase = Phase.Free, Holder = None, Armed = None, Seq = s.Seq + 1 };
        NetworkDebug.Event("free", NetworkObjectId, $"reason={reason};giver={giver};receiver={receiver}");
        if (NetworkManager.ConnectedClients.ContainsKey(giver)) FreeRpc(srvVel, srvAngVel, RpcTarget.Single(giver, RpcTargetUse.Temp));
        if (receiver != None && NetworkManager.ConnectedClients.ContainsKey(receiver)) RevokeRpc(RpcTarget.Single(receiver, RpcTargetUse.Temp));
    }

    void OnClientDisconnect(ulong c)
    {
        arms.Remove(c);
        var s = State;
        if (s.Holder != c && s.Armed != c && OwnerClientId != c) return;
        state.Value = new HandoverState { Phase = Phase.Free, Holder = None, Armed = None, Seq = s.Seq + 1 };
        if (OwnerClientId != NetworkManager.ServerClientId) NetworkObject.RemoveOwnership();
        ApplyPhysics();
    }
}
