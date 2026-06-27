"""
app.py

Receives a capture (RGB image + depth map + metadata + user command) from the
Quest, runs the vision pipeline, reprojects detections to world space, and
returns a JSON payload that QuestInstructionReceiver.cs can deserialize directly.

Output schema (matches AROverlay / InstructionResponse in QuestInstructionReceiver.cs):
{
  "id": str,
  "ar_overlays": [
    {
      "step":            int,
      "instruction":     str,
      "guidance_tool":   str,
      "manipulation_tag": str,
      "tool_settings": [{ "key": str, "value": str }, ...],
      "worldX": float,
      "worldY": float,
      "worldZ": float,
      "bboxCorners": [{ "x": float, "y": float, "z": float }, ...]
    },
    ...
  ]
}

/retry route
  The Quest sends periodic retries (configurable interval on device) for a
  capture_id that was previously processed. Each retry supplies a new RGB image
  + depth map + meta so the server can attempt to detect objects that were
  missed in the original /process call.

  The response has the same schema as /process but contains ONLY overlays for
  steps that have newly detected objects. The Quest merges them into the
  existing overlay set rather than replacing everything.

  If nothing new is found the server returns {"id": ..., "ar_overlays": []}.
  If the retry session is exhausted (all objects found) the response includes
  "retry_complete": true so the Quest stops sending retries.
"""

import json
import traceback
import cv2
import os
import base64
import tempfile
import numpy as np
from flask import Flask, request, jsonify

from vision_pipeline import fetch_step_segmentations, retry_detections, get_retry_session
from mask_reprojection import resolve_mask_world_point

app = Flask(__name__)

DEBUG_MASKS = os.environ.get("DEBUG_MASKS", "1") == "1"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _load_metadata(meta_path: str) -> dict:
    with open(meta_path, "r") as f:
        return json.load(f)


def _load_depth_map(depth_path: str, meta: dict) -> np.ndarray:
    width  = meta.get("depthWidth",  0)
    height = meta.get("depthHeight", 0)

    if width <= 0 or height <= 0:
        raise ValueError("Missing depth dimensions in metadata.")

    raw         = np.fromfile(depth_path, dtype=np.float32).reshape(height, width)
    clean_depth = np.nan_to_num(raw, nan=0.0, posinf=0.0, neginf=0.0)

    near = meta.get("depthNearZ", 0.1)
    far  = meta.get("depthFarZ",  20.0)

    if "depthNearZ" not in meta:
        print("[server] WARNING: depthNearZ not in metadata, using fallback 0.1")
    if "depthFarZ" not in meta:
        print("[server] WARNING: depthFarZ not in metadata, using fallback 20.0")

    if np.isinf(far) or far > 10000.0:
        return np.where(clean_depth > 0.0001, near / clean_depth, 0.0)

    return np.where(
        clean_depth > 0.0001,
        (near * far) / (near + clean_depth * (far - near)),
        0.0,
    )


def _settings_dict_to_kv(settings: dict) -> list[dict]:
    """Converts {"placement_rule": "up"} → [{"key": "placement_rule", "value": "up"}]"""
    return [{"key": k, "value": str(v)} for k, v in settings.items()]


def _text_only_overlay(step_number: int, instruction: str) -> dict:
    """
    Bare overlay carrying only instruction text.
    Quest receiver skips AR spawning but shows it in the UI panel.
    """
    return {
        "step":             step_number,
        "instruction":      instruction,
        "guidance_tool":    "",
        "manipulation_tag": "",
        "tool_settings":    [],
        "worldX":           0.0,
        "worldY":           0.0,
        "worldZ":           0.0,
        "bboxCorners":      [],
    }


