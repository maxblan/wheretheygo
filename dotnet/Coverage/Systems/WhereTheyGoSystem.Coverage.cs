using System.Globalization;
using Unity.Mathematics;

namespace WhereTheyGo
{
    // The equity measure on the game side: what the panel, the building colours and
    // the selected-building row read, published once per demand refresh. The field
    // and the share are measured on the worker (DemandStage, ServedWalkField,
    // JourneyEnds, Coverage) and adopted here together, so the horizon, the colours
    // and the headline share always describe the same measurement.
    public sealed partial class WhereTheyGoSystem
    {
        // The walk from each node of the snap's pedestrian network to the nearest
        // served stop: the field in place, handed to the next stage to read when it
        // does not need rebuilding.
        private int[]? m_ServedWalkMs;

        // The journey ends already snapped to the current tile snap's network, kept
        // across demand refreshes: the doors of a city barely change between two of
        // them. The demand stage owns it while one is out.
        private SnapMemo? m_SnapMemo;

        // Buffers the demand stage reuses from one refresh to the next, handed back
        // with it on adoption.
        private JourneyEnds? m_JourneyEnds;

        private IntDijkstra? m_CoverageDijkstra;

        // Each tile's walk to the nearest served stop, before it is quantised to bytes:
        // what the selected-building row reads.
        private int[]? m_AccessWalkMs;

        // The horizon the field and the share in place were measured against.
        private int m_CoverageHorizonMs;

        // What the served-walk field was built for: the stops, the pedestrian network
        // and the horizon. Unchanged means there is nothing to rebuild.
        private long m_FieldSignature = -1;

        // And the snap it was built on: a new snap is a new graph and a new numbering,
        // whatever the counts say.
        private TileSnap? m_FieldSnap;

        // How far each 32 m tile is from a served stop, as 0 (at a stop) to 255 (at or
        // beyond the walking horizon), and the grid it is laid out on. Rebuilt with the
        // served-walk field; BuildingAccessColorSystem samples it to colour buildings.
        private byte[]? m_AccessByTile;

        private int2 m_AccessFieldGrid;

        private float2 m_AccessFieldWorldMin;

        private int m_AccessFieldVersion;

        internal byte[]? AccessField => m_AccessByTile;

        internal int AccessFieldVersion => m_AccessFieldVersion;

        internal int2 AccessFieldGrid => m_AccessFieldGrid;

        internal float2 AccessFieldWorldMin => m_AccessFieldWorldMin;

        // The walk one building faces, read back out of the same field that colours it.
        // The selected-building panel shows this, so what the player is told and what
        // they can see on the map are one number by construction.
        //
        // Seconds rather than the stored byte: the byte is the walk as a fraction of the
        // equity horizon, which is a setting, so it means nothing on its own. `served`
        // is false at or beyond the horizon, since 255 is the far end of the ramp, not a
        // measurement, and reporting "10 min" for a building an hour from any stop would
        // be a lie the colour does not tell. `reached` is false beyond the search itself,
        // where there is no number to give at all - separately from the seconds, because
        // a building AT a stop has a walk of zero seconds and is reached all the same.
        internal bool TryGetAccessAt(float3 position, out int walkSeconds, out bool served, out bool reached)
        {
            walkSeconds = 0;
            served = false;
            reached = false;
            int[]? field = m_AccessWalkMs;
            int2 grid = m_AccessFieldGrid;
            if (field is null || grid.x <= 0 || grid.y <= 0 || field.Length != grid.x * grid.y || m_CoverageHorizonMs <= 0)
            {
                return false;
            }

            int x = (int)math.floor((position.x - m_AccessFieldWorldMin.x) / Assumptions.TileSize);
            int z = (int)math.floor((position.z - m_AccessFieldWorldMin.y) / Assumptions.TileSize);
            if (x < 0 || z < 0 || x >= grid.x || z >= grid.y)
            {
                return false;
            }

            int walk = field[(z * grid.x) + x];
            if (walk == int.MaxValue)
            {
                // Beyond the search itself, not merely beyond the horizon: there is no
                // number to give, and the caller says so.
                return true;
            }

            reached = true;
            walkSeconds = walk / 1000;
            served = walk < m_CoverageHorizonMs;
            return true;
        }

        // How long a walk still counts as served, for the panel to say so.
        internal int CoverageHorizonMinutes => m_CoverageHorizonMs / 60_000;

        // The colour-group index the transit-access infomode is active in, 0 when the
        // player has it switched off.
        internal int TransitAccessInfomodeIndex => m_Infoview.ObjectLayerIndex(OverlayLayer.TransitAccess);

