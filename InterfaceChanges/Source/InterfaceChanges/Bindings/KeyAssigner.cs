using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Logger = InterfaceChanges.Utility.Logger;
using RimWorld;
using UnityEngine;
using Verse;

namespace InterfaceChanges.Bindings
{
    // What KeyAssigner generated, kept so the key handler can look it up.
    public static class GeneratedKeys
    {
        public static readonly List<KeyValuePair<MainButtonDef, KeyBindingDef>> MainTabs
            = new List<KeyValuePair<MainButtonDef, KeyBindingDef>>();

        public static readonly List<KeyValuePair<DesignationCategoryDef, KeyBindingDef>> Categories
            = new List<KeyValuePair<DesignationCategoryDef, KeyBindingDef>>();
    }

    // Startup pass, ic-architecture.md section 3. Gives every main tab, architect
    // category and architect option that has no hotkey one, generated as a
    // KeyBindingDef so it shows in Options > Key Bindings and can be rebound.
    //
    // Timing, verified in lib\Assembly-CSharp.dll (Verse.PlayDataLoader): this
    // runs from [StaticConstructorOnStartup], which is queued AFTER every
    // DesignationCategoryDef's ResolveDesignators callback, so the designators
    // exist; but KeyPrefs.Init has already run, so any binding the player saved
    // for one of our defs sits in KeyPrefs' private unresolvedBindings and is
    // moved across by hand below, or a rebind would be lost every launch.
    [StaticConstructorOnStartup]
    public static class KeyAssigner
    {
        static KeyAssigner()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Logger.Warning("Key assignment failed, no generated hotkeys this session: " + ex);
            }
        }

        private static void Run()
        {
            KeyPrefsData prefs = KeyPrefs.KeyPrefsData;
            if (prefs == null)
            {
                Logger.Warning("KeyPrefs not loaded yet, no generated hotkeys this session.");
                return;
            }

            // Never alter an existing binding (ordered 2026-10-06). Every key any
            // KeyBindingDef uses, in any category, default or as the player has
            // set it, is off limits - the Development and debug categories too.
            // Generated keys only ever go on tabs, categories and options that
            // have no key, and only on keys nothing else uses.
            var allBound = new HashSet<KeyCode>();
            int defsScanned = 0;
            foreach (KeyBindingDef def in DefDatabase<KeyBindingDef>.AllDefs)
            {
                AddKeys(allBound, def, prefs);
                defsScanned++;
            }

            allBound.Remove(KeyCode.None);
            var liveInTabs = allBound;
            var liveInArchitect = allBound;

            var generated = new List<KeyBindingDef>();
            var unbound = new List<string>();
            int tabsTotal = 0, tabsBound = 0, catsTotal = 0, catsBound = 0, optsTotal = 0, optsBound = 0;

            // 1. Main tabs - unique among themselves, and off every key live with them.
            KeyBindingCategoryDef tabCat = MakeCategory("IC_MainTabs", "Interface Changes - main tabs",
                "Development", "Game", "GameMapCamera", "SelectionMisc", "MainTabs");
            var tabTaken = new HashSet<KeyCode>(liveInTabs);
            var tabs = new List<MainButtonDef>(DefDatabase<MainButtonDef>.AllDefsListForReading);
            tabs.Sort((a, b) => a.order.CompareTo(b.order));
            foreach (MainButtonDef tab in tabs)
            {
                if (tab.hotKey != null || !tab.buttonVisible)
                {
                    continue;
                }

                tabsTotal++;
                string tabLabel = tab.LabelCap.ToString();
                KeyCode tabKey = Pick(tabLabel, tabTaken);
                KeyBindingDef tabKb = MakeDef("IC_Tab_" + tab.defName, tabLabel, tabCat, tabKey);
                generated.Add(tabKb);
                GeneratedKeys.MainTabs.Add(new KeyValuePair<MainButtonDef, KeyBindingDef>(tab, tabKb));
                if (tabKey != KeyCode.None) tabsBound++; else unbound.Add("tab " + tabLabel + Why(tabLabel));
            }

            // 2. Architect categories - live only while the architect tab is
            // open, where the main tab keys are live too.
            KeyBindingCategoryDef catCat = MakeCategory("IC_ArchitectCategories", "Interface Changes - architect categories",
                "Development", "Game", "GameMapCamera", "Architect", "MainTabs", "IC_MainTabs");
            var catTaken = new HashSet<KeyCode>(liveInArchitect);
            foreach (KeyBindingDef g in generated)
            {
                AddKeys(catTaken, g, null);
            }

            var categories = new List<DesignationCategoryDef>(DefDatabase<DesignationCategoryDef>.AllDefsListForReading);
            categories.Sort((a, b) => b.order.CompareTo(a.order));
            foreach (DesignationCategoryDef cat in categories)
            {
                catsTotal++;
                string catLabel = cat.LabelCap.ToString();
                KeyCode catKey = Pick(catLabel, catTaken);
                KeyBindingDef catKb = MakeDef("IC_Cat_" + cat.defName, catLabel, catCat, catKey);
                generated.Add(catKb);
                GeneratedKeys.Categories.Add(new KeyValuePair<DesignationCategoryDef, KeyBindingDef>(cat, catKb));
                if (catKey != KeyCode.None) catsBound++; else unbound.Add("category " + catLabel + Why(catLabel));
            }

            // 3. Architect options - unique within their own panel, live only
            // while that panel is open, so the same letter may repeat across
            // panels. A designator's own hotKey (vanilla gives some) is kept.
            var archBase = new HashSet<KeyCode>(liveInArchitect);
            foreach (KeyBindingDef g in generated)
            {
                AddKeys(archBase, g, null);
            }

            foreach (DesignationCategoryDef cat in categories)
            {
                KeyBindingCategoryDef optCat = MakeCategory("IC_Arch_" + cat.defName,
                    "Interface Changes - architect: " + cat.LabelCap,
                    "Development", "Game", "GameMapCamera", "Architect", "MainTabs", "IC_MainTabs", "IC_ArchitectCategories");
                var taken = new HashSet<KeyCode>(archBase);
                List<Designator> designators = cat.AllResolvedDesignators;
                foreach (Designator d in designators)
                {
                    if (d.hotKey != null)
                    {
                        AddKeys(taken, d.hotKey, prefs);
                    }
                }

                var usedIds = new HashSet<string>();
                foreach (Designator d in designators)
                {
                    if (d.hotKey != null)
                    {
                        continue;
                    }

                    string label;
                    string id;
                    try
                    {
                        label = d.Label;
                        id = d is Designator_Build build && build.PlacingDef != null ? build.PlacingDef.defName : d.GetType().Name;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (label.NullOrEmpty())
                    {
                        continue;
                    }

                    string uniqueId = id;
                    for (int n = 2; !usedIds.Add(uniqueId); n++)
                    {
                        uniqueId = id + "_" + n;
                    }

                    optsTotal++;
                    KeyCode key = Pick(label, taken);
                    KeyBindingDef kb = MakeDef("IC_Des_" + cat.defName + "_" + uniqueId, cat.LabelCap + ": " + label, optCat, key);
                    generated.Add(kb);
                    d.hotKey = kb;
                    if (key != KeyCode.None) optsBound++; else unbound.Add("option " + label + " (" + cat.LabelCap + ")" + Why(label));
                }
            }

            // Register, then restore anything the player saved.
            var unresolved = AccessTools.Field(typeof(KeyPrefs), "unresolvedBindings").GetValue(null) as Dictionary<string, KeyBindingData>;
            int restored = 0;
            foreach (KeyBindingDef g in generated)
            {
                DefDatabase<KeyBindingDef>.Add(g);
                if (unresolved != null && unresolved.TryGetValue(g.defName, out KeyBindingData saved))
                {
                    prefs.keyPrefs[g] = saved;
                    unresolved.Remove(g.defName);
                    restored++;
                }
            }

            prefs.AddMissingDefaultBindings();

            var sb = new StringBuilder();
            sb.Append("Hotkeys generated: main tabs " + tabsBound + "/" + tabsTotal
                + ", architect categories " + catsBound + "/" + catsTotal
                + ", architect options " + optsBound + "/" + optsTotal + "; ");
            sb.Append("existing bindings untouched (" + defsScanned + " scanned, " + allBound.Count + " keys kept off limits); ");
            sb.Append(restored + " restored from saved bindings; " + unbound.Count + " left unbound");
            if (unbound.Count > 0)
            {
                sb.Append(": ").Append(string.Join(", ", unbound.GetRange(0, Math.Min(unbound.Count, 25)).ToArray()));
                if (unbound.Count > 25) sb.Append(", ...");
            }

            sb.Append('.');
            Logger.Info(sb.ToString());
        }

        // Why Pick came back empty: nothing to try, or every candidate taken.
        private static string Why(string label)
        {
            foreach (char c in label)
            {
                if (char.IsLetterOrDigit(c) && c < 128)
                {
                    return " [every letter in the label is taken]";
                }
            }

            return " [label has no usable letter]";
        }

        private static void AddKeys(HashSet<KeyCode> set, KeyBindingDef def, KeyPrefsData prefs)
        {
            set.Add(def.defaultKeyCodeA);
            set.Add(def.defaultKeyCodeB);
            if (prefs != null && prefs.keyPrefs.TryGetValue(def, out KeyBindingData data))
            {
                set.Add(data.keyBindingA);
                set.Add(data.keyBindingB);
            }
        }

        // First letter (or digit) of the label not in taken, else the second,
        // and so on. Adds the winner to taken. None if the label runs out.
        private static KeyCode Pick(string label, HashSet<KeyCode> taken)
        {
            foreach (char c in label)
            {
                KeyCode code = KeyCode.None;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                {
                    Enum.TryParse(char.ToUpperInvariant(c).ToString(), out code);
                }
                else if (c >= '0' && c <= '9')
                {
                    Enum.TryParse("Alpha" + c, out code);
                }

                if (code != KeyCode.None && !taken.Contains(code))
                {
                    taken.Add(code);
                    return code;
                }
            }

            return KeyCode.None;
        }

        private static KeyBindingDef MakeDef(string defName, string label, KeyBindingCategoryDef category, KeyCode key)
        {
            return new KeyBindingDef
            {
                defName = defName,
                label = label,
                category = category,
                defaultKeyCodeA = key,
            };
        }

        private static KeyBindingCategoryDef MakeCategory(string defName, string label, params string[] conflictsWith)
        {
            KeyBindingCategoryDef existing = DefDatabase<KeyBindingCategoryDef>.GetNamedSilentFail(defName);
            if (existing != null)
            {
                return existing;
            }

            var cat = new KeyBindingCategoryDef
            {
                defName = defName,
                label = label,
                selfConflicting = true,
            };
            foreach (string other in conflictsWith)
            {
                KeyBindingCategoryDef o = DefDatabase<KeyBindingCategoryDef>.GetNamedSilentFail(other);
                if (o != null)
                {
                    cat.checkForConflicts.Add(o);
                }
            }

            DefDatabase<KeyBindingCategoryDef>.Add(cat);
            return cat;
        }
    }
}
