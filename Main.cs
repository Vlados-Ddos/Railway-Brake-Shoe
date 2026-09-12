using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityModManagerNet;
using custom_item_mod;

namespace RailwayBrakeShoe
{
    public sealed class Settings : UnityModManager.ModSettings
    {
        public float DryFriction = 0.32f;
        public float WetFrictionMultiplier = 0.55f;
        public float MaximumBrakeForce = 120000f;
        public float BreakawayForce = 100000f;
        // Rail geometry is no longer configurable: the three numbers below were
        // tuned against the game's own rail profile until the shoe sat flat on
        // the railhead, and every value away from them is simply wrong. Leaving
        // them as sliders only offered the player a way to break placement.
        //
        // How far the aim point may be from the nearest RAIL, not from the track
        // centreline. Half a metre is about one railhead width plus a margin, so
        // the preview only turns valid when the player is genuinely pointing at
        // a rail rather than anywhere inside the sleeper bed.
        internal const float RailSnapDistance = 0.5f;
        // Fallback only. The real value is read per track from
        // railType.gauge * 0.5 + railType.railEdgeOffset, which is exactly how
        // RailwayMeshGenerator positions each rail. 1.435 / 2 + 0.0351.
        internal const float RailGaugeHalfWidth = 0.7526f;
        // Trim added on top of the railhead height taken from the rail profile,
        // measured in game against the loaded model.
        internal const float RailHeadOffset = -0.01764706f;
        // Half-length of the shoe's capture window along the rail, in metres.
        // A wheel whose span falls inside this window is riding the shoe.
        public float CaptureHalfLength = 0.22f;
        public bool ReduceHandbrake = true;
        public float HandbrakeMultiplier = 0.4f;
        // Both sounds are mixed through the game's own 3D mixer group, so these
        // are multipliers on top of the player's audio settings rather than an
        // absolute level. Zero silences one sound without touching the other.
        public float PlacementVolume = 1f;
        public float FrictionVolume = 1f;
        // Orientation hazard is sampled while a moving wheel remains against the
        // wrong face. Speed and frog events have their own one-shot/contact rules.
        public bool WrongShoeDerailEnabled = true;
        public float WrongShoeDerailChance = 0.15f;
        // A shoe being carried into a frog always jams there. The derailment
        // roll for each later bogie is fixed at 50% by the 1.2.2 rules.
        // Seconds between hazard rolls while the dangerous contact lasts. Read
        // through a floor at the use site: a zero here would roll on every
        // physics step, which is a certain derailment rather than a chance.
        public float HazardCheckInterval = 3.5f;
        // Speed hazards are one unified contact event with fixed thresholds and
        // probabilities; the toggle only lets a player disable that event.
        public bool SpeedHazardEnabled = true;
        // v1.2.0: Ejection physics when a shoe is knocked off the rail.
        public float EjectionImpulseMin = 1.5f;
        public float EjectionImpulseMax = 3f;
        // Relative to this car's actual full handbrake force, not a fixed mass.
        public float HandbrakeEquivalent = 1f;
        // v1.2.0: Count brake shoes as handbrakes for job completion.
        public bool BrakeShoeCountsAsHandbrake = true;
        public bool DebugLogging = false;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }

    public static class Main
    {
        internal static Settings Config;
        internal static UnityModManager.ModEntry Entry;
        internal static InventoryItemSpec ItemSpec;
        internal static CustomItem CustomItem;
        internal static readonly HashSet<BrakeShoeBehaviour> Shoes = new HashSet<BrakeShoeBehaviour>();
        private static Harmony harmony;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Entry = modEntry;
            // No migration step: the rail geometry values that used to need one
            // are constants now, and XmlSerializer.Deserialize ignores elements
            // that no longer have a field, so a Settings.xml written by an older
            // version loads cleanly and its stale geometry numbers are dropped.
            Config = Settings.Load<Settings>(modEntry);
            modEntry.OnGUI = DrawGui;
            modEntry.OnSaveGUI = delegate(UnityModManager.ModEntry e) { Config.Save(e); };
            modEntry.OnUpdate = Update;
            modEntry.OnToggle = Toggle;

            try
            {
                BrakeShoeFactory.Initialize(modEntry.Path);
                harmony = new Harmony(modEntry.Info.Id);
                PatchIfPresent(typeof(ItemModsFinder), "InitializeItems", typeof(CustomItemRegistration), "InitializeItemsPostfix", HarmonyPatchType.Postfix);
                PatchIfPresent(typeof(DV.Shops.GlobalShopController), "InitializeShopData", typeof(ShopPricePatches), "InitializeShopDataPrefix", HarmonyPatchType.Prefix);
                PatchIfPresent(typeof(DV.Shops.GlobalShopController), "InitializeShopData", typeof(ShopPricePatches), "InitializeShopDataPostfix", HarmonyPatchType.Postfix);
                PatchIfPresent(typeof(DV.Shops.ScanItemCashRegisterModule), "InitializeData", typeof(ShopPricePatches), "RegisterInitializePrefix", HarmonyPatchType.Prefix);
                PatchIfPresent(typeof(DV.Shops.ScanItemCashRegisterModule), "InitializeData", typeof(ShopPricePatches), "RegisterInitializePostfix", HarmonyPatchType.Postfix);
                PatchIfPresent(typeof(DV.Shops.ScanItemCashRegisterModule), "UpdateTexts", typeof(ShopPricePatches), "UpdateTextsPostfix", HarmonyPatchType.Postfix);
                PatchIfPresent(AccessTools.TypeByName("ShopRework.ShopReworkManager"), "RegisterShopItem", typeof(ShopPricePatches), "ShopReworkRegisterPrefix", HarmonyPatchType.Prefix);
                PatchIfPresent(typeof(ItemPlacerNonVr), "UpdateHelperPosition", typeof(PlacementPatches), "UpdateHelperPositionPostfix", HarmonyPatchType.Postfix);
                PatchIfPresent(typeof(ItemPlacerNonVr), "UpdateHelperRotation", typeof(PlacementPatches), "UpdateHelperRotationPrefix", HarmonyPatchType.Prefix);
                PatchIfPresent(typeof(ItemPlacerNonVr), "CheckOverlaps", typeof(PlacementPatches), "CheckOverlapsPrefix", HarmonyPatchType.Prefix);
                PatchIfPresent(typeof(ItemPlacerNonVr), "UpdatePlacement", typeof(PlacementPatches), "UpdatePlacementPostfix", HarmonyPatchType.Postfix);
                PatchIfPresent(typeof(ItemPlacerNonVr), "ResolvePlacement", typeof(PlacementPatches), "ResolvePlacementPrefix", HarmonyPatchType.Prefix);
                PatchIfPresent(typeof(ItemPlacerNonVr), "FinalizePlacement", typeof(PlacementPatches), "FinalizePlacementPostfix", HarmonyPatchType.Postfix);
                PatchIfPresent(typeof(ItemPlacerNonVr), "CancelPlacement", typeof(PlacementPatches), "CancelPlacementPostfix", HarmonyPatchType.Postfix);
                PatchIfPresent(AccessTools.TypeByName("DV.Simulation.Brake.BrakeSystem"), "SimulateBrakingForce", typeof(HandbrakePatch), "Postfix", HarmonyPatchType.Postfix);
                PatchIfPresent(AccessTools.TypeByName("I2.Loc.LocalizationManager"), "GetTranslation", typeof(BrakeShoeLocalization), "GetTranslationPostfix", HarmonyPatchType.Postfix);
                // v1.2.0: Job completion handbrake check. If a car has a brake shoe
                // near its wheels, count it as having handbrake applied.
                PatchIfPresent(typeof(DV.Logic.Job.TransportTask), "UpdateTaskState", typeof(JobHandbrakePatch), "Transpiler", HarmonyPatchType.Transpiler);
                PatchIfPresent(typeof(DV.Logic.Job.TransportTask), "UpdateTaskState", typeof(JobHandbrakePatch), "Postfix", HarmonyPatchType.Postfix);
                PatchIfPresent(typeof(Bogie), "UpdatePointSetTraveller", typeof(ShoeHoldingPatch), "Prefix", HarmonyPatchType.Prefix);
                PatchIfPresent(AccessTools.TypeByName("DV.Storages.LostAndFoundItemsSummoner"), "OnSummonPressed", typeof(ShoeRecoveryPatches), "SummonPrefix", HarmonyPatchType.Prefix);
                PatchIfPresent(AccessTools.TypeByName("DV.Storages.LostAndFoundItemsSummoner"), "OnSummonPressed", typeof(ShoeRecoveryPatches), "SummonFinalizer", HarmonyPatchType.Finalizer);
                PatchIfPresent(typeof(StorageController), "MoveItemsFromWorldToLostAndFound", typeof(ShoeRecoveryPatches), "FilterTranspiler", HarmonyPatchType.Transpiler);
                PatchIfPresent(typeof(RespawnOnDrop), "RespawnOrDestroy", typeof(ShoeRecoveryPatches), "RespawnPrefix", HarmonyPatchType.Prefix);
                ShopStockPatches.Install(harmony);
                modEntry.Logger.Log("Initialized against the current game assemblies. Optional patches fail closed.");
                return true;
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error("Initialization failed: " + ex);
                return false;
            }
        }

        private static bool Toggle(UnityModManager.ModEntry entry, bool enabled)
        {
            foreach (BrakeShoeBehaviour shoe in new List<BrakeShoeBehaviour>(Shoes))
            {
                if (shoe != null) shoe.enabled = enabled;
            }
            return true;
        }

        private static void PatchIfPresent(Type targetType, string targetName, Type patchType, string patchName, HarmonyPatchType kind)
        {
            if (targetType == null)
            {
                Entry.Logger.Warning("Compatibility: target type for " + targetName + " is absent; feature disabled.");
                return;
            }

            MethodInfo target = AccessTools.Method(targetType, targetName);
            MethodInfo patch = AccessTools.Method(patchType, patchName);
            if (target == null || patch == null)
            {
                Entry.Logger.Warning("Compatibility: patch point " + targetType.FullName + "." + targetName + " is absent.");
                return;
            }

            try
            {
                HarmonyMethod hm = new HarmonyMethod(patch);
                if (kind == HarmonyPatchType.Prefix) harmony.Patch(target, prefix: hm);
                else if (kind == HarmonyPatchType.Transpiler) harmony.Patch(target, transpiler: hm);
                else if (kind == HarmonyPatchType.Finalizer) harmony.Patch(target, finalizer: hm);
                else harmony.Patch(target, postfix: hm);
            }
            catch (Exception ex)
            {
                Entry.Logger.Warning("Compatibility: skipped patch " + targetType.FullName + "." + targetName + ": " + ex.Message);
            }
        }

        private static void Update(UnityModManager.ModEntry entry, float dt)
        {
        }

        // Step for the sliders whose range is a 0..1 fraction. Without it the
        // GUI reports values like 0.898, which reads as a measurement rather
        // than a choice; a tenth is as fine as any of these three need to be.
        private const float FractionSliderStep = 0.1f;

        private static void DrawGui(UnityModManager.ModEntry entry)
        {
            string lang = GetCurrentLanguage();
            bool isRussian = lang != null && lang.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

            GUILayout.Label(isRussian ? "Тормозной башмак - физика" : "Railway Brake Shoe - physics");
            Config.DryFriction = Slider(isRussian ? "Трение (сухое)" : "Dry friction", Config.DryFriction, 0.05f, 0.8f, 0.01f);
            Config.WetFrictionMultiplier = Slider(isRussian ? "Множитель (мокрое)" : "Wet multiplier", Config.WetFrictionMultiplier, 0.15f, 1f, 0.01f);
            Config.MaximumBrakeForce = Slider(isRussian ? "Макс. тормозная сила (Н)" : "Maximum brake force (N)", Config.MaximumBrakeForce, 10000f, 1500000f, 10000f);
            Config.BreakawayForce = Slider(isRussian ? "Порог срыва (Н)" : "Breakaway threshold (N)", Config.BreakawayForce, 10000f, 1250000f, 10000f);

            GUILayout.Space(10f);
            GUILayout.Label(isRussian ? "Контакт с колесом" : "Wheel contact");
            Config.CaptureHalfLength = Slider(isRussian ? "Полудлина захвата (м)" : "Capture half-length (m)", Config.CaptureHalfLength, 0.08f, 0.5f, 0f);

            GUILayout.Space(10f);
            GUILayout.Label(isRussian ? "Звук" : "Audio");
            Config.PlacementVolume = Slider(isRussian ? "Громкость установки" : "Placement volume", Config.PlacementVolume, 0f, 1f, FractionSliderStep);
            Config.FrictionVolume = Slider(isRussian ? "Громкость трения" : "Friction volume", Config.FrictionVolume, 0f, 1f, FractionSliderStep);

            GUILayout.Space(10f);
            GUILayout.Label(isRussian ? "Аварийные ситуации" : "Hazards");
            Config.WrongShoeDerailEnabled = GUILayout.Toggle(Config.WrongShoeDerailEnabled, isRussian ? "Сход с рельсов из-за неправильно установленного башмака" : "Derailment from a wrongly placed shoe");
            Config.WrongShoeDerailChance = Slider(isRussian ? "Шанс схода (неправильный башмак)" : "Derail chance (wrong shoe)", Config.WrongShoeDerailChance, 0f, 1f, 0.01f);
            Config.HazardCheckInterval = Slider(isRussian ? "Интервал проверки (с)" : "Check interval (s)", Config.HazardCheckInterval, 1f, 10f, 0.5f);
            Config.SpeedHazardEnabled = GUILayout.Toggle(Config.SpeedHazardEnabled, isRussian ? "Динамический риск на скорости выше 25 км/ч" : "Dynamic speed risk above 25 km/h");

            GUILayout.Space(10f);
            GUILayout.Label(isRussian ? "Сброс башмака с рельса" : "Shoe ejection");
            Config.EjectionImpulseMin = Slider(isRussian ? "Мин. боковой импульс (м/с)" : "Min lateral impulse (m/s)", Config.EjectionImpulseMin, 0.2f, 3f, 0.1f);
            Config.EjectionImpulseMax = Slider(isRussian ? "Макс. боковой импульс (м/с)" : "Max lateral impulse (m/s)", Config.EjectionImpulseMax, 0.5f, 6f, 0.1f);

            GUILayout.Space(10f);
            GUILayout.Label(isRussian ? "Удерживающая сила" : "Holding force");
            Config.HandbrakeEquivalent = Slider(isRussian ? "Эквивалент ручных тормозов" : "Equivalent handbrakes", Config.HandbrakeEquivalent, 0.1f, 2f, 0.1f);

            GUILayout.Space(10f);
            GUILayout.Label(isRussian ? "Прочее" : "Other");
            Config.BrakeShoeCountsAsHandbrake = GUILayout.Toggle(Config.BrakeShoeCountsAsHandbrake, isRussian ? "Башмак заменяет ручник при сдаче заданий" : "Brake shoe counts as handbrake for job completion");
            Config.ReduceHandbrake = GUILayout.Toggle(Config.ReduceHandbrake, isRussian ? "Опционально снизить эффективность ручника" : "Optionally reduce handbrake effectiveness");
            Config.HandbrakeMultiplier = Slider(isRussian ? "Множитель ручника" : "Handbrake multiplier", Config.HandbrakeMultiplier, 0.1f, 1f, FractionSliderStep);
            Config.DebugLogging = GUILayout.Toggle(Config.DebugLogging, isRussian ? "Логирование отладки состояния" : "Debug state logging");

            GUILayout.Space(10f);
            GUILayout.Label(isRussian ? "Требуется custom_item_mod 0.2.x; предмет/магазин/сохранение обеспечивается им." : "Requires custom_item_mod 0.2.x; item/shop/save lifecycle is provided by it.");
        }

        internal static string GetCurrentLanguage()
        {
            try
            {
                Type locAPI = AccessTools.TypeByName("DV.Localization.LocalizationAPI");
                if (locAPI != null)
                {
                    PropertyInfo ccProp = locAPI.GetProperty("CC", BindingFlags.Public | BindingFlags.Static);
                    if (ccProp != null)
                    {
                        System.Globalization.CultureInfo culture = ccProp.GetValue(null, null) as System.Globalization.CultureInfo;
                        if (culture != null) return culture.Name;
                    }
                }
            }
            catch { }
            return null;
        }

