using System;
using System.Collections.Generic;
using UnityEngine;
using Scorched.Combat;

namespace Scorched.Player
{
    /// <summary>
    /// Tactical spawn point definition with position and view rotation.
    /// </summary>
    [Serializable]
    public struct SpawnPointData
    {
        public Vector3 Position;
        public float YawAngle;

        public SpawnPointData(Vector3 pos, float yaw)
        {
            Position = pos;
            YawAngle = yaw;
        }

        public Quaternion Rotation => Quaternion.Euler(0f, YawAngle, 0f);
    }

    /// <summary>
    /// Tactical multiplayer spawn management system for "Scorched".
    /// Distributes players across distinct, balanced spawn points to eliminate overlapping spawns.
    /// Supports both Free For All (perimeter/randomized distribution) and Team Match
    /// (Alpha Base West vs Bravo Base East).
    /// </summary>
    [DisallowMultipleComponent]
    public class TacticalSpawnManager : MonoBehaviour
    {
        public static TacticalSpawnManager Instance { get; private set; }

        #region Factory Map Outdoor Concrete Courtyard Spawn Points
        // Team Alpha Bases (West side of outdoor concrete courtyard)
        private static readonly SpawnPointData[] TeamAlphaSpawns = new SpawnPointData[]
        {
            new SpawnPointData(new Vector3(-13.0f, 1.2f, 1.5f), 90f),  // West Rail - Facing East
            new SpawnPointData(new Vector3(-9.0f, 1.2f, -1.0f), 70f),  // West South - Facing East-North
            new SpawnPointData(new Vector3(-9.0f, 1.2f, 4.0f), 110f),  // West North - Facing East-South
            new SpawnPointData(new Vector3(-5.0f, 1.2f, 1.5f), 90f)   // Mid West - Facing East
        };

        // Team Bravo Bases (East side of outdoor concrete courtyard)
        private static readonly SpawnPointData[] TeamBravoSpawns = new SpawnPointData[]
        {
            new SpawnPointData(new Vector3(4.0f, 1.2f, 1.5f), 270f),   // Mid East - Facing West
            new SpawnPointData(new Vector3(8.0f, 1.2f, -1.5f), 290f),  // East South - Facing West-North
            new SpawnPointData(new Vector3(8.0f, 1.2f, 4.0f), 250f),   // East North - Facing West-South
            new SpawnPointData(new Vector3(12.0f, 1.2f, 1.0f), 270f)   // East Rail - Facing West
        };

        // All 8 Outdoor Concrete Spawns for Free For All (distributed across outdoor courtyard)
        private static readonly SpawnPointData[] AllFfaSpawns = new SpawnPointData[]
        {
            new SpawnPointData(new Vector3(-13.0f, 1.2f, 1.5f), 90f),
            new SpawnPointData(new Vector3(-9.0f, 1.2f, -1.0f), 70f),
            new SpawnPointData(new Vector3(-9.0f, 1.2f, 4.0f), 110f),
            new SpawnPointData(new Vector3(-5.0f, 1.2f, 1.5f), 90f),
            new SpawnPointData(new Vector3(4.0f, 1.2f, 1.5f), 270f),
            new SpawnPointData(new Vector3(8.0f, 1.2f, -1.5f), 290f),
            new SpawnPointData(new Vector3(8.0f, 1.2f, 4.0f), 250f),
            new SpawnPointData(new Vector3(12.0f, 1.2f, 1.0f), 270f)
        };
        #endregion

