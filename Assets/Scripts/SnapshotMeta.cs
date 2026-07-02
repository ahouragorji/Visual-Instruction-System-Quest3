using System;
using UnityEngine;

/// <summary>
/// Mirrors the JSON written by the Quest sender for each captured frame.
/// Used by the PC receiver (writer) and the Python detection/reprojection server (reader).
/// </summary>
[Serializable]
public class SnapshotMeta
{
    public int    cameraIndex;
    public string timestamp;
    public string fileName;

    // ── RGB camera pose in Unity world-space at the moment of capture ──
    public float posX, posY, posZ;
    public float rotX, rotY, rotZ, rotW;

    // ── RGB camera intrinsics ──
    public int   imageWidth;
    public int   imageHeight;
    public float fx, fy, cx, cy;
    public float[] distortionParams;

    // ── Depth camera pose in Unity world-space at the moment of capture ──
    // The depth sensor is physically offset from the RGB sensor on the Quest,
    // so it has its own pose and intrinsics. reprojection.py uses both poses
    // to map an RGB pixel to the correct depth-image pixel.
    public float depth_posX, depth_posY, depth_posZ;
    public float depth_rotX, depth_rotY, depth_rotZ, depth_rotW;

    // ── Depth camera intrinsics ──
    public float depth_fx, depth_fy, depth_cx, depth_cy;
    public int   depthWidth;
    public int   depthHeight;

    // ── Near/far planes used to linearize this depth frame (diagnostic only;
    //    linearization already happened on-device in DepthToFloat.shader) ──
    public float depthNearZ;
    public float depthFarZ;

    // ── Payload (cleared before writing to disk) ──
    public string imageRGB;
    public string imageDepth;

    // the voice command 
    public string command;
    // ── Convenience accessors ──

    /// <summary>Unity world-space position of the RGB camera at capture time.</summary>
    public Vector3    QuestPosition => new Vector3(posX, posY, posZ);

    /// <summary>Unity world-space rotation of the RGB camera at capture time.</summary>
    public Quaternion QuestRotation => new Quaternion(rotX, rotY, rotZ, rotW);

    /// <summary>Unity world-space position of the depth camera at capture time.</summary>
    public Vector3    DepthPosition => new Vector3(depth_posX, depth_posY, depth_posZ);

    /// <summary>Unity world-space rotation of the depth camera at capture time.</summary>
    public Quaternion DepthRotation => new Quaternion(depth_rotX, depth_rotY, depth_rotZ, depth_rotW);

    /// <summary>
    /// Serialises intrinsics to the format hloc/COLMAP expects:
    /// "PINHOLE width height fx fy cx cy"
    /// Distortion is all-zeros from the Quest passthrough camera, so PINHOLE is correct.
    /// </summary>
    public string ColmapIntrinsicsString =>
        $"PINHOLE {imageWidth} {imageHeight} {fx} {fy} {cx} {cy}";

    public string intent;

    public bool   useYoloe; 
}
