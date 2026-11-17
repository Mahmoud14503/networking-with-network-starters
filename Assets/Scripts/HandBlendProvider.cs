using Leap;
using Leap.Encoding;
using Leap.PhysicalHands;
using UnityEngine;

// Rendered local hand = Physical Hands (Hard Contact) output, blended towards the raw tracked
// hand as the physics hand gets blocked (separation 1 cm -> 3.5 cm), smoothed over ~50 ms,
// so the rendered hand never freezes or snaps. Re-binds the local hand models at start.
public class HandBlendProvider : PostProcessProvider
{
    public float blendStart = 0.10f, blendEnd = 0.15f, smoothTime = 0.05f;

    readonly float[] weight = new float[2];

    // raw tracked hands = the Physical Hands Manager's input provider
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

    readonly VectorHand vPhys = new VectorHand(), vRaw = new VectorHand();

    void Start()
    {
        var phys = FindAnyObjectByType<PhysicalHandsManager>();
        if (phys == null) return;
        inputLeapProvider = phys;
        foreach (var m in FindObjectsByType<HandModelBase>(FindObjectsInactive.Include))
            if (m.leapProvider == phys && m.HandModelType == ModelType.Graphics) m.leapProvider = this;
    }

    public override void ProcessFrame(ref Frame frame)
    {
        var raw = RawProvider;
        if (raw == null) return;
        foreach (var ph in frame.Hands)
        {
            var rh = raw.CurrentFrame.GetHand(ph.IsLeft ? Chirality.Left : Chirality.Right);
            if (rh == null) continue;
            int c = ph.IsLeft ? 0 : 1;
            if (!Time.inFixedTimeStep)
            {
                float sep = (ph.PalmPosition - rh.PalmPosition).magnitude;
                for (int f = 0; f < 5; f++) sep = Mathf.Max(sep, (ph.fingers[f].TipPosition - rh.fingers[f].TipPosition).magnitude);
                float target = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(blendStart, blendEnd, sep));
                weight[c] = Mathf.Lerp(weight[c], target, 1f - Mathf.Exp(-Time.deltaTime / smoothTime));
            }
            if (weight[c] < 0.001f) continue;
            vPhys.Encode(ph); vRaw.Encode(rh);
            vPhys.FillLerped(vPhys, vRaw, weight[c]);
            vPhys.Decode(ph);
        }
    }
}
