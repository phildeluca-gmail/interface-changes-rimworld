using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Logger = InterfaceChanges.Utility.Logger;

namespace InterfaceChanges.Sizing
{
    // State and layout for the larger colonist bar group. See
    // ic-architecture.md section 9. Holds one scale per group, the "group
    // being drawn now" context the patched ColonistBar.Scale reads, and the
    // layout that replaces vanilla's single global scale when the setting is
    // on. When it cannot do better than vanilla it declines and vanilla runs.
    public static class ColonistBarSizing
    {
        private delegate float FindBestScaleDel(ColonistBarDrawLocsFinder self, out bool onlyOneRow, out int maxPerGlobalRow, int groupsCount);
        private delegate void CalcInGroupDel(ColonistBarDrawLocsFinder self, int groupsCount);

        private const float PitchX = 48f + 20f;        // ColonistBar.BaseSize.x + 20 (vanilla 24; tightened 2026-10-18)
        private const float PitchY = 48f + 32f;        // ColonistBar.BaseSize.y + 32
        private const float FrameMargin = 12f;         // ColonistBarColonistDrawer.BaseGroupFrameMargin
        private const float MarginTop = 21f;           // ColonistBarDrawLocsFinder.MarginTop
        // Ordered 2026-10-18: tighter rows (per scale unit) and a grey frame ending this far below the lowest weapon icon.
        private const float RowTighten = 4f;
        private const float FramePad = 4f;
        private const float MinOtherScale = 0.35f;
        // Ordered 2026-10-11: the bar, outer frames included, stays this far from both screen edges.
        private const float SideMargin = 200f;
        // Verified 2026-10-10 in lib\Assembly-CSharp.dll (ilspycmd): MainButtonsRoot
        // draws the bottom bar at Rect(.., UI.screenHeight - 35, .., 36), so its
        // top edge is screenHeight - 35. The group's bottom must stay above that.
        private const float BottomBarHeight = 35f;
        private const float BottomMargin = 4f;

        private static FindBestScaleDel findBestScale;
        private static CalcInGroupDel calcInGroup;
        private static bool reflectionTried;
        private static bool reflectionOk;

        private static float[] groupScale = new float[0];
        // Per group: true when its weapon icons are drawn tight under the portrait
        // because its rows were spaced wider for them (see WeaponOffsetFactor).
        private static bool[] groupWeaponTight = new bool[0];
        private static bool weaponHookOk;
        private static bool active;
        private static int currentGroup = -1;
        private static string lastLogKey = "";

        public static bool Active { get { return active; } }

        // ---- the context read by the patched ColonistBar.Scale ----------

        public static int CurrentGroup { get { return currentGroup; } }

        public static void SetCurrentGroup(int group)
        {
            currentGroup = group;
        }

        // Called by the injected IL in ColonistBarOnGUI at the top of each
        // entry's loop pass, before that entry's rect is built.
        public static void SetCurrentEntry(int entryIndex)
        {
            List<ColonistBar.Entry> entries = Find.ColonistBar.Entries;
            currentGroup = entryIndex >= 0 && entryIndex < entries.Count ? entries[entryIndex].group : -1;
        }

        public static void ClearContext()
        {
            currentGroup = -1;
        }

        // True with the scale to use when a group is being drawn and the
        // enlarged layout is on; false means "let vanilla's getter run".
        public static bool TryGetContextScale(out float scale)
        {
            scale = 1f;
            if (!active || currentGroup < 0 || currentGroup >= groupScale.Length)
            {
                return false;
            }
            scale = groupScale[currentGroup];
            return true;
        }

        // Scale of the group the entry at this index belongs to, for the
        // hit-testing transpilers. Falls back to vanilla's own value.
        public static float ScaleForEntry(int entryIndex)
        {
            ColonistBar bar = Find.ColonistBar;
            if (active)
            {
                List<ColonistBar.Entry> entries = bar.Entries;
                if (entryIndex >= 0 && entryIndex < entries.Count)
                {
                    int g = entries[entryIndex].group;
                    if (g >= 0 && g < groupScale.Length)
                    {
                        return groupScale[g];
                    }
                }
            }
            return bar.Scale;
        }

