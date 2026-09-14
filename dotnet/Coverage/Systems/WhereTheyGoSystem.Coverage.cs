using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace WhereTheyGo
{
    // The equity measure (register A1.8/A1.9) on the game side: every journey of the
    // last demand refresh with its ends snapped to the pedestrian network, the walk
    // from each node to the nearest served stop, and the coverage share the panel
    // and the set objective read. The arithmetic is Coverage.
    public sealed partial class WhereTheyGoSystem
    {
        private int[] m_JourneyOriginNode = Array.Empty<int>();

        private int[] m_JourneyOriginAccess = Array.Empty<int>();

        private int[] m_JourneyDestinationNode = Array.Empty<int>();

        private int[] m_JourneyDestinationAccess = Array.Empty<int>();

        private float[] m_JourneyWeight = Array.Empty<float>();

        private int[]? m_ServedWalkMs;

        // The access field before it is quantised to bytes, and how far each tile's
        // value was carried to reach it. Fields rather than locals so the spread pass has
        // somewhere to work without allocating on every refresh.
        private int[]? m_AccessWalkMs;

        private int[] m_AccessSpreadMs = System.Array.Empty<int>();

        private CoverageReport? m_Coverage;

        private int m_CoverageHorizonMs;

        private IntDijkstra? m_CoverageDijkstra;

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
        // is false at or beyond the horizon — 255 is the far end of the ramp, not a
        // measurement, and reporting "10 min" for a building an hour from any stop would
        // be a lie the colour does not tell.
        internal bool TryGetAccessAt(float3 position, out int walkSeconds, out bool served)
        {
            walkSeconds = 0;
            served = false;
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

            walkSeconds = walk / 1000;
            served = walk < m_CoverageHorizonMs;
            return true;
        }

        // How long a walk still counts as served, for the panel to say so.
        internal int CoverageHorizonMinutes => m_CoverageHorizonMs / 60_000;

        // The colour-group index the transit-access infomode is active in, 0 when the
        // player has it switched off.
        internal int TransitAccessInfomodeIndex => m_Infoview.ObjectLayerIndex(OverlayLayer.TransitAccess);

        // Snaps every journey end to the pedestrian network once per demand refresh,
        // measures how many journeys the served stops reach at both ends within the
        // walking horizon, and publishes the figure the panel and the ranking use.
        private void MeasureCoverage(Setting settings)
        {
            TileSnap? snap = m_TileSnap;
            WalkGraph? graph = m_WalkGraph;
            if (snap?.Index is null || graph is null)
            {
                m_Coverage = null;
                return;
            }

            m_CoverageHorizonMs = settings.CoverageWalkMinutes * 60_000;
            int count = m_Journeys.Count;
            if (m_JourneyWeight.Length < count)
            {
                m_JourneyOriginNode = new int[count];
                m_JourneyOriginAccess = new int[count];
                m_JourneyDestinationNode = new int[count];
                m_JourneyDestinationAccess = new int[count];
                m_JourneyWeight = new float[count];
            }

            for (int i = 0; i < count; i++)
            {
                Trip trip = m_Journeys[i];
                m_JourneyOriginNode[i] = WalkAccess.SnapPoint(snap.Index, trip.m_Origin.x, trip.m_Origin.y, Assumptions.AccessWalkMs, out m_JourneyOriginAccess[i]);
                m_JourneyDestinationNode[i] = WalkAccess.SnapPoint(snap.Index, trip.m_Destination.x, trip.m_Destination.y, Assumptions.AccessWalkMs, out m_JourneyDestinationAccess[i]);
                m_JourneyWeight[i] = trip.m_Weight;
            }

            if (m_CoverageDijkstra is null || m_CoverageDijkstra.Dist.Length != graph.NodeCount)
            {
                m_CoverageDijkstra = new IntDijkstra(graph.NodeCount);
            }

            SnapStops(m_TransitStops, snap.Index, Assumptions.AccessWalkMs, out int[] stopNodes, out int[] stopAccess);
            // Searched well past the equity horizon on purpose. Every "is this served"
            // test compares against the horizon itself (Coverage.EndServed), so the
            // coverage figure is unchanged; what the extra range buys is a real number
            // for the buildings beyond it. A house 12 minutes from the nearest stop and
            // a house 40 minutes away are different problems, and "over 10 min" for both
            // reads as a broken measurement rather than a long walk.
            m_ServedWalkMs = Coverage.ServedWalkMs(
                graph, m_CoverageDijkstra, stopNodes, stopAccess, stopNodes.Length,
                m_CoverageHorizonMs * Assumptions.AccessFieldHorizonMultiple);
            BuildAccessField(snap);
            RefreshCoverage(settings, "measured");
        }

        // The served-walk field rasterised onto the heat map's own tile grid: how long a
        // walk from each tile to the nearest stop the city's lines actually serve, as 0
        // (at a stop) to 255 (at or beyond the walking horizon). This is what colours the
        // buildings and what the selected-building row reports.
        //
        // Two steps, and the second one is the point. TileSnap.TileNode only
        // holds a node for a tile within the ACCESS budget of the pedestrian network —
        // 8110 of 200704 tiles on Valmare — because that budget answers a different
        // question: how far a STOP may stand from a road. Reading it as "this tile has no
        // transit" put a house at one minute and the house next door at over ten, all
        // over the city, and painted a tram terminus as unserved. So tiles without a node
        // of their own take the walk of a nearby tile that has one, plus the walk between
        // them.
        private void BuildAccessField(TileSnap access)
        {
            int[]? served = m_ServedWalkMs;
            if (served is null || m_CoverageHorizonMs <= 0 || access.TileNode.Length == 0)
            {
                m_AccessByTile = null;
                return;
            }

            int count = access.TileNode.Length;
            int2 grid = m_PlayableGridAtCompute;
            if (grid.x <= 0 || grid.y <= 0 || grid.x * grid.y != count)
            {
                m_AccessByTile = null;
                return;
            }

            if (m_AccessWalkMs is null || m_AccessWalkMs.Length != count)
            {
                m_AccessWalkMs = new int[count];
                m_AccessSpreadMs = new int[count];
            }

            if (m_AccessByTile is null || m_AccessByTile.Length != count)
            {
                m_AccessByTile = new byte[count];
            }

            int[] walkMs = m_AccessWalkMs;
            for (int i = 0; i < count; i++)
            {
                int node = access.TileNode[i];
                long walk = node >= 0 && node < served.Length && served[node] != Coverage.NotServed
                    ? (long)served[node] + access.TileWalkMs[i]
                    : int.MaxValue;
                walkMs[i] = walk >= int.MaxValue ? int.MaxValue : (int)walk;
            }

            SpreadAccessField(walkMs, m_AccessSpreadMs, grid);

            // The COLOUR ramp still runs over the horizon and no further: past it every
            // walk is equally bad to look at, and stretching the ramp to the longest
            // walk on the map would wash out the difference between two and eight
            // minutes, which is the difference that matters.
            byte[] field = m_AccessByTile;
            int reached = 0;
            for (int i = 0; i < count; i++)
            {
                if (walkMs[i] == int.MaxValue || walkMs[i] >= m_CoverageHorizonMs)
                {
                    field[i] = 255;
                    continue;
                }

                reached++;
                field[i] = (byte)((long)walkMs[i] * 255L / m_CoverageHorizonMs);
            }

            m_AccessFieldGrid = grid;
            m_AccessFieldWorldMin = m_ScoreWorldMin;
            m_AccessFieldVersion++;
            DeferredLog.Info(
                $"Transit access field: {(reached).ToString(CultureInfo.InvariantCulture)} of {(count).ToString(CultureInfo.InvariantCulture)} tiles within " +
                $"{(m_CoverageHorizonMs / 60_000).ToString(CultureInfo.InvariantCulture)} min of a served stop (grid {(m_AccessFieldGrid.x).ToString(CultureInfo.InvariantCulture)}x{(m_AccessFieldGrid.y).ToString(CultureInfo.InvariantCulture)}, version {(m_AccessFieldVersion).ToString(CultureInfo.InvariantCulture)})");
        }

        // Fills in tiles that have no pedestrian node of their own from tiles that do,
        // charging the walk between tile centres. Two sweeps of a chamfer distance
        // transform — forward over increasing indices, backward over decreasing — which
        // is exact for this cost pattern and costs two passes over the grid.
        //
        // Bounded to Assumptions.AccessFieldSpreadTiles deliberately. Unbounded, it would
        // walk straight over a river to a stop on the far bank and report a walk nobody
        // can make; bounded, it bridges a house to its own street and no further.
        private static void SpreadAccessField(int[] walkMs, int[] spreadMs, int2 grid)
        {
            int straight = (int)(Assumptions.TileSize / Assumptions.WalkSpeed * 1000f);
            // A diagonal step is sqrt(2) tiles, charged as such rather than as one:
            // rounding it down is what turns a distance transform into a square.
            int diagonal = (int)(Assumptions.TileSize * 1.41421356f / Assumptions.WalkSpeed * 1000f);
            int budget = Assumptions.AccessFieldSpreadTiles * straight;

            for (int i = 0; i < walkMs.Length; i++)
            {
                spreadMs[i] = walkMs[i] == int.MaxValue ? int.MaxValue : 0;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                bool forward = pass == 0;
                for (int step = 0; step < grid.y; step++)
                {
                    int z = forward ? step : grid.y - 1 - step;
                    for (int inner = 0; inner < grid.x; inner++)
                    {
                        int x = forward ? inner : grid.x - 1 - inner;
                        int index = (z * grid.x) + x;
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0)
                                {
                                    continue;
                                }

                                int nx = x + dx;
                                int nz = z + dz;
                                if (nx < 0 || nz < 0 || nx >= grid.x || nz >= grid.y)
                                {
                                    continue;
                                }

                                int from = (nz * grid.x) + nx;
                                if (spreadMs[from] == int.MaxValue)
                                {
                                    continue;
                                }

                                int cost = dx != 0 && dz != 0 ? diagonal : straight;
                                // The budget is on the SPREAD, not on the walk: a tile
                                // may inherit a long walk from its street, but only over
                                // a few tiles of open ground. Without this the field
                                // would carry a walk straight over a river to a stop on
                                // the far bank and report a walk nobody can make.
                                long carried = (long)spreadMs[from] + cost;
                                if (carried > budget)
                                {
                                    continue;
                                }

                                long candidate = (long)walkMs[from] + cost;
                                if (candidate < walkMs[index])
                                {
                                    walkMs[index] = (int)candidate;
                                    spreadMs[index] = (int)carried;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void SnapStops(List<float2Like> stops, WalkNodeIndex index, int accessMs, out int[] nodes, out int[] access)
        {
            nodes = new int[stops.Count];
            access = new int[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                nodes[i] = WalkAccess.SnapPoint(index, stops[i].x, stops[i].y, accessMs, out access[i]);
            }
        }

        private void RefreshCoverage(Setting settings, string why)
        {
            if (m_ServedWalkMs is null)
            {
                return;
            }

            m_Coverage = Coverage.Measure(
                m_ServedWalkMs, m_CoverageHorizonMs,
                m_JourneyOriginNode, m_JourneyOriginAccess, m_JourneyDestinationNode, m_JourneyDestinationAccess,
                m_JourneyWeight, m_Journeys.Count);
            s_CoverageFigures = PanelPayload.CoverageRow(m_Coverage.Share, settings.CoverageWalkMinutes, m_Coverage.GiniWalk);
            DeferredLog.Info(
                $"Coverage ({why}): {(m_Coverage.Share * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of journey weight served at both ends within " +
                $"{settings.CoverageWalkMinutes.ToString(CultureInfo.InvariantCulture)} min, " +
                $"{(m_Coverage.TripsCovered).ToString(CultureInfo.InvariantCulture)}/{(m_Coverage.Trips).ToString(CultureInfo.InvariantCulture)} journeys, " +
                $"{(m_Coverage.TripsOffNetwork).ToString(CultureInfo.InvariantCulture)} with an end off the pedestrian network, " +
                $"Gini of access walk {m_Coverage.GiniWalk.ToString("F3", CultureInfo.InvariantCulture)}, " +
                $"served stops {(m_TransitStops.Count).ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
