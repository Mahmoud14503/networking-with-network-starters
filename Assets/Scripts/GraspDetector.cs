using System.Collections.Generic;
using Leap;
using Leap.PhysicalHands;
using UnityEngine;

// PHI-NOM grasp onset + aperture release for one RAW tracked hand (one component per hand).
// Onset: one ray per fingertip along the finger pad; contact valid if fingertip within
// eps_d = 1.5 cm of the hit point; thumb + >=1 opposing finger valid on the same object with
// normal angle > 120 deg; held for 2 frames (+ the object's calibrated extra frames).
// Release (holder): aperture > grasp aperture + 1.5 cm (or + 0.8 cm while opening > 15 cm/s)
// for 2 frames. Re-grasp needs aperture < grasp aperture + margin/2 plus a fresh onset.
[DefaultExecutionOrder(-10)]
public class GraspDetector : MonoBehaviour
{
    public static readonly List<GraspDetector> Local = new List<GraspDetector>();
    static LeapProvider raw;
    public static LeapProvider RawProvider
    {
        get
        {
            if (raw == null)
            {
                var phys = FindAnyObjectByType<PhysicalHandsManager>();
                raw = phys != null ? phys.InputProvider : Hands.Provider;
            }
            return raw;
        }
    }

    public Chirality hand = Chirality.Right;
    public float epsD = 0.015f, minNormalAngle = 120f, rayBack = 0.03f, rayForward = 0.05f;
    public int onsetFrames = 2, releaseFrames = 2;
    public float releaseMargin = 0.015f, fastMargin = 0.008f, fastSpeed = 0.15f;

    public bool IsTracked => h != null;
    public byte HandIndex => (byte)(hand == Chirality.Left ? 0 : 1);
    public Pose Palm => h != null ? new Pose(h.PalmPosition, h.Rotation) : lastPalm;
    public Vector3 PalmVelocity { get; private set; }
    public Vector3 PalmAngularVelocity { get; private set; }
    public NetworkOwnershipRequest Holding { get; private set; }

    Hand h;
    Pose lastPalm;
    readonly RaycastHit[] hits = new RaycastHit[32];
    readonly NetworkOwnershipRequest[] cObj = new NetworkOwnershipRequest[5];
    readonly Vector3[] cNormal = new Vector3[5];
    readonly bool[] cValid = new bool[5];

    NetworkOwnershipRequest streakObj;
    int streak, streakMask;
    double streakStart;
    readonly Dictionary<NetworkOwnershipRequest, float> rearmBelow = new Dictionary<NetworkOwnershipRequest, float>();

    int holdMask, releaseCount;
    float graspAperture, prevAperture = float.NaN, releaseMarginUsed;
    double releaseStart, lastTracked;

    void OnEnable() => Local.Add(this);
    void OnDisable() => Local.Remove(this);

