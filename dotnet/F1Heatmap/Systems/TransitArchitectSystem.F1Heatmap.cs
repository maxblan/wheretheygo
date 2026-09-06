using System.Collections.Generic;
using System.Globalization;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // The heat map itself: the cached collections the access pass reads, the worker
    // task that runs SuitabilityWalkAccess, the combine and normalisation of its terms,
    // and the scoring queries every later stage asks of the resulting field.
    public sealed partial class TransitArchitectSystem
    {


        // One intensity array per layer, plus the interleaved RGBA buffer uploaded
        // to the terrain texture.
        private readonly byte[][] m_LayerIntensities = new byte[SuitabilityLayers.Count][];

        private int2 m_IntensityGrid;

        private float m_LastComputeFinish;

        private float m_LastCollectionRebuild;

        private float m_LastMaskRefresh;

        // The access pass runs on a worker thread as plain managed code — it is the
        // Unity-free core in SuitabilityWalkAccess, so a Task is the right vehicle and
        // the same code runs offline on an exported city. Completion is polled from
        // OnUpdate exactly as the Burst job it replaced was.
        private bool m_JobPending;

        private System.Threading.Tasks.Task? m_PendingAccess;

        private AccessBox? m_PendingBox;

        private WalkAccessInputs? m_PendingInputs;

        private int m_PendingHomeCount;

        private WalkAccessOutput? m_Access;

        private WalkAccessInputs? m_AccessInputs;

        private int2 m_PendingGrid;

        private float2 m_PendingWorldMin;

        private int m_PendingStopCount;

        private int m_PendingOrphanCount;

        private int m_PendingJobSiteCount;

        private int m_PendingZonedCount;

        // The worker writes its output here; the main thread reads it once the task
        // reports completion, which is the memory barrier that makes the hand-off safe.
        private sealed class AccessBox
        {
            public WalkAccessOutput? m_Output;
        }

        // The PLAYABLE-AREA grid as of the last compute. Deliberately not the grid the
        // compute ran on — that one comes from the population map's own extent — and
        // named for what it holds, because "the grid at compute" invited the reader to
        // assume the two were the same. It exists only to notice the playable area
        // changing, which is a reason to recompute.
        private int2 m_PlayableGridAtCompute;

        // Cached raw terms from the last compute, plus everything derived from them.
        private SuitabilityCell[]? m_RawTerms;

        private float[]? m_Scores;

        private float[]? m_ScoreScratch;

        private float[]? m_TermScratch;

        private float2 m_ScoreWorldMin;

        // Term caps from the last combine. Cached because the ridership sampler
        // needs them per stop, and recomputing a percentile over the whole grid for
        // every stop on every sample would cost tens of millions of operations.
        private float m_DemandCap;

        private float m_JobsCap;

        private float m_FutureCap;

        // Terrain-derived masks, rebuilt rarely.
        private byte[]? m_Buildable;

        // Managed because only the main thread reads it (mask build, shoreline
        // scoring, the lattices); the access pass takes a copy of Buildable.
        private byte[]? m_Land;


        private int2 m_MaskGrid;

        private bool m_MaskDirty = true;

        private ModePreset m_MaskMode;

        private int m_MaskSlope;

        // Every served stop with its transport type. The access pass accumulates each
        // type per node, so the map (one mode) and stop placement (the suggested
        // line's mode) read the same numbers.
        private readonly List<float2> m_AllStopPositions = new List<float2>();

        private readonly List<int> m_AllStopTypes = new List<int>();

        // What the score field in m_Scores was combined FOR. A query for any other mode
        // has to undo this mode's stop-derived terms and put its own in their place.
        private ModePreset m_ScoredMode;

        private float m_ScoredInvSelf;

        private CombineWeights m_Scored;

        // The pedestrian network, rebuilt with the road cache.
        private WalkGraph? m_WalkGraph;

        private int m_EdgesWithoutPavement;

        private readonly List<float2> m_HomePositions = new List<float2>();

        private readonly List<float> m_HomeResidents = new List<float>();

        private EntityQuery m_HouseholdQuery;

        private readonly List<float2> m_JobPositions = new List<float2>();

        private readonly List<float> m_JobWorkers = new List<float>();

        private readonly List<float2> m_FutureHomePositions = new List<float2>();

        private readonly List<float> m_FutureHomeWeights = new List<float>();

        private readonly List<float2> m_FutureJobPositions = new List<float2>();

        private readonly List<float> m_FutureJobWeights = new List<float>();

        private bool m_RoadCacheDirty = true;

        private bool m_WorkplaceCacheDirty = true;

        private bool m_ZoneCacheDirty = true;

        private int m_LastOrphanCount;

        private int m_HouseholdsWithoutHome;

        private bool StartCompute()
        {
            var settings = Mod.Settings;
            if (m_JobPending || m_PopulationSystem is null || m_TerrainSystem is null || settings is null)
            {
                return false;
            }

            Dependency.Complete();

            // The population map is still what fixes the map's extent and grid, so the
            // score grid stays aligned with everything else that samples the world.
            CellMapData<PopulationCell> popData = m_PopulationSystem.GetData(readOnly: true, out JobHandle popDeps);
            if (popData.m_CellSize.x <= 0f || popData.m_CellSize.y <= 0f ||
                popData.m_TextureSize.x <= 0 || popData.m_TextureSize.y <= 0)
            {
                return false;
            }

            float2 mapSize = popData.m_CellSize * new float2(popData.m_TextureSize.x, popData.m_TextureSize.y);
            if (mapSize.x <= 0f || mapSize.y <= 0f)
            {
                return false;
            }

            popDeps.Complete();
            float2 worldMin = -mapSize * 0.5f;
            int2 gridSize = SuitabilityInputs.GridDims(mapSize, Assumptions.TileSize);

            EnsureMasks(settings, gridSize, worldMin);
            EnsureCollectionsCurrent(settings);
            if (m_WalkGraph is null)
            {
                return false;
            }

            byte[]? mask = m_Buildable;
            if (mask is null)
            {
                return false;
            }

            WalkAccessInputs inputs = BuildAccessInputs(m_WalkGraph);
            // The task must not see a mask rebuilt under it, so it gets its own copy.
            var buildable = (byte[])mask.Clone();
            int cls = SuitabilityWalkAccess.ClassOf(inputs.CatchmentMs, Assumptions.CatchmentMs(settings.Mode));
            var selfType = (int)SuitabilityInputs.TransportTypeOf(settings.Mode);
            float[] typeWeight = TypeWeights();
            int width = gridSize.x;
            int height = gridSize.y;
            float minX = worldMin.x;
            float minZ = worldMin.y;

            // Everything the task touches is captured here as plain arrays; nothing on
            // the worker reads ECS state.
            var box = new AccessBox();
            m_PendingBox = box;
            m_PendingAccess = System.Threading.Tasks.Task.Run(
                () => box.m_Output = SuitabilityWalkAccess.Run(inputs, width, height, minX, minZ, Assumptions.TileSize, buildable, cls, selfType, typeWeight),
                System.Threading.CancellationToken.None);
            m_PendingInputs = inputs;
            CaptureExportInputs(inputs, gridSize, worldMin, buildable, settings.Mode, cls, selfType, typeWeight);

            m_PendingGrid = gridSize;
            m_PendingWorldMin = worldMin;
            m_PendingStopCount = m_AllStopPositions.Count;
            m_PendingOrphanCount = m_LastOrphanCount;
            m_PendingJobSiteCount = m_JobPositions.Count;
            m_PendingHomeCount = m_HomePositions.Count;
            m_PendingZonedCount = m_FutureHomePositions.Count + m_FutureJobPositions.Count;
            m_JobPending = true;
            m_PlayableGridAtCompute = GetGridSize();
            return true;
        }

        // Plain-array copies of the cached collections, in their cached order.
        private WalkAccessInputs BuildAccessInputs(WalkGraph graph)
        {
            var inputs = new WalkAccessInputs
            {
                Graph = graph,
                Homes = SourcesOf(m_HomePositions, m_HomeResidents),
                Jobs = SourcesOf(m_JobPositions, m_JobWorkers),
                Future = SourcesOf(m_FutureHomePositions, m_FutureHomeWeights, m_FutureJobPositions, m_FutureJobWeights),
                StopCount = m_AllStopPositions.Count,
                StopX = new float[m_AllStopPositions.Count],
                StopZ = new float[m_AllStopPositions.Count],
                StopType = m_AllStopTypes.ToArray(),
                TypeCount = (int)TransportType.Count,
                AccessMs = Assumptions.AccessWalkMs,
                TransferMs = Assumptions.TransferWalkMs,
                CatchmentMs = Assumptions.CatchmentClassesMs,
            };
            for (int i = 0; i < m_AllStopPositions.Count; i++)
            {
                inputs.StopX[i] = m_AllStopPositions[i].x;
                inputs.StopZ[i] = m_AllStopPositions[i].y;
            }

            return inputs;
        }

        private static WalkSources SourcesOf(List<float2> positions, List<float> weights)
        {
            return SourcesOf(positions, weights, new List<float2>(), new List<float>());
        }

        // Two lists concatenated in order — zoned homes then zoned workplaces for the
        // future term.
        private static WalkSources SourcesOf(List<float2> first, List<float> firstWeights, List<float2> second, List<float> secondWeights)
        {
            int count = first.Count + second.Count;
            var sources = new WalkSources { Count = count, X = new float[count], Z = new float[count], Weight = new float[count] };
            for (int i = 0; i < first.Count; i++)
            {
                sources.X[i] = first[i].x;
                sources.Z[i] = first[i].y;
                sources.Weight[i] = firstWeights[i];
            }

            for (int i = 0; i < second.Count; i++)
            {
                sources.X[first.Count + i] = second[i].x;
                sources.Z[first.Count + i] = second[i].y;
                sources.Weight[first.Count + i] = secondWeights[i];
            }

            return sources;
        }

        private void FinishComputeIfReady()
        {
            System.Threading.Tasks.Task? pending = m_PendingAccess;
            WalkAccessOutput? output = m_PendingBox?.m_Output;
            if (!m_JobPending || pending is null || !pending.IsCompleted)
            {
                return;
            }

            m_JobPending = false;
            m_PendingAccess = null;
            m_PendingBox = null;
            m_LastComputeFinish = UnityEngine.Time.realtimeSinceStartup;
            if (pending.IsFaulted || pending.IsCanceled || output is null)
            {
                // A fault here is a defect in the pure core, not a game condition, so
                // it is logged in full rather than swallowed; the previous map stays.
                DeferredLog.Error($"Access pass failed: {pending.Exception}");
                m_PendingInputs = null;
                return;
            }
            m_Access = output;
            m_AccessInputs = m_PendingInputs;
            m_PendingInputs = null;
            m_RawTerms = output.Terms;

            m_IntensityGrid = m_PendingGrid;
            m_ScoreWorldMin = m_PendingWorldMin;
            RecombineAndNormalize();
            // After the combine, so the exported score field is the one these terms
            // produce rather than the previous compute's.
            WriteExportIfCaptured();

            DeferredLog.Info(
                $"Overlay computed: grid {(m_PendingGrid.x).ToString(CultureInfo.InvariantCulture)}x{(m_PendingGrid.y).ToString(CultureInfo.InvariantCulture)}, " +
                $"walk network {(output.Result.Demand.Length > 0 ? m_AccessInputs?.Graph.NodeCount ?? 0 : 0).ToString(CultureInfo.InvariantCulture)} nodes " +
                $"({(m_EdgesWithoutPavement).ToString(CultureInfo.InvariantCulture)} edges without a pedestrian lane skipped), " +
                $"{(output.TilesOnNetwork).ToString(CultureInfo.InvariantCulture)} tiles within {(Assumptions.AccessWalkMs / 1000).ToString(CultureInfo.InvariantCulture)} s of a node, " +
                $"homes={(m_PendingHomeCount).ToString(CultureInfo.InvariantCulture)} ({(m_HouseholdsWithoutHome).ToString(CultureInfo.InvariantCulture)} households without a home skipped), " +
                $"jobSites={(m_PendingJobSiteCount).ToString(CultureInfo.InvariantCulture)}, zonedCells={(m_PendingZonedCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"servedStops={(m_PendingStopCount).ToString(CultureInfo.InvariantCulture)} (orphans ignored={(m_PendingOrphanCount).ToString(CultureInfo.InvariantCulture)}), " +
                $"sourcesOffNetwork={(output.Result.SourcesOffNetwork).ToString(CultureInfo.InvariantCulture)}, " +
                $"settled={(output.Result.Relaxations).ToString(CultureInfo.InvariantCulture)}, sites={(m_SiteCount).ToString(CultureInfo.InvariantCulture)}");
        }

        private void DiscardPendingCompute()
        {
            if (!m_JobPending)
            {
                return;
            }

            // A running task finishes on its own and is ignored, since nothing reads a
            // result whose pending fields were cleared.
            m_PendingAccess = null;
            m_PendingBox = null;
            m_PendingInputs = null;
            m_JobPending = false;
        }

        private void EnsureMasks(Setting settings, int2 gridSize, float2 worldMin)
        {
            int cells = gridSize.x * gridSize.y;
            bool resized = !m_MaskGrid.Equals(gridSize) || m_Buildable is null || m_Buildable.Length != cells;
            if (!resized && !m_MaskDirty && m_MaskMode == settings.Mode && m_MaskSlope == settings.MaxSlope)
            {
                return;
            }

            if (resized || m_Land is null)
            {
                m_Buildable = new byte[cells];
                m_Land = new byte[cells];
                m_MaskGrid = gridSize;
            }

            byte[] buildable = m_Buildable ??= new byte[cells];
            byte[] land = m_Land;
            SuitabilityMasks.Build(
                m_TerrainSystem,
                m_WaterSystem,
                settings.Mode,
                settings.MaxSlope,
                gridSize,
                worldMin,
                Assumptions.TileSize,
                buildable,
                land);

            m_MaskDirty = false;
            m_MaskMode = settings.Mode;
            m_MaskSlope = settings.MaxSlope;
            m_LastMaskRefresh = UnityEngine.Time.realtimeSinceStartup;

            int buildableCount = 0;
            for (int i = 0; i < cells; i++)
            {
                if (buildable[i] != 0)
                {
                    buildableCount++;
                }
            }

            DeferredLog.Info($"Terrain mask rebuilt: {(buildableCount).ToString(CultureInfo.InvariantCulture)}/{(cells).ToString(CultureInfo.InvariantCulture)} tiles buildable.");
        }

        // Rebuilds the cached collections only when change detection says they moved.
        // All of these walk large parts of the entity world on the main thread, and
        // the periodic overlay refresh would otherwise pay for them every ten seconds
        // for as long as the infoview stays open.
        private void EnsureCollectionsCurrent(Setting settings)
        {
            bool rebuilt = false;

            if (m_RoadCacheDirty || m_WalkGraph is null)
            {
                SuitabilityInputs.CollectWalkNetwork(
                    EntityManager, m_NodeQuery, m_AllEdgeQuery,
                    out float[] nodeX, out float[] nodeZ, out int[] edgeA, out int[] edgeB, out float[] edgeMetres,
                    out bool[] siteable, out m_EdgesWithoutPavement);
                m_WalkGraph = WalkGraph.Build(nodeX, nodeZ, edgeA, edgeB, edgeMetres, edgeA.Length, siteable);
                int offGround = 0;
                for (int n = 0; n < siteable.Length; n++)
                {
                    offGround += siteable[n] ? 0 : 1;
                }

                Mod.Log.Info(
                    $"Pedestrian network: {(nodeX.Length).ToString(CultureInfo.InvariantCulture)} nodes, " +
                    $"{(edgeA.Length).ToString(CultureInfo.InvariantCulture)} edges with a pavement, " +
                    $"{(offGround).ToString(CultureInfo.InvariantCulture)} nodes in tunnels or on bridges (walkable, not sites)");
                m_RoadCacheDirty = false;
                rebuilt = true;
            }

            if (m_WorkplaceCacheDirty || m_JobPositions.Count == 0)
            {
                m_TransformLookup.Update(this);
                m_PropertyRenterLookup.Update(this);
                SuitabilityInputs.CollectWorkplaces(m_WorkplaceQuery, m_TransformLookup, m_PropertyRenterLookup, m_JobPositions, m_JobWorkers);
                // Homes move on the same cadence as workplaces: buildings.
                SuitabilityInputs.CollectResidents(
                    EntityManager, m_HouseholdQuery, m_PropertyRenterLookup, m_TransformLookup,
                    m_HomePositions, m_HomeResidents, out m_HouseholdsWithoutHome);
                m_WorkplaceCacheDirty = false;
                rebuilt = true;
            }

            if (m_ZoneCacheDirty)
            {
                rebuilt = true;
                m_ZoneDataLookup.Update(this);
                SuitabilityInputs.CollectZonedCells(
                    EntityManager,
                    m_BlockQuery,
                    m_ZoneSystem,
                    m_ZoneDataLookup,
                    m_FutureHomePositions,
                    m_FutureHomeWeights,
                    m_FutureJobPositions,
                    m_FutureJobWeights);
                m_ZoneCacheDirty = false;
            }

            // Stops depend on the selected mode and on line membership, both of
            // which change often enough that they are collected every compute.
            SuitabilityInputs.CollectStops(
                EntityManager,
                m_PrefabSystem,
                m_StopQuery,
                settings.Mode,
                StopWeightOf,
                m_AllStopPositions,
                m_AllStopTypes,
                out m_LastOrphanCount);

            if (rebuilt)
            {
                m_LastCollectionRebuild = UnityEngine.Time.realtimeSinceStartup;
            }
        }

        // Blends the cached raw terms into weighted scores, normalizes each layer,
        // and extracts the recommended sites. Runs after every compute and again
        // whenever only weights or the highlight share change.
        private void RecombineAndNormalize()
        {
            // Guard on the INPUT only. m_Scores and m_ScoreScratch are outputs that
            // EnsureScoreBuffers allocates below, so testing them here made that call
            // unreachable and turned this whole pass into a permanent no-op: no
            // intensities were ever written, no sites extracted, and the term caps
            // stayed at zero, which starved the corridor seeds too.
            if (m_RawTerms is null)
            {
                return;
            }

            var settings = Mod.Settings;
            if (settings is null)
            {
                return;
            }

            int totalCells = m_RawTerms.Length;
            EnsureScoreBuffers(totalCells, out float[] scores, out float[] scoreScratch, out float[] termScratch);

            // Each unbounded term is normalized against a high percentile of its own
            // positive values, so W1..W5 behave as real relative weights. Without
            // this, population counts drown the bounded penalty and access terms.
            m_DemandCap = SuitabilityHeatmap.TermCap(m_RawTerms, totalCells, CapTerm.Demand, termScratch, scoreScratch);
            m_JobsCap = SuitabilityHeatmap.TermCap(m_RawTerms, totalCells, CapTerm.Jobs, termScratch, scoreScratch);
            m_FutureCap = SuitabilityHeatmap.TermCap(m_RawTerms, totalCells, CapTerm.Future, termScratch, scoreScratch);
            float invDemand = m_DemandCap > 0f ? 1f / m_DemandCap : 0f;
            float invJobs = m_JobsCap > 0f ? 1f / m_JobsCap : 0f;
            float invFuture = m_FutureCap > 0f ? 1f / m_FutureCap : 0f;

            // Each layer is null unless it is actually registered as an infomode, because
            // only a registered layer can ever reach a terrain channel — and filling
            // seven of them is seven passes of rounding and clamping over every cell on
            // the map, on every recompute.
            //
            // The demand layer used to bind SuitabilityLayer.TravelDemand, so the
            // residents term was written into the travel-demand channel and
            // SuitabilityLayer.Demand was never written at all; BuildDemandLayer then
            // overwrote the same buffer with the desire-line raster and whichever pass
            // ran last decided what the channel held.
            var termLayers = new SuitabilityHeatmap.TermLayers
            {
                Demand = TermLayer(SuitabilityLayer.Demand),
                Jobs = TermLayer(SuitabilityLayer.Jobs),
                Coverage = TermLayer(SuitabilityLayer.Coverage),
                Access = TermLayer(SuitabilityLayer.Access),
                Future = TermLayer(SuitabilityLayer.Future),
                Interchange = TermLayer(SuitabilityLayer.Interchange),
                CrossCoverage = TermLayer(SuitabilityLayer.CrossCoverage),
            };

            // Both cross-mode terms are expressed RELATIVE to the mode being placed,
            // so a bus gains a lot from sitting at a metro station while a metro
            // gains comparatively little from sitting at a bus stop. That asymmetry
            // is the feeder relationship: the smaller mode should come to the trunk.
            float selfWeight = math.max(0.1f, StopWeightOf(SuitabilityInputs.TransportTypeOf(settings.Mode)));
            float invSelf = 1f / selfWeight;

            // Pinned here rather than read from `settings` at query time: a score is
            // only comparable with the map it was combined into, and the panel's mode,
            // weights and radii can all move between a compute and a suggestion.
            m_ScoredMode = settings.Mode;
            m_ScoredInvSelf = invSelf;
            m_Scored = new CombineWeights(
                settings.W1, settings.W2, settings.W3, settings.W4, settings.W5, settings.W6, settings.W7,
                invDemand, invJobs, invFuture);

            // The score per tile under the weights just pinned, and the per-term layers
            // (raw inputs, unweighted, so they stay meaningful when a weight is zero).
            SuitabilityHeatmap.CombineField(m_RawTerms, totalCells, m_Access?.TileNode, in m_Scored, invSelf, scores, termLayers);

            SuitabilityScoring.NormalizeIntensities(
                scores,
                totalCells,
                settings.HighlightShare / 100f,
                Assumptions.IntensityGamma,
                m_LayerIntensities[(int)SuitabilityLayer.Score],
                scoreScratch);

            LogCombine(scores, totalCells);
            ExtractSites(settings);

            // Any layer's bytes may have changed, so force the interleaved buffer to
            // be rebuilt even if the active set is identical.
            m_Infoview.InvalidateExpandedCache();
        }

        // The map's own cells and a query for a different mode both come through
        // here, under the weights and caps pinned by the last combine, so they cannot
        // disagree. The formula itself is SuitabilityScoring.Combine, where it is tested.
        private float CombineCell(in SuitabilityCell cell, float invSelf)
        {
            return SuitabilityScoring.Combine(in cell, in m_Scored, invSelf);
        }

        // The combine pass is where a plausible wrong map is made: a term cap of zero
        // silently drops that term out of every score, and a score field with no
        // positive member yields no sites and no corridor seeds while still painting a
        // full-looking gradient. None of those numbers were visible anywhere, so a map
        // showing only road access read exactly like a working one.
        private void LogCombine(float[] scores, int totalCells)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            int positive = 0;
            for (int i = 0; i < totalCells; i++)
            {
                float score = scores[i];
                min = math.min(min, score);
                max = math.max(max, score);
                if (score > 0f)
                {
                    positive++;
                }
            }

            float residents = 0f;
            float jobsTotal = 0f;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (inputs is not null)
            {
                for (int i = 0; i < inputs.Homes.Count; i++)
                {
                    residents += inputs.Homes.Weight[i];
                }

                for (int i = 0; i < inputs.Jobs.Count; i++)
                {
                    jobsTotal += inputs.Jobs.Weight[i];
                }
            }

            DeferredLog.Info(
                $"Combine: caps demand={(m_DemandCap).ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"jobs={(m_JobsCap).ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"future={(m_FutureCap).ToString("F1", CultureInfo.InvariantCulture)} " +
                "(a cap of 0 means that term is zero everywhere and drops out of the score); " +
                $"scores min={(min).ToString("F3", CultureInfo.InvariantCulture)} max={(max).ToString("F3", CultureInfo.InvariantCulture)}, " +
                $"{(positive).ToString(CultureInfo.InvariantCulture)}/{(totalCells).ToString(CultureInfo.InvariantCulture)} positive; " +
                $"source totals residents={(residents).ToString("F0", CultureInfo.InvariantCulture)}, jobs={(jobsTotal).ToString("F0", CultureInfo.InvariantCulture)}");

            LogTopCells(scores, totalCells);
        }

        // The three brightest tiles on the map, by world position, with the terms that
        // put them there. "The map is bright somewhere it should not be" is otherwise
        // unanswerable: the score is a weighted sum of seven measurements and the map
        // shows only the sum. With this, one line of the log says which measurement it
        // was, and a position to go and look at.
        private void LogTopCells(float[] scores, int totalCells)
        {
            SuitabilityCell[]? terms = m_RawTerms;
            if (terms is null || totalCells <= 0 || m_IntensityGrid.x <= 0)
            {
                return;
            }

            // net48: no Span here, and three ints do not need one.
            var best = new int[Assumptions.TopCellsLogged];
            int found = 0;
            for (int i = 0; i < totalCells && i < terms.Length; i++)
            {
                if (scores[i] <= 0f)
                {
                    continue;
                }

                int at = found;
                while (at > 0 && scores[best[at - 1]] < scores[i])
                {
                    if (at < Assumptions.TopCellsLogged)
                    {
                        best[at] = best[at - 1];
                    }

                    at--;
                }

                if (at < Assumptions.TopCellsLogged)
                {
                    best[at] = i;
                    found = math.min(found + 1, Assumptions.TopCellsLogged);
                }
            }

            var builder = new System.Text.StringBuilder("Strongest tiles: ");
            for (int k = 0; k < found; k++)
            {
                int index = best[k];
                int x = index % m_IntensityGrid.x;
                int y = index / m_IntensityGrid.x;
                float2 world = m_ScoreWorldMin + new float2((x + 0.5f) * Assumptions.TileSize, (y + 0.5f) * Assumptions.TileSize);
                SuitabilityCell cell = terms[index];
                if (k > 0)
                {
                    _ = builder.Append("; ");
                }

                _ = builder.Append('#').Append((k + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(" at (").Append(world.x.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(", ").Append(world.y.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(") score ").Append(scores[index].ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" [demand ").Append(cell.m_Demand.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(", jobs ").Append(cell.m_Jobs.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(", future ").Append(cell.m_Future.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(", coverage ").Append(cell.m_Coverage.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(", access ").Append(cell.m_Access.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(", interchange ").Append(cell.m_Interchange.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(", crossCoverage ").Append(cell.m_CrossCoverage.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(']');
            }

            DeferredLog.Info(builder.ToString());
        }

        // Returns the two buffers the combine pass writes through. Handing them back
        // is what keeps them out of that pass's entry guard, where a null test on them
        // made this allocation unreachable.
        //
        // All three co-allocated fields are in the condition: testing only m_Scores
        // left flow analysis trusting one of the three.
        private void EnsureScoreBuffers(int totalCells, out float[] scores, out float[] scoreScratch, out float[] termScratch)
        {
            if (m_Scores is null || m_ScoreScratch is null || m_TermScratch is null
                || m_Scores.Length != totalCells)
            {
                m_Scores = new float[totalCells];
                m_ScoreScratch = new float[totalCells];
                m_TermScratch = new float[totalCells];
            }

            scores = m_Scores;
            scoreScratch = m_ScoreScratch;
            termScratch = m_TermScratch;

            for (int i = 0; i < SuitabilityLayers.Count; i++)
            {
                if (m_LayerIntensities[i] is null || m_LayerIntensities[i].Length != totalCells)
                {
                    m_LayerIntensities[i] = new byte[totalCells];
                }
            }
        }

        // The intensity buffer for a layer, or null when that layer is not registered
        // as an infomode and therefore cannot be drawn. SuitabilityLayers.All decides
        // which are; EnsurePrefabs registers exactly those.
        private byte[]? TermLayer(SuitabilityLayer layer)
        {
            return m_Infoview.IsRegistered(layer) ? m_LayerIntensities[(int)layer] : null;
        }

        // Demand near each network node, so corridor growth can tell a street with
        // people on it from a rural through-road carrying only passing trips.
        private float[]? BuildNodeDemand(AlignmentNetwork network, int2 gridSize)
        {
            if (network.Graph is null || m_RawTerms is null || network.NodeCount == 0)
            {
                return null;
            }

            return SuitabilityHeatmap.NodeDemand(
                network, m_RawTerms, m_DemandCap, m_JobsCap,
                new float2Like(m_ScoreWorldMin.x, m_ScoreWorldMin.y), Assumptions.TileSize, new int2Like(gridSize.x, gridSize.y));
        }

        private float ScoreAtWorld(float2 point, int2 gridSize)
        {
            if (m_Scores is null)
            {
                return 0f;
            }

            int2 cell = SuitabilityInputs.WorldToCell(point, m_ScoreWorldMin, Assumptions.TileSize, gridSize);
            int index = cell.x + cell.y * gridSize.x;
            if (index < 0 || index >= m_Scores.Length)
            {
                return 0f;
            }

            return m_Scores[index];
        }

        // The same tile, scored for a DIFFERENT mode than the map was built for.
        //
        // The access pass keeps every mode's accumulators per network node, so this is
        // the full combine over the tile's node for the requested mode — not a swap of
        // the stop-derived terms against the panel's map. It matters because the map is
        // built for whatever mode the PANEL is showing, while a suggested line's mode is
        // decided by flow and length. A tram was being placed against the bus map:
        // penalised for sitting near bus stops, which is not its own service, and
        // rewarded for sitting near trams, which is.
        private float ScoreForMode(float2 point, int2 gridSize, ModePreset mode)
        {
            float baseScore = ScoreAtWorld(point, gridSize);
            WalkAccessOutput? access = m_Access;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (mode == m_ScoredMode || access is null || inputs is null)
            {
                return baseScore;
            }

            int2 cell = SuitabilityInputs.WorldToCell(point, m_ScoreWorldMin, Assumptions.TileSize, gridSize);
            int index = cell.x + (cell.y * gridSize.x);
            if (index < 0 || index >= access.TileNode.Length || access.TileNode[index] < 0)
            {
                return 0f;
            }

            int cls = SuitabilityWalkAccess.ClassOf(inputs.CatchmentMs, Assumptions.CatchmentMs(mode));
            if (cls < 0)
            {
                return baseScore;
            }

            var selfType = (int)SuitabilityInputs.TransportTypeOf(mode);
            SuitabilityCell terms = SuitabilityWalkAccess.NodeTerms(access.Result, access.TileNode[index], cls, selfType, access.TypeWeight);
            terms.m_Access = (float)SuitabilityWalkAccess.Kernel(access.TileWalkMs[index], inputs.AccessMs);
            float invSelf = 1f / math.max(0.1f, StopWeightOf(SuitabilityInputs.TransportTypeOf(mode)));
            return CombineCell(in terms, invSelf);
        }

        // A ferry pier belongs where the water meets the land it serves. Scoring open
        // water at zero keeps stops off mid-crossing positions; the score of the
        // nearby land then decides which stretch of coast gets the pier.
        private float ShorelineScoreAt(float2 point, int2 gridSize)
        {
            if (m_Land is null)
            {
                return 0f;
            }

            int2 centre = SuitabilityInputs.WorldToCell(point, m_ScoreWorldMin, Assumptions.TileSize, gridSize);
            int span = 2;
            bool touchesLand = false;
            float best = 0f;
            var bestCell = default(int2);

            for (int dy = -span; dy <= span; dy++)
            {
                int y = centre.y + dy;
                if (y < 0 || y >= gridSize.y)
                {
                    continue;
                }
                for (int dx = -span; dx <= span; dx++)
                {
                    int x = centre.x + dx;
                    if (x < 0 || x >= gridSize.x)
                    {
                        continue;
                    }

                    int index = x + y * gridSize.x;
                    if (index >= m_Land.Length || m_Land[index] == 0)
                    {
                        continue;
                    }

                    touchesLand = true;
                    if (m_Scores is not null && index < m_Scores.Length && m_Scores[index] > best)
                    {
                        best = m_Scores[index];
                        bestCell = new int2(x, y);
                    }
                }
            }

            if (!touchesLand)
            {
                return 0f;
            }

            // Corrected for the mode being placed, like every other scoring path. This
            // read m_Scores straight, so a ferry stop was judged by whatever mode the
            // PANEL happened to be showing.
            //
            // The 5x5 sweep still picks the best cell off the uncorrected map, and only
            // the winner is re-scored: correcting all twenty-five would cost twenty-five
            // scans of the stop list per candidate position. The correction swaps three
            // stop-derived terms, which vary over the catchment radius rather than over
            // the 160 m this window spans, so it cannot reorder the cells within it.
            return ScoreForMode(
                SuitabilityInputs.CellCentre(bestCell, m_ScoreWorldMin, Assumptions.TileSize),
                gridSize, ModePreset.Ferry);
        }
    }
}
