using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Logger = InterfaceChanges.Utility.Logger;

namespace InterfaceChanges.Drop
{
    [DefOf]
    public static class IcJobDefOf
    {
        public static JobDef IC_DropInventoryStack;

        static IcJobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(IcJobDefOf));
        }
    }

    // Drops one stack from the pawn's own inventory. Vanilla has queued jobs
    // for worn apparel (JobDriver_RemoveApparel) and the equipped weapon
    // (JobDriver_DropEquipment) but none for an inventory stack. Matches
    // ITab_Pawn_Gear.InterfaceDrop's inventory branch: innerContainer.TryDrop
    // with ThingPlaceMode.Near, result left unforbidden.
    public class JobDriver_DropInventoryStack : JobDriver
    {
        // Nothing to reserve - the target is in the pawn's own inventory.
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil toil = new Toil();
            toil.initAction = () =>
            {
                Thing thing = job.GetTarget(TargetIndex.A).Thing;
                if (thing == null || thing.Destroyed || !pawn.inventory.innerContainer.Contains(thing))
                {
                    // Gone by the time its queued turn came - an expected race.
                    Logger.Info(pawn.LabelShort + ": inventory stack for a queued drop was already gone, skipping it");
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out Thing _);
                Logger.Info(pawn.LabelShort + " dropped " + thing.LabelCap + " from inventory (drop everything)");
                EndJobWith(JobCondition.Succeeded);
            };
            yield return toil;
        }
    }
}
