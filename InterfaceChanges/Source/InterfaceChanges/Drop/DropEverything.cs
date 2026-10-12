using System;
using System.Collections.Generic;
using InterfaceChanges.Core;
using RimWorld;
using Verse;
using Verse.AI;
using Logger = InterfaceChanges.Utility.Logger;

namespace InterfaceChanges.Drop
{
    // The "Drop everything" order, ic-architecture.md section 4. A standalone
    // copy of Do Not Be Lazy's; nothing here refers to that mod.
    //
    // Queue order follows Pawn_JobTracker.TryTakeOrderedJob: the first call per
    // pawn passes requestQueueing false (replaces the current job), every later
    // call passes true (appends).
    public static class DropEverything
    {
        private static readonly string launchStamp = DateTime.Now.ToString("HHmm");
        private static int orderCounter;
        private const int GiveUpTicks = 15000; // 6 in-game hours

        // Each order remembers the exact jobs it queued, so it ends when those
        // are gone, not when the whole queue is empty (other mods or the game
        // may queue their own jobs behind ours and keep the queue non-empty).
        private class PendingOrder
        {
            public string id;
            public List<Job> jobs = new List<Job>();
            // JobMaker pools finished Job objects and reuses them, so a bare
            // reference can match an unrelated later job. The def is recorded
            // at queue time and must still match.
            public Dictionary<Job, JobDef> defs = new Dictionary<Job, JobDef>();
            public int startTick;
        }

        private static readonly Dictionary<Pawn, PendingOrder> pending = new Dictionary<Pawn, PendingOrder>();

        public static void Queue(Pawn pawn)
        {
            if (pawn?.jobs == null)
            {
                return;
            }

            bool keepWeapon = InterfaceChangesMod.Settings.keepEquippedWeapons;
            string orderId = "IC-" + launchStamp + "-" + (++orderCounter);
            Logger.Info("ORDER " + orderId + " issued: " + pawn.LabelShort + " drop everything (keep equipped weapon: " + (keepWeapon ? "on" : "off") + ")");

            if (pending.TryGetValue(pawn, out PendingOrder previous))
            {
                Logger.Info("ORDER " + previous.id + " ended (replaced by new order " + orderId + "): " + pawn.LabelShort + " drop everything");
                pending.Remove(pawn);
            }

            var order = new PendingOrder { id = orderId, startTick = Find.TickManager.TicksGame };
            bool firstJob = true;
            bool startedLogged = false;
            int apparelQueued = 0, apparelLocked = 0, apparelWarcasket = 0;
            int weaponQueued = 0, weaponKept = 0;
            int inventoryQueued = 0;

            if (pawn.apparel != null)
            {
                // Snapshot - WornApparel is the tracker's live list.
                foreach (Apparel apparel in new List<Apparel>(pawn.apparel.WornApparel))
                {
                    if (pawn.apparel.IsLocked(apparel))
                    {
                        apparelLocked++;
                        NameAndSkip(pawn, apparel, "locked");
                        continue;
                    }

                    if (WarcasketCompat.IsWarcasket(apparel))
                    {
                        apparelWarcasket++;
                        NameAndSkip(pawn, apparel, "a warcasket");
                        continue;
                    }

                    string label = apparel.LabelCap;
                    Job job = JobMaker.MakeJob(JobDefOf.RemoveApparel, apparel);
                    if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: !firstJob))
                    {
                        firstJob = false;
                        order.jobs.Add(job);
                        order.defs[job] = job.def;
                        apparelQueued++;
                        LogStarted(ref startedLogged, orderId, pawn, job);
                        Logger.Info(pawn.LabelShort + ": drop everything - " + label + " queued to be taken off");
                    }
                    else
                    {
                        Logger.Info(pawn.LabelShort + ": drop everything - " + label + " NOT queued, the game refused the remove-apparel order");
                    }
                }
            }

