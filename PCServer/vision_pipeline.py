"""
vision_pipeline.py

Two-call GPT architecture:

  CALL 1 — Planner (_fetch_step_plan)
    Receives: image + user request.
    Returns:  SemanticPlan — steps with instructions and per-object tags.
    Each object carries:
      - tag:       rich GDINO-friendly phrase for detection
      - user_view: how the object appears in the captured image (visual POV)
    The planner owns everything visual: scene description, object naming,
    spatial orientation. It has the image, so it can answer these accurately.

  CALL 2 — Annotator (_fetch_object_annotations)
    Receives: plan text only — NO image.
    Returns:  ObjectAnnotationPlan — simple_noun + action per object per step.
    The annotator owns everything semantic: what the user does to each object.
    No image needed — the action is inferable from the instruction verb alone.
    No tool/gesture decisions here — those are made by our Python code.

  TOOL SELECTION — _select_tool()
    Pure Python. Uses action + user_view → guidance_tool + gesture + placement_rule.
    Deterministic, no GPT, easy to tune.

RETRY SYSTEM
  After a /process call, any object that failed detection is stored in a
  per-capture RetrySession. The /retry route in app.py passes a new image
  and the capture_id; retry_detections() runs detection only on the missing
  objects, then returns structured results that app.py can reproject and
  relay to the Quest — exactly the same overlay shape as the original call.

  RetrySession stores per-object:
    tag        — rich GDINO phrase (same string used in original detection)
    simple_noun — bare noun for YOLOE class list
    user_view  — from Call 1 (needed for tool_settings placement_rule)
    action     — from Call 2 (needed for _select_tool, task path only)
    guidance_tool / tool_settings — pre-computed at original call time,
                   stored so retry doesn't need to re-run tool selection
    step_number / instruction — for building the overlay shape
"""

import json
import base64
import os
import threading
import requests
import pycocotools.mask as mask_util
from dataclasses import dataclass, field
from typing import Dict, List, Literal, Optional

from pydantic import BaseModel, Field
from openai import OpenAI
from ultralytics import YOLOE, SAM

client = OpenAI(api_key=os.environ.get("OPENAI_API_KEY"))

_model_lock  = threading.Lock()
_yoloe_model = None
_sam_model   = None


def _get_models():
    global _yoloe_model, _sam_model
    with _model_lock:
        if _yoloe_model is None:
            print("[vision_pipeline] Loading YOLOE (first request only)...")
            _yoloe_model = YOLOE("yoloe-v8l-seg.pt")
        if _sam_model is None:
            print("[vision_pipeline] Loading SAM 2 (first request only)...")
            _sam_model = SAM("sam2.1_s.pt")
    return _yoloe_model, _sam_model


# ---------------------------------------------------------------------------
# Retry session store
# ---------------------------------------------------------------------------

@dataclass
class _MissedObject:
    """Everything needed to re-detect one object and build its overlay."""
    step_number:   int
    instruction:   str
    tag:           str        # rich GDINO phrase
    simple_noun:   str        # bare noun for YOLOE
    user_view:     str        # from Call 1
    guidance_tool: str        # pre-computed by _select_tool
    tool_settings: dict       # pre-computed by _select_tool


@dataclass
class RetrySession:
    """
    Holds per-capture retry state across HTTP requests.
    Keyed by capture_id in _retry_sessions.

    missed: list of _MissedObject — objects not detected in the original call.
    detector / server_url: carried forward so /retry doesn't need to re-send them.
    """
    detector:   str
    server_url: str
    missed:     List[_MissedObject] = field(default_factory=list)


# Module-level store: capture_id → RetrySession
# A new /process call for the same id overwrites the old session.
_retry_sessions: Dict[str, RetrySession] = {}
_retry_lock = threading.Lock()


def get_retry_session(capture_id: str) -> Optional[RetrySession]:
    with _retry_lock:
        return _retry_sessions.get(capture_id)


def _new_retry_session(capture_id: str, detector: str, server_url: str) -> RetrySession:
    session = RetrySession(detector=detector, server_url=server_url)
    with _retry_lock:
        _retry_sessions[capture_id] = session
    return session


def _record_missed(session: RetrySession, step_number: int, instruction: str,
                   tag: str, simple_noun: str, user_view: str,
                   guidance_tool: str, tool_settings: dict) -> None:
    with _retry_lock:
        session.missed.append(_MissedObject(
            step_number=step_number,
            instruction=instruction,
            tag=tag,
            simple_noun=simple_noun,
            user_view=user_view,
            guidance_tool=guidance_tool,
            tool_settings=tool_settings,
        ))


# ---------------------------------------------------------------------------
# Shared enums
# ---------------------------------------------------------------------------

ACTION_ENUM = Literal[
    "press",    
    "pick_up",  
    "wipe",     
    "open",     
    "close",    
    "move_item",  
    "move_dest",  
    "chop",     
    "rotate",   # NEW: turning, twisting, spinning
    "other",    
]
VIEW_ENUM = Literal[
    "front",
    "front_left",
    "front_right",
    "front_top",
    "front_bottom",
    "top",
    "bottom",
]


# ---------------------------------------------------------------------------
# CALL 1 — Planner schema
# ---------------------------------------------------------------------------

