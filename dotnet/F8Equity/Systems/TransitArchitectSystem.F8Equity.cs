using System;
using System.Collections.Generic;
using System.Globalization;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // The equity measure (register A1.8/A1.9) on the game side: every journey of the
    // last demand refresh with its ends snapped to the pedestrian network, the walk
    // from each node to the nearest served stop, and the coverage share the panel
    // and the set objective read. The arithmetic is SuitabilityEquity.
    public sealed partial class StationSuitabilityOverlaySystem
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
            m_ServedWalkMs = SuitabilityEquity.ServedWalkMs(inputs.Graph, m_EquityDijkstra, stopNodes, stopAccess, stopNodes.Length, m_EquityHorizonMs);
            RefreshCoverage(settings, "measured");
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

            m_Coverage = SuitabilityEquity.Coverage(
                m_ServedWalkMs, m_EquityHorizonMs,
                m_JourneyOriginNode, m_JourneyOriginAccess, m_JourneyDestinationNode, m_JourneyDestinationAccess,
                m_JourneyWeight, m_Journeys.Count);
            s_Equity = SuitabilityPanelPayload.EquityRow(m_Coverage.Share, settings.EquityWalkMinutes, settings.EquityFloorPercent, m_Coverage.GiniWalk);
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
