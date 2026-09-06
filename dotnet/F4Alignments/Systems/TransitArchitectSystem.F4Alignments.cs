using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // F4 — the alignment search on the worker: journeys assigned to every network,
    // corridors grown on the streets and direct traces on the lattices, gathered as
    // the candidate pool the mode ladder and the set selection then work through.
    public sealed partial class TransitArchitectSystem
    {

        // Each network contributes candidates for the modes it can carry; the merged
        // set is ranked by trips carried and the best kept. Auto-assignment therefore
        // falls out of which network won, rather than being guessed after the fact.
        private void BuildRoutes(Setting settings, int2 gridSize, float2 worldMin, RoutePass pass)
        {
            var objective = (RouteObjective)settings.Objective;
            List<SuggestedRoute> candidates = pass.Candidates;
            candidates.Clear();

            // Journeys onto the networks by shortest path (A3.3: this only seeds the
            // corridor search). Generous cost ceiling: a trip longer than this is not
            // a candidate for a single transit line anyway.
            var assigning = System.Diagnostics.Stopwatch.StartNew();
            int[]? zoneNodes = m_ZoneNodes;
            if (zoneNodes is not null)
            {
                pass.AssignedPairs = m_RoadGraph.AssignFlow(m_ZoneFlows, zoneNodes, 20000f, out float assignedWeight);
                pass.AssignedWeight = assignedWeight;
            }

            AssignLatticeFlow(m_TrainNetwork, worldMin, m_ZoneFlows);
            AssignLatticeFlow(m_MetroNetwork, worldMin, m_ZoneFlows);
            // Every journey is offered to the water lattice too (register A3.4): a
            // ferry along a coast is judged like any other line, by the time it saves
            // and the seats it fills, not by whether the ends share a landmass.
            AssignLatticeFlow(m_WaterNetwork, worldMin, m_ZoneFlows);
            pass.AssignMs = assigning.ElapsedMilliseconds;

            float[]? roadDemand = BuildNodeDemand(m_RoadGraph, gridSize);

            const float demandFloor = Assumptions.CorridorDemandFloor;

            int grownTotal = 0;
            int shortTotal = 0;

            var phases = System.Diagnostics.Stopwatch.StartNew();
            StopContext stops = BuildStopContext();
            Routes.BuildForNetwork(m_RoadGraph, objective, settings.RouteCount,
                Assumptions.RoadFlowFraction, TransitModes.MaxAlignmentMetresFor(RouteNetwork.Road), roadDemand, demandFloor, forcedMode: null, candidates,
                stops, out int g1, out int s1, out int h1);

            // The lattices connect two places rather than following a flow ridge — see
            // BuildDirectForNetwork for why growth is the wrong instrument on a uniform
            // grid. Trains and metros each trace on their own lattice (A4.5); the water
            // lattice sees every journey (A3.4).
            var zoneGrid = new int2Like(m_ZoneGrid.x, m_ZoneGrid.y);
            var origin = new float2Like(worldMin.x, worldMin.y);
            Routes.BuildDirectForNetwork(m_TrainNetwork, m_ZoneFlows,
                m_TrainNetwork.MapZonesToNodes(zoneGrid, origin), settings.RouteCount,
                TransitModes.MaxAlignmentMetresFor(RouteNetwork.Rail), ModePreset.Train, candidates,
                stops, out int g2, out int s2, out int h2);

            Routes.BuildDirectForNetwork(m_MetroNetwork, m_ZoneFlows,
                m_MetroNetwork.MapZonesToNodes(zoneGrid, origin), settings.RouteCount,
                TransitModes.MaxAlignmentMetresFor(RouteNetwork.Metro), ModePreset.Metro, candidates,
                stops, out int g3, out int s3, out int h3);

            var shoreline = new StopContext
            {
                Ends = stops.Ends,
                EndWeights = stops.EndWeights,
                Facts = stops.Facts,
                Hubs = stops.Hubs,
                ScoreAt = (point, _) => ShorelineScoreAt(new float2(point.x, point.y), gridSize),
            };
            Routes.BuildDirectForNetwork(m_WaterNetwork, m_ZoneFlows,
                m_WaterNetwork.MapZonesToNodes(zoneGrid, origin), settings.RouteCount,
                TransitModes.MaxAlignmentMetresFor(RouteNetwork.Water), ModePreset.Ferry, candidates,
                shoreline, out int g4, out int s4, out int h4);

            grownTotal = g1 + g2 + g3 + g4;
            shortTotal = s1 + s2 + s3 + s4;

            DeferredLog.Info(
                $"Candidates by network: road grown={(g1).ToString(CultureInfo.InvariantCulture)} tooShort={(s1).ToString(CultureInfo.InvariantCulture)}, " +
                $"train pairs tried={(g2).ToString(CultureInfo.InvariantCulture)} tooShort={(s2).ToString(CultureInfo.InvariantCulture)}, " +
                $"metro pairs tried={(g3).ToString(CultureInfo.InvariantCulture)} tooShort={(s3).ToString(CultureInfo.InvariantCulture)}, " +
                $"ferry pairs tried={(g4).ToString(CultureInfo.InvariantCulture)} tooShort={(s4).ToString(CultureInfo.InvariantCulture)}, " +
                $"termini aimed at an interchange={(h1 + h2 + h3 + h4).ToString(CultureInfo.InvariantCulture)} (road {(h1).ToString(CultureInfo.InvariantCulture)}) of {(m_Interchanges.Count).ToString(CultureInfo.InvariantCulture)} served stops, " +
                $"tooShort = fewer than {(Assumptions.MinStops).ToString(CultureInfo.InvariantCulture)} stops; " +
                $"ride limits (min): bus {(Assumptions.MaxRideSecondsFor(ModePreset.Bus) / 60f).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"tram {(Assumptions.MaxRideSecondsFor(ModePreset.Tram) / 60f).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"metro {(Assumptions.MaxRideSecondsFor(ModePreset.Metro) / 60f).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"train {(Assumptions.MaxRideSecondsFor(ModePreset.Train) / 60f).ToString("F0", CultureInfo.InvariantCulture)} " +
                $"ferry {(Assumptions.MaxRideSecondsFor(ModePreset.Ferry) / 60f).ToString("F0", CultureInfo.InvariantCulture)}");

            // Corridor flow is all there is to rank by until the routing pass runs, and
            // it decides which candidates are inside the scoring window. Enabled demand
            // takes over from there, and is re-measured once per accepted suggestion.
            candidates.Sort(static (a, b) => b.CapturedFlow.CompareTo(a.CapturedFlow));
            pass.AlignmentMs = phases.ElapsedMilliseconds;
            SelectRoutes(settings, gridSize, grownTotal, shortTotal, pass);
        }
    }
}
