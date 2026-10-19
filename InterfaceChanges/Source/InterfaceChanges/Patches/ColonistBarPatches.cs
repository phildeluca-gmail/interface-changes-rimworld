using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using InterfaceChanges.Sizing;
using RimWorld;
using UnityEngine;
using Verse;
using Logger = InterfaceChanges.Utility.Logger;

namespace InterfaceChanges.Patches
{
    // The larger colonist bar group. See ic-architecture.md section 9.
    // Every patch is applied by hand so a target that a RimWorld update
    // moved is logged and skipped instead of throwing out of PatchAll.
    // Vanilla keeps one scale for the whole bar (ColonistBar.cachedScale);
    // these patches give each group its own, through ColonistBarSizing.
    public static class ColonistBarPatches
    {
        public static void Apply(Harmony harmony)
        {
            // Layout: conditional skip-original prefix. Returns true (vanilla
            // runs untouched) whenever the larger group is off or cannot fit.
            Patch(harmony, "ColonistBarDrawLocsFinder.CalculateDrawLocs (layout)",
                AccessTools.Method(typeof(ColonistBarDrawLocsFinder), "CalculateDrawLocs",
                    new Type[] { typeof(List<Vector2>), typeof(float).MakeByRefType(), typeof(int) }),
                prefix: nameof(LayoutPrefix));

            // The one scale every drawer method reads, plus the two getters
            // built on it (patched too in case the JIT inlined Scale into them).
            Patch(harmony, "ColonistBar.Scale getter", AccessTools.PropertyGetter(typeof(ColonistBar), "Scale"), prefix: nameof(ScalePrefix));
            Patch(harmony, "ColonistBar.Size getter", AccessTools.PropertyGetter(typeof(ColonistBar), "Size"), prefix: nameof(SizePrefix));
            Patch(harmony, "ColonistBar.SpaceBetweenColonistsHorizontal getter",
                AccessTools.PropertyGetter(typeof(ColonistBar), "SpaceBetweenColonistsHorizontal"), prefix: nameof(SpacePrefix));

            // Which group is being drawn: injected per entry in the draw loop.
            Patch(harmony, "ColonistBar.ColonistBarOnGUI", AccessTools.DeclaredMethod(typeof(ColonistBar), "ColonistBarOnGUI"),
                transpiler: nameof(OnGuiTranspiler));

            // Hit-testing: size read per entry instead of once.
            Patch(harmony, "ColonistBar.TryGetEntryAt", AccessTools.DeclaredMethod(typeof(ColonistBar), "TryGetEntryAt"),
                transpiler: nameof(HitTestTranspiler));
            Patch(harmony, "ColonistBar.ColonistsOrCorpsesInScreenRect", AccessTools.DeclaredMethod(typeof(ColonistBar), "ColonistsOrCorpsesInScreenRect"),
                transpiler: nameof(HitTestTranspiler));

            // Methods that take the group as an argument: set it as context
            // for the call, restore the previous one after.
            Patch(harmony, "ColonistBar.DrawColonistMouseAttachment", AccessTools.DeclaredMethod(typeof(ColonistBar), "DrawColonistMouseAttachment"),
                prefix: nameof(MouseAttachmentPrefix), finalizer: nameof(RestoreFinalizer));
            Patch(harmony, "ColonistBarColonistDrawer.GroupFrameRect", AccessTools.DeclaredMethod(typeof(ColonistBarColonistDrawer), "GroupFrameRect"),
                prefix: nameof(GroupFramePrefix), finalizer: nameof(RestoreFinalizer));

            // The bar is only recached when marked dirty; vanilla does not
            // mark it on a map switch.
            Patch(harmony, "Game.CurrentMap setter", AccessTools.PropertySetter(typeof(Game), "CurrentMap"), postfix: nameof(MapSwitchPostfix));
        }

