using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using RimWorld;
using Verse;
using Logger = InterfaceChanges.Utility.Logger;

namespace InterfaceChanges.Patches
{
    // Postfix on RimWorld.MainTabWindow_Inspect.DoWindowContents: when more
    // than one thing is selected the lower-left box shows only a label such
    // as "Colonist x5" and no body (ShouldShowPaneContents is false for
    // NumSelected != 1). This draws the selected pawns' names in that empty
    // body, as "Nickname - first name". Applied by hand, not by attribute,
    // so a missing target is logged instead of throwing out of PatchAll.
    // See ic-architecture.md section 7.
    public static class SelectionNamesPatch
    {
        private const float Margin = 12f;      // InspectPaneUtility.PaneInnerMargin
        private const float TopOffset = 34f;   // below the Medium-font label
        private const float LineHeight = 22f;
        private const float ColumnWidth = 200f;

        public static void Apply(Harmony harmony)
        {
            MethodInfo target = AccessTools.DeclaredMethod(typeof(MainTabWindow_Inspect), "DoWindowContents");
            if (target == null)
            {
                Logger.Warning("SelectionNamesPatch: MainTabWindow_Inspect.DoWindowContents not found, selection names will not be drawn.");
                return;
            }
            MethodInfo postfix = AccessTools.Method(typeof(SelectionNamesPatch), nameof(Postfix));
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Logger.Info("SelectionNamesPatch applied to MainTabWindow_Inspect.DoWindowContents.");
        }

        public static void Postfix(Rect inRect)
        {
            List<object> selected = Find.Selector.SelectedObjects;
            if (selected.Count < 2)
            {
                return;
            }

            Rect area = inRect.ContractedBy(Margin);
            area.yMin += TopOffset;
            int columns = Mathf.Max(1, (int)(area.width / ColumnWidth));
            int rows = Mathf.Max(1, (int)(area.height / LineHeight));
            int capacity = columns * rows;

            int pawnCount = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is Pawn)
                {
                    pawnCount++;
                }
            }
            if (pawnCount == 0)
            {
                return;
            }

            // When the pawns do not all fit, the last slot is the "+N more" line.
            int shown = pawnCount > capacity ? capacity - 1 : pawnCount;

            GameFont oldFont = Text.Font;
            TextAnchor oldAnchor = Text.Anchor;
            bool oldWrap = Text.WordWrap;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = false;

            int slot = 0;
            for (int i = 0; i < selected.Count && slot < shown; i++)
            {
                if (selected[i] is Pawn pawn)
                {
                    DrawSlot(area, slot, rows, NameFor(pawn));
                    slot++;
                }
            }
            if (shown < pawnCount)
            {
                DrawSlot(area, slot, rows, "+" + (pawnCount - shown) + " more");
            }

            Text.Font = oldFont;
            Text.Anchor = oldAnchor;
            Text.WordWrap = oldWrap;
        }

        private static void DrawSlot(Rect area, int slot, int rows, string text)
        {
            float x = area.x + (slot / rows) * ColumnWidth;
            float y = area.y + (slot % rows) * LineHeight;
            Rect rect = new Rect(x, y, ColumnWidth - 8f, LineHeight);
            Widgets.Label(rect, text.Truncate(rect.width));
        }

        // "Nickname - first name" for a humanlike with a name triple; the
        // name once when nickname and first name are the same; the short
        // label for everything else (animals, mechs, single names).
        public static string NameFor(Pawn pawn)
        {
            if (pawn.Name is NameTriple triple)
            {
                string nick = triple.Nick;
                string first = triple.First;
                if (nick.NullOrEmpty())
                {
                    return first.NullOrEmpty() ? pawn.LabelShort : first;
                }
                if (first.NullOrEmpty() || nick == first)
                {
                    return nick;
                }
                return nick + " - " + first;
            }
            return pawn.LabelShort;
        }
    }
}