class TaggedObject(BaseModel):
    tag: str = Field(
        description=(
            "A noun phrase describing EXACTLY ONE physical object. "
            "If a step involves multiple objects, each gets its own separate TaggedObject entry — never combine them. "
            "WRONG: 'glass and spoon on the table'  RIGHT: two entries, 'glass with floral pattern' and 'spoon on the table'. "
            "For visible objects: include colour, material, or one spatial anchor. "
            "For objects NOT in the image: use a short generic noun phrase like 'the sponge' or 'dish soap'. "
            "No verbs, no actions, no abstract nouns, no pronouns."
        )
    )
    user_view: VIEW_ENUM = Field(
    description=(
        "The viewing angle of the camera relative to this specific object in the captured image.\n"
        "Think about what surface of the object is most visible to the camera.\n\n"
        "  top          — camera looks almost straight DOWN onto the object. "
                         "You see the top face clearly, little to no front face visible. "
                         "Example: looking down at a floor mat, a book lying flat.\n"
        "  front_top    — camera looks DOWNWARD at an angle. "
                         "You see both the top AND front face of the object. "
                         "THIS IS THE DEFAULT FOR ANY OBJECT RESTING ON A TABLE OR DESK. "
                         "Example: mug on a desk, jar on a counter, bowl on a table, "
                         "keyboard, cutting board, any object photographed from standing height.\n"
        "  front        — camera faces the object's front surface STRAIGHT ON, at eye level. "
                         "You see the front face only — no top visible at all. "
                         "Example: a wall socket, a monitor screen, a cabinet door face, "
                         "a button on an appliance panel.\n"
        "  front_left   — same as front but the camera is slightly to the object's left.\n"
        "  front_right  — same as front but the camera is slightly to the object's right.\n"
        "  front_bottom — camera looks UPWARD at the object. "
                         "Example: underside of a shelf, ceiling light.\n"
        "  bottom       — camera looks almost straight UP at the object. "
                         "Example: ceiling-mounted fixture.\n\n"
        "RULE: If the object is sitting on any horizontal surface (table, counter, floor, desk), "
        "the answer is almost always front_top unless you are standing directly over it looking straight down."
    )
)
 
class SemanticStep(BaseModel):
    instruction: str = Field(
        description=(
            "One clear, concrete, single-action instruction for this step. "
            "Start with a verb. Under 15 words."
        )
    )
    objects: List[TaggedObject] = Field(
        description=(
            "One TaggedObject per physical object the user must interact with in this step. "
            "NEVER leave this empty if the step involves touching, picking up, using, or placing any object. "
            "If the object is not visible in the image, still include it with a generic tag like 'the sponge'. "
            "Only truly object-free steps (e.g. 'Walk to the sink') may have an empty list."
        )
    )


class SemanticPlan(BaseModel):
    intent: str = Field(
        description="'task' for step-by-step procedures, 'query' for locate/identify requests."
    )
    steps: List[SemanticStep] = Field(
        description=(
            "Chronological steps. Each step is one atomic action. "
            "For How to questions: include every step the user needs — don't skip obvious ones. "
            "For a Detection request or Other types of Queries: exactly ONE step whose instruction directly answers the question."
        )
    )