        public static float SizeXForEntry(int entryIndex)
        {
            return ColonistBar.BaseSize.x * ScaleForEntry(entryIndex);
        }

        public static float SizeYForEntry(int entryIndex)
        {
            return ColonistBar.BaseSize.y * ScaleForEntry(entryIndex);
        }

        // ---- weapon icon under each portrait ------------------------------

        // Set by the ColonistBarOnGUI transpiler once it has swapped vanilla's
        // 1.05 weapon offset for WeaponOffsetFactor. Without the swap the
        // wider row spacing would be pointless, so it is not applied.
        public static void MarkWeaponHook()
        {
            weaponHookOk = true;
        }

        // Pixels (not scaled) the weapon icon sits below the box bottom: clear of
        // the name label, which spans 4 * scale above the box bottom down labelH.
        private static float WeaponClearance(float s)
        {
            float labelH = Prefs.DisableTinyText ? 16f : 12f;
            // 2026-10-18: halved on "space between the owner and the weapon about half", then
            // 0.75 on "Undo the distance between weapon and pawn. Make it what you think to reduce distance."
            return (Mathf.Max(0f, labelH - 4f * s) + 1f) * 0.75f;
        }

        // Distance between rows that keeps the icon (vanilla: 0.75 of the box)
        // clear of the next row's portrait, which rises 27 * scale above its box
        // (ColonistBarColonistDrawer.GetPawnTextureRect). Never less than vanilla's 80 * scale.
        private static float WrappedRowPitch(float s)
        {
            float box = ColonistBar.BaseSize.y;
            float overhang = ColonistBarColonistDrawer.PawnTextureSize.y - box;
            return Mathf.Max(PitchY * s, (box + 0.75f * box + overhang - RowTighten) * s + WeaponClearance(s));
        }

        // Height of the last row of a wrapped group: the row's own pitch, or the
        // weapon icon's real bottom (box + 0.75 * box below the row top, plus
        // clearance) when that reaches lower.
        private static float WrappedLastRowExtent(float s)
        {
            float box = ColonistBar.BaseSize.y;
            return Mathf.Max(PitchY * s, (box + 0.75f * box) * s + WeaponClearance(s) + FramePad * s);
        }

        // Bottom of the weapon icon below the row top, in UI pixels, for a group
        // whose icons are tight under the portrait; -1 when the group is not.
        // Icon rect bottom = y + h * (factor + 0.875) = y + 1.75h + clearance.
        public static float WeaponBottomBelowRowTop(int group, out float pad)
        {
            pad = 0f;
            if (!active || group < 0 || group >= groupWeaponTight.Length || !groupWeaponTight[group])
            {
                return -1f;
            }
            float s = groupScale[group];
            pad = FramePad * s;
            return 1.75f * ColonistBar.BaseSize.y * s + WeaponClearance(s);
        }

        private static bool WeaponsShown()
        {
            return weaponHookOk && Prefs.ShowWeaponsUnderPortraitMode != ShowWeaponsUnderPortraitMode.Never;
        }

        // Replaces the 1.05f in vanilla's `new Rect(x, y + height * 1.05f, ..).ScaledBy(0.75f)`.
        // ScaledBy shrinks about the centre, so the icon's top edge lies at
        // (factor + 0.125) * height under the box top: vanilla 1.175 (56 * scale),
        // here just under the name label, i.e. 48 * scale + WeaponClearance.
        // Vanilla's value for every group that is not wrapped.
        public static float WeaponOffsetFactor()
        {
            if (!active || currentGroup < 0 || currentGroup >= groupWeaponTight.Length || !groupWeaponTight[currentGroup])
            {
                return 1.05f;
            }
            float s = groupScale[currentGroup];
            return 0.875f + WeaponClearance(s) / (ColonistBar.BaseSize.y * s);
        }

