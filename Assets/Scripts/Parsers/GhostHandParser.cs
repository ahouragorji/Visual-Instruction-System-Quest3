using UnityEngine;

namespace Alpha.Parsers
{
    public class GhostHandParser : IToolParser
    {
        public string ToolName => "ghost_hand";

        private readonly GameObject _pokePrefab;
        private readonly GameObject _grabPrefab;
        private readonly GameObject _cleanPrefab;
        private readonly GameObject _rotatePrefab; // NEW: Added Rotate Prefab

        private readonly float _approachDistance;
        private readonly float _surfaceOffset;

        public GhostHandParser(
            GameObject pokePrefab,
            GameObject grabPrefab,
            GameObject cleanPrefab,
            GameObject rotatePrefab, // NEW: Added to constructor
            float approachDistance = 0.0f,
            float surfaceOffset    = 0.0f)
        {
            _pokePrefab        = pokePrefab;
            _grabPrefab        = grabPrefab;
            _cleanPrefab       = cleanPrefab;
            _rotatePrefab      = rotatePrefab; // NEW
            _approachDistance  = approachDistance;
            _surfaceOffset     = surfaceOffset;
        }

        public ParsedSpawnData Parse(QuestInstructionReceiver.AROverlay overlay, Vector3 userSavedPos)
        {
            string gesture = GetParam(overlay.tool_settings, "gesture", "poke");
            string placementRule = GetParam(overlay.tool_settings, "placement_rule", "front");

            // ── 1. Pick prefab ────────────────────────────────────────────────
            GameObject prefab;
            switch (gesture)
            {
                case "grab":   prefab = _grabPrefab;   break;
                case "clean":  prefab = _cleanPrefab;  break;
                case "rotate": prefab = _rotatePrefab; break; // NEW: Route to rotate
                default:       prefab = _pokePrefab;   break;
            }

            if (prefab == null) return new ParsedSpawnData();

            Vector3 center = new Vector3(overlay.worldX, overlay.worldY, overlay.worldZ);
            
            // Assuming rotate uses the approach distance so the hand hovers just off the knob/cap
            float offset = (gesture == "clean") ? _surfaceOffset : _approachDistance;

            // ── 2. Calculate User Vectors ─────────────────────────────────────
            // True 3D line of sight from the object to the user's eyes
            Vector3 trueToUser = userSavedPos - center;
            if (trueToUser.sqrMagnitude < 0.0001f) trueToUser = Vector3.forward;
            
            // Flat horizontal line (used to keep cleans/grabs level with the floor)
            Vector3 flatToUser = trueToUser;
            flatToUser.y = 0f; 
            if (flatToUser.sqrMagnitude < 0.0001f) flatToUser = Vector3.forward;

            trueToUser.Normalize();
            flatToUser.Normalize();

            Vector3 finalPos;
            Quaternion finalRot;

            // ── 3. Calculate Placement ────────────────────────────────────────
            if (gesture == "poke")
            {
                // POKING: Always approaches exactly along the 3D line of sight.
                finalPos = center + (trueToUser * offset);
                finalRot = Quaternion.LookRotation(-trueToUser, Vector3.up);
            }
            else if (gesture == "rotate")
            {
                // NEW: ROTATING LOGIC
                if (placementRule == "up")
                {
                    // e.g., Twisting a cap off a jar on a table.
                    // Hand floats above the object and points perfectly DOWN.
                    finalPos = center + (Vector3.up * offset);
                    finalRot = Quaternion.LookRotation(Vector3.down, flatToUser);
                }
                else 
                {
                    // e.g., Twisting a knob on an oven/wall.
                    // Hand floats in front of the object and points directly IN at it.
                    finalPos = center + (trueToUser * offset);
                    finalRot = Quaternion.LookRotation(-trueToUser, Vector3.up);
                }
            }
            else if (placementRule == "up")
            {
                // UP PLACEMENT: Grabbing or Cleaning a top surface
                finalPos = center + (Vector3.up * offset);

                if (gesture == "clean")
                {
                    // Lays flat on the surface, facing away from user
                    finalRot = Quaternion.LookRotation(-flatToUser, Vector3.up);
                }
                else // grab
                {
                    // Stays horizontal, custom X-axis points to user
                    Vector3 localZ = Vector3.Cross(flatToUser, Vector3.up).normalized;
                    finalRot = Quaternion.LookRotation(localZ, Vector3.up);
                }
            }
            else 
            {
                // FRONT PLACEMENT: Grabbing or Cleaning a wall/appliance face
                finalPos = center + (flatToUser * offset);

                if (gesture == "clean")
                {
                    // Faces user, stays completely vertical and horizontal
                    finalRot = Quaternion.LookRotation(flatToUser, Vector3.up);
                }
                else // grab
                {
                    // Stays horizontal, custom X-axis points to user
                    Vector3 localZ = Vector3.Cross(flatToUser, Vector3.up).normalized;
                    finalRot = Quaternion.LookRotation(localZ, Vector3.up);
                }
            }

            return new ParsedSpawnData
            {
                PrefabToSpawn = prefab,
                Position      = finalPos,
                Rotation      = finalRot,
            };
        }

        private static string GetParam(QuestInstructionReceiver.FeatureParameter[] parameters, string key, string fallback)
        {
            if (parameters == null) return fallback;
            foreach (var p in parameters)
                if (p.key == key) return p.value;
            return fallback;
        }
    }
}