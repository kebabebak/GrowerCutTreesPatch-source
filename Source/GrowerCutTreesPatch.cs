using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using WorkTab;

/*
 * GrowerCutTreesPatch
 *
 * Problem:
 * In HSK, sowing in growing zones embeds CutPlant jobs and SeedsPlease auto-designates trees for
 * the Gardener (PlantCutting) work type. Clearing cannot be prioritized separately under Farmer.
 *
 * Solution:
 * 1. GrowerCutPlants WorkGiverDef under Growing with its own Work Tab sub-priority.
 * 2. Suppress embedded CutPlant from sow work givers (JobOnCell + HasJobOnCell).
 * 3. Optionally patch SeedsPlease auto-designation when that mod is present.
 * 4. Route growing-zone CutPlant designations away from Gardener; respect Work Tab priorities.
 * 5. GrowerCutPlants accepts blighted / CutPlant-designated zone crops (mandatory cuts).
 */
namespace HSK.GrowerCutTreesPatch
{
    public static class ModCompatibility
    {
        private const string SeedsPleasePackageId = "notfood.SeedsPlease";
        private const string WorkTabPackageId = "Fluffy.WorkTab";

        public static bool IsSeedsPleaseLoaded()
        {
            return IsPackageActive(SeedsPleasePackageId);
        }

        public static bool IsWorkTabLoaded()
        {
            return IsPackageActive(WorkTabPackageId);
        }

