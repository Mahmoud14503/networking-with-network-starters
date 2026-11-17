using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
#if DYADIC_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
#endif

// Unity Recorder: one Movie Recorder per virtual camera at 60 fps (constant rate when replaying
// offline, variable/capped when live). The recorder part compiles only once com.unity.recorder
// is installed: this script adds the DYADIC_RECORDER define automatically.
[InitializeOnLoad]
static class VirtualCameraRecorder
{
    static VirtualCameraRecorder()
    {
        EditorApplication.delayCall += () =>
        {
            bool has = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("UnityEditor.Recorder.RecorderController", false) != null);
            var target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            var defines = PlayerSettings.GetScriptingDefineSymbols(target).Split(';').Where(d => d.Length > 0).ToList();
            if (has == defines.Contains("DYADIC_RECORDER")) return;
            if (has) defines.Add("DYADIC_RECORDER"); else defines.Remove("DYADIC_RECORDER");
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
        };
#if DYADIC_RECORDER
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode) Stop(); };
#endif
    }

#if DYADIC_RECORDER
    static RecorderController controller;
    static VirtualCameraRig rig;

    static void Tick()
    {
        if (!Application.isPlaying) return;
        if (controller == null)
        {
            var r = UnityEngine.Object.FindAnyObjectByType<VirtualCameraRig>();
            if (r != null && r.autoRecord && r.targets.Count > 0 && !r.ReplayFinished) Start(r);
        }
        else if (rig != null && rig.offline && rig.ReplayFinished) Stop();
    }

    [MenuItem("Dyadic/Record Virtual Cameras")]
    static void StartMenu() { var r = UnityEngine.Object.FindAnyObjectByType<VirtualCameraRig>(); if (r != null) Start(r); }

    [MenuItem("Dyadic/Stop Recording")]
    static void Stop()
    {
        if (controller != null && controller.IsRecording()) controller.StopRecording();
        controller = null;
    }

    static void Start(VirtualCameraRig r)
    {
        if (controller != null) return;
        var settings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
        string folder = $"Recordings/{DateTime.Now:yyyyMMdd_HHmmss}_{(r.offline ? "offline" : "live")}";
        for (int i = 0; i < r.targets.Count; i++)
        {
            var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            movie.name = r.cameras[i].name;
            movie.Enabled = true;
            movie.EncoderSettings = new CoreEncoderSettings { Codec = CoreEncoderSettings.OutputCodec.MP4, EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High };
            movie.ImageInputSettings = new RenderTextureInputSettings { RenderTexture = r.targets[i], OutputWidth = r.targets[i].width, OutputHeight = r.targets[i].height };
            movie.OutputFile = $"{folder}/{r.cameras[i].name}";
            settings.AddRecorderSettings(movie);
        }
        settings.SetRecordModeToManual();
        settings.FrameRate = 60;
        settings.FrameRatePlayback = r.offline ? FrameRatePlayback.Constant : FrameRatePlayback.Variable;
        settings.CapFrameRate = true;
        controller = new RecorderController(settings);
        controller.PrepareRecording();
        controller.StartRecording();
        rig = r;
    }
#endif
}
