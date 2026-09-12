using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using DV.CabControls;
using DV.Shops;

namespace RailwayBrakeShoe
{
    internal static class JobHandbrakePatch
    {
        private static readonly FieldInfo CarsField = AccessTools.Field(typeof(DV.Logic.Job.TransportTask), "cars");
        private static readonly FieldInfo HandbrakePendingField = AccessTools.Field(typeof(DV.Logic.Job.TransportTask), "anyHandbrakeRequiredAndNotDone");

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            FieldInfo brakes = AccessTools.Field(typeof(TrainCar), "brakeSystem");
            FieldInfo position = AccessTools.Field(typeof(DV.Simulation.Brake.BrakeSystem), "handbrakePosition");
            int replacements = 0;
            for (int i = 0; i + 1 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldfld || !Equals(code[i].operand, brakes) ||
                    code[i + 1].opcode != OpCodes.Ldfld || !Equals(code[i + 1].operand, position)) continue;
                // Keep both instruction objects, labels and exception boundaries.
                code[i].opcode = OpCodes.Call;
                code[i].operand = AccessTools.Method(typeof(JobHandbrakePatch), "EffectivePosition");
                code[i + 1].opcode = OpCodes.Nop;
                code[i + 1].operand = null;
                replacements++;
            }
            if (replacements != 1) throw new InvalidOperationException("Expected one TransportTask handbrake check; found " + replacements);
            return code;
        }

        internal static float EffectivePosition(TrainCar car)
        {
            float original = car != null && car.brakeSystem != null ? car.brakeSystem.handbrakePosition : 0f;
            if (Main.Config == null || !Main.Config.BrakeShoeCountsAsHandbrake) return original;
            return BrakeShoeAPI.IsCarSecured(car) ? Math.Max(original, 1f) : original;
            // Track, speed, coupling, locomotive exclusion, final-task scope and
            // the game's 0.75 threshold remain in the untouched vanilla method.
        }

        // Fallback for UI/builds where a second vanilla path reconstructs the
        // handbrake warning from TransportTask's cached flag after the IL check.
        // It only clears that flag when every non-locomotive car still needing a
        // handbrake is secured by the existing shoe API.
        internal static void Postfix(DV.Logic.Job.TransportTask __instance, ref DV.Logic.Job.TaskState __result)
        {
            try
            {
                if (__instance == null || HandbrakePendingField == null ||
                    !(bool)HandbrakePendingField.GetValue(__instance)) return;
                object cars = CarsField == null ? null : CarsField.GetValue(__instance);
                System.Collections.IEnumerable list = cars as System.Collections.IEnumerable;
                if (list == null) return;
                TrainCarRegistry registry = DV.Utils.SingletonBehaviour<TrainCarRegistry>.Instance;
                if (registry == null || registry.logicCarToTrainCar == null) return;
                bool found = false;
                foreach (object logicCar in list)
                {
                    TrainCar car;
                    if (!registry.logicCarToTrainCar.TryGetValue((DV.Logic.Job.Car)logicCar, out car) || car == null) continue;
                    if (DV.ThingTypes.CarTypes.IsAnyLocomotiveOrTender(car.carLivery)) continue;
                    if (car.brakeSystem != null && car.brakeSystem.handbrakePosition > 0.75f) continue;
                    found = true;
                    if (!BrakeShoeAPI.IsCarSecured(car)) return;
                }
                if (!found) return;
                HandbrakePendingField.SetValue(__instance, false);
                FieldInfo coupling = AccessTools.Field(typeof(DV.Logic.Job.TransportTask), "couplingRequiredAndNotDone");
                bool couplingPending = coupling != null && (bool)coupling.GetValue(__instance);
                if (!couplingPending)
                {
                    __instance.state = DV.Logic.Job.TaskState.Done;
                    __result = DV.Logic.Job.TaskState.Done;
                }
            }
            catch (Exception ex) { Main.Log("Job handbrake fallback failed: " + ex.Message); }
        }
    }

    internal static class ShoeRecoveryPatches
    {
        private static int manualDepth;
        private static readonly FieldInfo SummonWorld = AccessTools.Field(
            AccessTools.TypeByName("DV.Storages.LostAndFoundItemsSummoner"), "summonItemsFromWorld");
        private static readonly FieldInfo RespawnHandle = AccessTools.Field(typeof(RespawnOnDrop), "respawnOrDestroyCoro");
        private static readonly FieldInfo OutOfRange = AccessTools.Field(typeof(RespawnOnDrop), "wentOutOfRange");

        internal static void SummonPrefix(object __instance, out bool __state)
        {
            __state = SummonWorld != null && (bool)SummonWorld.GetValue(__instance);
            if (__state) manualDepth++;
        }

        internal static Exception SummonFinalizer(Exception __exception, bool __state)
        {
            if (__state) manualDepth = Math.Max(0, manualDepth - 1);
            return __exception;
        }

        internal static IEnumerable<CodeInstruction> FilterTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getter = AccessTools.Method(typeof(StorageBase), "GetStorageItemList");
            List<CodeInstruction> result = new List<CodeInstruction>();
            int count = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                result.Add(instruction);
                if (instruction.Calls(getter))
                {
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ShoeRecoveryPatches), "FilterWorldItems")));
                    count++;
                }
            }
            if (count != 1) throw new InvalidOperationException("World item recovery list changed: " + count);
            return result;
        }

        internal static List<ItemBase> FilterWorldItems(List<ItemBase> original)
        {
            List<ItemBase> result = new List<ItemBase>(original.Count);
            foreach (ItemBase item in original)
            {
                BrakeShoeBehaviour shoe = item != null ? item.GetComponent<BrakeShoeBehaviour>() : null;
                if (shoe == null) { result.Add(item); continue; }
                if (manualDepth <= 0 || !shoe.CanReturnManually()) continue;
                shoe.PrepareManualReturn();
                result.Add(item);
            }
            return result;
        }

        internal static bool RespawnPrefix(RespawnOnDrop __instance, ref IEnumerator __result)
        {
            BrakeShoeBehaviour shoe = __instance.GetComponent<BrakeShoeBehaviour>();
            InventoryItemSpec spec = __instance.GetComponent<InventoryItemSpec>();
            if (shoe == null || spec == null || !spec.BelongsToPlayer) return true;
            // Guard the original coroutine before it deactivates/reparents a shoe.
            // Buying, inventory and ItemSaveData loading don't use this coroutine.
            __result = KeepWorldItem(__instance);
            return false;
        }

        private static IEnumerator KeepWorldItem(RespawnOnDrop owner)
        {
            // StartChecking assigns the returned coroutine handle after Start().
            yield return null;
            if (owner == null) yield break;
            if (RespawnHandle != null) RespawnHandle.SetValue(owner, null);
            if (OutOfRange != null) OutOfRange.SetValue(owner, false);
        }
    }

    internal static class ShopStockPatches
    {
        private static readonly HashSet<BrakeShoeBehaviour> AllShoes = new HashSet<BrakeShoeBehaviour>();
        private static readonly Dictionary<string, int> Pending = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly FieldInfo RegisterShop = AccessTools.Field(typeof(ScanItemCashRegisterModule), "shop");

        internal static void Track(BrakeShoeBehaviour shoe) { AllShoes.Add(shoe); }
        internal static void Untrack(BrakeShoeBehaviour shoe) { AllShoes.Remove(shoe); }

        internal static void Install(Harmony harmony)
        {
            // ShopRework and game updates can remove or rename individual
            // methods. Patch each point independently so one optional shop
            // integration cannot abort the whole mod load.
            foreach (string name in new[] { "AddItemsToBuy", "UpdateTexts" })
                PatchOptional(harmony, AccessTools.Method(typeof(ScanItemCashRegisterModule), name),
                    new HarmonyMethod(typeof(ShopStockPatches), "StockTranspiler"), name);
            PatchOptional(harmony, AccessTools.Method(typeof(GlobalShopController), "AddItemToInstantiationQueue"),
                new HarmonyMethod(typeof(ShopStockPatches), "PurchasePrefix"),
                new HarmonyMethod(typeof(ShopStockPatches), "PurchasePostfix"), "AddItemToInstantiationQueue");
            PatchOptional(harmony, AccessTools.Method(typeof(GlobalShopController), "UpdateItemStocksOnGameLoad"),
                new HarmonyMethod(typeof(ShopStockPatches), "LoadPrefix"),
                new HarmonyMethod(typeof(ShopStockPatches), "LoadPostfix"), "UpdateItemStocksOnGameLoad");
            PatchOptional(harmony, AccessTools.Method(typeof(GlobalShopController), "InitializeShopData"),
                new HarmonyMethod(typeof(ShopStockPatches), "SessionPrefix"), null, "InitializeShopData");
            PatchOptional(harmony, AccessTools.Method(typeof(ShopRestocker), "OnDestroy"),
                new HarmonyMethod(typeof(ShopStockPatches), "DestroyedPrefix"), null, "ShopRestocker.OnDestroy");
            // Use the existing purchase queue. Stamp its existing (transform,item,
            // shop) tuple at creation, before delayed activation or a possible save.
            MethodInfo iterator = AccessTools.Method(typeof(GlobalShopController), "InstantiatePurchasedItems");
            MethodInfo moveNext = iterator == null ? null : AccessTools.EnumeratorMoveNext(iterator);
            PatchOptional(harmony, moveNext,
                new HarmonyMethod(typeof(ShopStockPatches), "PurchaseTranspiler"), "InstantiatePurchasedItems.MoveNext");
        }

        private static void PatchOptional(Harmony harmony, MethodInfo target, HarmonyMethod transpiler, string label)
        {
            PatchOptional(harmony, target, null, null, transpiler, label);
        }

        private static void PatchOptional(Harmony harmony, MethodInfo target, HarmonyMethod prefix,
            HarmonyMethod postfix, string label)
        {
            PatchOptional(harmony, target, prefix, postfix, null, label);
        }

        private static void PatchOptional(Harmony harmony, MethodInfo target, HarmonyMethod prefix,
            HarmonyMethod postfix, HarmonyMethod transpiler, string label)
        {
            if (target == null)
            {
                if (Main.Entry != null) Main.Entry.Logger.Warning("Compatibility: shop target absent; skipped " + label);
                return;
            }
            try { harmony.Patch(target, prefix: prefix, postfix: postfix, transpiler: transpiler); }
            catch (Exception ex)
            {
                if (Main.Entry != null) Main.Entry.Logger.Warning("Compatibility: skipped shop patch " + label + ": " + ex.Message);
            }
        }

        internal static void EnsureGlobalCapacity(ShopItemData data)
        {
            if (data == null) return;
            GlobalShopController controller = GlobalShopController.Instance;
            int shops = controller != null && controller.globalShopList != null ? controller.globalShopList.Count : 1;
            // Preserve the vanilla aggregate counter and restocker. The shelf's
            // availability check is local; the aggregate must accommodate all shops.
            data.allowedToHaveAmount = 20 * Math.Max(1, shops);
        }

        private static string ShopId(Shop shop)
        {
            if (shop == null) return null;
            string key = shop.name;
            for (Transform p = shop.transform.parent; p != null && p != WorldMover.OriginShiftParent; p = p.parent)
                key = p.name + "/" + key;
            return key;
        }

        private static bool IsPurchased(BrakeShoeBehaviour shoe)
        {
            if (shoe == null || shoe.StockDestroyed) return false;
            InventoryItemSpec spec = shoe.GetComponent<InventoryItemSpec>();
            ShopRestocker restocker = shoe.GetComponent<ShopRestocker>();
            return spec != null && spec.BelongsToPlayer && restocker != null && restocker.restockOnItemDestroyed;
        }

        internal static int LocalStock(ShopItemData data, ScanItemCashRegisterModule register)
        {
            if (data == null) return 0;
            if (!ShopPricePatches.IsBrakeShoe(data.item)) return data.ItemsInStock;
            if (data.unavailableDueToGameMode) return 0;
            Shop shop = RegisterShop == null || register == null ? null : RegisterShop.GetValue(register) as Shop;
            if (shop == null) shop = register.GetComponentInParent<Shop>();
            string id = ShopId(shop);
            if (id == null) return 0;
            int owned = 0;
            foreach (BrakeShoeBehaviour shoe in AllShoes)
                if (IsPurchased(shoe) && shoe.PurchaseShopId == id) owned++;
            int pending;
            Pending.TryGetValue(id, out pending);
            return ShoeRules.Stock(owned, pending);
        }

        internal static IEnumerable<CodeInstruction> StockTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> result = new List<CodeInstruction>();
            MethodInfo stock = AccessTools.PropertyGetter(typeof(ShopItemData), "ItemsInStock");
            int count = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(stock))
                {
                    CodeInstruction context = new CodeInstruction(OpCodes.Ldarg_0);
                    context.labels.AddRange(instruction.labels);
                    context.blocks.AddRange(instruction.blocks);
                    result.Add(context);
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ShopStockPatches), "LocalStock")));
                    count++;
                }
                else result.Add(instruction);
            }
            if (count != 1) throw new InvalidOperationException("Shop stock check changed: " + count);
            return result;
        }

        internal static void PurchasePrefix(InventoryItemSpec boughtItemSpec, Shop shop, out int __state)
        {
            ShopItemData data = ShopPricePatches.IsBrakeShoe(boughtItemSpec) ? GlobalShopController.Instance.GetShopItemData(boughtItemSpec) : null;
            __state = data != null ? data.purchasedItems : -1;
            if (data != null) EnsureGlobalCapacity(data);
        }

        internal static void PurchasePostfix(InventoryItemSpec boughtItemSpec, Shop shop, int __state)
        {
            if (__state < 0) return;
            string id = ShopId(shop);
            if (id == null) return;
            ShopItemData data = GlobalShopController.Instance.GetShopItemData(boughtItemSpec);
            int reserved;
            Pending.TryGetValue(id, out reserved);
            Pending[id] = reserved + Math.Max(0, data.purchasedItems - __state);
        }

        internal static IEnumerable<CodeInstruction> PurchaseTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> result = new List<CodeInstruction>();
            Type tuple = typeof(ValueTuple<Transform, ItemBase, Shop>);
            int count = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                ConstructorInfo ctor = instruction.operand as ConstructorInfo;
                if (instruction.opcode == OpCodes.Newobj && ctor != null && ctor.DeclaringType == tuple)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(ShopStockPatches), "PurchasedItem");
                    count++;
                }
                result.Add(instruction);
            }
            if (count != 1) throw new InvalidOperationException("Shop purchase tuple changed: " + count);
            return result;
        }

        internal static ValueTuple<Transform, ItemBase, Shop> PurchasedItem(Transform transform, ItemBase item, Shop shop)
        {
            BrakeShoeBehaviour shoe = item != null ? item.GetComponent<BrakeShoeBehaviour>() : null;
            if (shoe != null)
            {
                string id = ShopId(shop);
                shoe.PurchaseShopId = id;
                int reserved;
                if (id != null && Pending.TryGetValue(id, out reserved)) Pending[id] = Math.Max(0, reserved - 1);
            }
            return new ValueTuple<Transform, ItemBase, Shop>(transform, item, shop);
        }

        internal static void SessionPrefix() { Pending.Clear(); }
        internal static void LoadPrefix()
        {
            Pending.Clear();
            if (Main.CustomItem != null) EnsureGlobalCapacity(Main.CustomItem.ShopData);
        }

        internal static void LoadPostfix()
        {
            GlobalShopController controller = GlobalShopController.Instance;
            if (controller == null || controller.globalShopList == null) return;
            // Older saves have no purchase origin. Attribute each existing shoe
            // once to its nearest shop and store that choice in ItemSaveData.
            foreach (BrakeShoeBehaviour shoe in AllShoes)
            {
                if (!IsPurchased(shoe) || !string.IsNullOrEmpty(shoe.PurchaseShopId)) continue;
                Shop nearest = null;
                float best = float.PositiveInfinity;
                foreach (Shop shop in controller.globalShopList)
                {
                    if (shop == null) continue;
                    float distance = (shop.transform.position - shoe.transform.position).sqrMagnitude;
                    if (distance < best) { best = distance; nearest = shop; }
                }
                shoe.PurchaseShopId = ShopId(nearest);
            }
            controller.Fire_GlobalShopDataChanged();
        }

        internal static void DestroyedPrefix(ShopRestocker __instance)
        {
            BrakeShoeBehaviour shoe = __instance.GetComponent<BrakeShoeBehaviour>();
            if (shoe != null) shoe.StockDestroyed = true;
        }
    }
}