            if (pawn.equipment?.Primary != null)
            {
                ThingWithComps weapon = pawn.equipment.Primary;
                string label = weapon.LabelCap;
                if (keepWeapon)
                {
                    weaponKept++;
                    Logger.Info(pawn.LabelShort + ": drop everything - " + label + " kept (equipped weapon)");
                }
                else
                {
                    Job job = JobMaker.MakeJob(JobDefOf.DropEquipment, weapon);
                    if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: !firstJob))
                    {
                        firstJob = false;
                        order.jobs.Add(job);
                        order.defs[job] = job.def;
                        weaponQueued++;
                        LogStarted(ref startedLogged, orderId, pawn, job);
                        Logger.Info(pawn.LabelShort + ": drop everything - " + label + " queued to be dropped");
                    }
                    else
                    {
                        Logger.Info(pawn.LabelShort + ": drop everything - " + label + " NOT queued, the game refused the drop-equipment order");
                    }
                }
            }

            if (pawn.inventory != null)
            {
                // Inventory weapons are dropped like anything else; only the
                // equipped weapon is covered by the setting.
                foreach (Thing thing in new List<Thing>(pawn.inventory.innerContainer))
                {
                    Job job = JobMaker.MakeJob(IcJobDefOf.IC_DropInventoryStack, thing);
                    if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: !firstJob))
                    {
                        firstJob = false;
                        order.jobs.Add(job);
                        order.defs[job] = job.def;
                        inventoryQueued++;
                        LogStarted(ref startedLogged, orderId, pawn, job);
                    }
                }
            }

            int totalQueued = apparelQueued + weaponQueued + inventoryQueued;
            string outcome = totalQueued == 0
                ? "nothing to drop"
                : "apparel " + apparelQueued + " queued (" + apparelLocked + " locked, " + apparelWarcasket + " warcasket skipped), "
                    + "weapon " + weaponQueued + " queued, " + weaponKept + " kept, inventory " + inventoryQueued + " queued";
            Logger.Info(pawn.LabelShort + ": drop everything pressed - " + outcome);

            if (totalQueued == 0)
            {
                Logger.Info("ORDER " + orderId + " ended (failed to start: nothing to drop or every order refused): " + pawn.LabelShort + " drop everything");
            }
            else
            {
                pending[pawn] = order;
            }
        }

        private static void LogStarted(ref bool startedLogged, string orderId, Pawn pawn, Job job)
        {
            if (!startedLogged)
            {
                startedLogged = true;
                Logger.Info("ORDER " + orderId + " started: " + pawn.LabelShort + " " + job.def.defName + " " + job.targetA);
            }
        }

        // Called every 60 ticks. The vanilla drop jobs have no end hook short
        // of a patch, so an order ends when none of the jobs it queued is the
        // current job or still in the queue.
        public static void PollOrders()
        {
            if (pending.Count == 0)
            {
                return;
            }

            int now = Find.TickManager.TicksGame;
            var done = new List<Pawn>();
            foreach (KeyValuePair<Pawn, PendingOrder> pair in pending)
            {
                Pawn pawn = pair.Key;
                PendingOrder order = pair.Value;
                string reason = null;
                if (pawn == null || pawn.Destroyed || pawn.Dead)
                {
                    reason = "pawn dead or gone";
                }
                else if (pawn.Downed)
                {
                    reason = "pawn downed";
                }
                else
                {
                    order.jobs.RemoveAll(j => !StillQueued(pawn, j, order.defs[j]));
                    if (order.jobs.Count == 0)
                    {
                        reason = "nothing left that can be dropped - every queued drop job has run or been replaced";
                    }
                    else if (now - order.startTick > GiveUpTicks)
                    {
                        reason = "gave up after " + (now - order.startTick) + " ticks with " + order.jobs.Count + " drop job(s) still queued";
                    }
                }

                if (reason != null)
                {
                    Logger.Info("ORDER " + order.id + " ended (" + reason + "): " + pawn?.LabelShort + " drop everything");
                    done.Add(pawn);
                }
            }

            foreach (Pawn pawn in done)
            {
                pending.Remove(pawn);
            }
        }

        private static bool StillQueued(Pawn pawn, Job job, JobDef def)
        {
            if (pawn.jobs == null || job.def != def)
            {
                return false;
            }

            if (pawn.jobs.curJob == job)
            {
                return true;
            }

            // JobQueue.Contains is the game's own identity test and skips null entries.
            return pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.Contains(job);
        }

        // Called when a game is loaded or started: pawns held from the
        // previous game are stale and would never end.
        public static void EndAllForLoad()
        {
            foreach (KeyValuePair<Pawn, PendingOrder> pair in pending)
            {
                Logger.Info("ORDER " + pair.Value.id + " ended (game loaded): " + pair.Key?.LabelShort + " drop everything");
            }

            pending.Clear();
        }

        private static void NameAndSkip(Pawn pawn, Apparel apparel, string reason)
        {
            Logger.Tell(pawn.LabelShort + ": leaving " + apparel.LabelCap + " - " + reason + ".", pawn);
        }
    }
}