QUERY_SYSTEM_PROMPT = """You are an expert spatial assistant helping a user locate or identify objects in their real environment.
You receive a photo of their space and their spoken request.

Your ONLY job is to produce EXACTLY ONE step that directly answers their question by identifying the requested objects.

OBJECT TAGGING RULES (Optimized for GroundingDINO Detection):
  • Describe the object exactly as it appears in the image.
  • Start with the category: "mug", "cable", "shoe".
  • Add visual attributes: "dark blue mug", "red cable".
  • Use ONE spatial anchor if needed: "the blue mug beside the laptop".
  • NEVER include actions, verbs, pronouns, or full sentences in the tag.
  • Groups are allowed: "the scattered shoes".

USER VIEW RULES — look at each object individually:
  MOST COMMON CASE: Any object resting on a table, desk, counter, or floor
  → use "front_top". This is the default for tabletop scenes.

  Use "top" ONLY when the camera is directly above looking straight down
  (e.g. aerial shot, camera mounted on ceiling, or object photographed from directly overhead).

  Use "front" ONLY when the object's front face is at eye level with no top surface visible
  (e.g. wall socket, monitor screen, cabinet door, appliance panel button).

  Use "front_left" / "front_right" for eye-level objects seen from a slight side angle.

  Use "front_bottom" / "bottom" for objects seen from below (ceiling fixtures, underside of shelf).

  NEVER default to "front" for objects that are sitting on a surface —
  a standing user photographing a desk always sees objects from above, making it front_top.

OUTPUT FORMAT:
  Return exactly ONE step.
  The 'intent' field MUST be "query".
  Provide one TaggedObject for every physical item the user is asking about.
"""
PLANNER_SYSTEM_PROMPT = """You are an expert spatial assistant helping a user in their real environment.
You receive a photo of their space and their spoken request.
Your ONLY job is to write an excellent step-by-step action plan.

PLANNING RULES (STRICTLY ENFORCED):
  • Be concise: Most tasks need 2–5 steps. Only generate steps strictly necessary to complete the goal.
  • No micro-steps: Combine fluid motions. "Pick up the mug" not "Reach for the mug" then "Grab it".
  • No duplicates: Never repeat the same action or goal across steps.
  • Each step = one atomic action. Never combine two unrelated actions.
  • For cleaning tasks: SEPARATE picking up items FROM wiping surfaces.
  • For QUERIES: produce exactly ONE step that directly answers the question from visual evidence in the image.

  RELOCATION RULE — most important:
  If an object is being moved from one place to another, that is ONE step, not two.
  NEVER split a relocation into a "pick up X" step followed by a "place X on Y" or "move X to Y" step.
  The pick-up is implied by the move. Merge them into a single instruction.

  ✗ WRONG — two steps:
      Step 1: Pick up the knife from the table.         objects: [knife]
      Step 2: Move the knife to the cutting board.      objects: [knife, cutting board]

  ✓ RIGHT — one step:
      Step 1: Move the knife to the cutting board.      objects: [knife, cutting board]

  The only exception: if the user must pick something up WITHOUT a known destination
  (e.g. "clear the table" with no specified target location), a bare pick_up step is allowed.

OBJECT TAGGING RULES — READ CAREFULLY:

  RULE 1 — ONE OBJECT PER TAG, ALWAYS.
  Each TaggedObject describes exactly one physical item. If a step involves two objects, create two TaggedObjects.
  ✗ WRONG: tag = "glass and spoon on the table"
  ✓ RIGHT: two entries → tag = "glass with floral pattern", tag = "spoon on the table"

  RULE 2 — TAG EVERY OBJECT THE USER TOUCHES OR ACTS ON IN A STEP.
  This includes BOTH the tool being used AND the surface or object being acted upon.

  For wipe/clean steps: tag BOTH the cleaning tool AND the surface being cleaned.
  ✗ WRONG: "wipe the desk with a cloth"   → objects: [cloth]
  ✓ RIGHT: "wipe the desk with a cloth"   → objects: [desk surface, clean cloth]

  For use/apply steps: tag BOTH the item being applied AND what it's applied to.
  ✗ WRONG: "apply soap to the sponge"     → objects: [soap]
  ✓ RIGHT: "apply soap to the sponge"     → objects: [dish soap, sponge]

  For scrub/chop steps: tag BOTH the tool AND the thing being worked on.
  ✗ WRONG: "scrub the dishes with a sponge" → objects: [sponge]
  ✓ RIGHT: "scrub the dishes with a sponge" → objects: [dishes in the sink, sponge]

  Never leave objects with only the tool — the surface or target object is always included.

  RULE 3 — TAG OUT-OF-VIEW OBJECTS TOO.
  Our system has a retry loop that searches for objects as the user moves around.
  If an object is needed but not visible in the photo, STILL create a TaggedObject for it.
  Use a short generic noun phrase: "the sponge", "dish soap", "clean towel", "the sink tap".
  Set its user_view to "front".

  RULE 4 — DESCRIBE VISIBLE OBJECTS PRECISELY.
  For objects you can see: add colour, material, or one spatial anchor.
  "glass with floral pattern on the left", "yellow sponge near the sink".

  NEVER in a tag:
  ✗ Actions or verbs ("pick up the glass")
  ✗ Two objects in one tag ("glass and spoon")
  ✗ Abstract nouns ("cleanliness", "task")
  ✗ Pronouns ("it", "them")
  ✗ Full sentences

USER VIEW RULES:
  • Visible object lying flat → "top"
  • Visible object facing camera → "front"
  • Visible object seen from slight left → "front_left"
  • Object NOT visible in image → "front"
"""


# ---------------------------------------------------------------------------
# CALL 2 — Annotator schema (no image, text-only)
# ---------------------------------------------------------------------------

class ObjectAnnotation(BaseModel):
    original_tag: str = Field(
        description=(
            "Copy the object tag exactly as written in the plan. "
            "Do not rephrase or shorten it."
        )
    )
    simple_noun: str = Field(
        description=(
            "The absolute bare singular noun for this object — one word, no adjectives. "
            "Used internally for logging and future rule extensions. "
            "Examples: 'mug', 'button', 'shelf', 'drawer', 'cable', 'pillow', 'sock'."
        )
    )
    action: ACTION_ENUM = Field(
        description=(
            "The physical action the user performs on this object in this step.\n"
            "  press    — tap, poke, push, flip; object stays fixed in place\n"
            "  pick_up  — lift, grab, collect; object leaves its surface but the destination isn't in the image\n"
            "  wipe     — scrub, clean, dust, sweep; friction across the surface\n"
        "  move_item — this object is being physically carried or relocated\n"
        "  move_dest — this object is the destination/surface the item is moved onto\n"
        "              Example: 'put the mug on the shelf' → mug=move_item, shelf=move_dest\n"
        "              Example: 'place the book in the bag' → book=move_item, bag=move_dest\n"
        "              If only one object is tagged in a relocation step, use move_item.\n"
            "  open     — open a door, drawer, lid, or container\n"
            "  close    — close a door, drawer, lid, or container\n"
            "  other    — locate, look at, identify; no strong hand gesture"
        )
    )


class AnnotatedStep(BaseModel):
    step_number: int = Field(description="1-based index matching the plan step number.")
    objects: List[ObjectAnnotation] = Field(
        description=(
            "One ObjectAnnotation per object listed in this step. "
            "Empty list if the step has no objects."
        )
    )


class ObjectAnnotationPlan(BaseModel):
    annotated_steps: List[AnnotatedStep] = Field(
        description="One AnnotatedStep per step in the plan, in order."
    )