        private static bool IsPackageActive(string packageId)
        {
            if (ModsConfig.IsActive(packageId))
            {
                return true;
            }

            // PackageId strings are normalized; keep a defensive fallback for mod lists.
            return LoadedModManager.RunningModsListForReading.Exists(
                mod => string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public class GrowerCutTreesPatchMod : Mod
    {
        private const string HarmonyId = "kebabebak.grower.cut.trees.patch";
        private readonly GrowerCutTreesPatchSettings settings;

        public static WorkGiverDef GrowerCutPlantsDef;

        public GrowerCutTreesPatchMod(ModContentPack content)
            : base(content)
        {
            settings = GetSettings<GrowerCutTreesPatchSettings>();
            LongEventHandler.ExecuteWhenFinished(InitializeAfterDefsLoaded);
        }

        private static void InitializeAfterDefsLoaded()
        {
            try
            {
                GrowerCutPlantsDef = DefDatabase<WorkGiverDef>.GetNamedSilentFail("GrowerCutPlants");
                WorkTabPriorityHelper.CacheGrowingWorkGivers();
                // Bare PatchAll() resolves the caller assembly via stack trace; inlining or callbacks can skip patches silently.
                new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());

                int sowWorkGivers = DefDatabase<WorkGiverDef>.AllDefsListForReading
                    .Count(SowWorkCutSuppression.IsSowWorkGiver);
                Log.Message(
                    $"[GrowerCutTreesPatch] Loaded (verbose logging {(GrowerCutTreesPatchSettings.EnableLogging ? "ON" : "OFF")}, " +
                    $"WorkTab={(ModCompatibility.IsWorkTabLoaded() ? "ON" : "OFF")}, " +
                    $"SeedsPlease={(ModCompatibility.IsSeedsPleaseLoaded() ? "ON" : "OFF")}, " +
                    $"GrowerCutPlants priorityInType={GrowerCutPlantsDef?.priorityInType.ToString() ?? "missing"}). " +
                    $"{sowWorkGivers} farmer sow work giver def(s) route cuts through GrowerCutPlants. " +
                    "Enable logging in mod settings for growing-zone cut/sow traces.");
            }
            catch (Exception ex)
            {
                Log.Error("[GrowerCutTreesPatch] Failed to apply patches: " + ex);
            }
        }

        public override string SettingsCategory()
        {
            return "HSK Grower Cut Trees Patch";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            settings.DrawSettings(inRect);
        }
    }

    public static class WorkTabPriorityHelper
    {
        private const int DeferCutCacheTicks = 30;
        private const int DeferCutCacheCleanupIntervalTicks = 250;

        private static List<WorkGiverDef> growingWorkGivers;
        private static readonly Dictionary<int, DeferCutCacheEntry> deferCutCache = new Dictionary<int, DeferCutCacheEntry>();
        private static int lastDeferCutCacheCleanupTick = -1;

        private struct DeferCutCacheEntry
        {
            public int ValidUntilTick;
            public int Hour;
            public bool Defer;
        }

        public static void CacheGrowingWorkGivers()
        {
            growingWorkGivers = DefDatabase<WorkGiverDef>.AllDefsListForReading
                .Where(wg => wg != null && wg.workType == WorkTypeDefOf.Growing)
                .OrderByDescending(wg => wg.priorityInType)
                .ToList();
        }

        public static int GetWorkGiverPriority(Pawn pawn, WorkGiverDef workGiver)
        {
            if (pawn == null || workGiver == null || pawn.workSettings == null || !pawn.workSettings.EverWork)
            {
                return 0;
            }

            if (!ModCompatibility.IsWorkTabLoaded())
            {
                return pawn.workSettings.GetPriority(workGiver.workType) > 0 ? 3 : 0;
            }

            try
            {
                int hour = GetCurrentPriorityHour(pawn);
                return PriorityManager.Get[pawn].GetPriority(workGiver, hour);
            }
            catch (Exception ex)
            {
                PatchLog.Warning(
                    $"[GrowerCutTreesPatch] Work Tab priority read failed for {workGiver.defName} on {DescribePawn(pawn)}: {ex.Message}");
                return pawn.workSettings.GetPriority(workGiver.workType) > 0 ? 3 : 0;
            }
        }

        public static int GetCurrentPriorityHour(Pawn pawn)
        {
            return GenLocalDate.HourOfDay(pawn);
        }

        public static bool IsWorkGiverEnabled(Pawn pawn, WorkGiverDef workGiver)
        {
            return GetWorkGiverPriority(pawn, workGiver) > 0;
        }

        public static bool ShouldDeferCutting(Pawn pawn, WorkGiverDef cutWorkGiver)
        {
            if (pawn == null || cutWorkGiver == null)
            {
                return true;
            }

            if (!IsWorkGiverEnabled(pawn, cutWorkGiver))
            {
                return true;
            }

            int hour = GetCurrentPriorityHour(pawn);
            int tick = Find.TickManager?.TicksGame ?? 0;
            int pawnId = pawn.thingIDNumber;
            MaybeCleanupDeferCutCache(tick);

            if (deferCutCache.TryGetValue(pawnId, out DeferCutCacheEntry cached) &&
                tick <= cached.ValidUntilTick &&
                cached.Hour == hour)
            {
                return cached.Defer;
            }

            bool defer;
            try
            {
                defer = ComputeShouldDeferCutting(pawn, cutWorkGiver);
            }
            catch (Exception ex)
            {
                PatchLog.Warning(
                    $"[GrowerCutTreesPatch] ShouldDeferCutting failed for {DescribePawn(pawn)}: {ex.Message}");
                defer = false;
            }

            deferCutCache[pawnId] = new DeferCutCacheEntry
            {
                ValidUntilTick = tick + DeferCutCacheTicks,
                Hour = hour,
                Defer = defer,
            };
            return defer;
        }

        private static bool ComputeShouldDeferCutting(Pawn pawn, WorkGiverDef cutWorkGiver)
        {
            int cutPriority = GetWorkGiverPriority(pawn, cutWorkGiver);
            EnsureGrowingWorkGiversCached();

            foreach (WorkGiverDef workGiver in growingWorkGivers)
            {
                if (workGiver == null ||
                    workGiver == cutWorkGiver ||
                    workGiver.workType != WorkTypeDefOf.Growing)
                {
                    continue;
                }

                int otherPriority = GetWorkGiverPriority(pawn, workGiver);
                if (otherPriority <= 0 || otherPriority >= cutPriority)
                {
                    continue;
                }

                if (GrowingWorkUtility.HasGrowingWorkPendingCached(pawn, workGiver))
                {
                    PatchLog.Message(
                        $"[GrowerCutTreesPatch] Deferring {cutWorkGiver.defName} for {DescribePawn(pawn)} " +
                        $"because higher-priority Growing work {workGiver.defName} " +
                        $"(prio {otherPriority} < {cutPriority}) is available.");
                    return true;
                }
            }

            PatchLog.Message(
                $"[GrowerCutTreesPatch] ShouldDeferCutting: {DescribePawn(pawn)} may cut " +
                $"(cut prio={cutPriority}, snapshot={DescribeGrowingPriorities(pawn)}).");
            return false;
        }

        private static void MaybeCleanupDeferCutCache(int tick)
        {
            if (lastDeferCutCacheCleanupTick >= 0 &&
                tick - lastDeferCutCacheCleanupTick < DeferCutCacheCleanupIntervalTicks)
            {
                return;
            }

            lastDeferCutCacheCleanupTick = tick;
            List<int> expiredPawnIds = null;
            foreach (KeyValuePair<int, DeferCutCacheEntry> entry in deferCutCache)
            {
                if (tick <= entry.Value.ValidUntilTick)
                {
                    continue;
                }

                expiredPawnIds ??= new List<int>();
                expiredPawnIds.Add(entry.Key);
            }

            if (expiredPawnIds == null)
            {
                return;
            }

            for (int i = 0; i < expiredPawnIds.Count; i++)
            {
                deferCutCache.Remove(expiredPawnIds[i]);
            }
        }

        public static string DescribeGrowingPriorities(Pawn pawn)
        {
            if (pawn == null)
            {
                return "<no pawn>";
            }

            EnsureGrowingWorkGiversCached();
            var builder = new StringBuilder();
            foreach (WorkGiverDef workGiver in growingWorkGivers)
            {
                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }

                builder.Append(workGiver.defName)
                    .Append('=')
                    .Append(GetWorkGiverPriority(pawn, workGiver));
            }

            return builder.ToString();
        }

        private static void EnsureGrowingWorkGiversCached()
        {
            if (growingWorkGivers == null)
            {
                CacheGrowingWorkGivers();
            }
        }

        private static string DescribePawn(Pawn pawn)
        {
            return pawn?.LabelShort ?? "<no pawn>";
        }
    }

