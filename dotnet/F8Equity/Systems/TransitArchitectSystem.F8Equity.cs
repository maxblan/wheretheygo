using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // The equity measure (register A1.8/A1.9) on the game side: every journey of the
    // last demand refresh with its ends snapped to the pedestrian network, the walk
    // from each node to the nearest served stop, and the coverage share the panel
    // and the set objective read. The arithmetic is Equity.
    public sealed partial class TransitArchitectSystem
    {
        private int[] m_JourneyOriginNode = Array.Empty<int>();

        private int[] m_JourneyOriginAccess = Array.Empty<int>();

        private int[] m_JourneyDestinationNode = Array.Empty<int>();

        private int[] m_JourneyDestinationAccess = Array.Empty<int>();

        private float[] m_JourneyWeight = Array.Empty<float>();

        private int[]? m_ServedWalkMs;

        private CoverageReport? m_Coverage;

        private int m_EquityHorizonMs;

        private IntDijkstra? m_EquityDijkstra;

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
            byte[]? field = m_AccessByTile;
            int2 grid = m_AccessFieldGrid;
            if (field is null || grid.x <= 0 || grid.y <= 0 || field.Length != grid.x * grid.y || m_EquityHorizonMs <= 0)
            {
                return false;
            }

            int x = (int)math.floor((position.x - m_AccessFieldWorldMin.x) / Assumptions.TileSize);
            int z = (int)math.floor((position.z - m_AccessFieldWorldMin.y) / Assumptions.TileSize);
            if (x < 0 || z < 0 || x >= grid.x || z >= grid.y)
            {
                return false;
            }

            byte value = field[(z * grid.x) + x];
            served = value < byte.MaxValue;
            walkSeconds = (int)((long)value * m_EquityHorizonMs / (255L * 1000L));
            return true;
        }

        // How long a walk still counts as served, for the panel to say so.
        internal int EquityHorizonMinutes => m_EquityHorizonMs / 60_000;

        // The colour-group index the transit-access infomode is active in, 0 when the
        // player has it switched off.
        internal int TransitAccessInfomodeIndex => m_Infoview.ObjectLayerIndex(SuitabilityLayer.TransitAccess);

        // Snaps every journey end to the pedestrian network once per demand refresh,
        // measures how many journeys the served stops reach at both ends within the
        // walking horizon, and publishes the figure the panel and the ranking use.
        private void MeasureEquity(Setting settings)
        {
            WalkAccessOutput? access = m_Access;
            WalkAccessInputs? inputs = m_AccessInputs;
            if (access?.Index is null || inputs is null)
            {
                m_Coverage = null;
                return;
            }

            m_EquityHorizonMs = settings.EquityWalkMinutes * 60_000;
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
                m_JourneyOriginNode[i] = SuitabilityWalkAccess.SnapPoint(access.Index, trip.m_Origin.x, trip.m_Origin.y, inputs.AccessMs, out m_JourneyOriginAccess[i]);
                m_JourneyDestinationNode[i] = SuitabilityWalkAccess.SnapPoint(access.Index, trip.m_Destination.x, trip.m_Destination.y, inputs.AccessMs, out m_JourneyDestinationAccess[i]);
                m_JourneyWeight[i] = trip.m_Weight;
            }

            if (m_EquityDijkstra is null || m_EquityDijkstra.Dist.Length != inputs.Graph.NodeCount)
            {
                m_EquityDijkstra = new IntDijkstra(inputs.Graph.NodeCount);
            }

            SnapStops(m_TransitStops, access.Index, inputs.AccessMs, out int[] stopNodes, out int[] stopAccess);
            m_ServedWalkMs = Equity.ServedWalkMs(inputs.Graph, m_EquityDijkstra, stopNodes, stopAccess, stopNodes.Length, m_EquityHorizonMs);
            BuildAccessField(access);
            RefreshCoverage(settings, "measured");
        }

        // The served-walk field rasterised onto the heat map's own tile grid, reusing
        // the tile-to-node snap the access pass already made (WalkAccessOutput.TileNode
        // / TileWalkMs). A tile off the network, or one whose nearest stop is beyond the
        // horizon, is 255 — the far end of the gradient rather than "no data", because
        // "no service within ten minutes" is exactly what the player wants to see.
        private void BuildAccessField(WalkAccessOutput access)
        {
            int[]? served = m_ServedWalkMs;
            if (served is null || m_EquityHorizonMs <= 0 || access.TileNode.Length == 0)
            {
                m_AccessByTile = null;
                return;
            }

            int count = access.TileNode.Length;
            if (m_AccessByTile is null || m_AccessByTile.Length != count)
            {
                m_AccessByTile = new byte[count];
            }

            byte[] field = m_AccessByTile;
            int reached = 0;
            for (int i = 0; i < count; i++)
            {
                int node = access.TileNode[i];
                long walk = node >= 0 && node < served.Length && served[node] != Equity.NotServed
                    ? (long)served[node] + access.TileWalkMs[i]
                    : m_EquityHorizonMs;
                if (walk >= m_EquityHorizonMs)
                {
                    field[i] = 255;
                    continue;
                }

                reached++;
                field[i] = (byte)(walk * 255L / m_EquityHorizonMs);
            }

            m_AccessFieldGrid = m_PlayableGridAtCompute;
            m_AccessFieldWorldMin = m_ScoreWorldMin;
            m_AccessFieldVersion++;
            DeferredLog.Info(
                $"Transit access field: {(reached).ToString(CultureInfo.InvariantCulture)} of {(count).ToString(CultureInfo.InvariantCulture)} tiles within " +
                $"{(m_EquityHorizonMs / 60_000).ToString(CultureInfo.InvariantCulture)} min of a served stop (grid {(m_AccessFieldGrid.x).ToString(CultureInfo.InvariantCulture)}x{(m_AccessFieldGrid.y).ToString(CultureInfo.InvariantCulture)}, version {(m_AccessFieldVersion).ToString(CultureInfo.InvariantCulture)})");
        }

        private static void SnapStops(List<float2Like> stops, WalkNodeIndex index, int accessMs, out int[] nodes, out int[] access)
        {
            nodes = new int[stops.Count];
            access = new int[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                nodes[i] = SuitabilityWalkAccess.SnapPoint(index, stops[i].x, stops[i].y, accessMs, out access[i]);
            }
        }

        private void RefreshCoverage(Setting settings, string why)
        {
            if (m_ServedWalkMs is null)
            {
                return;
            }

            m_Coverage = Equity.Coverage(
                m_ServedWalkMs, m_EquityHorizonMs,
                m_JourneyOriginNode, m_JourneyOriginAccess, m_JourneyDestinationNode, m_JourneyDestinationAccess,
                m_JourneyWeight, m_Journeys.Count);
            s_Equity = PanelPayload.EquityRow(m_Coverage.Share, settings.EquityWalkMinutes, settings.EquityFloorPercent, m_Coverage.GiniWalk);
            DeferredLog.Info(
                $"Equity ({why}): {(m_Coverage.Share * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of journey weight served at both ends within " +
                $"{settings.EquityWalkMinutes.ToString(CultureInfo.InvariantCulture)} min (floor {settings.EquityFloorPercent.ToString(CultureInfo.InvariantCulture)} %), " +
                $"{(m_Coverage.TripsCovered).ToString(CultureInfo.InvariantCulture)}/{(m_Coverage.Trips).ToString(CultureInfo.InvariantCulture)} journeys, " +
                $"{(m_Coverage.TripsOffNetwork).ToString(CultureInfo.InvariantCulture)} with an end off the pedestrian network, " +
                $"Gini of access walk {m_Coverage.GiniWalk.ToString("F3", CultureInfo.InvariantCulture)}, " +
                $"served stops {(m_TransitStops.Count).ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
