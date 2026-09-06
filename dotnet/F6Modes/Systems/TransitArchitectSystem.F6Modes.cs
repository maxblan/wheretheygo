using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // F6 — mode choice on the game side: settles what mode a candidate runs as from
    // its own riders, walks the capacity ladder across networks (a rail alignment is
    // first re-traced on streets), offers the next mode up as a variant, and applies
    // the shape and already-built gates. The arithmetic is TransitModes.ChooseMode.
    public sealed partial class StationSuitabilityOverlaySystem
    {

        // The per-line bars a resolved candidate has to clear before the set search may
        // consider it: its shape (at least MinStops calls, a ride within the mode's
        // limit) and not being a line the player has already built. Whether it fills
        // its vehicles or duplicates another suggestion is the set search's to judge.
        private bool PassesLineGates(SuggestedRoute candidate, int index, FleetFacts facts, RejectionTally tally)
        {
            float rideSeconds = RideSecondsOf(candidate, facts);
            if (!SuitabilityRoutes.KeepsItsShape(candidate, rideSeconds))
            {
                tally.Unjustified++;
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, corridorFlow={(candidate.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"len={(candidate.Length).ToString("F0", CultureInfo.InvariantCulture)}m, {candidate.Stops.Count} stops, ride {(rideSeconds / 60f).ToString("F1", CultureInfo.InvariantCulture)} min — DROPPED, " +
                    $"{(candidate.Stops.Count < Assumptions.MinStops ? $"fewer than {Assumptions.MinStops.ToString(CultureInfo.InvariantCulture)} stops" : $"over the {(Assumptions.MaxRideSecondsFor(candidate.Mode) / 60f).ToString("F0", CultureInfo.InvariantCulture)} min ride limit for a {candidate.Mode}")}");
                return false;
            }

            if (SuitabilityRoutes.DuplicatesExisting(candidate, m_ExistingLines, m_TransitStops, Assumptions.DuplicateLineMatchMetres))
            {
                tally.AlreadyBuilt++;
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network} {candidate.Mode}, {candidate.Stops.Count} stops — DROPPED, already built");
                return false;
            }

            return true;
        }

        // End-to-end ride time as the rider is quoted it: directed street legs where
        // they exist, cruise speed otherwise, plus one stop delay per intermediate stop.
        private float RideSecondsOf(SuggestedRoute route, FleetFacts facts)
        {
            float delay = facts.DelayPerStopSeconds(route.Mode);
            float[]? legs = RoadRideSeconds(route);
            if (legs is null)
            {
                return TransitModes.RideSeconds(route.Length, route.Stops.Count, Assumptions.CruiseSpeedFor(route.Mode), delay);
            }

            float driving = 0f;
            float speed = Assumptions.CruiseSpeedFor(route.Mode);
            for (int i = 1; i < legs.Length; i++)
            {
                driving += legs[i] > 0f ? legs[i] : float2Like.Distance(route.Stops[i - 1], route.Stops[i]) / math.max(1f, speed);
            }

            return driving + (Math.Max(0, route.Stops.Count - 2) * delay);
        }

        // Why the candidates that did not become suggestions were turned down, for the
        // one summary line the log is read by.
        //
        // A class rather than four `ref int` parameters threaded through two methods:
        // every rejection reason added so far added a parameter to both, and the count
        // was reaching the point where the call site said nothing about what it did.
        private sealed class RejectionTally
        {
            public int Unjustified;
            public int Retraced;
            public int AlreadyBuilt;
        }

        // Settles what mode a candidate runs as and hands it to the line gates (register
        // A6.x: the smallest mode whose vehicles the riders do not overload).
        //
        // For a RAIL alignment the ladder starts on the streets: the same journey is
        // re-traced along roads and, if a bus or tram carries its own riders without
        // overloading, that is the candidate — a metro is only offered where the street
        // modes would be overloaded or no street path exists. Without this rule a rail
        // alignment always won on speed alone, and a city with its lines removed was
        // offered three metros of 1.4 to 5.4 km (Valmare, 19:44). Ferries keep their
        // water alignment and fall back to streets only under the utilisation floor.
        //
        // The mode chosen is re-measured after the stops are re-placed for it, and the
        // next mode up is offered as a second candidate whenever a set could hand the
        // line enough riders to overload it (a feeder's trunk); the set's utilisation
        // ceiling then rules the smaller one out. Variants of one alignment duplicate
        // each other, so a set holds at most one of them.
        private void ResolveCandidate(
            Setting settings,
            SuggestedRoute candidate,
            int index,
            FleetFacts facts,
            StopContext stops,
            List<int> scratch,
            RejectionTally tally,
            List<SuggestedRoute> resolved,
            RoutePass pass)
        {
            bool onLattice = candidate.Network is RouteNetwork.Rail or RouteNetwork.Metro;
            if (onLattice && candidate.Stops.Count >= 2
                && OfferOnStreets(settings, candidate, index, facts, stops, scratch, tally, resolved, pass, requireFit: true))
            {
                return;
            }

            if (!SettleMode(settings, candidate, index, facts, stops, pass, out float utilisation, out ModePreset? nextUp))
            {
                tally.Unjustified++;
                return;
            }

            if (PassesLineGates(candidate, index, facts, tally))
            {
                resolved.Add(candidate);
            }

            OfferNextUp(settings, candidate, index, facts, stops, resolved, pass, nextUp, utilisation);

            if (candidate.Network != RouteNetwork.Water || candidate.Stops.Count < 2 || utilisation >= settings.UtilisationFloorPercent / 100f)
            {
                return;
            }

            _ = OfferOnStreets(settings, candidate, index, facts, stops, scratch, tally, resolved, pass, requireFit: false);
        }

        // The candidate's journey re-traced along streets as a road line. With
        // `requireFit` the street variant is only accepted — and the lattice alignment
        // thereby dropped — when its own riders leave the chosen road mode under the
        // utilisation ceiling and it passes the line gates. Returns true when a street
        // variant was offered.
        private bool OfferOnStreets(
            Setting settings, SuggestedRoute candidate, int index, FleetFacts facts, StopContext stops,
            List<int> scratch, RejectionTally tally, List<SuggestedRoute> resolved, RoutePass pass, bool requireFit)
        {
            SuggestedRoute? onRoad = SuitabilityRoutes.RetraceOnRoad(
                m_RoadGraph, candidate.Stops[0], candidate.Stops[candidate.Stops.Count - 1], stops, scratch);
            if (onRoad is null)
            {
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {candidate.Network}, no street path between its ends — the {candidate.Mode} alignment stands");
                return false;
            }

            onRoad.Group = candidate.Group;
            onRoad.DemandScored = true;
            PrepareFleet(onRoad, facts);
            onRoad.EnabledDemand = RidersAlone(settings, onRoad, pass);
            if (!SettleMode(settings, onRoad, index, facts, stops, pass, out float roadUtilisation, out ModePreset? roadNextUp))
            {
                return false;
            }

            if (requireFit && roadUtilisation > Assumptions.MaxPlannedUtilisation)
            {
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: streets overloaded as a {onRoad.Mode} ({(roadUtilisation * 100f).ToString("F0", CultureInfo.InvariantCulture)} %) — the {candidate.Mode} alignment stands");
                return false;
            }

            bool passes = PassesLineGates(onRoad, index, facts, tally);
            if (requireFit && !passes)
            {
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: the street variant fails the line gates — the {candidate.Mode} alignment stands");
                return false;
            }

            tally.Retraced++;
            if (passes)
            {
                resolved.Add(onRoad);
            }

            DeferredLog.Info(
                $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {(requireFit ? $"a {onRoad.Mode} along streets carries it at {(roadUtilisation * 100f).ToString("F0", CultureInfo.InvariantCulture)} %; the {candidate.Mode} alignment is not needed" : $"also offered along streets as a {onRoad.Mode}")} " +
                $"({onRoad.Stops.Count} stops, len={(onRoad.Length).ToString("F0", CultureInfo.InvariantCulture)}m, riders/day alone={(onRoad.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)})");
            OfferNextUp(settings, onRoad, index, facts, stops, resolved, pass, roadNextUp, roadUtilisation);
            return true;
        }

        // The next mode up as its own candidate, when the set could plausibly overload
        // this one: past half its seats alone.
        private void OfferNextUp(Setting settings, SuggestedRoute route, int index, FleetFacts facts, StopContext stops, List<SuggestedRoute> resolved, RoutePass pass, ModePreset? nextUp, float utilisation)
        {
            if (nextUp is not ModePreset larger || utilisation < Assumptions.MaxPlannedUtilisation * 0.5f)
            {
                return;
            }

            SuggestedRoute variant = route.CopyFor(larger);
            SuitabilityRoutes.Restop(variant, larger, stops);
            PrepareFleet(variant, facts);
            variant.EnabledDemand = RidersAlone(settings, variant, pass);
            if (TransitModes.ChooseMode(variant.Network, variant.EnabledDemand, facts, m => RoundTripSecondsOf(variant, m, facts), out ModePreset settled, out FleetPlan fleet) && settled == larger)
            {
                ApplyFleet(variant, fleet, facts);
            }
            DeferredLog.Info(
                $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: also offered as a {larger} " +
                $"({variant.Stops.Count} stops, riders/day alone={(variant.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)})");
            if (PassesLineGates(variant, index, facts, new RejectionTally()))
            {
                resolved.Add(variant);
            }
        }

        // Two candidates with the same mode and the same stops (within the stop merge
        // distance) evaluate identically and could never both be in a feasible set;
        // the second only widens the search. Retraces of neighbouring rail pairs along
        // the same streets produce them by the dozen.
        private static int DropIdenticalCandidates(List<SuggestedRoute> resolved)
        {
            const float sameSq = Assumptions.IdenticalCandidateStopMetres * Assumptions.IdenticalCandidateStopMetres;
            int dropped = 0;
            for (int i = resolved.Count - 1; i >= 1; i--)
            {
                SuggestedRoute a = resolved[i];
                for (int j = 0; j < i; j++)
                {
                    SuggestedRoute b = resolved[j];
                    if (a.Mode != b.Mode || a.Stops.Count != b.Stops.Count)
                    {
                        continue;
                    }

                    bool same = true;
                    for (int k = 0; k < a.Stops.Count && same; k++)
                    {
                        same = float2Like.DistanceSq(a.Stops[k], b.Stops[k]) <= sameSq;
                    }

                    if (same)
                    {
                        resolved.RemoveAt(i);
                        dropped++;
                        break;
                    }
                }
            }

            return dropped;
        }

        // The round trip a candidate would take as a given mode: the directed street
        // legs out and back where the streets have them (the return may take other
        // streets than the outward leg), the loop at cruise speed otherwise, plus the
        // dwell at every call each way — the game's stableDuration for a line that does
        // not exist yet.
        private float RoundTripSecondsOf(SuggestedRoute route, ModePreset mode, FleetFacts facts)
        {
            float delay = facts.DelayPerStopSeconds(mode);
            float speed = Assumptions.CruiseSpeedFor(mode);
            if (route.Network != RouteNetwork.Road || m_RoadGraph.Directed is null || route.Stops.Count < 2)
            {
                return TransitModes.RoundTripSeconds(route.Length * 2f, route.Stops.Count * 2, speed, delay);
            }

            double driving = 0.0;
            for (int i = 1; i < route.Stops.Count; i++)
            {
                driving += LegSeconds(route.Stops[i - 1], route.Stops[i], speed);
                driving += LegSeconds(route.Stops[i], route.Stops[i - 1], speed);
            }

            return (float)driving + (route.Stops.Count * 2 * delay);
        }

        // The fleet a candidate would start with as the player builds it — the prefab
        // interval within the game's span — before any rider has been counted. What the
        // probe routes against; SettleMode then sizes the fleet to the riders.
        private void PrepareFleet(SuggestedRoute route, FleetFacts facts)
        {
            route.RoundTripSeconds = RoundTripSecondsOf(route, route.Mode, facts);
            facts.FleetSpanFor(route.Mode, route.RoundTripSeconds, out route.FleetMin, out route.FleetMax);
            int atPrefab = TransitModes.GameFleet(facts.PrefabIntervalFor(route.Mode), route.RoundTripSeconds);
            route.Vehicles = TransitModes.Clamp(atPrefab, route.FleetMin, route.FleetMax);
            route.HeadwaySeconds = TransitModes.GameInterval(route.RoundTripSeconds, route.Vehicles);
        }

        private void ApplyFleet(SuggestedRoute route, FleetPlan fleet, FleetFacts facts)
        {
            route.RoundTripSeconds = RoundTripSecondsOf(route, route.Mode, facts);
            route.Vehicles = fleet.Vehicles;
            route.HeadwaySeconds = fleet.HeadwaySeconds;
            route.FleetMin = fleet.Min;
            route.FleetMax = fleet.Max;
        }

        // Chooses the mode from the route's own riders, re-placing its stops for the
        // mode and re-measuring once, since stops and riders depend on each other.
        // False when no vehicle of any mode on the network is installed. The route
        // leaves with the fleet the ladder sized within the game's span (A5.5/A6.8).
        private bool SettleMode(Setting settings, SuggestedRoute route, int index, FleetFacts facts, StopContext stops, RoutePass pass, out float utilisation, out ModePreset? nextUp)
        {
            nextUp = null;
            utilisation = 0f;
            float RoundTripFor(ModePreset m) => RoundTripSecondsOf(route, m, facts);
            if (!TransitModes.ChooseMode(route.Network, route.EnabledDemand, facts, RoundTripFor, out ModePreset mode, out FleetPlan fleet))
            {
                DeferredLog.Info(
                    $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {route.Network}, riders/day={(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)} — DROPPED, no vehicle of any {route.Network} mode is installed");
                return false;
            }

            if (mode != route.Mode)
            {
                SuitabilityRoutes.Restop(route, mode, stops);
                PrepareFleet(route, facts);
                route.EnabledDemand = RidersAlone(settings, route, pass);
                if (TransitModes.ChooseMode(route.Network, route.EnabledDemand, facts, RoundTripFor, out ModePreset again, out fleet) && again != mode)
                {
                    mode = again;
                    SuitabilityRoutes.Restop(route, mode, stops);
                    PrepareFleet(route, facts);
                    route.EnabledDemand = RidersAlone(settings, route, pass);
                    _ = TransitModes.ChooseMode(route.Network, route.EnabledDemand, facts, RoundTripFor, out _, out fleet);
                }
            }

            ApplyFleet(route, fleet, facts);
            utilisation = fleet.Utilisation;

            ModePreset[] ladder = TransitModes.ModesFor(route.Network);
            int rung = Array.IndexOf(ladder, mode);
            for (int i = rung + 1; i < ladder.Length; i++)
            {
                if (facts.CapacityFor(ladder[i]) > 0f)
                {
                    nextUp = ladder[i];
                    break;
                }
            }

            DeferredLog.Info(
                $"  candidate {(index).ToString(CultureInfo.InvariantCulture)}: {route.Network} -> {mode}{(route.BentThroughHub ? " via an interchange" : string.Empty)}, " +
                $"riders/day alone={(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, fleet {(fleet.Vehicles).ToString(CultureInfo.InvariantCulture)} of [{(fleet.Min).ToString(CultureInfo.InvariantCulture)}, {(fleet.Max == int.MaxValue ? "?" : fleet.Max.ToString(CultureInfo.InvariantCulture))}] " +
                $"(round trip {(route.RoundTripSeconds).ToString("F0", CultureInfo.InvariantCulture)} s -> interval {(fleet.HeadwaySeconds).ToString("F0", CultureInfo.InvariantCulture)} s), " +
                $"utilisation alone={(utilisation * 100f).ToString("F1", CultureInfo.InvariantCulture)} %, " +
                $"{route.Stops.Count} stops, len={(route.Length).ToString("F0", CultureInfo.InvariantCulture)}m");
            return true;
        }

        // The journey weight that would ride this line on its own against the existing
        // network (the same evaluation WeighCandidatesAlone makes for the pool).
        private float RidersAlone(Setting settings, SuggestedRoute route, RoutePass pass)
        {
            if (route.Stops.Count < 2)
            {
                return 0f;
            }

            LineSetProblem probe = BuildLineSetProblem(settings, new List<SuggestedRoute> { route }, 1, pass);
            float[] before = pass.Baseline ??= SuitabilityLineSet.Evaluate(probe, Array.Empty<int>(), 0, before: null).After;
            return (float)SuitabilityLineSet.Evaluate(probe, s_OnlyCandidate, 1, before).Riders[0];
        }

        private static readonly int[] s_OnlyCandidate = { 0 };
    }
}
