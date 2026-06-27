using UnityEngine;

namespace Alpha.Parsers
{
    public class MoveToolParser : IToolParser
    {
        public string ToolName => "move";

        private GameObject _sourcePrefab;
        private GameObject _targetPrefab;
        private float _offset;

        public MoveToolParser(GameObject sourcePrefab, GameObject targetPrefab, float offset)
        {
            _sourcePrefab = sourcePrefab;
            _targetPrefab = targetPrefab;
            _offset = offset;
        }

        public ParsedSpawnData Parse(QuestInstructionReceiver.AROverlay overlay, Vector3 userSavedPos)
        {
            // 1. Extract settings
            string role = GetParam(overlay.tool_settings, "role", "source");
            string placement = GetParam(overlay.tool_settings, "placement_rule", "up");

            // 2. Choose the correct prefab based on the role
            GameObject prefab = (role == "target") ? _targetPrefab : _sourcePrefab;

            // 3. Calculate Spatial Direction
            Vector3 center = new Vector3(overlay.worldX, overlay.worldY, overlay.worldZ);

            Vector3 objectToUser = userSavedPos - center;
            objectToUser.y = 0;
            objectToUser.Normalize();

            Vector3 dir = Vector3.up; // Default for "up"
            if (placement == "front")
            {
                dir = objectToUser;
            }

            // 4. Find the edge of the bounding box
            float edgeDistance = 0f;
            if (overlay.bboxCorners != null && overlay.bboxCorners.Length == 8)
            {
                foreach (var corner in overlay.bboxCorners)
                {
                    Vector3 centerToCorner = corner.ToVector3() - center;
                    float projection = Vector3.Dot(centerToCorner, dir);
                    if (projection > edgeDistance) edgeDistance = projection;
                }
            }

            Vector3 finalPos = center + (dir * (edgeDistance + _offset));

            // 5. Rotation (Point inward toward the center of the object)
            Quaternion rot = (finalPos != center)
                ? Quaternion.LookRotation(center - finalPos, Vector3.up)
                : Quaternion.identity;

            return new ParsedSpawnData
            {
                PrefabToSpawn = prefab,
                Position = finalPos,
                Rotation = rot
            };
        }

        private string GetParam(QuestInstructionReceiver.FeatureParameter[] parameters, string key, string fallback)
        {
            if (parameters == null) return fallback;
            foreach (var p in parameters) if (p.key == key) return p.value;
            return fallback;
        }
    }
}