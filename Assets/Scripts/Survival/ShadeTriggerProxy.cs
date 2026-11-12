using UnityEngine;

namespace Scorched.Survival
{
    /// <summary>
    /// Lightweight trigger relay placed on each building's child ShadeTriggerVolume.
    /// </summary>
    [DisallowMultipleComponent]
    public class ShadeTriggerProxy : MonoBehaviour
    {
        [SerializeField] private ShadeZoneBuilding _ownerZone;

        public void Bind(ShadeZoneBuilding owner)
        {
            _ownerZone = owner;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_ownerZone == null || other == null) return;
            var thermal = other.GetComponentInParent<PlayerThermalController>();
            if (thermal != null)
            {
                _ownerZone.HandlePlayerEntered(thermal);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (_ownerZone == null || other == null) return;
            var thermal = other.GetComponentInParent<PlayerThermalController>();
            if (thermal != null)
            {
                _ownerZone.HandlePlayerExited(thermal);
            }
        }
    }
}

