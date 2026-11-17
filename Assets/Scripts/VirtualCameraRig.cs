using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Leap;
using Leap.Encoding;
using UnityEngine;

// PC server: four cameras at the real rig's extrinsics (table frame = world), each rendering
// to a RenderTexture recorded by Editor/VirtualCameraRecorder.cs (one Movie Recorder each, 60 fps).
// Live: renders the server scene (remote hands + objects from the network).
// Offline: replays NetworkDebug logs (hands.csv / objects.csv) on the synced timeline.
public class VirtualCameraRig : MonoBehaviour
{
    [Serializable]
    public class Cam
    {
        public string name = "Cam";
        public Vector3 position, eulerAngles;           // table frame, Unity convention (+Z forward, +Y up)
        public int width = 1920, height = 1080;
        public float fx, fy, cx, cy;                   // pixels; fx = 0 -> verticalFov
        public float verticalFov = 60f;
    }

    public bool offline;
    public bool autoRecord;
    public List<Cam> cameras = new List<Cam>
    {
        new Cam { name = "Cam1", position = new Vector3(-1f, 1.6f, -1f), eulerAngles = new Vector3(35f, 45f, 0f) },
        new Cam { name = "Cam2", position = new Vector3( 1f, 1.6f, -1f), eulerAngles = new Vector3(35f, -45f, 0f) },
        new Cam { name = "Cam3", position = new Vector3(-1f, 1.6f,  1f), eulerAngles = new Vector3(35f, 135f, 0f) },
        new Cam { name = "Cam4", position = new Vector3( 1f, 1.6f,  1f), eulerAngles = new Vector3(35f, -135f, 0f) },
    };
    [Tooltip("Offline: one log folder per headset (DyadicLogs/<time>_<device>)")]
    public List<string> logFolders = new List<string>();
    [Tooltip("Offline: hand model prefab, e.g. Ultraleap CapsuleHands' left/right hand")]
    public HandModelBase leftHandPrefab, rightHandPrefab;

    public readonly List<RenderTexture> targets = new List<RenderTexture>();
    public bool ReplayFinished { get; private set; }

    void Start()
    {
        foreach (var c in cameras)
        {
            var cam = new GameObject("VirtualCam_" + c.name).AddComponent<Camera>();
            cam.transform.SetParent(transform, false);
            cam.transform.SetPositionAndRotation(c.position, Quaternion.Euler(c.eulerAngles));
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            if (c.fx > 0f)
            {   // pinhole intrinsics: physical camera in pixel units
                cam.usePhysicalProperties = true;
                cam.gateFit = Camera.GateFitMode.None;
                cam.focalLength = c.fy;
                cam.sensorSize = new Vector2(c.width * c.fy / c.fx, c.height);
                cam.lensShift = new Vector2(-(c.cx - c.width * 0.5f) / c.width, (c.cy - c.height * 0.5f) / c.height);
            }
            else cam.fieldOfView = c.verticalFov;
            cam.targetTexture = new RenderTexture(c.width, c.height, 24) { name = "RT_" + c.name };
            targets.Add(cam.targetTexture);
        }
        if (offline) LoadLogs();
    }

    void OnGUI()   // 2x2 preview on the PC
    {
        for (int i = 0; i < targets.Count; i++)
            GUI.DrawTexture(new Rect(i % 2 * Screen.width / 2f, i / 2 * Screen.height / 2f, Screen.width / 2f, Screen.height / 2f), targets[i], ScaleMode.ScaleToFit);
    }

    // ---------------------------------------------------------------- offline replay