        private readonly Dictionary<ulong, int> _assignedSpawnIndices = new Dictionary<ulong, int>();
        private readonly HashSet<int> _occupiedSpawnIndices = new HashSet<int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            if (Instance == null && FindAnyObjectByType<TacticalSpawnManager>() == null)
            {
                var go = new GameObject("TacticalSpawnManager");
                go.AddComponent<TacticalSpawnManager>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Clears all spawn reservations when a match resets or server stops.
        /// </summary>
        public void ResetSpawns()
        {
            _assignedSpawnIndices.Clear();
            _occupiedSpawnIndices.Clear();
            Debug.Log("[TacticalSpawnManager] Reset all tactical spawn reservations.");
        }

        /// <summary>
        /// Releases a spawn point when a player disconnects.
        /// </summary>
        public void ReleaseSpawn(ulong clientId)
        {
            if (_assignedSpawnIndices.TryGetValue(clientId, out int spawnIdx))
            {
                _occupiedSpawnIndices.Remove(spawnIdx);
                _assignedSpawnIndices.Remove(clientId);
                Debug.Log($"[TacticalSpawnManager] Released spawn point index {spawnIdx} for Client {clientId}.");
            }
        }

        /// <summary>
        /// Retrieves the previously assigned spawn position and rotation for a client, if known.
        /// </summary>
        public bool TryGetAssignedSpawn(ulong clientId, out Vector3 position, out Quaternion rotation)
        {
            if (_assignedSpawnIndices.TryGetValue(clientId, out int idx))
            {
                SpawnPointData data;
                if (idx < AllFfaSpawns.Length)
                {
                    data = AllFfaSpawns[idx];
                }
                else
                {
                    data = AllFfaSpawns[0];
                }

                position = SnapToFloor(data.Position);
                rotation = data.Rotation;
                return true;
            }

            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>
        /// Assigns and retrieves a distinct, randomized tactical spawn point for the given player.
        /// </summary>
        public void GetSpawn(ulong clientId, int team, GameMode mode, out Vector3 position, out Quaternion rotation)
        {
            SpawnPointData selectedData;

            if (mode == GameMode.TeamMatch)
            {
                // Select from respective team roster
                var teamSpawns = (team == 0) ? TeamAlphaSpawns : TeamBravoSpawns;
                int teamOffset = (team == 0) ? 0 : 4;

                // Pick first unoccupied spawn in this team's base, or modulo index
                int chosenLocalIdx = 0;
                bool foundEmpty = false;
                for (int i = 0; i < teamSpawns.Length; i++)
                {
                    if (!_occupiedSpawnIndices.Contains(teamOffset + i))
                    {
                        chosenLocalIdx = i;
                        foundEmpty = true;
                        break;
                    }
                }

                if (!foundEmpty)
                {
                    chosenLocalIdx = (int)(clientId % (ulong)teamSpawns.Length);
                }

                int globalIdx = teamOffset + chosenLocalIdx;
                _occupiedSpawnIndices.Add(globalIdx);
                _assignedSpawnIndices[clientId] = globalIdx;

                selectedData = teamSpawns[chosenLocalIdx];
                Debug.Log($"[TacticalSpawnManager] Client {clientId} assigned Team {(team == 0 ? "Alpha" : "Bravo")} spawn index {globalIdx} at {selectedData.Position}.");
            }
            else
            {
                // Free For All: Pick unoccupied spawn from all 8 locations
                List<int> availableIndices = new List<int>();
                for (int i = 0; i < AllFfaSpawns.Length; i++)
                {
                    if (!_occupiedSpawnIndices.Contains(i))
                    {
                        availableIndices.Add(i);
                    }
                }

                int chosenIdx;
                if (availableIndices.Count > 0)
                {
                    // Randomize from available spawns
                    int r = UnityEngine.Random.Range(0, availableIndices.Count);
                    chosenIdx = availableIndices[r];
                }
                else
                {
                    // Fallback to modulo if all 8 are occupied
                    chosenIdx = (int)(clientId % (ulong)AllFfaSpawns.Length);
                }

                _occupiedSpawnIndices.Add(chosenIdx);
                _assignedSpawnIndices[clientId] = chosenIdx;
                selectedData = AllFfaSpawns[chosenIdx];
                Debug.Log($"[TacticalSpawnManager] Client {clientId} assigned FFA spawn index {chosenIdx} at {selectedData.Position}.");
            }

            // Snap position to terrain/floor surface
            position = SnapToFloor(selectedData.Position);
            rotation = selectedData.Rotation;
        }

        /// <summary>
        /// Raycasts downward to precisely place the character on solid outdoor concrete geometry.
        /// </summary>
        public static Vector3 SnapToFloor(Vector3 targetPos)
        {
            Vector3 rayStart = targetPos + Vector3.up * 2.0f;
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 8.0f, ~0, QueryTriggerInteraction.Ignore))
            {
                return hit.point + Vector3.up * 0.05f;
            }
            return targetPos;
        }
    }
}
