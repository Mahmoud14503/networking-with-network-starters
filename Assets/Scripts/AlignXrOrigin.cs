using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;

public class NetworkedOriginSpawner : MonoBehaviour
{
    // Shared room frame from two QR codes on the table (Meta MR Utility Kit v78+, system QR tracking,
    // Spatial Data permission). Poses averaged over 5 s with IQR outlier rejection; the XR origin
    // is set once so the table frame lands at the world origin; QR tracking is then switched off.
    // MRUK is used via reflection so the project compiles before the package is installed.
    public string[] payloads = { "TABLE_A", "TABLE_B" };
    public Vector3[] tablePositions = { new Vector3(-0.4f, 0f, 0f), new Vector3(0.4f, 0f, 0f) }; // QR centres in the table frame (m)
    public float sampleSeconds = 5f;

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

        // 2. Register both headsets to the table QR codes (Quest only; the PC uses the table frame as world)
        if (Application.platform == RuntimePlatform.Android) StartCoroutine(Calibrate());
    }

    IEnumerator Calibrate()
    {
        const string permission = "com.oculus.permission.USE_SCENE"; // Spatial Data; no camera permission needed
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
            UnityEngine.Android.Permission.RequestUserPermission(permission);
        while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission)) yield return null;

        var mrukType = FindType("Meta.XR.MRUtilityKit.MRUK");
        var trackableType = FindType("Meta.XR.MRUtilityKit.MRUKTrackable");
        if (mrukType == null) { Debug.LogError("[CAL] Meta MR Utility Kit not installed"); yield break; }
        object mruk;
        while ((mruk = mrukType.GetProperty("Instance")?.GetValue(null)) == null) yield return null;
        SetQrTracking(mruk, true);

        // tracking space = XR Origin's Camera Offset; MRUK reports world poses only with an OVRCameraRig
        var trackingSpace = transform.childCount > 0 ? transform.GetChild(0) : transform;
        bool worldPoses = FindType("OVRCameraRig") is Type rig && FindAnyObjectByType(rig) != null;
        var samples = payloads.ToDictionary(p => p, p => new List<Vector3>());
        float until = float.MaxValue;
        while (Time.time < until)
        {
            foreach (Component t in FindObjectsByType(trackableType))
            {
                if (Get(t, "TrackableType")?.ToString() != "QRCode" || Get(t, "IsTracked") is false) continue;
                if (Get(t, "MarkerPayloadString") is string p && samples.TryGetValue(p.Trim(), out var list))
                    list.Add(worldPoses ? trackingSpace.InverseTransformPoint(t.transform.position) : t.transform.position);
            }
            if (until == float.MaxValue && samples.Values.All(l => l.Count > 0))
            {   // both codes seen: start the 5 s averaging window
                foreach (var l in samples.Values) l.Clear();
                until = Time.time + sampleSeconds;
            }
            yield return null;
        }
        SetQrTracking(mruk, false);

        // robust mean per code, then gravity-aligned (yaw + translation) fit: tracking <- table
        var measured = payloads.Select(p => RobustMean(samples[p])).ToArray();
        Vector3 ct = Vector3.zero, cm = Vector3.zero;
        for (int i = 0; i < measured.Length; i++) { ct += tablePositions[i]; cm += measured[i]; }
        ct /= measured.Length; cm /= measured.Length;
        double dot = 0, cross = 0;
        for (int i = 0; i < measured.Length; i++)
        {
            Vector3 a = tablePositions[i] - ct, b = measured[i] - cm;
            dot += a.x * b.x + a.z * b.z;
            cross += a.z * b.x - a.x * b.z;
        }
        var yaw = Quaternion.AngleAxis((float)(Math.Atan2(cross, dot) * Mathf.Rad2Deg), Vector3.up);
        var trackingFromTable = new Pose(cm - yaw * ct, yaw);

        // XR origin set once: world = table frame  =>  rootWorld * offsetLocal * trackingFromTable = identity
        var inv = Quaternion.Inverse(trackingFromTable.rotation);
        var worldFromTracking = new Pose(inv * -trackingFromTable.position, inv);
        var offRot = trackingSpace == transform ? Quaternion.identity : trackingSpace.localRotation;
        var offPos = trackingSpace == transform ? Vector3.zero : trackingSpace.localPosition;
        var rootRot = worldFromTracking.rotation * Quaternion.Inverse(offRot);
        transform.SetPositionAndRotation(worldFromTracking.position - rootRot * offPos, Quaternion.Euler(0f, rootRot.eulerAngles.y, 0f));

        float rms = Mathf.Sqrt(Enumerable.Range(0, measured.Length)
            .Select(i => (yaw * tablePositions[i] + trackingFromTable.position - measured[i]).sqrMagnitude).Average());
        NetworkDebug.Event("calibration", 0, $"rms_mm={rms * 1000f:F2};samples={string.Join("|", samples.Values.Select(l => l.Count))}");
    }

    static Vector3 RobustMean(List<Vector3> pts)
    {
        var med = new Vector3(Median(pts.Select(p => p.x)), Median(pts.Select(p => p.y)), Median(pts.Select(p => p.z)));
        var d = pts.Select(p => (p - med).magnitude).OrderBy(x => x).ToList();
        float q1 = d[d.Count / 4], q3 = d[3 * d.Count / 4], hi = q3 + 1.5f * (q3 - q1);
        var inliers = pts.Where(p => (p - med).magnitude <= hi).ToList();
        return inliers.Aggregate(Vector3.zero, (s, p) => s + p) / inliers.Count;
    }

    static float Median(IEnumerable<float> xs) { var s = xs.OrderBy(x => x).ToList(); return s[s.Count / 2]; }

    static Type FindType(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

    static object Get(object o, string member) =>
        o.GetType().GetProperty(member)?.GetValue(o) ?? o.GetType().GetField(member)?.GetValue(o);

    // MRUK.Instance.SceneSettings.TrackerConfiguration.QRCodeTrackingEnabled (struct: modify, write back)
    static void SetQrTracking(object mruk, bool on)
    {
        try
        {
            var settings = Get(mruk, "SceneSettings");
            var cfgProp = (System.Reflection.MemberInfo)settings.GetType().GetProperty("TrackerConfiguration")
                          ?? settings.GetType().GetField("TrackerConfiguration");
            var cfg = Get(settings, "TrackerConfiguration");
            var qr = cfg.GetType().GetProperty("QRCodeTrackingEnabled");
            if (qr != null) qr.SetValue(cfg, on); else cfg.GetType().GetField("QRCodeTrackingEnabled").SetValue(cfg, on);
            if (cfgProp is System.Reflection.PropertyInfo pi) pi.SetValue(settings, cfg);
            else ((System.Reflection.FieldInfo)cfgProp).SetValue(settings, cfg);
            NetworkDebug.Event("qr_tracking", 0, $"enabled={on}");
        }
        catch (Exception e) { Debug.LogWarning($"[CAL] enable QR Code Tracking in the MRUK component ({e.Message})"); }
    }
}
