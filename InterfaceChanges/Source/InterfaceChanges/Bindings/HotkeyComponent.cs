using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using InterfaceChanges.Drop;
using RimWorld;
using UnityEngine;
using Verse;
using Logger = InterfaceChanges.Utility.Logger;

namespace InterfaceChanges.Bindings
{
    // Handles the generated main tab and architect category keys, and polls
    // Drop everything orders. A GameComponent, so no Harmony patch is needed
    // (RimWorld constructs one per game from every GameComponent subclass).
    //
    // GameComponentOnGUI runs at the very end of UIRoot.UIRootOnGUI, after the
    // windows have had the event, so a key typed into a text box has already
    // been used up and is not seen here. GUIUtility.keyboardControl is checked
    // as well. The designator option keys need nothing here - vanilla's
    // Command.GizmoOnGUI fires Command.hotKey itself while the panel is drawn.
    public class HotkeyComponent : GameComponent
    {
        private static readonly FieldInfo desPanelsCached = AccessTools.Field(typeof(MainTabWindow_Architect), "desPanelsCached");
        private static readonly MethodInfo clickedCategory = AccessTools.Method(typeof(MainTabWindow_Architect), "ClickedCategory");

        private int ticks;

        public HotkeyComponent(Game game)
        {
        }

        public override void FinalizeInit()
        {
            DropEverything.EndAllForLoad();
        }

        public override void GameComponentTick()
        {
            ticks++;
            if (ticks % 60 == 0)
            {
                DropEverything.PollOrders();
            }
        }

        public override void GameComponentOnGUI()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown || e.keyCode == KeyCode.None)
            {
                return;
            }

            if (e.control || e.alt || e.command || GUIUtility.keyboardControl != 0)
            {
                return;
            }

            foreach (KeyValuePair<MainButtonDef, KeyBindingDef> pair in GeneratedKeys.MainTabs)
            {
                if (pair.Value.KeyDownEvent && !pair.Key.Worker.Disabled)
                {
                    Logger.Info("Hotkey " + pair.Value.MainKeyLabel + " pressed: main tab " + pair.Key.LabelCap + ".");
                    e.Use();
                    pair.Key.Worker.InterfaceTryActivate();
                    return;
                }
            }

            if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Architect)
            {
                return;
            }

            var window = MainButtonDefOf.Architect.TabWindow as MainTabWindow_Architect;
            var panels = window == null || desPanelsCached == null ? null : desPanelsCached.GetValue(window) as List<ArchitectCategoryTab>;
            if (panels == null || clickedCategory == null)
            {
                return;
            }

            foreach (KeyValuePair<DesignationCategoryDef, KeyBindingDef> pair in GeneratedKeys.Categories)
            {
                if (!pair.Value.KeyDownEvent)
                {
                    continue;
                }

                foreach (ArchitectCategoryTab panel in panels)
                {
                    if (panel.def == pair.Key)
                    {
                        Logger.Info("Hotkey " + pair.Value.MainKeyLabel + " pressed: architect category " + pair.Key.LabelCap
                            + (panel.Visible ? "." : " (nothing available in it, the game says so)."));
                        e.Use();
                        clickedCategory.Invoke(window, new object[] { panel });
                        return;
                    }
                }
            }
        }
    }
}
