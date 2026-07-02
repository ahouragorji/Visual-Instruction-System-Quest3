"""
camera_math.py

Geometry helpers that bridge Unity's pose/intrinsics convention (as written by
QuestPassthroughSender into SnapshotMeta JSON) with the reprojection.py module's
expected inputs (rotation matrices, translation vectors, OpenCV-style pinhole
intrinsics).

Coordinate system notes
------------------------
Unity uses a left-handed coordinate system (X right, Y up, Z forward) and
quaternions in (x, y, z, w) order. reprojection.py and standard computer
vision pipelines (OpenCV, COLMAP) assume right-handed camera coordinates
(X right, Y down, Z forward, looking down +Z). We convert explicitly rather
than assuming the two happen to line up — they don't, and silently treating
a Unity quaternion as an OpenCV rotation matrix produces a transform that
"looks plausible" (it's still a valid rotation) but reprojects to the wrong
point, which is a very easy bug to miss until your balls show up in the
wrong corner of the room.
"""

import numpy as np


def quat_to_rotmat_unity(qx, qy, qz, qw):
    """
    Converts a Unity quaternion (left-handed, x,y,z,w) into a 3x3 rotation
    matrix that operates in Unity's left-handed coordinate convention.
    This matches the standard quaternion-to-matrix formula; handedness is a
    property of how the resulting matrix's columns are later interpreted,
    not of the formula itself.
    """
    x, y, z, w = qx, qy, qz, qw
    n = x * x + y * y + z * z + w * w
    if n < 1e-12:
        return np.eye(3)
    s = 2.0 / n

    wx, wy, wz = s * w * x, s * w * y, s * w * z
    xx, xy, xz = s * x * x, s * x * y, s * x * z
    yy, yz, zz = s * y * y, s * y * z, s * z * z

    return np.array([
        [1.0 - (yy + zz), xy - wz,         xz + wy        ],
        [xy + wz,         1.0 - (xx + zz), yz - wx        ],
        [xz - wy,         yz + wx,         1.0 - (xx + yy)],
    ])


def unity_to_cv_point(p_unity):
    """
    Converts a point from Unity's left-handed (X right, Y up, Z forward) space
    into a right-handed OpenCV-style camera space (X right, Y down, Z forward).
    Flipping Y is sufficient and standard for this conversion; X and Z keep
    their sign and meaning, so the conversion is just a single axis flip.
    """
    return np.array([p_unity[0], -p_unity[1], p_unity[2]])


def cv_to_unity_point(p_cv):
    """Inverse of unity_to_cv_point — flips Y back."""
    return np.array([p_cv[0], -p_cv[1], p_cv[2]])


def build_depth_to_rgb_transform(meta):
    """
    Given a SnapshotMeta-shaped dict (as saved by PCScreenshotReceiver to
    Meta_<id>.json), builds the rotation R_d2r and translation T_d2r that
    reprojection.py expects: the transform that takes a point in the DEPTH
    camera's local space and expresses it in the RGB camera's local space.

    Both poses in the metadata are given in Unity WORLD space (position +
    rotation of each camera at capture time). We compute the relative
    transform between them directly, which automatically accounts for the
    physical offset between the two sensors on the headset, without needing
    a separate hand-measured baseline.

    Returns:
        R_d2r: 3x3 rotation matrix (depth space -> RGB space), OpenCV convention
        T_d2r: 3x1 translation vector (metres), OpenCV convention
    """
    # World-space rotations of each camera (Unity convention)
    R_rgb_world_unity   = quat_to_rotmat_unity(meta["rotX"], meta["rotY"], meta["rotZ"], meta["rotW"])
    R_depth_world_unity = quat_to_rotmat_unity(meta["depth_rotX"], meta["depth_rotY"],
                                                 meta["depth_rotZ"], meta["depth_rotW"])

    pos_rgb_unity   = np.array([meta["posX"], meta["posY"], meta["posZ"]])
    pos_depth_unity = np.array([meta["depth_posX"], meta["depth_posY"], meta["depth_posZ"]])

    # Relative rotation: depth-local -> world -> rgb-local, still in Unity's
    # left-handed convention at this point.
    R_d2r_unity = R_rgb_world_unity.T @ R_depth_world_unity

    # Relative translation: vector from RGB camera to depth camera, expressed
    # in the RGB camera's local axes (still Unity convention).
    delta_world_unity = pos_depth_unity - pos_rgb_unity
    T_d2r_unity = R_rgb_world_unity.T @ delta_world_unity

    # Convert both into OpenCV's right-handed convention (Y flipped) so they
    # match what reprojection.py expects and what the pinhole intrinsics
    # (fx, fy, cx, cy) assume.
    flip = np.diag([1.0, -1.0, 1.0])
    R_d2r = flip @ R_d2r_unity @ flip
    T_d2r = flip @ T_d2r_unity

    return R_d2r, T_d2r.reshape(3, 1)


def rgb_camera_point_to_unity_world(point_cv, meta):
    """
    Takes a 3D point expressed in the RGB camera's LOCAL space (OpenCV
    convention: X right, Y down, Z forward — i.e. the output of unprojecting
    a pixel with a known depth), and transforms it into Unity WORLD space
    using the RGB camera's pose at capture time.

    This is the final step before sending a placement back to the headset:
    the server only knows the camera-relative location of a detected object,
    but the headset needs a world-space point that's valid regardless of
    where the user has moved since the photo was taken.
    """
    # Camera-local point: flip back to Unity's left-handed convention first.
    point_unity_local = cv_to_unity_point(point_cv)

    R_rgb_world_unity = quat_to_rotmat_unity(meta["rotX"], meta["rotY"], meta["rotZ"], meta["rotW"])
    pos_rgb_unity     = np.array([meta["posX"], meta["posY"], meta["posZ"]])

    point_world = R_rgb_world_unity @ point_unity_local + pos_rgb_unity
    return point_world

 
def unproject_pixel(u, v, depth_z, fx, fy, cx, cy):
    """
    Standard pinhole unprojection: given a pixel (u, v) and its depth Z
    (metres, along the camera's forward axis), returns the 3D point in the
    camera's local OpenCV-convention space.
    """
    # adding offset
    x = (u - cx) * depth_z / fx
    y = (v - cy) * depth_z / fy
    z = depth_z

    z += depth_z*0.10;
    y -= depth_z*0.15;

    return np.array([x, y, z])
