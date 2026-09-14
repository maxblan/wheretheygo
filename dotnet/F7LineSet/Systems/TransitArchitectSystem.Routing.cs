using System.Collections.Generic;
using System.Globalization;

namespace TransitArchitect
{
    // Routing every journey door to door over the lines the player has built. One
    // evaluation per demand refresh answers both questions the mod asks of the
    // network: how long each journey takes on it, and how many riders each existing
    // line carries.
    public sealed partial class TransitArchitectSystem
    {
        // Door-to-door seconds of every pair in the table over the existing network,
        // float.MaxValue where the network cannot carry it.
        private float[]? m_Baseline;

        private LineSetProblem? m_PairTable;

        // The journeys as door-to-door pairs: every trip at its own two positions, not
        // a zone centre; trips between the same two doors (one household's commuters to
        // one workplace) are one pair with their summed weight. Evaluate then searches
        // once per distinct origin door, and the geometry cache on the table is shared
        // by every problem built from it.
        private LineSetProblem BuildPairTable()
        {
            var pairIndex = new Dictionary<(float, float, float, float), int>();
            var ox = new List<float>();
            var oz = new List<float>();
            var dx = new List<float>();
            var dz = new List<float>();
            var weight = new List<float>();
            var dayWeight = new List<float>();
            for (int i = 0; i < m_Journeys.Count; i++)
            {
                Trip trip = m_Journeys[i];
                var key = (trip.m_Origin.x, trip.m_Origin.y, trip.m_Destination.x, trip.m_Destination.y);
                if (pairIndex.TryGetValue(key, out int existing))
                {
                    weight[existing] += trip.m_Weight;
                    dayWeight[existing] += trip.m_Weight * trip.m_DayShare;
                    continue;
                }

                pairIndex.Add(key, ox.Count);
                ox.Add(trip.m_Origin.x);
                oz.Add(trip.m_Origin.y);
                dx.Add(trip.m_Destination.x);
                dz.Add(trip.m_Destination.y);
                weight.Add(trip.m_Weight);
                dayWeight.Add(trip.m_Weight * trip.m_DayShare);
            }

            var dayShare = new float[ox.Count];
            for (int i = 0; i < dayShare.Length; i++)
            {
                dayShare[i] = weight[i] > 0f ? dayWeight[i] / weight[i] : 1f;
            }

            var table = new LineSetProblem
            {
                PairCount = ox.Count,
                PairOx = ox.ToArray(),
                PairOz = oz.ToArray(),
                PairDx = dx.ToArray(),
                PairDz = dz.ToArray(),
                PairWeight = weight.ToArray(),
                PairDayShare = dayShare,
            };
            table.Geometry = LineSet.GeometryOf(table);
            return table;
        }

        // The pairs plus the existing served stops and lines as the network they are
        // routed over.
        private LineSetProblem BuildRoutingProblem()
        {
            LineSetProblem pairs = m_PairTable ??= BuildPairTable();
            var problem = new LineSetProblem
            {
                PairCount = pairs.PairCount,
                PairOx = pairs.PairOx,
                PairOz = pairs.PairOz,
                PairDx = pairs.PairDx,
                PairDz = pairs.PairDz,
                PairWeight = pairs.PairWeight,
                PairDayShare = pairs.PairDayShare,
                Geometry = pairs.Geometry,
                BaseStopCount = m_TransitStops.Count,
                BaseStopX = new float[m_TransitStops.Count],
                BaseStopZ = new float[m_TransitStops.Count],
                BaseLines = Lines.ToTransitLines(m_ExistingLines),
                WalkRadius = Assumptions.TransferWalkRadius,
                BoardPenaltySeconds = Assumptions.DefaultBoardPenaltySeconds,
                MaxTravelSeconds = Assumptions.MaxJourneySeconds,
                ZoneReachMetres = Assumptions.ZoneStopReachMetres,
            };
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                problem.BaseStopX[i] = m_TransitStops[i].x;
                problem.BaseStopZ[i] = m_TransitStops[i].y;
            }

            return problem;
        }

        // The network as it stands, routed once per refresh: every pair's door-to-door
        // time, and the riders each existing line carries — which is what a line's own
        // reading shows as its demand (register A8.2).
        private void RouteJourneys()
        {
            m_PairTable = null;
            LineSetProblem problem = BuildRoutingProblem();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            LineSetEvaluation routed = LineSet.Evaluate(problem, before: null);
            m_Baseline = routed.After;
            m_ExistingLineRiders.Clear();
            for (int i = 0; i < routed.BaseRiders.Length && i < m_ExistingLines.Count; i++)
            {
                m_ExistingLineRiders[m_ExistingLines[i].m_Id] =
                    ((float)routed.BaseRiders[i], (float)routed.BaseRidersByDay[i], (float)routed.BaseRidersByNight[i]);
            }

            DeferredLog.Info(
                $"Door-to-door routing: {(problem.PairCount).ToString(CultureInfo.InvariantCulture)} pairs from " +
                $"{(LineSet.GeometryOf(problem).ZoneCount).ToString(CultureInfo.InvariantCulture)} doors over " +
                $"{(problem.BaseLines.Count).ToString(CultureInfo.InvariantCulture)} existing lines in " +
                $"{(stopwatch.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture)} ms");
            for (int i = 0; i < problem.BaseLines.Count && i < m_ExistingLines.Count; i++)
            {
                DeferredLog.Info(
                    $"  line \"{m_ExistingLines[i].m_Name}\" ({m_ExistingLines[i].m_Mode}): riders/day={(routed.BaseRiders[i]).ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"(day {(routed.BaseRidersByDay[i]).ToString("F0", CultureInfo.InvariantCulture)}, night {(routed.BaseRidersByNight[i]).ToString("F0", CultureInfo.InvariantCulture)})");
            }
        }
    }
}
