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
        private CarriedReport m_CarriedReport;

        internal CarriedReport Carried => m_CarriedReport;

        private BandSet? m_Bands;

        // One routing pass, handed to a worker task. Nothing on the worker reads ECS
        // state: the problem is plain arrays copied for it, the journeys a list nobody
        // writes while the pass is out, and on a demand refresh the stage, which the
        // main thread hands over whole (DemandStage) and does not touch again until it
        // is adopted.
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
            public IReadOnlyList<Journey> Journeys = new List<Journey>();
            public readonly List<DeferredLogLine> Log = new List<DeferredLogLine>();
            public long RouteMs;
            public long BandMs;
            public long ContributionMs;
            // Set on a demand refresh: the journeys still to be ordered, snapped,
            // measured and paired before the routing. Null on a selection pass, which
            // routes the journeys as they stand.
            public DemandStage? Demand;
        }

        private System.Threading.Tasks.Task? m_PendingRouting;

        private RoutingPass? m_PendingPass;

        private bool m_RoutingPending;

        internal bool RoutingPending => m_RoutingPending;

        // What the renderer and the picker read. Null until the first refresh lands.
        internal BandSet? Bands => m_Bands;

        // The reading for the line the player has selected, and which line it belongs
        // to. -1 means nothing measured yet, and the section then says so rather than
        // showing somebody else's numbers.
        private LineContribution m_LineContribution;

        private int m_ContributionLineId = -1;

        internal bool TryGetLineContribution(int lineId, out LineContribution contribution)
        {
            contribution = m_LineContribution;
            return lineId >= 0 && lineId == m_ContributionLineId;
        }

        // The pair table of the journeys as they stand, built once per demand refresh
        // on the worker and shared by every pass started on it. A selection pass routes
        // the SAME journeys, and rebuilding the table for it, a dictionary of every door
        // pair plus the geometry cached on it, was main-thread work for an answer that
        // had not moved. Adopted with the journeys it was built from, dropped by
        // ResetCityState; the passes only ever read it.
        private RoutingProblem? m_PairTable;

        // The hour strip's two profiles against the headline figure. They are measured
        // differently on purpose - the figure weighs door pairs, the strip weighs the
        // departures of the bands those pairs folded into - so they will not match to
        // the decimal; a wide gap between them means bands lost journeys the routing
        // counted, which is what the cap does and is worth seeing.
        private static void LogCarriedHours(BandSet bands, float carriedShare)
        {
            float[] hours = bands.HourlyProfile(AllPurposes);
            float[] carried = bands.CarriedHourlyProfile(AllPurposes);
            float total = 0f;
            float ridden = 0f;
            for (int hour = 0; hour < Band.HoursPerDay; hour++)
            {
                total += hours[hour];
                ridden += carried[hour];
            }

            DeferredLog.Info(
                $"Hour strip: {(total * 0.5f).ToString("F0", CultureInfo.InvariantCulture)} journeys/day over 24 hours, " +
                $"{((total > 0f ? ridden / total : 0f) * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of them carried " +
                $"(the panel's headline figure reads {(carriedShare * 100f).ToString("F1", CultureInfo.InvariantCulture)} %, weighed over door pairs rather than bands)");
        }

        // The existing served stops and lines as the network the pairs are routed
        // over. The pairs themselves are attached separately: a demand pass builds
        // them on the worker, a selection pass reuses the table of the journeys as
        // they stand.
        private RoutingProblem BuildRoutingProblem(int horizonMs)
        {
            var problem = new RoutingProblem
            {
                BaseStopCount = m_TransitStops.Count,
                BaseStopX = new float[m_TransitStops.Count],
                BaseStopZ = new float[m_TransitStops.Count],
                BaseLines = Lines.ToTransitLines(m_ExistingLines),
                WalkRadius = Assumptions.TransferWalkRadius,
                BoardPenaltySeconds = Assumptions.DefaultBoardPenaltySeconds,
                MaxTravelSeconds = Assumptions.MaxJourneySeconds,
                ZoneReachMetres = Assumptions.ZoneStopReachMetres,
                WalkedHorizonSeconds = WalkedHorizonSeconds(horizonMs),
            };
            for (int i = 0; i < m_TransitStops.Count; i++)
            {
                problem.BaseStopX[i] = m_TransitStops[i].x;
                problem.BaseStopZ[i] = m_TransitStops[i].y;
            }

            return problem;
        }

        // One horizon, two uses: the walk the player
        // accepts to reach a stop, "Walking horizon for served", is also the walk under
        // which a whole journey is a walk rather than a transit question. It is read off
        // the coverage measure rather than the settings, and a change of it reaches the
        // routing at the same demand refresh that re-measures the coverage, at most
        // thirty seconds on: a demand pass routes against the horizon its own stage
        // measures with (DemandStage.HorizonMs), a selection pass against the one in
        // place (m_CoverageHorizonMs, which only AdoptCoverage writes). Before the first
        // measure the default stands in, so the first pass is not one without walks.
        private static float WalkedHorizonSeconds(int horizonMs)
        {
            return horizonMs > 0
                ? horizonMs / 1000f
                : Assumptions.CoverageWalkMinutesDefault * 60f;
        }

        // Hands the routing and the bundling to a worker task.
        //
        // Both are far too heavy for the frame: one Dijkstra per distinct origin door
        // over the whole transit graph, then a sweep over every zone pair. Measured at
        // two seconds for a city of 1,400 journeys, which on the main thread is two
        // seconds of frozen game every time the demand refreshes.
        //
        // Everything the worker reads is gathered HERE, on the main thread, as copies:
        // the problem's plain arrays and a snapshot of the journeys, or on a demand
        // refresh the stage it hands over whole (the drained trips, the memo, the
        // served-walk field), which the main thread does not touch again until the
        // pass is adopted. While a pass is out, OnUpdate leaves those inputs alone
        // (m_RoutingPending).
        private bool StartRoutingPass(DemandStage? demand = null)
        {
            if (m_RoutingPending)
            {
                return false;
            }

            var pass = new RoutingPass
            {
                Problem = BuildRoutingProblem(demand?.HorizonMs ?? m_CoverageHorizonMs),
                WorldMin = new float2Like(m_ScoreWorldMin.x, m_ScoreWorldMin.y),
                ZoneGrid = new int2Like(m_ZoneGrid.x, m_ZoneGrid.y),
                TargetLineId = SelectedLineId,
                Demand = demand,
            };
            pass.TargetLine = IndexOfLine(pass.TargetLineId);
            pass.Problem.TargetLine = pass.TargetLine;
            // This pass answers the selection as it stands, whichever path started it.
            // Recorded here, not only where a click starts a pass: a demand refresh that
            // started with a fresh selection used to be followed by a second pass for
            // the same answer, because the selection gate had never seen it.
            m_RequestedLineId = pass.TargetLineId;
            if (demand is null)
            {
                // Shared, not copied: m_Journeys is only ever replaced, by adopting a
                // demand stage, and none can be out while this pass is.
                DoorPairs.Attach(pass.Problem, m_PairTable ??= DoorPairs.Build(m_Journeys));
                pass.Journeys = m_Journeys;
            }
            else
            {
                pass.Journeys = demand.Journeys;
            }

            m_PendingPass = pass;
            m_RoutingPending = true;
            m_PendingRouting = System.Threading.Tasks.Task.Run(
                () =>
                {
                    _ = DeferredLog.Bind(pass.Log);
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

        // The worker's half: route every journey over the network as it stands (which
        // decides what the network carries and credits each line with the carried
        // journeys riding it), bundle the result into bands, and, when the player has
        // a line selected, route the whole city a second time without that line, which
        // is the only honest way to say what it is worth.
        private static void RunRoutingPass(RoutingPass pass)
        {
            if (pass.Demand is not null)
            {
                pass.Demand.Run(pass.WorldMin, pass.ZoneGrid);
                DoorPairs.Attach(pass.Problem, pass.Demand.Pairs);
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            RoutingResult routed = JourneyRouting.Evaluate(pass.Problem);
            pass.Carried = routed.Report;
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
        // the alternative, attributing time saved from inside a single search, was
        // exactly the kind of arithmetic that hid the free-transfer bug.
        private static LineContribution MeasureTargetLine(RoutingPass pass, RoutingResult routed)
        {
            RoutingProblem full = pass.Problem;
            var reduced = new RoutingProblem
            {
                BaseStopCount = full.BaseStopCount,
                BaseStopX = full.BaseStopX,
                BaseStopZ = full.BaseStopZ,
                WalkRadius = full.WalkRadius,
                BoardPenaltySeconds = full.BoardPenaltySeconds,
                MaxTravelSeconds = full.MaxTravelSeconds,
                ZoneReachMetres = full.ZoneReachMetres,
            };
            DoorPairs.Attach(reduced, full);
            for (int i = 0; i < full.BaseLines.Count; i++)
            {
                if (i != pass.TargetLine)
                {
                    reduced.BaseLines.Add(full.BaseLines[i]);
                }
            }

            RoutingResult without = JourneyRouting.Evaluate(reduced);
            return JourneyRouting.Measure(full, routed, without);
        }

        // Adopts a demand stage the moment the worker has finished it, without waiting
        // for the routing behind it: the journeys, the pair table, the field and the
        // share are complete by then, and holding them back would tie the coverage
        // figure, and the building colours, to however long the routing takes, or to
        // whether it fails at all.
        private void FinishDemandIfReady()
        {
            DemandStage? demand = m_PendingPass?.Demand;
            if (!m_RoutingPending || demand is null || demand.Adopted || !demand.Done)
            {
                return;
            }

            demand.Adopted = true;
            AdoptDemand(demand);
        }

        // Adopts a finished pass: its log lines first, in order, then the fields the
        // panel, the renderer and the line readings all read. A faulted pass is logged
        // in full, naming the stage that threw, and the previous bands stay on the map.
        private void FinishRoutingIfReady()
        {
            FinishDemandIfReady();
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
                // A stage that never finished is the demand stage's fault, and its
                // journeys and coverage stay as they were; one that did was adopted
                // above, and only the routing behind it is lost.
                string failed = pass.Demand is { Done: false } ? "Demand stage (ordering, snapping, coverage)" : "Routing pass";
                DeferredLog.Error($"{failed} failed: {pending.Exception}");
                return;
            }

            m_ContributionLineId = pass.TargetLine >= 0 ? pass.TargetLineId : -1;
            m_LineContribution = pass.Contribution;
            m_CarriedReport = pass.Carried;
            SetCarriedFigure(m_CarriedReport.Share, m_CarriedReport.WalkedShare);
            m_Bands = pass.Bands;
            m_ExistingLineRiders.Clear();
            for (int i = 0; i < pass.Result.BaseRiders.Length && i < m_ExistingLines.Count; i++)
            {
                m_ExistingLineRiders[m_ExistingLines[i].m_Id] =
                    ((float)pass.Result.BaseRiders[i], (float)pass.Result.BaseRidersByDay[i], (float)pass.Result.BaseRidersByNight[i]);
            }

            // The coverage figure is NOT refreshed here: the demand stage measured it
            // and FinishDemandIfReady published it, on the same journeys this pass
            // routed. Measuring it again sorted every journey again for the Gini and
            // logged a second "Coverage" line that could only ever repeat the first.
            LogRoutingPass(pass, pass.Result, pass.Bands);
        }

        private void LogRoutingPass(RoutingPass pass, RoutingResult routed, BandSet bands)
        {
            RoutingProblem problem = pass.Problem;
            DeferredLog.Info(
                $"Door-to-door routing: {(problem.PairCount).ToString(CultureInfo.InvariantCulture)} pairs from " +
                $"{(JourneyRouting.GeometryOf(problem).ZoneCount).ToString(CultureInfo.InvariantCulture)} doors over " +
                $"{(problem.BaseLines.Count).ToString(CultureInfo.InvariantCulture)} existing lines in " +
                $"{(pass.RouteMs).ToString(CultureInfo.InvariantCulture)} ms on the worker; " +
                $"carried {(pass.Carried.CarriedPairs).ToString(CultureInfo.InvariantCulture)} pairs = " +
                $"{(pass.Carried.Share * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of journey weight " +
                $"(faster than walking and under {(pass.Carried.CeilingSeconds).ToString("F0", CultureInfo.InvariantCulture)}s, " +
                $"{(pass.Carried.CeilingSeconds >= Assumptions.MaxJourneySeconds ? "the fixed hour: too few carried journeys for a median" : $"{Assumptions.ServedCeilingMultiple.ToString("F0", CultureInfo.InvariantCulture)}x this city's median of {pass.Carried.MedianSeconds.ToString("F0", CultureInfo.InvariantCulture)}s")}); " +
                $"walked {(pass.Carried.WalkedPairs).ToString(CultureInfo.InvariantCulture)} pairs = " +
                $"{(pass.Carried.WalkedShare * 100f).ToString("F1", CultureInfo.InvariantCulture)} % of journey weight " +
                $"(within the {(problem.WalkedHorizonSeconds / 60f).ToString("F0", CultureInfo.InvariantCulture)} min horizon, left out of the carried share)");
            DeferredLog.Info(
                $"Desire bands: {(bands.Bands.Length).ToString(CultureInfo.InvariantCulture)} from " +
                $"{(problem.PairCount).ToString(CultureInfo.InvariantCulture)} door pairs " +
                $"({(bands.MergedPairs).ToString(CultureInfo.InvariantCulture)} zone pairs folded into a neighbour at {(Assumptions.BandMergeMetres).ToString("F0", CultureInfo.InvariantCulture)} m), " +
                $"heaviest {(bands.HeaviestWeight).ToString("F0", CultureInfo.InvariantCulture)} journeys/day, " +
                $"{(pass.BandMs).ToString(CultureInfo.InvariantCulture)} ms; " +
                $"{(bands.WalkedPairs).ToString(CultureInfo.InvariantCulture)} zone pairs entirely within walking distance folded away, " +
                $"together {(bands.WalkedWeight).ToString("F0", CultureInfo.InvariantCulture)} journeys/day walked in all" +
                (bands.HiddenPairs > 0
                    ? $"; NOT SHOWN: {(bands.HiddenPairs).ToString(CultureInfo.InvariantCulture)} zone pairs past the cap of {(Assumptions.MaxBands).ToString(CultureInfo.InvariantCulture)} bands, together {(bands.HiddenWeight).ToString("F0", CultureInfo.InvariantCulture)} journeys/day"
                    : string.Empty));
            LogCarriedHours(bands, pass.Carried.Share);
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
