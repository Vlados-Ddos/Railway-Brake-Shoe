using System;
using System.Collections.Generic;
using UnityEngine;

namespace RailwayBrakeShoe
{
    public sealed partial class BrakeShoeBehaviour
    {
        internal string PurchaseShopId;
        internal bool StockDestroyed;
        private ConfigurableJoint holdingJoint;
        private Bogie holdingBogie;
        private float nextHoldAttempt;
        private bool holdWasCreated;
        private BoxCollider[] physicalBoxes;
        private float pushedUntil;
        private float pushedSpeed;

        internal bool HasStaticHold { get { return holdingJoint != null && holdingBogie != null && !holdingBogie.HasDerailed; } }

        private float GetHoldingCapacity(TrainCar car)
        {
            if (car == null || car.Bogies == null) return 0f;
            float handbrake = 0f;
            // Bogie.FixedUpdate: brakingFactor * maxBrakingForcePerKg * rb.mass.
            // A fully applied handbrake supplies factor 1 on every bogie of this car.
            foreach (Bogie bogie in car.Bogies)
                if (bogie != null && bogie.rb != null && !bogie.HasDerailed)
                    handbrake += bogie.maxBrakingForcePerKg * bogie.rb.mass;
            Settings c = Main.Config;
            return ShoeRules.Capacity(handbrake, c.HandbrakeEquivalent, c.DryFriction,
                WeatherCompatibility.GetWetness(), c.WetFrictionMultiplier, c.MaximumBrakeForce, c.BreakawayForce);
        }

        private bool TryHoldStoppedWheel(Bogie bogie, float speed, double workDirection)
        {
            if (holdWasCreated && holdingJoint == null)
            {
                // PhysX broke the contact because its reaction exceeded capacity.
                holdWasCreated = false;
                holdingBogie = null;
                nextHoldAttempt = Time.time + 1f;
            }
            if (HasStaticHold)
            {
                if (holdingBogie != bogie || !wheelTouching || contactFromBehind || jammedInFrog)
                    ReleaseStaticHold();
                else
                {
                    holdingJoint.breakForce = GetHoldingCapacity(contactCar);
                    return true;
                }
            }
            if (speed > StopEnterSpeed || Time.time < nextHoldAttempt || body == null ||
                bogie == null || bogie.rb == null || bogie.rb.isKinematic) return false;
            float capacity = GetHoldingCapacity(contactCar);
            if (capacity <= 0f) return false;
            // A static joint is valid only when this shoe can resist the force
            // currently trying to move the car down the grade. If the grade
            // exceeds capacity, use the normal sliding path instead.
            float slopeForce = GetSlopeForceComponent(bogie, contactCar);
            if (slopeForce > capacity) return false;

            Vector3 centre, forward, up;
            if (!RailPlacement.TryGetPoseAtSpan(currentTrack, railSpan, out centre, out forward, out up)) return false;
            Vector3 direction = forward * (float)workDirection;
            // The existing kinematic rail body is the fixed contact. A one-sided
            // linear limit supports gravity/coupler/traction loads in PhysX. There
            // is two metres of free travel AWAY from the wedge, then we detach.
            // No projection, gravity cancellation, or whole-train velocity reset.
            holdingJoint = gameObject.AddComponent<ConfigurableJoint>();
            holdingJoint.autoConfigureConnectedAnchor = false;
            holdingJoint.connectedBody = bogie.rb;
            holdingJoint.axis = transform.InverseTransformDirection(direction);
            holdingJoint.secondaryAxis = transform.InverseTransformDirection(up);
            holdingJoint.anchor = transform.InverseTransformPoint(bogie.rb.position - direction);
            holdingJoint.connectedAnchor = Vector3.zero;
            holdingJoint.xMotion = ConfigurableJointMotion.Limited;
            holdingJoint.yMotion = ConfigurableJointMotion.Free;
            holdingJoint.zMotion = ConfigurableJointMotion.Free;
            holdingJoint.angularXMotion = ConfigurableJointMotion.Free;
            holdingJoint.angularYMotion = ConfigurableJointMotion.Free;
            holdingJoint.angularZMotion = ConfigurableJointMotion.Free;
            SoftJointLimit limit = new SoftJointLimit();
            limit.limit = 1f;
            limit.bounciness = 0f;
            limit.contactDistance = 0f;
            holdingJoint.linearLimit = limit;
            holdingJoint.projectionMode = JointProjectionMode.None;
            holdingJoint.breakForce = capacity;
            holdingJoint.breakTorque = float.PositiveInfinity;
            holdingJoint.enableCollision = true;
            holdingBogie = bogie;
            holdWasCreated = true;
            return true;
        }

