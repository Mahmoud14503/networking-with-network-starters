using System.Collections.Generic;
using Leap;
using Leap.Encoding;
using UnityEngine;
using Unity.Netcode;

public class NetworkHands : NetworkBehaviour
{
    [SerializeField]
    private HandModelBase leftModel = null, rightModel = null;

    private LeapProvider leapProvider;

    private VectorHand leftVector = new VectorHand(), rightVector = new VectorHand();
    private Hand leftHand = new Hand(), rightHand = new Hand();
    private Hand leftHandLatest = new Hand(), rightHandLatest = new Hand();

    private byte[] leftBytes = new byte[VectorHand.NUM_BYTES],
                   rightBytes = new byte[VectorHand.NUM_BYTES];
    private bool leftTracked, rightTracked;
    private bool leftHandReady, rightHandReady;

    // jitter buffer: timestamped snapshots (ClockSync timeline), rendered interpolationDelay behind
    [SerializeField] private float interpolationDelay = 0.05f;
    private struct Snapshot { public double t; public VectorHand l, r; }
    private readonly List<Snapshot> buffer = new List<Snapshot>();
    private readonly VectorHand leftLerp = new VectorHand(), rightLerp = new VectorHand();

    private void Awake()
    {
        // stream exactly what the standard Physical Hands Manager outputs (the hand the user sees)
        var phys = FindAnyObjectByType<Leap.PhysicalHands.PhysicalHandsManager>();
        leapProvider = phys != null ? phys : Hands.Provider;
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            // This is our local player — send our tracking data, 
            // destroy the remote hand visuals (we use Physical Hands locally)
            leapProvider.OnUpdateFrame += OnUpdateFrame;
            Destroy(leftModel?.gameObject);
            Destroy(rightModel?.gameObject);
        }
        else
        {
            // This is a remote player — drive their hands from network data,
            // not from a local LeapProvider
            if (leftModel != null) leftModel.leapProvider = null;
            if (rightModel != null) rightModel.leapProvider = null;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner && leapProvider != null)
            // We no longer need this event as we have disconnected from the network
            leapProvider.OnUpdateFrame -= OnUpdateFrame;
    }

    // Drive remote hands every frame from latest received data
    private void Update()
    {
        if (IsOwner) return;
        Interpolate(ClockSync.Now - interpolationDelay);

        if (leftModel != null && leftHandReady)
        {
            leftModel.SetLeapHand(leftHandLatest);
            leftModel.UpdateHand();
        }

        if (rightModel != null && rightHandReady)
        {
            rightModel.SetLeapHand(rightHandLatest);
            rightModel.UpdateHand();
        }
    }

    private float _lastSendTime;
    private const float SEND_INTERVAL = 0.008f; // every tracking frame: 72-90 Hz

    private void OnUpdateFrame(Frame frame)
    {
        if (Time.time - _lastSendTime < SEND_INTERVAL) return;
        _lastSendTime = Time.time;

        int ind = frame.Hands.FindIndex(x => x.IsLeft);
        leftTracked = ind != -1;
        if (leftTracked) { leftVector.Encode(frame.Hands[ind]); leftVector.FillBytes(leftBytes); }

        ind = frame.Hands.FindIndex(x => !x.IsLeft);
        rightTracked = ind != -1;
        if (rightTracked) { rightVector.Encode(frame.Hands[ind]); rightVector.FillBytes(rightBytes); }

        UpdateHandRpc(ClockSync.Now, leftTracked, rightTracked, leftBytes, rightBytes);
    }

    // owner -> server -> other Quest (and the PC server itself), unreliable, timestamped
    [Rpc(SendTo.NotOwner, Delivery = RpcDelivery.Unreliable)]
    private void UpdateHandRpc(double t, bool lTracked, bool rTracked, byte[] lBytes, byte[] rBytes)
    {
        var snap = new Snapshot { t = t };
        if (lTracked) { snap.l = new VectorHand(); snap.l.ReadBytes(lBytes); }
        if (rTracked) { snap.r = new VectorHand(); snap.r.ReadBytes(rBytes); }
        int i = buffer.Count;
        while (i > 0 && buffer[i - 1].t > t) i--;   // unreliable: may arrive out of order
        buffer.Insert(i, snap);
        if (buffer.Count > 64) buffer.RemoveAt(0);
    }

    private void Interpolate(double renderTime)
    {
        if (buffer.Count == 0) return;
        int i = buffer.Count - 1;
        while (i > 0 && buffer[i].t > renderTime) i--;
        var a = buffer[i];
        var b = i + 1 < buffer.Count ? buffer[i + 1] : a;
        float u = b.t > a.t ? Mathf.Clamp01((float)((renderTime - a.t) / (b.t - a.t))) : 0f;
        if (i > 1) buffer.RemoveRange(0, i - 1);
        LoadHandsData(Lerp(a.l, b.l, u, leftLerp), Lerp(a.r, b.r, u, rightLerp));
    }

    private static VectorHand Lerp(VectorHand a, VectorHand b, float u, VectorHand into)
    {
        if (a != null && b != null) { into.FillLerped(a, b, u); return into; }
        return u < 0.5f ? a : b;   // hand appeared / disappeared between snapshots
    }

    private void LoadHandsData(VectorHand l, VectorHand r)
    {
        if (leftModel != null)
        {
            leftModel.gameObject.SetActive(l != null);
            if (l != null) l.Decode(leftHandLatest); // store latest, Update() drives it
            leftHandReady = l != null;
        }

        if (rightModel != null)
        {
            rightModel.gameObject.SetActive(r != null);
            if (r != null) r.Decode(rightHandLatest);
            rightHandReady = r != null;
        }
    }
}