    public static class GrowingWorkUtility
    {
        private const int PendingWorkCacheTicks = 30;

        private static readonly Dictionary<GrowingWorkPendingCacheKey, bool> pendingWorkCache =
            new Dictionary<GrowingWorkPendingCacheKey, bool>();
        private static int pendingWorkCacheBucket = -1;

        private struct GrowingWorkPendingCacheKey : IEquatable<GrowingWorkPendingCacheKey>
        {
            public int PawnId;
            public string WorkGiverDefName;

            public bool Equals(GrowingWorkPendingCacheKey other)
            {
                return PawnId == other.PawnId && WorkGiverDefName == other.WorkGiverDefName;
            }

            public override bool Equals(object obj)
            {
                return obj is GrowingWorkPendingCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (PawnId * 397) ^ (WorkGiverDefName?.GetHashCode() ?? 0);
                }
            }
        }

        public static bool HasGrowingWorkPendingCached(Pawn pawn, WorkGiverDef workGiver)
        {
            if (pawn == null || workGiver == null)
            {
                return false;
            }

            int bucket = (Find.TickManager?.TicksGame ?? 0) / PendingWorkCacheTicks;
            if (bucket != pendingWorkCacheBucket)
            {
                pendingWorkCache.Clear();
                pendingWorkCacheBucket = bucket;
            }

            var key = new GrowingWorkPendingCacheKey
            {
                PawnId = pawn.thingIDNumber,
                WorkGiverDefName = workGiver.defName,
            };
            if (pendingWorkCache.TryGetValue(key, out bool cached))
            {
                return cached;
            }

            bool pending = HasGrowingWorkPending(pawn, workGiver);
            pendingWorkCache[key] = pending;
            return pending;
        }

        public static bool HasGrowingWorkPending(Pawn pawn, WorkGiverDef workGiver)
        {
            if (SowWorkCutSuppression.IsSowWorkGiver(workGiver))
            {
                return HasPendingSowWork(pawn, workGiver);
            }

            return HasPendingWork(pawn, workGiver);
        }

