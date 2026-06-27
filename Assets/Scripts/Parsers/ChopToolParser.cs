using UnityEngine;

namespace Alpha.Parsers
{
    public class ChopToolParser : IToolParser
    {
        public string ToolName => "chop_line";

        private GameObject _chopPrefab;
        private float _offset;

        public ChopToolParser(GameObject chopPrefab, float offset)
        {
            _chopPrefab = chopPrefab;
            _offset = offset;
        }

        public ParsedSpawnData Parse(QuestInstructionReceiver.AROverlay overlay, Vector3 userSavedPos)
        {
            Vector3 center = new Vector3(overlay.worldX, overlay.worldY, overlay.worldZ);

            // Find the top edge of the bounding box so the line doesn't clip inside the object
            float edgeDistance = 0f;
            if (overlay.bboxCorners != null && overlay.bboxCorners.Length == 8)
            {
                foreach (var corner in overlay.bboxCorners)
                {
                    Vector3 centerToCorner = corner.ToVector3() - center;
                    float projection = Vector3.Dot(centerToCorner, Vector3.up);
                    if (projection > edgeDistance) edgeDistance = projection;
                }
            }

            // Place the prefab slightly above the top of the object
            Vector3 finalPos = center + (Vector3.up * (edgeDistance + _offset));

            // Make the cut-line face the user perfectly
            Vector3 objectToUser = userSavedPos - center;
            objectToUser.y = 0; 
            objectToUser.Normalize();

            // LookRotation makes the Z-axis (forward) point at the user. 
            Quaternion rot = (objectToUser != Vector3.zero) 
                ? Quaternion.LookRotation(objectToUser, Vector3.up) 
                : Quaternion.identity;

            return new ParsedSpawnData
            {
                PrefabToSpawn = _chopPrefab,
                Position = finalPos,
                Rotation = rot
            };
        }
    }
}