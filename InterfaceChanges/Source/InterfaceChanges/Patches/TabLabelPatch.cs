using System.Reflection;
using HarmonyLib;
using InterfaceChanges.Bindings;
using RimWorld;
using UnityEngine;
using Verse;
using Logger = InterfaceChanges.Utility.Logger;

namespace InterfaceChanges.Patches
{
    // Postfix on RimWorld.MainButtonWorker.DoButton(Rect), the method that
    // draws every bottom-bar main tab button. Verified by reflection on
    // lib\Assembly-CSharp.dll: only MainButtonWorker declares DoButton; the
    // ToggleTab, ToggleWorld, ToggleMechTab and ToggleResearchTab workers
    // do not override it. Draws the tab's key at the left edge of the button,
    // left of the label. Shows whatever key is bound - vanilla's, another
    // mod's or ours. Applied by hand so a missing target logs, not throws.
    // See ic-architecture.md section 8.
    public static class TabLabelPatch
    {
        private const float LeftPad = 6f;

        public static void Apply(Harmony harmony)
        {
            MethodInfo target = AccessTools.DeclaredMethod(typeof(MainButtonWorker), "DoButton");
            if (target == null)
            {
                Logger.Warning("TabLabelPatch: MainButtonWorker.DoButton not found, tab keys will not be drawn.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(TabLabelPatch), nameof(Postfix))));
            Logger.Info("TabLabelPatch applied to MainButtonWorker.DoButton.");
        }

        public static void Postfix(MainButtonWorker __instance, Rect rect)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            MainButtonDef def = __instance.def;
            KeyBindingDef key = def.hotKey;
            if (key == null)
            {
                foreach (var pair in GeneratedKeys.MainTabs)
                {
                    if (pair.Key == def)
                    {
                        key = pair.Value;
                        break;
                    }
                }
            }

            if (key == null || key.MainKey == KeyCode.None)
            {
                return;
            }

            GameFont oldFont = Text.Font;
            TextAnchor oldAnchor = Text.Anchor;
            Color oldColor = GUI.color;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            Widgets.Label(new Rect(rect.x + LeftPad, rect.y, 40f, rect.height), key.MainKeyLabel);
            GUI.color = oldColor;
            Text.Anchor = oldAnchor;
            Text.Font = oldFont;
        }
    }
}