        public static bool HasPendingWork(Pawn pawn, WorkGiverDef workGiver)
        {
            if (pawn == null || workGiver?.Worker == null || pawn.Map == null)
            {
                return false;
            }

            try
            {
                WorkGiver worker = workGiver.Worker;
                if (worker.ShouldSkip(pawn, forced: false))
                {
                    return false;
                }

                if (worker is not WorkGiver_Scanner scanner)
                {
                    return false;
                }

                if (workGiver.scanThings)
                {
                    IEnumerable<Thing> things = scanner.PotentialWorkThingsGlobal(pawn);
                    if (things == null)
                    {
                        return false;
                    }

                    foreach (Thing thing in things)
                    {
                        if (thing != null && scanner.HasJobOnThing(pawn, thing, forced: false))
                        {
                            PatchLog.Message(
                                $"[GrowerCutTreesPatch] HasPendingWork: {workGiver.defName} found thing " +
                                $"{thing.LabelShort} for {DescribePawn(pawn)}.");
                            return true;
                        }
                    }

                    return false;
                }

                IEnumerable<IntVec3> cells = scanner.PotentialWorkCellsGlobal(pawn);
                if (cells == null)
                {
                    return false;
                }

                foreach (IntVec3 cell in cells)
                {
                    if (scanner.HasJobOnCell(pawn, cell, forced: false))
                    {
                        PatchLog.Message(
                            $"[GrowerCutTreesPatch] HasPendingWork: {workGiver.defName} found cell " +
                            $"{cell} for {DescribePawn(pawn)}.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                PatchLog.Warning(
                    $"[GrowerCutTreesPatch] HasPendingWork failed for {workGiver.defName} on {DescribePawn(pawn)}: {ex.Message}");
            }

            return false;
        }

        public static bool HasPendingSowWork(Pawn pawn, WorkGiverDef workGiver)
        {
            if (pawn == null || workGiver?.Worker is not WorkGiver_Scanner scanner || pawn.Map == null)
            {
                return false;
            }

            try
            {
                if (scanner.ShouldSkip(pawn, forced: false))
                {
                    return false;
                }

                Map map = pawn.Map;
                Danger maxDanger = pawn.NormalMaxDanger();
                List<Zone> zonesList = map.zoneManager.AllZones;
                for (int i = 0; i < zonesList.Count; i++)
                {
                    if (zonesList[i] is not Zone_Growing growZone ||
                        growZone.cells.Count == 0 ||
                        growZone.ContainsStaticFire)
                    {
                        continue;
                    }

                    if (!pawn.CanReach(growZone.Cells[0], PathEndMode.OnCell, maxDanger))
                    {
                        continue;
                    }

                    for (int j = 0; j < growZone.cells.Count; j++)
                    {
                        IntVec3 cell = growZone.cells[j];
                        // Cells blocked by trees are not actionable sow work; our sow HasJobOnCell
                        // postfix already hides cut-only jobs when cut is routed through GrowerCutPlants.
                        if (GrowerZoneCutUtility.CellNeedsCutBeforeSow(pawn, cell))
                        {
                            continue;
                        }

                        if (scanner.HasJobOnCell(pawn, cell, forced: false))
                        {
                            PatchLog.Message(
                                $"[GrowerCutTreesPatch] HasPendingSowWork: {workGiver.defName} found sow cell " +
                                $"{cell} for {DescribePawn(pawn)}.");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PatchLog.Warning(
                    $"[GrowerCutTreesPatch] HasPendingSowWork failed for {workGiver.defName} on {DescribePawn(pawn)}: {ex.Message}");
            }

            return false;
        }

        public static bool IsGrowingZoneCutPlant(Thing plantThing, Map map, out Zone_Growing zone)
        {
            zone = null;
            if (plantThing?.def.category != ThingCategory.Plant || map == null)
            {
                return false;
            }

            zone = plantThing.Position.GetZone(map) as Zone_Growing;
            return zone != null && zone.allowCut;
        }

        private static string DescribePawn(Pawn pawn)
        {
            return pawn?.LabelShort ?? "<no pawn>";
        }
    }

    public static class SowWorkCutSuppression
    {
        public static bool IsSowWorkGiver(WorkGiverDef workGiver)
        {
            if (workGiver == null ||
                workGiver.workType != WorkTypeDefOf.Growing ||
                workGiver.giverClass == null)
            {
                return false;
            }

            if (workGiver.defName == "GrowerCutPlants")
            {
                return false;
            }

            return workGiver.defName == "GrowerSow" ||
                   typeof(WorkGiver_GrowerSow).IsAssignableFrom(workGiver.giverClass);
        }

        internal static void SuppressJobOnCell(ref Job __result, Pawn pawn, IntVec3 cell, WorkGiver __instance)
        {
            if (__result == null || !IsCutPlantJob(__result))
            {
                return;
            }

            if (!TryGetGrowingZoneCutTarget(__result, pawn, out _))
            {
                return;
            }

            WorkGiverDef cutDef = GrowerCutTreesPatchMod.GrowerCutPlantsDef;
            if (cutDef != null && !WorkTabPriorityHelper.IsWorkGiverEnabled(pawn, cutDef))
            {
                PatchLog.Message(
                    $"[GrowerCutTreesPatch] Blocked growing-zone CutPlant at {cell} for {DescribePawn(pawn)} " +
                    $"via {__instance?.def?.defName ?? "<unknown>"}; GrowerCutPlants is disabled in Work Tab.");
                __result = null;
                return;
            }

            if (__instance?.def == null || !IsSowWorkGiver(__instance.def))
            {
                return;
            }

            PatchLog.Message(
                $"[GrowerCutTreesPatch] Farmer/sow: suppressed embedded CutPlant at {cell} for {DescribePawn(pawn)} " +
                $"via {__instance.def.defName} (target={DescribeTarget(__result.targetA)}); " +
                $"route through GrowerCutPlants only. " +
                $"priorities={WorkTabPriorityHelper.DescribeGrowingPriorities(pawn)}.");
            __result = null;
        }

        internal static void SuppressHasJobOnCell(ref bool __result, Pawn pawn, IntVec3 c, WorkGiver __instance)
        {
            if (!__result ||
                __instance?.def == null ||
                !IsSowWorkGiver(__instance.def))
            {
                return;
            }

            if (!GrowerZoneCutUtility.CellNeedsCutBeforeSow(pawn, c))
            {
                return;
            }

            WorkGiverDef cutDef = GrowerCutTreesPatchMod.GrowerCutPlantsDef;
            if (cutDef != null && !WorkTabPriorityHelper.IsWorkGiverEnabled(pawn, cutDef))
            {
                PatchLog.Message(
                    $"[GrowerCutTreesPatch] Farmer/sow: blocked cut-only HasJobOnCell at {c} for {DescribePawn(pawn)} " +
                    $"via {__instance.def.defName}; GrowerCutPlants is disabled in Work Tab.");
                __result = false;
                return;
            }

            PatchLog.Message(
                $"[GrowerCutTreesPatch] Farmer/sow: suppressed cut-only HasJobOnCell at {c} for {DescribePawn(pawn)} " +
                $"via {__instance.def.defName}; use GrowerCutPlants (Farmer) not sow/gardener. " +
                $"cut enabled={WorkTabPriorityHelper.IsWorkGiverEnabled(pawn, cutDef)}.");
            __result = false;
        }

        public static bool TryGetGrowingZoneCutTarget(Job job, Pawn pawn, out Zone_Growing zone)
        {
            zone = null;
            if (!IsCutPlantJob(job) || pawn?.Map == null || !job.targetA.HasThing)
            {
                return false;
            }

            return GrowingWorkUtility.IsGrowingZoneCutPlant(job.targetA.Thing, pawn.Map, out zone);
        }

        public static bool IsCutPlantJob(Job job)
        {
            return job?.def != null &&
                   (job.def == JobDefOf.CutPlant || job.def.defName == "CutPlant");
        }

        private static string DescribePawn(Pawn pawn)
        {
            return pawn?.LabelShort ?? "<no pawn>";
        }

        private static string DescribeTarget(LocalTargetInfo target)
        {
            if (!target.IsValid)
            {
                return "<invalid>";
            }

            if (target.HasThing)
            {
                Thing thing = target.Thing;
                return $"{thing.LabelShort} at {thing.Position}";
            }

            return target.Cell.ToString();
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.JobOnCell))]
    public static class SowWorkCutSuppressionJobOnCellPatch
    {
        public static void Postfix(ref Job __result, Pawn pawn, IntVec3 cell, WorkGiver __instance)
        {
            SowWorkCutSuppression.SuppressJobOnCell(ref __result, pawn, cell, __instance);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnCell))]
    public static class SowWorkCutSuppressionHasJobOnCellPatch
    {
        public static void Postfix(ref bool __result, Pawn pawn, IntVec3 c, WorkGiver __instance)
        {
            SowWorkCutSuppression.SuppressHasJobOnCell(ref __result, pawn, c, __instance);
        }
    }

    [HarmonyPatch]
    public static class SeedsPleaseSowSitePatch
    {
        private static MethodBase targetMethod;

        private static bool Prepare()
        {
            if (!ModCompatibility.IsSeedsPleaseLoaded())
            {
                return false;
            }

            Type driverType = AccessTools.TypeByName("SeedsPlease.JobDriver_PlantSowWithSeeds");
            if (driverType == null)
            {
                Log.WarningOnce(
                    "[GrowerCutTreesPatch] SeedsPlease is loaded but JobDriver_PlantSowWithSeeds was not found.",
                    0x1f6a8d20);
                return false;
            }

            targetMethod = AccessTools.Method(
                driverType,
                "IsCellOpenForSowingPlantOfType",
                new[] { typeof(IntVec3), typeof(Map), typeof(ThingDef) });

            if (targetMethod != null)
            {
                return true;
            }

            Log.WarningOnce(
                "[GrowerCutTreesPatch] SeedsPlease is loaded but IsCellOpenForSowingPlantOfType was not found.",
                0x2b7c4e91);
            return false;
        }

        private static MethodBase TargetMethod()
        {
            return targetMethod;
        }

        public static bool Prefix(IntVec3 cell, Map map, ThingDef plantDef, ref bool __result)
        {
            foreach (Thing thing in map.thingGrid.ThingsListAt(cell))
            {
                if (thing.def.category != ThingCategory.Plant || !thing.def.BlocksPlanting())
                {
                    continue;
                }

                PatchLog.Message(
                    $"[GrowerCutTreesPatch] Blocked SeedsPlease auto-designation at {cell} for {thing.LabelShort}; " +
                    "growing-zone clearing uses GrowerCutPlants only.");
                __result = false;
                return false;
            }

            return true;
        }
    }

    public static class GardenerGrowingZoneCutPatch
    {
        internal static void SuppressJobOnThing(ref Job __result, Pawn pawn, Thing t, WorkGiver __instance)
        {
            if (!IsGardenerCutWorkGiver(__instance?.def?.defName) || __result == null ||
                !TryBlockGrowingZoneCut(pawn, t, out Zone_Growing zone))
            {
                return;
            }

            if (WorkTabPriorityHelper.IsWorkGiverEnabled(pawn, GrowerCutTreesPatchMod.GrowerCutPlantsDef))
            {
                PatchLog.Message(
                    $"[GrowerCutTreesPatch] Gardener/{__instance.def.defName}: blocked JobOnThing for {t?.LabelShort} in growing zone " +
                    $"{zone.label}; growing-zone trees use Farmer GrowerCutPlants only " +
                    $"(priorities={WorkTabPriorityHelper.DescribeGrowingPriorities(pawn)}).");
            }
            else
            {
                PatchLog.Message(
                    $"[GrowerCutTreesPatch] Gardener/{__instance.def.defName}: blocked JobOnThing for {t?.LabelShort} in growing zone " +
                    $"{zone.label}; GrowerCutPlants is disabled in Work Tab.");
            }

            __result = null;
        }

        internal static void SuppressHasJobOnThing(
            ref bool __result,
            Pawn pawn,
            Thing t,
            WorkGiver_Scanner __instance)
        {
            if (!IsGardenerCutWorkGiver(__instance?.def?.defName) || !__result ||
                !TryBlockGrowingZoneCut(pawn, t, out Zone_Growing zone))
            {
                return;
            }

            PatchLog.Message(
                $"[GrowerCutTreesPatch] Gardener/{__instance.def.defName}: blocked HasJobOnThing for {t?.LabelShort} in growing zone " +
                $"{zone.label}.");
            __result = false;
        }

        private static bool IsGardenerCutWorkGiver(string defName)
        {
            return defName == "PlantsCut" || defName == "FellTrees";
        }

        private static bool TryBlockGrowingZoneCut(Pawn pawn, Thing t, out Zone_Growing zone)
        {
            zone = null;
            return GrowingWorkUtility.IsGrowingZoneCutPlant(t, pawn?.Map, out zone);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_PlantsCut), nameof(WorkGiver_PlantsCut.JobOnThing))]
    public static class GardenerGrowingZoneCutJobOnThingPatch
    {
        public static void Postfix(ref Job __result, Pawn pawn, Thing t, WorkGiver __instance)
        {
            GardenerGrowingZoneCutPatch.SuppressJobOnThing(ref __result, pawn, t, __instance);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnThing))]
    public static class GardenerGrowingZoneCutHasJobOnThingPatch
    {
        public static void Postfix(ref bool __result, Pawn pawn, Thing t, WorkGiver_Scanner __instance)
        {
            GardenerGrowingZoneCutPatch.SuppressHasJobOnThing(ref __result, pawn, t, __instance);
        }
    }