    void Update()
    {
        if (RawProvider == null) return;
        double now = ClockSync.Now;
        h = RawProvider.CurrentFrame.GetHand(hand);
        if (h == null)
        {
            streak = 0;
            if (Holding != null && now - lastTracked > 0.25) Release(now, now, 0f, "tracking_lost");
            return;
        }
        lastTracked = now;

        var palm = new Pose(h.PalmPosition, h.Rotation);
        if (Time.deltaTime > 1e-5f)
        {
            PalmVelocity = Vector3.Lerp(PalmVelocity, (palm.position - lastPalm.position) / Time.deltaTime, 0.5f);
            (palm.rotation * Quaternion.Inverse(lastPalm.rotation)).ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            if (!float.IsNaN(axis.x)) PalmAngularVelocity = Vector3.Lerp(PalmAngularVelocity, axis * ang * Mathf.Deg2Rad / Time.deltaTime, 0.5f);
        }
        lastPalm = palm;

        // one ray per fingertip along the finger-pad direction (distal bone -Y)
        for (int f = 0; f < 5; f++)
        {
            var dir = h.fingers[f].bones[3].Rotation * Vector3.down;
            var tip = h.fingers[f].TipPosition;
            int n = Physics.RaycastNonAlloc(tip - dir * rayBack, dir, hits, rayBack + rayForward, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; cValid[f] = false; cObj[f] = null;
            for (int i = 0; i < n; i++)
            {
                var o = hits[i].rigidbody != null ? hits[i].rigidbody.GetComponent<NetworkOwnershipRequest>() : null;
                if (o == null || hits[i].distance >= best) continue;
                best = hits[i].distance;
                cObj[f] = o; cNormal[f] = hits[i].normal;
                cValid[f] = hits[i].distance - rayBack <= epsD; // <0 = fingertip inside the surface
            }
        }

        if (Holding != null) CheckRelease(now);
        CheckOnset(now);
    }

    void CheckOnset(double now)
    {
        var obj = cValid[0] ? cObj[0] : null;
        int mask = 0;
        if (obj != null)
            for (int f = 1; f < 5; f++)
                if (cValid[f] && cObj[f] == obj && Vector3.Angle(cNormal[0], cNormal[f]) > minNormalAngle) mask |= 1 << f;
        if (mask == 0 || Holding != null) { streak = 0; streakObj = null; return; }

        if (rearmBelow.TryGetValue(obj, out float below))
        {
            if (Aperture(mask) < below) rearmBelow.Remove(obj);
            else { streak = 0; return; }
        }
        if (obj != streakObj) { streakObj = obj; streak = 0; streakStart = now; }
        streak++;
        streakMask = mask;
        if (streak == onsetFrames + obj.onsetExtraFrames)
            NetworkDebug.Event("onset", obj.NetworkObjectId,
                $"hand={HandIndex};t_first={streakStart:F6};t_detect={now:F6};aperture={Aperture(mask):F4};mask={mask}");
    }

    // true while the PHI-NOM rule (incl. the frame window) holds for obj
    public bool OnsetActive(NetworkOwnershipRequest obj) =>
        h != null && Holding == null && streakObj == obj && streak >= onsetFrames + obj.onsetExtraFrames;

    float Aperture(int mask)
    {
        Vector3 sum = Vector3.zero; int n = 0;
        for (int f = 1; f < 5; f++) if ((mask & (1 << f)) != 0) { sum += h.fingers[f].TipPosition; n++; }
        return n == 0 ? 0f : (h.fingers[0].TipPosition - sum / n).magnitude;
    }

    public void BeginHold(NetworkOwnershipRequest obj)
    {
        Holding = obj;
        holdMask = streakMask != 0 ? streakMask : 0b11110;
        graspAperture = Aperture(holdMask);
        prevAperture = float.NaN;
        releaseCount = 0;
        streak = 0;
    }

    public void EndHold() => Holding = null;

    void CheckRelease(double now)
    {
        float a = Aperture(holdMask);
        float speed = float.IsNaN(prevAperture) ? 0f : (a - prevAperture) / Mathf.Max(Time.deltaTime, 1e-5f);
        prevAperture = a;
        float open = a - graspAperture;
        float margin = open > releaseMargin ? releaseMargin : (open > fastMargin && speed > fastSpeed ? fastMargin : 0f);
        if (margin == 0f) { releaseCount = 0; return; }
        if (releaseCount++ == 0) { releaseStart = now; releaseMarginUsed = margin; }
        releaseMarginUsed = Mathf.Max(releaseMarginUsed, margin);
        if (releaseCount >= releaseFrames) Release(releaseStart, now, speed, "aperture");
    }

    void Release(double tFirst, double now, float speed, string reason)
    {
        var obj = Holding;
        Holding = null;
        if (reason == "aperture") rearmBelow[obj] = graspAperture + releaseMarginUsed * 0.5f;
        NetworkDebug.Event("release", obj.NetworkObjectId,
            $"hand={HandIndex};reason={reason};t_first={tFirst:F6};t_detect={now:F6};grasp_aperture={graspAperture:F4};margin={releaseMarginUsed:F4};speed={speed:F3}");
        obj.OnLocalRelease(this, now);
    }
}
