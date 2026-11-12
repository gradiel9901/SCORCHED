using System.Collections.Generic;
using UnityEngine;

namespace Scorched.Environment
{
    /// <summary>
    /// Scene-level supervisor for 1:1 glove-tight structural collision in SCORCHED.
    /// Tracks scan telemetry and optionally auto-normalizes newly added structures in the Editor.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class ScorchedCollisionManager : MonoBehaviour
    {
        [Header("Scan & Auto-Fix Settings")]
        public bool AutoFixNewObjectsInEditor = false;
        public LayerMask IgnoreLayers;
        [Tooltip("Skip meshes smaller than this maximum bounds size or non-manifold decals.")]
        public float MinMaxBoundsSize = 0.02f;
        public int MinVertexCount = 3;

        [Header("Last Scan Telemetry")]
        public int TotalStructuresScanned;
        public int MissingCollidersFixed;
        public int TrappingBoxCollidersRemoved;
        public int MismatchedProxyMeshesReplaced;
        public int ConvexTrapsFixed;
        public int LodCollidersNormalized;
        public string LastScanSummary = "Not scanned yet.";

        [HideInInspector]
        public List<string> RecentFixLog = new List<string>();
    }
}