    public class WorkGiver_GrowerCutPlants : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (base.ShouldSkip(pawn, forced))
            {
                return true;
            }

            if (!WorkTabPriorityHelper.IsWorkGiverEnabled(pawn, def))
            {
                PatchLog.Message(
                    $"[GrowerCutTreesPatch] Skipping {def.defName} for {DescribePawn(pawn)} because Work Tab " +
                    $"priority is disabled (prio={WorkTabPriorityHelper.GetWorkGiverPriority(pawn, def)}).");
                return true;
            }

            if (!forced && WorkTabPriorityHelper.ShouldDeferCutting(pawn, def))
            {
                return true;
            }

            return false;
        }

        public override IEnumerable<IntVec3> PotentialWorkCellsGlobal(Pawn pawn)
        {
            Danger maxDanger = pawn.NormalMaxDanger();
            List<Zone> zonesList = pawn.Map.zoneManager.AllZones;
            for (int i = 0; i < zonesList.Count; i++)
            {
                if (zonesList[i] is not Zone_Growing growZone)
                {
                    continue;
                }

                if (growZone.cells.Count == 0 || !growZone.allowCut || growZone.ContainsStaticFire)
                {
                    continue;
                }

                if (!pawn.CanReach(growZone.Cells[0], PathEndMode.OnCell, maxDanger))
                {
                    continue;
                }

                for (int j = 0; j < growZone.cells.Count; j++)
                {
                    yield return growZone.cells[j];
                }
            }
        }

