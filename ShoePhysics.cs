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
        private RailTrack holdingTrack;
        private double holdingBogieSpan;
        // Geometric contact hysteresis, not a change to wheel capture/forces.
        private const float HoldSeparationTolerance = 0.002f;
        private float nextHoldAttempt;
        private bool holdWasCreated;
        private BoxCollider[] physicalBoxes;
        private float pushedUntil;
        private float pushedSpeed;

        internal bool HasStaticHold { get { return holdingJoint != null && holdingBogie != null && !holdingBogie.HasDerailed; } }
        internal double HeldBogieSpan { get { return holdingBogieSpan; } }

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
                ShoeHoldingPatch.Unregister(this, holdingBogie);
                holdingBogie = null;
                nextHoldAttempt = Time.time + 1f;
            }
            if (HasStaticHold)
            {
                if (holdingBogie != bogie || !wheelTouching || contactFromBehind || jammedInFrog ||
                    bogie.track != holdingTrack || body == null || !body.isKinematic || bogie.rb == null ||
                    holdingJoint.connectedBody != bogie.rb)
                {
                    ReleaseStaticHold();
                    return false;
                }
                else if (HoldSeparation() > HoldSeparationTolerance)
                {
                    ReleaseStaticHold();
                    nextHoldAttempt = Time.time + 1f;
                    return false;
                }
                else
                {
                    float currentCapacity = GetHoldingCapacity(contactCar);
                    if (currentCapacity <= 0f) { ReleaseStaticHold(); return false; }
                    // Rewriting a joint property can wake its connected bodies.
                    if (holdingJoint.breakForce != currentCapacity) holdingJoint.breakForce = currentCapacity;
                    return true;
                }
            }
            if (speed > StopEnterSpeed || Time.time < nextHoldAttempt || body == null ||
                !body.isKinematic || bogie == null || bogie.rb == null || bogie.rb.isKinematic ||
                bogie.traveller == null) return false;
            float capacity = GetHoldingCapacity(contactCar);
            if (capacity <= 0f) return false;
            // A static joint is valid only when this shoe can resist the force
            // currently trying to move the car down the grade. If the grade
            // exceeds capacity, use the normal sliding path instead.
            float slopeForce = GetSlopeForceComponent(bogie, contactCar);
            if (slopeForce > capacity) return false;

            Vector3 centre, forward, up;
            if (!RailPlacement.TryGetPoseAtSpan(currentTrack, railSpan, out centre, out forward, out up)) return false;
            Vector3 railPosition;
            RailPlacement.GetRailheadPose(currentTrack, centre, forward, up, railSide, out railPosition, out forward);
            // Define the joint in the physical rail pose that ApplyRailPose will
            // submit this step, never the interpolated render Transform. The
            // bogie's own rail tangent is the axis of its native rail motion.
            Vector3 direction = bogie.traveller.worldForward.normalized;
            if (Vector3.Dot(direction, forward * (float)workDirection) < 0f) direction = -direction;
            Quaternion railRotation = Quaternion.LookRotation(forward * (float)workDirection, up);
            Quaternion inverseRailRotation = Quaternion.Inverse(railRotation);
            // Native Bogie.FixedUpdate also moves to this traveller position.
            // A transient off-rail solver pose must not become the fixed stop.
            Vector3 contactCentre = (Vector3)bogie.traveller.worldPosition + WorldMover.currentMove +
                bogie.rb.rotation * bogie.rb.centerOfMass;
            // The existing kinematic rail body is the fixed contact. A one-sided
            // linear limit supports gravity/coupler/traction loads in PhysX. There
            // is two metres of free travel AWAY from the wedge, then we detach.
            // No projection, gravity cancellation, or whole-train velocity reset.
            holdingJoint = gameObject.AddComponent<ConfigurableJoint>();
            holdingJoint.autoConfigureConnectedAnchor = false;
            holdingJoint.connectedBody = bogie.rb;
            holdingJoint.axis = inverseRailRotation * direction;
            holdingJoint.secondaryAxis = inverseRailRotation * bogie.traveller.worldUp;
            holdingJoint.anchor = inverseRailRotation * (contactCentre - direction - railPosition);
            holdingJoint.connectedAnchor = bogie.rb.centerOfMass;
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
            holdingTrack = bogie.track;
            holdingBogieSpan = bogie.traveller.Span;
            holdWasCreated = true;
            ShoeHoldingPatch.Register(this, bogie);
            return true;
        }

        private void ReleaseStaticHold()
        {
            ShoeHoldingPatch.Unregister(this, holdingBogie);
            if (holdingJoint != null)
            {
                // Destroy is deferred by Unity; free the constraint immediately.
                holdingJoint.xMotion = ConfigurableJointMotion.Free;
                UnityEngine.Object.Destroy(holdingJoint);
            }
            holdingJoint = null;
            holdingBogie = null;
            holdingTrack = null;
            holdWasCreated = false;
        }

        internal bool KeepHeldTraveller(Bogie bogie, float localVelocity)
        {
            if (!HasStaticHold || holdingBogie != bogie || !IsAnchored || !offsetLocked ||
                contactFromBehind || jammedInFrog || !isActiveAndEnabled) return false;
            if (body == null || !body.isKinematic || bogie.rb == null || bogie.traveller == null ||
                bogie.track != holdingTrack || holdingJoint.connectedBody != bogie.rb)
            {
                ReleaseStaticHold();
                return false;
            }
            float separation = HoldSeparation();
            if (separation > HoldSeparationTolerance)
            {
                ReleaseStaticHold();
                nextHoldAttempt = Time.time + 1f;
                return false;
            }
            // Clamp only solver penetration through the existing physical stop.
            // Movement on the free side remains native, including rolling away.
            // Velocity alone is not evidence of separation or joint breakage.
            return separation <= 0f;
        }

        private float HoldSeparation()
        {
            Vector3 direction = body.rotation * holdingJoint.axis;
            Vector3 stop = body.position + body.rotation * holdingJoint.anchor +
                direction * holdingJoint.linearLimit.limit;
            Vector3 connected = holdingBogie.rb.position + holdingBogie.rb.rotation * holdingJoint.connectedAnchor;
            return Vector3.Dot(stop - connected, direction);
        }

        // Observe an actual axle on consecutive physics steps. CCD alone cannot
        // repair a missed span contact: native bogies are driven by travellers.
        private Bogie wheelSampleBogie;
        private TrainCar wheelSampleCar;
        private RailTrack wheelSampleTrack;
        private double wheelSampleSpan, wheelSampleOffset;
        private float wheelSampleTime;

        private bool TryMapWheelTrack(RailTrack track, out RailPlacement.SpanMap map)
        {
            map = default(RailPlacement.SpanMap);
            if (track == null || currentTrack == null) return false;
            if (track == currentTrack) { map.Track = track; map.Alignment = 1f; return true; }
            if (RailPlacement.TryGetNeighbour(currentTrack, false, out map) && map.Track == track) return true;
            return RailPlacement.TryGetNeighbour(currentTrack, true, out map) && map.Track == track;
        }

        private void SampleWheelAxle(Bogie bogie, double span, float direction)
        {
            RailPlacement.SpanMap map;
            if (bogie == null || bogie.traveller == null || !TryMapWheelTrack(bogie.track, out map))
            { wheelSampleBogie = null; return; }
            double offset = (span - map.Map(bogie.traveller.Span)) * (direction >= 0f ? 1.0 : -1.0);
            if (wheelSampleBogie == bogie && Math.Abs(offset - wheelSampleOffset) > 0.002)
                ClearEngagement();
            wheelSampleBogie = bogie;
            wheelSampleCar = bogie.Car;
            wheelSampleTrack = currentTrack;
            wheelSampleSpan = span;
            // Offset in the bogie's own forward axis identifies the same axle,
            // even if another axle is nearer after a fast step or axes reverse.
            wheelSampleOffset = offset;
            wheelSampleTime = Time.fixedTime;
        }

        private bool TryFindSweptAxle(out Bogie bogie, out double axleSpan, out float direction, out double contactAlong)
        {
            bogie = null; axleSpan = 0.0; direction = 1f; contactAlong = 0.0;
            Bogie sample = wheelSampleBogie;
            float elapsed = Time.fixedTime - wheelSampleTime;
            if (sample == null || sample.HasDerailed || sample.Car == null || sample.Car != wheelSampleCar ||
                sample.traveller == null || sample.rb == null || elapsed <= 0f ||
                elapsed > Time.fixedDeltaTime * 1.5f || (IsServiceShoe && sample.Car != ServiceCar)) return false;
            RailPlacement.SpanMap currentMap, previousMap;
            if (!TryMapWheelTrack(sample.track, out currentMap) || !TryMapWheelTrack(wheelSampleTrack, out previousMap)) return false;
            float sign = sample.TrackDirectionSign >= 0f ? 1f : -1f;
            float mappedSign = sign * currentMap.Alignment;
            double current = currentMap.Map(sample.traveller.Span) + wheelSampleOffset * mappedSign;
            double previous = previousMap.Map(wheelSampleSpan);
            double moved = current - previous;
            float speed = Math.Abs(Vector3.Dot(sample.rb.velocity, sample.transform.forward));
            // A load/teleport or stale sample is not a wheel strike. No predicted
            // future position is used to enlarge the physical contact envelope.
            if (Math.Abs(moved) < 1e-7 || Math.Abs(moved) > speed * elapsed * 2.0 + 0.05) return false;
            double work = isReversed ? -1.0 : 1.0;
            double before = (previous - railSpan) * work, after = (current - railSpan) * work;
            double capture = IsServiceShoe ? ServiceContactDistance + ServiceContactGap : Main.Config.CaptureHalfLength;
            double back = BrakeShoeFactory.VisualBounds.size.z * BrakeShoeFactory.StopOuterFace +
                WheelClearanceForHeight(GetWheelRadius(sample.Car), BrakeShoeFactory.VisualBounds.size.y * BrakeShoeFactory.StopTopFraction);
            if (!IsServiceShoe)
                capture = Math.Max(capture, back - BrakeShoeFactory.VisualBounds.size.z *
                    (BrakeShoeFactory.StopOuterFace + BrakeShoeFactory.StopInnerFace) + HoldSeparationTolerance);
            double middle = BrakeShoeFactory.VisualBounds.size.z * BrakeShoeFactory.StopBoxCentre;
            bool behind = hasCapturedOffset && contactBogie == sample ? contactFromBehind : before > middle;
            if (behind ? (after >= before || before < -capture || after > back) :
                (after <= before || before > back || after < -capture)) return false;
            bogie = sample; axleSpan = current; direction = mappedSign;
            contactAlong = Math.Max(-capture, Math.Min(back, before));
            return true;
        }

        internal bool CanReturnManually()
        {
            if (IsServiceShoe || IsPermanentFrogJam) return false;
            EnsureItemControl();
            bool owned = item != null && item.InventorySpecs != null && item.InventorySpecs.BelongsToPlayer;
            bool stored = item == null || item.IsGrabbed() || item.IsSnapped || item.InContainer != null;
            float speed = body != null && !body.isKinematic ? body.velocity.magnitude : 0f;
            if (Time.time < pushedUntil) speed = Math.Max(speed, pushedSpeed);
            bool touching = wheelTouching;
            if (IsAnchored) touching |= HasWheelAtShoe();
            bool available = ShoeRules.CanRecover(true, owned, stored, restorePending || restoreHoldActive,
                touching, HasStaticHold || offsetLocked || IsUnderRollingWheel, speed,
                body != null && !body.isKinematic ? body.angularVelocity.magnitude : 0f);
            return available && !HasPhysicalTrainContact();
        }

        private static readonly Collider[] recoveryContacts = new Collider[32];

        private bool HasPhysicalTrainContact()
        {
            // Manual summon only, after the cheap state/axle checks. A resting
            // dynamic shoe can support a car without an anchored span or joint.
            // Query its existing solid boxes, plus PhysX's real contact skin.
            // OverlapBox supports non-convex train meshes; ClosestPoint does not.
            if (physicalBoxes == null) physicalBoxes = GetComponentsInChildren<BoxCollider>();
            foreach (BoxCollider box in physicalBoxes)
            {
                if (box == null || !box.enabled || box.isTrigger) continue;
                Vector3 scale = box.transform.lossyScale;
                scale = new Vector3(Math.Abs(scale.x), Math.Abs(scale.y), Math.Abs(scale.z));
                Vector3 half = Vector3.Scale(box.size, scale) * 0.5f +
                    Vector3.one * (box.contactOffset + Physics.defaultContactOffset);
                int count = Physics.OverlapBoxNonAlloc(box.transform.TransformPoint(box.center), half,
                    recoveryContacts, box.transform.rotation, ~0, QueryTriggerInteraction.Ignore);
                bool occupied = count == recoveryContacts.Length;
                for (int i = 0; i < count; i++)
                {
                    Collider other = recoveryContacts[i];
                    recoveryContacts[i] = null;
                    if (other == null || other.attachedRigidbody == body) continue;
                    Bogie bogie = other.GetComponentInParent<Bogie>();
                    if ((bogie != null && bogie.Car != null) || other.GetComponentInParent<TrainCar>() != null)
                        occupied = true;
                }
                // A saturated result cannot prove that the item is free.
                if (occupied) return true;
            }
            return false;
        }

        private bool HasWheelAtShoe()
        {
            // Manual recovery only. Check every physical occupant: the closest
            // axle can be outside the short ramp reach while a slightly farther
            // axle already touches the tall back of the stop. Motion is irrelevant.
            int count = BuildSearchMaps(Main.Config.CaptureHalfLength + 3.0);
            double work = isReversed ? -1.0 : 1.0;
            for (int m = 0; m < count; m++)
            {
                RailPlacement.SpanMap map = searchMaps[m];
                HashSet<Bogie> bogies = RailPlacement.GetBogiesOnTrack(map.Track);
                if (bogies == null) continue;
                foreach (Bogie b in bogies)
                {
                    if (b == null || b.HasDerailed || b.track != map.Track || b.traveller == null || b.Car == null) continue;
                    double back = BrakeShoeFactory.VisualBounds.size.z * BrakeShoeFactory.StopOuterFace +
                        WheelClearanceForHeight(GetWheelRadius(b.Car), BrakeShoeFactory.VisualBounds.size.y);
                    double front = Math.Max(Main.Config.CaptureHalfLength,
                        WheelClearanceForHeight(GetWheelRadius(b.Car), BrakeShoeFactory.VisualBounds.size.y * BrakeShoeFactory.StopTopFraction) -
                        BrakeShoeFactory.VisualBounds.size.z * BrakeShoeFactory.StopInnerFace + HoldSeparationTolerance);
                    float sign = b.TrackDirectionSign >= 0f ? 1f : -1f;
                    Bogie.AxleInfo[] axles = b.Axles;
                    int axleCount = axles == null || axles.Length == 0 ? 1 : axles.Length;
                    for (int i = 0; i < axleCount; i++)
                    {
                        if (axles != null && axles.Length != 0 && axles[i] == null) continue;
                        double offset = axles == null || axles.Length == 0 ? 0.0 : axles[i].distanceFromBogiePivot * sign;
                        double along = (map.Map(b.traveller.Span + offset) - railSpan) * work;
                        if (along >= -front && along <= back) return true;
                    }
                }
            }
            return false;
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

        private double MoveWithShoeContacts(double target, float speed, HashSet<BrakeShoeBehaviour> chain, Bogie pushingBogie = null)
        {
            double movement = target - railSpan;
            // A dense pile must not exhaust the managed stack. A capped tail
            // acts as a solid stop; callers retain their contact gap and sweep.
            if (Math.Abs(movement) < 1e-7 || !IsAnchored || jammedInFrog || pendingSwitchConflict != null || chain.Count >= 64 || !chain.Add(this)) return 0.0;
            if (pushingBogie == null && wheelTouching && !wheelStopped) pushingBogie = contactBogie;
            double sign = Math.Sign(movement);
            double requested = Math.Abs(movement);
            double start = railSpan;
            double min, max;
            GetColliderExtents(out min, out max);
            double frogTravel;
            string frogName;
            bool internalFrogHit = LimitTravelAtInternalFrog(start, target, min, max, out frogTravel, out frogName);
            if (internalFrogHit) requested = Math.Min(requested, frogTravel);
            double bladeTravel;
            string bladeName;
            bool bladeHit = LimitTravelAtSwitchCuts(start, start + sign * requested, min, max, out bladeTravel, out bladeName);
            if (bladeHit) requested = Math.Min(requested, bladeTravel);
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
                        sign * nearestMap.Alignment * push, speed, chain, pushingBogie);
                    allowed = before + Math.Abs(otherTravel);
                }
                else allowed = before;
            }
            allowed = Math.Min(allowed, SweepFreeShoes(sign, allowed, speed));
            railSpan = start + sign * allowed;
            if (internalFrogHit && allowed >= frogTravel - 1e-7) JamAtInternalFrog(frogName);
            if (bladeHit && !jammedInFrog && allowed >= bladeTravel - 1e-7)
            {
                // A confirmed blade contact always ejects the shoe. The existing
                // hazard handler separately evaluates the train's speed risk.
                pendingSwitchConflict = bladeName;
                // Keep the actual source of the push across the next contact
                // refresh, including a second shoe in a pile. Never substitute
                // a different nearby wheel when this event is consumed.
                switchImpactBogie = pushingBogie;
                switchImpactSpeed = pushingBogie != null && pushingBogie == contactBogie && wheelTouching ?
                    Math.Abs(lastBogieSpeed) : speed;
            }
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

        private TurnoutFrogGeometry.Marker internalFrogMarker;

        private TurnoutSwitchCutGeometry.Marker switchCutMarker;
        private RailTrack switchCutTrack;
        private float switchCutRetryTime;
        private string pendingSwitchConflict;
        private Bogie switchImpactBogie;
        private float? switchImpactSpeed;

        private bool LimitTravelAtSwitchCuts(double start, double target, double min, double max, out double travel, out string name)
        {
            travel = Math.Abs(target - start); name = null;
            if (jammedInFrog || currentTrack == null || travel < 1e-7) return false;
            if (switchCutTrack != currentTrack || (switchCutMarker != null && !switchCutMarker.Alive) ||
                (switchCutMarker == null && Time.time >= switchCutRetryTime))
            {
                switchCutTrack = currentTrack;
                switchCutMarker = TurnoutSwitchCutGeometry.Find(currentTrack);
                switchCutRetryTime = Time.time + 0.25f;
            }
            if (TurnoutSwitchCutGeometry.TryHit(currentTrack, railSide, start, target, min, max,
                ref switchCutMarker, out travel, out name)) return true;

            // Use the same native branch map as the existing frog test when
            // one physical sweep crosses into or out of the turnout track.
            double total;
            if (!RailPlacement.TryGetTrackSpan(currentTrack, out total)) return false;
            bool outEnd = target > start;
            if (outEnd ? target + max <= total : target + min >= 0.0) return false;
            RailPlacement.SpanMap map;
            if (!RailPlacement.TryGetNeighbour(currentTrack, outEnd, out map)) return false;
            return TurnoutSwitchCutGeometry.TryHit(map.Track, map.MapSide(railSide), map.Unmap(start), map.Unmap(target),
                map.Alignment > 0f ? min : -max, map.Alignment > 0f ? max : -min,
                ref switchCutMarker, out travel, out name);
        }

        private bool LimitTravelAtInternalFrog(double start, double target, double min, double max,
            out double travel, out string name)
        {
            if (TurnoutFrogGeometry.TryHit(currentTrack, railSide, start, target, min, max,
                ref internalFrogMarker, out travel, out name)) return true;
            // Match the existing migration's neighbouring section, including
            // inverted span/rail-side coordinates. Route selection stays native.
            double total;
            if (!RailPlacement.TryGetTrackSpan(currentTrack, out total)) return false;
            bool outEnd = target > start;
            if (outEnd ? target + max <= total : target + min >= 0.0) return false;
            RailPlacement.SpanMap map;
            if (!RailPlacement.TryGetNeighbour(currentTrack, outEnd, out map)) return false;
            return TurnoutFrogGeometry.TryHit(map.Track, map.MapSide(railSide), map.Unmap(start), map.Unmap(target),
                map.Alignment > 0f ? min : -max, map.Alignment > 0f ? max : -min,
                ref internalFrogMarker, out travel, out name);
        }

        private void JamAtInternalFrog(string name)
        {
            // Reuse the fixed anchor and per-bogie derail interaction, but keep
            // the obstacle kind so ordinary shoes cannot be ejected from this nose.
            ReleaseStaticHold();
            jammedInFrog = true;
            jammedAtInternalFrog = true;
            pendingSwitchConflict = null;
            switchImpactBogie = null;
            switchImpactSpeed = null;
            jammedJunctionName = name;
            jammedRolledBogie = null;
            offsetLocked = false;
            pushSpanDirection = 0.0;
            isScraping = false;
            slideSpeed = 0f;
            ResetHazardTimer();
            Main.LogAlways("Brake shoe jammed at the internal crossing nose of " + name + ".");
        }

        private bool IsAtInternalFrog()
        {
            double min, max, travel;
            string name;
            GetColliderExtents(out min, out max);
            return LimitTravelAtInternalFrog(railSpan, railSpan + 0.002, min, max, out travel, out name) ||
                LimitTravelAtInternalFrog(railSpan, railSpan - 0.002, min, max, out travel, out name) ||
                TurnoutFrogGeometry.IsLegacyContact(currentTrack, railSide, railSpan, min, max);
        }

        private bool TryFindCrossingAxle(out Bogie bogie, out double axleSpan, out float direction)
        {
            bogie = null; axleSpan = 0.0; direction = 1f;
            if (currentTrack == null) return false;
            // Select the branch pair owned by this exact turnout, including a
            // reversed-span branch with another junction at its other endpoint.
            RailTrack crossing = null;
            for (int end = 0; end < 2 && crossing == null; end++)
            {
                Junction junction = end == 0 ? currentTrack.inJunction : currentTrack.outJunction;
                if (junction == null || junction.outBranches == null || junction.outBranches.Count != 2 ||
                    junction.transform.parent != currentTrack.transform.parent) continue;
                bool ownBranch = false;
                RailTrack other = null;
                foreach (Junction.Branch branch in junction.outBranches)
                {
                    if (branch == null) continue;
                    if (branch.track == currentTrack) ownBranch = true;
                    else other = branch.track;
                }
                if (ownBranch && other != null && other.transform.parent == currentTrack.transform.parent) crossing = other;
            }
            if (crossing == null) return false;
            Vector3 center, forward, up;
            if (!RailPlacement.TryGetPoseAtSpan(currentTrack, railSpan, out center, out forward, out up)) return false;
            Vector3 shoeRight = Vector3.Cross(up, forward).normalized;
            Vector3 shoePosition = RailPlacement.GetRailheadPosition(currentTrack, center, shoeRight, up, railSide);
            Vector3 shoeForward = forward * (isReversed ? -1f : 1f);
            Vector3 shoeUp = up;
            shoeRight *= isReversed ? -1f : 1f;
            double crossingSpan;
            if (!RailPlacement.TryGetSpanAt(crossing, shoePosition, out crossingSpan) ||
                !RailPlacement.TryGetPoseAtSpan(crossing, crossingSpan, out center, out forward, out up)) return false;
            Vector3 right = Vector3.Cross(up, forward).normalized;
            float side = Vector3.Dot(shoePosition - center, right) >= 0f ? 1f : -1f;
            Vector3 rail = RailPlacement.GetRailheadPosition(crossing, center, right, up, side);
            if (Math.Abs(Vector3.Dot(shoePosition - rail, up)) > 0.08f) return false;
            double min, max;
            GetColliderExtents(out min, out max);
            // Project the actual compound footprint into the crossing wheel's
            // rail frame. Native wagon / DH4 / DE6 rim meshes span x=.6985..
            // .8067 at a rail centre of .7526 (54 mm on either side);
            // an outer rail or nearby independent track cannot pass this test.
            Vector3 low = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 high = -low;
            foreach (BoxCollider box in physicalBoxes)
            {
                if (box == null || !box.enabled || box.isTrigger) continue;
                Vector3 half = box.size * 0.5f;
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        for (int z = -1; z <= 1; z += 2)
                        {
                            Vector3 local = transform.InverseTransformPoint(box.transform.TransformPoint(
                                box.center + Vector3.Scale(half, new Vector3(x, y, z))));
                            Vector3 p = shoePosition + shoeRight * local.x + shoeUp * local.y + shoeForward * local.z - rail;
                            Vector3 q = new Vector3(Vector3.Dot(p, right), Vector3.Dot(p, up), Vector3.Dot(p, forward));
                            low = Vector3.Min(low, q); high = Vector3.Max(high, q);
                        }
            }
            if (low.x > 0.054f || high.x < -0.054f) return false;
            HashSet<Bogie> bogies = RailPlacement.GetBogiesOnTrack(crossing);
            if (bogies == null) return false;
            double best = double.PositiveInfinity;
            foreach (Bogie candidate in bogies)
            {
                if (candidate == null || candidate.HasDerailed || candidate.track != crossing || candidate.traveller == null) continue;
                Bogie.AxleInfo[] axles = candidate.Axles;
                int count = axles == null || axles.Length == 0 ? 1 : axles.Length;
                float sign = candidate.TrackDirectionSign == 0f ? 1f : candidate.TrackDirectionSign;
                double clearance = WheelClearanceForHeight(GetWheelRadius(candidate.Car), Math.Max(0f, high.y));
                for (int i = 0; i < count; i++)
                {
                    if (axles != null && axles.Length > 0 && axles[i] == null) continue;
                    double span = candidate.traveller.Span + (axles == null || axles.Length == 0 ? 0f : axles[i].distanceFromBogiePivot * sign);
                    double delta = span - crossingSpan;
                    if (delta < low.z - clearance || delta > high.z + clearance || Math.Abs(delta) >= best) continue;
                    best = Math.Abs(delta); bogie = candidate;
                    // Contact has been established geometrically. Report it in
                    // the existing engagement frame, without moving the anchor.
                    axleSpan = railSpan;
                    direction = Vector3.Dot(forward * sign, shoeForward * (isReversed ? -1f : 1f)) >= 0f ? 1f : -1f;
                }
            }
            return bogie != null;
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

    // The fixed inner crossing inside the authored turnout mesh. Moving switch
    // blades and selectedBranch are neither consulted nor modified here.
    internal static class TurnoutFrogGeometry
    {
        // A conservative 0.127 m correction towards the wing-rail throat,
        // retaining physical overlap with both crossing wheel paths. Swept
        // collider extents still stop the shoe; saved anchors are never moved.
        internal static readonly Vector3 Nose = new Vector3(0.727432f, 0.1471981f, -8.8f);
        private static readonly Vector3 LegacyNose = new Vector3(0.727432f, 0.1471981f, -8.9266434f);

        internal sealed class Marker
        {
            internal RailTrack Track;
            internal Junction Junction;
            internal MeshFilter Filter;
            internal Mesh Mesh;
        }

        private static bool IsBranch(Junction junction, RailTrack track)
        {
            if (junction == null || junction.outBranches == null || junction.outBranches.Count != 2) return false;
            for (int i = 0; i < junction.outBranches.Count; i++)
                if (junction.outBranches[i] != null && junction.outBranches[i].track == track) return true;
            return false;
        }

        private static Marker Find(RailTrack track)
        {
            Junction junction = IsBranch(track.inJunction, track) ? track.inJunction :
                IsBranch(track.outJunction, track) ? track.outJunction : null;
            if (junction == null || junction.transform.parent == null ||
                track.transform.parent != junction.transform.parent) return null;
            Transform meshTransform = junction.transform.parent.Find("Graphical/rails_static");
            if (meshTransform == null) return null;
            MeshFilter filter = meshTransform.GetComponent<MeshFilter>();
            Mesh mesh = filter == null ? null : filter.sharedMesh;
            // Unknown/replaced assets fail closed. No proximity-only fallback
            // and no vertex-array readback inside physics, even with LOD hidden.
            if (mesh == null || mesh.name != "rails_static" || mesh.vertexCount != 1370 ||
                (mesh.bounds.min - new Vector3(-0.8185022f, -0.0000026f, -12.0984354f)).sqrMagnitude > 0.000001f ||
                (mesh.bounds.max - new Vector3(2.6425574f, 0.1520014f, 12.0010166f)).sqrMagnitude > 0.000001f) return null;
            return new Marker { Track = track, Junction = junction, Filter = filter, Mesh = mesh };
        }

        internal static bool IsLegacyContact(RailTrack track, float side, double anchor, double min, double max)
        {
            if (track == null) return false;
            Marker marker = Find(track);
            if (marker == null) return false;
            Vector3 nose = marker.Filter.transform.TransformPoint(LegacyNose);
            double span;
            Vector3 center, forward, up;
            if (!RailPlacement.TryGetSpanAt(track, nose, out span) ||
                !RailPlacement.TryGetPoseAtSpan(track, span, out center, out forward, out up)) return false;
            Vector3 right = Vector3.Cross(up, forward).normalized;
            if (side != (Vector3.Dot(nose - center, right) >= 0f ? 1f : -1f)) return false;
            if ((RailPlacement.GetRailheadPosition(track, center, right, up, side) - nose).sqrMagnitude > 0.0225f) return false;
            return anchor + min <= span + 0.004 && anchor + max >= span - 0.004;
        }

        internal static bool TryHit(RailTrack track, float side, double start, double target, double min, double max,
            ref Marker marker, out double travel, out string name)
        {
            travel = Math.Abs(target - start); name = null;
            if (track == null || travel < 1e-7) return false;
            if (marker == null || marker.Track != track || marker.Filter == null || marker.Mesh == null || marker.Junction == null ||
                marker.Filter.sharedMesh != marker.Mesh) marker = Find(track);
            if (marker == null) return false;
            // TransformPoint handles mirrored prefabs, grades and origin shifts.
            // The rail frame and span are resolved from current native geometry.
            Vector3 nose = marker.Filter.transform.TransformPoint(Nose);
            Vector3 center, forward, up;
            if (!RailPlacement.TryGetPoseAtSpan(track, start, out center, out forward, out up)) return false;
            Vector3 right = Vector3.Cross(up, forward).normalized;
            Vector3 position = RailPlacement.GetRailheadPosition(track, center, right, up, side);
            double reach = travel + Math.Max(Math.Abs(min), Math.Abs(max)) + 0.15;
            if ((position - nose).sqrMagnitude > reach * reach) return false;
            double span;
            if (!RailPlacement.TryGetSpanAt(track, nose, out span) ||
                !RailPlacement.TryGetPoseAtSpan(track, span, out center, out forward, out up)) return false;
            right = Vector3.Cross(up, forward).normalized;
            float innerSide = Vector3.Dot(nose - center, right) >= 0f ? 1f : -1f;
            if (side != innerSide) return false;
            Vector3 rail = RailPlacement.GetRailheadPosition(track, center, right, up, side);
            if ((rail - nose).sqrMagnitude > 0.0225f) return false;
            double gap;
            if (target > start)
            {
                if (start + min > span + 0.002) return false;
                gap = Math.Max(0.0, span - start - max);
            }
            else
            {
                if (start + max < span - 0.002) return false;
                gap = Math.Max(0.0, start + min - span);
            }
            if (gap > travel) return false;
            travel = gap;
            name = marker.Junction.name + " / rails_static";
            return true;
        }
    }

    // Exact point-rail cuts in the installed turnout mesh. Each cut is
    // projected onto the live branch track and swept like the fixed frog nose;
    // no lever-distance trigger or guessed world coordinate is involved.
    internal static class TurnoutSwitchCutGeometry
    {
        private static readonly Vector3 ThroughCut = new Vector3(0.8086513f, 0.1472008f, 6.7532244f);
        private static readonly Vector3 DivergingCut = new Vector3(-0.7215974f, 0.1472009f, 6.7532244f);

        internal sealed class Marker
        {
            internal RailTrack Track;
            internal Junction Junction;
            internal MeshFilter StaticFilter;
            internal Mesh StaticMesh;
            internal RailTrack ThroughTrack, DivergingTrack;
            internal bool HasRoute
            {
                get
                {
                    if (Junction == null || Junction.outBranches == null ||
                        Junction.selectedBranch >= Junction.outBranches.Count) return false;
                    Junction.Branch branch = Junction.outBranches[Junction.selectedBranch];
                    return branch != null && (branch.track == ThroughTrack || branch.track == DivergingTrack);
                }
            }
            internal Vector3 Cut { get { return Junction.outBranches[Junction.selectedBranch].track == ThroughTrack ? ThroughCut : DivergingCut; } }
            internal bool Alive { get { return Track != null && Junction != null && StaticFilter != null &&
                StaticMesh != null && ThroughTrack != null && DivergingTrack != null &&
                StaticFilter.sharedMesh == StaticMesh; } }
        }

        private static bool IsBranch(Junction junction, RailTrack track)
        {
            if (junction == null || junction.outBranches == null || junction.outBranches.Count != 2) return false;
            for (int i = 0; i < junction.outBranches.Count; i++)
                if (junction.outBranches[i] != null && junction.outBranches[i].track == track) return true;
            return false;
        }

        internal static Marker Find(RailTrack track)
        {
            if (track == null) return null;
            Junction junction = IsBranch(track.inJunction, track) ? track.inJunction :
                IsBranch(track.outJunction, track) ? track.outJunction : null;
            if (junction == null || junction.transform.parent == null || track.transform.parent != junction.transform.parent) return null;
            Transform root = junction.transform.parent;
            Transform staticRail = root.Find("Graphical/rails_static");
            if (staticRail == null) return null;
            MeshFilter staticFilter = staticRail.GetComponent<MeshFilter>();
            Mesh staticMesh = staticFilter == null ? null : staticFilter.sharedMesh;
            if (staticMesh == null || staticMesh.name != "rails_static" || staticMesh.vertexCount != 1370 ||
                (staticMesh.bounds.min - new Vector3(-0.8185022f, -0.0000026f, -12.0984354f)).sqrMagnitude > 0.000001f ||
                (staticMesh.bounds.max - new Vector3(2.6425574f, 0.1520014f, 12.0010166f)).sqrMagnitude > 0.000001f) return null;

            // DoubleTrack appends a number to every unnamed track, including
            // these two children. Names are not branch identity. In the verified
            // prefab mesh basis the through centreline has the smaller lateral
            // displacement between its two actual endpoints. Use first to pick
            // the far endpoint independently of the pointset's span direction.
            RailTrack throughTrack = null, divergingTrack = null;
            float leastDeflection = float.PositiveInfinity;
            foreach (Junction.Branch branch in junction.outBranches)
            {
                if (branch == null || branch.track == null) return null;
                double length;
                Vector3 near, far, forward, up;
                if (!RailPlacement.TryGetTrackSpan(branch.track, out length) ||
                    !RailPlacement.TryGetPoseAtSpan(branch.track, branch.first ? 0 : length, out near, out forward, out up) ||
                    !RailPlacement.TryGetPoseAtSpan(branch.track, branch.first ? length : 0, out far, out forward, out up)) return null;
                float deflection = Math.Abs(staticRail.InverseTransformPoint(far).x - staticRail.InverseTransformPoint(near).x);
                if (deflection < leastDeflection)
                {
                    divergingTrack = throughTrack;
                    throughTrack = branch.track;
                    leastDeflection = deflection;
                }
                else divergingTrack = branch.track;
            }
            if (throughTrack == null || divergingTrack == null) return null;
            return new Marker { Track = track, Junction = junction, StaticFilter = staticFilter,
                StaticMesh = staticMesh, ThroughTrack = throughTrack, DivergingTrack = divergingTrack };
        }

        internal static bool TryHit(RailTrack track, float side, double start, double target, double min, double max,
            ref Marker marker, out double travel, out string name)
        {
            travel = Math.Abs(target - start); name = null;
            if (track == null || travel < 1e-7) return false;
            if (marker == null || marker.Track != track || !marker.Alive) marker = Find(track);
            if (marker == null || !marker.HasRoute) return false;

            // The switch selects the physical cut side, not a whole-track veto.
            // Both centreline branches pass through this throat; project the
            // selected cut onto the shoe's actual branch, including trailing travel.
            Vector3 cut = marker.StaticFilter.transform.TransformPoint(marker.Cut);
            Vector3 center, forward, up;
            if (!RailPlacement.TryGetPoseAtSpan(track, start, out center, out forward, out up)) return false;
            Vector3 right = Vector3.Cross(up, forward).normalized;
            Vector3 position = RailPlacement.GetRailheadPosition(track, center, right, up, side);
            double reach = travel + Math.Max(Math.Abs(min), Math.Abs(max)) + 0.15;
            if ((position - cut).sqrMagnitude > reach * reach) return false;

            double span;
            if (!RailPlacement.TryGetSpanAt(track, cut, out span) ||
                !RailPlacement.TryGetPoseAtSpan(track, span, out center, out forward, out up)) return false;
            right = Vector3.Cross(up, forward).normalized;
            float cutSide = Vector3.Dot(cut - center, right) >= 0f ? 1f : -1f;
            if (side != cutSide) return false;
            Vector3 rail = RailPlacement.GetRailheadPosition(track, center, right, up, side);
            if ((rail - cut).sqrMagnitude > 0.0225f) return false;

            double gap;
            if (target > start)
            {
                if (start + min > span + 0.002) return false;
                gap = Math.Max(0.0, span - start - max);
            }
            else
            {
                if (start + max < span - 0.002) return false;
                gap = Math.Max(0.0, start + min - span);
            }
            if (gap > travel) return false;
            travel = gap;
            name = marker.Junction.name + " / switch point cut";
            return true;
        }
    }

    // Shared centre paths extracted from the flat rail crowns of the installed
    // prefab meshes. Runtime mesh readback is disabled in the game build.
    // The live mesh transforms (including animated points) supply world pose.
    internal static class TurnoutRailSurface
    {
        private sealed class Surface
        {
            internal MeshFilter Fixed, Moving;
            internal Mesh FixedMesh, MovingMesh;
            internal bool Alive { get { return Fixed != null && Moving != null &&
                Fixed.sharedMesh == FixedMesh && Moving.sharedMesh == MovingMesh; } }
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RailTrack, Surface> Surfaces =
            new System.Runtime.CompilerServices.ConditionalWeakTable<RailTrack, Surface>();

        private static Surface Find(RailTrack track)
        {
            if (track == null || track.transform.parent == null) return null;
            // Native Junction membership survives track renaming and list order.
            Junction junction = track.inJunction ?? track.outJunction;
            if (junction == null || junction.transform.parent != track.transform.parent || junction.outBranches == null) return null;
            bool member = false;
            foreach (Junction.Branch b in junction.outBranches) if (b != null && b.track == track) member = true;
            if (!member) return null;
            Transform root = track.transform.parent;
            Transform fixedT = root.Find("Graphical/rails_static"), movingT = root.Find("Graphical/rails_moving");
            if (fixedT == null || movingT == null) return null;
            MeshFilter fixedF = fixedT.GetComponent<MeshFilter>(), movingF = movingT.GetComponent<MeshFilter>();
            Mesh fixedM = fixedF == null ? null : fixedF.sharedMesh, movingM = movingF == null ? null : movingF.sharedMesh;
            if (fixedM == null || movingM == null || fixedM.name != "rails_static" || fixedM.vertexCount != 1370 ||
                movingM.name != "rails_moving" || movingM.vertexCount != 2146 ||
                (fixedM.bounds.min - new Vector3(-0.8185022f, -0.0000026f, -12.0984354f)).sqrMagnitude > 0.000001f ||
                (fixedM.bounds.max - new Vector3(2.6425574f, 0.1520014f, 12.0010166f)).sqrMagnitude > 0.000001f ||
                (movingM.bounds.min - new Vector3(-1.3224802f, 0.0000027f, -2.0088263f)).sqrMagnitude > 0.000001f ||
                (movingM.bounds.max - new Vector3(0.3716023f, 0.1520048f, 20.2212219f)).sqrMagnitude > 0.000001f) return null;
            return new Surface {Fixed=fixedF, Moving=movingF, FixedMesh=fixedM, MovingMesh=movingM};
        }

        internal static bool TryGet(RailTrack track, Vector3 nominal, Vector3 forward, Vector3 up,
            out Vector3 position, out Vector3 tangent)
        {
            position = nominal; tangent = forward;
            // Ordinary procedural tracks and their physics keep the existing path.
            if (track == null || (track.inJunction == null && track.outJunction == null)) return false;
            Surface surface;
            if (!Surfaces.TryGetValue(track, out surface) || !surface.Alive)
            {
                Surfaces.Remove(track);
                surface = Find(track);
                if (surface == null) return false;
                Surfaces.Add(track, surface);
            }
            float lateral, height;
            RailPlacement.TryGetRailGeometry(track, out lateral, out height);
            // Association gate is narrower than half the distance to the other
            // rail. It rejects displaced/replaced tracks, not a position clamp.
            float best = lateral * lateral * 0.25f;
            bool found = Sample(surface.Fixed.transform, StaticCrowns, nominal, forward, up, ref best, ref position, ref tangent);
            found |= Sample(surface.Moving.transform, MovingCrowns, nominal, forward, up, ref best, ref position, ref tangent);
            return found;
        }

        private static bool Sample(Transform mesh, Vector3[][] paths, Vector3 nominal, Vector3 forward, Vector3 up,
            ref float best, ref Vector3 position, ref Vector3 tangent)
        {
            Vector3 local = mesh.InverseTransformPoint(nominal);
            bool found = false;
            foreach (Vector3[] path in paths)
            {
                if (local.z < path[0].z || local.z > path[path.Length - 1].z) continue;
                int lo = 0, hi = path.Length - 1;
                while (hi - lo > 1) { int mid = (lo + hi) / 2; if (path[mid].z <= local.z) lo = mid; else hi = mid; }
                Vector3 a = path[lo], b = path[hi];
                float t = (local.z - a.z) / (b.z - a.z);
                Vector3 crown = mesh.TransformPoint(a + (b - a) * t);
                // Keep the existing base seating adjustment relative to the
                // measured crown, rather than the simulation centreline.
                Vector3 candidate = crown + up * Settings.RailHeadOffset;
                float score = (candidate - nominal).sqrMagnitude;
                if (score >= best) continue;
                Vector3 direction = (mesh.TransformPoint(b) - mesh.TransformPoint(a)).normalized;
                if (Vector3.Dot(direction, forward) < 0f) direction = -direction;
                // Exclude crosswise cap edges; a support rail must run along the
                // actual wheel route, independently of positive local mesh Z.
                if (Vector3.Dot(direction, forward) < 0.9f) continue;
                best = score; position = candidate; tangent = direction; found = true;
            }
            return found;
        }

        private static readonly Vector3[][] StaticCrowns = new Vector3[][] {
            new Vector3[] {
                new Vector3(1.06022776f, 0.15200037f, -12.09326000f),
                new Vector3(1.07136465f, 0.15200037f, -12.09050000f),
                new Vector3(1.08262554f, 0.15200037f, -12.08773000f),
                new Vector3(1.00540169f, 0.15200037f, -11.45024000f),
                new Vector3(0.93004849f, 0.15200037f, -10.82576000f),
                new Vector3(0.82198995f, 0.15200037f, -9.92468000f),
                new Vector3(0.81447467f, 0.15200037f, -9.86179000f),
                new Vector3(0.79848193f, 0.15200035f, -9.72745000f),
                new Vector3(0.78839780f, 0.15200035f, -9.61464000f),
                new Vector3(0.76416560f, 0.15200035f, -9.34248000f),
                new Vector3(0.75256636f, 0.15200037f, -9.14765000f),
            },
            new Vector3[] {
                new Vector3(0.75249785f, 0.15199791f, -12.00000000f),
                new Vector3(0.75249821f, 0.15199822f, -9.86179000f),
                new Vector3(0.75243739f, 0.15199824f, -9.72744000f),
                new Vector3(0.74102658f, 0.15199827f, -9.34247000f),
                new Vector3(0.74104807f, 0.15199834f, -9.14948000f),
                new Vector3(0.75244816f, 0.15199837f, -9.14765000f),
            },
            new Vector3[] {
                new Vector3(-0.75250220f, 0.15199749f, -12.00000000f),
                new Vector3(-0.75249755f, 0.15200098f, 12.00000000f),
            },
            new Vector3[] {
                new Vector3(2.55427908f, 0.15199849f, -11.91227000f),
                new Vector3(2.56546444f, 0.15199849f, -11.90950000f),
                new Vector3(2.57669048f, 0.15199849f, -11.90674000f),
                new Vector3(2.49951897f, 0.15199856f, -11.26959000f),
                new Vector3(2.42425479f, 0.15199864f, -10.64585000f),
                new Vector3(2.35087702f, 0.15199870f, -10.03498000f),
                new Vector3(2.27937018f, 0.15199876f, -9.43657000f),
                new Vector3(2.20972093f, 0.15199885f, -8.85023000f),
                new Vector3(2.14191651f, 0.15199889f, -8.27559000f),
                new Vector3(2.07593634f, 0.15199897f, -7.71219000f),
                new Vector3(2.01177079f, 0.15199903f, -7.15968000f),
                new Vector3(1.94940529f, 0.15199910f, -6.61766000f),
                new Vector3(1.88882219f, 0.15199915f, -6.08571000f),
                new Vector3(1.83000735f, 0.15199922f, -5.56344000f),
                new Vector3(1.77294917f, 0.15199928f, -5.05047000f),
                new Vector3(1.71762865f, 0.15199934f, -4.54638000f),
                new Vector3(1.66403187f, 0.15199943f, -4.05077000f),
                new Vector3(1.61214452f, 0.15199946f, -3.56325000f),
                new Vector3(1.56195253f, 0.15199952f, -3.08343000f),
                new Vector3(1.51343872f, 0.15199961f, -2.61090000f),
                new Vector3(1.46658847f, 0.15199967f, -2.14525000f),
                new Vector3(1.42138718f, 0.15199974f, -1.68610000f),
                new Vector3(1.37781963f, 0.15199980f, -1.23305000f),
                new Vector3(1.33587026f, 0.15199985f, -0.78569000f),
                new Vector3(1.29552101f, 0.15199991f, -0.34360000f),
                new Vector3(1.25675849f, 0.15199997f, 0.09360000f),
                new Vector3(1.21956648f, 0.15200003f, 0.52632000f),
                new Vector3(1.18393002f, 0.15200007f, 0.95496000f),
                new Vector3(1.14983055f, 0.15200013f, 1.37995000f),
                new Vector3(1.11725798f, 0.15200020f, 1.80163000f),
                new Vector3(1.08619163f, 0.15200025f, 2.22047000f),
                new Vector3(1.05661694f, 0.15200029f, 2.63685000f),
                new Vector3(1.02851907f, 0.15200037f, 3.05118000f),
                new Vector3(1.00188074f, 0.15200046f, 3.46387000f),
                new Vector3(0.97668814f, 0.15200047f, 3.87533000f),
                new Vector3(0.95292361f, 0.15200055f, 4.28596000f),
                new Vector3(0.93057497f, 0.15200055f, 4.69617000f),
                new Vector3(0.90962261f, 0.15200064f, 5.10638000f),
                new Vector3(0.89005614f, 0.15200065f, 5.51697000f),
                new Vector3(0.87185673f, 0.15200076f, 5.92835000f),
                new Vector3(0.85501170f, 0.15200077f, 6.34094000f),
                new Vector3(0.83950371f, 0.15200083f, 6.75515000f),
                new Vector3(0.82531985f, 0.15200087f, 7.17140000f),
                new Vector3(0.81244381f, 0.15200093f, 7.59009000f),
                new Vector3(0.80086319f, 0.15200099f, 8.01160000f),
                new Vector3(0.79056146f, 0.15200105f, 8.43638000f),
                new Vector3(0.78152571f, 0.15200108f, 8.86479000f),
                new Vector3(0.77374079f, 0.15200111f, 9.29726000f),
                new Vector3(0.76719205f, 0.15200119f, 9.73421000f),
                new Vector3(0.76186619f, 0.15200125f, 10.17603000f),
                new Vector3(0.75774853f, 0.15200128f, 10.62310000f),
                new Vector3(0.75482480f, 0.15200134f, 11.07586000f),
                new Vector3(0.75308119f, 0.15200137f, 11.53469000f),
                new Vector3(0.75369092f, 0.15200143f, 12.00091000f),
                new Vector3(0.76550597f, 0.15200143f, 12.00094000f),
                new Vector3(0.76550597f, 0.15200143f, 12.00096000f),
            },
        };
        private static readonly Vector3[][] MovingCrowns = new Vector3[][] {
            new Vector3[] {
                new Vector3(0.28324326f, 0.15200402f, -2.00062000f),
                new Vector3(0.29442644f, 0.15200404f, -1.99812000f),
                new Vector3(0.30569194f, 0.15200402f, -1.99562000f),
                new Vector3(0.07596349f, 0.15200405f, -0.10092000f),
                new Vector3(0.06995168f, 0.15200411f, 0.02695000f),
                new Vector3(0.06995166f, 0.15200412f, 15.38488000f),
                new Vector3(0.06447265f, 0.15200409f, 15.80333000f),
                new Vector3(0.05928309f, 0.15200406f, 16.22405000f),
                new Vector3(0.05103321f, 0.15200406f, 17.08157000f),
                new Vector3(0.03604767f, 0.15200402f, 18.39065000f),
                new Vector3(0.02545543f, 0.15200400f, 19.74867000f),
                new Vector3(0.02380957f, 0.15200402f, 19.99043000f),
                new Vector3(0.03002619f, 0.15200396f, 20.18034000f),
                new Vector3(0.03260499f, 0.15200390f, 20.21534000f),
            },
            new Vector3[] {
                new Vector3(-0.05099606f, 0.15200260f, -1.99845000f),
                new Vector3(-0.06224818f, 0.15200263f, -1.99733000f),
                new Vector3(-0.07368539f, 0.15200265f, -1.99621000f),
                new Vector3(-0.06836334f, 0.15200277f, -1.24511000f),
                new Vector3(-0.06296557f, 0.15200292f, -0.36268000f),
                new Vector3(-0.06997549f, 0.15200296f, -0.02634000f),
                new Vector3(-0.07393506f, 0.15200306f, 0.08968000f),
                new Vector3(-0.07430909f, 0.15200307f, 0.09481000f),
                new Vector3(-0.10034655f, 0.15200311f, 0.32895000f),
                new Vector3(-0.16178621f, 0.15200318f, 0.88330000f),
                new Vector3(-0.22149636f, 0.15200323f, 1.42731000f),
                new Vector3(-0.27949017f, 0.15200329f, 1.96138000f),
                new Vector3(-0.33577886f, 0.15200335f, 2.48590000f),
                new Vector3(-0.39037728f, 0.15200341f, 3.00130000f),
                new Vector3(-0.44329662f, 0.15200344f, 3.50796000f),
                new Vector3(-0.49454859f, 0.15200353f, 4.00628000f),
                new Vector3(-0.54414626f, 0.15200354f, 4.49667000f),
                new Vector3(-0.59210203f, 0.15200359f, 4.97953000f),
                new Vector3(-0.63842974f, 0.15200366f, 5.45527000f),
                new Vector3(-0.68313822f, 0.15200371f, 5.92427000f),
                new Vector3(-0.72624161f, 0.15200377f, 6.38694000f),
                new Vector3(-0.76775381f, 0.15200380f, 6.84370000f),
                new Vector3(-0.80768430f, 0.15200385f, 7.29493000f),
                new Vector3(-0.84604396f, 0.15200390f, 7.74101000f),
                new Vector3(-0.88284709f, 0.15200394f, 8.18236000f),
                new Vector3(-0.91810546f, 0.15200400f, 8.61939000f),
                new Vector3(-0.95182921f, 0.15200403f, 9.05247000f),
                new Vector3(-0.98403027f, 0.15200406f, 9.48199000f),
                new Vector3(-1.01472383f, 0.15200414f, 9.90840000f),
                new Vector3(-1.04391694f, 0.15200414f, 10.33204000f),
                new Vector3(-1.07162478f, 0.15200421f, 10.75333000f),
                new Vector3(-1.09785678f, 0.15200424f, 11.17266000f),
                new Vector3(-1.12262666f, 0.15200430f, 11.59041000f),
                new Vector3(-1.14594383f, 0.15200433f, 12.00698000f),
                new Vector3(-1.16782429f, 0.15200438f, 12.42278000f),
                new Vector3(-1.18827604f, 0.15200439f, 12.83819000f),
                new Vector3(-1.20731372f, 0.15200445f, 13.25359000f),
                new Vector3(-1.22494824f, 0.15200448f, 13.66940000f),
                new Vector3(-1.24119433f, 0.15200450f, 14.08601000f),
                new Vector3(-1.25606175f, 0.15200451f, 14.50382000f),
                new Vector3(-1.26956508f, 0.15200457f, 14.92319000f),
                new Vector3(-1.28170561f, 0.15200461f, 15.34453000f),
                new Vector3(-1.28947883f, 0.15200457f, 15.76822000f),
                new Vector3(-1.29631313f, 0.15200463f, 16.19468000f),
                new Vector3(-1.29927338f, 0.15200467f, 16.62429000f),
                new Vector3(-1.30177100f, 0.15200467f, 17.05746000f),
                new Vector3(-1.30130560f, 0.15200463f, 17.49455000f),
                new Vector3(-1.29997283f, 0.15200461f, 17.93596000f),
                new Vector3(-1.29746509f, 0.15200466f, 18.38210000f),
                new Vector3(-1.29518814f, 0.15200471f, 18.83340000f),
                new Vector3(-1.29256961f, 0.15200470f, 19.29021000f),
                new Vector3(-1.29077824f, 0.15200473f, 19.75298000f),
                new Vector3(-1.29097105f, 0.15200475f, 19.97927000f),
                new Vector3(-1.29283174f, 0.15200453f, 20.20537000f),
                new Vector3(-1.29232528f, 0.15200451f, 20.22120000f),
            },
        };
    }


    internal static class ShoeHoldingPatch
    {
        // Only actual holding constraints belong here. An unrelated bogie does
        // one dictionary lookup, regardless of station or world shoe count.
        private static readonly Dictionary<Bogie, List<BrakeShoeBehaviour>> holds =
            new Dictionary<Bogie, List<BrakeShoeBehaviour>>();

        internal static void Register(BrakeShoeBehaviour shoe, Bogie bogie)
        {
            List<BrakeShoeBehaviour> list;
            if (!holds.TryGetValue(bogie, out list))
                holds.Add(bogie, list = new List<BrakeShoeBehaviour>(2));
            if (!list.Contains(shoe)) list.Add(shoe);
        }

        internal static void Unregister(BrakeShoeBehaviour shoe, Bogie bogie)
        {
            if (ReferenceEquals(bogie, null)) return;
            List<BrakeShoeBehaviour> list;
            if (!holds.TryGetValue(bogie, out list)) return;
            list.Remove(shoe);
            if (list.Count == 0) holds.Remove(bogie);
        }

        internal static bool Prefix(Bogie __instance, float localZVelocity, ref bool __result)
        {
            List<BrakeShoeBehaviour> list;
            if (!holds.TryGetValue(__instance, out list)) return true;
            for (int i = 0; i < list.Count;)
            {
                BrakeShoeBehaviour shoe = list[i];
                if (shoe == null || !shoe.HasStaticHold)
                {
                    list.RemoveAt(i);
                    continue;
                }
                if (shoe.KeepHeldTraveller(__instance, localZVelocity))
                {
                    __instance.traveller.MoveToSpan(shoe.HeldBogieSpan);
                    __instance.point1 = __instance.traveller.curPoint;
                    __instance.point2 = __instance.traveller.pointSet.points[__instance.point1.index + 1];
                    __result = true;
                    return false;
                }
                // KeepHeldTraveller may release itself from this very list.
                if (i < list.Count && ReferenceEquals(list[i], shoe)) i++;
            }
            if (list.Count == 0) holds.Remove(__instance);
            return true;
        }
    }

    // Exact authored ballast mesh (sharedassets520.assets, mesh 21). The
    // installed turnout has only MeshFilter/Renderer here, so small dropped
    // objects otherwise settle on terrain hidden under this visible surface.
    internal static class TurnoutGroundSupport
    {
        private static Mesh collisionMesh;
        internal static bool Ensure(RailTrack track)
        {
            try { return EnsureCore(track); }
            catch (Exception ex)
            {
                Main.LogAlways("Turnout ballast support failed: " + ex.Message);
                return false;
            }
        }

        private static bool EnsureCore(RailTrack track)
        {
            if (track == null || track.transform.parent == null) return false;
            Transform ballast = track.transform.parent.Find("Graphical/ballast");
            if (ballast == null) return false;
            MeshFilter filter = ballast.GetComponent<MeshFilter>();
            Mesh mesh = filter == null ? null : filter.sharedMesh;
            if (mesh == null || mesh.name != "ballast" || mesh.vertexCount != 108 ||
                (mesh.bounds.min - new Vector3(-3.026999f, -1.5404376f, -12.343564f)).sqrMagnitude > 0.000001f ||
                (mesh.bounds.max - new Vector3(4.833934f, -0.05647293f, 12.005175f)).sqrMagnitude > 0.000001f) return false;
            // Respect an existing/replacement collider. One shared support per
            // turnout, created only on an ejection or restore, never per tick.
            Collider existing = ballast.GetComponent<Collider>();
            if (existing != null && existing.enabled && !existing.isTrigger) return true;
            Transform previous = ballast.Find("RailwayBrakeShoe_ground");
            if (previous != null)
            {
                MeshCollider ready = previous.GetComponent<MeshCollider>();
                if (ready != null && ready.enabled && !ready.isTrigger && ready.sharedMesh != null) return true;
                return false;
            }
            int terrainLayer = LayerMask.NameToLayer("Terrain");
            if (terrainLayer < 0) return false;
            if (collisionMesh == null)
            {
                // The shipped mesh is unreadable; this small, measured copy is
                // readable so PhysX can cook mirrored turnout instances too.
                collisionMesh = new Mesh();
                collisionMesh.name = "RailwayBrakeShoe_ballast_contact";
                collisionMesh.hideFlags = HideFlags.HideAndDontSave;
                collisionMesh.vertices = new Vector3[] {
                    new Vector3(-2.1439991f, -0.408484757f, 12.0000038f),
                    new Vector3(-3.026999f, -1.44648504f, -12.0000029f),
                    new Vector3(-3.026999f, -1.44648504f, 12.0000038f),
                    new Vector3(-2.1439991f, -0.408484757f, -12.0000029f),
                    new Vector3(-1.89899874f, -0.197484657f, 12.0000038f),
                    new Vector3(-1.89899874f, -0.197484657f, -12.0000029f),
                    new Vector3(-1.50499892f, -0.0564846098f, 12.0000038f),
                    new Vector3(-1.50499892f, -0.0564846098f, -12.0000029f),
                    new Vector3(-1.04299855f, -0.056473434f, 12.0000038f),
                    new Vector3(-1.04299855f, -0.0564848781f, -12.0000029f),
                    new Vector3(1.43051147e-06f, -0.0564731956f, 12.0000038f),
                    new Vector3(1.43051147e-06f, -0.0564846396f, -12.0000029f),
                    new Vector3(0.865994811f, -0.109878823f, 12.0000038f),
                    new Vector3(0.865994811f, -0.056484431f, -12.0000029f),
                    new Vector3(3.02699804f, -1.44648492f, 12.0051746f),
                    new Vector3(3.02838564f, -0.88212353f, 11.5047388f),
                    new Vector3(2.1439991f, -0.408484697f, 12.0037441f),
                    new Vector3(2.14914441f, -0.202528626f, 10.8537159f),
                    new Vector3(3.03210354f, -0.394811004f, 10.8622742f),
                    new Vector3(2.15191627f, -0.122274473f, 10.442812f),
                    new Vector3(3.03485298f, -0.165100887f, 10.4534206f),
                    new Vector3(3.03820467f, -0.165100813f, 10.1989155f),
                    new Vector3(3.04216456f, -0.165100694f, 9.94283009f),
                    new Vector3(3.04673433f, -0.254546106f, 9.6853466f),
                    new Vector3(1.90415585f, -0.123639271f, 10.8513422f),
                    new Vector3(1.89899921f, -0.197484657f, 12.0033474f),
                    new Vector3(1.90693378f, -0.0927468985f, 10.4398699f),
                    new Vector3(2.16389084f, -0.121184349f, 9.66867733f),
                    new Vector3(3.0577414f, -0.490746349f, 9.30579185f),
                    new Vector3(1.51017404f, -0.05648458f, 10.8475246f),
                    new Vector3(1.50499988f, -0.0564846396f, 12.0027084f),
                    new Vector3(1.51296186f, -0.05648458f, 10.4351387f),
                    new Vector3(1.91893435f, -0.0974306464f, 9.66405106f),
                    new Vector3(2.17498255f, -0.224252105f, 9.28513241f),
                    new Vector3(3.07127047f, -0.747179925f, 8.93476295f),
                    new Vector3(1.04819536f, -0.106484577f, 10.8430481f),
                    new Vector3(1.04300022f, -0.0564729273f, 12.0019588f),
                    new Vector3(0.865994215f, -0.0564729869f, 12.0020313f),
                    new Vector3(0.865994453f, -0.109998882f, 10.8416424f),
                    new Vector3(1.05099463f, -0.106484577f, 10.4295883f),
                    new Vector3(0.865994096f, -0.109998941f, 10.4277267f),
                    new Vector3(1.06308615f, -0.106484532f, 9.64789295f),
                    new Vector3(0.865994215f, -0.109999701f, 9.64453316f),
                    new Vector3(1.95995712f, -0.197484419f, 8.36412811f),
                    new Vector3(2.20482922f, -0.333290458f, 8.37205315f),
                    new Vector3(3.08736801f, -1.04838133f, 8.40061855f),
                    new Vector3(3.11644316f, -1.44648457f, 7.58464479f),
                    new Vector3(1.52500415f, -0.0564845204f, 9.6566143f),
                    new Vector3(1.10440469f, -0.106484413f, 8.33643723f),
                    new Vector3(0.865994692f, -0.109999597f, 8.32908058f),
                    new Vector3(1.5661633f, -0.0564844012f, 8.35138226f),
                    new Vector3(1.49495459f, -0.106484413f, 1.44561625f),
                    new Vector3(0.865994692f, -0.109999597f, 1.39715934f),
                    new Vector3(0.865994692f, -0.109999597f, -3.28839493f),
                    new Vector3(2.59265351f, -0.408484459f, 1.53081799f),
                    new Vector3(3.47300625f, -1.44648468f, 1.59914994f),
                    new Vector3(2.34838796f, -0.197484419f, 1.5118587f),
                    new Vector3(1.95556927f, -0.0564844012f, 1.48136854f),
                    new Vector3(2.09089327f, -0.106484577f, -5.03682518f),
                    new Vector3(3.18597388f, -0.408484638f, -4.92280483f),
                    new Vector3(4.06422615f, -1.4464848f, -4.83135986f),
                    new Vector3(2.94229078f, -0.197484598f, -4.94817734f),
                    new Vector3(2.55040932f, -0.0564845502f, -4.98898029f),
                    new Vector3(3.95731592f, -0.408484697f, -11.722949f),
                    new Vector3(4.83393383f, -1.44648492f, -11.6169748f),
                    new Vector3(3.71408677f, -0.197484657f, -11.7523546f),
                    new Vector3(3.32293463f, -0.0564846396f, -11.7996416f),
                    new Vector3(2.86427402f, -0.0564838648f, -11.8550901f),
                    new Vector3(1.82881308f, -0.0564841926f, -11.9802694f),
                    new Vector3(1.05350089f, -0.126484588f, -5.14484024f),
                    new Vector3(0.865995049f, -0.109999597f, -5.1639967f),
                    new Vector3(0.865995407f, -0.109999597f, -10.7078447f),
                    new Vector3(0.865995169f, -0.0564844608f, -12.0966654f),
                    new Vector3(0.629106402f, -0.106484652f, -10.7362938f),
                    new Vector3(0.793351889f, -0.0564844906f, -12.1054487f),
                    new Vector3(0.334691048f, -0.0564846396f, -12.1608973f),
                    new Vector3(0.170318127f, -0.131386995f, -10.7906761f),
                    new Vector3(-0.0564610958f, -0.197484657f, -12.2081842f),
                    new Vector3(-0.220942736f, -0.272387028f, -10.8370552f),
                    new Vector3(-0.299690008f, -0.408484727f, -12.2375898f),
                    new Vector3(-0.464239359f, -0.483387083f, -10.8658953f),
                    new Vector3(-1.17630768f, -1.44648492f, -12.343564f),
                    new Vector3(3.11644316f, -1.44648457f, 7.58464479f),
                    new Vector3(3.08736801f, -1.04838133f, 8.40061855f),
                    new Vector3(3.8938539f, -1.44648421f, 8.4006176f),
                    new Vector3(3.87775683f, -1.29872203f, 8.93476105f),
                    new Vector3(3.07127047f, -0.747179925f, 8.93476295f),
                    new Vector3(4.68424273f, -1.48764336f, 8.93476105f),
                    new Vector3(3.0577414f, -0.490746349f, 9.30579185f),
                    new Vector3(4.67071342f, -1.41376865f, 9.30578995f),
                    new Vector3(3.86422753f, -1.02622485f, 9.30578995f),
                    new Vector3(3.04673433f, -0.254546106f, 9.6853466f),
                    new Vector3(4.65970564f, -1.36637652f, 9.68534374f),
                    new Vector3(3.85321999f, -0.851413786f, 9.68534565f),
                    new Vector3(3.04216456f, -0.165100694f, 9.94283009f),
                    new Vector3(4.65117741f, -1.35235095f, 10.1989136f),
                    new Vector3(3.8446908f, -0.799678206f, 10.1989136f),
                    new Vector3(3.03820467f, -0.165100813f, 10.1989155f),
                    new Vector3(4.6450758f, -1.37552774f, 10.8622675f),
                    new Vector3(3.03485298f, -0.165100887f, 10.4534206f),
                    new Vector3(3.83858967f, -0.885169387f, 10.8622704f),
                    new Vector3(3.03210354f, -0.394811004f, 10.8622742f),
                    new Vector3(4.64135838f, -1.49102247f, 11.504735f),
                    new Vector3(3.02838564f, -0.88212353f, 11.5047388f),
                    new Vector3(3.83487201f, -1.21874011f, 11.5047369f),
                    new Vector3(4.63996983f, -1.54043758f, 12.0051708f),
                    new Vector3(3.02699804f, -1.44648492f, 12.0051746f),
                    new Vector3(3.8334837f, -1.49346125f, 12.0051727f),
                };
                collisionMesh.triangles = new int[] {
                    0, 1, 2, 0, 3, 1, 4, 3, 0, 4, 5, 3, 6, 5, 4, 6, 7, 5, 8, 7, 6, 8, 9, 7,
                    10, 9, 8, 10, 11, 9, 12, 11, 10, 12, 13, 11, 14, 15, 16, 17, 16, 15, 17, 15, 18, 18, 19, 17,
                    18, 20, 19, 19, 20, 21, 19, 21, 22, 19, 22, 23, 16, 17, 24, 16, 24, 25, 17, 19, 26, 17, 26, 24,
                    23, 27, 19, 27, 23, 28, 25, 24, 29, 25, 29, 30, 24, 26, 31, 24, 31, 29, 19, 32, 26, 19, 27, 32,
                    27, 28, 33, 33, 28, 34, 30, 29, 35, 30, 35, 36, 37, 36, 35, 37, 35, 38, 29, 39, 35, 38, 35, 39,
                    29, 31, 39, 38, 39, 40, 40, 39, 41, 31, 41, 39, 40, 41, 42, 27, 33, 43, 27, 43, 32, 33, 34, 44,
                    33, 44, 43, 34, 45, 44, 45, 46, 44, 31, 47, 41, 26, 47, 31, 26, 32, 47, 42, 41, 48, 47, 48, 41,
                    42, 48, 49, 32, 50, 47, 47, 50, 48, 32, 43, 50, 49, 48, 51, 50, 51, 48, 49, 51, 52, 53, 52, 51,
                    54, 44, 46, 54, 46, 55, 44, 56, 43, 44, 54, 56, 43, 57, 50, 50, 57, 51, 43, 56, 57, 53, 51, 58,
                    57, 58, 51, 55, 59, 54, 55, 60, 59, 54, 61, 56, 54, 59, 61, 56, 62, 57, 57, 62, 58, 56, 61, 62,
                    60, 63, 59, 60, 64, 63, 59, 63, 65, 59, 65, 61, 61, 65, 66, 61, 66, 62, 62, 66, 67, 62, 67, 58,
                    58, 67, 68, 58, 68, 69, 58, 69, 53, 70, 53, 69, 71, 70, 69, 71, 69, 68, 68, 72, 71, 73, 71, 72,
                    73, 72, 74, 73, 74, 75, 73, 75, 76, 76, 75, 77, 76, 77, 78, 78, 77, 79, 78, 79, 80, 80, 79, 81,
                    82, 83, 84, 83, 85, 84, 83, 86, 85, 84, 85, 87, 88, 85, 86, 85, 89, 87, 88, 90, 85, 85, 90, 89,
                    91, 90, 88, 90, 92, 89, 91, 93, 90, 90, 93, 92, 94, 93, 91, 93, 95, 92, 94, 96, 93, 93, 96, 95,
                    94, 97, 96, 96, 98, 95, 99, 96, 97, 96, 100, 98, 99, 100, 96, 99, 101, 100, 102, 98, 100, 103, 100, 101,
                    102, 100, 104, 103, 104, 100, 105, 102, 104, 106, 104, 103, 105, 104, 107, 106, 107, 104,
                };
                collisionMesh.RecalculateBounds();
            }
            GameObject ground = new GameObject("RailwayBrakeShoe_ground");
            ground.hideFlags = HideFlags.DontSave;
            ground.layer = terrainLayer;
            ground.transform.SetParent(ballast, false);
            MeshCollider collider = ground.AddComponent<MeshCollider>();
            collider.sharedMesh = collisionMesh;
            return true;
        }
    }
}
