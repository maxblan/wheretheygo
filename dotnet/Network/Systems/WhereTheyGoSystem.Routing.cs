using System.Collections.Generic;
using System.Globalization;

namespace WhereTheyGo
{
    // Routing every journey door to door over the lines the player has built. One
    // evaluation per demand refresh answers both questions the mod asks of the
    // network: how long each journey takes on it, and how many riders each existing
    // line carries.
    public sealed partial class WhereTheyGoSystem
    {
        // Door-to-door seconds of every pair in the table over the existing network:
        // the better of its transit itinerary and walking the whole way.
        private float[]? m_Baseline;

        // What the last routing found, kept for the figures and the bands that read it.
        private RoutingResult? m_Routed;

        private RoutingProblem? m_RoutedProblem;

        private CarriedReport m_CarriedReport;

        internal CarriedReport Carried => m_CarriedReport;

        private BandSet? m_Bands;

        // One routing pass, handed to a worker task. Everything it touches is a copy
        // or a plain array; nothing on the worker reads ECS state.
        private sealed class RoutingPass
        {
            public RoutingProblem Problem = new RoutingProblem();
            public RoutingResult? Result;
            public CarriedReport Carried;
            public BandSet? Bands;
            public float2Like WorldMin;
            // The line the player has selected, as its index in BaseLines, and what
            // taking it out of the network would cost its riders.
            public int TargetLine = -1;
            public int TargetLineId = -1;
            public LineContribution Contribution;
            public int2Like ZoneGrid;
            public List<Journey> Journeys = new List<Journey>();
            public readonly List<DeferredLogLine> Log = new List<DeferredLogLine>();
            public long RouteMs;
            public long BandMs;
            public long ContributionMs;
        }

        private System.Threading.Tasks.Task? m_PendingRouting;

        private RoutingPass? m_PendingPass;

        private bool m_RoutingPending;

        internal bool RoutingPending => m_RoutingPending;

        // What the renderer and the picker read. Null until the first refresh lands.
        internal BandSet? Bands => m_Bands;

        // The reading for the line the player has selected, and which line it belongs
        // to. -1 means nothing measured yet — the section then says so rather than
        // showing somebody else's numbers.
        private LineContribution m_LineContribution;

        private int m_ContributionLineId = -1;

        internal bool TryGetLineContribution(int lineId, out LineContribution contribution)
        {
            contribution = m_LineContribution;
            return lineId >= 0 && lineId == m_ContributionLineId;
        }

        private RoutingProblem? m_PairTable;

        // The journeys as door-to-door pairs: every trip at its own two positions, not
        // a zone centre; trips between the same two doors (one household's commuters to
        // one workplace) are one pair with their summed weight. Evaluate then searches
        // once per distinct origin door, and the geometry cache on the table is shared
        // by every problem built from it.
        private RoutingProblem BuildPairTable()
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
                Journey trip = m_Journeys[i];
                var key = (trip.m_Origin.x, trip.m_Origin.y, trip.m_Destination.x, trip.m_Destination.y);
                if (pairIndex.TryGetValue(key, out int existing))
                {
                    weight[existing] += trip.m_Weight;
                    dayWeight[existing] += trip.m_Weight * trip.DayShare;
                    continue;
                }

                pairIndex.Add(key, ox.Count);
                ox.Add(trip.m_Origin.x);
                oz.Add(trip.m_Origin.y);
                dx.Add(trip.m_Destination.x);
                dz.Add(trip.m_Destination.y);
                weight.Add(trip.m_Weight);
                dayWeight.Add(trip.m_Weight * trip.DayShare);
            }

            var dayShare = new float[ox.Count];
            for (int i = 0; i < dayShare.Length; i++)
            {
                dayShare[i] = weight[i] > 0f ? dayWeight[i] / weight[i] : 1f;
            }