        private static void Patch(Harmony harmony, string label, MethodBase target, string prefix = null, string postfix = null,
            string transpiler = null, string finalizer = null)
        {
            if (target == null)
            {
                Logger.Warning("ColonistBarPatches: " + label + " not found, the larger colonist bar group is incomplete.");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(ColonistBarPatches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(ColonistBarPatches), postfix),
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(ColonistBarPatches), transpiler),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ColonistBarPatches), finalizer));
                Logger.Info("ColonistBarPatches applied to " + label + ".");
            }
            catch (Exception e)
            {
                Logger.Warning("ColonistBarPatches: patching " + label + " failed (" + e.Message + ").");
            }
        }

        // ---- prefixes, postfix, finalizer -------------------------------

        public static bool LayoutPrefix(ColonistBarDrawLocsFinder __instance, List<Vector2> outDrawLocs, ref float scale, int groupsCount)
        {
            try
            {
                return ColonistBarSizing.TryComputeLayout(__instance, outDrawLocs, ref scale, groupsCount);
            }
            catch (Exception e)
            {
                Logger.Warning("Colonist bar layout threw, vanilla layout used: " + e);
                return true;
            }
        }

        public static bool ScalePrefix(ref float __result)
        {
            float s;
            if (!ColonistBarSizing.TryGetContextScale(out s))
            {
                return true;
            }
            __result = s;
            return false;
        }

        public static bool SizePrefix(ref Vector2 __result)
        {
            float s;
            if (!ColonistBarSizing.TryGetContextScale(out s))
            {
                return true;
            }
            __result = ColonistBar.BaseSize * s;
            return false;
        }

        public static bool SpacePrefix(ref float __result)
        {
            float s;
            if (!ColonistBarSizing.TryGetContextScale(out s))
            {
                return true;
            }
            __result = 20f * s;
            return false;
        }

        public static void MouseAttachmentPrefix(int entryGroup, ref int __state)
        {
            __state = ColonistBarSizing.CurrentGroup;
            ColonistBarSizing.SetCurrentGroup(entryGroup);
        }

        // Vanilla's GroupFrameRect (the grey box) ends 12 * scale below the lowest
        // pawn box, which stops short of the weapon icons in a wrapped group. For
        // such a group the rect is rebuilt here to end FramePad below the lowest
        // icon; everything else runs vanilla. Same patch, no new one.
        private static bool frameWarned;

        public static bool GroupFramePrefix(int group, ref int __state, ref Rect __result)
        {
            __state = ColonistBarSizing.CurrentGroup;
            ColonistBarSizing.SetCurrentGroup(group);
            try
            {
                float pad;
                float iconBottom = ColonistBarSizing.WeaponBottomBelowRowTop(group, out pad);
                if (iconBottom < 0f)
                {
                    return true;
                }
                ColonistBar bar = Find.ColonistBar;
                List<ColonistBar.Entry> entries = bar.Entries;
                List<Vector2> locs = bar.DrawLocs;
                float minX = 99999f, maxX = 0f, maxY = 0f;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].group == group)
                    {
                        minX = Mathf.Min(minX, locs[i].x);
                        maxX = Mathf.Max(maxX, locs[i].x + bar.Size.x);
                        maxY = Mathf.Max(maxY, locs[i].y + iconBottom);
                    }
                }
                if (minX > maxX)
                {
                    return true;
                }
                float margin = 12f * bar.Scale;
                __result = new Rect(minX - margin, -margin, maxX - minX + 2f * margin, maxY + pad + margin);
                return false;
            }
            catch (Exception e)
            {
                if (!frameWarned)
                {
                    frameWarned = true;
                    Logger.Warning("Colonist bar group frame threw " + e.GetType().Name + ", vanilla frame used (logged once): " + e.Message);
                }
                return true;
            }
        }

        public static void RestoreFinalizer(int __state)
        {
            ColonistBarSizing.SetCurrentGroup(__state);
        }

        public static void MapSwitchPostfix()
        {
            try
            {
                if (Current.ProgramState == ProgramState.Playing)
                {
                    Find.ColonistBar?.MarkColonistsDirty();
                }
            }
            catch (Exception)
            {
                // No UI root yet (map generation, loading): nothing to mark.
            }
        }

        // ---- transpilers -------------------------------------------------

        // ColonistBarOnGUI builds each entry's rect from Size before it has
        // looked the entry up, so the group context is set at the top of the
        // loop pass: found as the first cachedDrawLocs[i] read followed by
        // an index load. Context is cleared before every return.
        public static IEnumerable<CodeInstruction> OnGuiTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = instructions.ToList();
            FieldInfo drawLocs = AccessTools.Field(typeof(ColonistBar), "cachedDrawLocs");
            MethodInfo getItem = AccessTools.Method(typeof(List<Vector2>), "get_Item");
            MethodInfo setEntry = AccessTools.Method(typeof(ColonistBarSizing), nameof(ColonistBarSizing.SetCurrentEntry));
            MethodInfo clear = AccessTools.Method(typeof(ColonistBarSizing), nameof(ColonistBarSizing.ClearContext));

            int at = -1;
            for (int k = 1; k + 2 < list.Count; k++)
            {
                if (list[k].LoadsField(drawLocs) && IsLocalLoad(list[k + 1]) && list[k + 2].Calls(getItem))
                {
                    at = k - 1;
                    break;
                }
            }
            if (at < 0)
            {
                Logger.Warning("ColonistBarPatches: ColonistBarOnGUI loop not recognised, per-group sizes will not be drawn.");
                return list;
            }

            // Vanilla's weapon icon rect: `y + height * 1.05f`. The constant becomes
            // a call so wrapped rows can pull the icon up under its own portrait.
            MethodInfo weaponFactor = AccessTools.Method(typeof(ColonistBarSizing), nameof(ColonistBarSizing.WeaponOffsetFactor));
            int weaponAt = list.FindIndex(c => c.opcode == OpCodes.Ldc_R4 && c.operand is float && Math.Abs((float)c.operand - 1.05f) < 0.0001f);
            if (weaponAt >= 0)
            {
                ColonistBarSizing.MarkWeaponHook();
            }
            else
            {
                Logger.Warning("ColonistBarPatches: weapon icon offset in ColonistBarOnGUI not found, wrapped rows keep vanilla spacing.");
            }

            List<CodeInstruction> result = new List<CodeInstruction>(list.Count + 4);
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (i == weaponAt)
                {
                    CodeInstruction call = new CodeInstruction(OpCodes.Call, weaponFactor);
                    call.labels.AddRange(ci.labels);
                    result.Add(call);
                    continue;
                }
                if (i == at)
                {
                    // list[at] is ldarg.0, list[at+1] the ldfld, list[at+2] the index load.
                    // Jump targets that pointed at ldarg.0 now point at the injected push.
                    CodeInstruction pushIndex = list[at + 2].Clone();
                    pushIndex.labels.AddRange(ci.labels);
                    ci.labels.Clear();
                    result.Add(pushIndex);
                    result.Add(new CodeInstruction(OpCodes.Call, setEntry));
                }
                else if (ci.opcode == OpCodes.Ret)
                {
                    CodeInstruction call = new CodeInstruction(OpCodes.Call, clear);
                    call.labels.AddRange(ci.labels);
                    ci.labels.Clear();
                    result.Add(call);
                }
                result.Add(ci);
            }
            return result;
        }

        // TryGetEntryAt and ColonistsOrCorpsesInScreenRect both read
        // `Vector2 size = Size;` once before the loop. Every read of that
        // local's x or y inside the loop becomes a lookup for the entry
        // being tested, so an enlarged or shrunk box is hit-tested at its
        // drawn size.
        public static IEnumerable<CodeInstruction> HitTestTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> list = instructions.ToList();
            MethodInfo sizeGetter = AccessTools.PropertyGetter(typeof(ColonistBar), "Size");
            MethodInfo getItem = AccessTools.Method(typeof(List<Vector2>), "get_Item");
            FieldInfo fx = AccessTools.Field(typeof(Vector2), "x");
            FieldInfo fy = AccessTools.Field(typeof(Vector2), "y");
            MethodInfo sizeX = AccessTools.Method(typeof(ColonistBarSizing), nameof(ColonistBarSizing.SizeXForEntry));
            MethodInfo sizeY = AccessTools.Method(typeof(ColonistBarSizing), nameof(ColonistBarSizing.SizeYForEntry));

            int sizeCall = list.FindIndex(c => c.Calls(sizeGetter));
            int itemCall = list.FindIndex(c => c.Calls(getItem));
            if (sizeCall < 0 || sizeCall + 1 >= list.Count || itemCall < 1 || !IsLocalLoad(list[itemCall - 1]))
            {
                Logger.Warning("ColonistBarPatches: " + original.Name + " not recognised, hit-testing keeps vanilla's single size.");
                return list;
            }
            int sizeLocal = LocalIndexOf(list[sizeCall + 1]);
            CodeInstruction indexLoad = list[itemCall - 1];
            if (sizeLocal < 0)
            {
                Logger.Warning("ColonistBarPatches: " + original.Name + " size local not found, hit-testing keeps vanilla's single size.");
                return list;
            }

            List<CodeInstruction> result = new List<CodeInstruction>(list.Count);
            int replaced = 0;
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                bool loadsSize = (ci.opcode == OpCodes.Ldloca || ci.opcode == OpCodes.Ldloca_S || IsLocalLoad(ci)) && LocalIndexOf(ci) == sizeLocal;
                if (loadsSize && i + 1 < list.Count && (list[i + 1].LoadsField(fx) || list[i + 1].LoadsField(fy)))
                {
                    CodeInstruction push = indexLoad.Clone();
                    push.labels.AddRange(ci.labels);
                    result.Add(push);
                    result.Add(new CodeInstruction(OpCodes.Call, list[i + 1].LoadsField(fx) ? sizeX : sizeY));
                    i++;
                    replaced++;
                    continue;
                }
                result.Add(ci);
            }
            if (replaced == 0)
            {
                Logger.Warning("ColonistBarPatches: " + original.Name + " had no size reads to replace, hit-testing keeps vanilla's single size.");
            }
            return result;
        }

        private static bool IsLocalLoad(CodeInstruction ci)
        {
            OpCode op = ci.opcode;
            return op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1
                || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3;
        }

        // Local variable index of a load/store/address instruction, -1 if it is none.
        private static int LocalIndexOf(CodeInstruction ci)
        {
            OpCode op = ci.opcode;
            if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) return 0;
            if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) return 1;
            if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) return 2;
            if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) return 3;
            if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Stloc || op == OpCodes.Stloc_S
                || op == OpCodes.Ldloca || op == OpCodes.Ldloca_S)
            {
                LocalBuilder lb = ci.operand as LocalBuilder;
                if (lb != null) return lb.LocalIndex;
                if (ci.operand is byte) return (byte)ci.operand;
                if (ci.operand is int) return (int)ci.operand;
                if (ci.operand is short) return (short)ci.operand;
            }
            return -1;
        }
    }
}