        private static float Slider(string label, float value, float min, float max, float step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ": " + value.ToString("0.###"), GUILayout.Width(260f));
            float raw = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(250f));
            if (step > 0f)
            {
                raw = Mathf.Round(raw / step) * step;
                raw = Mathf.Clamp(raw, min, max);
            }
            GUILayout.EndHorizontal();
            return raw;
        }

        internal static void Log(string text)
        {
            if (Config != null && Config.DebugLogging) Entry.Logger.Log(text);
        }

        /// <summary>
        /// For one-off facts worth having in every log, not per-frame traffic.
        /// </summary>
        internal static void LogAlways(string text)
        {
            if (Entry != null && Entry.Logger != null) Entry.Logger.Log(text);
        }
    }

    /// <summary>
    /// The stable surface other mods may call. Everything here answers from the
    /// mod's own engagement state, so a caller never has to measure distances or
    /// know what "correctly placed" means.
    ///
    /// Meant to be used without a hard dependency. A caller that does not want to
    /// reference this assembly can reach the same answers reflectively:
    ///
    ///     Type api = AccessTools.TypeByName("RailwayBrakeShoe.BrakeShoeAPI");
    ///     bool secured = api != null &amp;&amp;
    ///         (bool)api.GetMethod("IsCarSecured").Invoke(null, new object[] { car });
    ///
    /// A null Type means the mod is absent, which is the caller's "no shoe" case.
    /// Nothing here throws: every method is guarded and answers false rather than
    /// propagating an exception into the caller's frame.
    ///
    /// The signatures below are the mod's public contract. Later versions may add
    /// members, but these keep their names, parameters and meanings.
    /// </summary>
    public static class BrakeShoeAPI
    {
        /// <summary>
        /// Version of this API surface, not of the mod. Bumped only if a member
        /// here changes meaning, so a caller can gate on it.
        /// </summary>
        public static int ApiVersion { get { return 1; } }

        /// <summary>
        /// True when at least one shoe is holding this car: correctly placed,
        /// under one of its wheels, and actually able to resist.
        ///
        /// This is the question a mod asking "is this car secured?" wants. It is
        /// the same test the friction code uses to decide a shoe contributes
        /// braking force, so it cannot drift from the physics: a shoe lying the
        /// wrong way round, one merely resting nearby, and one a wheel is rolling
        /// off all answer false.
        /// </summary>
        public static bool IsCarSecured(TrainCar car)
        {
            if (car == null) return false;
            try
            {
                foreach (BrakeShoeBehaviour shoe in Main.Shoes)
                {
                    if (shoe == null) continue;
                    shoe.RefreshJobSecurity(car);
                    if (shoe.SecuredCar == car) return true;
                }
            }
            catch (Exception ex) { Main.Log("API IsCarSecured failed: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// How many shoes are holding this car, for a caller that wants to know
        /// whether both ends are chocked rather than just that one shoe is on.
        /// </summary>
        public static int GetSecuringShoeCount(TrainCar car)
        {
            if (car == null) return 0;
            int count = 0;
            try
            {
                foreach (BrakeShoeBehaviour shoe in Main.Shoes)
                {
                    if (shoe == null) continue;
                    shoe.RefreshJobSecurity(car);
                    if (shoe.SecuredCar == car) count++;
                }
            }
            catch (Exception ex) { Main.Log("API GetSecuringShoeCount failed: " + ex.Message); }
            return count;
        }

        /// <summary>
        /// True when a wheel of this car is touching a shoe at all, however the
        /// shoe is lying. Includes the wrongly placed case, which
        /// <see cref="IsCarSecured"/> deliberately excludes, so a caller can tell
        /// "chocked" from "merely in contact".
        /// </summary>
        public static bool IsCarInShoeContact(TrainCar car)
        {
            if (car == null) return false;
            try
            {
                foreach (BrakeShoeBehaviour shoe in Main.Shoes)
                {
                    if (shoe == null) continue;
                    if (shoe.ContactCar == car) return true;
                }
            }
            catch (Exception ex) { Main.Log("API IsCarInShoeContact failed: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// True when a shoe is placed on a rail within <paramref name="radius"/>
        /// metres of the car's body, whether or not a wheel is on it. For a caller
        /// that wants "is there a shoe by this car" rather than "is it holding".
        /// Only anchored shoes count; one in a hand or a crate is not on the track.
        /// </summary>
        public static bool IsShoeNearCar(TrainCar car, float radius)
        {
            return GetNearestShoeDistance(car) <= Mathf.Max(0f, radius);
        }

        /// <summary>
        /// Distance in metres from the car's body to the nearest anchored shoe, or
        /// float.PositiveInfinity when there is none. Measured to the car's own
        /// bounds, so it does not vary with car length the way a centre-to-shoe
        /// distance would.
        /// </summary>
        public static float GetNearestShoeDistance(TrainCar car)
        {
            if (car == null) return float.PositiveInfinity;
            float best = float.PositiveInfinity;
            try
            {
                foreach (BrakeShoeBehaviour shoe in Main.Shoes)
                {
                    if (shoe == null || !shoe.IsAnchored) continue;
                    // Into the car's local frame first: Bounds is axis-aligned in
                    // local space, and a world-space box around a car sitting at an
                    // angle would reach well past its actual body.
                    Vector3 local = car.transform.InverseTransformPoint(shoe.transform.position);
                    float distance = Mathf.Sqrt(car.Bounds.SqrDistance(local));
                    if (distance < best) best = distance;
                }
            }
            catch (Exception ex) { Main.Log("API GetNearestShoeDistance failed: " + ex.Message); }
            return best;
        }
    }

    internal static class BrakeShoeLocalization
    {
        private const string NameKey = "RailwayBrakeShoe/item_railway_brake_shoe_name";
        private const string DescKey = "RailwayBrakeShoe/item_railway_brake_shoe_desc";

        // Shared with the custom_item_mod registration so the shelf label, the
        // inventory entry and CustomItemInfo cannot drift apart.
        internal const string EnglishName = "Railway Brake Shoe";
        internal const string EnglishDescription =
            "A steel railway brake shoe. Place it on a rail in either direction.";
        private const string RussianName = "Тормозной башмак";
        private const string RussianDescription =
            "Стальной тормозной башмак. Установите на рельс в любом направлении.";

        /// <summary>
        /// Postfix on I2.Loc.LocalizationManager.GetTranslation that answers for
        /// both of this mod's terms.
        ///
        /// It has to supply English as well as Russian, not just override a
        /// translation. custom_item_mod's own prefix opens with
        /// Term.StartsWith(its own mod Id) and returns true for anything else,
        /// so it never reaches the branch that would hand back CustomItem.Name
        /// for a key namespaced to this mod; the untouched original then falls
        /// through to I2.Loc, which has no such term and yields
        /// "[ MISSING TRANSLATION ]" in the shop and the inventory. A postfix
        /// still runs after a skipped or a passed-through original, so filling
        /// the result in here covers every language without depending on the
        /// exact mod Id string another mod happens to be built with.
        ///
        /// Anything other than Russian gets English, which is also what an
        /// unreadable language setting falls back to.
        /// </summary>
        internal static void GetTranslationPostfix(ref string __result, string Term)
        {
            try
            {
                if (Term != NameKey && Term != DescKey) return;

                string lang = Main.GetCurrentLanguage();
                bool russian = lang != null && lang.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

                if (Term == NameKey)
                {
                    __result = russian ? RussianName : EnglishName;
                }
                else
                {
                    __result = russian ? RussianDescription : EnglishDescription;
                }
            }
            catch { }
        }

        internal static void SetLocalizationKeys(InventoryItemSpec spec)
        {
            if (spec == null) return;
            try
            {
                spec.localizationKeyName = NameKey;
                spec.localizationKeyDescription = DescKey;
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Warning("Failed to set localization keys: " + ex.Message);
            }
        }
    }

    internal static class CustomItemRegistration
    {
        internal static void InitializeItemsPostfix()
        {
            try
            {
                foreach (CustomItem existing in ItemModsFinder.CustomItems)
                {
                    if (existing != null && existing.Name == BrakeShoeLocalization.EnglishName)
                    {
                        Main.CustomItem = existing;
                        Main.ItemSpec = existing.ItemSpec;
                        BrakeShoeLocalization.SetLocalizationKeys(Main.ItemSpec);
                        PrepareRuntimePrefab(existing);
                        Main.Entry.Logger.Log("Reused existing custom_item_mod registration.");
                        return;
                    }
                }
                // InitializeItems can run again on a new session; rebuild scene-owned prefab data each time.
                GameObject source = BrakeShoeFactory.CreateCustomItemSource();
                CustomItemInfo info = new CustomItemInfo();
                info.Name = BrakeShoeLocalization.EnglishName;
                info.Description = BrakeShoeLocalization.EnglishDescription;
                // Shop stock, and with it the purchase limit. Traced in IL:
                // CustomItem..ctor copies this to ShopItemData.amount,
                // GlobalShopController.InitializeShopData copies that to
                // initialAmount and then to allowedToHaveAmount, and
                // ShopItemData.ItemsInStock is allowedToHaveAmount minus
                // purchasedItems. Twenty shoes is enough to pin a long rake.
                info.Amount = 20;
                info.Price = (int)ShopPricePatches.BrakeShoePrice;
                info.PreviewRotation = Vector3.zero;
                // The supplied shelf prefab owns its local display pose.
                info.ShelfRotation = Vector3.zero;
                Sprite icon = BrakeShoeFactory.CreateIcon();
                // Build the shelf sample first: it measures the footprint its
                // yawed model actually occupies, and ShelfBounds is then taken
                // from that measurement so the reserved box matches the visual.
                GameObject shelf = BrakeShoeFactory.CreateShelfSource();
                // Verified in custom_item_mod IL: only ShelfBounds.x and .y are
                // read, into ShelfItem.size, where x is the width along the
                // shelf (rejected if longer than the shelf) and y is the depth
                // (rejected outright if deeper than the shelf). Z is ignored.
                // Leaving this zero would make it fall back to the shelf
                // prefab's BoxCollider size, whose y is our height, not depth.
                info.ShelfBounds = new Vector3(
                    BrakeShoeFactory.ShelfFootprint.x,
                    BrakeShoeFactory.ShelfFootprint.y,
                    BrakeShoeFactory.ShelfFootprint.z);
                Main.CustomItem = new CustomItem(info, source, icon, icon, shelf, null, false, false, false);
                Main.ItemSpec = Main.CustomItem.ItemSpec;
                BrakeShoeLocalization.SetLocalizationKeys(Main.ItemSpec);
                PrepareRuntimePrefab(Main.CustomItem);
                if (!ItemModsFinder.CustomItems.Contains(Main.CustomItem)) ItemModsFinder.CustomItems.Add(Main.CustomItem);
                Main.Entry.Logger.Log("Injected into custom_item_mod before its FinalizeItems pass.");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("custom_item_mod item construction failed: " + ex);
            }
        }

        private static void PrepareRuntimePrefab(CustomItem customItem)
        {
            if (customItem == null || customItem.ItemPrefab == null || customItem.ItemSpec == null)
                throw new InvalidOperationException("custom_item_mod did not create the brake-shoe prefab/spec.");
            InventoryItemSpec localSpec = customItem.ItemPrefab.GetComponent<InventoryItemSpec>();
            if (localSpec == null || localSpec != customItem.ItemSpec)
                throw new InvalidOperationException("Brake-shoe InventoryItemSpec is not on the custom item prefab.");

            // custom_item_mod keeps this object below an inactive staging parent.
            // activeSelf must be true so Resources.Load clones run Spec.Item.Awake;
            // that lifecycle callback asks ControlsInstantiator for the single
            // platform-specific ItemBase implementation after cloning.
            customItem.ItemPrefab.SetActive(true);
            ShopPricePatches.EnsureBrakeShoePrice(customItem);
        }

    }

    // Preserves catalogue prices around vanilla's non-Career zeroing, and keeps
    // ShopRework's baseline dictionary current when that mod is installed.
    // Restores the brake shoe price and all other affected catalogue entries.
    internal static class ShopPricePatches
    {
        private static readonly FieldInfo PrefabNameField = AccessTools.Field(typeof(InventoryItemSpec), "itemPrefabName");
        private static readonly Type ShopReworkManagerType = AccessTools.TypeByName("ShopRework.ShopReworkManager");
        private static readonly FieldInfo ShopReworkOriginalPricesField = ShopReworkManagerType == null ? null : AccessTools.Field(ShopReworkManagerType, "originalPricesByKey");
        private static readonly MethodInfo ShopReworkGetShopNameMethod = ShopReworkManagerType == null ? null : AccessTools.Method(ShopReworkManagerType, "GetShopNameFromItem");
        private static readonly Dictionary<string, float> OriginalBasePrices = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static bool refreshingPriceText;
        private static bool loggedGameModeFailure;

        private const float CareerBrakeShoePrice = 250f;

        // Vanilla prices goods in Career only: InitializeShopData zeroes the
        // basePrice of every item that is not careerOnly in any other game mode.
        // The shoe is registered after that pass, so vanilla never reaches it and
        // the price has to be matched to the session here, or the shoe would be
        // the one item on the shelf still carrying a price tag in a sandbox save.
        internal static float BrakeShoePrice
        {
            get { return IsCareerSession ? CareerBrakeShoePrice : 0f; }
        }

        // Read fresh rather than cached: returning to the main menu and loading a
        // save in another mode reuses the process, so a cached answer would price
        // the shoe for the previous session's mode.
        private static bool IsCareerSession
        {
            get
            {
                try
                {
                    DV.UserManagement.UserManager manager = DV.Utils.SingletonBehaviour<DV.UserManagement.UserManager>.Instance;
                    if (manager == null || manager.CurrentUser == null) return true;
                    DV.Common.IGameSession session = manager.CurrentUser.CurrentSession;
                    if (session == null) return true;
                    return session.GameMode == "Career";
                }
                catch (Exception ex)
                {
                    // Fall back to the priced behaviour: charging in a sandbox save
                    // is the lesser fault against handing out free goods in career.
                    if (!loggedGameModeFailure)
                    {
                        loggedGameModeFailure = true;
                        Main.Log("Could not read the session game mode, assuming Career: " + ex.Message);
                    }
                    return true;
                }
            }
        }

        // In non-career sessions vanilla intentionally zeroes every ShopItemData
        // during initialization. ShopRework then caches those zeroes as the
        // "original" prices, so every later reset also restores $0. Capture the
        // serialized catalogue first, but only when ShopRework is installed.
        [HarmonyPriority(Priority.First)]
        internal static void InitializeShopDataPrefix(DV.Shops.GlobalShopController __instance)
        {
            if (!ShopReworkActive || __instance == null || __instance.shopItemsData == null) return;
            for (int i = 0; i < __instance.shopItemsData.Count; i++)
            {
                DV.Shops.ShopItemData data = __instance.shopItemsData[i];
                if (data == null || data.item == null || data.basePrice <= 0f) continue;
                string key = GetItemKey(data.item);
                if (!string.IsNullOrEmpty(key)) OriginalBasePrices[key] = data.basePrice;
            }
        }

        [HarmonyPriority(Priority.Last)]
        internal static void InitializeShopDataPostfix(DV.Shops.GlobalShopController __instance)
        {
            if (__instance == null || __instance.shopItemsData == null) return;
            int restored = 0;
            for (int i = 0; i < __instance.shopItemsData.Count; i++)
            {
                DV.Shops.ShopItemData data = __instance.shopItemsData[i];
                if (data == null || data.item == null) continue;
                if (IsBrakeShoe(data.item))
                {
                    data.basePrice = BrakeShoePrice;
                    continue;
                }

                if (!ShopReworkActive || data.basePrice > 0f) continue;
                float originalPrice;
                if (!OriginalBasePrices.TryGetValue(GetItemKey(data.item), out originalPrice) || originalPrice <= 0f) continue;
                data.basePrice = originalPrice;
                restored++;
            }

            if (ShopReworkActive)
            {
                restored += RefreshLoadedRegisters(__instance);
                RefreshShopReworkBaselines(__instance);
                if (restored > 0) Main.Entry.Logger.Log("ShopRework compatibility restored " + restored + " zeroed shop price(s).");
            }
        }

        [HarmonyPriority(Priority.First)]
        internal static void RegisterInitializePrefix(DV.Shops.ScanItemCashRegisterModule __instance)
        {
            if (!ShopReworkActive || __instance == null) return;
            float basePrice;
            if (TryGetCataloguePrice(__instance.sellingItemSpec, out basePrice))
                UpdateShopReworkBaseline(__instance, basePrice);
        }

        [HarmonyPriority(Priority.Last)]
        internal static void RegisterInitializePostfix(DV.Shops.ScanItemCashRegisterModule __instance)
        {
            if (__instance == null || __instance.sellingItemSpec == null) return;
            bool brakeShoe = IsBrakeShoe(__instance.sellingItemSpec);
            if (brakeShoe) EnsureBrakeShoePrice(Main.CustomItem);
            CashRegisterModule.CashRegisterModuleData data = __instance.Data;
            if (data == null) return;

            float basePrice;
            if (brakeShoe) basePrice = BrakeShoePrice;
            else if (!ShopReworkActive || !TryGetCataloguePrice(__instance.sellingItemSpec, out basePrice)) return;

            // This mod owns the shoe's price outright, so write it either way;
            // for other products only a missing price is filled in.
            if (brakeShoe || data.pricePerUnit <= 0f) data.pricePerUnit = basePrice;
            if (ShopReworkActive) UpdateShopReworkBaseline(__instance, basePrice);
        }

        // Runs immediately before ShopRework records a module. This guarantees
        // that its private baseline dictionary never receives a transient zero.
        [HarmonyPriority(Priority.First)]
        internal static void ShopReworkRegisterPrefix(DV.Shops.ScanItemCashRegisterModule item)
        {
            if (item == null || item.Data == null) return;
            float basePrice;
            if (!TryGetCataloguePrice(item.sellingItemSpec, out basePrice)) return;
            if (item.Data.pricePerUnit <= 0f) item.Data.pricePerUnit = basePrice;
            UpdateShopReworkBaseline(item, basePrice);
        }

        [HarmonyPriority(Priority.Last)]
        internal static void UpdateTextsPostfix(DV.Shops.ScanItemCashRegisterModule __instance)
        {
            if (refreshingPriceText || __instance == null || !IsBrakeShoe(__instance.sellingItemSpec)) return;
            CashRegisterModule.CashRegisterModuleData data = __instance.Data;
            if (data == null) return;

            float price = BrakeShoePrice;
            float previous = data.pricePerUnit;
            data.pricePerUnit = price;
            // ShopRework can cache a zero before this postfix runs. Re-run the
            // vanilla formatter once after correcting the data so the visible
            // register text is refreshed without touching other products.
            if (Mathf.Approximately(previous, price)) return;
            refreshingPriceText = true;
            try { __instance.UpdateTexts(); }
            finally { refreshingPriceText = false; }
        }

        internal static void EnsureBrakeShoePrice(CustomItem customItem)
        {
            if (customItem == null || customItem.ShopData == null) return;
            float price = BrakeShoePrice;
            customItem.ShopData.basePrice = price;
            ShopStockPatches.EnsureGlobalCapacity(customItem.ShopData);
            string key = GetItemKey(customItem.ItemSpec);
            // The cache holds real prices only; a free sandbox shoe must not be
            // remembered as this item's catalogue price.
            if (!string.IsNullOrEmpty(key) && price > 0f) OriginalBasePrices[key] = price;
        }

        private static bool ShopReworkActive
        {
            get { return ShopReworkManagerType != null && ShopReworkOriginalPricesField != null && ShopReworkGetShopNameMethod != null; }
        }

        private static string GetItemKey(InventoryItemSpec spec)
        {
            if (spec == null) return null;
            string prefabName = PrefabNameField == null ? null : PrefabNameField.GetValue(spec) as string;
            return string.IsNullOrEmpty(prefabName) ? spec.name : prefabName;
        }

        private static bool TryGetCataloguePrice(InventoryItemSpec spec, out float price)
        {
            price = 0f;
            if (spec == null) return false;
            if (IsBrakeShoe(spec))
            {
                // False outside Career leaves the shoe at $0 like everything else.
                price = BrakeShoePrice;
                return price > 0f;
            }

            string key = GetItemKey(spec);
            if (!string.IsNullOrEmpty(key) && OriginalBasePrices.TryGetValue(key, out price) && price > 0f) return true;
            DV.Shops.GlobalShopController controller = DV.Shops.GlobalShopController.Instance;
            if (controller == null) return false;
            DV.Shops.ShopItemData shopData = controller.GetShopItemData(spec);
            if (shopData == null || shopData.basePrice <= 0f) return false;
            price = shopData.basePrice;
            if (!string.IsNullOrEmpty(key)) OriginalBasePrices[key] = price;
            return true;
        }

        private static int RefreshLoadedRegisters(DV.Shops.GlobalShopController controller)
        {
            int restored = 0;
            DV.Shops.ScanItemCashRegisterModule[] registers = Resources.FindObjectsOfTypeAll<DV.Shops.ScanItemCashRegisterModule>();
            for (int i = 0; i < registers.Length; i++)
            {
                DV.Shops.ScanItemCashRegisterModule register = registers[i];
                if (register == null || register.sellingItemSpec == null || register.Data == null) continue;
                DV.Shops.ShopItemData shopData = controller.GetShopItemData(register.sellingItemSpec);
                if (shopData == null || shopData.basePrice <= 0f) continue;
                if (register.Data.pricePerUnit <= 0f)
                {
                    register.Data.pricePerUnit = shopData.basePrice;
                    restored++;
                    if (register.gameObject.activeInHierarchy)
                    {
                        try { register.UpdateTexts(); }
                        catch { }
                    }
                }
                UpdateShopReworkBaseline(register, shopData.basePrice);
            }
            return restored;
        }

        private static void UpdateShopReworkBaseline(DV.Shops.ScanItemCashRegisterModule register, float basePrice)
        {
            if (!ShopReworkActive || register == null || basePrice <= 0f) return;
            try
            {
                System.Collections.IDictionary prices = ShopReworkOriginalPricesField.GetValue(null) as System.Collections.IDictionary;
                string shopName = ShopReworkGetShopNameMethod.Invoke(null, new object[] { register }) as string;
                if (prices == null || string.IsNullOrEmpty(shopName) || shopName == "Unknown") return;
                string key = shopName + "::" + register.name;
                object existing = prices.Contains(key) ? prices[key] : null;
                if (existing == null || Convert.ToSingle(existing) <= 0f) prices[key] = basePrice;
            }
            catch (Exception ex)
            {
                Main.Log("ShopRework baseline update failed: " + ex.Message);
            }
        }

        private static void RefreshShopReworkBaselines(DV.Shops.GlobalShopController controller)
        {
            DV.Shops.ScanItemCashRegisterModule[] registers = Resources.FindObjectsOfTypeAll<DV.Shops.ScanItemCashRegisterModule>();
            for (int i = 0; i < registers.Length; i++)
            {
                DV.Shops.ScanItemCashRegisterModule register = registers[i];
                if (register == null || register.sellingItemSpec == null) continue;
                DV.Shops.ShopItemData data = controller.GetShopItemData(register.sellingItemSpec);
                if (data != null && data.basePrice > 0f) UpdateShopReworkBaseline(register, data.basePrice);
            }
        }

        internal static bool IsBrakeShoe(InventoryItemSpec spec)
        {
            if (spec == null) return false;
            if (Main.ItemSpec != null && spec == Main.ItemSpec) return true;
            string prefabName = PrefabNameField == null ? null : PrefabNameField.GetValue(spec) as string;
            return prefabName != null && prefabName.IndexOf("Railway Brake Shoe", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// Loads the two shipped clips and hands them out.
    ///
    /// The clips are plain .wav files next to the model, so they are decoded here
    /// rather than fetched through Resources: a UMM mod has no asset bundle to
    /// load them from. AudioClip.Create plus AudioClip.SetData is the same pair
    /// the game itself uses to build a clip at runtime (DV.Radio.RadioPlayer
    /// creates a streaming clip that way), which keeps this on API the game has
    /// already proven at runtime instead of a web request wrapper.
    ///
    /// Only uncompressed PCM is handled, because that is what both shipped files
    /// are: 44.1 kHz 16-bit, MetalSolid mono and BrakeShoes stereo. A file that
    /// is anything else is reported and skipped rather than half-decoded into
    /// noise.
    /// </summary>
    internal static class BrakeShoeAudio
    {
        internal static AudioClip PlacementClip;
        internal static AudioClip FrictionClip;

        // Item sound distances taken from DV.Items.Brick.BrickAudio.PlayClip,
        // which is the game's own handheld item playing a one-shot: minDistance
        // 0.1, maxDistance 50. Matching them means a shoe is audible over the
        // same range as vanilla item sounds instead of a made-up one.
        internal const float MinDistance = 0.1f;
        internal const float MaxDistance = 50f;

        // Gain baked into the placement clip's samples at load. The volume
        // argument cannot carry this: NAudio.Play hands it straight to
        // AudioSource.volume, which Unity clamps to 1, and the setting is
        // already at 1. MetalSolid peaks at -2.15 dBFS over an RMS of only
        // -22.4 dBFS, so it is a short metallic transient above a quiet body;
        // doubling that body pushes only the handful of samples near the peak
        // past full scale, and those are rounded off by the knee below rather
        // than clipped square.
        private const float PlacementGain = 2f;

        // Level where the soft knee starts bending samples back towards full
        // scale. Under it the gain is applied exactly.
        private const float SoftKneeThreshold = 0.7f;

        internal static void Initialize(string assetRoot)
        {
            PlacementClip = LoadWav(Path.Combine(assetRoot, "MetalSolid.wav"), "RailwayBrakeShoe_Placement",
                PlacementGain);
            FrictionClip = LoadWav(Path.Combine(assetRoot, "BrakeShoes.wav"), "RailwayBrakeShoe_Friction", 1f);
        }

        /// <summary>
        /// Plays a one-shot at the shoe through the game's audio pool.
        ///
        /// NAudio.Play is the game's own entry point for a positional one-shot:
        /// it takes a source from its pool, applies Default3DMixerGroup when no
        /// group is passed, and returns the source to the pool once the clip has
        /// finished. Going through it means the sound obeys the player's audio
        /// settings and does not leak an AudioSource per placement.
        /// </summary>
        internal static void PlayPlacement(Transform at)
        {
            if (PlacementClip == null || at == null) return;
            float volume = Mathf.Clamp01(Main.Config.PlacementVolume);
            if (volume <= 0f) return;
            try
            {
                NAudio.Play(PlacementClip, at.position, volume, 1f, 0f, MinDistance, MaxDistance,
                    default(AudioSourceCurves), null, at, false, 0f, null);
            }
            catch (Exception ex)
            {
                Main.Log("Placement sound skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Builds the looping friction source that lives on the shoe.
        ///
        /// Deliberately not NAudio.CreateSource: that helper also adds a Doppler,
        /// and Doppler.ApplyPitch assigns AudioSource.pitch straight from its ECS
        /// system every update, which would overwrite the slide-speed pitch this
        /// mod sets each physics step. The source is otherwise configured the way
        /// CreateSource configures its own - same mixer group, same 3D settings -
        /// so it still mixes like a game sound.
        /// </summary>
        internal static AudioSource CreateFrictionSource(Transform parent)
        {
            if (FrictionClip == null || parent == null) return null;
            try
            {
                GameObject host = new GameObject("RailwayBrakeShoe_FrictionAudio");
                host.transform.SetParent(parent, false);
                host.transform.localPosition = Vector3.zero;

                AudioSource source = host.AddComponent<AudioSource>();
                source.clip = FrictionClip;
                source.loop = true;
                source.playOnAwake = false;
                source.volume = 0f;
                source.spatialBlend = 1f;
                source.spread = 0f;
                source.minDistance = MinDistance;
                source.maxDistance = MaxDistance;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.outputAudioMixerGroup = NAudio.Default3DMixerGroup;
                return source;
            }
            catch (Exception ex)
            {
                Main.Log("Friction audio source not created: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Decodes a RIFF/WAVE PCM file into an AudioClip.
        ///
        /// Chunks are walked rather than assumed to sit at fixed offsets, because
        /// a WAV may carry LIST/fact chunks before the data. 16-bit is scaled by
        /// 1/32768 and 8-bit is unsigned with a 128 bias, which is how those two
        /// formats are defined; 24- and 32-bit PCM and any compressed format are
        /// refused outright so a wrong file is never played as noise.
        /// </summary>
        /// <summary>
        /// Applies gain to one sample, easing anything that would overshoot back
        /// under full scale instead of letting SetData square it off.
        ///
        /// Below the knee the gain is exact. Above it the remaining span up to
        /// 1 is compressed with a tanh-shaped curve, so the loud transients keep
        /// their relative order and stay continuous with the samples underneath
        /// them; a hard Clamp would flatten them all onto 1 and buzz.
        /// </summary>
        private static float ApplyGain(float sample, float gain)
        {
            float scaled = sample * gain;
            float magnitude = scaled < 0f ? -scaled : scaled;
            if (magnitude <= SoftKneeThreshold) return scaled;

            float headroom = 1f - SoftKneeThreshold;
            float over = (magnitude - SoftKneeThreshold) / headroom;
            // tanh flattens as its argument grows, so this approaches 1 without
            // ever reaching it however hard the sample is driven.
            float eased = SoftKneeThreshold + headroom * (float)Math.Tanh(over);
            return scaled < 0f ? -eased : eased;
        }

        private static AudioClip LoadWav(string path, string clipName, float gain)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Main.LogAlways("Audio file missing, that sound stays silent: " + path);
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 12 ||
                    bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
                    bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
                {
                    Main.LogAlways("Not a RIFF/WAVE file, that sound stays silent: " + path);
                    return null;
                }

                int channels = 0;
                int sampleRate = 0;
                int bitsPerSample = 0;
                int formatTag = 0;
                int dataOffset = -1;
                int dataLength = 0;

                // Chunk walk: 4-byte id, 4-byte little-endian size, payload,
                // padded to an even boundary.
                int cursor = 12;
                while (cursor + 8 <= bytes.Length)
                {
                    string id = "" + (char)bytes[cursor] + (char)bytes[cursor + 1] +
                                     (char)bytes[cursor + 2] + (char)bytes[cursor + 3];
                    int size = bytes[cursor + 4] | (bytes[cursor + 5] << 8) |
                               (bytes[cursor + 6] << 16) | (bytes[cursor + 7] << 24);
                    int payload = cursor + 8;
                    if (size < 0 || payload + size > bytes.Length) size = bytes.Length - payload;

                    if (id == "fmt " && size >= 16)
                    {
                        formatTag = bytes[payload] | (bytes[payload + 1] << 8);
                        channels = bytes[payload + 2] | (bytes[payload + 3] << 8);
                        sampleRate = bytes[payload + 4] | (bytes[payload + 5] << 8) |
                                     (bytes[payload + 6] << 16) | (bytes[payload + 7] << 24);
                        bitsPerSample = bytes[payload + 14] | (bytes[payload + 15] << 8);
                    }
                    else if (id == "data")
                    {
                        dataOffset = payload;
                        dataLength = size;
                    }

                    cursor = payload + size + (size & 1);
                }

                if (dataOffset < 0 || channels <= 0 || sampleRate <= 0)
                {
                    Main.LogAlways("WAV header incomplete, that sound stays silent: " + path);
                    return null;
                }
                // 1 is WAVE_FORMAT_PCM. Both shipped files are tag 1, 44100 Hz,
                // 16-bit; anything else would need a decoder this does not have.
                if (formatTag != 1 || (bitsPerSample != 16 && bitsPerSample != 8))
                {
                    Main.LogAlways("Unsupported WAV format (tag " + formatTag + ", " + bitsPerSample +
                        " bit); only uncompressed 8/16-bit PCM is read. Silent: " + path);
                    return null;
                }

                int bytesPerSample = bitsPerSample / 8;
                int totalSamples = dataLength / bytesPerSample;
                if (totalSamples <= 0)
                {
                    Main.LogAlways("WAV has no sample data, that sound stays silent: " + path);
                    return null;
                }

                float[] samples = new float[totalSamples];
                bool amplify = gain != 1f;
                if (bitsPerSample == 16)
                {
                    for (int i = 0; i < totalSamples; i++)
                    {
                        int at = dataOffset + i * 2;
                        short raw = (short)(bytes[at] | (bytes[at + 1] << 8));
                        float sample = raw / 32768f;
                        samples[i] = amplify ? ApplyGain(sample, gain) : sample;
                    }
                }
                else
                {
                    for (int i = 0; i < totalSamples; i++)
                    {
                        float sample = (bytes[dataOffset + i] - 128) / 128f;
                        samples[i] = amplify ? ApplyGain(sample, gain) : sample;
                    }
                }

                // lengthSamples is per channel, not the interleaved total.
                AudioClip clip = AudioClip.Create(clipName, totalSamples / channels, channels, sampleRate, false);
                clip.SetData(samples, 0);
                UnityEngine.Object.DontDestroyOnLoad(clip);
                Main.LogAlways("Loaded " + Path.GetFileName(path) + ": " + channels + " ch, " +
                    sampleRate + " Hz, " + bitsPerSample + " bit, " + clip.length.ToString("0.00") + " s");
                return clip;
            }
            catch (Exception ex)
            {
                Main.LogAlways("Failed to load " + path + ": " + ex.Message);
                return null;
            }
        }
    }

    internal static class BrakeShoeFactory
    {
        // Local basis after GltfLoader.MapSourceBasis, measured from the mesh
        // rather than assumed (tools/measure_glb.py, tools/measure_profile.py):
        // local Z is the long axis, local Y is height, local X is width, and
        // the low end of the source length axis maps to -Z. GltfLoader bakes
        // that basis into the vertices, so the visual, the colliders, the
        // shelf pose and the rail pose all share one set of local axes.
        internal static readonly Quaternion ImportedModelRotation = Quaternion.identity;
        // Independent first-person pose. Rail and shelf transforms never reuse it.
        internal static readonly Vector3 HeldItemPosition = new Vector3(0.14f, -0.12f, 0.26f);
        internal static readonly Quaternion HeldItemRotation = Quaternion.Euler(6f, 188f, -10f);
        // Identity: the baked basis already lies flat, base down, length along
        // Z, so the rail pose needs no correction on top of the track frame.
        internal static readonly Quaternion RailBaseRotation = Quaternion.identity;

        // Where the end stop sits along the model, as fractions of its length,
        // measured from the centre. These are the numbers the "Wheel stop" box
        // below is built from, and BrakeShoeBehaviour derives the span window a
        // wheel may occupy from the same two constants: the working surface
        // ends at the inner face, and a wheel arriving from behind meets the
        // outer one. Sharing them is what keeps the collider the player sees
        // and the physics that stops the wheel from disagreeing.
        internal const float StopBoxCentre = 0.30f;
        private const float StopBoxLength = 0.34f;
        internal const float StopInnerFace = StopBoxCentre - StopBoxLength * 0.5f;
        internal const float StopOuterFace = StopBoxCentre + StopBoxLength * 0.5f;

        // How tall the stop block is, as a fraction of the model's height, and
        // therefore how high its top edge stands above the railhead the shoe's
        // base rests on. The behaviour needs this to work out how far a wheel
        // approaching the back of the block has to stop short: a wheel is a
        // circle, so its body curves down over the block long before its contact
        // patch reaches it. Built from the same two numbers as the collider box.
        private const float StopBoxCentreY = 0.55f;
        private const float StopBoxHeight = 0.86f;
        internal const float StopTopFraction = StopBoxCentreY + StopBoxHeight * 0.5f;
        // Shelf pose: yaw only, so the shoe stays flat on its base. Set from
        // the measured footprint in CreateShelfSource rather than a guess.
        internal static readonly Quaternion ShopDisplayRotation = Quaternion.Euler(0f, 34f, 0f);
        internal static GameObject PreviewPrefab;
        // Replaced by the measured bounds in Initialize; this is only the
        // fallback used if no renderer exists. Realistic shoe: ~414 mm long,
        // ~122 mm tall, ~87 mm wide.
        internal static Bounds VisualBounds = new Bounds(Vector3.zero, new Vector3(0.087f, 0.122f, 0.414f));
        private static GameObject visualTemplate;
        private static string assetRoot;

        internal static void Initialize(string modPath)
        {
            assetRoot = Path.Combine(modPath, "Assets");
            BrakeShoeAudio.Initialize(assetRoot);
            string modelPath = Path.Combine(assetRoot, "railway_brake_shoe_final.glb");
            visualTemplate = GltfLoader.Load(modelPath);
            if (visualTemplate == null) visualTemplate = BuildFallbackVisual();
            else
            {
                // The loader has already mapped the source basis to local
                // X=width, Y=height, Z=length. The source length axis spans
                // 2 units, so 0.207 gives a realistic 414 mm shoe; that also
                // puts height at 122 mm and width at 87 mm.
                visualTemplate.transform.localRotation = ImportedModelRotation;
                visualTemplate.transform.localScale = Vector3.one * 0.207f;
            }
            visualTemplate.name = "RailwayBrakeShoe_VisualTemplate";
            visualTemplate.hideFlags = HideFlags.HideAndDontSave;
            visualTemplate.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(visualTemplate);
            VisualBounds = CalculateBounds(visualTemplate);
            // The provided GLB is centered around its origin. Move its measured
            // base to local y=0 so physics, shelf and rail poses share a true base.
            visualTemplate.transform.localPosition = Vector3.up * -VisualBounds.min.y;
            VisualBounds = CalculateBounds(visualTemplate);
            // Clone only after the base offset is applied; the placement helper
            // must see the same pivot and bounds as the physical prefab.
            PreviewPrefab = UnityEngine.Object.Instantiate(visualTemplate);
            PreviewPrefab.name = "RailwayBrakeShoe_Preview";
            PreviewPrefab.hideFlags = HideFlags.HideAndDontSave;
            PreviewPrefab.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(PreviewPrefab);
        }

        internal static GameObject CreateCustomItemSource()
        {
            GameObject inactiveHost = new GameObject("RailwayBrakeShoe_SourceHost");
            inactiveHost.SetActive(false);
            inactiveHost.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(inactiveHost);

            GameObject root = new GameObject("Railway Brake Shoe");
            root.SetActive(false);
            root.transform.SetParent(inactiveHost.transform, false);

            GameObject visual = UnityEngine.Object.Instantiate(visualTemplate, root.transform, false);
            visual.name = "Visual";
            visual.hideFlags = HideFlags.None;
            visual.transform.localRotation = ImportedModelRotation;
            visual.SetActive(true);
            // Measured, not clamped to guessed limits. The old clamps assumed
            // the pre-fix basis, where height and width were swapped, so their
            // floors (0.09 wide, 0.36 long) silently inflated the real model:
            // the shipped shoe is 414 mm long, 122 mm tall and only 87 mm wide.
            Bounds physicalBounds = CalculateBounds(root);
            float baseY = physicalBounds.min.y;
            float width = physicalBounds.size.x;
            float height = physicalBounds.size.y;
            float length = physicalBounds.size.z;

            Rigidbody rb = root.AddComponent<Rigidbody>();
            rb.mass = 12f;
            rb.drag = 0.12f;
            rb.angularDrag = 0.35f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.None;

            PhysicMaterial metal = new PhysicMaterial("BrakeShoeSteel");
            metal.dynamicFriction = Main.Config.DryFriction;
            metal.staticFriction = Mathf.Clamp01(Main.Config.DryFriction * 1.25f);
            metal.frictionCombine = PhysicMaterialCombine.Multiply;
            metal.bounceCombine = PhysicMaterialCombine.Minimum;

            List<GameObject> collisionObjects = new List<GameObject>();
            // Compound collider in the same measured local basis as the visual:
            // X width, Y height, Z length, base at baseY. The height profile
            // (tools/measure_profile.py) shows the low ramp end at -Z and the
            // tall stop end at +Z, so the boxes below follow the real shape
            // instead of a symmetric guess. Every box stays inside the mesh
            // extents; a box taller than the model is what let a wheel catch
            // the shoe above its own surface.
            AddBox(root.transform, "Rail contact", new Vector3(0f, baseY + height * 0.11f, 0f), new Vector3(width * 0.74f, height * 0.22f, length * 0.98f), Quaternion.identity, metal, collisionObjects);
            AddBox(root.transform, "Wheel ramp", new Vector3(0f, baseY + height * 0.16f, -length * 0.30f), new Vector3(width * 0.86f, height * 0.26f, length * 0.40f), Quaternion.Euler(-7f, 0f, 0f), metal, collisionObjects);
            AddBox(root.transform, "Wheel stop", new Vector3(0f, baseY + height * StopBoxCentreY, length * StopBoxCentre), new Vector3(width * 0.94f, height * StopBoxHeight, length * StopBoxLength), Quaternion.identity, metal, collisionObjects);
            AddBox(root.transform, "Left guide", new Vector3(-width * 0.44f, baseY + height * 0.30f, 0f), new Vector3(width * 0.12f, height * 0.52f, length * 0.66f), Quaternion.identity, metal, collisionObjects);
            AddBox(root.transform, "Right guide", new Vector3(width * 0.44f, baseY + height * 0.30f, 0f), new Vector3(width * 0.12f, height * 0.52f, length * 0.66f), Quaternion.identity, metal, collisionObjects);

            // Spec.Item.Awake lets the game's ControlsInstantiator create exactly
            // one platform-specific ItemBase implementation on the cloned prefab.
            // Supplying one here would make the clone contain two controls and the
            // second ItemBase.Awake would corrupt grab/inventory initialization.
            root.AddComponent<BrakeShoeBehaviour>();
            // Keep the source inactive: custom_item_mod adds Spec.Item after cloning.
            // PrepareRuntimePrefab activates the completed clone only after both
            // ItemBase dependencies are present.
            return root;
        }

        /// <summary>
        /// Footprint the shelf sample actually occupies, measured after the
        /// display yaw is applied: X across the shelf length, Y the depth, Z
        /// the height. CreateShelfSource fills this in, and the caller copies
        /// X and Y into CustomItemInfo.ShelfBounds so the declared footprint
        /// and the visible model cannot disagree.
        /// </summary>
        internal static Vector3 ShelfFootprint = new Vector3(0.35f, 0.30f, 0.13f);

        internal static GameObject CreateShelfSource()
        {
            GameObject inactiveHost = new GameObject("RailwayBrakeShoe_ShelfHost");
            inactiveHost.SetActive(false);
            inactiveHost.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(inactiveHost);
            GameObject root = new GameObject("RailwayBrakeShoe_ShelfVisual");
            root.SetActive(true);
            root.transform.SetParent(inactiveHost.transform, false);
            GameObject visual = UnityEngine.Object.Instantiate(visualTemplate, root.transform, false);
            visual.name = "Visual";
            visual.hideFlags = HideFlags.None;
            // Yaw only, so the shoe still rests flat on its base on the shelf.
            visual.transform.localRotation = ShopDisplayRotation * ImportedModelRotation;
            visual.SetActive(true);

            // Seat the yawed model inside the box DV.Shops.ShelfItem reserves
            // for it. Verified from IL: the selected-gizmo draws that box at
            // centre (0, height/2, -depth/2) with size (size.x, height,
            // size.y), so local X runs along the shelf, the base sits at
            // y = 0 and the depth is consumed in NEGATIVE Z. A model centred
            // on the origin therefore hangs half its depth off the front edge,
            // which is what the overhang in the shop was.
            Bounds rotated = CalculateBounds(root);
            visual.transform.localPosition += new Vector3(
                -rotated.center.x,
                -rotated.min.y,
                -rotated.max.z);

            Bounds seated = CalculateBounds(root);
            ShelfFootprint = new Vector3(seated.size.x, seated.size.z, seated.size.y);

            BoxCollider bounds = root.AddComponent<BoxCollider>();
            bounds.center = root.transform.InverseTransformPoint(seated.center);
            bounds.size = seated.size;
            // The sample is display-only; a solid collider here would fight
            // the player's own grab colliders at the counter.
            bounds.isTrigger = true;
            Main.Log("Shelf footprint: width " + ShelfFootprint.x.ToString("0.000")
                + " depth " + ShelfFootprint.y.ToString("0.000")
                + " height " + ShelfFootprint.z.ToString("0.000"));
            return root;
        }

        internal static Sprite CreateIcon()
        {
            string iconPath = Path.Combine(assetRoot ?? string.Empty, "railway_brake_shoe_icon.png");
            if (File.Exists(iconPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(iconPath);
                    Texture2D loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (loaded.LoadImage(bytes, true))
                    {
                        loaded.name = "RailwayBrakeShoe_Icon";
                        return Sprite.Create(loaded, new Rect(0f, 0f, loaded.width, loaded.height), new Vector2(0.5f, 0.5f), 100f);
                    }
                    UnityEngine.Object.Destroy(loaded);
                }
                catch (Exception ex) { Main.Log("Icon load failed: " + ex.Message); }
            }

            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color clear = new Color(0f, 0f, 0f, 0f);
            Color red = new Color(0.65f, 0.07f, 0.025f, 1f);
            Color steel = new Color(0.12f, 0.10f, 0.09f, 1f);
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;
            for (int y = 20; y < 38; y++)
                for (int x = 8; x < 56; x++) pixels[y * size + x] = red;
            for (int y = 38; y < 52; y++)
                for (int x = 34; x < 54; x++) pixels[y * size + x] = steel;
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            texture.name = "RailwayBrakeShoe_Icon";
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        // There was a debug spawner here that instantiated the item prefab in
        // front of the camera. Removed with its GUI button: a shoe made that way
        // never belonged to the player, so it could not be registered with the
        // world storage and silently failed to survive a reload, which reads as
        // a bug in the save code rather than as a limit of the test tool.
        // Shoes now only ever come from the shop.

        private static void AddBox(Transform parent, string name, Vector3 position, Vector3 scale, Quaternion rotation, PhysicMaterial material, List<GameObject> objects)
        {
            GameObject child = new GameObject(name);
            child.transform.SetParent(parent, false);
            child.transform.localPosition = position;
            child.transform.localRotation = rotation;
            BoxCollider collider = child.AddComponent<BoxCollider>();
            collider.size = scale;
            collider.material = material;
            objects.Add(child);
        }

        private static GameObject BuildFallbackVisual()
        {
            GameObject root = new GameObject("FallbackVisual");
            Material red = new Material(Shader.Find("Standard"));
            red.color = new Color(0.58f, 0.05f, 0.025f, 1f);
            GameObject basePart = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(basePart.GetComponent<Collider>());
            basePart.transform.SetParent(root.transform, false);
            basePart.transform.localScale = new Vector3(0.11f, 0.045f, 0.43f);
            basePart.GetComponent<Renderer>().sharedMaterial = red;
            return root;
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return VisualBounds;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            // The template has no parent, so renderer world bounds are exactly
            // the bounds in the future physical root's coordinate system.
            return bounds;
        }
    }

    public sealed partial class BrakeShoeBehaviour : MonoBehaviour, ICustomNonVRGrabAnchor
    {
        // StopContact was dropped here: nothing assigned it and nothing compared
        // against it. It described the shoe's stop end being struck, which was a
        // collision-era idea; engagement is measured along the track now, and a
        // wheel arriving at the stop end is InitialContact into Climbing. The
        // value is only ever written to a save as its own name and never parsed
        // back, so removing it cannot invalidate an existing save.
        public enum ShoeState { Free, Placed, InitialContact, Climbing, Braking, Sliding, Breakaway }

        // Speeds, in m/s, at which the wheel on the shoe starts and stops being
        // counted as stopped. Kept apart on purpose: the gap is the hysteresis
        // band that stops a slow-creeping wheel flipping state every step. The
        // lower bound is the old single threshold, so a wheel that genuinely
        // comes to rest is still called stopped at the same speed as before;
        // only the release is raised.
        private const float StopEnterSpeed = 0.02f;
        private const float StopExitSpeed = 0.06f;

        // Wheel radius used only when a car's livery cannot be read, which in
        // normal play does not happen: TrainCar.Awake resolves the livery before
        // any bogie moves. It is deliberately on the generous side of the sizes
        // DV uses, because the error is one-sided - too large only leaves a
        // finger's gap between wheel and stop block, while too small lets the
        // block reach into the wheel, which is the very fault this guards.
        private const float DefaultWheelRadius = 0.5f;

        // How fast, in m/s, the shoe works itself out from under a wheel it is
        // overlapping. Only ever applied on top of the wheel's own speed, so it
        // sets the pace of unwedging a stationary overlap and nothing else. Slow
        // on purpose: this should read as the wheel shoving the shoe along, not
        // as the shoe being flicked away.
        private const float DepenetrationSpeed = 0.35f;

        /// <summary>
        /// Current phase of the shoe. Transitions are logged because they are the
        /// only externally visible evidence that UpdateWheelEngagement ran at
        /// all: while the shoe is detached, FixedUpdate returns before reaching
        /// it, so a wheel rolling straight through the shoe and a shoe that lost
        /// its rail anchor look identical from outside. A log line naming the
        /// phase separates the two.
        /// </summary>
        public ShoeState State
        {
            get { return stateValue; }
            private set
            {
                if (stateValue == value) return;
                ShoeState previous = stateValue;
                stateValue = value;
                Main.Log("Shoe state " + previous + " -> " + value);
            }
        }
        private ShoeState stateValue = ShoeState.Free;
        private Rigidbody body;
        private Vector3 lastForce;
        internal bool snappedToRail;
        private ItemSaveData saveData;
        private DV.CabControls.ItemBase item;

        // Rail attachment expressed in the game's own track coordinates.
        // Bogies travel a PointSetTraveller measured in "span" (metres along the
        // kinked point set), so the shoe stores its own span on the same track
        // and all wheel contact is resolved in that 1-D space. Rails carry no
        // colliders, so span comparison is the only exact way to know that a
        // wheel is on the shoe.
        private RailTrack currentTrack;
        private double railSpan;
        private bool isReversed;
        private float railSide; // -1 or +1: which of the two rails
        private bool spanValid;
        // Job completion may sample between physics steps. Keep the tolerance
        // small and tied to the shoe's measured capture zone rather than
        // accepting an arbitrary nearby wagon.
        // Maximum distance from the shoe's anchored span at which the target
        // car's axle may still be considered secured during a job query.  The
        // value remains bounded: it is large enough to cover the measured wheel
        // contact envelope, but never becomes a general nearby-car radius.
        private const float SecuredWheelTolerance = 0.35f;
        // Search radius used only to find a candidate wheel for job validation.
        // Eligibility is still decided by the contact envelope, orientation and
        // existing holding state below; this is deliberately not a secured radius.
        private const double SecuredWheelSearchRadius = 1.0;

        // Read by the placement code to reject a spot that is already taken. The
        // anchor is the authoritative "where this shoe is": the transform can lag
        // by a physics step, and a shoe being carried by a wheel keeps moving.
        internal bool IsAnchored { get { return snappedToRail && spanValid && currentTrack != null; } }
        internal RailTrack AnchoredTrack { get { return currentTrack; } }

        /// <summary>
        /// The car this shoe is holding, or null when it is holding nothing.
        ///
        /// Backed by the same latch the friction code counts, so "secured" here
        /// means exactly what it means to the physics: the wheel is up on the
        /// working surface with the wedge engaged. A shoe lying the wrong way
        /// round clears offsetLocked, so it answers null however hard the wheel
        /// is pressing on its end stop.
        /// </summary>
        public TrainCar SecuredCar
        {
            get
            {
                if (!IsAnchored || contactFromBehind || !offsetLocked ||
                    jammedInFrog || !HasStaticHold) return null;
                if (!wheelTouching && !WithinSecuredWheelTolerance()) return null;
                return contactCar;
            }
        }

        // Career checks can run between FixedUpdate ticks. Refresh only the
        // deterministic contact snapshot (and create the same static joint the
        // physics tick would create) so an unchanged shoe cannot alternate
        // between secured and unsecured depending on callback order.
        internal bool RefreshJobSecurity(TrainCar car)
        {
            if (!IsAnchored || car == null) return false;
            Bogie bogie;
            double axle;
            float direction;
            // Resolve the candidate for this exact car.  Looking up the nearest
            // axle globally and comparing its car afterwards made a neighbouring
            // wagon mask a valid shoe on the requested wagon.
            if (!TryFindEngagedAxle(car, SecuredWheelSearchRadius, out bogie, out axle, out direction) || bogie == null || bogie.Car != car)
                return false;
            float speed = bogie.rb == null ? 0f : Mathf.Abs(Vector3.Dot(bogie.rb.velocity, bogie.transform.forward));
            // A joint that already holds this exact bogie is authoritative. The
            // solver can leave a small residual Rigidbody velocity for one frame
            // after the wheel stops; rejecting it here made an unchanged held
            // wagon appear unsecured depending on when the job button sampled.
            bool existingHold = HasStaticHold && holdingBogie == bogie;
            if (speed > StopExitSpeed && !existingHold) return false;
            double work = isReversed ? -1.0 : 1.0;
            double along = (axle - railSpan) * work;
            double back = BrakeShoeFactory.VisualBounds.size.z * BrakeShoeFactory.StopOuterFace +
                WheelClearanceForHeight(GetWheelRadius(car), BrakeShoeFactory.VisualBounds.size.y);
            double capture = Main.Config.CaptureHalfLength;
            double tolerance = SecuredWheelTolerance;
            bool withinPhysicalContact = along >= -capture && along <= back;
            // A job check may run before the first physics contact (manual
            // placement) or just after a step has released the transient
            // wheelTouching flag. Permit only the small, bounded ramp-side gap;
            // TryHoldStoppedWheel below still requires a stopped Rigidbody,
            // sufficient capacity and the same physical joint path.
            bool withinStableTolerance = along < -capture &&
                along >= -tolerance;
            if ((!withinPhysicalContact && !withinStableTolerance) ||
                along > BrakeShoeFactory.VisualBounds.size.z * BrakeShoeFactory.StopBoxCentre)
                return false;
            // The geometric face test above has established the correct side of
            // this shoe for this wheel.  Clear a stale wrong-way latch from a
            // previous contact before asking the existing holding path to reuse
            // or create its joint.
            contactFromBehind = false;
            contactBogie = bogie;
            contactCar = car;
            wheelTouching = true;
            if (!offsetLocked)
            {
                capturedSpanOffset = axle - railSpan;
                offsetLocked = true;
                pushSpanDirection = 0.0;
            }
            TryHoldStoppedWheel(bogie, speed, work);
            return SecuredCar == car;
        }

        private bool WithinSecuredWheelTolerance()
        {
            Bogie bogie;
            double axle;
            float direction;
            if (!TryFindEngagedAxle(contactCar, SecuredWheelSearchRadius, out bogie, out axle, out direction) || bogie == null || bogie.Car != contactCar)
                return false;
            return Math.Abs(axle - railSpan) <= SecuredWheelTolerance;
        }

        /// <summary>
        /// The car whose wheel is touching this shoe in any fashion, including the
        /// wrongly placed case. Null when no wheel is in reach.
        /// </summary>
        public TrainCar ContactCar
        {
            get { return IsAnchored && wheelTouching ? contactCar : null; }
        }

        /// <summary>
        /// True while the wheel in contact arrived at the back of the end stop,
        /// i.e. the shoe is lying the wrong way round for it.
        /// </summary>
        public bool IsWrongWayRound
        {
            get { return IsAnchored && wheelTouching && contactBogie != null && contactFromBehind; }
        }

        /// <summary>
        /// True once this shoe has jammed in a switch frog and become an obstacle
        /// on the railhead rather than a brake.
        /// </summary>
        public bool IsJammedInFrog { get { return jammedInFrog; } }

        internal double AnchoredSpan { get { return railSpan; } }
        internal float AnchoredSide { get { return railSide; } }

        // Wheel engagement state.
        private Bogie contactBogie;
        private TrainCar contactCar;
        private Vector3 contactPoint;
        private float lastAppliedForce;
        private double capturedSpanOffset;
        private bool hasCapturedOffset;
        // Whether capturedSpanOffset holds a real offset taken on the working
        // surface. A wheel that met the back of the end stop is in contact - so
        // hasCapturedOffset is set, which is what stops the contact face being
        // re-latched every step - but it never took an offset, so the two facts
        // are tracked apart.
        private bool offsetLocked;
        // Whether the wheel currently in contact arrived at the back of the end
        // stop, i.e. the shoe is lying the wrong way round for it. Latched on
        // first contact: once the shoe is being shoved along, the wheel's
        // position alone no longer says which face it came at.
        private bool contactFromBehind;
        // Which way along the span the wheel was travelling when it took the
        // shoe. A shoe is a wedge: it is only pushed in that direction.
        private double pushSpanDirection;
        // Latches whether the wheel on the shoe is currently counted as stopped.
        // The stop test needs hysteresis: a single threshold makes a wheel
        // creeping at around that speed flip between Braking and Sliding on
        // every physics step, which alternates whether resistance is applied at
        // all and fills the log with transitions. With two thresholds the shoe
        // has to slow below StopEnterSpeed to be called stopped and then exceed
        // StopExitSpeed to be moving again, so a wheel hovering between them
        // keeps whichever state it already had.
        private bool wheelStopped;

        // Whether the wheel found this step is genuinely touching the shoe rather
        // than merely being the nearest one on the track.
        //
        // contactBogie alone does not mean contact: TryFindEngagedAxle searches
        // out to CaptureHalfLength + 3 m so the approach can be seen coming, and
        // it assigns contactBogie for any wheel inside that. The hazard layer must
        // not roll against a wheel three metres away, so the reach test's own
        // verdict is recorded here instead of being inferred from the fields.
        private bool wheelTouching;

        // Hazard state. Kept apart from the engagement fields above so the
        // existing physics never reads it and cannot be changed by it.
        //
        // When the next hazard roll is due. One timer covers both hazards
        // because a shoe cannot be jammed in a frog and be chocking a wheel the
        // wrong way round at the same moment, and it is set on the step contact
        // begins rather than at zero, so the first roll comes after a full
        // interval instead of immediately on touch.
        private float nextHazardCheck;
        // The car the wrong-way timer belongs to. A new car arriving at the shoe
        // restarts the countdown; without this a second wheel would inherit the
        // elapsed time of the first and could be rolled against instantly.
        private TrainCar hazardContactCar;
        // Set once the shoe has jammed in a frog. From then on it is an obstacle:
        // it no longer travels with the wheel that was pushing it, and each axle
        // passing over it is rolled against.
        private bool jammedInFrog;
        // Where it jammed, kept only for the log line.
        private string jammedJunctionName;
        // Whether this dragged shoe has already been latched in the current frog
        // window, so the deterministic jam is applied only once per approach.
        private bool frogJamRolled;
        // The bogie last rolled against while the shoe sits jammed, so a single
        // axle standing on it is not rolled against on the same timer as the
        // axles still arriving.
        private Bogie jammedRolledBogie;

        // Half-length of the window around a junction node, in metres of span,
        // inside which a travelling shoe is treated as being at the frog. The
        // frog is the crossing casting where the rails intersect and a shoe
        // dragged into it has somewhere to wedge; a metre either side of the node
        // covers that casting on DV's switches without reaching into plain track.
        private const double FrogWindowHalfLength = 1.0;

        // v1.2.0: High-speed collision hazard. One-shot flag per contact to ensure
        // the high-speed check fires only once at first contact, not on every step.
        private bool highSpeedRolled;

        // v1.2.0: Track the last bogie speed to detect high-speed collisions.
        private float lastBogieSpeed;

        // Whether the ItemBase lifecycle events are currently subscribed. The
        // control is created by the game after this component, so subscribing
        // is retried until it exists rather than done once in Awake.
        private bool eventsHooked;

        // The grab handler the game put on this item, and whether the shoe is
        // currently refusing to be picked up because a wheel is riding it.
        private DV.Interaction.AGrabHandler grabHandler;
        private bool pickupBlocked;

        /// <summary>
        /// True while a wheel is actually on the shoe and moving, which is when
        /// the shoe must not come off.
        ///
        /// InitialContact is deliberately not included: there the wheel is
        /// merely approaching and has not reached the shoe body, so the shoe is
        /// still free to be taken back. Braking is excluded too - that is the
        /// wheel standing still on the shoe, which is exactly the case the
        /// player is told to wait for.
        /// </summary>
        private bool IsUnderRollingWheel
        {
            get
            {
                if (!IsAnchored) return false;
                return State == ShoeState.Climbing ||
                       State == ShoeState.Sliding ||
                       State == ShoeState.Breakaway;
            }
        }

        // The game's own RespawnOnDrop on this item, disabled for as long as the
        // shoe is anchored. Its Checker coroutine runs every 0.2 s and assigns
        // isKinematic = (item is far from the camera); an anchored shoe is right
        // next to the player, so it kept handing the body back to PhysX five
        // times a second while FixedUpdate forced it kinematic again. Disabling
        // the component stops that coroutine (OnDisable stops it explicitly),
        // which removes the fight instead of out-writing it.
        private MonoBehaviour respawnOnDrop;
        private bool respawnOnDropSuspended;

        // Whether this mod is the one that put the shoe in the world storage.
        // Only such a registration is withdrawn again when the shoe is picked up,
        // so an entry the game made itself is never touched.
        private bool worldStorageRegisteredByMod;

        // When the storage checks may run again. Both of them walk a storage's
        // item list, and neither answers a question that can change between
        // physics steps: a shoe is moved to lost and found by the shed, and its
        // world-storage entry only changes when it is placed or taken. Polling
        // them 50 times a second per shoe would scan those lists for nothing, so
        // they are sampled a few times a second instead. The delay only bounds how
        // long a shoe can be dragged back towards its old rail after the shed
        // moves it, which is well under one frame of visible travel.
        private float nextStorageCheck;
        private const float StorageCheckInterval = 0.25f;

        // Whether the kinematic-restore net has already been reported for the
        // current anchor. Reset on attach so a genuine new occurrence is still
        // visible in the log.
        private bool reportedPhysicsRestore;

        // A saved anchor that has not been re-established yet.
        //
        // LoadState used to resolve the track once and give up if it could not,
        // which loses the shoe: rails carry no colliders, so an unanchored shoe
        // has nothing to rest on and drops through the railhead. One attempt is
        // not enough, because the item's save data is applied while the world is
        // still being built - the track the shoe was left on may belong to a
        // streamed-in section that has not appeared yet, and a turntable's
        // hierarchy is still being assembled, so neither the saved path nor the
        // shoe's own position resolves on that first frame.
        //
        // Holding the saved values lets FixedUpdate keep trying until the world
        // has settled, which is what makes a placed shoe survive a reload.
        private bool restorePending;
        // Whether the shoe is being held in place for the duration of the retry.
        private bool restoreHoldActive;
        private string restoreTrackPath;
        private string restoreTrackName;
        private bool restoreReversed;
        private float restoreSide;
        private double restoreSpan;
        private bool restoreSpanKnown;
        private bool restoreJammed;
        private Vector3 restorePosition;
        private float restoreDeadline;
        private float nextRestoreAttempt;
        private int restoreAttempts;

        // How long, in seconds of play, the mod keeps trying to re-anchor a
        // restored shoe, and how often. The window is generous because world
        // streaming is what it waits on and that is driven by where the player
        // goes, not by elapsed time; the interval keeps the track scan - which
        // walks every RailTrack in the world - to a few times a second.
        private const float RestoreWindowSeconds = 120f;
        private const float RestoreRetryInterval = 0.25f;

        // Looping friction sound, created lazily the first time the shoe is
        // actually scraping. Most shoes never brake anything, so building the
        // source on Awake would add an AudioSource to every shoe in the world
        // and to every one sitting in a shop or an inventory.
        private AudioSource frictionAudio;
        // Reuses the game's WheelslipSparks prefab, but keeps the instance on
        // this shoe so the stock wheel spark controllers remain untouched.
        private static GameObject wheelSparksPrefab;
        private static bool wheelSparksPrefabAttempted;
        private GameObject shoeSparksObject;
        private ParticleSystem[] shoeSparks;
        private float nextSparkTime;
        private const float SparkInterval = 0.08f;
        // Smoothed drive value for that loop, so volume and pitch follow the
        // slide instead of snapping between physics steps. The smoothing
        // constant mirrors what CarFrictionAudioModule does for the cars' own
        // friction layer: a value lerped towards the target every update.
        private float frictionLevel;
        // Slide speed measured for the current step, in m/s. Written by
        // UpdateWheelEngagement and consumed by UpdateFrictionAudio, which runs
        // afterwards so it sees this step's values rather than the last one's.
        private float slideSpeed;
        private bool isScraping;
        // Set once the source has been asked for, whether or not it was built.
        // isScraping is recomputed from scratch every physics step, so without
        // this a missing or unreadable clip would retry the creation on every
        // step for as long as the shoe slides.
        private bool frictionAudioAttempted;

        private static readonly Dictionary<TrainCar, int> ActiveShoeCounts = new Dictionary<TrainCar, int>();
        private static int activeShoeCountFrame = -1;

        private void Awake()
        {
            ShopStockPatches.Track(this);
            body = GetComponent<Rigidbody>();
            item = GetComponent<DV.CabControls.ItemBase>();
            respawnOnDrop = FindRespawnOnDrop();
            State = ShoeState.Free;
            saveData = GetComponent<ItemSaveData>();
            if (saveData != null)
            {
                saveData.ItemSaveDataRequested += SaveState;
                saveData.ItemSaveDataLoaded += LoadState;
            }
        }

        private void OnEnable()
        {
            EnsureItemControl();
            Main.Shoes.Add(this);
        }
        private void OnDisable()
        {
            ReleaseStaticHold();
            Main.Shoes.Remove(this);
            // Items are disabled rather than destroyed when they go into a
            // container or a belt slot, and a disabled MonoBehaviour stops
            // getting FixedUpdate while its child AudioSource keeps playing.
            StopFrictionAudio();
            StopFrictionSparks();
            // The same reasoning applies to the ray block, and more sharply: no
            // FixedUpdate means nothing is left to clear it, so a shoe disabled
            // mid-roll would come back out of the container unpickable.
            ReleasePickupBlock();
        }
        private void OnDestroy()
        {
            ReleaseStaticHold();
            ShopStockPatches.Untrack(this);
            Main.Shoes.Remove(this);
            // A restore in flight must not outlive the component: items are
            // pooled, so a reused instance would otherwise start life holding a
            // previous shoe's saved position.
            restorePending = false;
            restoreHoldActive = false;
            // Items are pooled and reused, so a shoe destroyed while anchored
            // must not leave the game's respawn watcher switched off. The enabled
            // flag is handed back, but its coroutines are deliberately not
            // restarted: this object is going away, and a fresh Start runs when
            // the pool hands the item out again.
            RestoreRespawnOnDrop(false);
            // The grab handler outlives this component on a pooled item, so the
            // delegate has to come off with it or the reused item would spawn
            // permanently unpickable.
            ReleasePickupBlock();
            StopFrictionSparks();
            if (shoeSparksObject != null) UnityEngine.Object.Destroy(shoeSparksObject);
            UnhookItemEvents();
            if (saveData != null)
            {
                saveData.ItemSaveDataRequested -= SaveState;
                saveData.ItemSaveDataLoaded -= LoadState;
            }
        }

        private JObject SaveState(JObject data)
        {
            if (data == null) data = new JObject();
            if (!string.IsNullOrEmpty(PurchaseShopId)) data["railwayBrakeShoeShop"] = PurchaseShopId;
            data["railwayBrakeShoePlaced"] = snappedToRail;
            data["railwayBrakeShoeState"] = State.ToString();
            if (snappedToRail && currentTrack != null)
            {
                // Track names are NOT unique in this game, so the name alone
                // cannot identify a track across a reload. Every turntable in
                // the game calls its RailTrack "Turntable Track" (the string
                // appears in twelve level files), and switches use the shared
                // names "[track through]" and "[track diverging]" 563 times
                // each. RailTrackRegistryBase.GetTrackWithName is a LINQ
                // FirstOrDefault over AllTracks, so it returned the first
                // same-named track on the map: a shoe placed on one turntable
                // was restored onto a different one, teleported away, and read
                // as "disappeared" even though the item itself was fine, which
                // is why summoning lost items still produced it.
                //
                // The game does not persist a track by name. The path is the first
                // discriminator, while the saved world position resolves duplicate
                // paths (turntables and switch branches intentionally share names).
                // The name remains as a compatibility fallback for older saves.
                data["railwayBrakeShoeTrackPath"] = RailPlacement.GetTrackPath(currentTrack);
                data["railwayBrakeShoeTrackName"] = currentTrack.name;
                data["railwayBrakeShoeReversed"] = isReversed;
                data["railwayBrakeShoeSide"] = railSide;
                data["railwayBrakeShoeSpan"] = railSpan;
                // A shoe wedged in a crossing is still wedged there after a
                // reload; the whole point of the hazard is that it stays until
                // somebody picks it up. Written unconditionally so a shoe that has
                // been freed also records that.
                data["railwayBrakeShoeJammed"] = jammedInFrog;
            }
            return data;
        }

        private void LoadState(JObject data)
        {
            if (data == null) return;
            JToken shop = data["railwayBrakeShoeShop"];
            PurchaseShopId = shop != null && shop.Type == JTokenType.String ? (string)shop : null;
            JToken placed = data["railwayBrakeShoePlaced"];
            if (placed == null || placed.Type != JTokenType.Boolean || !(bool)placed) return;

            JToken trackPath = data["railwayBrakeShoeTrackPath"];
            JToken trackName = data["railwayBrakeShoeTrackName"];
            JToken reversed = data["railwayBrakeShoeReversed"];
            JToken side = data["railwayBrakeShoeSide"];
            JToken span = data["railwayBrakeShoeSpan"];
            JToken jammed = data["railwayBrakeShoeJammed"];

            string savedPath = trackPath != null && trackPath.Type == JTokenType.String ? (string)trackPath : null;
            string savedName = trackName != null && trackName.Type == JTokenType.String ? (string)trackName : null;
            if (savedPath == null && savedName == null) return;

            bool rev = reversed != null && reversed.Type == JTokenType.Boolean && (bool)reversed;
            float s = 1f;
            if (side != null && (side.Type == JTokenType.Float || side.Type == JTokenType.Integer))
                s = (float)side;
            double sp = 0.0;
            bool haveSpan = span != null && (span.Type == JTokenType.Float || span.Type == JTokenType.Integer);
            if (haveSpan) sp = (double)span;

            // Keep the anchor as a standing request rather than a single shot.
            // The position is captured here, while it is still the position the
            // shoe was saved at: an unanchored shoe falls, so by the time a later
            // attempt runs, transform.position has drifted below the railhead and
            // would no longer identify the right track.
            restorePending = true;
            restoreTrackPath = savedPath;
            restoreTrackName = savedName;
            restoreReversed = rev;
            restoreSide = s;
            restoreSpan = sp;
            restoreSpanKnown = haveSpan;
            restoreJammed = jammed != null && jammed.Type == JTokenType.Boolean && (bool)jammed;
            restorePosition = transform.position;
            restoreDeadline = Time.time + RestoreWindowSeconds;
            nextRestoreAttempt = 0f;
            restoreAttempts = 0;

            // Freeze the shoe at its saved spot first, then try. If the world is
            // already up this succeeds immediately and the hold is released again
            // in the same call, so nothing is paid for the common case.
            BeginRestoreHold();
            TryRestoreAnchor();
        }

        /// <summary>
        /// Re-establishes a saved rail anchor, retrying until the world has been
        /// built far enough for the track to be found.
        ///
        /// A shoe is held on the rail purely by span, so until this succeeds the
        /// shoe is an ordinary physics object over colliderless rails and sinks.
        /// That is what "placed shoes disappear on reload" looked like.
        /// </summary>
        private void TryRestoreAnchor()
        {
            if (!restorePending) return;

            // Path first, name only as the fallback for saves from earlier
            // versions. Resolving by name is what put turntable shoes on the
            // wrong turntable, so it must never win over an exact path match.
            //
            // The saved position is the tie-breaker: the game restores an item's
            // transform before its save data is applied, so a shoe on a turntable
            // was already standing on the right one even when its saved name
            // matches eleven others.
            RailTrack track = RailPlacement.FindTrack(restoreTrackPath, restoreTrackName, restorePosition);
            restoreAttempts++;

            if (track == null)
            {
                if (Time.time >= restoreDeadline)
                {
                    restorePending = false;
                    // Hand the shoe back to physics rather than leaving it frozen
                    // in mid-air: from here on it is an ordinary dropped item, and
                    // the game's own respawn watcher should be the thing looking
                    // after it.
                    EndRestoreHold();
                    // Reported unconditionally: this is a placed shoe that has
                    // genuinely lost its rail, and it is the one outcome a player
                    // would notice, so it must not be hidden behind the debug
                    // logging switch.
                    Main.LogAlways("Placed shoe could not be returned to its rail after " + restoreAttempts +
                        " attempts over " + RestoreWindowSeconds + " s; track '" + (restoreTrackName ?? "?") +
                        "' was never found. The shoe stays where the game put it.");
                }
                return;
            }

            restorePending = false;
            // The hold flag is cleared without undoing the kinematic body or the
            // respawn suspension, because AttachToRail below wants both anyway.
            restoreHoldActive = false;
            AttachToRail(track, restoreReversed, restoreSide, restoreSpan, restoreSpanKnown);
            // After the attach, which clears the flag as part of putting a shoe on
            // a rail. A shoe saved wedged in a crossing is still wedged there.
            if (restoreJammed)
            {
                jammedInFrog = true;
                Main.LogAlways("Restored brake shoe is still jammed in a switch frog.");
            }
            if (restoreAttempts > 1)
                Main.LogAlways("Placed shoe returned to its rail after " + restoreAttempts + " attempts.");
        }

        /// <summary>
        /// Pins the shoe at its saved position while the anchor is being restored.
        ///
        /// Without this the retry would have nothing left to save. Rails carry no
        /// colliders, so between the save data being applied and the track being
        /// found the shoe is a free body over thin air: it falls out of the world,
        /// and RespawnOnDrop measures that fall and hands the item to the shed.
        /// Holding it kinematic at the saved spot costs nothing if the very first
        /// attempt succeeds, and is what keeps the shoe recoverable when it does
        /// not.
        /// </summary>
        private void BeginRestoreHold()
        {
            restoreHoldActive = true;
            // Same reasoning as an anchored shoe: the watcher must not act on a
            // shoe the mod is positioning itself.
            SuspendRespawnOnDrop();
            if (body != null)
            {
                body.isKinematic = true;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            transform.position = restorePosition;
        }

        /// <summary>
        /// Holds the shoe at the saved position for as long as the restore is
        /// outstanding. Re-asserted every step because the same writers that take
        /// the kinematic flag off an anchored shoe - the pause handler, the train
        /// LOD handler - are active here too.
        /// </summary>
        private void MaintainRestoreHold()
        {
            if (!restoreHoldActive) return;
            // The suspension has to be held here as well as while anchored. The
            // anchored path re-asserts it from FixedUpdate, but that code sits
            // behind the "must be snapped to a rail" early return, and a shoe
            // waiting on a restore is by definition not snapped yet - so during
            // the restore window nothing was holding it down.
            //
            // This is the window that matters most: the game is still loading, so
            // this is exactly when the storage load path calls UpdateSpawnParams
            // and restarts the watcher on a shoe the mod is positioning itself.
            ReassertRespawnSuspension();
            if (body != null && !body.isKinematic)
            {
                body.isKinematic = true;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            if (body != null && body.isKinematic) body.MovePosition(restorePosition);
            else transform.position = restorePosition;
        }

        /// <summary>
        /// Abandons an outstanding restore and gives the shoe back to physics.
        /// Called from every path that gives the shoe a home of its own - a hand,
        /// a slot, a container, the shed - so a saved anchor can never pull it
        /// back out of one.
        /// </summary>
        private void CancelRestore()
        {
            if (!restorePending && !restoreHoldActive) return;
            restorePending = false;
            EndRestoreHold();
        }

        /// <summary>
        /// Gives the shoe back to physics when a restore is abandoned, so it is
        /// never left both frozen in mid-air and unwatched.
        /// </summary>
        private void EndRestoreHold()
        {
            if (!restoreHoldActive) return;
            restoreHoldActive = false;
            RestoreRespawnOnDrop();
            if (body != null)
            {
                body.isKinematic = false;
                body.interpolation = RigidbodyInterpolation.None;
            }
        }

        /// <summary>
        /// Binds the shoe to a track at a known span. Once attached the shoe is
        /// kinematic and driven purely by span, exactly like the game drives its
        /// own bogies, so it can never sink through or drift off the rail.
        /// </summary>
        internal void AttachToRail(RailTrack track, bool reversed, float side, double span, bool spanKnown)
        {
            currentTrack = track;
            isReversed = reversed;
            railSide = side;
            reportedPhysicsRestore = false;

            if (!spanKnown)
            {
                double resolved;
                if (RailPlacement.TryGetSpanAt(track, transform.position, out resolved)) span = resolved;
                else span = 0.0;
            }

            railSpan = span;
            spanValid = currentTrack != null;
            snappedToRail = spanValid;
            State = spanValid ? ShoeState.Placed : ShoeState.Free;
            // A shoe being put on a rail is a shoe that is not stuck in anything.
            // The restore path re-applies a saved jam right after this returns.
            jammedInFrog = false;
            jammedJunctionName = null;
            ClearEngagement();

            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                // Kinematic while on the rail: the game's own bogies are moved
                // with MovePosition along their traveller rather than being left
                // to PhysX, and the shoe must ride the same way to stay aligned.
                body.isKinematic = true;
                // v1.2.0: ContinuousSpeculative for kinematic bodies to prevent
                // fast-moving wheels from phasing through the shoe.
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                // The pose is only recomputed in FixedUpdate, so without
                // interpolation the shoe is redrawn at the same 50 Hz staircase
                // while the camera and the train move at frame rate. That reads
                // as vertical jitter on a graded or curved rail, where each
                // step also changes height. Interpolation smooths the render
                // pose between physics steps without touching the physics.
                body.interpolation = RigidbodyInterpolation.Interpolate;
            }

            if (spanValid)
            {
                SuspendRespawnOnDrop();
                EnsureWorldStorageRegistration();
                ApplyRailPose();
            }
        }

        /// <summary>
        /// Makes sure a shoe resting on a rail is listed in the world storage, so
        /// StorageSerializer.SaveStorage writes it: for every storage except the
        /// inventory that method takes its items from StorageBase.GetStorageItemList,
        /// and an item missing from that list is simply not saved.
        ///
        /// Needed because FinalizePlacementPostfix clears the placer's itemToPlace
        /// once the shoe is anchored, so the shoe stops being the held item without
        /// necessarily going through the inventory transition that would otherwise
        /// hand it to StorageController. Registering here is idempotent - the
        /// IsInStorageWorld check means a shoe already tracked by the game is left
        /// alone - so placing, picking up and placing again cannot add duplicates.
        /// </summary>
        private void EnsureWorldStorageRegistration()
        {
            try
            {
                if (item == null) item = GetComponent<DV.CabControls.ItemBase>();
                if (item == null) return;

                // Only items the game itself counts as the player's may enter the
                // world storage: StorageController.InitStorage creates
                // [Storage World] with acceptsNonEssential: false, so
                // AddItemToStorageItemList rejects anything else outright with
                // "You are trying to add non-essential item '...' but storage
                // '[Storage World]' doesn't allow it. Aborting.". A shop-bought
                // shoe carries the flag, because GlobalShopController sets
                // BelongsToPlayer on purchase; a shoe made by the debug button
                // deliberately does not, and stays a throwaway test object that
                // does not survive a reload.
                InventoryItemSpec specs = item.InventorySpecs;
                if (specs == null || !specs.BelongsToPlayer) return;

                StorageController controller = DV.Utils.SingletonBehaviour<StorageController>.Instance;
                if (controller == null) return;
                if (controller.IsInStorageWorld(item)) return;
                controller.AddItemToWorldStorage(item);
                worldStorageRegisteredByMod = true;
                Main.Log("Shoe registered with the world storage so it survives a reload.");
            }
            catch (Exception ex)
            {
                Main.Log("World-storage registration skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Takes the shoe back out of the world storage while the player is
        /// holding it.
        ///
        /// This is the other half of EnsureWorldStorageRegistration, and its
        /// absence was the duplication. StorageSerializer.SaveStorage records the
        /// held item separately - it reads PlayerCameraSwitcher.HiddenItem and
        /// HiddenItemSlot and stores that slot on the item's record - while the
        /// world storage list is serialized on its own. A shoe left in the world
        /// list while in the hand was therefore written twice, and the reload
        /// built two shoes from the two records. The second copy still carried the
        /// saved "placed" state, so it re-anchored itself to the rail and kept
        /// pushing back on the axle: that is the wheels behaving as though the
        /// shoe were still there.
        ///
        /// Only the registration this mod made is withdrawn. Nothing is done for a
        /// shoe the game itself is tracking, and dropping it puts it back the
        /// vanilla way - StorageController.OnInventoryStatusChanged calls
        /// AddItemToWorldStorage for a released item that BelongsToPlayer - so
        /// persistence of a shoe left lying on the ground is unaffected.
        /// </summary>
        private void ReleaseWorldStorageRegistration()
        {
            if (!worldStorageRegisteredByMod) return;
            try
            {
                if (item == null) return;
                StorageController controller = DV.Utils.SingletonBehaviour<StorageController>.Instance;
                if (controller == null) return;
                if (!controller.IsInStorageWorld(item)) { worldStorageRegisteredByMod = false; return; }
                controller.RemoveItemFromWorldStorage(item);
                worldStorageRegisteredByMod = false;
                Main.Log("Shoe taken out of the world storage while held, so it cannot be saved twice.");
            }
            catch (Exception ex)
            {
                Main.Log("World-storage release skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether the game has moved the shoe into the shed's lost-and-found
        /// storage. Asked every step while anchored, because nothing notifies an
        /// item when MoveItemsFromWorldToLostAndFound relocates it.
        /// </summary>
        private bool IsInLostAndFound()
        {
            try
            {
                if (item == null) item = GetComponent<DV.CabControls.ItemBase>();
                if (item == null) return false;
                StorageController controller = DV.Utils.SingletonBehaviour<StorageController>.Instance;
                if (controller == null) return false;
                return controller.IsInStorageLostAndFound(item);
            }
            catch { return false; }
        }

        /// <summary>
        /// Locates the game's RespawnOnDrop on this item, if the prefab has one.
        /// Resolved by name so the mod keeps building against the same reference
        /// set; the type lives in Assembly-CSharp and is not referenced anywhere
        /// else here.
        /// </summary>
        private MonoBehaviour FindRespawnOnDrop()
        {
            try
            {
                Type type = AccessTools.TypeByName("RespawnOnDrop");
                if (type == null) return null;
                Component found = GetComponent(type);
                return found as MonoBehaviour;
            }
            catch { return null; }
        }

        // The two Coroutine handles RespawnOnDrop keeps for its own watchers.
        // Needed because disabling the component is NOT enough to stop them, and
        // that gap is what made placed shoes vanish across a reload.
        //
        // RespawnOnDrop runs its Checker through CoroutineManager.Instance.Run,
        // so the coroutine lives on the CoroutineManager singleton rather than on
        // the item. Unity only stops a coroutine when the object that STARTED it
        // is disabled, and that object is the manager, which is never disabled.
        // The component's own OnDisable is what normally stops them, by calling
        // CoroutineManager.Stop on these two fields.
        //
        // So a watcher started while the component is already disabled keeps
        // running with nothing left to stop it. That happens on the load path
        // every time: StorageController.AddItemToStorageItemList ends by calling
        // GetComponent<RespawnOnDrop>().UpdateSpawnParams(), and on an item that
        // has not finished initializing that method goes straight to
        // StartChecking(), which starts Checker again. The mod itself reaches
        // that code through AddItemToWorldStorage in EnsureWorldStorageRegistration,
        // immediately after suspending the watcher.
        //
        // From there Checker measures the shoe against a spawn position worked
        // out mid-load and, when it reads as out of range, hands the item to
        // RespawnOrDestroy. Its loading-screen guard only protects the isKinematic
        // write, not that branch. For a purchased shoe - one that BelongsToPlayer -
        // RespawnOrDestroy relocates it to lost and found instead of destroying it,
        // which is exactly "the shoe is gone from the rail but the shed has it".
        //
        // Whether the shoe survived came down to how the load happened to be
        // ordered, which is why the same save lost its shoes on some entries and
        // kept them on others.
        private static readonly Type RespawnOnDropType = AccessTools.TypeByName("RespawnOnDrop");
        private static readonly FieldInfo RespawnCheckerCoroField =
            RespawnOnDropType == null ? null : AccessTools.Field(RespawnOnDropType, "respawnDistanceCheckerCoro");
        private static readonly FieldInfo RespawnOrDestroyCoroField =
            RespawnOnDropType == null ? null : AccessTools.Field(RespawnOnDropType, "respawnOrDestroyCoro");

        /// <summary>
        /// Stops the watcher coroutines RespawnOnDrop is running, the same way its
        /// own OnDisable does, and clears the handles so the component's lifecycle
        /// stays consistent with what actually runs.
        ///
        /// Clearing them matters in both directions: OnEnable starts a fresh
        /// Checker only when the handle is null, so a stale non-null handle would
        /// leave a detached shoe permanently unwatched, while a stale handle for a
        /// coroutine that is still running would be stopped twice.
        /// </summary>
        private void StopRespawnWatchers()
        {
            if (respawnOnDrop == null)
            {
                Main.Log("StopRespawnWatchers: respawnOnDrop is null");
                return;
            }
            if (RespawnCheckerCoroField == null && RespawnOrDestroyCoroField == null)
            {
                Main.Log("StopRespawnWatchers: both field reflections failed");
                return;
            }

            try
            {
                CoroutineManager manager = DV.Utils.SingletonBehaviour<CoroutineManager>.Instance;
                StopRespawnWatcher(manager, RespawnCheckerCoroField, "Checker");
                StopRespawnWatcher(manager, RespawnOrDestroyCoroField, "RespawnOrDestroy");
            }
            catch (Exception ex)
            {
                Main.Log("Stopping the respawn watcher failed: " + ex.Message);
            }
        }

        private void StopRespawnWatcher(CoroutineManager manager, FieldInfo field, string name)
        {
            if (field == null)
            {
                Main.Log("StopRespawnWatcher(" + name + "): field is null");
                return;
            }
            Coroutine running = field.GetValue(respawnOnDrop) as Coroutine;
            if (running == null) return;
            // Cleared first. If Stop throws, the handle is still dropped, so the
            // item is left in the state OnEnable can recover from rather than
            // holding a handle to a coroutine nobody owns.
            field.SetValue(respawnOnDrop, null);
            if (manager != null) manager.Stop(running);
            Main.Log("Respawn watcher coroutine " + name + " stopped while anchored.");
        }

        /// <summary>
        /// Stops RespawnOnDrop from driving isKinematic while the shoe is
        /// span-driven. Disabling the component is what stops its Checker
        /// coroutine; the enabled flag is restored verbatim on detach so an
        /// item that never had the behaviour running is left alone.
        /// </summary>
        private void SuspendRespawnOnDrop()
        {
            if (respawnOnDrop == null) respawnOnDrop = FindRespawnOnDrop();
            if (respawnOnDrop == null) return;

            // Deliberately not gated on the component being enabled right now,
            // and not on the suspension already being recorded.
            //
            // AttachToRail also runs from LoadItemData, which the game calls
            // while a restored item is still being set up, and RespawnOnDrop can
            // be disabled at that moment. The old early return on !enabled left
            // the suspension unrecorded, so once the item finished activating the
            // watcher was running again over an anchored shoe. Its Checker then
            // measured the shoe against the player and its spawn parent, and
            // RespawnOrDestroy took it out of the world storage and handed it to
            // lost and found. That is exactly a placed shoe going missing across
            // a reload, and for a purchased shoe - one that BelongsToPlayer, so
            // it is relocated rather than destroyed - it is also how the shed
            // ended up holding shoes at all.
            respawnOnDropSuspended = true;
            if (respawnOnDrop.enabled)
            {
                respawnOnDrop.enabled = false;
                Main.Log("RespawnOnDrop suspended while anchored.");
            }
            // Unconditional, and after the disable rather than instead of it.
            // Disabling only stops watchers the component itself started while
            // enabled; one started from StartChecking on a disabled component
            // keeps running on the CoroutineManager with nothing to stop it.
            StopRespawnWatchers();
        }

        /// <summary>
        /// Holds the suspension down for as long as the shoe is anchored.
        ///
        /// The component is switched back on by the item's own lifecycle - a
        /// restored item is activated after its save data has been applied - so a
        /// single suspend at attach time is not enough on the load path.
        /// </summary>
        private void ReassertRespawnSuspension()
        {
            if (!respawnOnDropSuspended || respawnOnDrop == null) return;
            if (respawnOnDrop.enabled)
            {
                respawnOnDrop.enabled = false;
                Main.Log("RespawnOnDrop re-suspended while anchored.");
            }
            // Checked even when the component is already disabled, because that
            // is the case that actually loses shoes: a watcher started through
            // StartChecking on a disabled component runs on the CoroutineManager,
            // so "disabled" says nothing about whether one is running. Both
            // handles are null in the normal case, making this a null read per
            // step and no allocation.
            StopRespawnWatchers();
        }

        /// <summary>
        /// Hands the shoe back to the game's respawn watcher, with its coroutines
        /// actually running again.
        ///
        /// Enabling the component is what restarts them: OnEnable calls
        /// StartChecking, but only when the checker handle is null - which it is,
        /// because suspending stopped and cleared it. If the component is somehow
        /// already enabled no OnEnable fires, and the shoe would be left with no
        /// watcher at all, so in that case it is cycled to raise the event the way
        /// Unity would. That is the one thing clearing the handles could otherwise
        /// have made worse than leaving them alone.
        /// </summary>
        /// <param name="restartWatchers">
        /// False while the component is being torn down. A coroutine started
        /// against a component that is going away would run once more against a
        /// dead object, and a pooled item gets a fresh Start anyway.
        /// </param>
        private void RestoreRespawnOnDrop(bool restartWatchers)
        {
            if (!respawnOnDropSuspended) return;
            respawnOnDropSuspended = false;
            if (respawnOnDrop == null) return;
            if (restartWatchers && respawnOnDrop.enabled) respawnOnDrop.enabled = false;
            respawnOnDrop.enabled = true;
            Main.Log("RespawnOnDrop restored after detaching.");
        }

        private void RestoreRespawnOnDrop()
        {
            RestoreRespawnOnDrop(true);
        }

        /// <summary>
        /// The metal-on-metal clack of setting the shoe down on the railhead.
        ///
        /// Fired from MarkPlaced rather than from AttachToRail, because LoadState
        /// also calls AttachToRail: putting it there would play one clack per
        /// saved shoe every time the world loads. MarkPlaced is only reached from
        /// FinalizePlacementPostfix, which is the player actually placing it.
        /// </summary>
        internal void MarkPlaced()
        {
            snappedToRail = true;
            State = ShoeState.Placed;
            BrakeShoeAudio.PlayPlacement(transform);
            // Opens the window in which the grab and inventory teardown paths
            // must not undo this anchor. FinalizePlacement runs while the shoe
            // is still in the hand and the release follows afterwards.
            placementGraceUntil = Time.time + PlacementGraceSeconds;
        }

        /// <summary>
        /// Releases the shoe back to normal free-body physics.
        ///
        /// Every caller passes why, and the reason is logged, because losing the
        /// anchor is what makes the shoe fall through the rail: ApplyRailPose
        /// forces the transform back onto the railhead on every step while the
        /// anchor is held, even if the body is not kinematic, so the shoe can
        /// only sink once snappedToRail is false. A detach that fires right after
        /// placement is therefore the whole failure, and the log has to name it.
        /// </summary>
        internal void DetachFromRail(string reason)
        {
            if (snappedToRail || currentTrack != null)
                Main.LogAlways("Shoe detached from rail: " + (reason ?? "unspecified"));
            DetachFromRailInternal();
        }

        private void DetachFromRailInternal(bool preserveWorldStorage = false)
        {
            currentTrack = null;
            spanValid = false;
            snappedToRail = false;
            // Whatever took the shoe off the rail - the player's hand, a
            // container, the shed - overrides a still-outstanding restore. Without
            // this a shoe picked up during the retry window would be yanked back
            // to its old span out of the player's hand. Only the flags are cleared
            // here; the body and the respawn watcher are handed back at the end of
            // this method anyway, which is exactly what EndRestoreHold would do.
            restorePending = false;
            restoreHoldActive = false;
            // Before anything else: a shoe that is no longer on a rail must not
            // stay listed in the world storage on this mod's account, or it is
            // serialized alongside the held-item record and comes back twice.
            if (!preserveWorldStorage) ReleaseWorldStorageRegistration();
            // Taking the shoe off the rail is what frees it from a frog: the jam is
            // a fact about it sitting in that crossing, and the shoe is no longer
            // there. Cleared before ClearEngagement, which deliberately leaves the
            // flag alone so a wheel rolling clear does not un-jam it.
            jammedInFrog = false;
            jammedJunctionName = null;
            ClearEngagement();
            // A shoe that is no longer on a rail cannot be scraping one, and
            // FixedUpdate now returns before UpdateFrictionAudio, so the loop has
            // to be stopped here rather than left to fade.
            StopFrictionAudio();
            // FixedUpdate also returns before the normal particle cleanup path
            // after a detach, so clear the local stock spark systems here too.
            StopFrictionSparks();
            // Same reasoning for the ray block: the state this depends on is
            // gone, and UpdatePickupBlock is no longer reached to clear it.
            ReleasePickupBlock();
            State = ShoeState.Free;
            // Hand distance-based respawning back before physics, so the item is
            // never left both free-bodied and unwatched.
            RestoreRespawnOnDrop();
            if (body != null)
            {
                body.isKinematic = false;
                // Back to the prefab setting. Interpolation is only wanted
                // while the shoe is driven from span in FixedUpdate; a free
                // body is stepped by PhysX and the grab code moves it
                // directly, so leave that path exactly as it was.
                body.interpolation = RigidbodyInterpolation.None;
                // v1.2.0: Restore ContinuousDynamic for dynamic bodies.
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
        }

        private void FixedUpdate()
        {
            EnsureItemControl();
            lastForce = Vector3.zero;
            lastAppliedForce = 0f;

            // Safety net only. OnItemGrabbed already detaches on the game's own
            // Grabbed event; this catches a shoe that somehow ends up held
            // without that event, for instance if it was grabbed before this
            // component finished subscribing. It cannot be the primary check
            // because IsGrabbed reports false while grabHandler is still null.
            //
            // The grace window matters: placement runs inside FinalizePlacement
            // while the shoe is still in the player's hand, and the grab is only
            // released afterwards. Without the window this safety net saw
            // "grabbed" on the very next physics step and detached the shoe it
            // had just anchored. Losing the anchor is exactly what lets the shoe
            // fall through the railhead, because ApplyRailPose stops holding it
            // there, and it also skips UpdateWheelEngagement below - which is
            // why wheels rolled straight through the shoe as well.
            if (item != null && item.IsGrabbed() && !IsInPlacementGrace())
            {
                if (snappedToRail || currentTrack != null) DetachFromRail("still grabbed after placement grace");
                // The player's hand wins over an outstanding restore. This has to
                // be cancelled explicitly: during a restore the shoe is not yet
                // anchored, so DetachFromRail above does not run and would leave
                // the request standing - the shoe would sit kinematic in the hand
                // and then snap back to its saved span once released.
                CancelRestore();
                StopFrictionAudio();
                // A shoe that is already in a hand cannot be under a wheel, and
                // leaving the ray blocked would make the held item unusable.
                ReleasePickupBlock();
                return;
            }

            // A shoe inside a container or belt slot is disabled and reparented;
            // it must never be driven along a rail from there.
            if (item != null && (item.IsSnapped || item.InContainer != null))
            {
                if (snappedToRail || currentTrack != null)
                    DetachFromRail(item.IsSnapped ? "snapped into a slot" : "moved into a container");
                // Same reasoning as the grab above: a shoe in a slot or a
                // container has a home already, and an outstanding restore would
                // otherwise pull it back onto a rail out of that slot.
                CancelRestore();
                StopFrictionAudio();
                ReleasePickupBlock();
                return;
            }

            // A saved anchor that has not been re-established yet. Placed after
            // the grab and container checks above, so a shoe the player has
            // already picked up is never dragged back to its old span, and before
            // the storage checks, because a shoe the shed has taken should be
            // dropped from the retry rather than re-anchored.
            //
            // Until this succeeds the shoe is a free body over colliderless
            // rails, so every step it stays pending is a step it spends falling.
            if (restorePending)
            {
                if (IsInLostAndFound())
                {
                    // The shed has it. That is a legitimate home, so stop trying
                    // to put it back on a rail and let the shed keep it.
                    restorePending = false;
                    EndRestoreHold();
                }
                else
                {
                    // Held first, tried second: the hold is what keeps the shoe
                    // in the world while the attempts run.
                    MaintainRestoreHold();
                    if (Time.time >= nextRestoreAttempt)
                    {
                        nextRestoreAttempt = Time.time + RestoreRetryInterval;
                        TryRestoreAnchor();
                    }
                }
            }

            // Both storage questions share one timer, and it is read once here so
            // the two cannot disagree about whether this is a sampling step.
            bool checkStorage = Time.time >= nextStorageCheck;
            if (checkStorage) nextStorageCheck = Time.time + StorageCheckInterval;

            if (!snappedToRail || !spanValid || currentTrack == null)
            {
                StopFrictionAudio();
                ReleasePickupBlock();
                return;
            }

            // Re-assert kinematic ownership. Releasing a held item restores its
            // physics - that is how a dropped item falls - and the release
            // happens just after FinalizePlacement has anchored the shoe. Rails
            // carry no colliders, so a shoe handed back to PhysX has nothing to
            // rest on and sinks straight through the railhead. Anchored means
            // span-driven, so the body must stay kinematic for as long as the
            // anchor is held, no matter who tried to hand it back.
            //
            // Kept as a net even though the known writer (RespawnOnDrop) is now
            // suspended while anchored, because it is not the only one: the pause
            // handler and the train LOD handler write the same flag. The log is
            // reported once per anchor rather than on every hit, since the old
            // per-hit line filled Player.log at five lines a second and made the
            // real events in it unreadable.
            if (body != null && !body.isKinematic)
            {
                if (!reportedPhysicsRestore)
                {
                    reportedPhysicsRestore = true;
                    Main.Log("Rigidbody physics were restored while anchored; returning to kinematic.");
                }
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                // v1.2.0: ContinuousSpeculative for kinematic bodies to prevent
                // fast-moving wheels from phasing through the shoe.
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.interpolation = RigidbodyInterpolation.Interpolate;
            }

            // Same reasoning as the kinematic net above, for the same reason it is
            // needed on the load path: the item's activation re-enables its own
            // components, so the suspension has to be held rather than set once.
            // Cheap - a bool field on a component already in hand - so it runs
            // every step rather than on the storage timer.
            ReassertRespawnSuspension();
            // Keeps the world-storage entry honest for a shoe that is still on a
            // rail, which is what makes it survive the next save. On the sampling
            // timer: this walks the storage item list.
            if (checkStorage) EnsureWorldStorageRegistration();

            UpdateWheelEngagement();
            // Strictly after engagement and strictly before the shoe is moved:
            // this reads the contact state engagement just settled, and a jam has
            // to be in effect before the span is followed and the pose applied,
            // so a shoe that jams this step is not carried one more step first.
            UpdateHazards();
            // Between the two: engagement is what pushes the span past the end of
            // the track, and the pose has to be sampled from whichever track the
            // shoe belongs to afterwards. Doing it here also means the span handed
            // to ApplyRailPose is always inside its own point set, so the clamp in
            // TryGetPoseAtSpan can no longer pin the shoe at a joint.
            MigrateAcrossJointIfNeeded();
            ApplyRailPose();
            // After the pose, so the audio host has already been moved to where
            // the shoe is being heard this step.
            UpdateFrictionAudio();
            UpdateFrictionSparks();
            // Last: UpdateWheelEngagement above has settled this step's state,
            // and the block is a function of that state.
            UpdatePickupBlock();
        }

        /// <summary>
        /// Drives the looping friction sound from the slide the shoe is actually
        /// doing this step.
        ///
        /// The sound is tied to scraping, not to being under a wheel: a shoe with
        /// a wheel standing still on it (ShoeState.Braking) is silent, and only a
        /// shoe being dragged along the railhead while resisting (Sliding, or
        /// Breakaway when the load exceeds the threshold) is heard. That is the
        /// real behaviour - a shoe only sings while it skids.
        ///
        /// Volume rises with slide speed and with how hard the shoe is actually
        /// biting, so a heavy car grinding to a stop is louder than a light one
        /// nudging the shoe along. Pitch rises with speed only, over a narrow
        /// band, which keeps a looped 2.7 s clip from sounding obviously cyclic
        /// without turning it into a whine.
        /// </summary>
        private void UpdateFrictionAudio()
        {
            bool scraping = isScraping && slideSpeed > 0.05f;

            // Target level from the two things that make the scrape loud: how
            // fast it slides, and how much of the configured maximum force it is
            // taking. Both are normalised, so neither can drive it past 1.
            float target = 0f;
            if (scraping)
            {
                float speedPart = Mathf.Clamp01(slideSpeed / 4f);
                float forcePart = Mathf.Clamp01(lastAppliedForce / Mathf.Max(1f, Main.Config.MaximumBrakeForce));
                target = Mathf.Clamp01(0.35f + 0.45f * speedPart + 0.20f * forcePart);
            }

            // Nothing to do and nothing playing: leave without building a source.
            if (frictionAudio == null)
            {
                if (target <= 0f || frictionAudioAttempted) return;
                frictionAudioAttempted = true;
                frictionAudio = BrakeShoeAudio.CreateFrictionSource(transform);
                // Still null means the clip is missing or the source could not be
                // built. The flag above makes that a one-off, so a silent shoe
                // costs nothing on the steps that follow.
                if (frictionAudio == null) return;
            }

            // Fade rather than cut, so starting and stopping a skid does not
            // click. Rising is quicker than falling: the scrape starts the
            // instant metal bites, and trails off as the wheel settles.
            float rate = target > frictionLevel ? 12f : 6f;
            frictionLevel = Mathf.MoveTowards(frictionLevel, target, rate * Time.fixedDeltaTime);

            float volume = frictionLevel * Mathf.Clamp01(Main.Config.FrictionVolume);
            if (volume <= 0.001f)
            {
                if (frictionAudio.isPlaying) frictionAudio.Stop();
                frictionAudio.volume = 0f;
                return;
            }

            frictionAudio.volume = volume;
            frictionAudio.pitch = Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp01(slideSpeed / 6f));
            if (!frictionAudio.isPlaying)
            {
                // Random entry point so several shoes scraping at once do not
                // play the same 2.7 s loop in lockstep. NAudio.GetRandomTime is
                // the game's own helper for exactly this.
                try { frictionAudio.time = NAudio.GetRandomTime(frictionAudio.clip); }
                catch { }
                frictionAudio.Play();
            }
        }

        /// <summary>
        /// Silences the friction loop immediately, for the paths where the shoe
        /// stops being a shoe on a rail: detaching, being grabbed, going into a
        /// container, or being disabled. Without this a shoe picked up mid-skid
        /// would keep scraping in the player's hand, because FixedUpdate returns
        /// early in those states and never reaches UpdateFrictionAudio.
        /// </summary>
        private void StopFrictionAudio()
        {
            isScraping = false;
            slideSpeed = 0f;
            frictionLevel = 0f;
            if (frictionAudio == null) return;
            if (frictionAudio.isPlaying) frictionAudio.Stop();
            frictionAudio.volume = 0f;
        }

        /// <summary>
        /// Emits a small burst from the game's own WheelslipSparks prefab while
        /// this shoe is genuinely sliding under load. The prefab is instantiated
        /// lazily, and every burst is explicitly emitted so a parked or merely
        /// placed shoe cannot leave a continuously running particle system.
        /// </summary>
        private void UpdateFrictionSparks()
        {
            bool scraping = IsAnchored && isScraping && slideSpeed > 0.05f &&
                (State == ShoeState.Sliding || State == ShoeState.Breakaway);
            if (!scraping)
            {
                StopFrictionSparks();
                return;
            }
            if (Time.time < nextSparkTime) return;
            nextSparkTime = Time.time + SparkInterval;
            if (!EnsureFrictionSparks()) return;

            // The rail contact is below the shoe body. Keep the stock spark
            // orientation aligned with the rail direction at that contact.
            float halfHeight = BrakeShoeFactory.VisualBounds.size.y > 0f
                ? BrakeShoeFactory.VisualBounds.size.y * 0.5f : 0.061f;
            Vector3 contact = transform.position - transform.up * (halfHeight + 0.002f);
            // Emit opposite to the measured wheel/shoe travel. The small
            // downward component sends the particles back toward the rail;
            // using the signed velocity means reverse motion flips naturally.
            float alongSpeed = 0f;
            if (contactBogie != null && contactBogie.rb != null)
                alongSpeed = Vector3.Dot(contactBogie.rb.velocity, transform.forward);
            if (Mathf.Abs(alongSpeed) < 0.05f && pushSpanDirection != 0.0)
                alongSpeed = (float)pushSpanDirection * slideSpeed;
            float travelSign = alongSpeed < 0f ? -1f : 1f;
            Vector3 travel = transform.forward * travelSign;
            Vector3 sparkDirection = (-travel - transform.up * 0.22f).normalized;
            Vector3 sparkUp = Vector3.Cross(transform.right, sparkDirection).normalized;
            if (sparkUp.sqrMagnitude < 0.01f) sparkUp = transform.up;
            shoeSparksObject.transform.SetPositionAndRotation(contact,
                Quaternion.LookRotation(sparkDirection, sparkUp));
            int count = slideSpeed > 2f ? 2 : 1;
            for (int i = 0; i < shoeSparks.Length; i++)
            {
                ParticleSystem system = shoeSparks[i];
                if (system == null) continue;
                system.Emit(count);
            }
        }

        private bool EnsureFrictionSparks()
        {
            if (shoeSparksObject != null && shoeSparks != null && shoeSparks.Length > 0) return true;
            if (!wheelSparksPrefabAttempted)
            {
                wheelSparksPrefabAttempted = true;
                try
                {
                    wheelSparksPrefab = Resources.Load("WheelslipSparks", typeof(GameObject)) as GameObject;
                    if (wheelSparksPrefab == null) Main.Log("Wheel spark prefab not found; shoe sparks disabled.");
                }
                catch (Exception ex) { Main.Log("Wheel spark prefab load failed: " + ex.Message); }
            }
            if (wheelSparksPrefab == null) return false;
            try
            {
                shoeSparksObject = UnityEngine.Object.Instantiate(wheelSparksPrefab, transform, false);
                shoeSparksObject.name = "RailwayBrakeShoe_WheelSparks";
                shoeSparks = shoeSparksObject.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < shoeSparks.Length; i++)
                    if (shoeSparks[i] != null) shoeSparks[i].Stop(true);
                if (shoeSparks.Length == 0)
                {
                    UnityEngine.Object.Destroy(shoeSparksObject);
                    shoeSparksObject = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Wheel spark instance creation failed: " + ex.Message);
                shoeSparksObject = null;
                shoeSparks = null;
                return false;
            }
        }

        private void StopFrictionSparks()
        {
            nextSparkTime = 0f;
            if (shoeSparks == null) return;
            for (int i = 0; i < shoeSparks.Length; i++)
                if (shoeSparks[i] != null) shoeSparks[i].Stop(true);
        }

        /// <summary>
        /// The radius of the wheels on a car, in metres. Read from the livery's
        /// parent type, which is where TrainCar.Awake itself gets the figure it
        /// hands to WheelRotationBase, so a shoe under a locomotive uses the
        /// locomotive's real wheels rather than an assumed size. Bogie and
        /// Bogie.AxleInfo carry no radius of their own, which is why the lookup
        /// goes through the car. Falls back to the wagon figure when a car has
        /// no livery yet, since this only sets how far a wheel stops short.
        /// </summary>
        private static float GetWheelRadius(TrainCar car)
        {
            if (car == null) return DefaultWheelRadius;
            try
            {
                DV.ThingTypes.TrainCarLivery livery = car.carLivery;
                if (livery == null || livery.parentType == null) return DefaultWheelRadius;
                float radius = livery.parentType.wheelRadius;
                // Guarded rather than trusted: a zero or absurd radius would put
                // the shoe somewhere silly, and a shoe in the wrong place is a
                // worse failure than one using the default size.
                if (radius > 0.05f && radius < 2f) return radius;
            }
            catch (Exception ex)
            {
                Main.Log("Wheel radius read failed: " + ex.Message);
            }
            return DefaultWheelRadius;
        }

        /// <summary>
        /// How far short of a step of the given height a wheel of the given
        /// radius comes to rest, measured along the rail from the axle centre.
        /// The wheel's contact patch is under the axle, but the wheel bulges out
        /// above that, so it touches the top edge of the step first. That contact
        /// point is where the circle of radius r crosses the height h, giving
        /// sqrt(r^2 - (r - h)^2) as the horizontal gap. A step at or above the
        /// axle centre is met at the widest part of the wheel, so the gap is
        /// simply the radius.
        /// </summary>
        private static double WheelClearanceForHeight(double radius, double height)
        {
            if (radius <= 0.0) return 0.0;
            if (height <= 0.0) return 0.0;
            if (height >= radius) return radius;
            double rise = radius - height;
            return Math.Sqrt(radius * radius - rise * rise);
        }

        /// <summary>
        /// Finds the nearest wheel on the shoe's own track and, when that wheel
        /// overlaps the shoe, drags the shoe along with it and pushes back on the
        /// bogie. Rails have no colliders, so the wheel is located by comparing
        /// the shoe's span against each axle's span on the same point set.
        /// </summary>
        private void UpdateWheelEngagement()
        {
            // Cleared every step and set again only on the paths that genuinely
            // scrape, so a state the shoe silently falls out of cannot leave the
            // friction loop running.
            isScraping = false;
            slideSpeed = 0f;
            // Same discipline for the contact flag: cleared here and set once the
            // reach test below has actually been passed.
            wheelTouching = false;

            Bogie bogie;
            double axleSpan;
            float axleDirection;
            if (!TryFindEngagedAxle(out bogie, out axleSpan, out axleDirection))
            {
                ClearEngagement();
                if (State != ShoeState.Free) State = ShoeState.Placed;
                return;
            }

            contactBogie = bogie;
            contactCar = bogie.Car;
            contactPoint = transform.position + transform.up * 0.05f;

            double delta = axleSpan - railSpan;
            float capture = Main.Config.CaptureHalfLength;

            // A shoe is a wedge, so which way round it lies decides everything
            // below. ApplyRailPose maps the model's local +Z - the end stop - onto
            // forward * (isReversed ? -1 : +1), and forward is the direction span
            // increases in, so this is the span direction that runs from the ramp
            // toe towards the stop. It is read fresh every step because
            // MigrateAcrossJointIfNeeded flips isReversed when the shoe crosses a
            // joint whose span axis opposes this one.
            double workDirection = isReversed ? -1.0 : 1.0;

            // Where the wheel sits along the shoe's own length axis, in metres
            // from the middle of the shoe and positive towards the end stop.
            // Every test below is written in this quantity, which is what makes
            // the shoe's facing part of the physics rather than a decoration.
            double alongShoe = delta * workDirection;

            float shoeLength = BrakeShoeFactory.VisualBounds.size.z;
            if (shoeLength <= 0f) shoeLength = 0.414f;
            float shoeHeight = BrakeShoeFactory.VisualBounds.size.y;
            if (shoeHeight <= 0f) shoeHeight = 0.122f;
            // The wheel rolls up the inclined working surface and comes to rest
            // against the inner face of the stop block. It cannot travel past it.
            double surfaceEnd = shoeLength * BrakeShoeFactory.StopInnerFace;
            // Coming from the other side it meets the outer face of that same
            // block. Everything between the two faces is inside the block, so the
            // line between "on the working surface" and "up against the back of
            // the stop" is drawn through its middle.
            double stopMiddle = shoeLength * BrakeShoeFactory.StopBoxCentre;
            double stopBack = shoeLength * BrakeShoeFactory.StopOuterFace;

            // How far back from the axle centre a wheel coming at the stop block
            // from behind actually touches it. The block's top edge is what the
            // wheel meets, and the wheel's rim curves out well ahead of its
            // contact patch, so this is a good deal more than the block's own
            // depth. Everything on the stop side is measured from here rather
            // than from the block's back face.
            double wheelClearance = WheelClearanceForHeight(GetWheelRadius(contactCar), shoeHeight * BrakeShoeFactory.StopTopFraction);
            double backReach = stopBack + wheelClearance;

            // Out of reach: the wheel is merely near the shoe. The ramp side keeps
            // the configured capture length, so the tuning for how early a wheel
            // takes the shoe is unchanged; the far side reaches out to where a
            // wheel's rim first meets the top of the stop block, because that is
            // the point contact begins - not where its contact patch would be.
            if (alongShoe < -capture || alongShoe > backReach)
            {
                State = ShoeState.InitialContact;
                hasCapturedOffset = false;
                offsetLocked = false;
                contactFromBehind = false;
                pushSpanDirection = 0.0;
                wheelStopped = false;
                return;
            }

            // Past the reach test above, so this wheel is on the shoe rather than
            // approaching it. Only from here may the hazard layer roll against it.
            wheelTouching = true;

            float bogieSpeed = 0f;
            if (bogie.rb != null)
                bogieSpeed = Vector3.Dot(bogie.rb.velocity, bogie.transform.forward);
            float speed = Mathf.Abs(bogieSpeed);

            // v1.2.0: Store the bogie speed for hazard checks.
            lastBogieSpeed = bogieSpeed;

            // bogie.transform.forward is traveller.worldForward * trackDirection
            // (Bogie.UpdateRotation), so this is the sign of the wheel's motion
            // in span, which is the direction the shoe can be pushed.
            float axleSign = axleDirection >= 0f ? 1f : -1f;
            double spanVelocitySign = bogieSpeed * axleSign >= 0f ? 1.0 : -1.0;

            // Single stop decision for the whole step, latched so the answer only
            // changes when the speed leaves the band between the two thresholds.
            if (wheelStopped)
            {
                if (speed > StopExitSpeed) wheelStopped = false;
            }
            else if (speed < StopEnterSpeed)
            {
                wheelStopped = true;
            }
            bool moving = !wheelStopped;

            // A shoe wedged in a frog has stopped being a wedge under a wheel and
            // become an obstacle fixed in the crossing. It is not dragged along and
            // it produces no braking force, so engagement ends here - but only
            // after the contact and speed above, which the hazard layer reads.
            //
            // Placed ahead of the latches below so a jammed shoe cannot re-capture
            // an offset from the next wheel to arrive and be carried out of the
            // frog it is supposed to be stuck in.
            if (jammedInFrog)
            {
                hasCapturedOffset = false;
                offsetLocked = false;
                contactFromBehind = false;
                pushSpanDirection = 0.0;
                State = ShoeState.InitialContact;
                return;
            }

            // Which face of the shoe this wheel met. Latched on first contact and
            // held while the wheel stays in reach, so a wheel that arrived at the
            // back of the stop block cannot be re-read as a wheel on the working
            // surface once it has pushed the shoe along a little.
            if (!hasCapturedOffset)
                contactFromBehind = alongShoe > stopMiddle;

            // The wheel came at the back of the end stop: the shoe is lying the
            // wrong way round for this wheel. A real shoe does nothing here - it
            // is simply shoved along the rail - so the wheel is stopped from
            // entering the shoe body and no resistance is produced at all.
            if (contactFromBehind)
            {
                hasCapturedOffset = true;
                offsetLocked = false;
                pushSpanDirection = 0.0;
                // Shove the shoe ahead so its back face stays against the wheel
                // instead of the wheel walking through the stop block and ending
                // up on the working surface from the wrong side. Only ever pushed
                // away from the wheel, so a wheel rolling back off it leaves the
                // shoe where it lies.
                //
                // The gap is measured to the wheel's rim, not to the axle. The
                // axle centre is a wheel radius above the railhead, so aligning
                // the back face with the axle's span buried the tall stop block
                // in the wheel and left it sticking out through the rail. A wheel
                // rests against the top edge of the block, which is backReach
                // back along the rail from the axle centre.
                double pushedSpan = axleSpan - backReach * workDirection;
                // Slide clear, do not jump. A shoe set down just behind a wheel
                // starts inside the space the wheel's rim occupies, and correcting
                // that in a single step would look like the shoe was flicked away.
                // The allowance is the wheel's own speed plus a slow unwedging
                // rate: the first term lets the shoe keep up exactly with a wheel
                // under power, so it is never overrun, and the second clears an
                // overlap under a standing wheel gently enough to read as a shove.
                // Route this bounded move through the same shoe-to-shoe swept
                // contact path as the normal working-surface follow. The wrong-way
                // branch used to write railSpan directly, which let a kinematic
                // shoe carried by a wheel pass through another anchored shoe.
                double maxStep = (speed + DepenetrationSpeed) * Time.fixedDeltaTime;
                // The shoe is shoved away from the wheel, and the wheel is on the
                // stop-block side, so that is the direction opposite the shoe's own
                // working direction - not along it.
                double targetSpan = railSpan;
                if (workDirection < 0.0)
                {
                    if (pushedSpan > railSpan) targetSpan = Math.Min(pushedSpan, railSpan + maxStep);
                }
                else if (pushedSpan < railSpan) targetSpan = Math.Max(pushedSpan, railSpan - maxStep);
                if (Math.Abs(targetSpan - railSpan) > 1e-7)
                    MoveWithShoeContacts(targetSpan, speed, new HashSet<BrakeShoeBehaviour>());
                // Not Braking: nothing is being braked. The wheel is touching the
                // shoe, which is what InitialContact means, and keeping it out of
                // the rolling states also leaves the shoe pickable.
                State = ShoeState.InitialContact;
                return;
            }

            // The wheel is on the working surface. Lock the relative span offset
            // the first frame so the shoe is pushed ahead of the wheel instead of
            // the wheel walking through it, then follow that offset every step.
            // This is what makes the shoe travel with the wheel rather than stay
            // in place.
            if (!hasCapturedOffset)
            {
                capturedSpanOffset = delta;
                hasCapturedOffset = true;
                offsetLocked = true;
                pushSpanDirection = moving ? spanVelocitySign : 0.0;
                State = ShoeState.Climbing;
            }
            else if (pushSpanDirection == 0.0 && moving)
            {
                // The wheel was stationary on the shoe and has now started to
                // move; that first motion sets which way the wedge is driven.
                pushSpanDirection = spanVelocitySign;
                capturedSpanOffset = delta;
            }

            // The wheel may not climb past the inner face of the end stop. Without
            // this the captured offset alone decided how far up the shoe the wheel
            // sat, so a wheel that took the shoe while already part-way along it
            // kept that offset forever. Clamping here is what makes the wheel ride
            // up the incline and then butt against the stop, whatever offset it
            // started from.
            if (alongShoe > surfaceEnd)
            {
                capturedSpanOffset = surfaceEnd * workDirection;
            }

            // Follow the wheel only in the direction the wedge is being driven.
            // If the wheel reverses off the shoe, the shoe stays where it is and
            // the wheel simply rolls away from it, as a real shoe would.
            double followSpan = axleSpan - capturedSpanOffset;

            if (wheelStopped)
            {
                if (TryHoldStoppedWheel(bogie, speed, workDirection))
                {
                    State = ShoeState.Braking;
                    return;
                }
                // Capacity is insufficient for the current grade. Continue
                // through the sliding path so the shoe slips with the wheel.
            }

            // A wedge only resists the direction it is driven. Once the wheel
            // reverses, it rolls back off the shoe and there is nothing to
            // resist, so no force is applied.
            if (pushSpanDirection != 0.0 && spanVelocitySign != pushSpanDirection)
            {
                State = ShoeState.InitialContact;
                return;
            }

            // Last direction test, and the one the whole shoe exists for: a wheel
            // is only resisted while it is driving the shoe towards its own end
            // stop. A wheel rolling the other way is running off the ramp toe, and
            // a shoe cannot hold that.
            if (pushSpanDirection != 0.0 && pushSpanDirection != workDirection)
            {
                State = ShoeState.InitialContact;
                return;
            }

            // Only now, after all direction checks pass, does the shoe follow the
            // wheel. Sweep the actual compound colliders through the full movement
            // interval, stopping at contact and recursively pushing loose shoes.
            double proposedSpan = followSpan;
            if (Math.Abs(proposedSpan - railSpan) > 1e-7)
            {
                MoveWithShoeContacts(proposedSpan, speed, new HashSet<BrakeShoeBehaviour>());
                proposedSpan = railSpan;
            }
            railSpan = proposedSpan;

            State = ShoeState.Sliding;

            // From here the shoe is being dragged along the railhead: that is the
            // scrape the friction loop plays. Set before the force is computed so
            // an early return below still leaves the sound running at the right
            // speed, with the force term simply contributing nothing.
            isScraping = true;
            slideSpeed = speed;

            // Sliding friction of a steel shoe skidding on a steel rail under the
            // share of car weight carried by this axle.
            float wetness = WeatherCompatibility.GetWetness();
            float mu = Main.Config.DryFriction * Mathf.Lerp(1f, Main.Config.WetFrictionMultiplier, wetness);
            float load = GetAxleLoad(bogie);
            float speedFactor = Mathf.Lerp(0.45f, 1f, Mathf.Clamp01(speed / 5f));

            // The friction model, unchanged from 1.1.0.
            float requested = Mathf.Min(Main.Config.MaximumBrakeForce, mu * load * speedFactor);

            // At crawl speed use the car's own full handbrake capacity as the
            // reference. This keeps the shoe equivalent across light and heavy
            // cars and avoids a fixed force that is wrong on steep grades.
            if (speed < 0.5f)
                requested = Mathf.Max(requested, GetHoldingCapacity(contactCar));

            int active = CountActiveShoes(contactCar);
            requested *= Mathf.Pow(0.92f, Mathf.Max(0, active - 1));

            if (requested > Main.Config.BreakawayForce)
            {
                State = ShoeState.Breakaway;
                requested = Main.Config.BreakawayForce;
            }

            if (float.IsNaN(requested) || float.IsInfinity(requested) || requested <= 0f) return;

            // Bogie.FixedUpdate brakes with rb.AddForce(transform.forward * ...),
            // so resistance is applied the same way and in the same units. The
            // bogie's own FixedUpdate then folds it into the traveller update.
            if (bogie.rb != null)
            {
                Vector3 force = bogie.transform.forward * -Mathf.Sign(bogieSpeed) * requested;
                bogie.rb.AddForce(force, ForceMode.Force);
                lastForce = force;
                lastAppliedForce = requested;
            }
        }

        /// <summary>
        /// The hazard layer, run after engagement has settled this step's state.
        ///
        /// Deliberately a reader of that state and never a writer of it: nothing
        /// here changes a span, an offset or a contact latch, so the braking
        /// physics behaves exactly as it did without this method. The one thing it
        /// does write is <see cref="jammedInFrog"/>, which the follow code checks.
        ///
        /// Both hazards are rolled on a timer for as long as the dangerous contact
        /// lasts, rather than once when it starts. A wheel grinding against the
        /// back of a shoe is in continuing danger, and one roll at the moment of
        /// touch would make the outcome a property of that instant.
        /// </summary>
        private void UpdateHazards()
        {
            if (!IsAnchored)
            {
                ResetHazardTimer();
                return;
            }

            // Jammed shoes are their own hazard and are handled first: once
            // jammed the shoe is an obstacle in the frog, not a brake, and the
            // wrong-way test below no longer describes it.
            if (jammedInFrog)
            {
                UpdateJammedFrogHazard();
                return;
            }

            if (TryJamInFrog()) return;

            // One unified speed event. It is sampled once per wheel contact and
            // is inactive through 25 km/h. The shoe drop and bogie derailment
            // rolls are independent outcomes of the same speed band.
            if (Main.Config.SpeedHazardEnabled && wheelTouching && contactBogie != null &&
                contactCar != null && !wheelStopped && !highSpeedRolled)
            {
                float speed = Mathf.Abs(lastBogieSpeed);
                float speedKmh = speed * 3.6f;
                float dropChance = ShoeRules.SpeedDropChance(speedKmh);
                float derailChance = ShoeRules.SpeedDerailChance(speedKmh);
                if (dropChance > 0f || derailChance > 0f)
                {
                    // Do not consume the one-shot latch below the 25 km/h
                    // activation threshold; a contact that accelerates later
                    // must receive its speed event when it crosses the limit.
                    highSpeedRolled = true;
                    bool derailed = Roll(derailChance);
                    if (derailed)
                    {
                        Main.LogAlways("Derailing " + DescribeCar(contactCar) + ": brake shoe strike at " + speedKmh.ToString("0.0") + " km/h");
                        DerailBogie(contactBogie, "struck a brake shoe at speed");
                    }
                    if (Roll(dropChance)) EjectFromRail(speed);
                    if (derailed || !IsAnchored) return;
                }
            }

            // A wheel up against the back of the end stop. The shoe is being
            // shoved along the railhead ahead of it and is under the wheel's
            // flange rather than beneath its tread, which is the case that
            // picks a wheel off the rail.
            if (contactFromBehind && wheelTouching && contactBogie != null && contactCar != null)
            {
                if (!Main.Config.WrongShoeDerailEnabled)
                {
                    ResetHazardTimer();
                    return;
                }
                // A shoe being shoved by a standing wheel is not yet a hazard;
                // it is the movement over it that lifts the wheel.
                if (wheelStopped)
                {
                    ResetHazardTimer();
                    return;
                }
                // The unified speed table owns extra derailment/drop risk below
                // 25 km/h: a wrong-way contact at shunting speed must not add a
                // second random derailment event.
                if (Mathf.Abs(lastBogieSpeed) * 3.6f <= 25f)
                {
                    ResetHazardTimer();
                    return;
                }
                if (!HazardCheckDue(contactCar)) return;
                if (!Roll(Main.Config.WrongShoeDerailChance)) return;

                Main.LogAlways("Derailing " + DescribeCar(contactCar) + ": wheel riding a brake shoe placed the wrong way round.");
                DerailBogie(contactBogie, "hit a brake shoe placed the wrong way round");
                // v1.2.0: Eject the shoe after derailment.
                EjectFromRail(Mathf.Abs(lastBogieSpeed));
                return;
            }

            ResetHazardTimer();
        }

        /// <summary>
        /// Rolls for the shoe wedging in a switch frog while a wheel is dragging
        /// it along. Returns true when it jammed on this step.
        ///
        /// Only a shoe actually being carried is a candidate: a shoe placed by
        /// hand inside a frog is where the player put it and is left alone. The
        /// roll is made once per approach, not per step, so crossing a switch is
        /// one chance rather than fifty.
        /// </summary>
        private bool TryJamInFrog()
        {
            // Being dragged means the wedge is engaged and the wheel is moving,
            // which is exactly the Sliding/Breakaway pair.
            bool dragged = offsetLocked && (State == ShoeState.Sliding || State == ShoeState.Breakaway);
            if (!dragged)
            {
                frogJamRolled = false;
                return false;
            }

            string junctionName;
            if (!IsInFrogWindow(out junctionName))
            {
                // Clear of the switch again, so the next one gets its own roll.
                frogJamRolled = false;
                return false;
            }

            // Diagnostic: log when the shoe enters the frog window while being dragged.
            if (!frogJamRolled)
            {
                Main.Log("Shoe being dragged through frog window at " + (junctionName ?? "a switch") + " (span: " + railSpan.ToString("0.00") + " m, state: " + State + "). Jamming shoe.");
            }

            if (frogJamRolled) return false;
            frogJamRolled = true;
            jammedInFrog = true;
            jammedJunctionName = junctionName;
            jammedRolledBogie = null;
            // The wedge is given up here: a jammed shoe stops following the wheel
            // that was pushing it. Clearing the offset is what the follow code in
            // UpdateWheelEngagement reads, and it is the one write this layer
            // makes into engagement state.
            offsetLocked = false;
            pushSpanDirection = 0.0;
            ResetHazardTimer();
            Main.LogAlways("Brake shoe jammed in the frog at " + (junctionName ?? "a switch") + ".");
            return true;
        }

        /// <summary>
        /// Rolls against each axle passing over a shoe already jammed in a frog.
        ///
        /// v1.2.0: One-shot check at first contact, like high-speed collision.
        /// The shoe is a lump of steel in the crossing, so any wheel reaching it
        /// while moving is at immediate risk of derailment.
        /// </summary>
        private void UpdateJammedFrogHazard()
        {
            // wheelTouching, not just contactBogie: the axle search reaches three
            // metres up the track so an approach can be seen coming, and a wheel
            // still that far off has not struck the jammed shoe yet.
            if (!wheelTouching || contactBogie == null || contactCar == null || wheelStopped)
            {
                if (contactBogie == null || !wheelTouching) jammedRolledBogie = null;
                return;
            }

            // One-shot per bogie: once it's rolled for this axle, that's final.
            if (jammedRolledBogie == contactBogie) return;
            jammedRolledBogie = contactBogie;

            // Every bogie gets its own independent 50% roll.
            if (!Roll(0.5f)) return;

            Main.LogAlways("Derailing " + DescribeCar(contactCar) + ": wheel struck a brake shoe jammed in " + (jammedJunctionName ?? "a frog") + ".");
            DerailBogie(contactBogie, "struck a brake shoe jammed in a switch frog");
            // v1.2.0: Eject the jammed shoe after being struck.
            EjectFromRail(Mathf.Abs(lastBogieSpeed));
        }

        /// <summary>
        /// Whether the shoe currently sits within the frog window of a junction at
        /// either end of its own track, and the name of that junction.
        ///
        /// The node is the end of the point set, so this is a span comparison
        /// against 0 and the track's length. Only a track that genuinely ends at a
        /// Junction counts: a plain joint between two tracks has no crossing
        /// casting to catch a shoe.
        /// </summary>
        private bool IsInFrogWindow(out string junctionName)
        {
            junctionName = null;
            if (currentTrack == null) return false;

            double total;
            if (!RailPlacement.TryGetTrackSpan(currentTrack, out total)) return false;

            try
            {
                if (railSpan <= FrogWindowHalfLength && currentTrack.inJunction != null &&
                    IsConflictingTurnoutRoute(currentTrack.inJunction, currentTrack))
                {
                    junctionName = currentTrack.inJunction.name;
                    return true;
                }
                if (railSpan >= total - FrogWindowHalfLength && currentTrack.outJunction != null &&
                    IsConflictingTurnoutRoute(currentTrack.outJunction, currentTrack))
                {
                    junctionName = currentTrack.outJunction.name;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Main.Log("Junction lookup for the frog window failed: " + ex.Message);
            }
            return false;
        }

        // A frog window is only hazardous for an approach from an unselected
        // turnout branch. The Junction object already exposes the authoritative
        // selectedBranch and outBranches list; using them keeps a correctly
        // routed shoe from receiving a distance-only frog event.
        private static bool IsConflictingTurnoutRoute(Junction junction, RailTrack track)
        {
            if (junction == null || track == null || junction.outBranches == null) return false;
            for (int i = 0; i < junction.outBranches.Count; i++)
            {
                Junction.Branch branch = junction.outBranches[i];
                if (branch != null && branch.track == track)
                    return i != junction.selectedBranch;
            }
            return false;
        }

        /// <summary>
        /// Whether a hazard roll is due for this car, restarting the countdown when
        /// the car in contact changes. Returns true at most once per interval and
        /// arms the next one.
        /// </summary>
        private bool HazardCheckDue(TrainCar car)
        {
            float interval = Mathf.Max(0.5f, Main.Config.HazardCheckInterval);
            if (hazardContactCar != car)
            {
                hazardContactCar = car;
                nextHazardCheck = Time.time + interval;
                return false;
            }
            if (nextHazardCheck <= 0f)
            {
                nextHazardCheck = Time.time + interval;
                return false;
            }
            if (Time.time < nextHazardCheck) return false;
            nextHazardCheck = Time.time + interval;
            return true;
        }

        /// <summary>
        /// Drops the countdown so the next dangerous contact waits a full interval
        /// before its first roll instead of being rolled against on contact.
        /// </summary>
        private void ResetHazardTimer()
        {
            nextHazardCheck = 0f;
            hazardContactCar = null;
        }

        private static bool Roll(float chance)
        {
            if (chance <= 0f) return false;
            if (chance >= 1f) return true;
            return UnityEngine.Random.value < chance;
        }

        /// <summary>
        /// Derails one bogie through the game's own entry point.
        ///
        /// Bogie.Derail is what the game's SafetyDerailer and the traveller code
        /// call, and it opens by returning if the bogie has already derailed, so a
        /// second roll landing on the same bogie is harmless. callDerailOnOtherBogies
        /// is false: one axle picked off the rail by a shoe should take that bogie,
        /// not lift the whole consist at once.
        /// </summary>
        private void DerailBogie(Bogie bogie, string message)
        {
            try
            {
                if (bogie == null || bogie.HasDerailed) return;
                bogie.Derail(message, false, false);
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Warning("Derail call failed: " + ex.Message);
            }
        }

        private static string DescribeCar(TrainCar car)
        {
            if (car == null) return "a car";
            try { return car.ID ?? car.name; }
            catch { return "a car"; }
        }

        /// <summary>
        /// v1.2.0: Ejects the shoe from the rail with a lateral impulse, simulating
        /// being knocked off by a wheel. The impulse is scaled by the collision speed
        /// so a gentle bump produces a small slide while a high-speed strike sends
        /// the shoe tumbling clear.
        /// </summary>
        private void EjectFromRail(float collisionSpeed)
        {
            if (!IsAnchored) return;

            // Detach first so the shoe becomes a dynamic body again.
            DetachFromRail("knocked off by wheel impact");

            if (body == null) return;

            try
            {
                // Lateral direction: perpendicular to the rail, away from the track centre.
                Vector3 lateral = transform.right * railSide;
                // Small upward component so the shoe doesn't just slide flat.
                Vector3 up = Vector3.up * 0.15f;

                // Scale impulse by collision speed, clamped to config range.
                float speedFactor = Mathf.Clamp01(collisionSpeed / 20f);
                float impulse = Mathf.Lerp(Main.Config.EjectionImpulseMin, Main.Config.EjectionImpulseMax, speedFactor);

                Vector3 direction = (lateral + up).normalized;
                body.AddForce(direction * impulse * body.mass, ForceMode.Impulse);

                Main.LogAlways("Brake shoe ejected from rail (speed: " + (collisionSpeed * 3.6f).ToString("0.0") + " km/h, impulse: " + impulse.ToString("0.0") + " m/s)");
            }
            catch (Exception ex)
            {
                Main.Log("Ejection failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Drops everything remembered about the wheel currently on the shoe,
        /// including which way the wedge was being driven, so the next wheel
        /// starts from a clean state.
        /// </summary>
        private void ClearEngagement()
        {
            ReleaseStaticHold();
            contactBogie = null;
            contactCar = null;
            hasCapturedOffset = false;
            offsetLocked = false;
            contactFromBehind = false;
            pushSpanDirection = 0.0;
            // Cleared with the rest of it: the latch is a fact about one wheel,
            // and leaving it set would let the next wheel start out already
            // counted as stopped and be held there until it passed the higher
            // release threshold.
            wheelStopped = false;
            // Facts about one wheel, so they go with it. jammedInFrog is not
            // cleared here: the shoe stays wedged in the crossing after the wheel
            // that was on it has gone, and it is only freed by being picked up.
            //
            // wheelTouching is cleared at the top of every engagement step anyway;
            // it is repeated here for the paths that clear engagement without one,
            // so a detached shoe cannot be left reporting a stale contact.
            wheelTouching = false;
            ResetHazardTimer();
            jammedRolledBogie = null;
            frogJamRolled = false;
            // v1.2.0: Reset high-speed flag so the next wheel gets its own roll.
            highSpeedRolled = false;
            lastBogieSpeed = 0f;
        }

        /// <summary>
        /// Normal load on the braked axle, in newtons. TrainMassController
        /// already computes exactly this (TotalMass / NumberOfAxles * 9.81),
        /// including cargo and consumables, so use the game's own figure.
        /// </summary>
        private float GetAxleLoad(Bogie bogie)
        {
            TrainCar car = bogie.Car;
            try
            {
                if (car != null && car.massController != null)
                {
                    float perAxle = car.massController.WeightPerAxle;
                    if (perAxle > 1f && !float.IsNaN(perAxle) && !float.IsInfinity(perAxle)) return perAxle;
                }
            }
            catch (Exception ex) { Main.Log("WeightPerAxle read failed: " + ex.Message); }

            float gravity = Mathf.Abs(Physics.gravity.y);
            if (car == null || car.rb == null) return 1000f * gravity;
            return Mathf.Max(1f, car.rb.mass * gravity / Mathf.Max(1, car.NumberOfAxles));
        }

        /// <summary>
        /// v1.2.0: Calculates the additional force component needed to resist a car
        /// sliding down a slope due to gravity. This is added on top of the base
        /// friction model, not replacing it.
        ///
        /// Uses the bogie's forward vector to determine track grade. The Y component
        /// is sin(angle), which gives the fraction of gravitational force pulling
        /// the car down the slope.
        ///
        /// Returns the force per shoe, already divided by the number of active shoes
        /// on this car.
        /// </summary>
        private float GetSlopeForceComponent(Bogie bogie, TrainCar car)
        {
            if (car == null || bogie == null) return 0f;

            try
            {
                // Total mass of the car including cargo.
                float totalMass = car.rb != null ? car.rb.mass : 1000f;
                float gravity = Mathf.Abs(Physics.gravity.y);

                // The bogie's forward vector points along the track. Its Y component
                // is the sine of the track's grade angle.
                Vector3 trackForward = bogie.transform.forward;
                float sinAngle = Mathf.Abs(trackForward.y);

                // F_slope = m * g * sin(angle)
                float totalSlopeForce = totalMass * gravity * sinAngle;

                // Distribute across all shoes on this car.
                int activeShoes = CountActiveShoes(car);
                float perShoe = totalSlopeForce / Mathf.Max(1, activeShoes);

                return perShoe;
            }
            catch (Exception ex)
            {
                Main.Log("GetSlopeForceComponent failed: " + ex.Message);
                return 0f;
            }
        }

        /// <summary>
        /// Scans the bogies registered on the shoe's track and returns the axle
        /// closest to the shoe in span space, together with its span.
        /// </summary>
        private bool TryFindEngagedAxle(out Bogie bogie, out double axleSpan, out float direction)
        {
            return TryFindEngagedAxle(null, out bogie, out axleSpan, out direction);
        }

        /// <summary>
        /// Finds the nearest axle, optionally restricted to one exact wagon.
        /// The unrestricted form is retained for the physics and placement
        /// callers; job validation uses the restricted form so another nearby
        /// wagon cannot win the nearest-axle race.
        /// </summary>
        private bool TryFindEngagedAxle(TrainCar requiredCar, out Bogie bogie, out double axleSpan, out float direction)
        {
            return TryFindEngagedAxle(requiredCar, Main.Config.CaptureHalfLength + 3.0, out bogie, out axleSpan, out direction);
        }

        private bool TryFindEngagedAxle(TrainCar requiredCar, double searchRadius, out Bogie bogie, out double axleSpan, out float direction)
        {
            bogie = null;
            axleSpan = 0.0;
            direction = 1f;

            // Only wheels within this distance are considered at all; beyond it
            // the shoe is simply parked on the rail.
            double reach = Math.Max(0.01, searchRadius);
            double bestDistance = reach;
            bool found = false;

            // A wheel approaching a shoe that sits near a track boundary is
            // registered on the track on the far side of that boundary, so the
            // search covers the neighbours too. Their spans are rewritten into
            // this shoe's own coordinate, which is what lets a single comparison
            // handle both cases.
            int mapCount = BuildSearchMaps(reach);

            for (int m = 0; m < mapCount; m++)
            {
                RailPlacement.SpanMap map = searchMaps[m];
                if (map.Track == null) continue;

                HashSet<Bogie> bogies = RailPlacement.GetBogiesOnTrack(map.Track);
                if (bogies == null || bogies.Count == 0) continue;

                foreach (Bogie candidate in bogies)
                {
                    if (candidate == null || candidate.HasDerailed) continue;
                    if (candidate.track != map.Track) continue;
                    if (candidate.traveller == null) continue;
                    if (requiredCar != null && candidate.Car != requiredCar) continue;

                    double bogieSpan;
                    try { bogieSpan = candidate.traveller.Span; }
                    catch (Exception ex) { Main.Log("Traveller span read failed: " + ex.Message); continue; }

                    float sign = candidate.TrackDirectionSign;
                    if (sign == 0f) sign = 1f;

                    // Each axle sits at a fixed longitudinal offset from the bogie
                    // pivot; convert that offset into the track's span direction.
                    Bogie.AxleInfo[] axles = null;
                    try { axles = candidate.Axles; }
                    catch (Exception ex) { Main.Log("Axle read failed: " + ex.Message); }

                    // The bogie's direction sign is expressed on its own track, so
                    // it flips with the map when the two span axes oppose.
                    float mappedSign = map.Alignment >= 0f ? sign : -sign;

                    if (axles == null || axles.Length == 0)
                    {
                        double onlySpan = map.Map(bogieSpan);
                        double distance = onlySpan - railSpan;
                        if (distance < 0.0) distance = -distance;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            axleSpan = onlySpan;
                            bogie = candidate;
                            direction = mappedSign;
                            found = true;
                        }
                        continue;
                    }

                    for (int i = 0; i < axles.Length; i++)
                    {
                        if (axles[i] == null) continue;
                        double span = map.Map(bogieSpan + axles[i].distanceFromBogiePivot * sign);
                        double distance = span - railSpan;
                        if (distance < 0.0) distance = -distance;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            axleSpan = span;
                            bogie = candidate;
                            direction = mappedSign;
                            found = true;
                        }
                    }
                }
            }

            return found;
        }

        // Reused between steps so the joint-aware search allocates nothing per
        // physics step: the shoe's own track plus at most one neighbour per end.
        private readonly RailPlacement.SpanMap[] searchMaps = new RailPlacement.SpanMap[3];

        /// <summary>
        /// The tracks to search for wheels, each with the map that expresses its
        /// spans in this shoe's coordinate. Always contains the shoe's own track;
        /// a neighbour is added only when the shoe is within `reach` of that end,
        /// so the branch lookups are skipped for a shoe in mid-track.
        /// </summary>
        private int BuildSearchMaps(double reach)
        {
            int count = 0;
            RailPlacement.SpanMap identity = default(RailPlacement.SpanMap);
            identity.Track = currentTrack;
            identity.Origin = 0.0;
            identity.Alignment = 1f;
            searchMaps[count++] = identity;

            double total;
            if (!RailPlacement.TryGetTrackSpan(currentTrack, out total)) return count;

            RailPlacement.SpanMap map;
            if (railSpan < reach && RailPlacement.TryGetNeighbour(currentTrack, false, out map))
                searchMaps[count++] = map;
            if (railSpan > total - reach && RailPlacement.TryGetNeighbour(currentTrack, true, out map))
                searchMaps[count++] = map;
            return count;
        }

        /// <summary>
        /// Hands the shoe over to the next track once a wheel has pushed it past
        /// the end of its own point set.
        ///
        /// Without this the shoe stops dead at a track boundary: TryGetPoseAtSpan
        /// clamps span to the track's own length, so the shoe would be pinned at
        /// the joint while the wheel kept rolling straight through it.
        ///
        /// Everything carried across is converted with the same map: span through
        /// Unmap, and the span-difference quantities (the captured wheel offset and
        /// the direction the wedge is being driven) by the alignment, since that is
        /// the derivative of the map. Rail side and facing flip with an opposed
        /// alignment because forward flips, and right is Cross(up, forward).
        /// </summary>
        private void MigrateAcrossJointIfNeeded()
        {
            if (currentTrack == null || !spanValid) return;

            double total;
            if (!RailPlacement.TryGetTrackSpan(currentTrack, out total)) return;
            if (railSpan >= 0.0 && railSpan <= total) return;

            bool outEnd = railSpan > total;
            RailPlacement.SpanMap map;
            if (!RailPlacement.TryGetNeighbour(currentTrack, outEnd, out map))
            {
                // Nothing beyond this joint. A bogie derails here; a shoe just
                // stops at the last span its own track has, rather than being
                // driven off the end of the point set.
                railSpan = outEnd ? total : 0.0;
                return;
            }

            RailTrack previous = currentTrack;
            currentTrack = map.Track;
            railSpan = map.Unmap(railSpan);
            if (map.Alignment < 0f)
            {
                railSide = -railSide;
                isReversed = !isReversed;
                capturedSpanOffset = -capturedSpanOffset;
                pushSpanDirection = -pushSpanDirection;
            }

            Main.Log("Shoe crossed joint from '" + previous.name + "' to '" + currentTrack.name + "'.");
        }

        /// <summary>
        /// Places the shoe at its current span on the correct rail with the
        /// correct orientation. Driven from span, so the shoe stays exactly on
        /// the railhead through curves, grades and Origin Shift.
        /// </summary>
        private void ApplyRailPose()
        {
            Vector3 center, forward, up;
            if (!RailPlacement.TryGetPoseAtSpan(currentTrack, railSpan, out center, out forward, out up))
                return;

            Vector3 right = Vector3.Cross(up, forward).normalized;
            Vector3 position = RailPlacement.GetRailheadPosition(currentTrack, center, right, up, railSide);
            Quaternion rotation = Quaternion.LookRotation(forward * (isReversed ? -1f : 1f), up);

            if (!IsFinite(position)) return;

            // One writer only. Doing MovePosition and then also assigning the
            // transform in the same step makes the two disagree: the assignment
            // teleports immediately while MovePosition asks PhysX to sweep there
            // over the step, and with interpolation on, the renderer is left
            // blending between a pose that already moved and a target it is
            // still travelling to. That fight was the visible jitter.
            if (body != null && body.isKinematic)
            {
                // Kinematic sweep, the same way the game moves its own bogies.
                // Keeps the shoe swept rather than teleported, so contacts with
                // non-kinematic bodies still resolve.
                body.MovePosition(position);
                body.MoveRotation(rotation);
            }
            else
            {
                transform.SetPositionAndRotation(position, rotation);
            }
        }

        public System.ValueTuple<Vector3, Quaternion> GetGrabAnchor()
        {
            return new System.ValueTuple<Vector3, Quaternion>(BrakeShoeFactory.HeldItemPosition, BrakeShoeFactory.HeldItemRotation);
        }

        /// <summary>
        /// Finds the item control and subscribes to the lifecycle events the
        /// game raises itself.
        ///
        /// Polling IsGrabbed is not reliable here: ItemNonVR.IsGrabbed forwards
        /// to its private grabHandler and returns FALSE while that field is
        /// still null (verified in IL), which is the case until Setup has run.
        /// A poll therefore reports "not held" for the first frames of the
        /// item's life, and the span-driven rail pose keeps teleporting the
        /// shoe back onto the rail while the player is holding it. That is the
        /// shoe vanishing out of the hand. ControlImplBase.FireGrabbed /
        /// FireUngrabbed raise Grabbed / Ungrabbed unconditionally, so the
        /// events cannot fail open the way the poll does.
        /// </summary>
        private void EnsureItemControl()
        {
            if (item == null) item = GetComponent<DV.CabControls.ItemBase>();
            if (item == null || eventsHooked) return;
            item.Grabbed += OnItemGrabbed;
            item.ItemInventoryStateChanged += OnItemInventoryStateChanged;
            eventsHooked = true;
        }

        /// <summary>
        /// Makes the shoe refuse to be picked up while a wheel is rolling over
        /// it, and hands it back the moment the wheel stops.
        ///
        /// This goes through AGrabHandler.AssignInteractionPassThrough, which is
        /// the game's own hook for "this collider is not interactable right now".
        /// GrabberRaycasterDV.RaycastPassThrough asks the handler
        /// InteractionPassThrough(hitPoint) for every hit it walks, and on true
        /// it skips that hit and ultimately returns null instead of the handler
        /// (verified in IL at IL_013C and IL_016B). Because
        /// GrabberInteractionHandlerDV.IdleStartInteraction reads
        /// CurrentlyRaycasted and gives up when it is null, blocking the ray
        /// removes the hover prompt and the grab together, rather than letting
        /// the player start a grab that then has to be undone.
        ///
        /// Nothing on an item assigns this delegate - only the cab controls do -
        /// so taking it cannot displace behaviour the game wanted. It is cleared
        /// again as soon as the block lifts, which leaves the field null, and a
        /// null delegate is the untouched default: InteractionPassThrough
        /// returns false without calling anything.
        ///
        /// InteractionAllowed was the other candidate and does not work here:
        /// only the NonVR cab controls route it into their pass-through, and no
        /// item type reads it on the grab path at all.
        /// </summary>
        private void UpdatePickupBlock()
        {
            bool block = IsUnderRollingWheel;
            if (block == pickupBlocked) return;

            if (grabHandler == null) grabHandler = GetComponent<DV.Interaction.AGrabHandler>();
            if (grabHandler == null) return;

            try
            {
                if (block)
                {
                    grabHandler.AssignInteractionPassThrough(BlockInteractionWhileRolling);
                    Main.Log("Pickup blocked: a wheel is rolling on the shoe.");
                }
                else
                {
                    grabHandler.AssignInteractionPassThrough(null);
                    Main.Log("Pickup allowed again: the wheel is no longer rolling.");
                }
                pickupBlocked = block;
            }
            catch (Exception ex)
            {
                Main.LogAlways("Failed to toggle the pickup block: " + ex.Message);
            }
        }

        // Answers the raycaster. The hit point is irrelevant: the whole shoe is
        // out of reach while the block is on, not just part of it.
        private bool BlockInteractionWhileRolling(Vector3 hitPoint)
        {
            return true;
        }

        /// <summary>
        /// Drops the block unconditionally. Called wherever the shoe stops being
        /// a shoe under a wheel, so a pooled or detached item can never be left
        /// permanently unpickable by a delegate nobody clears.
        /// </summary>
        private void ReleasePickupBlock()
        {
            if (!pickupBlocked) return;
            pickupBlocked = false;
            if (grabHandler == null) grabHandler = GetComponent<DV.Interaction.AGrabHandler>();
            if (grabHandler == null) return;
            try
            {
                grabHandler.AssignInteractionPassThrough(null);
                Main.Log("Pickup block released.");
            }
            catch (Exception ex)
            {
                Main.LogAlways("Failed to release the pickup block: " + ex.Message);
            }
        }

        private void UnhookItemEvents()
        {
            if (!eventsHooked || item == null) return;
            item.Grabbed -= OnItemGrabbed;
            item.ItemInventoryStateChanged -= OnItemInventoryStateChanged;
            eventsHooked = false;
        }

        /// <summary>
        /// The player has taken the shoe, so the grab code owns the transform
        /// from now on and the rail anchor has to go.
        /// </summary>
        private void OnItemGrabbed(DV.CabControls.ControlImplBase control)
        {
            // Not during the placement grace window. FinalizePlacement anchors
            // the shoe while it is still held, and the game can raise Grabbed
            // around that same moment; honouring it there would throw away the
            // anchor that was just established and drop the shoe through the
            // rail.
            if (IsInPlacementGrace()) return;
            if (snappedToRail || currentTrack != null)
            {
                DetachFromRail("grabbed by the player");
            }
        }

        /// <summary>
        /// Releases the rail anchor when the shoe is genuinely put away. Without
        /// this a shoe could be pocketed while still attached: the object is
        /// disabled and reparented into the container, but it keeps its track and
        /// span, so taking it out again snapped it back to the rail it used to be
        /// on.
        ///
        /// Only actions that really move the shoe into storage count. This used
        /// to detach on every action except Drop, which was wrong: the enum
        /// (verified in DV.Inventory.dll) also carries Unequip, Move, Reserve,
        /// Unreserve, Lock, Unlock and the four Belt* states, and taking the shoe
        /// out of the hand to put it on a rail raises one of those. That threw
        /// away the anchor immediately after placement - the shoe sank through
        /// the railhead and wheels rolled through it, because both behaviours
        /// depend on the anchor being held.
        /// </summary>
        private void OnItemInventoryStateChanged(DV.CabControls.ItemBase changed,
            DV.InventorySystem.InventoryActionType actionType,
            DV.InventorySystem.InventoryItemState itemState)
        {
            bool storedAway =
                actionType == DV.InventorySystem.InventoryActionType.Add ||
                actionType == DV.InventorySystem.InventoryActionType.Swap ||
                actionType == DV.InventorySystem.InventoryActionType.Purge ||
                actionType == DV.InventorySystem.InventoryActionType.Destroy;
            if (!storedAway) return;
            if (IsInPlacementGrace()) return;
            if (snappedToRail || currentTrack != null)
                DetachFromRail("inventory action " + actionType);
        }

        // How long after AttachToRail the anchor is protected from the grab and
        // inventory teardown paths. Placement happens inside FinalizePlacement
        // while the shoe is still in the hand, and the release follows a moment
        // later, so those paths would otherwise undo the anchor they just got.
        //
        // Deliberately short. It only has to outlast the release, which is a
        // frame or two, and a window this size cannot swallow a real grab: the
        // FixedUpdate safety net re-checks IsGrabbed once the window expires and
        // detaches then, so a shoe the player is genuinely holding frees itself
        // within a few hundredths of a second instead of being teleported back
        // onto the rail. A long window would reintroduce the shoe vanishing out
        // of the hand.
        private const float PlacementGraceSeconds = 0.2f;
        private float placementGraceUntil = -1f;

        private bool IsInPlacementGrace()
        {
            return placementGraceUntil > 0f && Time.time <= placementGraceUntil;
        }

        /// <summary>
        /// v1.2.0: Finds another brake shoe ahead of this one on the same track,
        /// in the direction of travel. Used for shoe-to-shoe collision detection.
        /// </summary>
        private BrakeShoeBehaviour FindBlockingShoe(double proposedSpan, double travelDirection)
        {
            if (currentTrack == null || travelDirection == 0.0) return null;

            // Search window: a few metres ahead in the direction of travel.
            double searchDistance = 2.0; // metres
            double searchSpan = proposedSpan + (searchDistance * travelDirection);

            foreach (BrakeShoeBehaviour other in Main.Shoes)
            {
                if (other == null || other == this) continue;
                if (!other.IsAnchored || other.currentTrack != currentTrack) continue;

                // Check if the other shoe is ahead of us in the direction of travel.
                double delta = (other.railSpan - proposedSpan) * travelDirection;
                if (delta > 0 && delta < searchDistance)
                {
                    return other;
                }
            }

            return null;
        }

        private static int CountActiveShoes(TrainCar car)
        {
            if (activeShoeCountFrame != Time.frameCount)
            {
                activeShoeCountFrame = Time.frameCount;
                ActiveShoeCounts.Clear();
                foreach (BrakeShoeBehaviour shoe in Main.Shoes)
                {
                    // offsetLocked, not hasCapturedOffset: a shoe lying the wrong
                    // way round is in contact but produces no resistance, so
                    // counting it would water down the shoes that do.
                    if (shoe == null || !shoe.offsetLocked || shoe.contactCar == null) continue;
                    int count;
                    ActiveShoeCounts.TryGetValue(shoe.contactCar, out count);
                    ActiveShoeCounts[shoe.contactCar] = count + 1;
                }
            }
            int result;
            return ActiveShoeCounts.TryGetValue(car, out result) ? Mathf.Max(1, result) : 1;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

    }

    internal static class RailPlacement
    {
        private sealed class RailSnapCache
        {
            internal RailTrack Track;
            internal readonly RailTrack[] SingleTrack = new RailTrack[1];
            internal Vector3 Source;
            internal Vector3 Position;
            internal Vector3 Forward;
            internal Vector3 Up;
            // Resolved on the way to the pose and kept so the occupancy test can
            // read them instead of repeating the same two point-set queries on
            // every placement frame.
            internal double Span;
            internal float Side;
            internal bool HasSample;
            internal bool Valid;
        }

        // The distance an aim point may drift before the cached sample above is
        // recomputed. Shared so the cache reader cannot disagree with the writer.
        private const float SampleToleranceSqr = 0.000025f;

        private static readonly Dictionary<int, RailSnapCache> Caches = new Dictionary<int, RailSnapCache>();

        internal static bool TryGetSnap(int cacheId, RailTrack aimedTrack, Vector3 source, bool reversed, out Vector3 position, out Quaternion rotation)
        {
            position = source;
            rotation = Quaternion.identity;
            if (aimedTrack == null) return false;
            try
            {
                RailSnapCache cache;
                if (!Caches.TryGetValue(cacheId, out cache))
                {
                    cache = new RailSnapCache();
                    Caches.Add(cacheId, cache);
                }

                if (cache.HasSample && cache.Track == aimedTrack && (cache.Source - source).sqrMagnitude <= SampleToleranceSqr)
                {
                    if (!cache.Valid) return false;
                    position = cache.Position;
                    rotation = Quaternion.LookRotation(cache.Forward * (reversed ? -1f : 1f), cache.Up);
                    return true;
                }

                cache.Source = source;
                cache.HasSample = true;
                cache.Valid = false;
                cache.Track = aimedTrack;
                cache.SingleTrack[0] = aimedTrack;

                // Resolve the aim point to a span, then sample the pose from that
                // span with exactly the same call the placed shoe uses each
                // physics step. Preview and final rest pose therefore agree.
                double span;
                if (!TryGetSpanAt(aimedTrack, source, out span)) return false;

                Vector3 center, forward, up;
                if (!TryGetPoseAtSpan(aimedTrack, span, out center, out forward, out up)) return false;

                Vector3 right = Vector3.Cross(up, forward).normalized;

                // Which of the two rails the player aimed at, from the lateral
                // offset of the aim point relative to the track centreline.
                Vector3 relative = source - center;
                float lateral = Vector3.Dot(relative, right);
                float side = lateral >= 0f ? 1f : -1f;

                position = GetRailheadPosition(aimedTrack, center, right, up, side);

                cache.Valid = IsFinite(position) && IsFinite(forward) && IsFinite(up);
                if (!cache.Valid) return false;
                cache.Span = span;
                cache.Side = side;
                cache.Position = position;
                cache.Forward = forward;
                cache.Up = up;
                rotation = Quaternion.LookRotation(forward * (reversed ? -1f : 1f), up);
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Rail query failed: " + ex.Message);
                return false;
            }
        }

        internal static void ClearCache(int cacheId)
        {
            Caches.Remove(cacheId);
        }

        /// <summary>
        /// The span and rail side TryGetSnap already resolved for this aim point,
        /// without querying the point set again. Only reports a hit when the
        /// cached sample is for the same track and aim point the caller is asking
        /// about, so a stale sample can never answer for a different spot.
        /// </summary>
        internal static bool TryGetCachedSpanAndSide(int cacheId, RailTrack aimedTrack, Vector3 source, out double span, out float side)
        {
            span = 0.0;
            side = 1f;
            if (aimedTrack == null) return false;
            RailSnapCache cache;
            if (!Caches.TryGetValue(cacheId, out cache)) return false;
            if (!cache.HasSample || !cache.Valid) return false;
            if (cache.Track != aimedTrack) return false;
            if ((cache.Source - source).sqrMagnitude > SampleToleranceSqr) return false;
            span = cache.Span;
            side = cache.Side;
            return true;
        }

        // ICollection, not an array: RailTrack.GetClosest takes ICollection
        // itself, so callers can hand over a reused List shortlist without
        // allocating a fresh array on every frame.
        internal static bool TryGetClosestPoint(Vector3 source, ICollection<RailTrack> tracks, out RailTrack track, out DV.PointSet.EquiPointSet.Point point)
        {
            return TryGetPoint(source, tracks, out track, out point);
        }

        internal static Vector3 GetCurrentMoveStatic()
        {
            return GetCurrentMove();
        }

        /// <summary>
        /// Full hierarchy path of a track's GameObject, "root/child/.../track".
        /// The game includes these strings in RailTrackRegistryBase.TracksHash
        /// (via GameObjectUtils.GetPath), but that layout hash does not provide
        /// a unique identifier for each track.
        /// Paths are not unique: turntables and repeated junction prefabs can
        /// have identically named siblings. FindTrack also uses the saved item
        /// position to disambiguate those paths.
        /// </summary>
        internal static string GetTrackPath(RailTrack track)
        {
            if (track == null) return null;
            try
            {
                // GameObjectUtils.GetPath lives in DV.Utils and does precisely
                // this walk, but it is reimplemented here so a shoe's save data
                // never depends on a helper the mod does not otherwise touch.
                Transform t = track.transform;
                string path = t.name;
                t = t.parent;
                while (t != null)
                {
                    path = t.name + "/" + path;
                    t = t.parent;
                }
                return path;
            }
            catch (Exception ex)
            {
                Main.Log("GetTrackPath failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolves the track a saved shoe was anchored to.
        ///
        /// Prefer the saved hierarchy, using position when several tracks have
        /// that path. In level520 all eleven turntable tracks are siblings named
        /// "[railway]/Turntable Track"; many junction paths repeat too. Returning
        /// the first path match therefore moved shoes to another station. The
        /// game's item loader restores world position before the load callback,
        /// and the turntables' point sets have already been rotated to their
        /// saved angles, so geometry resolves the correct member of that group.
        /// </summary>
        internal static RailTrack FindTrack(string trackPath, string trackName, Vector3 position)
        {
            try
            {
                RailTrack[] all = GetAllTracks();
                if (all == null || all.Length == 0) return null;

                if (!string.IsNullOrEmpty(trackPath))
                {
                    List<RailTrack> pathMatches = new List<RailTrack>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] == null) continue;
                        if (GetTrackPath(all[i]) == trackPath) pathMatches.Add(all[i]);
                    }
                    if (pathMatches.Count == 1) return pathMatches[0];
                    if (pathMatches.Count > 1)
                    {
                        RailTrack resolved = FindTrackByPosition(pathMatches.ToArray(), null, position);
                        if (resolved == null)
                            Main.Log("Saved track path is shared by " + pathMatches.Count +
                                " tracks, but none is near the saved shoe position: " + trackPath);
                        // An unresolved duplicate remains a pending restore. Do
                        // not apply its saved span to an unrelated name match.
                        return resolved;
                    }
                    // Reparenting can change a path, but rotating a turntable
                    // does not. Older saves still have name and position as a
                    // fallback when the hierarchy no longer matches.
                    Main.Log("Saved track path did not resolve, falling back to position: " + trackPath);
                }

                // Restrict the closest-track query to same-named candidates when a
                // name was saved. GetClosest picks the nearest of whatever it is
                // handed, and a shoe on a turntable sits within a metre or two of
                // the yard tracks the bridge lines up with; without the filter the
                // approach track could win over the bridge itself.
                RailTrack byPosition = FindTrackByPosition(all, trackName, position);
                if (byPosition != null) return byPosition;

                if (string.IsNullOrEmpty(trackName)) return null;

                // Position did not settle it either - the shoe may have been saved
                // far from any point set the query accepts. A unique name is still
                // a sound answer; an ambiguous one is not, so it is refused rather
                // than teleporting the shoe across the map.
                RailTrack match = null;
                int matches = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null || all[i].name != trackName) continue;
                    matches++;
                    if (matches > 1) break;
                    match = all[i];
                }

                if (matches == 1) return match;
                if (matches > 1) Main.Log("Saved track name '" + trackName + "' is ambiguous and the position did not resolve it.");
                return null;
            }
            catch (Exception ex)
            {
                Main.Log("FindTrack failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The track the shoe is physically standing on, preferring candidates that
        /// carry the saved name. Uses the same RailTrack.GetClosest the placement
        /// path uses, so a restored anchor lands on exactly the track the player
        /// would have hit aiming at that spot.
        /// </summary>
        private static RailTrack FindTrackByPosition(RailTrack[] all, string trackName, Vector3 position)
        {
            if (!IsFinite(position)) return null;

            List<RailTrack> candidates = new List<RailTrack>();
            if (!string.IsNullOrEmpty(trackName))
            {
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == trackName) candidates.Add(all[i]);
            }
            // A saved name that is absent means the target is not available yet.
            // Choosing a neighbouring track would reuse the saved span on the
            // wrong geometry. A null result lets the existing restore retry wait.
            if (!string.IsNullOrEmpty(trackName) && candidates.Count == 0) return null;
            if (string.IsNullOrEmpty(trackName))
            {
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) candidates.Add(all[i]);
            }
            if (candidates.Count == 0) return null;

            RailTrack track;
            DV.PointSet.EquiPointSet.Point point;
            if (!TryGetPoint(position, candidates, out track, out point)) return null;

            // A shoe rests on the railhead, so its saved position is within the
            // gauge of its own track. Anything further away means the query found
            // some unrelated track and the anchor should not be trusted to it.
            Vector3 railPoint = new Vector3((float)point.position.x, (float)point.position.y, (float)point.position.z) + GetCurrentMove();
            if ((railPoint - position).sqrMagnitude > PositionMatchToleranceSqr)
            {
                Main.Log("Closest track to the saved position was " + (railPoint - position).magnitude.ToString("0.00") + " m away, too far to anchor.");
                return null;
            }

            return track;
        }

        // Half the track gauge plus slack for the shoe's own offset off the rail
        // centre line. Comfortably larger than the distance from a seated shoe to
        // its point set, and far smaller than the spacing between neighbouring
        // tracks in a yard.
        private const float PositionMatchToleranceSqr = 3f * 3f;

        private static RailTrack[] GetAllTracks()
        {
            try
            {
                RailTrackRegistryBase registry = RailTrackRegistryBase.Instance;
                if (registry != null)
                {
                    RailTrack[] tracks = registry.AllTracks;
                    if (tracks != null && tracks.Length > 0) return tracks;
                }
            }
            catch (Exception ex) { Main.Log("Track registry read failed: " + ex.Message); }
            return UnityEngine.Object.FindObjectsOfType<RailTrack>();
        }

        /// <summary>
        /// Converts a Unity world position into a span along the track's kinked
        /// point set - the same 1-D coordinate the game's bogie travellers use.
        /// </summary>
        internal static bool TryGetSpanAt(RailTrack track, Vector3 worldPosition, out double span)
        {
            span = 0.0;
            if (track == null) return false;
            try
            {
                DV.PointSet.EquiPointSet pointSet = track.GetKinkedPointSet();
                if (pointSet == null || pointSet.points == null || pointSet.points.Length < 2) return false;

                // Point positions live in origin-shifted space, so shift the query
                // the same way RailTrack.GetClosestPoint does internally.
                Vector3 shifted = worldPosition - GetCurrentMove();
                Vector3d target = new Vector3d(shifted.x, shifted.y, shifted.z);

                DV.PointSet.EquiPointSet.Point[] points = pointSet.points;
                int best = -1;
                double bestSqr = double.MaxValue;
                for (int i = 0; i < points.Length; i++)
                {
                    double dx = points[i].position.x - target.x;
                    double dy = points[i].position.y - target.y;
                    double dz = points[i].position.z - target.z;
                    double sqr = dx * dx + dy * dy + dz * dz;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = i;
                    }
                }
                if (best < 0) return false;

                // Refine within the segment: project onto whichever neighbouring
                // segment the point actually falls on, so span is continuous
                // rather than quantised to the point spacing.
                span = points[best].span;
                double refined;
                if (best + 1 < points.Length && TryProject(points[best], points[best + 1], target, out refined)) span = refined;
                else if (best > 0 && TryProject(points[best - 1], points[best], target, out refined)) span = refined;

                if (double.IsNaN(span) || double.IsInfinity(span)) return false;
                if (span < 0.0) span = 0.0;
                if (span > pointSet.span) span = pointSet.span;
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("TryGetSpanAt failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryProject(DV.PointSet.EquiPointSet.Point a, DV.PointSet.EquiPointSet.Point b, Vector3d target, out double span)
        {
            span = a.span;
            double ax = b.position.x - a.position.x;
            double ay = b.position.y - a.position.y;
            double az = b.position.z - a.position.z;
            double lenSqr = ax * ax + ay * ay + az * az;
            if (lenSqr < 1e-9) return false;
            double tx = target.x - a.position.x;
            double ty = target.y - a.position.y;
            double tz = target.z - a.position.z;
            double t = (tx * ax + ty * ay + tz * az) / lenSqr;
            if (t < 0.0 || t > 1.0) return false;
            double segmentSpan = b.span - a.span;
            if (segmentSpan <= 0.0) segmentSpan = a.spanToNextPoint;
            span = a.span + t * segmentSpan;
            return true;
        }

        /// <summary>
        /// Samples the track's centreline pose at a span. Uses a temporary
        /// traveller so the shoe follows exactly the geometry a bogie would,
        /// including the interpolation between point-set points.
        /// </summary>
        internal static bool TryGetPoseAtSpan(RailTrack track, double span, out Vector3 position, out Vector3 forward, out Vector3 up)
        {
            position = Vector3.zero;
            forward = Vector3.forward;
            up = Vector3.up;
            if (track == null) return false;
            try
            {
                DV.PointSet.EquiPointSet pointSet = track.GetKinkedPointSet();
                if (pointSet == null || pointSet.points == null || pointSet.points.Length < 2) return false;

                if (span < 0.0) span = 0.0;
                if (span > pointSet.span) span = pointSet.span;

                DV.PointSet.PointSetTraveller traveller = GetPoseTraveller(pointSet);
                traveller.MoveToSpan(span);

                Vector3d worldPosition = traveller.worldPosition;
                position = new Vector3((float)worldPosition.x, (float)worldPosition.y, (float)worldPosition.z) + GetCurrentMove();
                up = traveller.worldUp.normalized;
                forward = Vector3.ProjectOnPlane(traveller.worldForward, up).normalized;
                if (forward.sqrMagnitude < 0.5f || up.sqrMagnitude < 0.5f) return false;

                // Re-orthogonalise so the basis stays clean through kinks.
                Vector3 right = Vector3.Cross(up, forward).normalized;
                up = Vector3.Cross(forward, right).normalized;
                return IsFinite(position) && IsFinite(forward) && IsFinite(up);
            }
            catch (Exception ex)
            {
                Main.Log("TryGetPoseAtSpan failed: " + ex.Message);
                return false;
            }
        }

        // One traveller per point set. A single shared traveller would be
        // rebuilt on every call once two shoes sit on different tracks, since
        // each would evict the other's point set.
        private static readonly Dictionary<DV.PointSet.EquiPointSet, DV.PointSet.PointSetTraveller> PoseTravellers =
            new Dictionary<DV.PointSet.EquiPointSet, DV.PointSet.PointSetTraveller>();

        private static DV.PointSet.PointSetTraveller GetPoseTraveller(DV.PointSet.EquiPointSet pointSet)
        {
            DV.PointSet.PointSetTraveller existing;
            if (PoseTravellers.TryGetValue(pointSet, out existing) && existing != null) return existing;

            // Bounded: shoes only ever occupy a handful of tracks, but a save
            // reload replaces point sets, so do not let stale keys accumulate.
            if (PoseTravellers.Count > 32) PoseTravellers.Clear();

            // preciseInterpolation: false matches Bogie.SetTrack. With true,
            // UpdateWorldPosition switches to Catmul-Rom instead of the linear
            // lerp the bogies use, so the shoe and the wheel would resolve a
            // shared span to slightly different places through curves.
            DV.PointSet.PointSetTraveller created = new DV.PointSet.PointSetTraveller(pointSet, false);
            PoseTravellers[pointSet] = created;
            return created;
        }


        /// <summary>
        /// Lateral distance from the track centreline to a railhead centre, and
        /// the height of the railhead above the centreline point, both read from
        /// the game's own track data rather than assumed.
        ///
        /// RailwayMeshGenerator.Start builds each rail from
        /// railType.railShape.GetPoints2D(0.1f) offset sideways by
        /// (gauge * 0.5f + railEdgeOffset), negated for the left rail. So the
        /// profile's own Y extent gives the railhead height and that offset
        /// gives the lateral placement, for whatever RailType a track uses.
        /// </summary>
        private struct RailGeometry
        {
            public float LateralOffset;
            public float HeadHeight;
        }

        private static readonly Dictionary<int, RailGeometry> RailGeometryCache = new Dictionary<int, RailGeometry>();

        internal static bool TryGetRailGeometry(RailTrack track, out float lateralOffset, out float headHeight)
        {
            lateralOffset = Settings.RailGaugeHalfWidth;
            headHeight = Settings.RailHeadOffset;
            if (track == null) return false;

            try
            {
                RailType railType = track.railType;
                if (railType == null) return false;

                int key = railType.GetInstanceID();
                RailGeometry cached;
                if (RailGeometryCache.TryGetValue(key, out cached))
                {
                    lateralOffset = cached.LateralOffset;
                    headHeight = cached.HeadHeight + Settings.RailHeadOffset;
                    return true;
                }

                RailGeometry geometry;
                geometry.LateralOffset = railType.gauge * 0.5f + railType.railEdgeOffset;
                // Raw profile height, without the user trim: the trim is added on
                // read so moving the slider takes effect without a cache flush.
                geometry.HeadHeight = 0f;

                // Shape.GetPoints2D reads its child transforms' local positions,
                // so it only works on a live Shape in the scene. The mesh
                // generator calls it with a 0.1 scale; match that exactly.
                Shape shape = railType.railShape;
                if (shape == null)
                {
                    Main.LogAlways("Rail profile for '" + railType.name + "' unavailable: railShape is null.");
                }
                else
                {
                    Vector2[] profile = shape.GetPoints2D(RailProfileScale);
                    if (profile == null || profile.Length == 0)
                    {
                        Main.LogAlways("Rail profile for '" + railType.name + "' unavailable: GetPoints2D returned " +
                            (profile == null ? "null" : "0 points") + ".");
                    }
                    else
                    {
                        float top = profile[0].y;
                        float bottom = profile[0].y;
                        for (int i = 1; i < profile.Length; i++)
                        {
                            if (profile[i].y > top) top = profile[i].y;
                            if (profile[i].y < bottom) bottom = profile[i].y;
                        }

                        // Both extents, raw and before any guard. This measured
                        // where the artist put the profile origin - at or above
                        // the railhead rather than at the foot, which is why the
                        // old top > 0 guard silently produced a 0 m railhead and
                        // why the guard below is symmetric. That is settled and
                        // baked into Settings.RailHeadOffset, so it is debug-only
                        // now; it stays because it is the only way to see what a
                        // modded or reworked rail profile actually measures.
                        Main.Log("Rail profile for '" + railType.name + "': " + profile.Length +
                            " points, y from " + bottom.ToString("0.0000") + " to " +
                            top.ToString("0.0000") + " m (scale " + RailProfileScale.ToString("0.###") + ").");

                        // A negative or zero top is now kept as measured. Only
                        // values that cannot describe a rail profile at all are
                        // rejected, and the band is symmetric because the sign
                        // depends on that origin rather than on anything wrong.
                        if (!float.IsNaN(top) && !float.IsInfinity(top) && top > -0.5f && top < 0.5f)
                            geometry.HeadHeight = top;
                    }
                }

                if (geometry.LateralOffset < 0.3f || geometry.LateralOffset > 1.5f) return false;

                // Once per RailType, not per frame. Debug-only: placement works,
                // so these numbers are no longer something a normal log needs to
                // carry, but they are exactly what to ask for if a player ever
                // reports the shoe sitting wrong on some other track type.
                Main.Log("Rail geometry for '" + railType.name + "': lateral " +
                    geometry.LateralOffset.ToString("0.0000") + " m, railhead height " +
                    geometry.HeadHeight.ToString("0.0000") + " m (gauge " +
                    railType.gauge.ToString("0.000") + ", edge offset " +
                    railType.railEdgeOffset.ToString("0.0000") + ")");

                RailGeometryCache[key] = geometry;
                lateralOffset = geometry.LateralOffset;
                headHeight = geometry.HeadHeight + Settings.RailHeadOffset;
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("TryGetRailGeometry failed: " + ex.Message);
                return false;
            }
        }

        // The scale RailwayMeshGenerator.Start passes to railShape.GetPoints2D.
        private const float RailProfileScale = 0.1f;

        /// <summary>
        /// Where the shoe's base should sit for a given track and rail side:
        /// on the centre of the railhead, at railhead height.
        /// </summary>
        internal static Vector3 GetRailheadPosition(RailTrack track, Vector3 center, Vector3 right, Vector3 up, float side)
        {
            float lateral;
            float height;
            TryGetRailGeometry(track, out lateral, out height);
            return center + right * side * lateral + up * height;
        }

        /// <summary>
        /// A neighbouring track plus the linear map that rewrites a span measured
        /// on it into a span in some reference track's coordinate:
        /// mapped = Origin + Alignment * neighbourSpan.
        ///
        /// Alignment is +1 when the two span axes run the same way across the
        /// joint and -1 when they oppose. It is not only arithmetic: forward flips
        /// with it, and right (Cross(up, forward)) flips with forward, so anything
        /// carrying a rail side or a facing across an opposed joint has to flip
        /// that too in order to stay on the same physical rail.
        /// </summary>
        internal struct SpanMap
        {
            internal RailTrack Track;
            internal double Origin;
            internal float Alignment;

            internal double Map(double spanOnTrack)
            {
                return Origin + Alignment * spanOnTrack;
            }

            internal double Unmap(double mappedSpan)
            {
                // Alignment is exactly +1 or -1, so this is its own inverse.
                return Alignment * (mappedSpan - Origin);
            }

            internal float MapSide(float sideOnTrack)
            {
                return Alignment >= 0f ? sideOnTrack : -sideOnTrack;
            }
        }

        /// <summary>
        /// Length of a track's point set, in span metres.
        /// </summary>
        internal static bool TryGetTrackSpan(RailTrack track, out double span)
        {
            span = 0.0;
            if (track == null) return false;
            try
            {
                DV.PointSet.EquiPointSet pointSet = track.GetKinkedPointSet();
                if (pointSet == null || pointSet.points == null || pointSet.points.Length < 2) return false;
                span = pointSet.span;
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Track span read failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The track across one end of this track's point set, with the map that
        /// expresses positions on it in this track's span coordinate.
        ///
        /// Mirrors Bogie.UpdatePointSetTraveller rather than guessing. That method
        /// selects the branch with GetInBranch/GetOutBranch, enters the neighbour
        /// at span 0 when branch.first and at the neighbour's full span otherwise,
        /// then travels the leftover overshoot signed +1 for branch.first and -1
        /// otherwise. So a point sitting `o` metres past the joint is at neighbour
        /// span `first ? o : total - o`, which inverts to the origin/alignment
        /// pairs below. Using the same GetInBranch/GetOutBranch accessors also
        /// means a thrown switch is followed exactly as a bogie follows it.
        /// </summary>
        internal static bool TryGetNeighbour(RailTrack track, bool outEnd, out SpanMap map)
        {
            map = default(SpanMap);
            if (track == null) return false;
            try
            {
                Junction.Branch branch = outEnd ? track.GetOutBranch() : track.GetInBranch();
                if (branch == null || branch.track == null) return false;

                double neighbourTotal;
                if (!TryGetTrackSpan(branch.track, out neighbourTotal)) return false;

                if (outEnd)
                {
                    // The joint is at this track's far end, so the neighbour maps
                    // to spans beyond it.
                    double trackTotal;
                    if (!TryGetTrackSpan(track, out trackTotal)) return false;
                    if (branch.first) { map.Origin = trackTotal; map.Alignment = 1f; }
                    else { map.Origin = trackTotal + neighbourTotal; map.Alignment = -1f; }
                }
                else
                {
                    // The joint is at this track's span 0, so the neighbour maps
                    // to negative spans.
                    if (branch.first) { map.Origin = 0.0; map.Alignment = -1f; }
                    else { map.Origin = -neighbourTotal; map.Alignment = 1f; }
                }

                map.Track = branch.track;
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Branch lookup failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The bogies the game itself has registered as being on this track.
        /// </summary>
        internal static HashSet<Bogie> GetBogiesOnTrack(RailTrack track)
        {
            if (track == null) return null;
            try { return RailTrackOnTrackBogiesExtensions.BogiesOnTrack(track); }
            catch (Exception ex)
            {
                Main.Log("BogiesOnTrack failed: " + ex.Message);
                return null;
            }
        }

        private static bool TryGetPoint(Vector3 source, ICollection<RailTrack> tracks, out RailTrack track, out DV.PointSet.EquiPointSet.Point point)
        {
            if (tracks == null || tracks.Count == 0)
            {
                track = null;
                point = default(DV.PointSet.EquiPointSet.Point);
                return false;
            }

            // RailTrack.GetClosestPoint applies OriginShift.currentMove itself.
            // Passing an already compensated position moves the query away from
            // the aimed track and makes a real rail hit fail to snap.
            // GetClosest's minDistFromEnd keeps the query away from the very end
            // of a track, so a shoe cannot be anchored where the point set runs
            // out. That is unrelated to how near the player must aim, so it is a
            // constant rather than being derived from RailSnapDistance.
            const float endClearance = 0.15f;
            var closest = RailTrack.GetClosest(source, endClearance, tracks);
            if (closest.Item1 == null || !closest.Item2.HasValue)
            {
                track = null;
                point = default(DV.PointSet.EquiPointSet.Point);
                return false;
            }
            track = closest.Item1;
            point = closest.Item2.Value;
            return true;
        }

        private static bool IsFinite(Vector3 p)
        {
            return !float.IsNaN(p.x) && !float.IsInfinity(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
        }

        private static Vector3 GetCurrentMove()
        {
            // WorldMover.currentMove is a static PROPERTY forwarding to
            // OriginShift.currentMove. Reading it via AccessTools.Field silently
            // returned null and left every conversion at Vector3.zero, which is
            // why placement landed in the wrong place once the world had shifted.
            try { return WorldMover.currentMove; }
            catch (Exception ex)
            {
                Main.Log("WorldMover.currentMove read failed: " + ex.Message);
                return Vector3.zero;
            }
        }
    }

    internal static class PlacementPatches
    {
        private static readonly FieldInfo ItemField = AccessTools.Field(typeof(ItemPlacerNonVr), "itemToPlace");
        private static readonly FieldInfo HelperField = AccessTools.Field(typeof(ItemPlacerNonVr), "helper");
        private static readonly PropertyInfo AllowedProperty = AccessTools.Property(typeof(ItemPlacerNonVr), "PlacementAllowed");
        private static readonly FieldInfo CurrentValidityField = AccessTools.Field(typeof(ItemPlacerNonVr), "currentValidity");
        private static readonly FieldInfo MaxDistanceField = AccessTools.Field(typeof(ItemPlacerNonVr), "maxDistance");
        private static readonly FieldInfo ContainerField = AccessTools.Field(typeof(ItemPlacerNonVr), "container");
        private static readonly MethodInfo UpdateAccessPointMethod = AccessTools.Method(typeof(ItemPlacerNonVr), "UpdateAccessPoint");
        private static readonly FieldInfo WorldOverlapMaskField = AccessTools.Field(typeof(ItemPlacerNonVr), "worldOverlapMask");
        // HelperData carries two sibling transforms, both parented to the player
        // camera in InitializePreview: boundsTransform is the invisible bounding
        // box, previewTransform is the model the player actually sees. Vanilla
        // UpdateTarget and UpdateRotation write both. Writing only the bounds
        // transform moved the box and left the visible model behind, which is why
        // the preview never appeared to rotate even though the direction flag was
        // toggling correctly.
        private static readonly FieldInfo BoundsTransformField = AccessTools.Field(HelperField.FieldType, "boundsTransform");
        private static readonly FieldInfo PreviewTransformField = AccessTools.Field(HelperField.FieldType, "previewTransform");
        private static readonly MethodInfo ToggleColorMethod = AccessTools.Method(HelperField.FieldType, "ToggleColor");
        private static readonly RaycastHit[] RailRayHits = new RaycastHit[64];
        private static readonly Dictionary<int, bool> ReversedByPlacer = new Dictionary<int, bool>();

        /// <summary>
        /// The rail anchor ResolvePlacementPrefix settled on, handed forward to
        /// FinalizePlacementPostfix.
        ///
        /// The postfix used to re-run TryGetAimedRail to work out where the shoe
        /// had just been placed, but by then vanilla FinalizePlacement has
        /// already called RemoveHelper and the player may have moved the mouse,
        /// so that second aim could resolve a different track or none at all.
        /// When it resolved none, the postfix skipped MarkPlaced/AttachToRail
        /// and never nulled itemToPlace: the shoe was left sitting on the rail
        /// unattached while the placer still believed it held it. Resolving once
        /// and carrying the result forward removes that disagreement.
        /// </summary>
        private sealed class ResolvedAnchor
        {
            internal RailTrack Track;
            internal float Side;
            internal double Span;
            internal bool SpanKnown;
        }

        private static readonly Dictionary<int, ResolvedAnchor> AnchorByPlacer = new Dictionary<int, ResolvedAnchor>();
        private static readonly Dictionary<int, int> LastToggleFrame = new Dictionary<int, int>();
        // Whether scroll was already non-zero the last time this placer was
        // polled. GetScrollValue keeps reporting a non-zero value for several
        // consecutive frames per wheel notch (measured in Player.log: two reads
        // per frame, non-zero across many frames in a row), so the flag has to
        // flip on the rising edge only. Flipping per frame made the preview
        // alternate 0/180 degrees at frame rate, which reads as "does not
        // rotate" and left the placed orientation down to which frame the
        // button happened to land on.
        private static readonly Dictionary<int, bool> ScrollWasActive = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> LastValidity = new Dictionary<int, bool>();
        private static readonly HashSet<int> AppliedValidity = new HashSet<int>();
        private static RailTrack[] cachedAllTracks = null;
        private static int lastTrackCacheFrame = -1;

        // RailTrack.GetClosestPoint walks every point of the track with no
        // bounding early-out, and calls GetKinkedPointSet, which generates the
        // point set on first touch. Handing GetClosest all ~2000 registry
        // tracks each frame therefore forces point set generation across the
        // whole map, which is what actually costs the time. Prefilter to a
        // shortlist of tracks whose bounding sphere is in reach, and rebuild
        // that only when the player has moved appreciably.
        private static readonly List<RailTrack> NearbyTracks = new List<RailTrack>();
        private static Vector3 nearbyTracksOrigin;
        private static bool nearbyTracksValid;
        private static int nearbyTracksSourceCount = -1;
        // The reach the current shortlist was built with. Raising the snap
        // distance in the settings GUI widens what counts as nearby, so a list
        // built with the old, smaller reach must be discarded.
        private static float nearbyTracksReach = -1f;
        private const float NearbyRefreshDistance = 40f;
        // How far outside a track's own extent it still counts as a candidate.
        // The ray lands on ballast rather than the railhead, so the aim point is
        // never exactly on the track; this only has to be comfortably larger
        // than that offset, since GetClosest still picks the true nearest.
        private const float NearbyTrackMargin = 25f;

        private struct TrackBounds
        {
            // Bounding sphere in absolute space: world position minus
            // currentMove, which is precisely what OriginShift.AbsolutePosition
            // computes (verified in its IL). That quantity is invariant under an
            // origin shift, and EquiPointSet positions are stored in the same
            // space, so these bounds stay valid for the life of the track.
            public Vector3 Centre;
            public float Radius;
        }

        private static readonly Dictionary<int, TrackBounds> TrackBoundsCache = new Dictionary<int, TrackBounds>();

        /// <summary>
        /// Conservative bounding sphere for a track, from its Bezier control
        /// points and handles. A Bezier segment is contained in the convex hull
        /// of those four points, so the sphere cannot exclude any part of the
        /// real rail. Deliberately avoids GetKinkedPointSet: that generates the
        /// point set on first call, and doing it for every track on the map is
        /// the cost this prefilter exists to avoid.
        /// </summary>
        private static bool TryGetTrackBounds(RailTrack track, Vector3 currentMove, out TrackBounds bounds)
        {
            bounds = default(TrackBounds);
            int key = track.GetInstanceID();
            if (TrackBoundsCache.TryGetValue(key, out bounds)) return true;

            // Instance IDs are never reused, so entries for tracks unloaded by
            // world streaming or a save reload would otherwise accumulate.
            if (TrackBoundsCache.Count > 4096) TrackBoundsCache.Clear();

            try
            {
                BezierCurve curve = track.curve;
                if (curve == null) return false;
                int count = curve.pointCount;
                if (count < 2) return false;

                Vector3 min = Vector3.one * float.MaxValue;
                Vector3 max = Vector3.one * float.MinValue;
                for (int i = 0; i < count; i++)
                {
                    BezierPoint bp = curve[i];
                    if (bp == null) return false;
                    // Handles included: the curve can bow outside the segment
                    // endpoints, and the hull property only holds with them.
                    Accumulate(ref min, ref max, bp.position - currentMove);
                    Accumulate(ref min, ref max, bp.globalHandle1 - currentMove);
                    Accumulate(ref min, ref max, bp.globalHandle2 - currentMove);
                }

                if (!IsFiniteLocal(min) || !IsFiniteLocal(max)) return false;

                bounds.Centre = (min + max) * 0.5f;
                bounds.Radius = (max - min).magnitude * 0.5f;
                TrackBoundsCache[key] = bounds;
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("TryGetTrackBounds failed: " + ex.Message);
                return false;
            }
        }

        private static void Accumulate(ref Vector3 min, ref Vector3 max, Vector3 p)
        {
            if (p.x < min.x) min.x = p.x;
            if (p.y < min.y) min.y = p.y;
            if (p.z < min.z) min.z = p.z;
            if (p.x > max.x) max.x = p.x;
            if (p.y > max.y) max.y = p.y;
            if (p.z > max.z) max.z = p.z;
        }

        private static bool IsFiniteLocal(Vector3 p)
        {
            return !float.IsNaN(p.x) && !float.IsInfinity(p.x) && !float.IsNaN(p.y)
                && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
        }

        private static List<RailTrack> GetNearbyTracks(Vector3 around, RailTrack[] allTracks)
        {
            Vector3 currentMove = RailPlacement.GetCurrentMoveStatic();
            // Compared in point set space so an origin shift cannot invalidate
            // the shortlist while the player stands still.
            Vector3 origin = around - currentMove;

            // Why this cannot drop the track the player is aiming at: the list is
            // reused only while the aim point P satisfies |P - origin| < refresh.
            // For a track to matter at P it must own a rail point R with
            // |R - P| <= snap, and every rail point lies within Radius of Centre.
            // So |Centre - origin| <= Radius + snap + refresh, which is inside
            // Radius + reach. The extra margin is pure slack on top of that.
            // No Mathf.Max guard on the snap distance any more: it is a positive
            // constant rather than a slider the player could drag to zero.
            float reach = NearbyRefreshDistance + NearbyTrackMargin + Settings.RailSnapDistance;

            // RailTrackRegistryBase._allTracks is a one-shot FindObjectsOfType
            // snapshot that is never refreshed, and RailTrack.OnDestroy does not
            // unregister (both verified in IL), so the array can only ever go
            // stale. The count check therefore only catches the fallback scene
            // scan returning something different.
            bool sourceChanged = allTracks.Length != nearbyTracksSourceCount;
            bool reachGrew = reach > nearbyTracksReach;

            if (nearbyTracksValid && !sourceChanged && !reachGrew
                && (origin - nearbyTracksOrigin).sqrMagnitude < NearbyRefreshDistance * NearbyRefreshDistance
                && !ShortlistHasDestroyedTrack())
                return NearbyTracks;

            NearbyTracks.Clear();

            for (int i = 0; i < allTracks.Length; i++)
            {
                RailTrack candidate = allTracks[i];
                if (candidate == null) continue;

                TrackBounds bounds;
                if (!TryGetTrackBounds(candidate, currentMove, out bounds))
                {
                    // Bounds unknown, so keep it rather than risk dropping the
                    // track the player is actually aiming at.
                    NearbyTracks.Add(candidate);
                    continue;
                }

                float limit = bounds.Radius + reach;
                if ((bounds.Centre - origin).sqrMagnitude <= limit * limit)
                    NearbyTracks.Add(candidate);
            }

            nearbyTracksOrigin = origin;
            nearbyTracksSourceCount = allTracks.Length;
            nearbyTracksReach = reach;
            nearbyTracksValid = true;

            // Only on rebuild, which is every 40 m of movement at most, so this
            // shows whether the prefilter is actually cutting the search down.
            Main.Log("Track shortlist rebuilt: " + NearbyTracks.Count + " of " + allTracks.Length + " tracks");
            return NearbyTracks;
        }

        /// <summary>
        /// True if any shortlisted track has been destroyed since the list was
        /// built. RailTrack.GetClosestPoint calls GetKinkedPointSet on every
        /// track it is handed with no null guard, so a destroyed entry would
        /// throw and lose the whole query. Unity's == null is true for a
        /// destroyed object, which is exactly the test needed here. Cost is one
        /// comparison per shortlisted track, not per registry track.
        /// </summary>
        private static bool ShortlistHasDestroyedTrack()
        {
            for (int i = 0; i < NearbyTracks.Count; i++)
                if (NearbyTracks[i] == null) return true;
            return false;
        }

        internal static bool UpdateHelperRotationPrefix(ItemPlacerNonVr __instance)
        {
            if (!IsBrakeShoeItem(GetItem(__instance))) return true;

            // For the brake shoe: suppress vanilla free rotation entirely (the
            // shoe only has two valid orientations on a rail), and toggle the
            // direction flag here instead. UpdatePlacement calls rotation, then
            // position, then overlap checks in that fixed order, so the flag set
            // here is already current when the preview pose is rebuilt in
            // UpdateHelperPositionPostfix.
            ToggleDirectionIfRequested(__instance);
            return false;
        }

        internal static bool CheckOverlapsPrefix(ItemPlacerNonVr __instance)
        {
            if (!IsBrakeShoeItem(GetItem(__instance))) return true;

            // The vanilla check only knows about generic surface placement. It
            // can overwrite our rail-only result and turn a floor preview blue.
            // Keep the custom result authoritative for this item alone.
            object helper = HelperField.GetValue(__instance);
            int id = __instance.GetInstanceID();
            bool valid;
            if (!LastValidity.TryGetValue(id, out valid)) valid = false;
            // Re-apply the material here because the generic placement flow
            // may already have painted the helper blue earlier in this frame.
            AppliedValidity.Remove(id);
            ApplyValidity(__instance, helper, valid);
            return false;
        }

        internal static void UpdatePlacementPostfix(ItemPlacerNonVr __instance)
        {
            Component item = GetItem(__instance);
            if (!IsBrakeShoeItem(item)) return;
            object helper = HelperField.GetValue(__instance);
            int id = __instance.GetInstanceID();
            bool valid;
            if (!LastValidity.TryGetValue(id, out valid)) valid = false;
            AppliedValidity.Remove(id);
            ApplyValidity(__instance, helper, valid);
        }

        internal static void UpdateHelperPositionPostfix(ItemPlacerNonVr __instance)
        {
            Component item = GetItem(__instance);
            if (!IsBrakeShoeItem(item)) return;

            object helperData = HelperField.GetValue(__instance);
            Transform helper = GetHelperTransform(helperData);
            Transform preview = GetPreviewTransform(helperData);
            if (helper == null) return;
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            RailTrack aimedTrack;
            Vector3 aimedPoint;
            // Pose and validity are resolved separately. A spot that is already
            // taken by another shoe or by a wheel still gets the exact rail pose -
            // the player has to see a red shoe sitting on the rail they aimed at,
            // which is the whole point of the warning - so being blocked only
            // clears validity.
            bool onRail = TryGetAimedRail(__instance, out aimedTrack, out aimedPoint) && TryGetPlacement(__instance, aimedTrack, aimedPoint, out position, out rotation);
            bool valid = onRail && !IsSpotBlocked(__instance, aimedTrack, aimedPoint);
            // Keep the helper on the deterministic rail pose even when invalid.
            // The vanilla UpdateTarget path otherwise leaves the previous blue
            // helper transform visible over the last surface hit.
            //
            // Both transforms are written, exactly as vanilla UpdateTarget and
            // UpdateRotation do: the bounding box and the visible model are
            // siblings under the camera, so moving one does not move the other.
            if (!onRail)
            {
                // Move the helper away from the previous target while invalid;
                // this prevents a stale cyan preview from appearing on floors.
                position = PlayerManager.PlayerCamera.transform.position + PlayerManager.PlayerCamera.transform.forward * 0.25f;
                rotation = BrakeShoeFactory.RailBaseRotation;
            }
            helper.position = position;
            helper.rotation = rotation;
            if (preview != null)
            {
                preview.position = position;
                preview.rotation = rotation;
            }
            ClearContainerTarget(__instance);
            ApplyValidity(__instance, helperData, valid);
        }

        /// <summary>
        /// Drops any container the vanilla helper picked up for this item.
        ///
        /// Verified in IL: UpdateHelperPosition raycasts for an
        /// ItemContainerAccessPoint and assigns its Container to the placer's
        /// container field, and FinalizePlacement caches that container into a
        /// local BEFORE calling ResolvePlacement, then fires
        /// ItemPlacementFinished(item, result, container) and returns it as the
        /// third tuple element. So a shoe aimed at a rail while an access point
        /// happened to be along the same ray was placed on the rail AND handed
        /// to a container - the shoe appearing in the inventory.
        ///
        /// The shoe is only ever placed on a rail, never into a container, so
        /// the target is cleared here, after vanilla has written it and before
        /// FinalizePlacement can read it. UpdateAccessPoint(null, false) drops
        /// the highlight vanilla put on the access point, so the container does
        /// not stay lit up while the player aims at the rail.
        /// </summary>
        private static void ClearContainerTarget(ItemPlacerNonVr placer)
        {
            if (ContainerField == null) return;
            if (ContainerField.GetValue(placer) == null) return;
            ContainerField.SetValue(placer, null);
            if (UpdateAccessPointMethod == null) return;
            try
            {
                UpdateAccessPointMethod.Invoke(placer, new object[] { null, false });
            }
            catch (Exception ex)
            {
                Main.Log("Clearing the container access point failed: " + ex.Message);
            }
        }

        internal static bool ResolvePlacementPrefix(ItemPlacerNonVr __instance, ref bool __result)
        {
            Component item = GetItem(__instance);
            if (!IsBrakeShoeItem(item)) return true;
            object helperData = HelperField.GetValue(__instance);
            Transform helper = GetHelperTransform(helperData);
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            // Initialized because the && below short-circuits: the compiler
            // cannot tell that a true "valid" implies TryGetAimedRail ran, and
            // StoreAnchor reads both after the early-out.
            RailTrack aimedTrack = null;
            Vector3 aimedPoint = Vector3.zero;
            bool valid = helper != null && TryGetAimedRail(__instance, out aimedTrack, out aimedPoint) && TryGetPlacement(__instance, aimedTrack, aimedPoint, out position, out rotation);
            // Same occupancy rule the preview shows, enforced on the actual
            // placement. Without this the preview would turn red and the shoe
            // would still go down on top of the one already there.
            if (valid && IsSpotBlocked(__instance, aimedTrack, aimedPoint))
            {
                Main.Log("Placement rejected: the spot is taken by another shoe or a wheel.");
                valid = false;
            }
            if (!valid)
            {
                ApplyValidity(__instance, helperData, false);
                __result = false;
                return false;
            }

            item.transform.SetPositionAndRotation(position, rotation);
            // Resolve the anchor here, while the aim that produced this pose is
            // still the current one, and hand it to FinalizePlacementPostfix.
            StoreAnchor(__instance, aimedTrack, aimedPoint);
            // Match what vanilla ResolvePlacement does on success. The enum is
            // Valid = 0, Invalid = 1, Questionable = 2, and vanilla ends a
            // successful placement by setting currentValidity to Invalid, not
            // Valid: it opens with "if (currentValidity == Invalid) return
            // false", so Invalid is the idle state that means "nothing is
            // pending". Leaving Valid behind made the placer look like it still
            // had a live, placeable target after the shoe was already on the
            // rail, and the next R press then resolved against that stale
            // state. CancelPlacement sets the same Invalid value on exit.
            CurrentValidityField.SetValue(__instance, Enum.ToObject(CurrentValidityField.FieldType, 1));
            // itemToPlace is deliberately NOT cleared here even though vanilla
            // clears it: FinalizePlacement has already cached it into a local
            // and returns local.gameObject as the second tuple element, which
            // is how FinalizePlacementPostfix finds the shoe. The postfix nulls
            // the field itself once it is done with it.
            __result = true;
            return false;
        }

        /// <summary>
        /// Works out which rail of the track was aimed at and where along it,
        /// and remembers that for FinalizePlacementPostfix.
        /// </summary>
        private static void StoreAnchor(ItemPlacerNonVr placer, RailTrack aimedTrack, Vector3 aimedPoint)
        {
            if (aimedTrack == null) return;
            ResolvedAnchor anchor = new ResolvedAnchor();
            anchor.Track = aimedTrack;
            anchor.Side = 1f;
            try
            {
                RailTrack outTrack;
                DV.PointSet.EquiPointSet.Point point;
                RailTrack[] tracks = new RailTrack[] { aimedTrack };
                if (RailPlacement.TryGetClosestPoint(aimedPoint, tracks, out outTrack, out point))
                {
                    Vector3 up = new Vector3((float)point.up.x, (float)point.up.y, (float)point.up.z).normalized;
                    Vector3 rawForward = new Vector3((float)point.forward.x, (float)point.forward.y, (float)point.forward.z);
                    Vector3 forward = Vector3.ProjectOnPlane(rawForward, up).normalized;
                    Vector3 right = Vector3.Cross(up, forward).normalized;

                    Vector3 shiftedPoint = new Vector3((float)point.position.x, (float)point.position.y, (float)point.position.z);
                    Vector3 center = shiftedPoint + RailPlacement.GetCurrentMoveStatic();

                    float lateral = Vector3.Dot(aimedPoint - center, right);
                    anchor.Side = lateral >= 0f ? 1f : -1f;
                }

                // Span in the same coordinate the bogies travel in.
                double resolvedSpan;
                if (RailPlacement.TryGetSpanAt(aimedTrack, aimedPoint, out resolvedSpan))
                {
                    anchor.Span = resolvedSpan;
                    anchor.SpanKnown = true;
                }
            }
            catch (Exception ex)
            {
                Main.Log("Failed to determine the rail anchor: " + ex.Message);
            }
            AnchorByPlacer[placer.GetInstanceID()] = anchor;
        }

        internal static void FinalizePlacementPostfix(ItemPlacerNonVr __instance, ref System.ValueTuple<bool, GameObject, GameObject> __result)
        {
            GameObject placed = __result.Item2;
            BrakeShoeBehaviour shoe = placed != null ? placed.GetComponentInChildren<BrakeShoeBehaviour>() : null;
            if (shoe == null)
            {
                // Not our item, or vanilla refused the placement outright and
                // returned no object. Either way there is nothing to anchor.
                ClearState(__instance);
                return;
            }

            int id = __instance.GetInstanceID();
            bool reversed;
            ReversedByPlacer.TryGetValue(id, out reversed);
            ResolvedAnchor anchor;
            bool haveAnchor = AnchorByPlacer.TryGetValue(id, out anchor) && anchor != null && anchor.Track != null;

            ClearState(__instance);

            if (!__result.Item1 || !haveAnchor)
            {
                // The placement did not go through, so the shoe is still the
                // player's. Leave itemToPlace alone: vanilla clears it on
                // release, and clearing it here would drop the item out of the
                // hand with nothing on the rail to show for it.
                return;
            }

            shoe.MarkPlaced();
            shoe.AttachToRail(anchor.Track, reversed, anchor.Side, anchor.Span, anchor.SpanKnown);
            // The shoe now belongs to the rail, so the placer must stop
            // treating it as the held item.
            ItemField.SetValue(__instance, null);
        }

        internal static void CancelPlacementPostfix(ItemPlacerNonVr __instance)
        {
            ClearState(__instance);
        }

        private static Transform GetHelperTransform(object helper)
        {
            return helper == null || BoundsTransformField == null ? null : BoundsTransformField.GetValue(helper) as Transform;
        }

        private static Transform GetPreviewTransform(object helper)
        {
            return helper == null || PreviewTransformField == null ? null : PreviewTransformField.GetValue(helper) as Transform;
        }

        // No logging in these two: they run on every placement frame for every
        // item, and the string concatenation for a log call is evaluated even
        // when DebugLogging is off, which showed up as stutter while holding R.
        private static Component GetItem(ItemPlacerNonVr placer)
        {
            if (placer == null || ItemField == null) return null;
            return ItemField.GetValue(placer) as Component;
        }

        private static bool IsBrakeShoeItem(Component item)
        {
            if (item == null) return false;
            InventoryItemSpec spec = item.GetComponent<InventoryItemSpec>();
            if (spec == null) return false;
            return ShopPricePatches.IsBrakeShoe(spec);
        }

        private static void ClearState(ItemPlacerNonVr placer)
        {
            int id = placer.GetInstanceID();
            ReversedByPlacer.Remove(id);
            AnchorByPlacer.Remove(id);
            LastToggleFrame.Remove(id);
            ScrollWasActive.Remove(id);
            LastValidity.Remove(id);
            AppliedValidity.Remove(id);
            RailPlacement.ClearCache(id);
        }

        private static void ApplyValidity(ItemPlacerNonVr placer, object helper, bool valid)
        {
            if (AllowedProperty != null) AllowedProperty.SetValue(placer, valid, null);
            int id = placer.GetInstanceID();
            bool previous;
            bool changed = !LastValidity.TryGetValue(id, out previous) || previous != valid || !AppliedValidity.Contains(id);
            LastValidity[id] = valid;
            AppliedValidity.Add(id);
            object value = Enum.ToObject(CurrentValidityField.FieldType, valid ? 0 : 1);
            // CheckOverlapsPrefix is also called after vanilla placement code;
            // restore the enum every time even when the boolean result did not
            // change, because vanilla may have overwritten it in between.
            if (CurrentValidityField != null) CurrentValidityField.SetValue(placer, value);
            if (changed && helper != null && ToggleColorMethod != null)
                ToggleColorMethod.Invoke(helper, new object[] { value, true });
        }

        private static bool TryGetPlacement(ItemPlacerNonVr placer, RailTrack aimedTrack, Vector3 source, out Vector3 position, out Quaternion rotation)
        {
            bool reversed;
            ReversedByPlacer.TryGetValue(placer.GetInstanceID(), out reversed);
            return RailPlacement.TryGetSnap(placer.GetInstanceID(), aimedTrack, source, reversed, out position, out rotation);
        }

        /// <summary>
        /// Whether an already-placed shoe overlaps the spot being aimed at.
        ///
        /// Compared in span on the same rail of the pair, not by collider overlap:
        /// rails carry no colliders, and the placement preview has none either, so
        /// the vanilla overlap test cannot see a placed shoe. Span is the
        /// coordinate everything else here already works in, and it stays correct
        /// while a shoe is being carried by a wheel.
        ///
        /// Shoes on the two tracks meeting at a joint are included. A track
        /// boundary is not a physical feature, so aiming just past one must not
        /// silently drop the clearance rule; their spans are brought into the
        /// aimed track's coordinate with SpanMap first, which also flips the rail
        /// side when the two span axes oppose.
        /// </summary>
        private static bool IsSpotOccupied(ItemPlacerNonVr placer, RailTrack aimedTrack, Vector3 source)
        {
            if (aimedTrack == null) return false;
            if (Main.Shoes.Count == 0) return false;

            // Cheap pass first. This runs on every placement frame while R is
            // held, and resolving a span or a rail side is a point-set query, so
            // neither is done until a shoe is actually anchored within reach.
            // Resolved off the held item the same way FinalizePlacementPostfix
            // does, rather than by comparing gameObject: the behaviour and the
            // ItemBase do sit on the same root today, but the placer's item is
            // only known to be somewhere in that hierarchy.
            Component heldItem = GetItem(placer);
            BrakeShoeBehaviour held = heldItem != null ? heldItem.GetComponentInChildren<BrakeShoeBehaviour>() : null;
            // Both flags are counted over the whole set rather than stopping at the
            // first hit. Stopping early would leave anyOffTrack false whenever a
            // shoe on the aimed track happened to be visited first, so whether a
            // shoe across a joint blocked the spot would depend on HashSet
            // iteration order.
            bool anyOnTrack = false;
            bool anyOffTrack = false;
            foreach (BrakeShoeBehaviour candidate in Main.Shoes)
            {
                if (candidate == null || !candidate.IsAnchored) continue;
                // Never test against the shoe currently in the hand: it is still
                // anchored during the placement grace window.
                if (candidate == held) continue;
                if (candidate.AnchoredTrack == aimedTrack) anyOnTrack = true;
                else anyOffTrack = true;
            }
            if (!anyOnTrack && !anyOffTrack) return false;

            // Prefer the sample TryGetSnap already took for this exact aim point:
            // it resolved the same span and the same rail side on the way to the
            // preview pose, so reading it back costs a dictionary lookup instead
            // of two point-set queries per frame. The direct queries stay as the
            // fallback for the frame before the first snap sample exists.
            double span;
            float side;
            if (!RailPlacement.TryGetCachedSpanAndSide(placer.GetInstanceID(), aimedTrack, source, out span, out side))
            {
                if (!RailPlacement.TryGetSpanAt(aimedTrack, source, out span)) return false;
                side = ResolveAimedSide(aimedTrack, source);
            }

            // One shoe length of clearance, so the preview turns red as soon as
            // the new shoe's body would touch the placed one rather than only
            // when their centres coincide.
            double clearance = BrakeShoeFactory.VisualBounds.size.z;
            if (clearance <= 0.0) clearance = 0.414;

            // The tracks across the two joints, resolved at most once per frame and
            // only when a shoe actually sits off the aimed track. A shoe further
            // than one shoe length from either end cannot be blocked from across a
            // joint, so the lookups are skipped in the common case too.
            RailPlacement.SpanMap inMap = default(RailPlacement.SpanMap);
            RailPlacement.SpanMap outMap = default(RailPlacement.SpanMap);
            if (anyOffTrack)
            {
                if (span < clearance)
                    RailPlacement.TryGetNeighbour(aimedTrack, false, out inMap);

                double trackTotal;
                if (RailPlacement.TryGetTrackSpan(aimedTrack, out trackTotal) && span > trackTotal - clearance)
                    RailPlacement.TryGetNeighbour(aimedTrack, true, out outMap);
            }

            foreach (BrakeShoeBehaviour shoe in Main.Shoes)
            {
                if (shoe == null || !shoe.IsAnchored) continue;
                if (shoe == held) continue;

                // Bring the placed shoe into the aimed track's span coordinate.
                // Same track is the identity map; a shoe across a joint is
                // rewritten through the SpanMap for that joint, which also flips
                // its rail side when the two span axes oppose.
                double otherSpan;
                float otherSide;
                if (shoe.AnchoredTrack == aimedTrack)
                {
                    otherSpan = shoe.AnchoredSpan;
                    otherSide = shoe.AnchoredSide;
                }
                else if (inMap.Track != null && shoe.AnchoredTrack == inMap.Track)
                {
                    otherSpan = inMap.Map(shoe.AnchoredSpan);
                    otherSide = inMap.MapSide(shoe.AnchoredSide);
                }
                else if (outMap.Track != null && shoe.AnchoredTrack == outMap.Track)
                {
                    otherSpan = outMap.Map(shoe.AnchoredSpan);
                    otherSide = outMap.MapSide(shoe.AnchoredSide);
                }
                else continue;

                // The two rails of a track are independent: a shoe on the left
                // rail does not block the right one at the same span.
                if (otherSide != side) continue;
                if (Math.Abs(otherSpan - span) < clearance) return true;
            }
            return false;
        }

        /// <summary>
        /// Everything that can stop a shoe going down at the aimed spot: another
        /// shoe already there, or a wheel standing on it.
        /// </summary>
        private static bool IsSpotBlocked(ItemPlacerNonVr placer, RailTrack aimedTrack, Vector3 source)
        {
            return IsSpotOccupied(placer, aimedTrack, source) || IsWheelAtSpot(placer, aimedTrack, source);
        }

        /// <summary>
        /// Whether a wheel is standing where the shoe would go.
        ///
        /// Only the shoe's own footprint is refused, not the space in front of a
        /// wheel: putting a shoe just ahead of a standing wheel so the wheel rolls
        /// onto it is the normal way the thing is used. What this rejects is the
        /// shoe being placed underneath a wheel that is already there, which left
        /// the shoe wedged between wheel and railhead.
        ///
        /// Axle spans are derived exactly as BrakeShoeBehaviour.TryFindEngagedAxle
        /// derives them, so the spot the placement refuses and the spot the physics
        /// treats as occupied are the same one.
        /// </summary>
        private static bool IsWheelAtSpot(ItemPlacerNonVr placer, RailTrack aimedTrack, Vector3 source)
        {
            if (aimedTrack == null) return false;

            double span;
            float side;
            if (!RailPlacement.TryGetCachedSpanAndSide(placer.GetInstanceID(), aimedTrack, source, out span, out side))
            {
                if (!RailPlacement.TryGetSpanAt(aimedTrack, source, out span)) return false;
            }

            // Half the shoe's length, plus a little, so the wheel has to be clear
            // of the body rather than merely clear of its centre. Both rails carry
            // the same axle, so rail side does not enter into it.
            double clearance = BrakeShoeFactory.VisualBounds.size.z;
            if (clearance <= 0.0) clearance = 0.414;
            clearance = clearance * 0.5 + 0.02;

            if (IsWheelWithin(aimedTrack, span, clearance, 1f, 0.0)) return true;

            // A wheel just across a joint is registered on the neighbour, so the
            // ends are checked there too. Skipped for a spot in mid-track, which
            // is the common case.
            double total;
            bool haveTotal = RailPlacement.TryGetTrackSpan(aimedTrack, out total);
            RailPlacement.SpanMap map;
            if (span < clearance && RailPlacement.TryGetNeighbour(aimedTrack, false, out map))
            {
                if (IsWheelWithin(map.Track, span, clearance, map.Alignment, map.Origin)) return true;
            }
            if (haveTotal && span > total - clearance && RailPlacement.TryGetNeighbour(aimedTrack, true, out map))
            {
                if (IsWheelWithin(map.Track, span, clearance, map.Alignment, map.Origin)) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether any axle registered on <paramref name="track"/> falls within
        /// <paramref name="clearance"/> of <paramref name="span"/>, with the
        /// track's spans mapped into the aimed track's coordinate.
        /// </summary>
        private static bool IsWheelWithin(RailTrack track, double span, double clearance, float alignment, double origin)
        {
            if (track == null) return false;
            HashSet<Bogie> bogies = RailPlacement.GetBogiesOnTrack(track);
            if (bogies == null || bogies.Count == 0) return false;

            foreach (Bogie bogie in bogies)
            {
                if (bogie == null || bogie.HasDerailed) continue;
                if (bogie.track != track) continue;
                if (bogie.traveller == null) continue;

                double bogieSpan;
                try { bogieSpan = bogie.traveller.Span; }
                catch (Exception ex) { Main.Log("Traveller span read failed: " + ex.Message); continue; }

                float sign = bogie.TrackDirectionSign;
                if (sign == 0f) sign = 1f;

                Bogie.AxleInfo[] axles = null;
                try { axles = bogie.Axles; }
                catch (Exception ex) { Main.Log("Axle read failed: " + ex.Message); }

                if (axles == null || axles.Length == 0)
                {
                    if (Math.Abs(origin + alignment * bogieSpan - span) < clearance) return true;
                    continue;
                }

                for (int i = 0; i < axles.Length; i++)
                {
                    if (axles[i] == null) continue;
                    double axleSpan = origin + alignment * (bogieSpan + axles[i].distanceFromBogiePivot * sign);
                    if (Math.Abs(axleSpan - span) < clearance) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Which rail of the pair the aim point falls on, as the +1/-1 value the
        /// anchor stores. Mirrors the lateral test in TryGetSnap and StoreAnchor.
        /// </summary>
        private static float ResolveAimedSide(RailTrack aimedTrack, Vector3 source)
        {
            try
            {
                RailTrack outTrack;
                DV.PointSet.EquiPointSet.Point point;
                RailTrack[] tracks = new RailTrack[] { aimedTrack };
                if (!RailPlacement.TryGetClosestPoint(source, tracks, out outTrack, out point)) return 1f;

                Vector3 up = new Vector3((float)point.up.x, (float)point.up.y, (float)point.up.z).normalized;
                Vector3 rawForward = new Vector3((float)point.forward.x, (float)point.forward.y, (float)point.forward.z);
                Vector3 forward = Vector3.ProjectOnPlane(rawForward, up).normalized;
                Vector3 right = Vector3.Cross(up, forward).normalized;
                Vector3 center = new Vector3((float)point.position.x, (float)point.position.y, (float)point.position.z) + RailPlacement.GetCurrentMoveStatic();
                return Vector3.Dot(source - center, right) >= 0f ? 1f : -1f;
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>
        /// Finds the track the player is aiming at. Returns the raw aim point,
        /// not the track centreline: callers derive which of the two rails was
        /// aimed at from the lateral offset of that point, so collapsing it onto
        /// the centreline would make the left rail unreachable.
        /// </summary>
        private static bool TryGetAimedRail(ItemPlacerNonVr placer, out RailTrack track, out Vector3 point)
        {
            track = null;
            point = Vector3.zero;
            Camera camera = PlayerManager.PlayerCamera;
            if (camera == null || MaxDistanceField == null) return false;

            float maxDistance = (float)MaxDistanceField.GetValue(placer);
            Transform view = camera.transform;

            // RailTracks carry no physical colliders, so the ray is only used to
            // find where the player is pointing on the ground/ballast. The track
            // itself is then resolved geometrically from that point.
            RaycastHit hit;
            if (!Physics.Raycast(view.position, view.forward, out hit, maxDistance, ~0, QueryTriggerInteraction.Ignore))
                return false;

            Vector3 aimPoint = hit.point;

            // RailTrackRegistryBase keeps the authoritative track array, so the
            // per-frame FindObjectsOfType scan over ~2000 RailTracks is not
            // needed; that scan was the source of the placement-mode stutter.
            int currentFrame = Time.frameCount;
            if (cachedAllTracks == null || lastTrackCacheFrame != currentFrame)
            {
                RailTrack[] tracks = null;
                try
                {
                    RailTrackRegistryBase registry = RailTrackRegistryBase.Instance;
                    if (registry != null) tracks = registry.AllTracks;
                }
                catch (Exception ex) { Main.Log("Track registry read failed: " + ex.Message); }
                if (tracks == null || tracks.Length == 0) tracks = UnityEngine.Object.FindObjectsOfType<RailTrack>();
                cachedAllTracks = tracks;
                lastTrackCacheFrame = currentFrame;
            }

            if (cachedAllTracks == null || cachedAllTracks.Length == 0) return false;

            try
            {
                // RailTrack.GetClosest applies the origin shift itself, but it
                // walks every point of every track it is handed, so it gets a
                // shortlist rather than the whole registry.
                RailTrack closestTrack;
                DV.PointSet.EquiPointSet.Point closestPoint;
                List<RailTrack> nearby = GetNearbyTracks(aimPoint, cachedAllTracks);
                if (!RailPlacement.TryGetClosestPoint(aimPoint, nearby, out closestTrack, out closestPoint)) return false;

                Vector3 shifted = new Vector3((float)closestPoint.position.x, (float)closestPoint.position.y, (float)closestPoint.position.z);
                Vector3 centre = shifted + RailPlacement.GetCurrentMoveStatic();
                Vector3 up = new Vector3(closestPoint.up.x, closestPoint.up.y, closestPoint.up.z).normalized;
                Vector3 forward = Vector3.ProjectOnPlane(new Vector3(closestPoint.forward.x, closestPoint.forward.y, closestPoint.forward.z), up).normalized;
                if (up.sqrMagnitude < 0.5f || forward.sqrMagnitude < 0.5f) return false;
                Vector3 right = Vector3.Cross(up, forward).normalized;

                // The ray cannot hit the rail itself - RailTracks have no
                // colliders - so it lands on the ballast/terrain below the
                // railhead. A single 3-D distance test would therefore mix the
                // vertical drop in with the lateral offset and reject valid aims.
                // Decompose in the track's own basis instead and allow a generous
                // vertical band, the way the game's own placement helpers offset
                // track points by point.up before measuring.
                Vector3 relative = aimPoint - centre;
                float lateral = Mathf.Abs(Vector3.Dot(relative, right));
                float vertical = Vector3.Dot(relative, up);

                // Same per-track rail geometry the shoe is actually placed
                // against, not the fallback constant, so what the test accepts
                // and where the shoe lands cannot disagree.
                float railLateral;
                float railHeight;
                RailPlacement.TryGetRailGeometry(closestTrack, out railLateral, out railHeight);

                // Distance to the NEARER RAIL, not to the centreline. Comparing
                // against the centreline accepted the whole sleeper bed plus the
                // snap distance on top, so the preview went valid while the
                // player was pointing at ballast a couple of metres away from
                // any rail. lateral is already an absolute value, so subtracting
                // the rail offset gives the offset from whichever rail is closer.
                float railDistance = Mathf.Abs(lateral - railLateral);
                if (railDistance > Settings.RailSnapDistance) return false;

                // Vertical band. The ray cannot hit the rail, so it lands on the
                // sleepers or ballast a little below the point set, and the
                // player may be looking slightly down onto the railhead from
                // above. Keep this tight enough that aiming at an embankment
                // well below the track no longer counts as aiming at the rail.
                if (vertical > 0.6f || vertical < -0.8f) return false;

                // Fold the aim point onto the railhead plane so the caller's
                // lateral test (which picks left vs right rail) sees only the
                // sideways component and not the drop to the terrain.
                track = closestTrack;
                point = aimPoint - up * vertical;
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("TryGetAimedRail failed: " + ex.Message);
                return false;
            }
        }

        private static void ToggleDirectionIfRequested(ItemPlacerNonVr placer)
        {
            // The game uses Rewired for input: InputManager.GetScrollValue reads
            // the scroll state, not Input.mouseScrollDelta (which is always zero).
            int scroll = 0;
            try
            {
                Type inputManager = AccessTools.TypeByName("DV.Interaction.Inputs.InputManager");
                if (inputManager != null)
                {
                    MethodInfo getScrollValue = inputManager.GetMethod("GetScrollValue", BindingFlags.Public | BindingFlags.Static);
                    if (getScrollValue != null)
                    {
                        object result = getScrollValue.Invoke(null, null);
                        if (result is int) scroll = (int)result;
                    }
                }
            }
            catch { }

            int id = placer.GetInstanceID();

            // Rising edge only. A per-frame flip is what made the preview look
            // frozen: GetScrollValue stays non-zero for several frames per
            // notch, so the flag alternated every frame and the preview showed
            // both orientations in turn at frame rate.
            bool wasActive;
            ScrollWasActive.TryGetValue(id, out wasActive);
            bool isActive = scroll != 0;
            ScrollWasActive[id] = isActive;
            if (!isActive || wasActive) return;

            // Second guard, kept for the case where the same placer is polled
            // more than once in a frame: UpdatePlacement calls the rotation
            // step once, but nothing forbids another caller from driving it
            // again, and a double flip in one frame would cancel itself out.
            int frame = Time.frameCount;
            int lastFrame;
            if (LastToggleFrame.TryGetValue(id, out lastFrame) && lastFrame == frame) return;
            LastToggleFrame[id] = frame;

            bool reversed;
            ReversedByPlacer.TryGetValue(id, out reversed);
            ReversedByPlacer[id] = !reversed;
            Main.Log("Shoe placement direction toggled: reversed = " + (!reversed));
        }

    }

    internal static class WeatherCompatibility
    {
        private static Component driver;
        private static float nextLookup;

        internal static float GetWetness()
        {
            try
            {
                if (driver == null && Time.unscaledTime >= nextLookup)
                {
                    nextLookup = Time.unscaledTime + 10f;
                    Type type = AccessTools.TypeByName("DV.WeatherSystem.WeatherDriver");
                    if (type != null) driver = UnityEngine.Object.FindObjectOfType(type) as Component;
                }
                if (driver == null) return 0f;
                object value = AccessTools.Property(driver.GetType(), "WetnessValue").GetValue(driver, null);
                if (value == null) return 0f;
                object current = AccessTools.Property(value.GetType(), "CurrentValue").GetValue(value, null);
                return Mathf.Clamp01(Convert.ToSingle(current));
            }
            catch { return 0f; }
        }
    }

    internal static class HandbrakePatch
    {
        internal static void Postfix(object __instance)
        {
            if (!Main.Config.ReduceHandbrake || __instance == null) return;
            Type type = __instance.GetType();
            FieldInfo handbrake = AccessTools.Field(type, "handbrakePosition");
            FieldInfo braking = AccessTools.Field(type, "brakingFactor");
            if (handbrake == null || braking == null || Convert.ToSingle(handbrake.GetValue(__instance)) <= 0f) return;
            float value = Convert.ToSingle(braking.GetValue(__instance));
            braking.SetValue(__instance, Mathf.Clamp01(value * Main.Config.HandbrakeMultiplier));
        }
    }

    internal static class GltfLoader
    {
        internal static GameObject Load(string path)
        {
            if (!File.Exists(path))
            {
                Main.Entry.Logger.Warning("Provided GLB was not found; procedural fallback visual will be used.");
                return null;
            }
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 28 || BitConverter.ToUInt32(data, 0) != 0x46546C67) return null;
                int jsonLength = BitConverter.ToInt32(data, 12);
                string jsonText = System.Text.Encoding.UTF8.GetString(data, 20, jsonLength).TrimEnd('\0', ' ', '\t', '\r', '\n');
                JObject json = JObject.Parse(jsonText);
                int binHeader = 20 + jsonLength;
                int binLength = BitConverter.ToInt32(data, binHeader);
                int binOffset = binHeader + 8;
                byte[] binary = new byte[binLength];
                Buffer.BlockCopy(data, binOffset, binary, 0, binLength);

                GameObject root = new GameObject("ProvidedBrakeShoeGLB");
                JArray meshes = (JArray)json["meshes"];
                JArray materials = (JArray)json["materials"];
                for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
                {
                    JArray primitives = (JArray)meshes[meshIndex]["primitives"];
                    for (int primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
                    {
                        JObject primitive = (JObject)primitives[primitiveIndex];
                        JObject attributes = (JObject)primitive["attributes"];
                        Vector3[] vertices = ReadVector3(json, binary, (int)attributes["POSITION"]);
                        MapSourceBasis(vertices);
                        Vector3[] normals = attributes["NORMAL"] == null ? null : ReadVector3(json, binary, (int)attributes["NORMAL"]);
                        if (normals != null) MapSourceBasis(normals);
                        Vector2[] uv = attributes["TEXCOORD_0"] == null ? null : ReadVector2(json, binary, (int)attributes["TEXCOORD_0"]);
                        int[] triangles = ReadIndices(json, binary, (int)primitive["indices"]);
                        // MapSourceBasis is a mirror, so the winding must be
                        // reversed to keep faces pointing outward.
                        FlipWinding(triangles);
                        Mesh mesh = new Mesh();
                        mesh.name = "BrakeShoeMesh_" + meshIndex + "_" + primitiveIndex;
                        mesh.vertices = vertices;
                        mesh.triangles = triangles;
                        if (normals != null && normals.Length == vertices.Length) mesh.normals = normals;
                        else mesh.RecalculateNormals();
                        if (uv != null && uv.Length == vertices.Length) mesh.uv = uv;
                        mesh.RecalculateBounds();

                        GameObject part = new GameObject(mesh.name);
                        part.transform.SetParent(root.transform, false);
                        part.AddComponent<MeshFilter>().sharedMesh = mesh;
                        MeshRenderer renderer = part.AddComponent<MeshRenderer>();
                        int materialIndex = primitive["material"] == null ? -1 : (int)primitive["material"];
                        renderer.sharedMaterial = CreateMaterial(materials, materialIndex, json, binary);
                        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                        renderer.receiveShadows = true;
                    }
                }
                return root;
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Warning("GLB runtime import failed; using fallback visual: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Converts the source GLB basis to the runtime basis.
        ///
        /// Measured from the shipped mesh (tools/measure_glb.py), not assumed:
        /// source X is the long axis (extent 1.999), source Y is height
        /// (0.588) and source Z is width (0.419). The up-axis test is what
        /// separates height from width - slicing along Y gives a wide flat
        /// base (1.783 x 0.419) tapering to a narrow top (0.431 x 0.236),
        /// while slicing along Z shows no taper at all (~1.79 x 0.11 at both
        /// ends). Only the height axis of a brake shoe tapers like that.
        ///
        /// Runtime wants Z=length, Y=height, X=width, so the mapping is
        /// (z, y, x). That swap has determinant -1, which is exactly what is
        /// needed: glTF is right-handed and Unity is left-handed, so the
        /// handedness conversion must be an odd permutation. Callers flip the
        /// triangle winding to match - see Load.
        /// </summary>
        private static void MapSourceBasis(Vector3[] values)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                Vector3 source = values[i];
                values[i] = new Vector3(source.z, source.y, source.x);
            }
        }

        /// <summary>
        /// Reverses each triangle's winding. MapSourceBasis mirrors the mesh
        /// (determinant -1), which inverts the face orientation implied by
        /// vertex order, so the winding has to be flipped back or every face
        /// points inward. Verified with tools/check_winding.py: after this
        /// flip the winding agrees with the shipped vertex normals on 100% of
        /// the 10424 triangles.
        /// </summary>
        private static void FlipWinding(int[] triangles)
        {
            if (triangles == null) return;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int first = triangles[i];
                triangles[i] = triangles[i + 2];
                triangles[i + 2] = first;
            }
        }

        private static Material CreateMaterial(JArray materials, int index, JObject json, byte[] binary)
        {
            Material material = new Material(Shader.Find("Standard"));
            material.name = index >= 0 ? "BrakeShoeMaterial_" + index : "BrakeShoeMaterial";
            Color color = Color.white;
            float metallic = 0.4f;
            float roughness = 0.8f;
            if (materials != null && index >= 0 && index < materials.Count)
            {
                JToken pbr = materials[index]["pbrMetallicRoughness"];
                JArray factor = pbr == null ? null : pbr["baseColorFactor"] as JArray;
                if (factor != null && factor.Count >= 4) color = new Color((float)factor[0], (float)factor[1], (float)factor[2], (float)factor[3]);
                if (pbr != null && pbr["metallicFactor"] != null) metallic = (float)pbr["metallicFactor"];
                if (pbr != null && pbr["roughnessFactor"] != null) roughness = (float)pbr["roughnessFactor"];
            }
            color.a = 1f;
            material.color = color;
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Glossiness", 1f - roughness);
            // Back-face culling, the Standard shader default. This used to be
            // forced off to hide the inverted faces caused by the old
            // determinant +1 basis mapping; the mapping is now a proper
            // mirror with flipped winding, so culling is correct again and
            // hiding it would only mask a regression.
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
            material.SetOverrideTag("RenderType", "Opaque");
            material.SetInt("_Mode", 0);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            material.SetInt("_ZWrite", 1);
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = -1;
            try
            {
                if (materials != null && index >= 0 && index < materials.Count)
                {
                    JToken pbrToken = materials[index]["pbrMetallicRoughness"];
                    JToken baseColorTexture = pbrToken == null ? null : pbrToken["baseColorTexture"];
                    JToken textureToken = baseColorTexture == null ? null : baseColorTexture["index"];
                    JArray textures = json["textures"] as JArray;
                    JArray images = json["images"] as JArray;
                    if (textureToken != null && textures != null && images != null)
                    {
                        int textureIndex = (int)textureToken;
                        int imageIndex = (int)textures[textureIndex]["source"];
                        JObject image = (JObject)images[imageIndex];
                        int viewIndex = (int)image["bufferView"];
                        JObject view = (JObject)((JArray)json["bufferViews"])[viewIndex];
                        int start = view["byteOffset"] == null ? 0 : (int)view["byteOffset"];
                        int length = (int)view["byteLength"];
                        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                        byte[] encoded = new byte[length];
                        Buffer.BlockCopy(binary, start, encoded, 0, length);
                        if (texture.LoadImage(encoded, true))
                        {
                            texture.name = "RailwayBrakeShoe_BaseColor";
                            texture.wrapMode = TextureWrapMode.Repeat;
                            material.mainTexture = texture;
                        }
                        else UnityEngine.Object.Destroy(texture);
                    }
                }
            }
            catch (Exception ex)
            {
                Main.Log("Embedded GLB texture skipped: " + ex.Message);
            }
            return material;
        }

        private static Vector3[] ReadVector3(JObject json, byte[] binary, int accessorIndex)
        {
            JObject accessor = (JObject)((JArray)json["accessors"])[accessorIndex];
            Vector3[] result = new Vector3[(int)accessor["count"]];
            int stride, offset;
            GetLayout(json, accessor, 12, out stride, out offset);
            for (int i = 0; i < result.Length; i++) result[i] = new Vector3(BitConverter.ToSingle(binary, offset + i * stride), BitConverter.ToSingle(binary, offset + i * stride + 4), BitConverter.ToSingle(binary, offset + i * stride + 8));
            return result;
        }

        private static Vector2[] ReadVector2(JObject json, byte[] binary, int accessorIndex)
        {
            JObject accessor = (JObject)((JArray)json["accessors"])[accessorIndex];
            Vector2[] result = new Vector2[(int)accessor["count"]];
            int stride, offset;
            GetLayout(json, accessor, 8, out stride, out offset);
            for (int i = 0; i < result.Length; i++) result[i] = new Vector2(BitConverter.ToSingle(binary, offset + i * stride), 1f - BitConverter.ToSingle(binary, offset + i * stride + 4));
            return result;
        }

        private static int[] ReadIndices(JObject json, byte[] binary, int accessorIndex)
        {
            JObject accessor = (JObject)((JArray)json["accessors"])[accessorIndex];
            int count = (int)accessor["count"];
            int component = (int)accessor["componentType"];
            int size = component == 5125 ? 4 : component == 5123 ? 2 : 1;
            int stride, offset;
            GetLayout(json, accessor, size, out stride, out offset);
            int[] result = new int[count];
            for (int i = 0; i < count; i++) result[i] = component == 5125 ? (int)BitConverter.ToUInt32(binary, offset + i * stride) : component == 5123 ? BitConverter.ToUInt16(binary, offset + i * stride) : binary[offset + i * stride];
            return result;
        }

        private static void GetLayout(JObject json, JObject accessor, int elementSize, out int stride, out int offset)
        {
            JObject view = (JObject)((JArray)json["bufferViews"])[(int)accessor["bufferView"]];
            stride = view["byteStride"] == null ? elementSize : (int)view["byteStride"];
            offset = (view["byteOffset"] == null ? 0 : (int)view["byteOffset"]) + (accessor["byteOffset"] == null ? 0 : (int)accessor["byteOffset"]);
        }
    }
}
