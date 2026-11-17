using Leap;
using Leap.Encoding;
using Leap.PhysicalHands;
using UnityEngine;

// Rendered local hand = Physical Hands (Hard Contact) output, blended towards the raw tracked
// hand as the physics hand gets blocked (separation 1 cm -> 3.5 cm), smoothed over ~50 ms,
// so the rendered hand never freezes or snaps. Re-binds the local hand models at start.
public class HandBlendProvider : PostProcessProvider
{
    public float blendStart = 0.01f, blendEnd = 0.035f, smoothTime = 0.05f;

    static readonly float[] separation = new float[2], weight = new float[2];
    public static float Separation(Chirality c) => separation[(int)c];
    public static float Weight(Chirality c) => weight[(int)c];

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
        var raw = GraspDetector.RawProvider;
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
                separation[c] = sep;
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