ANNOTATOR_SYSTEM_PROMPT = """You are an object annotation engine for an AR system.
You receive a step-by-step plan. Each step lists objects the user interacts with.

Your ONLY job for each object:
  1. Copy its tag exactly as written (original_tag) — DO NOT split, rephrase, or shorten it
  2. Extract the bare noun (simple_noun) — one word, the primary object type
  3. Classify the action the user performs on it

CRITICAL: If a tag says "glass with floral pattern and spoon on the table", that is ONE object entry.
Copy it as-is. Do not split it into "glass" and "spoon". The plan is your source of truth.
The number of ObjectAnnotations you produce MUST exactly match the number of object tags in the plan.

You make NO decisions about AR tools, prefabs, arrows, or gestures.
You do NOT rewrite instructions. You do NOT add or remove steps or objects.
ACTION RULES — pick the single best match:
  press      → user pokes, taps, pushes, or flips; object stays in place
  pick_up    → user lifts, grabs, takes, or collects; object leaves the surface
  wipe       → user rubs, scrubs, dusts, or sweeps; friction across the surface
  open       → user opens it (door, drawer, lid)
  close      → user closes it (door, drawer, lid)
  move_item  → the specific object being moved from one place to another
  move_dest  → the destination, receptacle, or surface where the item needs to be placed
  chop       → user cuts, slices, dices, minces, or carves an object
  rotate     → user turns, twists, spins, or revolves an object (knobs, dials, caps, keys)
  other      → user locates, looks at, or identifies; no strong hand gesture


  MOVE RULES — these two always appear as a pair in the same step:
  move_item → this object IS the thing being physically carried or relocated
  move_dest → this object IS the destination, surface, or container the item is moved to

  How to tell them apart:
    The instruction verb acts ON move_item and the preposition (to/onto/into/toward) points to move_dest.
    "Move the knife to the cutting board" → knife=move_item, cutting board=move_dest
    "Put the mug on the shelf"            → mug=move_item,  shelf=move_dest
    "Slide the book into the bag"         → book=move_item,  bag=move_dest
  
  NEVER assign move_item to the destination or move_dest to the thing being carried.
  If only one object is tagged in a move step, use move_item for it.

   wipe → the SURFACE being cleaned receives this action, not the cloth or tool doing the wiping.
         The cleaning tool (cloth, sponge, brush) gets pick_up if it needs to be grabbed first,
         or other if it's already in hand.
         Example: "wipe the desk with a cloth" → desk surface=wipe, cloth=pick_up
  
SIMPLE NOUN RULES:
  ✓ One bare singular noun: "mug", "button", "sponge", "towel"
  ✓ For combined tags like "glass with floral pattern", simple_noun = "glass"
  ✗ No adjectives, colours, or locations
"""

# ---------------------------------------------------------------------------
# TOOL SELECTION — pure Python, no GPT
# ---------------------------------------------------------------------------


_GHOST_HAND_ACTIONS = {"press", "pick_up", "wipe", "open", "close", "rotate"}

_ACTION_TO_GESTURE = {
    "press":   "poke",
    "open":    "grab",
    "close":   "poke",
    "pick_up": "grab",
    "wipe":    "clean",
    "clean":   "clean",
    "rotate":  "rotate", # NEW
}
ACTION_ALIASES = {
    # press
    "press": "press", "push": "press", "tap": "press", "poke": "press",
    "click": "press", "flip": "press", "toggle": "press", "switch": "press",
    "activate": "press", "turn_on": "press", "turn_off": "press",
    # pick_up
    "pick_up": "pick_up", "grab": "pick_up", "take": "pick_up",
    "lift": "pick_up", "collect": "pick_up", "retrieve": "pick_up",
    "carry": "pick_up", "hold": "pick_up", "remove": "pick_up",
    # move (relocation verbs all become move_item at the alias level)
    "move": "move_item", "put": "move_item", "place": "move_item",
    "set": "move_item", "drop": "move_item", "return": "move_item",
    "store": "move_item", "transfer": "move_item", "bring": "move_item",
    "slide": "move_item", "reposition": "move_item", "relocate": "move_item",
    "shift": "move_item", "arrange": "move_item", "stack": "move_item",
    # wipe
    "wipe": "wipe", "clean": "wipe", "scrub": "wipe", "dust": "wipe",
    "polish": "wipe", "sweep": "wipe", "sanitize": "wipe",
    # open / close
    "open": "open", "uncover": "open", "unlock": "open",
    "close": "close", "shut": "close", "lock": "close",
    # chop
    "chop": "chop", "cut": "chop", "slice": "chop", "dice": "chop",
    "mince": "chop", "carve": "chop", "split": "chop", "cleave": "chop",
    # rotate
    "rotate": "rotate", "turn": "rotate", "twist": "rotate", "spin": "rotate",
    "revolve": "rotate", "screw": "rotate", "unscrew": "rotate", "crank": "rotate",
    # other
    "find": "other", "locate": "other", "identify": "other", "look": "other",
    "inspect": "other", "check": "other", "observe": "other",
    "organize": "other", "sort": "other",
}

_VIEW_TO_PLACEMENT = {
    "front":        "front",
    "front_left":   "front",
    "front_right":  "front",
    "front_top":    "up",
    "front_bottom": "front",
    "top":          "up",
    "bottom":       "front",
    "left":         "front",
    "right":        "front",
}


def normalize_action(action: str) -> str:
    action = action.lower().replace(" ", "_")
    return ACTION_ALIASES.get(action, "other")

# only for testing purposes
FORCE_TOOL  = ""

