using System.Collections.Generic;
using System.Globalization;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // The networks an alignment may run on: the street graph and the three lattices,
    // the track masks that price them, and the directed stop-to-stop driving times.
    public sealed partial class StationSuitabilityOverlaySystem
    {
        private ComponentLookup<Node> m_NodeLookup;

        private ComponentLookup<Curve> m_CurveLookup;

        private ComponentLookup<PrefabRef> m_PrefabRefLookup;

        private ComponentLookup<RoadData> m_RoadDataLookup;

        private readonly AlignmentNetwork m_RoadGraph = new AlignmentNetwork();

        // Rail and water get their own free-form networks: a metro tunnel or a ferry
        // crossing cannot be expressed on the street graph at all. Train and metro
        // share the lattice shape but not its costs — train reuses existing track
        // wherever it can, metro prefers fresh alignment.
        // Two rail lattices: trains are cheap along existing train track, metros along
        // existing metro track (register A4.5: the same bonus, each for its own kind —
        // a metro cannot run on train track).
        private readonly AlignmentNetwork m_TrainNetwork = new AlignmentNetwork();

        private readonly AlignmentNetwork m_MetroNetwork = new AlignmentNetwork();

        private readonly AlignmentNetwork m_WaterNetwork = new AlignmentNetwork();

        private EntityQuery m_AllEdgeQuery;

        private byte[]? m_TrackMask;

        private readonly List<float2Like> m_TrackStarts = new List<float2Like>();

        private readonly List<float2Like> m_TrackEnds = new List<float2Like>();

        private readonly List<float2Like> m_MetroTrackStarts = new List<float2Like>();

        private readonly List<float2Like> m_MetroTrackEnds = new List<float2Like>();

        private byte[]? m_MetroTrackMask;

        private int[]? m_ZoneNodes;

        private bool m_GraphDirty = true;

        // The alignment a mode may run on. Buses and trams are stuck with streets,
        // metro and train lay their own, ferries need water.
        private AlignmentNetwork NetworkForMode(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Train: return m_TrainNetwork;
                case ModePreset.Metro: return m_MetroNetwork;
                case ModePreset.Ferry: return m_WaterNetwork;
                default: return m_RoadGraph;
            }
        }

        // Builds all four networks. The rail lattices differ only in how much they
        // discount running along track that already exists.
        private void BuildNetworks(int2 gridSize, float2 worldMin)
        {
            m_NodeLookup.Update(this);
            m_CurveLookup.Update(this);
            m_PrefabRefLookup.Update(this);
            m_RoadDataLookup.Update(this);

            SuitabilityRoads.Build(m_RoadGraph, EntityManager, m_RoadEdgeQuery, m_NodeLookup, m_CurveLookup,
                m_PrefabRefLookup, m_RoadDataLookup);
            m_RoadLegs.Clear();
            m_RoadLegsDropped = 0;
            var zoneGrid = new int2Like(m_ZoneGrid.x, m_ZoneGrid.y);
            var origin = new float2Like(worldMin.x, worldMin.y);
            var tileGrid = new int2Like(gridSize.x, gridSize.y);
            m_ZoneNodes = m_RoadGraph.MapZonesToNodes(zoneGrid, origin);

            int cells = gridSize.x * gridSize.y;
            if (m_MetroTrackMask is null || m_MetroTrackMask.Length != cells)
            {
                m_MetroTrackMask = new byte[cells];
            }

            if (m_TrackMask is null || m_TrackMask.Length != cells)
            {
                m_TrackMask = new byte[cells];
            }

            SuitabilityRoads.CollectTrackSegments(EntityManager, m_AllEdgeQuery, m_NodeLookup, m_TrackStarts, m_TrackEnds, m_MetroTrackStarts, m_MetroTrackEnds);
            SuitabilityLattice.RasterizeTracks(m_TrackStarts, m_TrackEnds, tileGrid, origin, TileSize, m_TrackMask);
            SuitabilityLattice.RasterizeTracks(m_MetroTrackStarts, m_MetroTrackEnds, tileGrid, origin, TileSize, m_MetroTrackMask);

            bool LandTile(int tile) => m_Land is not null && tile < m_Land.Length && m_Land[tile] != 0;
            bool WaterTile(int tile) => m_Land is not null && tile < m_Land.Length && m_Land[tile] == 0;
            bool OnTrack(int tile) => m_TrackMask is not null && tile < m_TrackMask.Length && m_TrackMask[tile] != 0;
            bool OnMetroTrack(int tile) => m_MetroTrackMask is not null && tile < m_MetroTrackMask.Length && m_MetroTrackMask[tile] != 0;

            CompactGraph trainGraph = SuitabilityLattice.Build(tileGrid, origin, TileSize, LandTile,
                tile => SuitabilityLattice.RailCostScale(OnTrack(tile)),
                out float[] trainX, out float[] trainZ);
            m_TrainNetwork.Adopt(trainGraph, trainX, trainZ, RouteNetwork.Rail);

            CompactGraph metroGraph = SuitabilityLattice.Build(tileGrid, origin, TileSize, LandTile,
                tile => SuitabilityLattice.RailCostScale(OnMetroTrack(tile)),
                out float[] metroX, out float[] metroZ);
            m_MetroNetwork.Adopt(metroGraph, metroX, metroZ, RouteNetwork.Metro);

            CompactGraph waterGraph = SuitabilityLattice.Build(tileGrid, origin, TileSize, WaterTile,
                tileCostScale: null, out float[] waterX, out float[] waterZ);
            m_WaterNetwork.Adopt(waterGraph, waterX, waterZ, RouteNetwork.Water);

            m_GraphDirty = false;
            DeferredLog.Info(
                $"Networks built: road {(m_RoadGraph.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_RoadGraph.EdgeCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"train lattice {(m_TrainNetwork.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_TrainNetwork.EdgeCount).ToString(CultureInfo.InvariantCulture)} on {(m_TrackStarts.Count).ToString(CultureInfo.InvariantCulture)} train track segments, " +
                $"metro lattice {(m_MetroNetwork.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_MetroNetwork.EdgeCount).ToString(CultureInfo.InvariantCulture)} on {(m_MetroTrackStarts.Count).ToString(CultureInfo.InvariantCulture)} metro track segments, " +
                $"water {(m_WaterNetwork.NodeCount).ToString(CultureInfo.InvariantCulture)}/{(m_WaterNetwork.EdgeCount).ToString(CultureInfo.InvariantCulture)}, " +
                $"trackSegments={m_TrackStarts.Count}; directed road arcs {(m_RoadGraph.Directed?.ArcCount ?? 0).ToString(CultureInfo.InvariantCulture)}, " +
                $"one-way streets {(m_RoadGraph.OneWayEdges).ToString(CultureInfo.InvariantCulture)}, " +
                $"streets without a car lane {(m_RoadGraph.EdgesWithoutCarLane).ToString(CultureInfo.InvariantCulture)}, " +
                $"turn cost {(m_RoadGraph.TurnSecondsPerRadian).ToString("F2", CultureInfo.InvariantCulture)} s/rad from the car pathfind prefab");
        }

        private void AssignLatticeFlow(AlignmentNetwork network, float2 worldMin, List<ZoneFlow> flows)
        {
            if (network.Graph is null || network.NodeCount == 0 || flows.Count == 0)
            {
                return;
            }

            int[] zoneNodes = network.MapZonesToNodes(new int2Like(m_ZoneGrid.x, m_ZoneGrid.y), new float2Like(worldMin.x, worldMin.y));
            _ = network.AssignFlow(flows, zoneNodes, 30000f, out float _);
        }

        // Stops on the road network sit on or beside a road node; further than this
        // the stop is not on the street it was placed along.
        private const float StopNodeSnapMetres = 64f;

        // Driving time into each stop of a road route from the stop before it, along
        // the fastest DIRECTED path with the street's speed limits and turn costs
        // (register A0.7/A0.8). Index i is the ride into stop i; 0 where no directed
        // path exists, which SuitabilityTransit reads as "fall back to distance over
        // cruise speed" — and which is logged, because a stop pair with no drivable
        // path between them is a line the game cannot run either. Null for lattice
        // routes, whose alignments have no streets.
        private float[]? RoadRideSeconds(SuggestedRoute route)
        {
            if (route.Network != RouteNetwork.Road || m_RoadGraph.Directed is null || route.Stops.Count < 2)
            {
                return null;
            }

            var seconds = new float[route.Stops.Count];
            int unreachable = 0;
            for (int i = 1; i < route.Stops.Count; i++)
            {
                long ms = RoadLegMs(route.Stops[i - 1], route.Stops[i]);
                if (ms == DirectedDijkstra.Unreached)
                {
                    unreachable++;
                    continue;
                }

                seconds[i] = ms / 1000f;
            }

            if (unreachable > 0)
            {
                DeferredLog.Warn(
                    $"Road route {route.Mode} with {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops: " +
                    $"{(unreachable).ToString(CultureInfo.InvariantCulture)} stop-to-stop legs have no drivable directed path (one-way streets?); cruise-speed fallback used for them.");
            }

            return seconds;
        }

        // Stops are points along a street, not its ends: each is projected onto the
        // nearest arc within the snap distance and timed from there (RoadLegs).
        private long RoadLegMs(float2Like from, float2Like to)
        {
            long ms = m_RoadGraph.PointLegMs(from, to, StopNodeSnapMetres, (long)ServedDemand.MaxJourneySeconds * 1000L, out RoadLeg leg);
            RememberRoadLeg(from, to, ms, leg);
            return ms;
        }

        // The stop-to-stop legs the last route pass asked the directed graph for, with
        // the answers it got: what the export hands the pipeline to certify. Bounded,
        // and reset whenever the graph is rebuilt so no leg outlives its graph.
        private const int MaxRememberedRoadLegs = 400;

        private readonly List<(float2Like from, float2Like to, long ms, RoadLeg leg)> m_RoadLegs = new List<(float2Like, float2Like, long, RoadLeg)>();

        private int m_RoadLegsDropped;

        private void RememberRoadLeg(float2Like from, float2Like to, long ms, RoadLeg leg)
        {
            for (int i = 0; i < m_RoadLegs.Count; i++)
            {
                if (m_RoadLegs[i].from.Equals(from) && m_RoadLegs[i].to.Equals(to))
                {
                    return;
                }
            }

            if (m_RoadLegs.Count >= MaxRememberedRoadLegs)
            {
                m_RoadLegsDropped++;
                return;
            }

            m_RoadLegs.Add((from, to, ms, leg));
        }

        // Out and back over the directed network — the return leg may take other
        // streets than the outward one — plus a dwell at every call each way. Falls
        // back to the cruise-speed estimate where a leg has no directed path.
        private int RoadVehicles(SuggestedRoute route, float headwaySeconds, float delayPerStopSeconds)
        {
            if (route.Network != RouteNetwork.Road || m_RoadGraph.Directed is null || route.Stops.Count < 2)
            {
                return SuitabilityRoutes.EstimateVehicles(route.Mode, route.Length, route.Stops.Count, headwaySeconds, delayPerStopSeconds);
            }

            float speed = TransitModes.CruiseSpeedFor(route.Mode);
            double roundTrip = 0.0;
            for (int i = 1; i < route.Stops.Count; i++)
            {
                roundTrip += LegSeconds(route.Stops[i - 1], route.Stops[i], speed);
                roundTrip += LegSeconds(route.Stops[i], route.Stops[i - 1], speed);
            }

            return SuitabilityRoutes.EstimateVehiclesFromRoundTrip((float)roundTrip, route.Stops.Count, headwaySeconds, delayPerStopSeconds);
        }

        private double LegSeconds(float2Like from, float2Like to, float cruiseSpeed)
        {
            long ms = RoadLegMs(from, to);
            return ms == DirectedDijkstra.Unreached
                ? float2Like.Distance(from, to) / math.max(1f, cruiseSpeed)
                : ms / 1000.0;
        }
    }
}