        public override bool HasJobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            if (ShouldSkip(pawn, forced))
            {
                return false;
            }

            return GrowerZoneCutUtility.TryFindCutTarget(pawn, c, forced, out _);
        }

        public override Job JobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            if (ShouldSkip(pawn, forced))
            {
                return null;
            }

            if (!GrowerZoneCutUtility.TryFindCutTarget(pawn, c, forced, out Plant plant))
            {
                return null;
            }

            PatchLog.Message(
                $"[GrowerCutTreesPatch] Farmer/GrowerCutPlants: {DescribePawn(pawn)} will cut {DescribePlant(plant)} " +
                $"for growing zone at {c} (WorkTab prio={WorkTabPriorityHelper.GetWorkGiverPriority(pawn, def)}).");
            return JobMaker.MakeJob(JobDefOf.CutPlant, plant);
        }

        private static string DescribePawn(Pawn pawn)
        {
            return pawn?.LabelShort ?? "<no pawn>";
        }

        private static string DescribePlant(Plant plant)
        {
            if (plant == null)
            {
                return "<null>";
            }

            return $"{plant.LabelShort} ({plant.def?.defName}) at {plant.Position}";
        }
    }

    public static class GrowerZoneCutUtility
    {
        public static bool CellNeedsCutBeforeSow(Pawn pawn, IntVec3 c)
        {
            return TryFindCutTarget(pawn, c, forced: false, out _);
        }

        /// <summary>
        /// Blighted crops or explicit CutPlant designations (incl. Plant CutAllBlight gizmo)
        /// must be cleared even when the plant matches the growing zone crop.
        /// </summary>
        public static bool IsMandatoryCut(Plant plant, Map map)
        {
            if (plant == null || plant.Destroyed || map == null)
            {
                return false;
            }

            if (plant.Blighted)
            {
                return true;
            }

            return map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null;
        }

        public static bool TryFindCutTarget(Pawn pawn, IntVec3 c, bool forced, out Plant result)
        {
            result = null;
            Map map = pawn.Map;
            if (c.IsForbidden(pawn))
            {
                return false;
            }

            Zone_Growing zone = c.GetZone(map) as Zone_Growing;
            if (zone == null || !zone.allowCut)
            {
                return false;
            }

            Plant mandatoryPlantOnCell = c.GetPlant(map);
            if (mandatoryPlantOnCell != null &&
                IsMandatoryCut(mandatoryPlantOnCell, map) &&
                TryAcceptCutTarget(
                    pawn,
                    mandatoryPlantOnCell,
                    zone,
                    c,
                    map,
                    forced,
                    out result,
                    allowZoneCrop: true))
            {
                PatchLog.Message(
                    $"[GrowerCutTreesPatch] Mandatory cut at {c}: {DescribePlant(result)} " +
                    $"(blighted={result.Blighted}, designated CutPlant).");
                return true;
            }

            if (!PlantUtility.GrowthSeasonNow(c, map, forSowing: true))
            {
                return false;
            }

            ThingDef wantedPlantDef = WorkGiver_Grower.CalculateWantedPlantDef(c, map);
            if (wantedPlantDef == null)
            {
                return false;
            }

            List<Thing> thingList = c.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                if (thingList[i].def == wantedPlantDef)
                {
                    if (thingList[i] is Plant zoneCrop &&
                        IsMandatoryCut(zoneCrop, map) &&
                        TryAcceptCutTarget(
                            pawn,
                            zoneCrop,
                            zone,
                            c,
                            map,
                            forced,
                            out result,
                            allowZoneCrop: true))
                    {
                        PatchLog.Message(
                            $"[GrowerCutTreesPatch] Mandatory zone-crop cut at {c}: {DescribePlant(result)}.");
                        return true;
                    }

                    return false;
                }
            }

            Plant plantOnCell = c.GetPlant(map);
            if (plantOnCell != null && plantOnCell.def.plant.blockAdjacentSow)
            {
                if (TryAcceptCutTarget(pawn, plantOnCell, zone, c, map, forced, out result))
                {
                    return true;
                }
            }

            Thing adjacentBlocker = PlantUtility.AdjacentSowBlocker(wantedPlantDef, c, map);
            if (adjacentBlocker is Plant adjacentPlant &&
                TryAcceptCutTarget(pawn, adjacentPlant, zone, c, map, forced, out result))
            {
                return true;
            }

            for (int j = 0; j < thingList.Count; j++)
            {
                Thing thing = thingList[j];
                if (thing.def.category != ThingCategory.Plant || !thing.def.BlocksPlanting())
                {
                    continue;
                }

                if (TryAcceptCutTarget(pawn, thing, zone, c, map, forced, out result))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryAcceptCutTarget(
            Pawn pawn,
            Thing plantThing,
            Zone_Growing sowZone,
            IntVec3 sowCell,
            Map map,
            bool forced,
            out Plant result,
            bool allowZoneCrop = false)
        {
            result = null;
            if (plantThing == null || plantThing.Destroyed || plantThing.IsForbidden(pawn))
            {
                return false;
            }

            if (!pawn.CanReserve(plantThing, 1, -1, null, forced))
            {
                return false;
            }

            if (!PlantUtility.PawnWillingToCutPlant_Job(plantThing, pawn))
            {
                return false;
            }

            if (PlantUtility.TreeMarkedForExtraction(plantThing))
            {
                return false;
            }

            Zone_Growing plantZone = plantThing.Position.GetZone(map) as Zone_Growing;
            if (plantZone != null && !plantZone.allowCut)
            {
                return false;
            }

            if (!allowZoneCrop)
            {
                if (plantZone != null &&
                    plantZone != sowZone &&
                    plantZone.GetPlantDefToGrow() == plantThing.def)
                {
                    return false;
                }

                IPlantToGrowSettable plantSettable = plantThing.Position.GetPlantToGrowSettable(map);
                if (plantSettable != null &&
                    plantSettable.GetPlantDefToGrow() == plantThing.def &&
                    (plantZone == null || plantZone == sowZone))
                {
                    return false;
                }
            }

            result = plantThing as Plant;
            if (result == null)
            {
                return false;
            }

            return true;
        }

        private static string DescribePlant(Plant plant)
        {
            if (plant == null)
            {
                return "<null>";
            }

            return $"{plant.LabelShort} ({plant.def?.defName}) at {plant.Position}";
        }
    }

    public class GrowerCutTreesPatchSettings : ModSettings
    {
        public static bool EnableLogging;

        public void DrawSettings(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("GrowerCutTreesPatch.EnableLogging".Translate(), ref EnableLogging);
            listing.End();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref EnableLogging, "EnableLogging", defaultValue: false);
        }
    }

    public static class PatchLog
    {
        public static void Message(string text)
        {
            if (GrowerCutTreesPatchSettings.EnableLogging)
            {
                Log.Message(text);
            }
        }

        public static void Warning(string text)
        {
            if (GrowerCutTreesPatchSettings.EnableLogging)
            {
                Log.Warning(text);
            }
        }
    }
}
