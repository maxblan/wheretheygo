using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // The route pass: the alignment searches, weighing every candidate alone, the mode
    // ladder, the exact line-set search and the adoption of a finished pass. Runs on
    // a worker task; see the class comment on m_RoutesPending for what that implies.
    public sealed partial class TransitArchitectSystem
    {
        // The route pipeline — alignment search, weighing every candidate alone and the
        // exact line-set search — runs on a worker task. Everything it reads (the
        // networks, zone flows, scores, masks, served stops, existing lines) is main-
        // thread state that OnUpdate leaves untouched while m_RoutesPending is set: the
        // heat-map adoption, the demand refresh, line collection, the improvement and
        // calibration requests all wait. Everything it produces lands in a RoutePass and
        // is copied into the fields the panel and the renderer read once the task has
        // completed (FinishRoutesIfReady). The pass measured 60+ s on the main thread
        // in a 13-line city — every frame of it a frozen game.
        private bool m_RoutesPending;

        // A finished pass the player has not taken over yet. The list on screen and
        // the lines on the map stay as they are — a selection the player is looking
        // at is not pulled away by a refresh — until the panel's button applies it.
        private RoutePass? m_StagedPass;

        private System.Threading.Tasks.Task? m_PendingRoutes;

        private RoutePass? m_PendingRoutePass;

        private float m_RoutePassStarted;

        private float m_LastRoutePassStart = float.NegativeInfinity;

        private sealed class RoutePass
        {
            public readonly List<SuggestedRoute> Candidates = new List<SuggestedRoute>();
            public readonly List<SuggestedRoute> Routes = new List<SuggestedRoute>();
            public readonly List<float2Like> AcceptedStops = new List<float2Like>();
            public readonly List<TransitLine> AcceptedLines = new List<TransitLine>();
            public readonly List<SuggestedRoute> Resolved = new List<SuggestedRoute>();
            public readonly List<DeferredLogLine> Log = new List<DeferredLogLine>();
            public LineSetSolution? Solution;
            public LineSetProblem? Problem;
            // What the demand refresh that started the pass would have reported.
            public int TripCount;
            public int AssignedPairs;
            public float TotalZoneWeight;
            public float AssignedWeight;
            // The journeys as door-to-door pairs and the base network's door-to-door
            // times, built once per pass and shared by every set problem of the pass.
            public LineSetProblem? PairTable;
            public float[]? Baseline;
            // The base network routed alone: the riders of every existing line (in the
            // order of m_ExistingLines when the pass started) for the line verdicts.
            public LineSetEvaluation? BaselineEvaluation;
            // Where the worker's time went, for the log.
            public long AssignMs;
            public long AlignmentMs;
            public long WeighMs;
            public long ResolveMs;
            public long SolveMs;
        }

        private readonly List<SuggestedRoute> m_RouteCandidates = new List<SuggestedRoute>();

        private readonly List<SuggestedRoute> m_Routes = new List<SuggestedRoute>();

        // Hands the route pipeline to a worker task. The fleet capacities are read here
        // because the first read walks the vehicle prefabs, which is ECS; afterwards the
        // worker only sees the cached copy.
        private bool StartRoutePass(Setting settings, int2 gridSize, float2 worldMin, int tripCount, float totalZoneWeight)
        {
            if (m_RoutesPending)
            {
                return false;
            }

            _ = ReadFleetFacts();
            var pass = new RoutePass
            {
                TripCount = tripCount,
                TotalZoneWeight = totalZoneWeight,
            };
            m_PendingRoutePass = pass;
            m_RoutesPending = true;
            m_RoutePassStarted = UnityEngine.Time.realtimeSinceStartup;
            m_LastRoutePassStart = m_RoutePassStarted;
            m_PendingRoutes = System.Threading.Tasks.Task.Run(
                () =>
                {
                    DeferredLog.Bind(pass.Log);
                    try
                    {
                        BuildRoutes(settings, gridSize, worldMin, pass);
                    }
                    finally
                    {
                        DeferredLog.Unbind();
                    }
                },
                System.Threading.CancellationToken.None);
            return true;
        }

        // Adopts a finished pass: its log lines first, in order, then the lists the
        // panel, the renderer and the export read, then the served-stop update the
        // kept suggestions imply. A faulted pass is logged in full and the previous
        // suggestions stay.
        private void FinishRoutesIfReady(Setting settings)
        {
            AnnounceRestoredRoutes();
            if (s_ApplyRouteUpdate)
            {
                s_ApplyRouteUpdate = false;
                if (m_StagedPass is not null)
                {
                    RoutePass staged = m_StagedPass;
                    m_StagedPass = null;
                    s_RouteUpdate = string.Empty;
                    AdoptPass(staged);
                }
            }

            System.Threading.Tasks.Task? pending = m_PendingRoutes;
            RoutePass? pass = m_PendingRoutePass;
            if (!m_RoutesPending || pending is null || pass is null || !pending.IsCompleted)
            {
                return;
            }

            m_RoutesPending = false;
            m_PendingRoutes = null;
            m_PendingRoutePass = null;
            DeferredLog.Flush(pass.Log);
            float elapsed = UnityEngine.Time.realtimeSinceStartup - m_RoutePassStarted;
            if (pending.IsFaulted || pending.IsCanceled)
            {
                DeferredLog.Error($"Route pass failed after {(elapsed).ToString("F1", CultureInfo.InvariantCulture)} s; the previous suggestions stay: {pending.Exception}");
                return;
            }

            DeferredLog.Info($"Route pass finished: {(elapsed).ToString("F1", CultureInfo.InvariantCulture)} s on the worker, {(pass.Routes.Count).ToString(CultureInfo.InvariantCulture)} suggestions");
            AdoptExistingLineRiders(pass);
            if (m_Routes.Count == 0)
            {
                AdoptPass(pass);
                return;
            }

            // Staged, not applied: the player decides when the list changes under them.
            // A newer pass replaces an older one still waiting.
            m_StagedPass = pass;
            s_RouteUpdate = (pass.Routes.Count).ToString(CultureInfo.InvariantCulture);
            DeferredLog.Info("New suggestions are staged; the panel offers to apply them");
        }

        // Copies a finished pass into the fields the panel, the renderer and the export
        // read, then the summary, the list and the log. Main thread only.
        private void AdoptPass(RoutePass pass)
        {
            m_RouteCandidates.Clear();
            m_RouteCandidates.AddRange(pass.Candidates);
            m_Routes.Clear();
            m_Routes.AddRange(pass.Routes);
            m_AcceptedStops.Clear();
            m_AcceptedStops.AddRange(pass.AcceptedStops);
            m_AcceptedLines.Clear();
            m_AcceptedLines.AddRange(pass.AcceptedLines);
            m_LineSetSolution = pass.Solution;
            m_LineSetProblem = pass.Problem;
            m_LineSetResolved.Clear();
            m_LineSetResolved.AddRange(pass.Resolved);

            UpdateRouteSummary(pass.TripCount, pass.AssignedPairs);
            LogRoutes();
            if (pass.TripCount >= 0)
            {
                LogSanityChecks(pass.TotalZoneWeight);
            }

        }

        // What the base network carries is a fact about the existing lines, not about
        // the suggestions, so it is taken over the moment the pass finishes — staged or
        // not. The collection the pass routed is the one still held (line collection
        // waits for the worker), so index i is m_ExistingLines[i].
        private void AdoptExistingLineRiders(RoutePass pass)
        {
            LineSetEvaluation? baseline = pass.BaselineEvaluation;
            if (baseline is null)
            {
                return;
            }

            m_ExistingLineRiders.Clear();
            for (int i = 0; i < baseline.BaseRiders.Length && i < m_ExistingLines.Count; i++)
            {
                m_ExistingLineRiders[m_ExistingLines[i].m_Id] = ((float)baseline.BaseRiders[i], (float)baseline.BaseRidersByDay[i], (float)baseline.BaseRidersByNight[i]);
            }
        }

        private void DiscardPendingRoutes()
        {
            if (!m_RoutesPending)
            {
                return;
            }

            // The task finishes on its own and is ignored: nothing reads a pass whose
            // pending fields were cleared.
            m_PendingRoutes = null;
            m_PendingRoutePass = null;
            m_RoutesPending = false;
            m_StagedPass = null;
            s_RouteUpdate = string.Empty;
        }
    }
}