        // ---- layout -----------------------------------------------------

        // Returns true so the caller can hand the layout back to vanilla.
        private static bool DeclineToVanilla(string reason, string mapLabel)
        {
            active = false;
            LogOnce("vanilla|" + reason + "|" + mapLabel,
                "Colonist bar: vanilla layout (" + reason + ")" + (mapLabel.Length > 0 ? ", current map " + mapLabel : "") + ".");
            return true;
        }

        // Returns true when vanilla should compute the layout instead
        // (disabled, one group, no group for the current map, no room, or
        // vanilla's private methods could not be reached).
        public static bool TryComputeLayout(ColonistBarDrawLocsFinder finder, List<Vector2> outDrawLocs, ref float scale, int groupsCount)
        {
            InterfaceChanges.Core.InterfaceChangesSettings settings = InterfaceChanges.Core.InterfaceChangesMod.Settings;
            List<ColonistBar.Entry> entries = Find.ColonistBar.Entries;
            if (settings == null || !settings.enlargeCurrentMapGroup)
            {
                return DeclineToVanilla("setting off", "");
            }
            if (entries.Count == 0 || groupsCount < 2)
            {
                return DeclineToVanilla("one group only", "");
            }
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return DeclineToVanilla("no current map", "");
            }
            string mapLabel = "map " + map.uniqueID;

            int bigGroup = -1;
            int[] counts = new int[groupsCount];
            for (int i = 0; i < entries.Count; i++)
            {
                counts[entries[i].group]++;
                if (bigGroup < 0 && entries[i].map == map)
                {
                    bigGroup = entries[i].group;
                }
            }
            if (bigGroup < 0)
            {
                return DeclineToVanilla("current map has no group", mapLabel);
            }
            if (!EnsureReflection())
            {
                return DeclineToVanilla("vanilla methods not reachable", mapLabel);
            }

            // s0: the one scale vanilla itself would have used. It is the
            // 100% mark of the slider.
            calcInGroup(finder, groupsCount);
            bool onlyOneRow;
            int maxPerRow;
            float s0 = findBestScale(finder, out onlyOneRow, out maxPerRow, groupsCount);

            float percent = Mathf.Clamp(settings.mapGroupSizePercent, 100f, 300f);
            float avail = UI.screenWidth - 2f * SideMargin;
            float requestedBig = s0 * percent / 100f;

            int maxRows = Mathf.Clamp(settings.mapGroupMaxRows, 1, 5);
            float sBig = requestedBig;
            float sOther = 0f;
            int[] slots = null;
            int slotsBig = counts[bigGroup];
            bool found = false;
            bool bigReduced = false;
            bool heightReason = false;
            bool bigHeightReason = false;
            float maxBottom = UI.screenHeight - BottomBarHeight - BottomMargin;
            while (true)
            {
                // Fewest rows that lets everything fit at this size, in
                // width and in height (bottom edge above the bottom bar).
                for (int rows = 1; rows <= maxRows && rows <= counts[bigGroup] && !found; rows++)
                {
                    bool tight = rows > 1 && WeaponsShown();
                    float rowPitch = tight ? WrappedRowPitch(sBig) : PitchY * sBig;
                    float lastRow = tight ? WrappedLastRowExtent(sBig) : PitchY * sBig;
                    if (MarginTop + (rows - 1) * rowPitch + lastRow > maxBottom)
                    {
                        heightReason = true;
                        break;
                    }
                    slotsBig = Mathf.Max(1, (counts[bigGroup] + rows - 1) / rows);
                    found = TryFitOthers(counts, bigGroup, slotsBig, sBig, s0, avail, out sOther, out slots);
                }
                if (found || sBig <= s0 + 0.0001f)
                {
                    break;
                }
                // The reason shown is the one that blocked the last size tried.
                bigHeightReason = heightReason;
                heightReason = false;
                sBig = Mathf.Max(s0, sBig * 0.95f);
                bigReduced = true;
            }
            if (!found)
            {
                return DeclineToVanilla("no room for a larger group", mapLabel);
            }

