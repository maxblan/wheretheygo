using System;
using System.Collections.Generic;

namespace TransitArchitect
{
    // A suggested transit line: the polyline it runs along, where its stops go, and
    // which mode it should be.
    internal sealed class SuggestedRoute
    {
        public readonly List<float2Like> Path = new List<float2Like>();
        public readonly List<float2Like> Stops = new List<float2Like>();
        public ModePreset Mode;
        // Length-weighted MEAN edge flow along the corridor (GraphMath's
        // GrowCorridor). Comparable to the network's mean positive edge flow, which is
        // what the mode floors are a multiple of.
        public float CapturedFlow;
        // Total journey weight this line would put onto the network, summed over every
        // zone pair it makes routable. A city-wide SUM, so it is one to two orders of
        // magnitude larger than CapturedFlow and the two must never be compared,
        // combined, or substituted for one another.
        public float EnabledDemand;
        // Whether EnabledDemand was ever measured. A candidate past the transfer
        // scoring window is left at zero without being routed, and a zero that was
        // never measured is not evidence of anything — it must not be read as "this
        // line would improve nothing", which is how every unscored candidate came to be
        // dropped by a bar that was meant to reject only measured zeroes.
        public bool DemandScored;
        public float Length;
        // Which network traced this alignment, and therefore which modes could
        // actually run on it. A tunnel path cannot host a bus.
        public RouteNetwork Network;
        // Whether the alignment was re-traced through an interchange it passes near.
        // Recorded so a corner in the drawn line can be attributed — a bend puts a
        // genuine corner at the hub, and telling that apart from a lattice staircase
        // the simplifier failed to straighten is otherwise guesswork.
        public bool BentThroughHub;
        // The fleet the line would run (TransitModes.PlanFleet on its riders within the
        // game's span), the interval that fleet yields, the span itself, and the round
        // trip they were sized on — the loop at cruise or directed speed plus the dwell
        // at every call each way.
        public int Vehicles;
        public float HeadwaySeconds;
        public int FleetMin;
        public int FleetMax;
        public float RoundTripSeconds;
        // The graph the alignment was traced on and its node path, kept so the stop
        // planner can read the corridor flow at any point of the line — including
        // after a mode change re-places the stops.
        public AlignmentNetwork? Source;
        public readonly List<int> Nodes = new List<int>();
        // The stop plan the stops came from (StopPlanning), for the log and the
        // verification export.
        public StopPlanProblem? StopPlan;
        public int[] StopPlanChosen = Array.Empty<int>();
        public double StopPlanGain;
        public double StopPlanDelay;
        // The alignment this candidate is a variant of (Routes numbers them
        // per pass); variants are alternatives in the set selection. Negative = none.
        public int Group = -1;
        // When the line should run (Daytime.Recommend on the set's riders by period),
        // with the utilisation in each period it rests on.
        public LineSchedule Schedule;
        public float DayUtilisation;
        public float NightUtilisation;

        // The same alignment as a candidate of another mode: path, flow, riders and
        // provenance copied, stops to be placed for the new mode by the caller.
        public SuggestedRoute CopyFor(ModePreset mode)
        {
            var copy = new SuggestedRoute
            {
                Mode = mode,
                CapturedFlow = CapturedFlow,
                EnabledDemand = EnabledDemand,
                DemandScored = DemandScored,
                Length = Length,
                Network = Network,
                BentThroughHub = BentThroughHub,
                Source = Source,
                Group = Group,
                Schedule = Schedule,
            };
            copy.Path.AddRange(Path);
            copy.Nodes.AddRange(Nodes);
            return copy;
        }
    }
}
