using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Scorched.Player;
using Scorched.UI;

namespace Scorched.Inventory
{
    /// <summary>
    /// Handles first-person raycast interaction within 3.0m (hold E for 1.0s to scavenge chests,
    /// tap E to pick up WorldItemPickups) and toggles the Cabal 30-slot Inventory & Crafting overlay
    /// via Tab or I while synchronizing Cursor.lockState.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerScavengeInteractor : NetworkBehaviour
    {
        [Header("Scavenge Interaction")]
        [SerializeField] private float _interactDistance = 3.0f;
        [SerializeField] private LayerMask _interactMask = ~0;

        private PlayerInventoryController _inventory;
        private PlayerMovement _playerMovement;
        private PlayerHealthController _healthController;

        private LootContainerInteractable _focusedContainer;
        private LootContainerInteractable _openContainer;
        private WorldItemPickup _focusedPickup;

        private void Awake()
        {
            _inventory = GetComponent<PlayerInventoryController>();
            _playerMovement = GetComponent<PlayerMovement>();
            _healthController = GetComponent<PlayerHealthController>();
        }

        private void Update()
        {
            if (IsSpawned && !IsOwner) return;

            var keyboard = Keyboard.current;
            bool isAlive = _healthController == null || _healthController.IsAlive;

            if (!isAlive)
            {
                if (ScorchedInputGates.IsBackpackOpen) SetInventoryOpen(false);
                if (ScorchedInputGates.IsContainerOpen) CloseContainerWindow();
                ClearInteractionPrompt();
                return;
            }

            if (keyboard != null && (keyboard.bKey.wasPressedThisFrame || keyboard.tabKey.wasPressedThisFrame))
            {
                SetInventoryOpen(!ScorchedInputGates.IsBackpackOpen);
            }

            if (ScorchedInputGates.IsInventoryOpen && keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                CloseContainerWindow();
                SetInventoryOpen(false);
                ScorchedInputGates.LastUiClosedFrame = Time.frameCount;
                ClearInteractionPrompt();
                return;
            }

            if (ScorchedInputGates.IsContainerOpen)
            {
                if (keyboard != null && keyboard.eKey.wasPressedThisFrame)
                {
                    CloseContainerWindow();
                }
                else if (_openContainer == null || !_openContainer.IsWithinReach(transform.position, 4.75f))
                {
                    CloseContainerWindow();
                }
            }

            if (ScorchedInputGates.IsInventoryOpen)
            {
                ClearInteractionPrompt();
                return;
            }

            UpdateTargetAndInteraction(keyboard);
        }

        public static void SetInventoryOpen(bool open)
        {
            ScorchedInputGates.IsBackpackOpen = open;
            SyncCursorAndOverlayLayout();
        }

        public static void CloseActiveContainer()
        {
            var local = PlayerInventoryController.LocalInstance;
            var interactor = local != null ? local.GetComponent<PlayerScavengeInteractor>() : null;
            if (interactor != null)
            {
                interactor.CloseContainerWindow();
                return;
            }

            ScorchedInputGates.IsContainerOpen = false;
            ScorchedContainerLootUI.Instance?.SetContainer(null);
            SyncCursorAndOverlayLayout();
        }

        private void OpenContainerWindow(LootContainerInteractable container)
        {
            _openContainer = container;
            ScorchedInputGates.IsContainerOpen = container != null;
            ScorchedContainerLootUI.Instance?.SetContainer(container);
            SyncCursorAndOverlayLayout();
        }

        private void CloseContainerWindow()
        {
            _openContainer = null;
            ScorchedInputGates.IsContainerOpen = false;
            ScorchedContainerLootUI.Instance?.SetContainer(null);
            SyncCursorAndOverlayLayout();
        }

        private static void SyncCursorAndOverlayLayout()
        {
            ScorchedInventoryUI.Instance?.SetVisible(ScorchedInputGates.IsBackpackOpen);
            ScorchedCraftingUI.Instance?.SetVisible(ScorchedInputGates.IsBackpackOpen && !ScorchedInputGates.IsContainerOpen);
            ScorchedContainerLootUI.Instance?.UpdatePanelPlacement();

            if (ScorchedInputGates.IsInventoryOpen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void UpdateTargetAndInteraction(Keyboard keyboard)
        {
            Transform cam = _playerMovement != null && _playerMovement.CameraTransform != null
                ? _playerMovement.CameraTransform
                : (Camera.main != null ? Camera.main.transform : null);

            if (cam == null)
            {
                ClearInteractionPrompt();
                return;
            }

            FindBestInteractable(cam, out LootContainerInteractable nextContainer, out WorldItemPickup nextPickup);

            _focusedContainer = nextContainer;
            _focusedPickup = nextPickup;

            if (_focusedContainer != null)
            {
                ScorchedHUDController.Instance?.UpdateInteractionPrompt(_focusedContainer.GetPromptLabel(), 0f, true);
                if (_focusedContainer.CanScavenge && keyboard != null && keyboard.eKey.wasPressedThisFrame)
                {
                    _focusedContainer.RequestOpen(_inventory);
                    OpenContainerWindow(_focusedContainer);
                }
                return;
            }

            if (_focusedPickup != null && !_focusedPickup.IsCollected)
            {
                ScorchedHUDController.Instance?.UpdateInteractionPrompt(_focusedPickup.GetPromptLabel(), 0f, true);
                if (keyboard != null && keyboard.eKey.wasPressedThisFrame)
                {
                    _focusedPickup.RequestPickup(_inventory);
                }
                return;
            }

            ClearInteractionPrompt();
        }

        private void FindBestInteractable(
            Transform cam,
            out LootContainerInteractable bestContainer,
            out WorldItemPickup bestPickup)
        {
            bestContainer = null;
            bestPickup = null;

            float aimRange = _interactDistance + 0.5f;
            float bestDist = float.MaxValue;

            // Pass 1: Forgiving forward SphereCastAll skipping player self-colliders and unrelated trigger volumes (e.g. ShadeZoneBuilding).
            var hits = Physics.SphereCastAll(cam.position, 0.28f, cam.forward, aimRange, _interactMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i].collider;
                if (col == null || col.transform.IsChildOf(transform)) continue;

                var container = col.GetComponentInParent<LootContainerInteractable>();
                var pickup = container == null ? col.GetComponentInParent<WorldItemPickup>() : null;
                if (container == null && pickup == null) continue;
                if (pickup != null && pickup.IsCollected) continue;

                Transform targetRoot = container != null ? container.transform : pickup.transform;
                Vector3 samplePoint = hits[i].point != Vector3.zero
                    ? hits[i].point
                    : col.ClosestPoint(cam.position);
                if (!HasLineOfSight(cam.position, samplePoint, targetRoot)) continue;

                float d = hits[i].distance;
                if (d < bestDist)
                {
                    bestDist = d;
                    bestContainer = container;
                    bestPickup = pickup;
                }
            }

            if (bestContainer != null || bestPickup != null)
            {
                return;
            }

            // Pass 2: Close-proximity forward-cone fallback so standing beside low floor boxes or large trash cans works without looking straight down.
            Vector3 bodyCenter = transform.position + Vector3.up * 0.9f;
            var nearby = Physics.OverlapSphere(bodyCenter, _interactDistance + 0.5f, _interactMask, QueryTriggerInteraction.Collide);
            Vector3 flatFwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            bool hasFlatFwd = flatFwd.sqrMagnitude > 0.0001f;
            if (hasFlatFwd) flatFwd.Normalize();

            float bestScore = float.MaxValue;
            for (int i = 0; i < nearby.Length; i++)
            {
                var col = nearby[i];
                if (col == null || col.transform.IsChildOf(transform)) continue;

                var container = col.GetComponentInParent<LootContainerInteractable>();
                var pickup = container == null ? col.GetComponentInParent<WorldItemPickup>() : null;
                if (container != null && !container.CanScavenge) continue;
                if (pickup != null && pickup.IsCollected) continue;
                if (container == null && pickup == null) continue;

                Vector3 closest = col.isTrigger ? col.bounds.center : col.ClosestPoint(cam.position);
                float distToCam = Vector3.Distance(cam.position, closest);
                if (distToCam > _interactDistance + 0.75f) continue;

                Vector3 toTarget = closest - cam.position;
                if (toTarget.sqrMagnitude < 0.0001f) continue;

                float dot3D = Vector3.Dot(cam.forward, toTarget.normalized);
                Vector3 flatToTarget = Vector3.ProjectOnPlane(toTarget, Vector3.up);
                float dotFlat = hasFlatFwd && flatToTarget.sqrMagnitude > 0.0001f
                    ? Vector3.Dot(flatFwd, flatToTarget.normalized)
                    : -1f;

                if (dot3D < 0.35f && dotFlat < 0.50f) continue;

                Transform targetRoot = container != null ? container.transform : pickup.transform;
                Vector3 losPoint = closest + Vector3.up * 0.12f;
                if (!HasLineOfSight(cam.position, losPoint, targetRoot)) continue;

                float score = distToCam - Mathf.Max(dot3D, dotFlat) * 1.2f;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestContainer = container;
                    bestPickup = pickup;
                }
            }
        }

        private bool HasLineOfSight(Vector3 from, Vector3 to, Transform targetRoot)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist <= 0.05f) return true;

            var blockers = Physics.RaycastAll(from, dir / dist, dist, _interactMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < blockers.Length; i++)
            {
                var c = blockers[i].collider;
                if (c == null) continue;
                if (c.transform.IsChildOf(transform)) continue;
                if (targetRoot != null && c.transform.IsChildOf(targetRoot)) continue;
                // Ignore small edge-grazing right next to target surface (< 0.15m from target point)
                if (blockers[i].distance >= dist - 0.15f) continue;
                return false;
            }
            return true;
        }

        private void ClearInteractionPrompt()
        {
            _focusedContainer = null;
            _focusedPickup = null;
            ScorchedHUDController.Instance?.UpdateInteractionPrompt(string.Empty, 0f, false);
        }
    }
}