            // The enlarged group wraps over as many rows as the fit chose.
            slots[bigGroup] = slotsBig;
            int bigRows = (counts[bigGroup] + slotsBig - 1) / slotsBig;
            float[] newScale = new float[groupsCount];
            bool wrapTight = bigRows > 1 && WeaponsShown();
            bool[] newTight = new bool[groupsCount];
            newTight[bigGroup] = wrapTight;
            for (int g = 0; g < groupsCount; g++)
            {
                newScale[g] = g == bigGroup ? sBig : sOther;
                slots[g] = Mathf.Max(1, Mathf.Min(slots[g], counts[g]));
            }

            // Group start positions, in vanilla group order, centred as vanilla does.
            float[] startX = new float[groupsCount];
            float total = 0f;
            for (int g = 0; g < groupsCount; g++)
            {
                if (g > 0)
                {
                    total += GapBetween(newScale[g - 1], newScale[g], sOther);
                }
                startX[g] = total;
                total += slots[g] * PitchX * newScale[g];
            }
            // Outer group frames hang FrameMargin * scale past the first and last boxes.
            float outerLeft = FrameMargin * newScale[0];
            float extent = outerLeft + total + FrameMargin * newScale[groupsCount - 1];
            // The last slot's pitch is wider than its box (68 vs 48 per scale), so the
            // visible bar ends 20 * scale before the reserved extent; centre the frames, not the slots.
            // The fit test used the full extent, so the visible bar is never wider than avail.
            float visibleExtent = extent - (PitchX - ColonistBar.BaseSize.x) * newScale[groupsCount - 1];
            float left = SideMargin + (avail - visibleExtent) / 2f + outerLeft;

            outDrawLocs.Clear();
            int[] seen = new int[groupsCount];
            for (int i = 0; i < entries.Count; i++)
            {
                int g = entries[i].group;
                int n = seen[g]++;
                float s = newScale[g];
                int perRow = slots[g];
                float x = left + startX[g] + (n % perRow) * PitchX * s;
                float y = MarginTop + (n / perRow) * (g == bigGroup && wrapTight ? WrappedRowPitch(s) : PitchY * s);
                // The last, short row is centred, as vanilla's GetDrawLoc does.
                if (n >= counts[g] - counts[g] % perRow)
                {
                    int empty = (perRow - counts[g] % perRow) % perRow;
                    x += empty * PitchX * s * 0.5f;
                }
                outDrawLocs.Add(new Vector2(x, y));
            }

            groupScale = newScale;
            groupWeaponTight = newTight;
            scale = sOther;
            active = true;
            LogOnce("big|" + map.uniqueID + "|" + wrapTight + "|" + bigRows + "|" + maxRows + "|" + percent.ToString("F0") + "|" + sBig.ToString("F2") + "|" + sOther.ToString("F2"),
                "Colonist bar: " + mapLabel + " group enlarged to " + percent.ToString("F0") + "% (scale " + sBig.ToString("F2")
                + (bigReduced ? (bigHeightReason ? ", reduced to fit height" : ", reduced to fit width") : "") + ", " + bigRows + " row(s) of at most " + maxRows + "), other groups scale " + sOther.ToString("F2")
                + ", vanilla scale " + s0.ToString("F2") + (wrapTight ? ", rows spaced for weapon icons." : "."));
            return false;
        }

        // Frame margins of both neighbours plus vanilla's 1-pixel-per-scale
        // slack, so frames never overlap; two equal scales give vanilla's 25 * scale.
        private static float GapBetween(float a, float b, float sOther)
        {
            return FrameMargin * a + FrameMargin * b + sOther;
        }

