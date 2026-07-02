import os
import cv2
import json
import torch
import numpy as np
import tempfile
import pycocotools.mask as mask_util
from fastapi import FastAPI, File, UploadFile, Form
from fastapi.responses import JSONResponse
from torchvision.ops import box_convert
from contextlib import asynccontextmanager

# SAM 2 and Grounding DINO imports
from sam2.build_sam import build_sam2
from sam2.sam2_image_predictor import SAM2ImagePredictor
from grounding_dino.groundingdino.util.inference import load_model, load_image, predict

"""
Hyperparameters & Configuration
"""
SAM2_CHECKPOINT = "./checkpoints/sam2.1_hiera_large.pt"
SAM2_MODEL_CONFIG = "configs/sam2.1/sam2.1_hiera_l.yaml"
GROUNDING_DINO_CONFIG = "grounding_dino/groundingdino/config/GroundingDINO_SwinT_OGC.py"
GROUNDING_DINO_CHECKPOINT = "gdino_checkpoints/groundingdino_swint_ogc.pth"
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

# Global variables to hold models
sam2_predictor = None
grounding_model = None

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Load models on startup
    global sam2_predictor, grounding_model
    print("Loading SAM2 Model...")
    sam2_model = build_sam2(SAM2_MODEL_CONFIG, SAM2_CHECKPOINT, device=DEVICE)
    sam2_predictor = SAM2ImagePredictor(sam2_model)
    
    print("Loading Grounding DINO Model...")
    grounding_model = load_model(
        model_config_path=GROUNDING_DINO_CONFIG, 
        model_checkpoint_path=GROUNDING_DINO_CHECKPOINT,
        device=DEVICE
    )
    
    # Enable tf32 for Ampere GPUs if available
    if torch.cuda.is_available() and torch.cuda.get_device_properties(0).major >= 8:
        torch.backends.cuda.matmul.allow_tf32 = True
        torch.backends.cudnn.allow_tf32 = True
        
    print("Models loaded successfully. Server is ready.")
    yield
    # Cleanup on shutdown (optional)
    print("Shutting down server...")

app = FastAPI(lifespan=lifespan)

def single_mask_to_rle(mask):
    rle = mask_util.encode(np.array(mask[:, :, None], order="F", dtype="uint8"))[0]
    rle["counts"] = rle["counts"].decode("utf-8")
    return rle

@app.post("/predict")
async def predict_endpoint(
    file: UploadFile = File(...),
    text_prompt: str = Form(...),
    box_threshold: float = Form(0.35),
    text_threshold: float = Form(0.25),
    multimask_output: bool = Form(False)
):
    """
    Accepts an image and a text prompt, returns bounding boxes and segmentation masks.
    """
    # 1. Save uploaded file to a temporary location
    with tempfile.NamedTemporaryFile(delete=False, suffix=".jpg") as temp_file:
        content = await file.read()
        temp_file.write(content)
        temp_img_path = temp_file.name

    try:
        # 2. Load image for Grounding DINO
        image_source, image = load_image(temp_img_path)
        
        # 3. Predict bounding boxes using Grounding DINO
        boxes, confidences, labels = predict(
            model=grounding_model,
            image=image,
            caption=text_prompt.lower() if text_prompt.endswith('.') else text_prompt.lower() + ".",
            box_threshold=box_threshold,
            text_threshold=text_threshold,
            device=DEVICE
        )

        if len(boxes) == 0:
            return JSONResponse(content={"message": "No objects detected.", "annotations": []})

        # 4. Process the box prompt for SAM 2
        h, w, _ = image_source.shape
        boxes_xywh = boxes * torch.Tensor([w, h, w, h])
        input_boxes = box_convert(boxes=boxes_xywh, in_fmt="cxcywh", out_fmt="xyxy").numpy()

        # 5. Predict Masks using SAM 2
        sam2_predictor.set_image(image_source)
        
        with torch.autocast(device_type=DEVICE, dtype=torch.bfloat16):
            masks, scores, logits = sam2_predictor.predict(
                point_coords=None,
                point_labels=None,
                box=input_boxes,
                multimask_output=multimask_output,
            )

        # 6. Post-process outputs
        if multimask_output:
            best = np.argmax(scores, axis=1)                     
            masks = masks[np.arange(masks.shape[0]), best]       

        if masks.ndim == 4:
            masks = masks.squeeze(1)

        confidences = confidences.numpy().tolist()
        class_names = labels
        mask_rles = [single_mask_to_rle(mask) for mask in masks]
        input_boxes_list = input_boxes.tolist()
        scores_list = scores.tolist()

        # 7. Format the response
        annotations = []
        for class_name, box, mask_rle, score, dino_conf in zip(class_names, input_boxes_list, mask_rles, scores_list, confidences):
            annotations.append({
                "class_name": class_name,
                "bbox": box, # xyxy format
                "box_confidence": dino_conf,
                "segmentation_rle": mask_rle,
                "mask_score": score
            })

        results = {
            "text_prompt": text_prompt,
            "box_format": "xyxy",
            "img_width": w,
            "img_height": h,
            "annotations": annotations
        }

        return JSONResponse(content=results)

    finally:
        # Ensure the temporary image file is deleted after processing
        if os.path.exists(temp_img_path):
            os.remove(temp_img_path)

if __name__ == "__main__":
    import uvicorn
    # Run the server on port 8000
    uvicorn.run("server:app", host="0.0.0.0", port=8000, reload=False)