        private void ReleaseStaticHold()
        {
            if (holdingJoint != null)
            {
                // Destroy is deferred by Unity; free the constraint immediately.
                holdingJoint.xMotion = ConfigurableJointMotion.Free;
                UnityEngine.Object.Destroy(holdingJoint);
            }
            holdingJoint = null;
            holdingBogie = null;
            holdWasCreated = false;
        }

        internal bool KeepHeldTraveller(Bogie bogie, float localVelocity)
        {
            if (!HasStaticHold || holdingBogie != bogie || !IsAnchored || !offsetLocked ||
                contactFromBehind || jammedInFrog || !isActiveAndEnabled) return false;
            double work = isReversed ? -1.0 : 1.0;
            // Derive orientation geometrically; bogie and shoe can straddle a joint.
            float toward = Vector3.Dot(bogie.transform.forward, transform.forward);
            if (localVelocity * toward < -0.002f)
            {
                ReleaseStaticHold();
                return false;
            }
            // The game normally integrates PhysX positional solver error back into
            // span. While the contact holds, retain its existing span instead of
            // accumulating micrometres of penetration every physics step.
            if (Math.Abs(localVelocity) > StopExitSpeed)
            {
                ReleaseStaticHold();
                nextHoldAttempt = Time.time + 1f;
                return false;
            }
            return true;
        }

        internal bool CanReturnManually()
        {
            EnsureItemControl();
            bool owned = item != null && item.InventorySpecs != null && item.InventorySpecs.BelongsToPlayer;
            bool stored = item == null || item.IsGrabbed() || item.IsSnapped || item.InContainer != null;
            float speed = body != null && !body.isKinematic ? body.velocity.magnitude : 0f;
            if (Time.time < pushedUntil) speed = Math.Max(speed, pushedSpeed);
            bool touching = wheelTouching;
            if (IsAnchored)
            {
                Bogie b; double axle; float direction;
                // Query geometry now: returning an item must not depend on which
                // MonoBehaviour received FixedUpdate first on the button frame.
                if (TryFindEngagedAxle(out b, out axle, out direction))
                {
                    double along = (axle - railSpan) * (isReversed ? -1.0 : 1.0);
                    double back = BrakeShoeFactory.VisualBounds.size.z * BrakeShoeFactory.StopOuterFace +
                        WheelClearanceForHeight(GetWheelRadius(b.Car), BrakeShoeFactory.VisualBounds.size.y);
                    touching |= along >= -Main.Config.CaptureHalfLength && along <= back;
                }
            }
            return ShoeRules.CanRecover(true, owned, stored, restorePending || restoreHoldActive,
                touching, HasStaticHold || offsetLocked || IsUnderRollingWheel, speed,
                body != null && !body.isKinematic ? body.angularVelocity.magnitude : 0f);
        }

        internal void PrepareManualReturn()
        {
            // StorageController is about to move this exact item from its copied
            // world list. Leave membership to that existing transaction.
            DetachFromRailInternal(true);
            worldStorageRegisteredByMod = false;
        }

        private void GetColliderExtents(out double min, out double max)
        {
            if (physicalBoxes == null) physicalBoxes = GetComponentsInChildren<BoxCollider>();
            min = double.PositiveInfinity;
            max = double.NegativeInfinity;
            foreach (BoxCollider box in physicalBoxes)
            {
                if (box == null || !box.enabled || box.isTrigger) continue;
                Vector3 half = box.size * 0.5f;
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        for (int z = -1; z <= 1; z += 2)
                        {
                            Vector3 corner = box.transform.TransformPoint(box.center + Vector3.Scale(half, new Vector3(x, y, z)));
                            double longitudinal = transform.InverseTransformPoint(corner).z * (isReversed ? -1.0 : 1.0);
                            min = Math.Min(min, longitudinal);
                            max = Math.Max(max, longitudinal);
                        }
            }
            if (double.IsInfinity(min)) { min = -0.207; max = 0.207; }
        }

