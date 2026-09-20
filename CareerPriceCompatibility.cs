using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace RailwayBrakeShoe
{
    // Extend Career Rework's Custom-mode item filter, never its calculation.
    // No compile-time dependency, settings mirror or replacement price formula.
    internal static class CareerPriceCompatibility
    {
        internal static void Install(Harmony harmony)
        {
            Type type = AccessTools.TypeByName("CareerRework.Main+Patch_ScanItemCashRegisterModule");
            if (type == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(type, "Postfix");
                if (target == null) throw new MissingMethodException(type.FullName, "Postfix");
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(CareerPriceCompatibility), "Transpiler"));
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Warning("Career Rework price integration could not be enabled: " + ex.Message);
            }
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int matches = 0;
            for (int i = 0; i + 3 < code.Count; i++)
            {
                FieldInfo field = code[i].operand as FieldInfo;
                if (code[i].opcode != OpCodes.Ldfld || field == null || field.Name != "priceMultiplierKeysGadgets" ||
                    field.DeclaringType.FullName != "CareerRework.CareerReworkSettings") continue;
                // Installed 1.06.0: read multiplier -> convert -> store -> load
                // item name for the Custom-mode switch. Preset dispatch is earlier.
                if (code[i + 1].opcode != OpCodes.Conv_R4 ||
                    !code[i + 2].opcode.Name.StartsWith("stloc", StringComparison.Ordinal) ||
                    !code[i + 3].opcode.Name.StartsWith("ldloc", StringComparison.Ordinal))
                    throw new InvalidOperationException("Career Rework gadget price filter changed.");
                code.Insert(i + 4, new CodeInstruction(OpCodes.Ldarg_0));
                code.Insert(i + 5, new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(CareerPriceCompatibility), "CustomPriceKey")));
                matches++;
            }
            if (matches != 1) throw new InvalidOperationException("Career Rework gadget price filter not found uniquely.");
            return code;
        }

        private static string CustomPriceKey(string original, DV.Shops.ScanItemCashRegisterModule register)
        {
            // Only the comparison key on the evaluation stack changes. Actual
            // item name/identity, localization, saves and preset prices do not.
            return register != null && ShopPricePatches.IsBrakeShoe(register.sellingItemSpec) ? "AmpLimiter" : original;
        }
    }
}