def _build_overlays(step_results: list, depth_map: np.ndarray, meta: dict) -> tuple[list, int]:
    """
    Shared reprojection logic used by both /process and /retry.

    Takes raw step_results from the vision pipeline, reprojects each
    detection's mask to world space, and returns (ar_overlays, skipped_count).
    """
    ar_overlays: list[dict] = []
    skipped = 0
    
    for step in step_results:
        step_number = step["step_number"]
        instruction = step["instruction"]

        if not step["detections"]:
            ar_overlays.append(_text_only_overlay(step_number, instruction))
            continue

        emitted_any = False

        for detection in step["detections"]:
            guidance_tool = detection["guidance_tool"]
            tool_settings = detection["tool_settings"]
            label         = detection["label"]
            mask          = detection.get("mask")
            bbox          = detection.get("bbox", [])

            # Floating overlay — no world anchor needed
            if not label or mask is None:
                ar_overlays.append({
                    "step":             step_number,
                    "instruction":      instruction,
                    "guidance_tool":    guidance_tool,
                    "manipulation_tag": label,
                    "tool_settings":    _settings_dict_to_kv(tool_settings),
                    "worldX":           0.0,
                    "worldY":           0.0,
                    "worldZ":           0.0,
                    "bboxCorners":      [],
                })
                emitted_any = True
                continue

            world_point, bbox_corners = resolve_mask_world_point(mask, depth_map, meta, bbox)
            if world_point is None:
                skipped += 1
                continue

            formatted_corners = [
                {"x": float(pt[0]), "y": float(pt[1]), "z": float(pt[2])}
                for pt in bbox_corners
            ]

            ar_overlays.append({
                "step":             step_number,
                "instruction":      instruction,
                "guidance_tool":    guidance_tool,
                "manipulation_tag": label,
                "tool_settings":    _settings_dict_to_kv(tool_settings),
                "worldX":           float(world_point[0]),
                "worldY":           float(world_point[1]),
                "worldZ":           float(world_point[2]),
                "bboxCorners":      formatted_corners,
            })
            emitted_any = True

        if not emitted_any:
            ar_overlays.append(_text_only_overlay(step_number, instruction))

    return ar_overlays, skipped


def save_debug_masks(rgb_path: str, step_results: list, capture_id: str) -> None:
    img = cv2.imread(rgb_path)
    if img is None:
        print(f"[debug] Could not load image at {rgb_path}")
        return

    canvas = img.copy()
    for step in step_results:
        for det in step["detections"]:
            if det.get("mask") is None:
                continue
            color = np.random.randint(100, 255, (3,), dtype=np.uint8).tolist()
            canvas[det["mask"]] = color

    cv2.addWeighted(canvas, 0.5, img, 0.5, 0, img)

    for step in step_results:
        for det in step["detections"]:
            if not det.get("bbox"):
                continue
            x1, y1, x2, y2 = map(int, det["bbox"])
            label   = det.get("label", "?")
            tool    = det.get("guidance_tool", "?")
            caption = f"Step {step['step_number']}: {label} [{tool}]"
            cv2.rectangle(img, (x1, y1), (x2, y2), (0, 255, 0), 2)
            cv2.putText(img, caption, (x1, max(y1 - 10, 10)),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)

    save_path = os.path.join(os.path.dirname(rgb_path), f"DebugMasks_{capture_id}.jpg")
    cv2.imwrite(save_path, img)
    print(f"[debug] Masks saved: {save_path}")


# ---------------------------------------------------------------------------
# /process  — original capture route
# ---------------------------------------------------------------------------