        // Vanilla's shrink search, run over the groups other than the
        // enlarged one, in the width the enlarged group leaves over.
        private static bool TryFitOthers(int[] counts, int bigGroup, int slotsBig, float sBig, float s0, float avail, out float sOther, out int[] slots)
        {
            int groupsCount = counts.Length;
            slots = new int[groupsCount];
            sOther = s0;
            while (sOther >= MinOtherScale - 0.0001f)
            {
                float width = slotsBig * PitchX * sBig;
                for (int g = 1; g < groupsCount; g++)
                {
                    float a = g - 1 == bigGroup ? sBig : sOther;
                    float b = g == bigGroup ? sBig : sOther;
                    width += GapBetween(a, b, sOther);
                }
                width += FrameMargin * (0 == bigGroup ? sBig : sOther) + FrameMargin * (groupsCount - 1 == bigGroup ? sBig : sOther);
                float remaining = avail - width;
                int maxPerRow = Mathf.FloorToInt(remaining / (PitchX * sOther));
                if (maxPerRow >= 1 && DistributeOthers(counts, bigGroup, maxPerRow, slots))
                {
                    int allowedRows = sOther > 0.58f ? 1 : (sOther > 0.42f ? 2 : 3);
                    bool fits = true;
                    for (int g = 0; g < groupsCount && fits; g++)
                    {
                        if (g == bigGroup)
                        {
                            continue;
                        }
                        fits = Mathf.CeilToInt((float)counts[g] / slots[g]) <= allowedRows;
                    }
                    if (fits)
                    {
                        return true;
                    }
                }
                sOther *= 0.95f;
            }
            return false;
        }

        // Vanilla's TryDistributeHorizontalSlotsBetweenGroups, over the
        // other groups only (the enlarged group's slot is set by the caller).
        private static bool DistributeOthers(int[] counts, int bigGroup, int maxPerRow, int[] slots)
        {
            List<int> others = new List<int>();
            List<int> otherIndex = new List<int>();
            for (int g = 0; g < counts.Length; g++)
            {
                if (g != bigGroup)
                {
                    others.Add(0);
                    otherIndex.Add(g);
                }
            }
            GenMath.DHondtDistribution(others, i => counts[otherIndex[i]], maxPerRow);
            for (int k = 0; k < others.Count; k++)
            {
                if (others[k] == 0)
                {
                    int max = 0;
                    int maxAt = 0;
                    for (int j = 0; j < others.Count; j++)
                    {
                        if (others[j] > max)
                        {
                            max = others[j];
                            maxAt = j;
                        }
                    }
                    if (max <= 1)
                    {
                        return false;
                    }
                    others[maxAt]--;
                    others[k]++;
                }
            }
            for (int i = 0; i < others.Count; i++)
            {
                slots[otherIndex[i]] = others[i];
            }
            return true;
        }

        private static bool EnsureReflection()
        {
            if (reflectionTried)
            {
                return reflectionOk;
            }
            reflectionTried = true;
            try
            {
                MethodInfo find = AccessTools.Method(typeof(ColonistBarDrawLocsFinder), "FindBestScale");
                MethodInfo calc = AccessTools.Method(typeof(ColonistBarDrawLocsFinder), "CalculateColonistsInGroup");
                if (find == null || calc == null)
                {
                    Logger.Warning("Colonist bar: FindBestScale or CalculateColonistsInGroup not found; the larger group is disabled.");
                    return false;
                }
                findBestScale = (FindBestScaleDel)Delegate.CreateDelegate(typeof(FindBestScaleDel), find);
                calcInGroup = (CalcInGroupDel)Delegate.CreateDelegate(typeof(CalcInGroupDel), calc);
                reflectionOk = true;
            }
            catch (Exception e)
            {
                Logger.Warning("Colonist bar: could not bind vanilla's private methods (" + e.Message + "); the larger group is disabled.");
            }
            return reflectionOk;
        }

        // One line per change of layout, never per frame or per recache.
        private static void LogOnce(string key, string message)
        {
            if (key == lastLogKey)
            {
                return;
            }
            lastLogKey = key;
            Logger.Info(message);
        }
    }
}