            var table = new RoutingProblem
            {
                PairCount = ox.Count,
                PairOx = ox.ToArray(),
                PairOz = oz.ToArray(),
                PairDx = dx.ToArray(),
                PairDz = dz.ToArray(),
                PairWeight = weight.ToArray(),
                PairDayShare = dayShare,
            };
            table.Geometry = JourneyRouting.GeometryOf(table);
            return table;
        }

        // The pairs plus the existing served stops and lines as the network they are
        // routed over.
        private RoutingProblem BuildRoutingProblem()
        {
            RoutingProblem pairs = m_PairTable ??= BuildPairTable();
            var problem = new RoutingProblem
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

        // Hands the routing and the bundling to a worker task.
        //
        // Both are far too heavy for the frame: one Dijkstra per distinct origin door
        // over the whole transit graph, then a sweep over every zone pair. Measured at
        // two seconds for a city of 1,400 journeys, which on the main thread is two
        // seconds of frozen game every time the demand refreshes.
        //
        // Everything the worker reads is gathered HERE, on the main thread, as copies:
        // the problem's plain arrays and a snapshot of the journeys. While a pass is
        // out, OnUpdate leaves those inputs alone (m_RoutingPending).
        private bool StartRoutingPass()
        {
            if (m_RoutingPending)
            {
                return false;
            }

            m_PairTable = null;
            var pass = new RoutingPass
            {
                Problem = BuildRoutingProblem(),
                WorldMin = new float2Like(m_ScoreWorldMin.x, m_ScoreWorldMin.y),
                ZoneGrid = new int2Like(m_ZoneGrid.x, m_ZoneGrid.y),
                TargetLineId = SelectedLineId,
            };
            pass.TargetLine = IndexOfLine(pass.TargetLineId);
            pass.Problem.TargetLine = pass.TargetLine;
            pass.Journeys.AddRange(m_Journeys);
            m_PendingPass = pass;
            m_RoutingPending = true;
            m_PendingRouting = System.Threading.Tasks.Task.Run(
                () =>
                {
                    DeferredLog.Bind(pass.Log);
                    try
                    {
                        RunRoutingPass(pass);
                    }
                    finally
                    {
                        DeferredLog.Unbind();
                    }
                },
                System.Threading.CancellationToken.None);
            return true;
        }

        // Which of the collected lines carries this id, or -1. The line the player
        // clicked may have been deleted, or may be one of the lines the collection
        // drops (fewer than two stops inside the city).
        private int IndexOfLine(int lineId)
        {
            if (lineId < 0)
            {
                return -1;
            }

            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                if (m_ExistingLines[i].m_Id == lineId)
                {
                    return i;
                }
            }

            return -1;
        }

        // The worker's half: route every journey over the network as it stands, decide
        // what the network carries, bundle the result into bands, and — when the player
        // has a line selected — route the whole city a second time without that line,
        // which is the only honest way to say what it is worth.
        private static void RunRoutingPass(RoutingPass pass)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            RoutingResult routed = JourneyRouting.Evaluate(pass.Problem, before: null);
            pass.Carried = JourneyRouting.MarkCarried(pass.Problem, routed, new float[pass.Problem.PairCount]);
            pass.Result = routed;
            pass.RouteMs = clock.ElapsedMilliseconds;

            if (pass.TargetLine >= 0)
            {
                clock.Restart();
                pass.Contribution = MeasureTargetLine(pass, routed);
                pass.ContributionMs = clock.ElapsedMilliseconds;
            }

            clock.Restart();
            pass.Bands = DesireBands.Build(
                pass.Journeys, pass.Problem, routed,
                pass.WorldMin, pass.ZoneGrid,
                Assumptions.BandMergeMetres, Assumptions.MaxBands);
            pass.BandMs = clock.ElapsedMilliseconds;
        }

        // The same city routed again with one line taken out. Two evaluations rather
        // than one clever pass: each is around twenty milliseconds on the worker, and
        // the alternative — attributing time saved from inside a single search — was
        // exactly the kind of arithmetic that hid the free-transfer bug.
        private static LineContribution MeasureTargetLine(RoutingPass pass, RoutingResult routed)
        {
            RoutingProblem full = pass.Problem;
            var reduced = new RoutingProblem
            {
                PairCount = full.PairCount,
                PairOx = full.PairOx,
                PairOz = full.PairOz,
                PairDx = full.PairDx,
                PairDz = full.PairDz,
                PairWeight = full.PairWeight,
                PairDayShare = full.PairDayShare,
                Geometry = full.Geometry,
                BaseStopCount = full.BaseStopCount,
                BaseStopX = full.BaseStopX,
                BaseStopZ = full.BaseStopZ,
                WalkRadius = full.WalkRadius,
                BoardPenaltySeconds = full.BoardPenaltySeconds,
                MaxTravelSeconds = full.MaxTravelSeconds,
                ZoneReachMetres = full.ZoneReachMetres,
            };
            for (int i = 0; i < full.BaseLines.Count; i++)
            {
                if (i != pass.TargetLine)
                {
                    reduced.BaseLines.Add(full.BaseLines[i]);
                }
            }

            RoutingResult without = JourneyRouting.Evaluate(reduced, before: null);
            return JourneyRouting.Measure(full, routed, without);
        }

        // Adopts a finished pass: its log lines first, in order, then the fields the
        // panel, the renderer and the line readings all read. A faulted pass is logged
        // in full and the previous bands stay on the map.
        private void FinishRoutingIfReady()
        {
            System.Threading.Tasks.Task? pending = m_PendingRouting;
            RoutingPass? pass = m_PendingPass;
            if (!m_RoutingPending || pending is null || pass is null || !pending.IsCompleted)
            {
                return;
            }

            m_RoutingPending = false;
            m_PendingRouting = null;
            m_PendingPass = null;
            DeferredLog.Flush(pass.Log);
            if (pending.IsFaulted || pending.IsCanceled || pass.Result is null || pass.Bands is null)
            {
                DeferredLog.Error($"Routing pass failed: {pending.Exception}");
                return;
            }

            m_ContributionLineId = pass.TargetLine >= 0 ? pass.TargetLineId : -1;
            m_LineContribution = pass.Contribution;
            m_Routed = pass.Result;
            m_RoutedProblem = pass.Problem;
            m_CarriedReport = pass.Carried;
            m_Bands = pass.Bands;
            m_Baseline = pass.Result.After;
            m_ExistingLineRiders.Clear();
            for (int i = 0; i < pass.Result.BaseRiders.Length && i < m_ExistingLines.Count; i++)
            {
                m_ExistingLineRiders[m_ExistingLines[i].m_Id] =
                    ((float)pass.Result.BaseRiders[i], (float)pass.Result.BaseRidersByDay[i], (float)pass.Result.BaseRidersByNight[i]);
            }

            // The carried share is half of the panel's first figure, and the coverage
            // measure ran before this pass started — so the figure is refreshed here
            // rather than left a refresh behind.
            Setting? settings = Mod.Settings;
            if (settings is not null)
            {
                RefreshCoverage(settings, "routed");
            }

            LogRoutingPass(pass);
        }

        private void LogRoutingPass(RoutingPass pass)
        {
            RoutingProblem problem = pass.Problem;
            RoutingResult routed = pass.Result!;
            BandSet bands = pass.Bands!;
            DeferredLog.Info(
                $"Door-to-door routing: {(problem.PairCount).ToString(CultureInfo.InvariantCulture)} pairs from " +
                $"{(JourneyRouting.GeometryOf(problem).ZoneCount).ToString(CultureInfo.InvariantCulture)} doors over " +
                $"{(problem.BaseLines.Count).ToString(CultureInfo.InvariantCulture)} existing lines in " +
                $"{(pass.RouteMs).ToString(CultureInfo.InvariantCulture)} ms on the worker; " +
                $"carried {(pass.Carried.CarriedPairs).ToString(CultureInfo.InvariantCulture)} pairs = " +
                $"{(pass.Carried.Share * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of journey weight " +
                $"(faster than walking and under {(pass.Carried.CeilingSeconds).ToString("F0", CultureInfo.InvariantCulture)}s, " +
                $"{(pass.Carried.CeilingSeconds >= Assumptions.MaxJourneySeconds ? "the fixed hour: too few carried journeys for a median" : $"{Assumptions.ServedCeilingMultiple.ToString("F0", CultureInfo.InvariantCulture)}x this city's median of {pass.Carried.MedianSeconds.ToString("F0", CultureInfo.InvariantCulture)}s")})");
            DeferredLog.Info(
                $"Desire bands: {(bands.Bands.Length).ToString(CultureInfo.InvariantCulture)} from " +
                $"{(problem.PairCount).ToString(CultureInfo.InvariantCulture)} door pairs " +
                $"({(bands.MergedPairs).ToString(CultureInfo.InvariantCulture)} zone pairs folded into a neighbour at {(Assumptions.BandMergeMetres).ToString("F0", CultureInfo.InvariantCulture)} m), " +
                $"heaviest {(bands.HeaviestWeight).ToString("F0", CultureInfo.InvariantCulture)} journeys/day, " +
                $"{(pass.BandMs).ToString(CultureInfo.InvariantCulture)} ms" +
                (bands.HiddenPairs > 0
                    ? $"; NOT SHOWN: {(bands.HiddenPairs).ToString(CultureInfo.InvariantCulture)} zone pairs past the cap of {(Assumptions.MaxBands).ToString(CultureInfo.InvariantCulture)} bands, together {(bands.HiddenWeight).ToString("F0", CultureInfo.InvariantCulture)} journeys/day"
                    : string.Empty));
            if (pass.TargetLine >= 0 && pass.TargetLine < m_ExistingLines.Count)
            {
                ExistingLine target = m_ExistingLines[pass.TargetLine];
                DeferredLog.Info(
                    $"Line reading for \"{target.m_Name}\" ({target.m_Mode}): " +
                    $"{(pass.Contribution.RiderWeight).ToString("F0", CultureInfo.InvariantCulture)} journeys/day ride it, " +
                    $"saving {(pass.Contribution.MinutesSaved).ToString("F0", CultureInfo.InvariantCulture)} passenger-minutes/day against the rest of the network and walking; " +
                    $"{(pass.Contribution.DuplicateShare * 100f).ToString("F0", CultureInfo.InvariantCulture)} % of them would be no slower without it " +
                    $"(measured by routing the city again without the line, {(pass.ContributionMs).ToString(CultureInfo.InvariantCulture)} ms)");
            }

            for (int i = 0; i < problem.BaseLines.Count && i < m_ExistingLines.Count; i++)
            {
                DeferredLog.Info(
                    $"  line \"{m_ExistingLines[i].m_Name}\" ({m_ExistingLines[i].m_Mode}): riders/day={(routed.BaseRiders[i]).ToString("F0", CultureInfo.InvariantCulture)} " +
                    $"(day {(routed.BaseRidersByDay[i]).ToString("F0", CultureInfo.InvariantCulture)}, night {(routed.BaseRidersByNight[i]).ToString("F0", CultureInfo.InvariantCulture)})");
            }
        }
    }
}
