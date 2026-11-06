using UnityEngine;

namespace Scorched.Combat
{
    /// <summary>
    /// Contract for any entity in "SCORCHED" capable of receiving health and hydration damage
    /// from authoritative ballistic weapons and environmental combat hazards.
    /// </summary>
    public interface IDamageable
    {
        /// <summary>
        /// Applies authoritative combat damage and tactical hydration drain to the target.
        /// </summary>
        /// <param name="healthDamage">Direct hit point damage subtracted from target health.</param>
        /// <param name="hydrationDamage">Dehydration penalty applied in Scorched's survival loop.</param>
        /// <param name="attackerClientId">NGO Client ID of the shooter for kill/hit attribution.</param>
        /// <param name="hitPoint">World-space location of the ballistic impact.</param>
        void ApplyCombatHit(float healthDamage, float hydrationDamage, ulong attackerClientId, Vector3 hitPoint);
    }
}
