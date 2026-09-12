using System;

namespace RailwayBrakeShoe
{
    // Numerical rules shared by the runtime and the regression executable.
    internal static class ShoeRules
    {
        internal static float Capacity(float fullHandbrake, float equivalents, float dryFriction,
            float wetness, float wetMultiplier, float maximum, float breakaway)
        {
            float wet = Math.Max(0f, Math.Min(1f, wetness));
            float force = fullHandbrake * Math.Max(0f, equivalents) * Math.Max(0f, dryFriction) / 0.32f
                * (1f + (Math.Max(0f, wetMultiplier) - 1f) * wet);
            force = Math.Min(force, Math.Min(Math.Max(0f, maximum), Math.Max(0f, breakaway)));
            return float.IsNaN(force) || float.IsInfinity(force) ? 0f : Math.Max(0f, force);
        }

        internal static double TravelBeforeContact(double gap, double requested)
        {
            return Math.Min(Math.Max(0.0, gap), Math.Max(0.0, requested));
        }

        internal static int Stock(int purchasedHere, int pendingHere)
        {
            return Math.Max(0, 20 - Math.Max(0, purchasedHere) - Math.Max(0, pendingHere));
        }

        // Unified speed hazard table from the 1.2.2 design. Below 25 km/h the
        // speed event is inactive; above it both rolls use the same contact
        // sample and therefore cannot disagree about which event occurred.
        internal static float SpeedDropChance(float speedKmh)
        {
            if (speedKmh <= 25f) return 0f;
            return speedKmh < 35f ? 0.25f : 0.50f;
        }

        internal static float SpeedDerailChance(float speedKmh)
        {
            if (speedKmh <= 25f) return 0f;
            return Math.Min(1f, Math.Max(0f, speedKmh) / 100f);
        }

        internal static bool CanRecover(bool manual, bool owned, bool heldOrStored, bool restoring,
            bool wheelContact, bool securing, float linearSpeed, float angularSpeed)
        {
            return manual && owned && !heldOrStored && !restoring && !wheelContact && !securing
                && linearSpeed <= 0.02f && angularSpeed <= 0.05f;
        }
    }
}