        // The coverage measure's main-thread half, once per demand refresh: the
        // horizon the player set, the memo for the current snap, and whether the
        // served-walk field has to be rebuilt. Everything is measured on the worker.
        private void PrepareCoverage(Setting settings, DemandStage demand)
        {
            demand.HorizonMs = settings.CoverageWalkMinutes * 60_000;
            TileSnap? snap = m_TileSnap;
            // The snap's OWN graph, not m_WalkGraph: the streets are re-collected on
            // every change tag, and a re-collection that keeps the counts keeps the
            // snap while numbering its nodes afresh. A snapped index only means
            // anything on the graph it was snapped to (TileSnap.Graph).
            WalkGraph? graph = snap?.Graph;
            WalkNodeIndex? index = snap?.Index;
            if (snap is null || index is null || graph is null)
            {
                return;
            }

            m_SnapMemo = SnapMemo.For(m_SnapMemo, index, Assumptions.AccessWalkMs, Assumptions.SnapMemoCapacity);
            demand.Snap = snap;
            demand.Memo = m_SnapMemo;
            demand.Stops = m_TransitStops.ToArray();
            demand.Served = m_ServedWalkMs;
            demand.Dijkstra = m_CoverageDijkstra;
            demand.Ends = m_JourneyEnds;

            // The walk to the nearest served stop, and the field the buildings are
            // coloured from, depend on the STOPS and the pedestrian network, not on
            // the journeys. Rebuilding them every demand refresh was one Dijkstra per
            // served stop plus a pass over every tile of the map, thirty seconds apart,
            // for an answer that had not moved: in the log the field's version counted
            // up while its own figure stayed at 87.1 % to the decimal.
            demand.FieldSignature = ServedWalkField.Signature(demand.Stops, graph, demand.HorizonMs);
            demand.RebuildField = m_ServedWalkMs is null || demand.FieldSignature != m_FieldSignature || !ReferenceEquals(snap, m_FieldSnap);
            // The grid the snap was laid out on and the world corner it starts at, as
            // they stand now: a new snap may land while the stage is out.
            demand.FieldGrid = new int2Like(m_IntensityGrid.x, m_IntensityGrid.y);
            demand.FieldWorldMin = new float2Like(m_ScoreWorldMin.x, m_ScoreWorldMin.y);
        }

        // The worker's measurement, published: the horizon, the field when it was
        // rebuilt, and the share. Its buffers come back for the next refresh.
        private void AdoptCoverage(DemandStage demand)
        {
            m_JourneyEnds = demand.Ends;
            m_CoverageDijkstra = demand.Dijkstra;
            m_CoverageHorizonMs = demand.HorizonMs;
            if (demand.FieldRebuilt && demand.Snap is not null)
            {
                m_FieldSignature = demand.FieldSignature;
                m_FieldSnap = demand.Snap;
                m_ServedWalkMs = demand.Served;
                PublishAccessField(demand.Field, demand);
            }

            CoverageReport? coverage = demand.Report;
            if (coverage is null)
            {
                return;
            }

            int walkMinutes = demand.HorizonMs / 60_000;
            SetCoverageFigures(coverage.Share, walkMinutes, coverage.WalkClassShare);
            DeferredLog.Info(
                $"Coverage (measured): {(coverage.Share * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of journey weight served at both ends within " +
                $"{walkMinutes.ToString(CultureInfo.InvariantCulture)} min, " +
                $"{(coverage.TripsCovered).ToString(CultureInfo.InvariantCulture)}/{(coverage.Trips).ToString(CultureInfo.InvariantCulture)} journeys, " +
                $"{(coverage.TripsOffNetwork).ToString(CultureInfo.InvariantCulture)} with an end off the pedestrian network, " +
                $"Gini of access walk {coverage.GiniWalk.ToString("F3", CultureInfo.InvariantCulture)}, " +
                $"walk classes {WalkClassText(coverage.WalkClassShare)}, " +
                $"served stops {(demand.Stops.Length).ToString(CultureInfo.InvariantCulture)}; " +
                $"on the worker {(demand.FieldMs).ToString(CultureInfo.InvariantCulture)} ms {(demand.FieldRebuilt ? "rebuilding the served-walk field" : "(field unchanged, not rebuilt)")}");
        }

        // A rebuilt field replaces the one on the map in one step: the arrays are new,
        // so BuildingAccessColorSystem and the selected-building row never read half of
        // one and half of the other. Null clears it, and the buildings go grey.
        private void PublishAccessField(ServedWalkField? field, DemandStage demand)
        {
            m_AccessFieldVersion++;
            if (field is null)
            {
                m_AccessByTile = null;
                m_AccessWalkMs = null;
                return;
            }

            m_AccessWalkMs = field.WalkMs;
            m_AccessByTile = field.ByTile;
            m_AccessFieldGrid = new int2(field.Grid.x, field.Grid.y);
            m_AccessFieldWorldMin = new float2(demand.FieldWorldMin.x, demand.FieldWorldMin.y);
            DeferredLog.Info(
                $"Transit access field: {(field.Reached).ToString(CultureInfo.InvariantCulture)} of {(field.ByTile.Length).ToString(CultureInfo.InvariantCulture)} tiles within " +
                $"{(demand.HorizonMs / 60_000).ToString(CultureInfo.InvariantCulture)} min of a served stop (grid {(field.Grid.x).ToString(CultureInfo.InvariantCulture)}x{(field.Grid.y).ToString(CultureInfo.InvariantCulture)}, version {(m_AccessFieldVersion).ToString(CultureInfo.InvariantCulture)})");
        }

        // The shape behind the Gini, and the bar the panel draws: the walk to a served
        // stop in four classes. Logged with their SUM, which is the point of logging it
        // at all - it must read 100 %, and a bar the player cannot check is one the log
        // has to.
        private static string WalkClassText(float[] shares)
        {
            if (shares is null || shares.Length == 0)
            {
                return "not measured yet";
            }

            var parts = new string[shares.Length];
            float sum = 0f;
            for (int i = 0; i < shares.Length; i++)
            {
                parts[i] = (shares[i] * 100f).ToString("F1", CultureInfo.InvariantCulture);
                sum += shares[i];
            }

            return $"{string.Join("/", parts)} % (sums to {(sum * 100f).ToString("F1", CultureInfo.InvariantCulture)} %)";
        }
    }
}
