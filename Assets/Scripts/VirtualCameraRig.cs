using System;
using System.Collections.Generic;
using UnityEngine;

// PC server: four cameras at the real rig's extrinsics (table frame = world), shown live to the
// study admin as a 2x2 view on the PC (remote hands + objects from the network). Nothing is recorded.
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

    public List<Cam> cameras = new List<Cam>
    {
        new Cam { name = "Cam1", position = new Vector3(-1f, 1.6f, -1f), eulerAngles = new Vector3(35f, 45f, 0f) },
        new Cam { name = "Cam2", position = new Vector3( 1f, 1.6f, -1f), eulerAngles = new Vector3(35f, -45f, 0f) },
        new Cam { name = "Cam3", position = new Vector3(-1f, 1.6f,  1f), eulerAngles = new Vector3(35f, 135f, 0f) },
        new Cam { name = "Cam4", position = new Vector3( 1f, 1.6f,  1f), eulerAngles = new Vector3(35f, -135f, 0f) },
    };
    readonly List<RenderTexture> targets = new List<RenderTexture>();

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
    }

    void OnGUI()   // 2x2 preview on the PC
    {
        for (int i = 0; i < targets.Count; i++)
            GUI.DrawTexture(new Rect(i % 2 * Screen.width / 2f, i / 2 * Screen.height / 2f, Screen.width / 2f, Screen.height / 2f), targets[i], ScaleMode.ScaleToFit);
    }
}