def _select_tool(action: str, user_view: str) -> dict:
    # Tool-specific actions always win regardless of view
    if FORCE_TOOL == "move":
    # Preserve the actual role from the action instead of hardcoding source
        if action == "move_dest":
            return {"guidance_tool": "move", "tool_settings": {"role": "target", "placement_rule": "up"}}
        else:
            return {"guidance_tool": "move", "tool_settings": {"role": "source", "placement_rule": "up"}}

    if FORCE_TOOL!= "":
        _forced_settings = {
            "chop_line":      {"placement_rule": "up"},
            "move":           {"role": "source", "placement_rule": "up"},
            "ghost_hand":     {"gesture": "grab", "placement_rule": "up"},
            "indicator_arrow": {"placement_rule": "front"},
        }
        return {
            "guidance_tool": FORCE_TOOL,
            "tool_settings": _forced_settings.get(FORCE_TOOL, {}),
        }
    

    if action == "chop":
        return {"guidance_tool": "chop_line", "tool_settings": {"placement_rule": "up"}}

    if action == "move_item":
        return {
            "guidance_tool": "move",
            "tool_settings": {"role": "source", "placement_rule": _VIEW_TO_PLACEMENT.get(user_view, "up")},
        }
    if action == "move_dest":
        return {
            "guidance_tool": "move",
            "tool_settings": {"role": "target", "placement_rule": _VIEW_TO_PLACEMENT.get(user_view, "up")},
        }
            

    # Ghost hand actions
    if action in _GHOST_HAND_ACTIONS:
        gesture = _ACTION_TO_GESTURE.get(action, "poke")
        if gesture == "poke":
            return {"guidance_tool": "ghost_hand", "tool_settings": {"gesture": "poke", "placement_rule": "front"}}
        if gesture == "clean":
            return {"guidance_tool": "ghost_hand", "tool_settings": {"gesture": "clean", "placement_rule": "up"}}
        if gesture == "rotate":
            placement = _VIEW_TO_PLACEMENT.get(user_view, "up")
            return {"guidance_tool": "ghost_hand", "tool_settings": {"gesture": "rotate", "placement_rule": placement}}
        # grab
        placement = _VIEW_TO_PLACEMENT.get(user_view, "up")
        return {"guidance_tool": "ghost_hand", "tool_settings": {"gesture": gesture, "placement_rule": placement}}

        # For all remaining actions: front-facing objects always get indicator_arrow
    if _VIEW_TO_PLACEMENT.get(user_view, "front") == "front":
        return {"guidance_tool": "indicator_arrow", "tool_settings": {"placement_rule": "front"}}
    # Fallback
    placement = _VIEW_TO_PLACEMENT.get(user_view, "up")
    return {"guidance_tool": "indicator_arrow", "tool_settings": {"placement_rule": placement}}

# ---------------------------------------------------------------------------
# GPT helpers
# ---------------------------------------------------------------------------

def _encode_image(image_path: str) -> str:
    with open(image_path, "rb") as f:
        return base64.b64encode(f.read()).decode("utf-8")


