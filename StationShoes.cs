using System;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RailwayBrakeShoe
{
    // Only new station/passenger generation enters this scope. Save restoration, shops,
    // manual car spawns and job definitions using existing cars cannot enter it.
    internal static class StationShoePatches
    {
        internal sealed class Generation
        {
            internal Generation Parent;
            internal readonly List<SpawnBatch> Batches = new List<SpawnBatch>();
        }

        internal sealed class SpawnBatch
        {
            internal TrainCar[] Cars;
            internal bool FirstEndIsFront;
            internal bool LastEndIsFront;
        }

        [ThreadStatic] private static Generation current;
        internal static bool Enabled = true;
        internal static bool CanCreate { get { return Enabled && Main.Config.AutomaticServiceShoes; } }
        private static bool installed;

        internal static void Install()
        {
            // Own patch owner: failure disables just this integration, without
            // unpatching the established shoe, inventory or job integrations.
            Harmony harmony = new Harmony(Main.Entry.Info.Id + ".StationService");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(StationProceduralJobGenerator), "GenerateJobChain"),
                    prefix: new HarmonyMethod(typeof(StationShoePatches), "GenerationPrefix"),
                    postfix: new HarmonyMethod(typeof(StationShoePatches), "GenerationPostfix"),
                    finalizer: new HarmonyMethod(typeof(StationShoePatches), "GenerationFinalizer"));
                harmony.Patch(AccessTools.Method(typeof(CarSpawner), "SpawnCars"),
                    postfix: new HarmonyMethod(typeof(StationShoePatches), "SpawnPostfix"));
                harmony.Patch(AccessTools.Method(typeof(Coupler), "CoupleTo"),
                    postfix: new HarmonyMethod(typeof(StationShoePatches), "CouplePostfix"));
                // These native collider changes do not all raise the LOD event.
                harmony.Patch(AccessTools.Method(typeof(TrainCarColliders), "TempDisableCollisionColliders"),
                    postfix: new HarmonyMethod(typeof(StationShoePatches), "CollidersPostfix"));
                harmony.Patch(AccessTools.Method(typeof(TrainCarColliders), "SetBogieColliders"),
                    postfix: new HarmonyMethod(typeof(StationShoePatches), "CollidersPostfix"));
                ServiceShoePersistence.Install(harmony);
                installed = true;
                InstallPassengerJobs();
            }
            catch (Exception ex)
            {
                Enabled = false;
                harmony.UnpatchAll(harmony.Id);
                Main.LogAlways("Service shoes: integration unavailable: " + ex.Message);
            }
        }

        internal static void GenerationPrefix(out Generation __state)
        {
            __state = new Generation { Parent = current };
            current = __state;
        }

        private static void InstallPassengerJobs()
        {
            // 5.3 GenerateController is synchronous even though its caller is
            // a coroutine. Capture only native SpawnCars within this scope;
            // reused cars and reloaded jobs create no new batches. Register
            // after a successful finalized job, not its earlier spawn event.
            Type generator = AccessTools.TypeByName("PassengerJobs.Generation.PassengerJobGenerator");
            if (generator == null) return;
            Harmony optional = new Harmony(Main.Entry.Info.Id + ".StationService.PassengerJobs");
            try
            {
                System.Reflection.MethodInfo target = AccessTools.Method(generator, "GenerateController");
                if (target == null || !typeof(JobChainController).IsAssignableFrom(target.ReturnType))
                    throw new InvalidOperationException("Passenger Jobs GenerateController signature unavailable");
                optional.Patch(target,
                    prefix: new HarmonyMethod(typeof(StationShoePatches), "GenerationPrefix"),
                    postfix: new HarmonyMethod(typeof(StationShoePatches), "GenerationPostfix"),
                    finalizer: new HarmonyMethod(typeof(StationShoePatches), "GenerationFinalizer"));
                Main.LogAlways("Service shoes: Passenger Jobs generation integration installed.");
            }
            catch (Exception ex)
            {
                optional.UnpatchAll(optional.Id);
                Main.LogAlways("Service shoes: Passenger Jobs integration unavailable: " + ex.Message);
            }
        }

        internal static void SpawnPostfix(CarSpawner.SpawnData spawnData, List<TrainCar> __result,
            bool playerSpawnedCars, bool uniqueSpawnedCars)
        {
            if (!CanCreate || current == null || __result == null || __result.Count == 0 ||
                playerSpawnedCars || uniqueSpawnedCars) return;
            if (spawnData.carData == null || spawnData.carData.Length != __result.Count) return;
            current.Batches.Add(new SpawnBatch {
                Cars = __result.ToArray(),
                // These are the exact outer-coupler choices in SpawnCars.
                FirstEndIsFront = !spawnData.carData[0].orientationReversed,
                LastEndIsFront = spawnData.carData[__result.Count - 1].orientationReversed
            });
        }

        internal static void GenerationPostfix(JobChainController __result, Generation __state)
        {
            if (!CanCreate || __result == null || __state == null || UnloadWatcher.isUnloading) return;
            try
            {
                foreach (SpawnBatch batch in __state.Batches)
                    StationShoeController.GetOrCreate().Register(batch);
            }
            catch (Exception ex) { Main.LogAlways("Service shoes: station registration failed: " + ex.Message); }
        }

        internal static Exception GenerationFinalizer(Exception __exception, Generation __state)
        {
            if (__state != null) current = __state.Parent;
            return __exception;
        }

        internal static void CouplePostfix(TrainCar __result)
        {
            if (__result == null || StationShoeController.Instance == null) return;
            StationShoeController.Instance.CheckCouplings(__result);
        }

        internal static void CollidersPostfix(TrainCar ___car)
        {
            if (___car != null && StationShoeController.Instance != null)
                StationShoeController.Instance.RefreshContacts(___car);
        }

        internal static void SetEnabled(bool enabled)
        {
            Enabled = enabled && installed;
            if (!enabled && StationShoeController.Instance != null)
                StationShoeController.Instance.ReleaseAll("mod disabled");
        }
    }

    internal sealed class StationShoeController : MonoBehaviour
    {
        internal sealed class End
        {
            internal TrainCar Car, ApproachCar, ProbeCar;
            internal bool Front, Sampled;
            internal Bogie Bogie;
            internal Coupler Coupler;
            internal RailTrack SampleTrack;
            internal double LastSpan, Travel;
            internal float ProbeGap;
        }

        internal sealed class Consist
        {
            internal int Id;
            internal StationShoePatches.SpawnBatch Batch;
            internal string[] Guids;
            internal readonly List<BrakeShoeBehaviour> Shoes = new List<BrakeShoeBehaviour>(4);
            internal bool Created;
            internal bool Released;
            internal float Deadline;
            internal readonly HashSet<TrainCar> WalkVisited = new HashSet<TrainCar>();
            internal readonly Queue<TrainCar> WalkPending = new Queue<TrainCar>();
            internal readonly End[] Ends = new End[2];
            internal int Motion, HiddenEnds;
            internal double MotionAnchor, StopAnchor;
            internal float StopSince = -1f, NextApproach, NextRestore;
            internal readonly Dictionary<TrainCar, ContactScan> ContactScans = new Dictionary<TrainCar, ContactScan>();
        }

        internal sealed class ContactScan
        {
            internal float Time = float.NegativeInfinity;
            internal readonly List<Collider> Colliders = new List<Collider>();
            internal readonly List<Collider> BogieBuffer = new List<Collider>();
        }

        internal static StationShoeController Instance;
        private readonly List<Consist> consists = new List<Consist>();
        private readonly Dictionary<TrainCar, Consist> owners = new Dictionary<TrainCar, Consist>();
        private CarSpawner spawner;
        private int nextId;
        private float nextCheck;
        private Consist[] activeSnapshot = new Consist[0];
        private bool scheduleDirty;
        private readonly HashSet<TrainCar> coupledVisited = new HashSet<TrainCar>();
        private readonly Queue<TrainCar> coupledPending = new Queue<TrainCar>();
        private readonly HashSet<Consist> coupledGroups = new HashSet<Consist>();
        private readonly List<TrainCar> locomotives = new List<TrainCar>();

        internal static StationShoeController GetOrCreate()
        {
            if (Instance != null) return Instance;
            GameObject host = new GameObject("RailwayBrakeShoe_StationService");
            host.transform.SetParent(WorldMover.OriginShiftParent, false);
            Instance = host.AddComponent<StationShoeController>();
            Instance.spawner = CarSpawner.Instance;
            if (Instance.spawner != null)
            {
                Instance.spawner.CarAboutToBeDeleted += Instance.CarDeleted;
                Instance.spawner.CarSpawned += Instance.CarSpawned;
                // Once on controller creation; subsequent additions are events.
                foreach (TrainCar car in Instance.spawner.AllCars) Instance.CarSpawned(car);
            }
            UnloadWatcher.UnloadRequested += Instance.Unload;
            return Instance;
        }

        internal void Register(StationShoePatches.SpawnBatch batch)
        {
            Register(batch, false);
        }

        private void Register(StationShoePatches.SpawnBatch batch, bool released,
            int hidden = 0, int motion = 0, TrainCar approachingFirst = null, TrainCar approachingLast = null)
        {
            if (!released && !StationShoePatches.CanCreate) return;
            if (batch == null || batch.Cars == null || batch.Cars.Length == 0) return;
            HashSet<TrainCar> unique = new HashSet<TrainCar>();
            foreach (TrainCar car in batch.Cars)
            {
                if (car == null || car.IsLoco || car.playerSpawnedCar || !unique.Add(car)) return;
                Consist old;
                if (owners.TryGetValue(car, out old))
                {
                    int index = Array.IndexOf(old.Batch.Cars, car);
                    if (index >= 0 && old.Guids[index] == car.CarGUID) return;
                    // Same pooled Unity object, but a different spawned wagon.
                    CarDeleted(car);
                }
            }
            Consist group = new Consist {
                Id = ++nextId, Batch = batch, Guids = new string[batch.Cars.Length],
                Deadline = Time.time + 30f, Released = released, HiddenEnds = hidden, Motion = motion
            };
            group.Ends[0] = new End { Car = batch.Cars[0], Front = batch.FirstEndIsFront, ApproachCar = approachingFirst };
            group.Ends[1] = new End { Car = batch.Cars[batch.Cars.Length - 1], Front = batch.LastEndIsFront, ApproachCar = approachingLast };
            for (int i = 0; i < batch.Cars.Length; i++)
            {
                TrainCar car = batch.Cars[i];
                group.Guids[i] = car.CarGUID;
                owners.Add(car, group);
                car.OnDestroyCar += CarDeleted;
            }
            consists.Add(group);
            scheduleDirty = true;
            nextCheck = 0f;
            Main.Log("Service shoes: new station consist " + group.Id + ", cars=" + batch.Cars.Length);
            TryCreate(group);
        }

        internal JArray CaptureSnapshot(HashSet<string> savedCars)
        {
            JArray records = new JArray();
            foreach (Consist group in consists)
            {
                bool complete = true;
                for (int i = 0; i < group.Guids.Length; i++)
                    if (!savedCars.Contains(group.Guids[i]) || group.Batch.Cars[i] == null ||
                        group.Batch.Cars[i].CarGUID != group.Guids[i]) { complete = false; break; }
                if (!complete) continue;
                JObject record = new JObject();
                record["cars"] = new JArray(group.Guids);
                record["firstFront"] = group.Batch.FirstEndIsFront;
                record["lastFront"] = group.Batch.LastEndIsFront;
                // Retain released identities too: a previously coupled group
                // must not regain shoes after reloading and uncoupling.
                record["released"] = group.Released || HasLocomotive(group);
                record["hiddenEnds"] = group.HiddenEnds;
                record["motion"] = group.Motion;
                record["approachingFirst"] = group.Ends[0].ApproachCar == null ? null : group.Ends[0].ApproachCar.CarGUID;
                record["approachingLast"] = group.Ends[1].ApproachCar == null ? null : group.Ends[1].ApproachCar.CarGUID;
                records.Add(record);
            }
            return records;
        }

        internal void RestoreSnapshot(JArray records)
        {
            // CarsSaveManager.Load has finished constructing ALL saved cars and
            // restoring their couplings. Their Start/physics may still be pending;
            // Register/TryCreate already waits for the real bogies and axles.
            Dictionary<string, TrainCar> cars = new Dictionary<string, TrainCar>(StringComparer.Ordinal);
            if (spawner == null) return;
            foreach (TrainCar car in spawner.AllCars)
                if (car != null && !string.IsNullOrEmpty(car.CarGUID))
                {
                    if (cars.ContainsKey(car.CarGUID)) cars[car.CarGUID] = null;
                    else cars.Add(car.CarGUID, car);
                }
            foreach (JToken token in records)
            {
                JObject record = token as JObject;
                JArray ids = record == null ? null : record["cars"] as JArray;
                if (ids == null || ids.Count == 0 ||
                    record["firstFront"] == null || record["firstFront"].Type != JTokenType.Boolean ||
                    record["lastFront"] == null || record["lastFront"].Type != JTokenType.Boolean ||
                    record["released"] == null || record["released"].Type != JTokenType.Boolean)
                {
                    Main.LogAlways("Service shoes: invalid saved consist record; skipped.");
                    continue;
                }
                TrainCar[] members = new TrainCar[ids.Count];
                HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
                bool complete = true;
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i].Type == JTokenType.String ? (string)ids[i] : null;
                    if (string.IsNullOrEmpty(id) || !unique.Add(id) ||
                        !cars.TryGetValue(id, out members[i]) || members[i] == null)
                    { complete = false; break; }
                }
                if (!complete)
                {
                    Main.LogAlways("Service shoes: saved consist has missing/duplicate wagon GUIDs; skipped.");
                    continue;
                }
                int hidden = 0, motion = 0;
                if ((record["hiddenEnds"] != null && (record["hiddenEnds"].Type != JTokenType.Integer ||
                    (double)record["hiddenEnds"] < 0 || (double)record["hiddenEnds"] > 2)) ||
                    (record["motion"] != null && (record["motion"].Type != JTokenType.Integer ||
                    (double)record["motion"] < -1 || (double)record["motion"] > 1)))
                {
                    Main.LogAlways("Service shoes: invalid saved end state; skipped.");
                    continue;
                }
                if (record["hiddenEnds"] != null) hidden = (int)record["hiddenEnds"];
                if (record["motion"] != null) motion = (int)record["motion"];
                TrainCar approachingFirst = ResolveApproachCar(record["approachingFirst"], cars);
                TrainCar approachingLast = ResolveApproachCar(record["approachingLast"], cars);
                Register(new StationShoePatches.SpawnBatch { Cars = members,
                    FirstEndIsFront = (bool)record["firstFront"], LastEndIsFront = (bool)record["lastFront"] },
                    (bool)record["released"], hidden, motion, approachingFirst, approachingLast);
            }
        }

        private void FixedUpdate()
        {
            if (scheduleDirty)
            {
                // Snapshot changes only with lifecycle, never on every tick.
                activeSnapshot = consists.FindAll(group => !group.Released).ToArray();
                scheduleDirty = false;
            }
            // One cheap motion state machine per active consist. No wheel search
            // or Physics query in this loop; lifecycle checks remain infrequent.
            foreach (Consist group in activeSnapshot)
                if (group.Created && !group.Released)
                    try { UpdateMotion(group); }
                    catch (Exception ex)
                    {
                        Release(group, "invalidated motion state");
                        Main.LogAlways("Service shoes: consist " + group.Id + " motion check failed: " + ex.Message);
                    }
            if (Time.time < nextCheck) return;
            bool pending = false;
            foreach (Consist group in activeSnapshot)
            {
                if (group.Released) continue;
                try
                {
                    if (!MembersAlive(group)) { Release(group, "wagon removed"); continue; }
                    if (HasLocomotive(group)) { Release(group, "locomotive coupled"); continue; }
                    if (!group.Created) TryCreate(group);
                    if (!group.Created && !group.Released) pending = true;
                }
                catch (Exception ex)
                {
                    Release(group, "invalidated spawn");
                    Main.LogAlways("Service shoes: consist " + group.Id + " check failed: " + ex.Message);
                }
            }
            // Coupling/deletion events are immediate. This is a recovery net
            // for missed external changes, with a faster bounded spawn retry.
            nextCheck = Time.time + (pending ? 0.1f : 2f);
        }

        private bool MembersAlive(Consist group)
        {
            for (int i = 0; i < group.Batch.Cars.Length; i++)
            {
                TrainCar car = group.Batch.Cars[i];
                if (car == null || !car.gameObject.activeInHierarchy || car.CarGUID != group.Guids[i] ||
                    (spawner != null && spawner.IsCarInPool(car))) return false;
            }
            return true;
        }

        private void TryCreate(Consist group)
        {
            if (group.Created || group.Released || !StationShoePatches.Enabled) return;
            if (!StationShoePatches.CanCreate) { Release(group, "automatic placement disabled"); return; }
            if (!MembersAlive(group)) { Release(group, "spawn cancelled"); return; }
            if (HasLocomotive(group)) { Release(group, "already coupled to locomotive"); return; }
            if (Time.time > group.Deadline)
            {
                Main.LogAlways("Service shoes: consist " + group.Id + " has no ready rail axles; skipped.");
                Release(group, "initialization timeout");
                return;
            }
            foreach (TrainCar car in group.Batch.Cars)
                if (car.Bogies == null || car.Bogies.Length == 0 || !car.AreBogiesFullyInitialized()) return;

            ServiceShoePlacement first, last;
            TrainCar[] cars = group.Batch.Cars;
            if (!ServiceShoePlacement.TryResolve(cars[0], group.Batch.FirstEndIsFront, out first) ||
                !ServiceShoePlacement.TryResolve(cars[cars.Length - 1], group.Batch.LastEndIsFront, out last)) return;
            if (first.Axle == last.Axle) return;
            try
            {
                // Resolve both ends before creating anything. A failure during
                // creation removes the whole partial set, never leaves 1-3 shoes.
                AddPair(group, first);
                AddPair(group, last);
                BindEnd(group.Ends[0], first);
                BindEnd(group.Ends[1], last);
                for (int i = 0; i < 2; i++)
                    if ((group.HiddenEnds & (1 << i)) != 0) HidePair(group, i);
                group.Created = true;
                Main.Log("Service shoes: consist " + group.Id + " created exactly four shoes.");
            }
            catch (Exception ex)
            {
                Release(group, "creation failed");
                Main.LogAlways("Service shoes: creation failed for consist " + group.Id + ": " + ex.Message);
            }
        }

        private static TrainCar ResolveApproachCar(JToken id, Dictionary<string, TrainCar> cars)
        {
            TrainCar car;
            // An in-memory save can contain JValue((string)null), whose Type
            // is String until JSON is parsed. Dictionary keys cannot be null.
            string guid = id != null && id.Type == JTokenType.String ? (string)id : null;
            return !string.IsNullOrEmpty(guid) && cars.TryGetValue(guid, out car) &&
                car != null && car.IsLoco ? car : null;
        }

        private void CarSpawned(TrainCar car)
        {
            if (car != null && car.IsLoco && !locomotives.Contains(car)) locomotives.Add(car);
        }

        private static void BindEnd(End end, ServiceShoePlacement placement)
        {
            end.Bogie = placement.Bogie;
            end.Coupler = end.Front ? end.Car.frontCoupler : end.Car.rearCoupler;
            end.SampleTrack = placement.Bogie.track;
            end.LastSpan = placement.Bogie.traveller.Span;
            end.Sampled = true;
        }

        private static bool SampleEnd(End end, int index, out double moved)
        {
            moved = 0.0;
            Bogie bogie = end.Bogie;
            if (end.Car == null || bogie == null || bogie.HasDerailed || bogie.rb == null ||
                bogie.track == null || bogie.traveller == null) return false;
            double span = bogie.traveller.Span;
            // + means toward the originally registered first outer end. This
            // local mapping remains valid with reversed cars and curved tracks.
            double sign = bogie.TrackDirectionSign * (end.Front ? 1.0 : -1.0) * (index == 0 ? 1.0 : -1.0);
            if (end.Sampled && end.SampleTrack == bogie.track)
                moved = (span - end.LastSpan) * sign;
            end.SampleTrack = bogie.track; end.LastSpan = span; end.Sampled = true;
            end.Travel += moved;
            return true;
        }

        private void UpdateMotion(Consist group)
        {
            double firstDelta, lastDelta;
            bool firstValid = SampleEnd(group.Ends[0], 0, out firstDelta);
            bool lastValid = SampleEnd(group.Ends[1], 1, out lastDelta);
            if (!firstValid || !lastValid) { group.StopSince = -1f; return; }
            double travel = (group.Ends[0].Travel + group.Ends[1].Travel) * 0.5;
            double moved = travel - group.MotionAnchor;
            // Actual native rail displacement, not residual solver velocity.
            // Six millimetres also filters bounded +/- sub-millimetre jitter.
            int direction = moved >= 0.006 ? 1 : moved <= -0.006 ? -1 : 0;
            if (direction != 0)
            {
                group.MotionAnchor = travel;
                if (group.Motion != direction)
                {
                    group.Motion = direction;
                    group.StopSince = -1f;
                    End leading = group.Ends[direction > 0 ? 0 : 1];
                    leading.ApproachCar = leading.ProbeCar = null;
                }
            }
            // Traveller displacement is authoritative even when a live holding
            // joint has residual solver velocity on a grade. The dwell and net
            // drift limit below distinguish a stop from continuous slow creep.
            bool quiet = Math.Abs(firstDelta) < 0.02 * Time.fixedDeltaTime &&
                Math.Abs(lastDelta) < 0.02 * Time.fixedDeltaTime;
            if (!quiet || (group.StopSince >= 0f && Math.Abs(travel - group.StopAnchor) > 0.002))
                group.StopSince = -1f;
            else if (group.StopSince < 0f) { group.StopSince = Time.time; group.StopAnchor = travel; }
            else if (Time.time - group.StopSince >= 0.8f && group.Motion != 0)
            {
                group.Motion = 0; group.MotionAnchor = travel; group.NextApproach = 0f;
            }

            if (Time.time >= group.NextApproach)
            {
                group.NextApproach = Time.time + 0.2f;
                for (int end = 0; end < 2; end++)
                {
                    if (group.Motion == (end == 0 ? 1 : -1)) continue;
                    UpdateApproach(group.Ends[end], (group.HiddenEnds & (1 << end)) != 0);
                }
            }
            int hidden = group.Motion > 0 ? 2 : group.Motion < 0 ? 1 :
                (group.Ends[0].ApproachCar != null ? 1 : 0) | (group.Ends[1].ApproachCar != null ? 2 : 0);
            // Two simultaneous approaching locomotives must not remove the
            // whole braking set. Retain the already selected pair of brakes.
            if (hidden == 3) hidden = group.HiddenEnds == 0 ? 1 : group.HiddenEnds;
            // A restored temporary state must survive the game's settling phase;
            // do not immediately recreate shoes under a stationary nearby loco.
            if (group.Motion == 0 && hidden == 0 && group.HiddenEnds != 0 &&
                (group.StopSince < 0f || Time.time - group.StopSince < 0.8f)) return;
            SetHiddenEnds(group, hidden);
        }

        private static Vector3 CouplerPosition(Coupler coupler)
        {
            // Same device point used by the native Coupler distance test.
            return coupler.transform.position + (coupler.visualCoupler != null ? coupler.transform.forward * -0.25f : new Vector3());
        }

        private static bool OnApproachRoute(End end, TrainCar locomotive)
        {
            if (end.Bogie == null || end.Bogie.traveller == null || locomotive.Bogies == null) return false;
            RailTrack track = end.Bogie.track;
            double origin = 0.0;
            float alignment = 1f;
            float outward = end.Bogie.TrackDirectionSign * (end.Front ? 1f : -1f);
            for (int hop = 0; hop < 4 && track != null; hop++)
            {
                foreach (Bogie bogie in locomotive.Bogies)
                    if (bogie != null && !bogie.HasDerailed && bogie.track == track && bogie.traveller != null)
                    {
                        double along = (origin + alignment * bogie.traveller.Span - end.Bogie.traveller.Span) * outward;
                        if (along >= 0.0 && along < 60.0) return true;
                    }
                RailPlacement.SpanMap map;
                if (!RailPlacement.TryGetNeighbour(track, alignment * outward > 0f, out map)) break;
                origin += alignment * map.Origin;
                alignment *= map.Alignment;
                track = map.Track;
            }
            return false;
        }

        private static bool ApproachGap(End end, TrainCar loco, bool latched, out float gap)
        {
            gap = float.PositiveInfinity;
            if (end.Coupler == null || loco == null || !loco.IsLoco || !loco.gameObject.activeInHierarchy ||
                loco.rb == null || end.Bogie == null || end.Bogie.rb == null) return false;
            Vector3 outward = end.Car.transform.forward * (end.Front ? 1f : -1f);
            float closing = -Vector3.Dot(loco.rb.velocity - end.Bogie.rb.velocity, outward);
            float ownClosing = -Vector3.Dot(loco.rb.velocity, outward);
            if (!latched && (closing <= 0.02f || ownClosing <= 0.02f)) return false;
            // Two approach samples at 5 Hz plus a 3 m coupling-device margin.
            float range = 3f + Mathf.Max(0f, closing) * 0.5f;
            if (latched) range = Mathf.Max(4f, range);
            Vector3 position = CouplerPosition(end.Coupler);
            Vector3 right = Vector3.Cross(end.Car.transform.up, outward).normalized;
            for (int i = 0; i < 2; i++)
            {
                Coupler coupler = i == 0 ? loco.frontCoupler : loco.rearCoupler;
                if (coupler == null || CoupledCar(coupler) != null) continue;
                Vector3 otherOutward = loco.transform.forward * (i == 0 ? 1f : -1f);
                if (Vector3.Dot(outward, otherOutward) > -0.5f) continue;
                Vector3 offset = CouplerPosition(coupler) - position;
                float along = Vector3.Dot(offset, outward);
                if (along < (latched ? -3f : -0.5f) || along > range ||
                    Mathf.Abs(Vector3.Dot(offset, right)) > 0.65f ||
                    Mathf.Abs(Vector3.Dot(offset, end.Car.transform.up)) > 0.75f) continue;
                if (!OnApproachRoute(end, loco)) continue;
                gap = along;
                return true;
            }
            return false;
        }

        private void UpdateApproach(End end, bool alreadyHidden)
        {
            float gap;
            // Once admitted, a stopped/pressing locomotive still occupies this
            // end. Reappear only after it clears, not just after its speed falls.
            if (end.ApproachCar != null && ApproachGap(end, end.ApproachCar, true, out gap)) return;
            end.ApproachCar = null;
            for (int i = 0; i < locomotives.Count; i++)
            {
                TrainCar loco = locomotives[i];
                // A locomotive may already be pressing the disabled trailing
                // end at the same speed. Keep that occupied end clear at stop;
                // mere proximity cannot hide an initially active pair.
                if (alreadyHidden && ApproachGap(end, loco, true, out gap) && gap <= 0.5f)
                { end.ApproachCar = loco; end.ProbeCar = null; return; }
                if (!ApproachGap(end, loco, false, out gap)) continue;
                if (end.ProbeCar == loco && gap < end.ProbeGap - 0.002f)
                { end.ApproachCar = loco; end.ProbeCar = null; return; }
                if (end.ProbeCar != loco || gap > end.ProbeGap) { end.ProbeCar = loco; end.ProbeGap = gap; }
                return;
            }
            end.ProbeCar = null;
        }

        private static void HidePair(Consist group, int end)
        {
            for (int i = end * 2; i < end * 2 + 2; i++)
                if (group.Shoes[i] != null) group.Shoes[i].gameObject.SetActive(false);
        }

        private static void SetHiddenEnds(Consist group, int hidden)
        {
            if (hidden == group.HiddenEnds || Time.time < group.NextRestore) return;
            // Restore the new leading pair FIRST on reversal. A failed rail
            // resolution must never remove the only remaining working brakes.
            for (int end = 0; end < 2; end++)
            {
                int bit = 1 << end;
                if ((group.HiddenEnds & bit) == 0 || (hidden & bit) != 0) continue;
                ServiceShoePlacement placement;
                if (!ServiceShoePlacement.TryResolve(group.Ends[end].Car, group.Ends[end].Front, out placement) ||
                    !ServiceShoeFactory.RestorePair(group.Shoes[end * 2], group.Shoes[end * 2 + 1], placement))
                { group.NextRestore = Time.time + 0.25f; return; }
                BindEnd(group.Ends[end], placement);
                group.HiddenEnds &= ~bit;
            }
            for (int end = 0; end < 2; end++)
                if ((hidden & (1 << end)) != 0) HidePair(group, end);
            group.HiddenEnds = hidden;
        }

        private void AddPair(Consist group, ServiceShoePlacement placement)
        {
            Main.Log("Service shoes: consist " + group.Id + ", end car=" + placement.Car.ID +
                ", axle=" + placement.Axle.transform.name + ", track=" + placement.Track.name +
                ", span=" + placement.Span + ", direction=" + placement.Direction);
            foreach (float side in new[] { -1f, 1f })
                group.Shoes.Add(ServiceShoeFactory.Create(transform, placement, side, group.Id));
        }

        // Follow only reciprocal, physically established couplings. In particular,
        // do not use CoupledToOrWithinBreakDistance (which includes nearby cars).
        internal static TrainCar CoupledCar(Coupler coupler)
        {
            if (coupler == null || coupler.coupledTo == null) return null;
            Coupler other = coupler.coupledTo;
            if (other.coupledTo != coupler || (coupler.rigidCJ == null && other.rigidCJ == null)) return null;
            return other.train;
        }

        private static bool HasLocomotive(Consist group)
        {
            return ServiceConsistRules.ReachesLocomotive(group.Batch.Cars,
                car => car != null && car.IsLoco,
                car => car != null ? CoupledCar(car.frontCoupler) : null,
                car => car != null ? CoupledCar(car.rearCoupler) : null,
                group.WalkVisited, group.WalkPending);
        }

        internal void CheckCouplings(TrainCar connected)
        {
            try
            {
                // The successful CoupleTo result is in the changed connected
                // component. Visit that component once, not every station group.
                coupledVisited.Clear();
                coupledPending.Clear();
                coupledGroups.Clear();
                if (connected != null) coupledPending.Enqueue(connected);
                bool locomotive = false;
                while (coupledPending.Count > 0)
                {
                    TrainCar car = coupledPending.Dequeue();
                    if (car == null || !coupledVisited.Add(car)) continue;
                    if (car.IsLoco) locomotive = true;
                    Consist group;
                    if (owners.TryGetValue(car, out group) && !group.Released) coupledGroups.Add(group);
                    TrainCar front = CoupledCar(car.frontCoupler), rear = CoupledCar(car.rearCoupler);
                    if (front != null && !coupledVisited.Contains(front)) coupledPending.Enqueue(front);
                    if (rear != null && !coupledVisited.Contains(rear)) coupledPending.Enqueue(rear);
                }
                if (locomotive)
                    foreach (Consist group in coupledGroups) Release(group, "locomotive coupled");
            }
            catch (Exception ex) { Main.LogAlways("Service shoes: coupling check failed: " + ex.Message); }
            finally { coupledVisited.Clear(); coupledPending.Clear(); coupledGroups.Clear(); }
        }

        internal void RefreshContacts(TrainCar car)
        {
            Consist group;
            if (!owners.TryGetValue(car, out group) || group.Released) return;
            InvalidateContactScan(car);
            foreach (BrakeShoeBehaviour shoe in group.Shoes)
                if (shoe != null && shoe.ServiceCar == car && shoe.ServiceCollision != null)
                    shoe.ServiceCollision.RefreshCandidates();
        }

        internal void InvalidateContactScan(TrainCar car)
        {
            Consist group;
            ContactScan scan;
            if (car != null && owners.TryGetValue(car, out group) && group.ContactScans.TryGetValue(car, out scan))
                scan.Time = float.NegativeInfinity;
        }

        internal bool CopyContactCandidates(TrainCar car, List<Collider> result)
        {
            Consist group;
            if (!owners.TryGetValue(car, out group) || group.Released) return false;
            ContactScan scan;
            if (!group.ContactScans.TryGetValue(car, out scan))
            {
                scan = new ContactScan();
                group.ContactScans.Add(car, scan);
            }
            // Pair members scan on the same tick. Share hierarchy traversal,
            // never the ignore ledger: each shoe still checks every own pair.
            if (scan.Time != Time.fixedTime)
            {
                car.GetComponentsInChildren<Collider>(true, scan.Colliders);
                if (car.Bogies != null)
                    foreach (Bogie bogie in car.Bogies)
                        if (bogie != null && !bogie.transform.IsChildOf(car.transform))
                        {
                            bogie.GetComponentsInChildren<Collider>(true, scan.BogieBuffer);
                            for (int i = 0; i < scan.BogieBuffer.Count; i++) scan.Colliders.Add(scan.BogieBuffer[i]);
                        }
                scan.Time = Time.fixedTime;
            }
            result.Clear();
            // Older List<T>.AddRange implementations allocate a temporary array.
            // Reuse the caller's capacity for these recurring contact scans.
            for (int i = 0; i < scan.Colliders.Count; i++) result.Add(scan.Colliders[i]);
            return true;
        }

        private void Release(Consist group, string reason)
        {
            if (group.Released) return;
            group.Released = true;
            scheduleDirty = true;
            int count = group.Shoes.Count;
            foreach (BrakeShoeBehaviour shoe in group.Shoes)
            {
                if (shoe == null) continue;
                // OnDisable frees the existing holding joint immediately;
                // deferred Unity destruction cannot hold the locomotive one tick.
                shoe.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(shoe.gameObject);
            }
            group.Shoes.Clear();
            group.ContactScans.Clear();
            Main.Log("Service shoes: consist " + group.Id + " removed " + count + " shoes: " + reason);
            // Keep ownership until deletion/unload, so repeated spawn callbacks
            // cannot recreate a set that has already been released by coupling.
        }

        private void CarDeleted(TrainCar car)
        {
            if (ReferenceEquals(car, null)) return;
            if (locomotives.Remove(car))
                foreach (Consist candidate in consists)
                    foreach (End end in candidate.Ends)
                    {
                        if (end.ApproachCar == car) end.ApproachCar = null;
                        if (end.ProbeCar == car) end.ProbeCar = null;
                    }
            Consist group;
            if (!owners.TryGetValue(car, out group)) return;
            Release(group, "wagon despawned");
            car.OnDestroyCar -= CarDeleted;
            owners.Remove(car);
            foreach (TrainCar member in group.Batch.Cars)
            {
                Consist owner;
                if (!ReferenceEquals(member, null) && owners.TryGetValue(member, out owner) && owner == group) return;
            }
            consists.Remove(group);
        }

        internal void ReleaseAll(string reason)
        {
            foreach (Consist group in consists.ToArray()) Release(group, reason);
        }

        private void Unload()
        {
            ReleaseAll("world unloading");
            UnityEngine.Object.Destroy(gameObject);
        }

        private void OnDestroy()
        {
            ReleaseAll("service controller destroyed");
            UnloadWatcher.UnloadRequested -= Unload;
            if (spawner != null) spawner.CarAboutToBeDeleted -= CarDeleted;
            if (spawner != null) spawner.CarSpawned -= CarSpawned;
            locomotives.Clear();
            foreach (TrainCar car in owners.Keys)
                if (car != null) car.OnDestroyCar -= CarDeleted;
            owners.Clear();
            consists.Clear();
            activeSnapshot = new Consist[0];
            if (Instance == this) Instance = null;
        }
    }

    internal static class ServiceShoePersistence
    {
        internal const string SaveKey = "RailwayBrakeShoe.ServiceConsists";

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(CarsSaveManager), "GetCarsSaveData"),
                postfix: new HarmonyMethod(typeof(ServiceShoePersistence), "SavePostfix"));
            harmony.Patch(AccessTools.Method(typeof(CarsSaveManager), "Load"),
                postfix: new HarmonyMethod(typeof(ServiceShoePersistence), "LoadPostfix"));
        }

        internal static void SavePostfix(JObject __result)
        {
            if (__result == null) return;
            try
            {
                // This namespaced sibling belongs to this exact save snapshot.
                // Never alter carsData, item saves, save slots or global settings.
                HashSet<string> saved = new HashSet<string>(StringComparer.Ordinal);
                JArray cars = __result["carsData"] as JArray;
                if (cars == null) return;
                foreach (JToken token in cars)
                {
                    JObject car = token as JObject;
                    JToken id = car == null ? null : car["carGuid"];
                    if (id != null && id.Type == JTokenType.String) saved.Add((string)id);
                }
                JObject state = new JObject();
                state["version"] = 1;
                state["consists"] = StationShoeController.Instance == null ? new JArray() :
                    StationShoeController.Instance.CaptureSnapshot(saved);
                __result[SaveKey] = state;
            }
            catch (Exception ex) { Main.LogAlways("Service shoes: saving consist state failed: " + ex.Message); }
        }

        internal static void LoadPostfix(JObject savedData, bool __result)
        {
            if (!__result || !StationShoePatches.Enabled || savedData == null) return;
            try
            {
                JObject state = savedData[SaveKey] as JObject;
                // Older saves contain no service ownership history. Do not infer
                // it from nearby wagons, jobs or a currently uncoupled locomotive.
                if (state == null) return;
                JToken version = state["version"];
                JArray records = state["consists"] as JArray;
                if (version == null || version.Type != JTokenType.Integer || (int)version != 1 || records == null)
                {
                    Main.LogAlways("Service shoes: unsupported or invalid saved state; skipped.");
                    return;
                }
                if (records.Count > 0) StationShoeController.GetOrCreate().RestoreSnapshot(records);
            }
            catch (Exception ex) { Main.LogAlways("Service shoes: restoring consist state failed: " + ex.Message); }
        }
    }

    // Rail contact already supplies the longitudinal holding/sliding reaction.
    // A second PhysX contact with this wagon's mesh/compound colliders can
    // depenetrate vertically against that rail constraint. Filter only those
    // pairs while this internal shoe is anchored; other shoes still collide.
    internal sealed class ServiceShoeContact : MonoBehaviour
    {
        private sealed class Pair
        {
            internal Collider Shoe, Car;
            internal bool Known, WasIgnored;
        }

        private BrakeShoeBehaviour shoe;
        private TrainCar car;
        private Collider[] sources;
        private readonly HashSet<Collider> targets = new HashSet<Collider>();
        private readonly List<Pair> pairs = new List<Pair>();
        private readonly List<Collider> candidates = new List<Collider>();
        private float nextScan;
        private float nextVerify;
        private bool applied;
        private TrainPhysicsLod lod;
        private TrainCarColliders carColliders;

        internal void Initialize(BrakeShoeBehaviour serviceShoe, TrainCar owner)
        {
            shoe = serviceShoe;
            car = owner;
            sources = GetComponentsInChildren<Collider>(true);
            shoe.ServiceCollision = this;
            Tick(); // before the first physics step and holding joint
        }

        // Called by the existing shoe tick; no second Unity FixedUpdate callback.
        internal void Tick()
        {
            if (shoe == null || !shoe.IsServiceShoe || !shoe.isActiveAndEnabled || !shoe.IsAnchored || car == null)
            {
                if (applied) RestorePairs();
                return;
            }
            if (Time.time >= nextScan)
            {
                Subscribe();
                nextScan = Time.time + (lod != null ? 2f : 0.5f);
                // Cargo/interior rebuilds may destroy old collider instances.
                // Do not retain their wrappers or ever-growing ignore ledgers.
                for (int i = pairs.Count - 1; i >= 0; i--)
                {
                    Pair pair = pairs[i];
                    if (pair.Car == null) targets.Remove(pair.Car);
                    if (pair.Car == null || pair.Shoe == null) pairs.RemoveAt(i);
                }
                bool shared = StationShoeController.Instance != null &&
                    StationShoeController.Instance.CopyContactCandidates(car, candidates);
                if (!shared) car.GetComponentsInChildren<Collider>(true, candidates);
                AddTargets(candidates);
                // The car query already includes nested/inactive bogies. Only
                // bogies reparented outside it need a separate hierarchy scan.
                if (!shared && car.Bogies != null)
                    foreach (Bogie bogie in car.Bogies)
                        if (bogie != null && !bogie.transform.IsChildOf(car.transform))
                        {
                            bogie.GetComponentsInChildren<Collider>(true, candidates);
                            AddTargets(candidates);
                        }
            }
            // Native LOD/cargo events restore filters synchronously. Keep a
            // rare safety check for external changes; if LOD events are not yet
            // available or the shoe is moving, retain the original tick checks.
            if (applied && lod != null && shoe.HasStaticHold && Time.time < nextVerify) return;
            nextVerify = Time.time + 0.5f;
            applied = true;
            foreach (Pair pair in pairs)
            {
                if (!Active(pair.Shoe) || !Active(pair.Car)) continue;
                bool ignored = Physics.GetIgnoreCollision(pair.Shoe, pair.Car);
                if (!pair.Known) { pair.WasIgnored = ignored; pair.Known = true; }
                // Unity drops ignore pairs on collider deactivation. Reassert on
                // reactivation/LOD without re-posing the shoe or applying force.
                if (!ignored) Physics.IgnoreCollision(pair.Shoe, pair.Car, true);
            }
        }

        private void Subscribe()
        {
            if (lod != car.physicsLod)
            {
                if (lod != null) lod.TrainPhysicsLodChanged -= LodChanged;
                lod = car.physicsLod;
                if (lod != null) lod.TrainPhysicsLodChanged += LodChanged;
            }
            if (carColliders != car.carColliders)
            {
                if (carColliders != null) carColliders.CargoCollidersChanged -= CargoChanged;
                carColliders = car.carColliders;
                if (carColliders != null) carColliders.CargoCollidersChanged += CargoChanged;
            }
        }

        internal void Refresh() { nextVerify = 0f; Tick(); }
        internal void RefreshCandidates()
        {
            // TempDisableCollisionColliders changes isTrigger. A collider that
            // was a trigger during the previous scan has no pair ledger yet.
            // Revisit candidates before PhysX sees it as solid again; the
            // controller shares this event's scan between the owner's shoes.
            nextScan = 0f;
            Refresh();
        }
        private void LodChanged(int value) { Refresh(); }
        private void CargoChanged(bool value)
        {
            if (StationShoeController.Instance != null) StationShoeController.Instance.InvalidateContactScan(car);
            nextScan = nextVerify = 0f;
            Tick();
        }

        private void AddTargets(List<Collider> found)
        {
            foreach (Collider target in found)
            {
                if (target == null || target.isTrigger || targets.Contains(target)) continue;
                if (target.GetComponentInParent<BrakeShoeBehaviour>() != null) continue;
                targets.Add(target);
                foreach (Collider source in sources)
                    if (source != null && !source.isTrigger && source != target)
                        pairs.Add(new Pair { Shoe = source, Car = target });
            }
        }

        private static bool Active(Collider collider)
        {
            return collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
        }

        private void RestorePairs()
        {
            applied = false;
            foreach (Pair pair in pairs)
            {
                if (pair.Known && !pair.WasIgnored && Active(pair.Shoe) && Active(pair.Car))
                    Physics.IgnoreCollision(pair.Shoe, pair.Car, false);
                pair.Known = false;
            }
        }

        private void OnEnable() { nextScan = nextVerify = 0f; }
        private void OnDisable()
        {
            if (lod != null) lod.TrainPhysicsLodChanged -= LodChanged;
            if (carColliders != null) carColliders.CargoCollidersChanged -= CargoChanged;
            lod = null; carColliders = null;
            RestorePairs();
        }
        private void OnDestroy() { OnDisable(); }
    }

    internal sealed class ServiceShoePlacement
    {
        internal TrainCar Car;
        internal Bogie Bogie;
        internal Bogie.AxleInfo Axle;
        internal RailTrack Track;
        internal double Span;
        internal float Direction;
        internal double ContactDistance;

        internal static bool TryResolve(TrainCar car, bool frontEnd, out ServiceShoePlacement placement)
        {
            placement = null;
            Bogie selected = null;
            Bogie.AxleInfo outer = null;
            float best = float.NegativeInfinity;
            float end = frontEnd ? 1f : -1f;
            foreach (Bogie bogie in car.Bogies)
            {
                if (bogie == null || !bogie.fullyInitialized || bogie.HasDerailed || bogie.rb == null ||
                    bogie.track == null || bogie.traveller == null) return false;
                Bogie.AxleInfo[] axles = bogie.Axles;
                if (axles == null || axles.Length == 0) return false;
                foreach (Bogie.AxleInfo axle in axles)
                {
                    if (axle == null || axle.transform == null) return false;
                    float outward = car.transform.InverseTransformPoint(axle.transform.position).z * end;
                    if (outward > best) { best = outward; selected = bogie; outer = axle; }
                }
            }
            if (selected == null || outer == null) return false;
            if (selected.TrackDirectionSign == 0f) return false;
            double axleSpan = selected.traveller.Span + outer.distanceFromBogiePivot * selected.TrackDirectionSign;
            Vector3 centre, forward, up;
            RailTrack track = selected.track;
            float direction = 1f;
            if (!TryMapSpan(ref track, ref axleSpan, ref direction)) return false;
            if (!RailPlacement.TryGetPoseAtSpan(track, axleSpan, out centre, out forward, out up)) return false;
            float alignment = Vector3.Dot(forward, car.transform.forward * end);
            if (Mathf.Abs(alignment) < 0.5f) return false;
            direction = alignment > 0f ? 1f : -1f;
            // Use the ordinary shoe's first working-side contact:
            // (axleSpan - shoeSpan) * direction == -CaptureHalfLength.
            // Read that existing setting, not the wheel transform's transient
            // height at spawn; differing end poses must not choose different
            // longitudinal distances. Apply the outward sign exactly once.
            double distance = Main.Config.CaptureHalfLength;
            if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0.0) return false;
            double span = axleSpan + distance * direction;
            if (!TryMapSpan(ref track, ref span, ref direction)) return false;
            placement = new ServiceShoePlacement { Car = car, Bogie = selected, Axle = outer,
                Track = track, Span = span, Direction = direction, ContactDistance = distance };
            return true;
        }

        private static bool TryMapSpan(ref RailTrack track, ref double span, ref float direction)
        {
            for (int i = 0; i < 4; i++)
            {
                double total;
                if (!RailPlacement.TryGetTrackSpan(track, out total)) return false;
                if (span >= 0 && span <= total)
                {
                    return true;
                }
                RailPlacement.SpanMap map;
                if (!RailPlacement.TryGetNeighbour(track, span > total, out map)) return false;
                span = map.Unmap(span);
                direction *= map.Alignment;
                track = map.Track;
            }
            return false;
        }
    }

    internal static class ServiceShoeFactory
    {
        private static GameObject template;

        private static void EnsureTemplate()
        {
            if (template != null) return;
            template = BrakeShoeFactory.CreateCustomItemSource();
        }

        internal static BrakeShoeBehaviour Create(Transform parent, ServiceShoePlacement placement, float side, int id)
        {
            // Bare source, NOT CustomItem.ItemPrefab: the latter adds inventory,
            // grabbing, saving and shop components. The source stays inactive.
            EnsureTemplate();
            GameObject root = UnityEngine.Object.Instantiate(template, parent, false);
            try
            {
                root.name = "RailwayBrakeShoe_Service_" + id;
                root.hideFlags = HideFlags.DontSave;
                BrakeShoeBehaviour shoe = root.GetComponent<BrakeShoeBehaviour>();
                shoe.IsServiceShoe = true; // before Awake/OnEnable on this inactive clone
                shoe.ServiceCar = placement.Car;
                shoe.ServiceContactDistance = placement.ContactDistance;
                Vector3 centre, forward, up;
                if (!RailPlacement.TryGetPoseAtSpan(placement.Track, placement.Span, out centre, out forward, out up))
                    throw new InvalidOperationException("Rail contact pose unavailable");
                Vector3 position = RailPlacement.GetRailheadPosition(placement.Track, centre,
                    Vector3.Cross(up, forward).normalized, up, side);
                Quaternion rotation = Quaternion.LookRotation(forward * placement.Direction, up);
                root.transform.SetPositionAndRotation(position, rotation);
                Rigidbody body = root.GetComponent<Rigidbody>();
                // Set the supported CCD mode before AttachToRail makes this
                // service clone kinematic; otherwise Unity logs a CCD warning.
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.position = position;
                body.rotation = rotation;
                root.SetActive(true);
                shoe.AttachToRail(placement.Track, placement.Direction < 0f, side, placement.Span, true);
                ServiceShoeContact contact = root.AddComponent<ServiceShoeContact>();
                contact.Initialize(shoe, placement.Car);
                shoe.InitializeServiceContact();
                return shoe;
            }
            catch
            {
                root.SetActive(false);
                UnityEngine.Object.Destroy(root);
                throw;
            }
        }

        internal static bool RestorePair(BrakeShoeBehaviour a, BrakeShoeBehaviour b, ServiceShoePlacement placement)
        {
            if (a == null || b == null) return false;
            Vector3 centre, forward, up;
            if (!RailPlacement.TryGetPoseAtSpan(placement.Track, placement.Span, out centre, out forward, out up)) return false;
            Quaternion rotation = Quaternion.LookRotation(forward * placement.Direction, up);
            Vector3 right = Vector3.Cross(up, forward).normalized;
            Vector3 leftPosition = RailPlacement.GetRailheadPosition(placement.Track, centre, right, up, -1f);
            Vector3 rightPosition = RailPlacement.GetRailheadPosition(placement.Track, centre, right, up, 1f);
            // Transition only. Conservative footprint check also catches an
            // ordinary shoe left at the return position while this pair was off.
            float reach = Mathf.Max(0.414f, BrakeShoeFactory.VisualBounds.size.z) + 0.02f;
            foreach (BrakeShoeBehaviour other in Main.Shoes)
            {
                if (other == null || other == a || other == b || !other.isActiveAndEnabled) continue;
                Vector3 delta = other.transform.position - leftPosition;
                Vector3 deltaRight = other.transform.position - rightPosition;
                if (Mathf.Abs(Vector3.Dot(delta, up)) > 0.2f) continue;
                if (Mathf.Abs(Vector3.Dot(delta, forward)) < reach &&
                    (Mathf.Abs(Vector3.Dot(delta, right)) < reach || Mathf.Abs(Vector3.Dot(deltaRight, right)) < reach)) return false;
            }
            try
            {
                Restore(a, placement, -1f, centre, forward, up, rotation);
                Restore(b, placement, 1f, centre, forward, up, rotation);
                return true;
            }
            catch (Exception ex)
            {
                a.gameObject.SetActive(false); b.gameObject.SetActive(false);
                Main.LogAlways("Service shoes: end restoration failed: " + ex.Message);
                return false;
            }
        }

        private static void Restore(BrakeShoeBehaviour shoe, ServiceShoePlacement placement, float side,
            Vector3 centre, Vector3 forward, Vector3 up, Quaternion rotation)
        {
            GameObject root = shoe.gameObject;
            root.SetActive(false);
            Rigidbody body = root.GetComponent<Rigidbody>();
            Vector3 position = RailPlacement.GetRailheadPosition(placement.Track, centre,
                Vector3.Cross(up, forward).normalized, up, side);
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.position = position; body.rotation = rotation;
            root.transform.SetPositionAndRotation(position, rotation);
            shoe.ServiceCar = placement.Car; shoe.ServiceContactDistance = placement.ContactDistance;
            root.SetActive(true);
            shoe.AttachToRail(placement.Track, placement.Direction < 0f, side, placement.Span, true);
            if (shoe.ServiceCollision != null) shoe.ServiceCollision.Refresh();
            shoe.InitializeServiceContact();
        }
    }

    public sealed partial class BrakeShoeBehaviour
    {
        private const double ServiceContactGap = 0.002;
        [NonSerialized] internal bool IsServiceShoe;
        [NonSerialized] internal TrainCar ServiceCar;
        [NonSerialized] internal double ServiceContactDistance;
        [NonSerialized] internal ServiceShoeContact ServiceCollision;
        private HashSet<BrakeShoeBehaviour> servicePushChain;

        private HashSet<BrakeShoeBehaviour> ServicePushChain()
        {
            // Recursive pushes share this root buffer. A new service tick also
            // clears a chain left behind by an interrupted/failed operation.
            if (servicePushChain == null) servicePushChain = new HashSet<BrakeShoeBehaviour>();
            else servicePushChain.Clear();
            return servicePushChain;
        }

        internal void InitializeServiceContact()
        {
            // Same contact selection, latches, capacity and ConfigurableJoint
            // as a player shoe. Run now so a ready spawn need not wait a frame.
            if (IsServiceShoe) UpdateWheelEngagement();
        }
    }

    internal static class ServiceConsistRules
    {
        // Graph traversal is shared with the regression harness. A locomotive
        // coupled through additional wagons counts; spatial neighbours do not.
        internal static bool ReachesLocomotive<T>(IList<T> members, Func<T, bool> isLoco,
            Func<T, T> front, Func<T, T> rear, HashSet<T> visited, Queue<T> pending) where T : class
        {
            visited.Clear();
            pending.Clear();
            try
            {
                for (int i = 0; i < members.Count; i++)
                    if (members[i] != null) pending.Enqueue(members[i]);
                while (pending.Count > 0)
                {
                    T car = pending.Dequeue();
                    if (!visited.Add(car)) continue;
                    if (isLoco(car)) return true;
                    T a = front(car), b = rear(car);
                    if (a != null && !visited.Contains(a)) pending.Enqueue(a);
                    if (b != null && !visited.Contains(b)) pending.Enqueue(b);
                }
                return false;
            }
            finally { visited.Clear(); pending.Clear(); }
        }
    }
}