        private double MoveWithShoeContacts(double target, float speed, HashSet<BrakeShoeBehaviour> chain)
        {
            double movement = target - railSpan;
            if (Math.Abs(movement) < 1e-7 || !IsAnchored || !chain.Add(this)) return 0.0;
            double sign = Math.Sign(movement);
            double requested = Math.Abs(movement);
            double start = railSpan;
            double min, max;
            GetColliderExtents(out min, out max);
            double leading = sign > 0 ? max : -min;
            BrakeShoeBehaviour nearest = null;
            double nearestGap = requested;
            RailPlacement.SpanMap nearestMap = default(RailPlacement.SpanMap);
            // Search the entire swept interval, not just the proposed end point.
            // Side, neighbouring-track orientation and both collider sizes matter.
            foreach (BrakeShoeBehaviour other in Main.Shoes)
            {
                if (other == null || other == this || !other.IsAnchored || chain.Contains(other)) continue;
                RailPlacement.SpanMap map;
                if (!TryMapOtherShoe(other, out map)) continue;
                if (map.MapSide(other.railSide) != railSide) continue;
                double otherCentre = map.Map(other.railSpan);
                if ((otherCentre - start) * sign <= 0) continue;
                double otherMin, otherMax;
                other.GetColliderExtents(out otherMin, out otherMax);
                if (map.Alignment < 0f) { double oldMin = otherMin; otherMin = -otherMax; otherMax = -oldMin; }
                double trailing = sign > 0 ? -otherMin : otherMax;
                double gap = (otherCentre - start) * sign - leading - trailing - 0.002;
                if (gap <= nearestGap)
                {
                    nearest = other;
                    nearestGap = Math.Max(0.0, gap);
                    nearestMap = map;
                }
            }
            double allowed = requested;
            if (nearest != null)
            {
                double before = ShoeRules.TravelBeforeContact(nearestGap, requested);
                double push = requested - before;
                if (push > 0 && nearest.CanBePushedOnRail())
                {
                    double otherTravel = nearest.MoveWithShoeContacts(nearest.railSpan +
                        sign * nearestMap.Alignment * push, speed, chain);
                    allowed = before + Math.Abs(otherTravel);
                }
                else allowed = before;
            }
            allowed = Math.Min(allowed, SweepFreeShoes(sign, allowed, speed));
            railSpan = start + sign * allowed;
            MigrateAcrossJointIfNeeded();
            ApplyRailPose();
            pushedUntil = Time.time + Time.fixedDeltaTime * 2f;
            pushedSpeed = speed;
            chain.Remove(this);
            return sign * allowed;
        }

        private bool TryMapOtherShoe(BrakeShoeBehaviour other, out RailPlacement.SpanMap map)
        {
            map = default(RailPlacement.SpanMap);
            if (other.currentTrack == currentTrack)
            {
                map.Track = currentTrack; map.Alignment = 1f; return true;
            }
            if (RailPlacement.TryGetNeighbour(currentTrack, false, out map) && map.Track == other.currentTrack) return true;
            return RailPlacement.TryGetNeighbour(currentTrack, true, out map) && map.Track == other.currentTrack;
        }

        private bool CanBePushedOnRail()
        {
            return !jammedInFrog && !HasStaticHold && !wheelTouching && !offsetLocked && !restorePending;
        }

        private double SweepFreeShoes(double sign, double distance, float speed)
        {
            if (body == null || distance <= 0.0) return distance;
            Vector3 centre, forward, up;
            if (!RailPlacement.TryGetPoseAtSpan(currentTrack, railSpan, out centre, out forward, out up)) return distance;
            Vector3 direction = forward * (float)sign;
            double allowed = distance;
            // Sweep the existing compound colliders against loose dynamic shoes.
            // Unity's kinematic MovePosition alone can tunnel through a light prop.
            RaycastHit[] hits = body.SweepTestAll(direction, (float)distance + 0.002f, QueryTriggerInteraction.Ignore);
            HashSet<Rigidbody> pushed = new HashSet<Rigidbody>();
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null) continue;
                BrakeShoeBehaviour other = hit.collider.GetComponentInParent<BrakeShoeBehaviour>();
                if (other == null || other == this || other.IsAnchored || other.body == null) continue;
                if (other.item != null && (other.item.IsGrabbed() || other.item.IsSnapped || other.item.InContainer != null)) continue;
                allowed = Math.Min(allowed, Math.Max(0.0, hit.distance - 0.002f));
                if (!other.body.isKinematic && pushed.Add(other.body))
                {
                    float relative = speed - Vector3.Dot(other.body.velocity, direction);
                    if (relative > 0f) other.body.AddForce(direction * (relative * other.body.mass), ForceMode.Impulse);
                }
            }
            return allowed;
        }
    }

    internal static class ShoeHoldingPatch
    {
        internal static bool Prefix(Bogie __instance, float localZVelocity, ref bool __result)
        {
            foreach (BrakeShoeBehaviour shoe in Main.Shoes)
                if (shoe != null && shoe.KeepHeldTraveller(__instance, localZVelocity))
                {
                    __instance.traveller.MoveToSpan(__instance.traveller.Span);
                    __instance.point1 = __instance.traveller.curPoint;
                    __instance.point2 = __instance.traveller.pointSet.points[__instance.point1.index + 1];
                    __result = true;
                    return false;
                }
            return true;
        }
    }
}