def _fetch_step_plan(user_prompt: str, image_path: str, pinned_intent: str = "") -> SemanticPlan:
    """Call 1: Planner sees the image. Returns SemanticPlan with tagged objects."""
    base64_image = _encode_image(image_path)

    active_system_prompt = (
        QUERY_SYSTEM_PROMPT if pinned_intent == "query" else PLANNER_SYSTEM_PROMPT
    )

    response = client.beta.chat.completions.parse(
        model="gpt-4o",
        messages=[
            {"role": "system", "content": active_system_prompt},
            {
                "role": "user",
                "content": [
                    {"type": "text",      "text": f"User request: {user_prompt}"},
                    {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{base64_image}"}},
                ],
            },
        ],
        response_format=SemanticPlan,
        temperature=0.2,
    )

    plan = response.choices[0].message.parsed
    print(f"[vision_pipeline] Planner: intent='{plan.intent}', steps={len(plan.steps)}")
    for i, s in enumerate(plan.steps, 1):
        tags = [(o.tag, o.user_view) for o in s.objects]
        print(f"  Step {i}: {s.instruction} | objects: {tags}")
    return plan


def _fetch_object_annotations(plan: SemanticPlan) -> ObjectAnnotationPlan:
    """
    Call 2: Annotator receives plan text only (no image).
    Adds simple_noun + action per object. No tool decisions.
    """
    plan_lines = [f"intent: {plan.intent}", ""]
    for i, step in enumerate(plan.steps, 1):
        plan_lines.append(f"Step {i}: {step.instruction}")
        if step.objects:
            for obj in step.objects:
                plan_lines.append(f"  - {obj.tag}")
        else:
            plan_lines.append("  (no objects)")
        plan_lines.append("")
    plan_text = "\n".join(plan_lines)

    response = client.beta.chat.completions.parse(
        model="gpt-4o",
        messages=[
            {"role": "system", "content": ANNOTATOR_SYSTEM_PROMPT},
            {"role": "user",   "content": f"Plan to annotate:\n\n{plan_text}"},
        ],
        response_format=ObjectAnnotationPlan,
        temperature=0.0,
    )

    result = response.choices[0].message.parsed
    print(f"[vision_pipeline] Annotator: {len(result.annotated_steps)} steps annotated.")
    for s in result.annotated_steps:
        for obj in s.objects:
            print(f"  Step {s.step_number} | noun='{obj.simple_noun}' action='{obj.action}'")
    return result


# ---------------------------------------------------------------------------
# Detection helper — shared by task path, query path, and retry
# ---------------------------------------------------------------------------

def make_object_map(
    unique_tags: list[str],
    image_path: str,
    server_url: str,
    detector: str,
    tag_to_noun: dict[str, str] | None = None,  # required for YOLOE; ignored for GDINO
) -> dict[str, list]:
    """
    Runs detection for all unique_tags against image_path.
    Returns {tag: [{"bbox": [...], "mask": np.ndarray}, ...]} for every tag.

    tag_to_noun maps rich tag → bare noun for YOLOE class list construction.
    If None is passed for a YOLOE run the rich tag itself is used as the class
    name, which works only when the tag is already a short noun (query path).
    """
    detected_objects_map: dict[str, list] = {t: [] for t in unique_tags}

    if not unique_tags:
        return detected_objects_map

    if detector == "gdino_server":
        text_prompt = " . ".join(unique_tags)
        try:
            with open(image_path, "rb") as f:
                resp = requests.post(
                    server_url,
                    files={"file": f},
                    data={"text_prompt": text_prompt, "multimask_output": False},
                )
            if resp.status_code == 200:
                for ann in resp.json().get("annotations", []):
                    matched = _match_dino_class(ann["class_name"].lower(), unique_tags)
                    if not matched:
                        print(f"[vision_pipeline] GDINO '{ann['class_name']}' matched no tag — skipped.")
                        continue
                    rle = ann["segmentation_rle"]
                    rle["counts"] = rle["counts"].encode("utf-8")
                    mask = mask_util.decode(rle).astype(bool)
                    detected_objects_map[matched].append({"bbox": ann["bbox"], "mask": mask})
            else:
                print(f"[vision_pipeline] GDINO server error {resp.status_code}: {resp.text}")
        except requests.exceptions.RequestException as e:
            print(f"[vision_pipeline] GDINO server unreachable: {e}")

    elif detector == "yoloe":
        yolo, sam = _get_models()
        # YOLOE needs simple nouns as class names, not rich GDINO phrases.
        # tag_to_noun provides the mapping; fall back to the tag itself for
        # query-path calls where nouns weren't produced by the Annotator.
        noun_for = tag_to_noun or {}
        unique_nouns = list(dict.fromkeys(noun_for.get(t, t) for t in unique_tags))

        try:
            yolo.set_classes(unique_nouns, yolo.get_text_pe(unique_nouns))
        except AttributeError:
            yolo.set_classes(unique_nouns)

        yolo_results = yolo.predict(image_path, verbose=False)
        all_bboxes, all_refs = [], []
        for box in yolo_results[0].boxes:
            noun = unique_nouns[int(box.cls[0])]
            bbox = box.xyxy[0].tolist()
            all_bboxes.append(bbox)
            all_refs.append((noun, bbox))

        if all_bboxes:
            sam_results = sam(image_path, bboxes=all_bboxes, verbose=False)
            masks = (
                sam_results[0].masks.data.cpu().numpy()
                if sam_results[0].masks is not None
                else [None] * len(all_bboxes)
            )
            for (noun, bbox), mask in zip(all_refs, masks):
                # Map noun back to all tags that share this noun
                for tag in unique_tags:
                    if noun_for.get(tag, tag) == noun:
                        detected_objects_map[tag].append({
                            "bbox": bbox,
                            "mask": (mask > 0.5) if mask is not None else None,
                        })

    return detected_objects_map


# ---------------------------------------------------------------------------
# Tag matching (GDINO class_name → our tag)
# ---------------------------------------------------------------------------

def _match_tag(phrase: str, unique_tags: list[str]) -> str | None:
    """Word-overlap matching with tiebreak by shorter tag (more specific)."""
    phrase_words = set(phrase.lower().split())
    best_tag, best_score = None, 0
    for tag in unique_tags:
        tag_words = set(tag.lower().split())
        overlap   = len(tag_words & phrase_words)
        if overlap > best_score or (
            overlap == best_score and best_tag and len(tag) < len(best_tag)
        ):
            best_score = overlap
            best_tag   = tag
    return best_tag if best_score > 0 else None


def _match_dino_class(dino_class: str, unique_tags: list[str]) -> str | None:
    """
    Matches DINO's output back to our rich tag.
    Prevents "anchor bleed" by ignoring detections that only appear
    AFTER spatial prepositions in the rich tag.
    """
    dino_class  = dino_class.lower().strip()
    prepositions = [" near ", " on ", " next to ", " beside ", " under ",
                    " in ", " by ", " above ", " with "]

    best_tag, best_score = None, -1

    for rich_tag in unique_tags:
        rich_tag_lower = rich_tag.lower()

        # Isolate the "target" half (before the first preposition)
        target_phrase = rich_tag_lower
        for prep in prepositions:
            if prep in target_phrase:
                target_phrase = target_phrase.split(prep)[0]

        if dino_class not in target_phrase:
            continue

        dino_words = set(dino_class.split())
        rich_words = set(rich_tag_lower.split())
        overlap    = len(rich_words & dino_words)

        if overlap > best_score:
            best_score = overlap
            best_tag   = rich_tag

    return best_tag


# ---------------------------------------------------------------------------
# Main pipeline entry point
# ---------------------------------------------------------------------------

def fetch_step_segmentations(
    user_prompt: str,
    image_path: str,
    capture_id: str = "",           # needed to register the retry session
    intent: str = "",               # "task" | "query" | "" (auto)
    detector: Literal["yoloe", "gdino_server"] = "gdino_server",
    server_url: str = "http://localhost:8000/predict",
) -> list:
    """
    Runs Planner → Annotator → Tool Selection → Detection → Results.
    Also initialises a RetrySession for capture_id so /retry can pick up
    any objects that weren't found this time.

    Return shape per step:
        {
          "step_number": int,
          "instruction": str,
          "detections": [
            {
              "guidance_tool": str,
              "tool_settings": dict,
              "label":         str,
              "bbox":          list,
              "mask":          np.ndarray | None,
            },
            ...
          ]
        }
    """
    if not os.path.exists(image_path):
        raise FileNotFoundError(f"Image not found: {image_path}")

    # Create (or reset) the retry session for this capture
    session = _new_retry_session(capture_id, detector, server_url)

    # ── Call 1: Planner ───────────────────────────────────────────────────────
    plan = _fetch_step_plan(user_prompt, image_path, pinned_intent=intent)
    if not plan.steps:
        return []

    resolved_intent = intent if intent in ("task", "query") else plan.intent

    # ── Query path ────────────────────────────────────────────────────────────
    if resolved_intent == "query":
        return _run_query_path(plan, image_path, detector, server_url, session)

    # ── Task path: Call 2 → tool selection → detection ────────────────────────
    annotation_plan = _fetch_object_annotations(plan)

    annotation_map: dict[int, list[ObjectAnnotation]] = {
        s.step_number: s.objects for s in annotation_plan.annotated_steps
    }

    tag_to_view: dict[str, str] = {}
    for step in plan.steps:
        for obj in step.objects:
            if obj.tag not in tag_to_view:
                tag_to_view[obj.tag] = obj.user_view

    tag_to_noun: dict[str, str] = {}
    for s in annotation_plan.annotated_steps:
        for obj in s.objects:
            if obj.original_tag not in tag_to_noun:
                tag_to_noun[obj.original_tag] = obj.simple_noun

    unique_tags: list[str] = []
    for step in plan.steps:
        for obj in step.objects:
            if obj.tag and obj.tag not in unique_tags:
                unique_tags.append(obj.tag)

    detected_objects_map = make_object_map(
        unique_tags, image_path, server_url, detector, tag_to_noun
    )

    # ── Build results and record misses for retry ─────────────────────────────
    results_per_step = []

    for i, step in enumerate(plan.steps, 1):
        ann_objects = annotation_map.get(i, [])

        if not ann_objects:
            results_per_step.append({
                "step_number": i,
                "instruction": step.instruction,
                "detections":  [],
            })
            continue

        step_detections = []

        for ann_obj in ann_objects:
            tag       = ann_obj.original_tag
            user_view = tag_to_view.get(tag, "front")
            decision  = _select_tool(ann_obj.action, user_view)

            guidance_tool = decision["guidance_tool"]
            tool_settings = decision["tool_settings"]

            print(f"[vision_pipeline] Step {i} | '{ann_obj.simple_noun}' "
                  f"action={ann_obj.action} view={user_view} "
                  f"→ {guidance_tool} {tool_settings}")

            if not tag:
                step_detections.append({
                    "guidance_tool": guidance_tool,
                    "tool_settings": tool_settings,
                    "label": "", "bbox": [], "mask": None,
                })
                continue

            detected_items = detected_objects_map.get(tag, [])

            if not detected_items:
                print(f"[vision_pipeline] Step {i}: '{tag}' not found — queued for retry.")
                _record_missed(
                    session, i, step.instruction,
                    tag, ann_obj.simple_noun, user_view,
                    guidance_tool, tool_settings,
                )
                continue

            for item in detected_items:
                step_detections.append({
                    "guidance_tool": guidance_tool,
                    "tool_settings": tool_settings,
                    "label":         tag,
                    "bbox":          item["bbox"],
                    "mask":          item["mask"],
                })
# ── Move pair validation ───────────────────────────────────────────────────
# A move overlay is only meaningful when both source and target are present.
# If one is missing (queued for retry or simply undetected), downgrade the
# lone survivor to indicator_arrow so it still gives the user a useful hint
# rather than an orphaned prefab with no line.
        # move_detections = [d for d in step_detections if d["guidance_tool"] == "move"]
        # if move_detections:
        #     has_source = any(d["tool_settings"].get("role") == "source" for d in move_detections)
        #     has_target = any(d["tool_settings"].get("role") == "target" for d in move_detections)

    

        # ── Move pair validation ───────────────────────────────────────────
        # A move overlay is only meaningful when BOTH source and target are
        # present in the same response. If one is missing (not in image,
        # queued for retry), downgrade the lone survivor to indicator_arrow
        # so the user still gets a useful arrow rather than an orphaned
        # source with no line, or a target with nothing pointing at it.
        # When retry later finds the missing half, MergeOverlays will spawn
        # it as move and wire the pair at that point.
        move_detections = [d for d in step_detections if d["guidance_tool"] == "move"]
        if move_detections:
            has_source = any(d["tool_settings"].get("role") == "source" for d in move_detections)
            has_target = any(d["tool_settings"].get("role") == "target" for d in move_detections)

            if not (has_source and has_target):
                missing = "target" if has_source else "source"
                print(f"[vision_pipeline] Step {i}: move pair incomplete "
                      f"(missing {missing}) — downgrading to indicator_arrow.")
                for d in step_detections:
                    if d["guidance_tool"] == "move":
                        d["guidance_tool"] = "indicator_arrow"
                        d["tool_settings"] = {
                            "placement_rule": d["tool_settings"].get("placement_rule", "up")
                        }

        results_per_step.append({
            "step_number": i,
            "instruction": step.instruction,
            "detections":  step_detections,
        })
       
    missed_count = len(session.missed)
    print(f"[vision_pipeline] {missed_count} object(s) queued for retry "
          f"(capture_id='{capture_id}').")

    return results_per_step


# ---------------------------------------------------------------------------
# Query path (separate from task — no Annotator, no gesture)
# ---------------------------------------------------------------------------

def _run_query_path(
    plan: SemanticPlan,
    image_path: str,
    detector: str,
    server_url: str,
    session: RetrySession,
) -> list:
    """
    Handles intent='query': locate / identify / count requests.
    No Annotator call — tool is always indicator_arrow.
    Misses are recorded in the retry session exactly like the task path.
    """
    results = []
    print("[vision_pipeline] Query path active.")

    for i, step in enumerate(plan.steps, 1):
        unique_tags = [obj.tag for obj in step.objects if obj.tag]

        # For queries, tags are already short-ish nouns — pass None so
        # make_object_map uses the tag itself as the YOLOE class name.
        detected_objects_map = make_object_map(
            unique_tags, image_path, server_url, detector, tag_to_noun=None
        )

        step_detections = []

        for obj in step.objects:
            tag       = obj.tag
            user_view = obj.user_view
            tool_settings = {"placement_rule": _VIEW_TO_PLACEMENT.get(user_view, "up")}

            detected_items = detected_objects_map.get(tag, [])

            if not detected_items:
                print(f"[vision_pipeline/query] '{tag}' not detected — queued for retry.")
                # Queries always use indicator_arrow, so pre-compute the same way
                _record_missed(
                    session, i, step.instruction,
                    tag,
                    tag.split()[0] if tag else tag,  # best-effort bare noun
                    user_view,
                    "indicator_arrow",
                    tool_settings,
                )
                continue

            for item in detected_items:
                step_detections.append({
                    "guidance_tool": "indicator_arrow",
                    "tool_settings": tool_settings,
                    "label":         tag,
                    "bbox":          item["bbox"],
                    "mask":          item["mask"],
                })

        results.append({
            "step_number": i,
            "instruction": step.instruction,
            "detections":  step_detections,
        })

    return results


# ---------------------------------------------------------------------------
# Retry detection — called by /retry route in app.py
# ---------------------------------------------------------------------------

def retry_detections(
    capture_id: str,
    new_image_path: str,
) -> list:
    """
    Attempts to detect every object that was missed in the original /process
    call for capture_id, using new_image_path.

    Returns a list of step results in the same shape as fetch_step_segmentations,
    but containing ONLY the steps that have at least one newly found object.
    Steps whose objects are still all undetected are omitted entirely — the
    caller (app.py) should not emit text-only overlays for them on retry,
    since the Quest already has the text from the original /process response.

    Objects that are found are removed from the retry session so they won't
    be searched for again on the next retry interval.
    """
    session = get_retry_session(capture_id)
    if session is None:
        raise KeyError(f"No retry session found for capture_id='{capture_id}'. "
                       "Call /process first.")

    with _retry_lock:
        still_missed = list(session.missed)  # snapshot to iterate safely

    if not still_missed:
        print(f"[vision_pipeline/retry] '{capture_id}': nothing left to retry.")
        return []

    # Collect unique tags across all missed objects
    unique_tags = list(dict.fromkeys(m.tag for m in still_missed))
    tag_to_noun = {m.tag: m.simple_noun for m in still_missed}

    print(f"[vision_pipeline/retry] '{capture_id}': retrying {len(unique_tags)} tag(s) "
          f"in new image '{new_image_path}'.")

    detected_objects_map = make_object_map(
        unique_tags, new_image_path,
        session.server_url, session.detector, tag_to_noun,
    )

    # Group newly found items by step, build overlay-shaped dicts
    # step_number → {"instruction": str, "detections": [...]}
    found_by_step: dict[int, dict] = {}
    newly_resolved: list[_MissedObject] = []

    for missed in still_missed:
        items = detected_objects_map.get(missed.tag, [])
        if not items:
            continue  # still not found — leave in session for next retry

        step_n = missed.step_number
        if step_n not in found_by_step:
            found_by_step[step_n] = {
                "step_number": step_n,
                "instruction": missed.instruction,
                "detections":  [],
            }

        for item in items:
            found_by_step[step_n]["detections"].append({
                "guidance_tool": missed.guidance_tool,
                "tool_settings": missed.tool_settings,
                "label":         missed.tag,
                "bbox":          item["bbox"],
                "mask":          item["mask"],
            })

        newly_resolved.append(missed)

    # Remove resolved objects from the session
    if newly_resolved:
        with _retry_lock:
            for resolved in newly_resolved:
                try:
                    session.missed.remove(resolved)
                except ValueError:
                    pass  # already removed by a concurrent request
        print(f"[vision_pipeline/retry] '{capture_id}': resolved "
              f"{len(newly_resolved)} object(s), "
              f"{len(session.missed)} still pending.")

    return list(found_by_step.values())