    class Track<T> { public List<double> t = new List<double>(); public List<T> v = new List<T>(); }
    readonly List<(Track<VectorHand> track, HandModelBase model)> hands = new List<(Track<VectorHand>, HandModelBase)>();
    readonly List<(Track<Pose> track, Transform target)> objects = new List<(Track<Pose>, Transform)>();
    readonly VectorHand lerp = new VectorHand();
    readonly Hand hand = new Hand();
    double time, end;

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    void LoadLogs()
    {
        var objTracks = new Dictionary<string, List<(double t, Pose p)>>();
        time = double.MaxValue; end = double.MinValue;
        foreach (var dir in logFolders)
        {
            var handTracks = new Dictionary<string, Track<VectorHand>>();
            foreach (var c in File.ReadLines(Path.Combine(dir, "hands.csv")).Skip(1).Select(l => l.Split(',')))
            {
                double t = double.Parse(c[0], CultureInfo.InvariantCulture);
                Vector3 palm = new Vector3(F(c[3]), F(c[4]), F(c[5]));
                var rot = new Quaternion(F(c[6]), F(c[7]), F(c[8]), F(c[9]));
                var joints = new Vector3[25];
                for (int j = 0; j < 25; j++)
                    joints[j] = VectorHand.ToLocal(new Vector3(F(c[10 + 3 * j]), F(c[11 + 3 * j]), F(c[12 + 3 * j])), palm, rot);
                if (!handTracks.TryGetValue(c[2], out var tr)) handTracks[c[2]] = tr = new Track<VectorHand>();
                tr.t.Add(t); tr.v.Add(new VectorHand(c[2] == "L", palm, rot, joints));
                time = Math.Min(time, t); end = Math.Max(end, t);
            }
            foreach (var kv in handTracks)
            {
                var prefab = kv.Key == "L" ? leftHandPrefab : rightHandPrefab;
                if (prefab == null) continue;
                var model = Instantiate(prefab, transform);
                model.leapProvider = null;
                hands.Add((kv.Value, model));
            }
            // object pose from whichever headset owned it at that moment (is_owner = 1)
            foreach (var c in File.ReadLines(Path.Combine(dir, "objects.csv")).Skip(1).Select(l => l.Split(',')))
            {
                if (c[12] != "1") continue;
                if (!objTracks.TryGetValue(c[3], out var list)) objTracks[c[3]] = list = new List<(double, Pose)>();
                list.Add((double.Parse(c[0], CultureInfo.InvariantCulture),
                          new Pose(new Vector3(F(c[4]), F(c[5]), F(c[6])), new Quaternion(F(c[7]), F(c[8]), F(c[9]), F(c[10])))));
            }
        }
        foreach (var kv in objTracks)
        {
            var go = GameObject.Find(kv.Key);
            if (go == null) continue;
            if (go.TryGetComponent<Rigidbody>(out var rb)) rb.isKinematic = true;
            var tr = new Track<Pose>();
            foreach (var (t, p) in kv.Value.OrderBy(x => x.t)) { tr.t.Add(t); tr.v.Add(p); }
            objects.Add((tr, go.transform));
        }
    }

    void Update()
    {
        if (!offline || ReplayFinished || hands.Count + objects.Count == 0) return;
        time += Time.deltaTime;                         // constant 1/60 s while recording
        if (time >= end) ReplayFinished = true;
        foreach (var (track, model) in hands)
        {
            bool on = Bracket(track.t, time, out int a, out int b, out float u);
            model.gameObject.SetActive(on);
            if (!on) continue;
            lerp.FillLerped(track.v[a], track.v[b], u);
            lerp.Decode(hand);
            model.SetLeapHand(hand);
            model.UpdateHand();
        }
        foreach (var (track, target) in objects)
            if (Bracket(track.t, time, out int a, out int b, out float u))
                target.SetPositionAndRotation(Vector3.Lerp(track.v[a].position, track.v[b].position, u),
                                              Quaternion.Slerp(track.v[a].rotation, track.v[b].rotation, u));
    }

    static bool Bracket(List<double> ts, double t, out int a, out int b, out float u)
    {
        a = b = 0; u = 0f;
        int i = ts.BinarySearch(t);
        if (i < 0) i = ~i;
        if (i == 0 || i >= ts.Count) return false;
        a = i - 1; b = i;
        if (ts[b] - ts[a] > 0.1) return false;          // tracking gap: hide the hand
        u = (float)((t - ts[a]) / (ts[b] - ts[a]));
        return true;
    }
}
