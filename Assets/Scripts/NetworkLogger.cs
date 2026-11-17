using System;
using System.Globalization;
using System.IO;
using System.Text;
using Leap;
using Unity.Netcode;
using UnityEngine;

public class NetworkDebug : MonoBehaviour
{
    // Session log on the ClockSync timeline, one folder per headset/PC:
    // persistentDataPath/DyadicLogs/<time>_<device>/{events,hands,objects}.csv
    static StreamWriter events, hands, objects;
    static readonly CultureInfo inv = CultureInfo.InvariantCulture;
    readonly StringBuilder sb = new StringBuilder(4096);

    public static void Event(string type, ulong objectId, string details)
    {
        Debug.Log($"[EVT] {type} obj={objectId} {details}");
        events?.WriteLine($"{ClockSync.Now.ToString("F6", inv)},{Time.frameCount},{Me()},{type},{objectId},\"{details}\"");
    }

    void Start()
    {
        CultureInfo.CurrentCulture = inv; // '.' decimals in every log line
        NetworkManager.Singleton.OnClientConnectedCallback += id =>
            Event("client_connected", 0, $"client={id}");
        NetworkManager.Singleton.OnClientDisconnectCallback += id =>
            Event("client_disconnected", 0, $"client={id}");
        NetworkManager.Singleton.OnServerStarted += () =>
            Debug.Log("[NGO] Server started");

        var dir = Path.Combine(Application.persistentDataPath, "DyadicLogs",
            $"{DateTime.Now:yyyyMMdd_HHmmss}_{SystemInfo.deviceName.Replace(' ', '_')}");
        Directory.CreateDirectory(dir);
        events = new StreamWriter(Path.Combine(dir, "events.csv"), false, Encoding.UTF8, 1 << 16);
        hands = new StreamWriter(Path.Combine(dir, "hands.csv"), false, Encoding.UTF8, 1 << 16);
        objects = new StreamWriter(Path.Combine(dir, "objects.csv"), false, Encoding.UTF8, 1 << 16);
        events.WriteLine("t_sync,frame,client,type,object_id,details");
        var h = new StringBuilder("t_sync,frame,hand,palm_px,palm_py,palm_pz,palm_qx,palm_qy,palm_qz,palm_qw");
        for (int j = 0; j < 25; j++) h.Append($",j{j}x,j{j}y,j{j}z");
        hands.WriteLine(h.Append(",phys_to_raw_m,blend_w").ToString());
        objects.WriteLine("t_sync,frame,object_id,name,px,py,pz,qx,qy,qz,qw,owner,is_owner,phase,holder,seq");
        Debug.Log($"[LOG] {dir}");
    }

    static string Me() => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
        ? NetworkManager.Singleton.LocalClientId.ToString() : "-1";

    void LateUpdate()
    {
        if (hands == null) return;
        string t = ClockSync.Now.ToString("F6", inv) + "," + Time.frameCount;

        // raw tracked joints + physics-to-raw offset (per frame, per tracked hand)
        var raw = GraspDetector.RawProvider;
        if (raw != null)
            foreach (var hand in raw.CurrentFrame.Hands)
            {
                sb.Clear().Append(t).Append(',').Append(hand.IsLeft ? 'L' : 'R');
                V(hand.PalmPosition); Q(hand.Rotation);
                foreach (var f in hand.fingers) { foreach (var b in f.bones) V(b.PrevJoint); V(f.bones[3].NextJoint); }
                var c = hand.IsLeft ? Chirality.Left : Chirality.Right;
                sb.Append(',').Append(HandBlendProvider.Separation(c).ToString("F5", inv))
                  .Append(',').Append(HandBlendProvider.Weight(c).ToString("F4", inv));
                hands.WriteLine(sb.ToString());
            }

        foreach (var o in NetworkOwnershipRequest.All)
        {
            var s = o.State;
            sb.Clear().Append(t).Append(',').Append(o.NetworkObjectId).Append(',').Append(o.name);
            V(o.transform.position); Q(o.transform.rotation);
            sb.Append(',').Append(o.OwnerClientId).Append(',').Append(o.IsOwner ? 1 : 0).Append(',').Append(s.Phase)
              .Append(',').Append(s.Holder).Append(',').Append(s.Seq);
            objects.WriteLine(sb.ToString());
        }
    }

    void V(Vector3 v) => sb.Append(',').Append(v.x.ToString("F5", inv)).Append(',').Append(v.y.ToString("F5", inv)).Append(',').Append(v.z.ToString("F5", inv));
    void Q(Quaternion q) => sb.Append(',').Append(q.x.ToString("F6", inv)).Append(',').Append(q.y.ToString("F6", inv)).Append(',').Append(q.z.ToString("F6", inv)).Append(',').Append(q.w.ToString("F6", inv));

    void OnApplicationPause(bool paused) { if (paused) { events?.Flush(); hands?.Flush(); objects?.Flush(); } }

    void OnDestroy()
    {
        events?.Dispose(); hands?.Dispose(); objects?.Dispose();
        events = hands = objects = null;
    }
}
