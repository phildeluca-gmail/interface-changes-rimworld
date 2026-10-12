using System.Collections.Generic;
using HarmonyLib;
using InterfaceChanges.Drop;
using RimWorld;
using Verse;

namespace InterfaceChanges.Patches
{
    // Postfix on Verse.Pawn.GetGizmos: adds "Drop everything" for colonists.
    // The groupKey differs from Do Not Be Lazy's copy of the button, so the
    // two never merge if both mods are loaded. GizmoGridDrawer merges
    // same-groupKey gizmos into one button but still runs every pawn's
    // action on click, which gives each selected pawn its own queue.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class DropGizmoPatch
    {
        private const int GroupKey = 0x49434450; // "ICDP"

        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (Gizmo gizmo in gizmos)
            {
                yield return gizmo;
            }

            if (__instance != null && __instance.IsColonist && __instance.Spawned && !__instance.Dead)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Drop everything",
                    defaultDesc = "Drop every carried item and worn apparel piece. "
                        + "Locked apparel and warcaskets are left where they are. "
                        + "The equipped weapon is kept unless the setting in Mod options says otherwise.",
                    icon = TexButton.Drop,
                    groupKey = GroupKey,
                    action = () => DropEverything.Queue(__instance)
                };
            }
        }
    }
}
