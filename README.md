# 3D Task Assistance on Meta Quest using DepthAPI

This is a simple mixed-reality assistant that places animated 3D overlays directly onto real-world objects in your environment. You speak or type a command ("How to clean my room", "where are the books"), and the system breaks the task into steps, identifies the relevant objects in the scene, and anchors a guidance overlay on each one — aligned in 3D space using the Quest's own depth sensor.

No pre-built maps. No QR codes. No environment scanning sessions. One photo, one depth frame, one API call.




https://github.com/user-attachments/assets/0a0ac61c-eee7-440a-bb50-987082138721


---

## The core insight

Most AR guidance systems run object detection in 2D and overlay flat bounding boxes on a video stream. This falls apart the moment the user moves — a label floating over a chair stays glued to screen-space, not chair-space.

On the other hand, using a full 3D scene reconstruction before placing anything is too slow and too heavy for a comfortable interactive loop. 

Insteak we use a prebuilt functional object detection and image segmentation model, and reproject objects.

Here is what happens, in details:

1. The Quest takes a single RGB photo and also the depth frame from Environment Depth API. 
2. Both frames, along with the exact camera poses and intrinsics for both sensors at the moment of capture, are streamed over WebRTC to a PC.
3. On the PC, GPT-4o-mini (using your own API key, it's needed btw) reads the image and your command, plans the steps, and names the objects to highlight.
4. YOLOE detects those objects in the image. SAM 2 segments them precisely. You can also use Grounding Dino to do both simeltaneously.
5. For each segmented object, the system samples pixels across its mask and uses iterative reprojection to read the true metric depth at each pixel from the depth map. 
6. The mask's centroid is unprojected into 3D camera space and transformed into Unity world coordinates. Based on the view you have of each target object, and the rules you defined, the overlay is decided.
7. Overlays and where they need to be spawned are decided quest-side with the parser you created (based on IParserTool class).
8. Bounding boxes are also sent, for more accurate and customizable orientation.
9. Animated overlays are spawned in the right place, for each step and disappear once you proceed to the next step.


There are corrently 3 types in the project, Hand gestures, arrows and an animated overlay that connects two objects. You can add more by editing guidance_tool and file and adding corresponding parsers in the project.

<img width="800" height="450" alt="ezgif-6ed3a199200c4c09" src="https://github.com/user-attachments/assets/3940bda0-3b73-4153-be94-8bf0f7ee8f11" />


---

## How the depth reprojection works

The Quest 3 has two physically separate cameras: an RGB passthrough camera and an infrared depth sensor. They share no pixels. To find the depth of an RGB pixel you cannot simply index into the depth map, you have to trace a ray from the RGB camera through 3D space into the depth camera's coordinate frame and sample the depth image there.

`reprojection.py` solves this iteratively:

1. Cast a ray from the RGB camera through pixel (u, v) with an initial depth guess Z = 1 m.
2. Compute the 3D point in RGB space: `P = Z * [(u−cx)/fx, (v−cy)/fy, 1]`.
3. Transform P into the depth camera's local frame using the relative rotation and translation derived from both sensors' world poses.
4. Project onto the depth image to get a candidate pixel, read the actual depth there.
5. Back-project that depth-space Z into an RGB-space Z, accounting for the sensor's physical downward tilt.
6. Repeat up to 5 iterations until convergence (< 5 mm change).

`mask_reprojection.py` runs this over up to 200 randomly sampled pixels inside the SAM mask and takes the **median** of the results. 

Then `camera_math.py`, which converts the Unity left-handed quaternion poses into OpenCV right-handed rotation matrices, builds the relative depth-to-RGB transform, and transforms the unprojected camera-space point back into Unity world space so the headset can place an overlay at the correct real-world location.

---

## Prerequisites

**Meta Quest 3** (or Quest 3S)
- Unity 2022.3 LTS or later
- [Unity WebRTC](https://github.com/Unity-Technologies/com.unity.webrtc)
- [NativeWebSocket](https://github.com/endel/NativeWebSocket)
- Meta XR SDK (Passthrough Camera Access + Environment Depth Manager)
- [Whisper Unity](https://github.com/Macoron/whisper.unity) (for on-device voice), the native meta TTS and Speech to text is also implemented.
- TextMeshPro

**PC**
- Node.js ≥ 18 (signaling server)
- Python ≥ 3.10
- An NVIDIA GPU is strongly recommended for SAM 2 inference

**Python packages**
```bash
pip install flask openai ultralytics torch torchvision
# SAM 2 — install from Meta's repo:
You should use https://github.com/facebookresearch/segment-anything-2.git. Follow instructions there and add the server file in PCserver of this repo to communicate.
# Grounding_Sam2
use https://github.com/IDEA-Research/Grounded-SAM-2 and add the grounding sam server script.
```

**API key**
Don't forget to add this to your environemental variables before running the PC server (AI server).
You also need to add your custom api key to use meta's native TTS and Speech to text. Follow this video for more information:
https://www.youtube.com/watch?v=61VAC6oQHTQ&t=1394s
```bash
export OPENAI_API_KEY="sk-..."
```

---

## Setup

### 1. Signaling server

```bash
npm install ws
node server.js
# Listens on ws://0.0.0.0:3000
```

Both the Quest and the PC must be on the same LAN (or the server must be reachable from both). You can get your PC's LAN address with ipconfig on cmd.

### 2. Python detection server

```bash
cd python
python app.py
# Listens on http://127.0.0.1:5000
```

```bash
DEBUG_MASKS=1 python app.py
```

Note:
If your Python server runs inside WSL, set `serverIsOnWSL = true` on `PCScreenshotReceiver` in the Unity Inspector. The script will automatically rewrite `C:\QuestSnapshots\...` paths to `/mnt/c/QuestSnapshots/...` before sending them to Flask.

### 3. Unity (Quest side)

- Open the Quest scene.
- On `QuestPassthroughSender`, set `signalingUrl` to `ws://<YOUR_SERVER_LAN_IP>:3000`.
- Assign `EnvironmentDepthManager`, `OVRCameraRig`, `PassthroughCameraAccess`, the `DepthToFloat` material, and the `commandInputField`.
- Add the api keys for speech to text and text to speech to the building blocks.
- Build and deploy to the Quest.

### 4. Unity (PC side)

- Open the PC scene.
- On `PCScreenshotReceiver`, set `signalingUrl` to `ws://<YOUR_SERVER_LAN_IP>:3000` and `detectionServerUrl` to `http://127.0.0.1:5000/process`.
- Set `saveFolder` to a path where RGB, depth, and metadata files will be written (e.g. `C:/QuestSnapshots`).
- Enter Play mode.

### 5. Connect

Start the signaling server → start Play mode on PC → put on the Quest. The two peers register automatically and the DataChannel opens within a few seconds. You'll see `[Quest] DataChannel OPEN` in the Quest's CanvasLogger.
Don't forget to ensure both devices are accessible to each other. You may need to disable your firewall.

To change the signaling address at runtime without rebuilding, use the `SignalingAddressChanger` UI panel.

---

## Usage

**Typed command**  
Type a command into the input field on the Quest's floating canvas (the `KeyboardHandler` opens the Quest overlay keyboard on tap) then press **A** to capture.

**Voice command**  
Hold the **left index trigger** to record. Release to transcribe on-device via Whisper. The recognised text populates the input field automatically; press **A** to capture.

**Navigating steps**  
Use the **Next / Previous** buttons on the instruction panel to step through the plan. The arrow for each step appears in the correct location in your real environment; arrows for other steps are hidden.

**Dismissing an overlay**  
Pinch or poke any arrow or hand gesture to disintegrate it with a particle effect. 

**Debug bounding boxes**  
Toggle the debug button to show the 3D bounding box wireframe around each detected object (rendered as cyan LineRenderers in Unity world space).

---

## License
MIT
