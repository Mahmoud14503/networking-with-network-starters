using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// NTP-style clock sync: the PC server is the time master; clients exchange one
// request/response per second and keep the offset of the min-RTT sample of the last 16.
// ClockSync.Now = shared timeline (server clock, seconds) used by all logs and hand timestamps.
public class ClockSync : MonoBehaviour
{
    static readonly Stopwatch watch = Stopwatch.StartNew();
    public static double LocalTime => watch.Elapsed.TotalSeconds;
    public static double Offset { get; private set; }
    public static double Now => LocalTime + Offset;
    public static double BestRtt { get; private set; } = double.NaN;

    readonly List<(double offset, double rtt)> samples = new List<(double, double)>();
    bool registered;
    double nextSend;

    void Update()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) { registered = false; return; }
        if (!registered)
        {
            if (nm.IsServer) nm.CustomMessagingManager.RegisterNamedMessageHandler("ClockReq", OnRequest);
            else nm.CustomMessagingManager.RegisterNamedMessageHandler("ClockResp", OnResponse);
            registered = true;
        }
        if (nm.IsServer || !nm.IsConnectedClient || LocalTime < nextSend) return;
        nextSend = LocalTime + (samples.Count < 10 ? 0.05 : 1.0); // short burst, then 1 Hz
        using var w = new FastBufferWriter(8, Allocator.Temp);
        w.WriteValueSafe(LocalTime);
        nm.CustomMessagingManager.SendNamedMessage("ClockReq", NetworkManager.ServerClientId, w, NetworkDelivery.Reliable);
    }

    void OnRequest(ulong sender, FastBufferReader r)
    {
        double t1 = LocalTime;
        r.ReadValueSafe(out double t0);
        using var w = new FastBufferWriter(24, Allocator.Temp);
        w.WriteValueSafe(t0); w.WriteValueSafe(t1); w.WriteValueSafe(LocalTime);
        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("ClockResp", sender, w, NetworkDelivery.Reliable);
    }

    void OnResponse(ulong sender, FastBufferReader r)
    {
        double t3 = LocalTime;
        r.ReadValueSafe(out double t0); r.ReadValueSafe(out double t1); r.ReadValueSafe(out double t2);
        double rtt = (t3 - t0) - (t2 - t1);
        samples.Add((((t1 - t0) + (t2 - t3)) * 0.5, rtt));
        if (samples.Count > 16) samples.RemoveAt(0);
        var best = samples[0];
        foreach (var s in samples) if (s.rtt < best.rtt) best = s;
        Offset = best.offset;
        BestRtt = best.rtt;
        NetworkDebug.Event("rtt", 0, $"rtt_ms={rtt * 1000:F3};best_rtt_ms={best.rtt * 1000:F3};offset_ms={Offset * 1000:F3}");
    }
}