@app.route("/process", methods=["POST"])
def process():
    body = request.get_json(force=True)

    required_fields = ["id", "rgbPath", "depthPath", "metaPath", "command"]
    missing = [f for f in required_fields if f not in body or not body[f]]
    if missing:
        return jsonify({"error": f"Missing required fields: {missing}"}), 400

    capture_id = body["id"]
    rgb_path   = body["rgbPath"]
    depth_path = body["depthPath"]
    meta_path  = body["metaPath"]
    command    = body["command"]
    use_yoloe  = body.get("useYoloe", False)
    intent     = body.get("intent", "").strip().lower()

    detector = "yoloe" if use_yoloe else "gdino_server"

    try:
        meta = _load_metadata(meta_path)
    except Exception as e:
        return jsonify({"error": f"Failed to load metadata: {e}"}), 400

    try:
        depth_map = _load_depth_map(depth_path, meta)
    except Exception as e:
        return jsonify({"error": f"Failed to load depth map: {e}"}), 400

    try:
        step_results = fetch_step_segmentations(
            command, rgb_path,
            capture_id=capture_id,
            intent=intent,
            detector=detector,
            server_url="http://localhost:8000/predict",
        )
        if DEBUG_MASKS:
            save_debug_masks(rgb_path, step_results, capture_id)
    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": f"Vision pipeline failed: {e}"}), 500

    ar_overlays, skipped = _build_overlays(step_results, depth_map, meta)

    print(f"[server] '{capture_id}': {len(ar_overlays)} overlays built, "
          f"{skipped} detections skipped (no valid depth).")

    return jsonify({"id": capture_id, "ar_overlays": ar_overlays})


# ---------------------------------------------------------------------------
# /retry  — re-detection route for missed objects
# ---------------------------------------------------------------------------

@app.route("/retry", methods=["POST"])
def retry():
    """
    Called by the Quest on a configurable interval after /process.

    Expected body:
    {
      "id":        str,          # same capture_id as the original /process call
      "rgbPath":   str,          # path to the NEW image on disk (written by PC receiver)
      "depthPath": str,          # path to the NEW depth map on disk
      "metaPath":  str           # path to the NEW metadata JSON on disk
    }

    The new image/depth/meta are from a fresh capture the Quest took while
    waiting — same format as /process, just without a command (we already
    have the plan from the original call).

    Response:
    {
      "id":             str,
      "ar_overlays":    [...],   # only newly found overlays; empty if nothing new
      "retry_complete": bool     # true when no objects remain in the retry queue
    }
    """
    body = request.get_json(force=True)

    required_fields = ["id", "rgbPath", "depthPath", "metaPath"]
    missing = [f for f in required_fields if f not in body or not body[f]]
    if missing:
        return jsonify({"error": f"Missing required fields: {missing}"}), 400

    capture_id = body["id"]
    rgb_path   = body["rgbPath"]
    depth_path = body["depthPath"]
    meta_path  = body["metaPath"]

    # Make sure we have a session for this capture
    session = get_retry_session(capture_id)
    if session is None:
        return jsonify({
            "error": f"No retry session for id='{capture_id}'. Call /process first."
        }), 404

    # Short-circuit: nothing left to find
    if not session.missed:
        return jsonify({
            "id":             capture_id,
            "ar_overlays":    [],
            "retry_complete": True,
        })

    try:
        meta = _load_metadata(meta_path)
    except Exception as e:
        return jsonify({"error": f"Failed to load metadata: {e}"}), 400

    try:
        depth_map = _load_depth_map(depth_path, meta)
    except Exception as e:
        return jsonify({"error": f"Failed to load depth map: {e}"}), 400

    try:
        step_results = retry_detections(capture_id, rgb_path)
    except KeyError as e:
        return jsonify({"error": str(e)}), 404
    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": f"Retry detection failed: {e}"}), 500

    # Reproject whatever was newly found
    ar_overlays, skipped = _build_overlays(step_results, depth_map, meta)

    if DEBUG_MASKS and step_results:
        save_debug_masks(rgb_path, step_results, f"{capture_id}_retry")

    # Check if the queue is now empty
    retry_complete = len(session.missed) == 0

    print(f"[server/retry] '{capture_id}': {len(ar_overlays)} new overlays, "
          f"{skipped} skipped, complete={retry_complete}.")

    return jsonify({
        "id":             capture_id,
        "ar_overlays":    ar_overlays,
        "retry_complete": retry_complete,
    })


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    print("Starting AR guidance server on http://127.0.0.1:5000")
    print("Ensure OPENAI_API_KEY is set in your environment.")
    print(f"Debug masks: {'ON' if DEBUG_MASKS else 'OFF'} (set DEBUG_MASKS=0 to disable)")
    app.run(host="127.0.0.1", port=5000, debug=False)