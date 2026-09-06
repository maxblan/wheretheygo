using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationSuitabilityOverlay.Tests
{
    // The alignment stage (F4) and stop placement (F5) offline. The synthetic city
    // below is the one the conversion to float2Like was characterized on: the
    // game-typed code and the pure code produced identical bits for every value of
    // it (82,171 of them), and the figures pinned here are a sample of that record.
    internal static partial class Program
    {
        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

        private static void AssertBits(uint expected, float actual, string because)
        {
            if (Bits(actual) != unchecked((int)expected))
            {
                throw new TestFailedException(
                    $"{because}: expected bits {expected.ToString("X8", CultureInfo.InvariantCulture)} ({BitConverter.Int32BitsToSingle(unchecked((int)expected)).ToString("R", CultureInfo.InvariantCulture)}), " +
                    $"got {Bits(actual).ToString("X8", CultureInfo.InvariantCulture)} ({actual.ToString("R", CultureInfo.InvariantCulture)})");
            }
        }

        private static void AssertBits(ulong expected, double actual, string because)
        {
            if (BitConverter.DoubleToInt64Bits(actual) != unchecked((long)expected))
            {
                throw new TestFailedException(
                    $"{because}: expected bits {expected.ToString("X16", CultureInfo.InvariantCulture)}, got {BitConverter.DoubleToInt64Bits(actual).ToString("X16", CultureInfo.InvariantCulture)} ({actual.ToString("R", CultureInfo.InvariantCulture)})");
            }
        }

        private static void Float2LikeMatchesUnityFormulas()
        {
            // Values from Unity.Mathematics.dll run on the same inputs.
            var a = new float2Like(1.5f, 2.25f);
            var b = new float2Like(-3.1f, 7.7f);
            AssertBits(unchecked((uint)Bits(7.1317945f)), float2Like.Distance(a, b), "distance");
            float2Like lerp = float2Like.Lerp(a, b, 0.3f);
            AssertBits(unchecked((uint)Bits(0.120000005f)), lerp.x, "lerp.x");
            AssertBits(unchecked((uint)Bits(3.885f)), lerp.y, "lerp.y");
            AssertTrue(float2Like.DistanceSq(a, b) == float2Like.LengthSq(b - a), "distancesq is lengthsq of the difference");
            AssertTrue(float2Like.Dot(a, b) == ((1.5f * -3.1f) + (2.25f * 7.7f)), "dot is the left-to-right sum");
            AssertTrue((a * 2f) == (2f * a) && (a * 2f).x == 3f && (a / 2f).y == 1.125f, "scalar operators");
            AssertTrue((-a).x == -1.5f && (a + b - b) == a, "unary minus, add and subtract");
        }

        private static void TileGridClampsAndInverts()
        {
            var worldMin = new float2Like(-2048f, -2048f);
            int2Like dims = TileGrid.GridDims(new float2Like(4096f, 4000f), 32f);
            AssertTrue(dims.x == 128 && dims.y == 125, "dims round up");
            AssertTrue(TileGrid.GridDims(new float2Like(0f, 0f), 32f) == new int2Like(1, 1), "a degenerate size still has one cell");
            int2Like inside = TileGrid.WorldToCell(new float2Like(-2047f, 1000f), worldMin, 32f, dims);
            AssertTrue(inside.x == 0 && inside.y == 95, "cell of a point inside");
            int2Like outside = TileGrid.WorldToCell(new float2Like(9000f, -9000f), worldMin, 32f, dims);
            AssertTrue(outside.x == 127 && outside.y == 0, "points off the map clamp to the edge cells");
            float2Like centre = TileGrid.CellCentre(inside, worldMin, 32f);
            AssertTrue(TileGrid.WorldToCell(centre, worldMin, 32f, dims) == inside, "a cell's centre maps back to the cell");
            AssertTrue(centre.x == -2032f && centre.y == 1008f, "centre is half a cell in");
        }

        private static void ZonesAggregateAndRasterize()
        {
            var worldMin = new float2Like(0f, 0f);
            var grid = new int2Like(4, 4);
            var trips = new List<Trip>
            {
                new Trip { m_Origin = new float2Like(100f, 100f), m_Destination = new float2Like(900f, 100f), m_Weight = 2f, m_DayShare = 1f },
                new Trip { m_Origin = new float2Like(900f, 900f), m_Destination = new float2Like(100f, 100f), m_Weight = 1f, m_DayShare = 0f },
                new Trip { m_Origin = new float2Like(120f, 120f), m_Destination = new float2Like(910f, 110f), m_Weight = 3f, m_DayShare = 1f },
                new Trip { m_Origin = new float2Like(100f, 100f), m_Destination = new float2Like(200f, 200f), m_Weight = 5f, m_DayShare = 1f },
                new Trip { m_Origin = new float2Like(-5f, 100f), m_Destination = new float2Like(900f, 100f), m_Weight = 7f, m_DayShare = 1f },
            };
            var flows = new List<ZoneFlow>();
            var journeys = new List<Trip>();
            float total = SuitabilityZones.Aggregate(trips, worldMin, grid, flows, out int count, journeys);
            AssertTrue(count == 3 && total == 6f, $"three trips survive (self-zone and off-map dropped): count {count.ToString(CultureInfo.InvariantCulture)}, weight {total.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(journeys.Count == 3, "the surviving trips are handed back as journeys");
            AssertTrue(flows.Count == 2, "two zone pairs");
            AssertTrue(flows[0].m_Origin == 0 && flows[0].m_Destination == 3 && flows[0].m_Weight == 5f, "pair 0->3 sums both trips' weights");
            AssertTrue(flows[1].m_Origin == 15 && flows[1].m_Destination == 0 && flows[1].m_Weight == 1f, "pairs come out in (origin, destination) order");
            AssertTrue(SuitabilityZones.ZoneOf(new float2Like(1023f, 1023f), worldMin, grid) == 15 && SuitabilityZones.ZoneOf(new float2Like(1024f, 0f), worldMin, grid) == -1, "zone index and the open upper edge");
            float2Like centre = SuitabilityZones.ZoneCentre(5, worldMin, grid);
            AssertTrue(centre.x == 384f && centre.y == 384f, "zone centre");

            var raster = new float[8 * 8];
            SuitabilityZones.RasterizeDesireLines(flows, worldMin, grid, new int2Like(8, 8), 128f, raster);
            float deposited = 0f;
            for (int i = 0; i < raster.Length; i++)
            {
                deposited += raster[i];
            }

            AssertTrue(deposited > 0f && raster[0] == 0f, "desire lines deposit weight along the line between the zone centres, not at the corner");
        }

        private static void NetworkAdoptsStreetsWithShapes()
        {
            // Three nodes in a row; the middle edge bends (two sampled points), the first is straight.
            float[] nodeX = { 0f, 100f, 200f };
            float[] nodeZ = { 0f, 0f, 0f };
            int[] edgeA = { 0, 1 };
            int[] edgeB = { 1, 2 };
            float[] cost = { 100f, 110f };
            bool[] noStops = { false, true };
            float[] shapeX = { 130f, 170f };
            float[] shapeZ = { 20f, 20f };
            int[] shapeStart = { 0, 0 };
            int[] shapeCount = { 0, 2 };
            var arcs = new AlignmentNetwork.ArcBuilder();
            arcs.Add(0, 1, 0, 100f, 10f, 1f, 0f, 1f, 0f);
            arcs.Add(1, 0, 0, 100f, 10f, -1f, 0f, -1f, 0f);
            arcs.Add(1, 2, 1, 110f, 5f, 1f, 0f, 1f, 0f);
            var network = new AlignmentNetwork();
            network.AdoptRoads(nodeX, nodeZ, edgeA, edgeB, cost, noStops, shapeX, shapeZ, shapeStart, shapeCount, arcs, 0f, 1, 0);

            AssertTrue(network.Network == RouteNetwork.Road && network.NodeCount == 3 && network.EdgeCount == 2, "graph adopted");
            AssertTrue(network.Directed is not null && network.Directed.ArcCount == 3 && network.OneWayEdges == 1, "directed arcs adopted, one-way street counted");
            AssertTrue(network.TurnSecondsPerRadian == 2f, "no pathfind prefab named a rate, so the CarPathfind default holds");
            AssertTrue(network.EdgeCannotHostStops is not null && network.EdgeCannotHostStops[1], "the highway flag is carried");

            var nodes = new List<int>();
            AssertTrue(network.TracePath(0, 2, 1000f, nodes) && nodes.Count == 3, "trace along the row");
            var path = new List<float2Like>();
            network.MaterialisePath(nodes, path);
            AssertTrue(path.Count == 5 && path[2].x == 130f && path[3].x == 170f, "the bend's two samples are inserted in A->B order");
            network.MaterialisePath(new List<int> { 2, 1, 0 }, path);
            AssertTrue(path.Count == 5 && path[1].x == 170f && path[2].x == 130f, "walked the other way the samples reverse");

            AssertTrue(network.PointLegMs(new float2Like(10f, 0f), new float2Like(190f, 0f), 64f, 3_600_000L, out RoadLeg leg) > 0 && !leg.SameArc, "a point-to-point leg drives the two arcs");
            AssertTrue(network.PointLegMs(new float2Like(190f, 0f), new float2Like(150f, 0f), 64f, 3_600_000L, out _) == DirectedDijkstra.Unreached, "against the one-way street there is no leg");
            network.EdgeFlow[0] = 10f;
            network.EdgeFlow[1] = 20f;
            AssertBits(unchecked((uint)Bits((10f * 100f + 20f * 110f) / 210f)), network.FlowAlong(nodes), "flow along is length-weighted");
            AssertTrue(network.FlowNear(nodes, new float2Like(150f, 30f)) == 20f, "flow near a point is the nearest edge's");
        }

        private static void LatticeBuildsAndPricesTrack()
        {
            // 8x8 tiles of 32 m = 256 m: a 2x2 lattice at 128 m pitch.
            var grid = new int2Like(8, 8);
            var worldMin = new float2Like(0f, 0f);
            var trackMask = new byte[64];
            SuitabilityLattice.RasterizeTracks(new List<float2Like> { new float2Like(128f, 64f) }, new List<float2Like> { new float2Like(128f, 64f) }, grid, worldMin, 32f, trackMask);
            int marked = 0;
            for (int i = 0; i < trackMask.Length; i++)
            {
                marked += trackMask[i];
            }

            AssertTrue(marked == 9 && trackMask[4 + (2 * 8)] == 1 && trackMask[3 + (1 * 8)] == 1 && trackMask[2 + (2 * 8)] == 0, "a point marks its tile and a one-tile halo");

            CompactGraph graph = SuitabilityLattice.Build(grid, worldMin, 32f, tile => tile % 8 < 4 || tile / 8 < 4, tile => SuitabilityLattice.RailCostScale(trackMask[tile] != 0), out float[] xs, out float[] zs);
            // Nodes at (64,64) (192,64) (64,192); (192,192) is not passable.
            AssertTrue(graph.NodeCount == 3 && xs[0] == 64f && zs[0] == 64f && xs[1] == 192f && zs[2] == 192f, "nodes only where the mask admits, in row order");
            AssertTrue(graph.EdgeCount == 3, "right and down from the first node, and the rising diagonal from the third to the second");
            float onTrack = 128f * SuitabilityLattice.RailCostScale(onExistingTrack: true);
            float offTrack = 128f * SuitabilityLattice.RailCostScale(onExistingTrack: false);
            AssertTrue(graph.EdgeA[0] == 0 && graph.EdgeB[0] == 1 && graph.EdgeA[2] == 2 && graph.EdgeB[2] == 1, "edge order follows the sweep");
            AssertTrue(graph.EdgeCost[0] == onTrack, "the edge whose midpoint tile carries track is cheap");
            AssertTrue(graph.EdgeCost[1] == offTrack, "the edge off the track pays the full scale");
            AssertBits(unchecked((uint)Bits(128f * 1.41421356f * 1.6f)), graph.EdgeCost[2], "the diagonal off the track pays the full scale on the diagonal length");
            AssertTrue(offTrack > onTrack, "off-track alignment is dearer");
        }

        // ---- the synthetic city (the characterization scenario, verbatim) ----

        private sealed class SyntheticCity
        {
            public readonly Random Rng = new Random(12345);
            public const float Tile = 32f;
            public readonly float2Like WorldMin = new float2Like(-2048f, -2048f);
            public const int GridX = 128;
            public const int GridY = 128;
            public readonly int2Like ZoneGrid = new int2Like(16, 16);
            public readonly AlignmentNetwork Roads = new AlignmentNetwork();
            public readonly List<ZoneFlow> Flows = new List<ZoneFlow>();
            public int[] RoadZoneNodes = Array.Empty<int>();
            public int AssignedPairs;
            public float AssignedWeight;
            public float[] NodeDemand = Array.Empty<float>();
            public StopContext Stops = new StopContext();
            public FleetFacts Facts;
            public readonly List<float> Xs = new List<float>();
            public readonly List<float> Zs = new List<float>();

            private static float NextF(Random rng, float lo, float hi) => lo + ((float)rng.NextDouble() * (hi - lo));

            private static float ScoreAt(float2Like point, ModePreset mode)
            {
                int cx = (int)Math.Floor(point.x / 64f);
                int cz = (int)Math.Floor(point.y / 64f);
                uint h;
                unchecked
                {
                    h = (uint)(cx * 73856093) ^ (uint)(cz * 19349663) ^ (uint)((int)mode * 83492791);
                    h ^= h >> 13;
                    h *= 0x5bd1e995u;
                    h ^= h >> 15;
                }

                float u = (h & 0xFFFFFF) / 16777216f;
                return u < 0.15f ? 0f : (u - 0.15f) / 0.85f;
            }

            public bool Land(int tileIndex)
            {
                int tx = tileIndex % GridX;
                int ty = tileIndex / GridX;
                float x = WorldMin.x + ((tx + 0.5f) * Tile);
                float z = WorldMin.y + ((ty + 0.5f) * Tile);
                float d1 = ((x + 600f) * (x + 600f)) + ((z - 300f) * (z - 300f));
                float d2 = ((x - 900f) * (x - 900f)) + ((z + 700f) * (z + 700f));
                return d1 > 500f * 500f && d2 > 400f * 400f;
            }

            public SyntheticCity()
            {
                Random rng = Rng;
                const int n = 14;
                const float pitch = 260f;
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        Xs.Add(-1800f + (i * pitch) + NextF(rng, -40f, 40f));
                        Zs.Add(-1800f + (j * pitch) + NextF(rng, -40f, 40f));
                    }
                }

                var ea = new List<int>();
                var eb = new List<int>();
                var ec = new List<float>();
                void Link(int a, int b)
                {
                    float dx = Xs[a] - Xs[b];
                    float dz = Zs[a] - Zs[b];
                    ea.Add(a);
                    eb.Add(b);
                    ec.Add(Math.Max(1f, (float)Math.Sqrt((double)((dx * dx) + (dz * dz)))));
                }

                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int a = i + (j * n);
                        if (i + 1 < n && rng.NextDouble() < 0.88)
                        {
                            Link(a, a + 1);
                        }

                        if (j + 1 < n && rng.NextDouble() < 0.88)
                        {
                            Link(a, a + n);
                        }

                        if (i + 1 < n && j + 1 < n && rng.NextDouble() < 0.15)
                        {
                            Link(a, a + n + 1);
                        }
                    }
                }

                Roads.Adopt(CompactGraph.Build(Xs.Count, ea.ToArray(), eb.ToArray(), ec.ToArray(), ea.Count), Xs.ToArray(), Zs.ToArray(), RouteNetwork.Road);

                var totals = new Dictionary<long, float>();
                int zoneCount = ZoneGrid.x * ZoneGrid.y;
                for (int k = 0; k < 90; k++)
                {
                    int a = rng.Next(Xs.Count);
                    int b = rng.Next(Xs.Count);
                    int za = SuitabilityZones.ZoneOf(new float2Like(Xs[a], Zs[a]), WorldMin, ZoneGrid);
                    int zb = SuitabilityZones.ZoneOf(new float2Like(Xs[b], Zs[b]), WorldMin, ZoneGrid);
                    if (za < 0 || zb < 0 || za == zb)
                    {
                        continue;
                    }

                    float w = (float)Math.Round(NextF(rng, 5f, 60f) * 2f, MidpointRounding.ToEven) / 2f;
                    long key = ((long)za * zoneCount) + zb;
                    _ = totals.TryGetValue(key, out float existing);
                    totals[key] = existing + w;
                }

                foreach (KeyValuePair<long, float> pair in totals)
                {
                    Flows.Add(new ZoneFlow { m_Origin = (int)(pair.Key / zoneCount), m_Destination = (int)(pair.Key % zoneCount), m_Weight = pair.Value });
                }

                Flows.Sort(static (a, b) =>
                {
                    int c = a.m_Origin.CompareTo(b.m_Origin);
                    return c != 0 ? c : a.m_Destination.CompareTo(b.m_Destination);
                });
                RoadZoneNodes = Roads.MapZonesToNodes(ZoneGrid, WorldMin);
                AssignedPairs = Roads.AssignFlow(Flows, RoadZoneNodes, 20000f, out AssignedWeight);

                NodeDemand = new float[Roads.NodeCount];
                for (int i = 0; i < NodeDemand.Length; i++)
                {
                    NodeDemand[i] = rng.NextDouble() < 0.3 ? 0f : NextF(rng, 0f, 1.5f);
                }

                var ends = new float2Like[900];
                var endWeights = new float[900];
                for (int i = 0; i < ends.Length; i++)
                {
                    ends[i] = new float2Like(NextF(rng, -2000f, 2000f), NextF(rng, -2000f, 2000f));
                    endWeights[i] = NextF(rng, 1f, 3f);
                }

                var byMode = new ModeFacts[5];
                byMode[(int)ModePreset.Bus] = new ModeFacts { Capacity = 80f, HeadwaySeconds = 45f, StopDurationSeconds = 5f, Acceleration = 4f, Braking = 5f };
                byMode[(int)ModePreset.Metro] = new ModeFacts { Capacity = 600f, HeadwaySeconds = 60f, StopDurationSeconds = 20f, Acceleration = 1.2f, Braking = 1.5f };
                byMode[(int)ModePreset.Tram] = new ModeFacts { Capacity = 200f, HeadwaySeconds = 45f, StopDurationSeconds = 10f, Acceleration = 1.5f, Braking = 2f };
                byMode[(int)ModePreset.Train] = new ModeFacts { Capacity = 800f, HeadwaySeconds = 90f, StopDurationSeconds = 30f, Acceleration = 0.8f, Braking = 1f };
                byMode[(int)ModePreset.Ferry] = new ModeFacts { Capacity = 100f, HeadwaySeconds = 90f, StopDurationSeconds = 30f, Acceleration = 0.5f, Braking = 0.8f };
                Facts = new FleetFacts(byMode);
                var hubX = new float[6];
                var hubZ = new float[6];
                var hubModes = new int[6];
                for (int i = 0; i < 6; i++)
                {
                    hubX[i] = NextF(rng, -1500f, 1500f);
                    hubZ[i] = NextF(rng, -1500f, 1500f);
                    hubModes[i] = 1 + rng.Next(31);
                }

                float transferRadius = SuitabilityTransit.WalkSpeed * (TransitModes.TransferWalkMs / 1000f);
                InterchangeMap hubs = SuitabilityTransit.BuildInterchangeMap(hubX, hubZ, hubModes, 6, transferRadius);
                Stops = new StopContext { Ends = ends, EndWeights = endWeights, Facts = Facts, Hubs = hubs, ScoreAt = ScoreAt };
            }

            // The rail lattice with three random track segments, built after the streets
            // so the random sequence matches the characterization run.
            public AlignmentNetwork BuildRail(out byte[] trackMask, out CompactGraph graph)
            {
                Random rng = Rng;
                var trackStarts = new List<float2Like>();
                var trackEnds = new List<float2Like>();
                for (int i = 0; i < 3; i++)
                {
                    trackStarts.Add(new float2Like(NextF(rng, -1800f, 1800f), NextF(rng, -1800f, 1800f)));
                    trackEnds.Add(new float2Like(NextF(rng, -1800f, 1800f), NextF(rng, -1800f, 1800f)));
                }

                trackMask = new byte[GridX * GridY];
                var tileGrid = new int2Like(GridX, GridY);
                SuitabilityLattice.RasterizeTracks(trackStarts, trackEnds, tileGrid, WorldMin, Tile, trackMask);
                byte[] mask = trackMask;
                graph = SuitabilityLattice.Build(tileGrid, WorldMin, Tile, Land, t => SuitabilityLattice.RailCostScale(mask[t] != 0), out float[] railX, out float[] railZ);
                var rail = new AlignmentNetwork();
                rail.Adopt(graph, railX, railZ, RouteNetwork.Rail);
                return rail;
            }
        }

        private static List<SuggestedRoute> GrowOn(SyntheticCity city, RouteObjective objective, out int grown, out int tooShort, out int atHub)
        {
            var output = new List<SuggestedRoute>();
            SuitabilityRoutes.BuildForNetwork(city.Roads, objective, 3, 0.1f, 12000f, city.NodeDemand, 0.005f, null, output, city.Stops, out grown, out tooShort, out atHub);
            return output;
        }

        private static void AssertRouteWellFormed(SuggestedRoute route, string label)
        {
            AssertTrue(route.Stops.Count >= TransitModes.MinStops, $"{label}: a candidate has at least {TransitModes.MinStops.ToString(CultureInfo.InvariantCulture)} stops");
            AssertTrue(route.Path.Count >= 2 && route.Nodes.Count >= 2, $"{label}: path and node walk exist");
            AssertTrue(route.Length > 0f && route.StopPlan is not null && route.StopPlanChosen.Length <= route.StopPlan.CandidateCount, $"{label}: a stop plan was solved");
            float along = 0f;
            for (int i = 1; i < route.Path.Count; i++)
            {
                along += float2Like.Distance(route.Path[i - 1], route.Path[i]);
            }

            AssertBits(unchecked((uint)Bits(along)), route.Length, $"{label}: Length is the trimmed path's length");
            AssertTrue(route.Stops[0] == route.Path[0] && route.Stops[route.Stops.Count - 1] == route.Path[route.Path.Count - 1], $"{label}: the path is trimmed to the first and last call");
        }

        private static void CorridorsOnSyntheticCityArePinned()
        {
            var city = new SyntheticCity();
            AssertTrue(city.Roads.NodeCount == 196 && city.Roads.EdgeCount == 351 && city.Flows.Count == 90, "the synthetic streets and journeys");
            AssertTrue(city.AssignedPairs == 90, "every pair routed");
            AssertBits(0x4531C000u, city.AssignedWeight, "assigned weight");

            List<SuggestedRoute> ridership = GrowOn(city, RouteObjective.Ridership, out int grown, out int tooShort, out int atHub);
            AssertTrue(grown == 16 && tooShort == 0 && atHub == 0 && ridership.Count == 12, $"ridership: grown {grown.ToString(CultureInfo.InvariantCulture)}, tooShort {tooShort.ToString(CultureInfo.InvariantCulture)}, kept {ridership.Count.ToString(CultureInfo.InvariantCulture)}");
            for (int i = 0; i < ridership.Count; i++)
            {
                AssertRouteWellFormed(ridership[i], "ridership #" + i.ToString(CultureInfo.InvariantCulture));
                AssertTrue(ridership[i].Group == i && ridership[i].Network == RouteNetwork.Road && ridership[i].Mode == ModePreset.Bus, "a grown corridor is its own group on the streets' smallest mode");
            }

            SuggestedRoute first = ridership[0];
            AssertBits(0x45AC6958u, first.Length, "first corridor length");
            AssertBits(0x4371995Du, first.CapturedFlow, "first corridor mean flow");
            AssertTrue(first.Nodes.Count == 22 && first.Path.Count == 22 && first.Stops.Count == 12, "first corridor shape");
            AssertBits(0xC49D9FBFu, first.Stops[0].x, "first stop x");
            AssertBits(0xC4D78174u, first.Stops[0].y, "first stop z");
            AssertBits(0x40F0469969B83874ul, first.StopPlanGain, "stop plan gain");
            AssertBits(0x40D8786000000000ul, first.StopPlanDelay, "stop plan delay");
            StopPlanProblem? firstPlan = first.StopPlan;
            AssertTrue(firstPlan is not null && firstPlan.CandidateCount == 94 && firstPlan.EndCount == 249, "stop plan size");

            List<SuggestedRoute> balanced = GrowOn(city, RouteObjective.Balanced, out grown, out tooShort, out atHub);
            AssertTrue(grown == 21 && tooShort == 4 && atHub == 0 && balanced.Count == 12, "balanced counts");
            AssertBits(0x4589BB0Du, balanced[0].Length, "balanced first length");
            AssertBits(0x435CA3F3u, balanced[0].CapturedFlow, "balanced first flow");
            AssertTrue(balanced[0].Nodes.Count == 17 && balanced[0].Stops.Count == 12, "balanced first shape");
            AssertBits(0x40EF9052E2F29DAAul, balanced[0].StopPlanGain, "balanced plan gain");

            List<SuggestedRoute> coverage = GrowOn(city, RouteObjective.Coverage, out grown, out tooShort, out atHub);
            AssertTrue(grown == 18 && tooShort == 2 && atHub == 0 && coverage.Count == 12, "coverage counts");
            AssertBits(0x4589BB0Du, coverage[0].Length, "coverage first length");
            StopPlanProblem? coveragePlan = coverage[0].StopPlan;
            AssertTrue(coveragePlan is not null && coveragePlan.CandidateCount == 79 && coveragePlan.EndCount == 219, "coverage plan size");
        }

        private static void LatticeTracesArePinned()
        {
            var city = new SyntheticCity();
            AlignmentNetwork rail = city.BuildRail(out byte[] trackMask, out CompactGraph graph);
            int marked = 0;
            for (int i = 0; i < trackMask.Length; i++)
            {
                marked += trackMask[i];
            }

            AssertTrue(marked == 618 && graph.NodeCount == 945 && graph.EdgeCount == 3522, $"lattice size: marked {marked.ToString(CultureInfo.InvariantCulture)}, nodes {graph.NodeCount.ToString(CultureInfo.InvariantCulture)}, edges {graph.EdgeCount.ToString(CultureInfo.InvariantCulture)}");
            int[] zoneNodes = rail.MapZonesToNodes(city.ZoneGrid, city.WorldMin);
            int assigned = rail.AssignFlow(city.Flows, zoneNodes, 30000f, out float weight);
            AssertTrue(assigned == 85, "rail assignment");
            AssertBits(0x45289000u, weight, "rail assigned weight");

            var output = new List<SuggestedRoute>();
            SuitabilityRoutes.BuildDirectForNetwork(rail, city.Flows, zoneNodes, 3, 20000f, ModePreset.Train, output, city.Stops, out int considered, out int tooShort, out int atHub);
            AssertTrue(considered == 15 && tooShort == 3 && atHub == 3 && output.Count == 18, $"traces: considered {considered.ToString(CultureInfo.InvariantCulture)}, tooShort {tooShort.ToString(CultureInfo.InvariantCulture)}, atHub {atHub.ToString(CultureInfo.InvariantCulture)}, kept {output.Count.ToString(CultureInfo.InvariantCulture)}");
            int bent = 0;
            for (int i = 0; i < output.Count; i++)
            {
                SuggestedRoute route = output[i];
                AssertRouteWellFormed(route, "trace #" + i.ToString(CultureInfo.InvariantCulture));
                AssertTrue(route.Mode == ModePreset.Train && route.Network == RouteNetwork.Rail, "forced mode and network");
                if (route.BentThroughHub)
                {
                    bent++;
                    AssertTrue(i > 0 && !output[i - 1].BentThroughHub && output[i - 1].Group == route.Group, "a bent variant follows its direct alignment in the same group");
                }
            }

            AssertTrue(bent == 6, $"six alignments were also offered bent through an interchange, got {bent.ToString(CultureInfo.InvariantCulture)}");
            int[] expectedStops = { 3, 3, 4, 4, 4, 4, 4, 3, 3, 4, 4, 4, 4, 3, 3, 4, 5, 5 };
            for (int i = 0; i < expectedStops.Length; i++)
            {
                AssertTrue(output[i].Stops.Count == expectedStops[i], "stop counts of the traces");
            }
        }

        private static void RestopAndFleetArePinned()
        {
            var city = new SyntheticCity();
            _ = GrowOn(city, RouteObjective.Ridership, out _, out _, out _);
            List<SuggestedRoute> balanced = GrowOn(city, RouteObjective.Balanced, out _, out _, out _);
            SuggestedRoute first = balanced[0];
            SuggestedRoute copy = first.CopyFor(ModePreset.Tram);
            SuitabilityRoutes.Restop(copy, ModePreset.Tram, city.Stops);
            AssertTrue(copy.Mode == ModePreset.Tram && copy.Stops.Count == 10 && first.Stops.Count == 12, "the tram's wider spacing places fewer stops on the same alignment");
            AssertBits(0x4589BB0Du, copy.Length, "the trimmed length is unchanged when both termini stay");
            float ride = TransitModes.RideSeconds(copy.Length, copy.Stops.Count, TransitModes.CruiseSpeedFor(copy.Mode), city.Facts.DelayPerStopSeconds(copy.Mode));
            AssertBits(0x43FBA411u, ride, "ride seconds");
            AssertTrue(SuitabilityRoutes.KeepsItsShape(copy, ride), "within the tram's ride limit");
            AssertTrue(!SuitabilityRoutes.KeepsItsShape(copy, TransitModes.MaxRideSecondsFor(ModePreset.Tram) + 1f), "over the limit the shape gate fails");
            AssertTrue(SuitabilityRoutes.EstimateVehicles(copy.Mode, copy.Length, copy.Stops.Count, city.Facts.HeadwayFor(copy.Mode), city.Facts.DelayPerStopSeconds(copy.Mode)) == 4, "fleet from length at cruise speed");
            AssertTrue(SuitabilityRoutes.EstimateVehiclesFromRoundTrip(1234.5f, copy.Stops.Count, 300f, 20f) == 5, "fleet from a measured round trip");
        }

        private static void RetraceAndDuplicatesArePinned()
        {
            var city = new SyntheticCity();
            _ = GrowOn(city, RouteObjective.Ridership, out _, out _, out _);
            List<SuggestedRoute> balanced = GrowOn(city, RouteObjective.Balanced, out _, out _, out _);
            SuggestedRoute first = balanced[0];

            var existing = new ExistingLine { m_Mode = ModePreset.Bus };
            var stopPositions = new List<float2Like>(first.Stops);
            for (int s = 0; s < first.Stops.Count; s++)
            {
                existing.m_StopIndices.Add(s);
            }

            var partial = new ExistingLine { m_Mode = ModePreset.Bus };
            partial.m_StopIndices.Add(0);
            AssertTrue(SuitabilityRoutes.DuplicatesExisting(first, new List<ExistingLine> { existing }, stopPositions, 150f), "a line calling at every stop is already built");
            AssertTrue(!SuitabilityRoutes.DuplicatesExisting(first, new List<ExistingLine> { partial }, stopPositions, 150f), "one shared stop is not");
            AssertTrue(!SuitabilityRoutes.DuplicatesExisting(first, new List<ExistingLine>(), stopPositions, 150f), "no lines, no duplicate");

            _ = GrowOn(city, RouteObjective.Coverage, out _, out _, out _);
            var scratch = new List<int>();
            SuggestedRoute? retraced = SuitabilityRoutes.RetraceOnRoad(city.Roads, new float2Like(-1500f, -1500f), new float2Like(1500f, 1200f), city.Stops, scratch);
            AssertTrue(retraced is not null, "a street path between the two points exists");
            if (retraced is null)
            {
                return;
            }

            AssertTrue(retraced.Mode == ModePreset.Bus && retraced.Network == RouteNetwork.Road && retraced.Nodes.Count == 18 && retraced.Stops.Count == 15, "the re-trace runs on the streets as a bus");
            AssertBits(0x45942103u, retraced.Length, "re-trace length");
            AssertRouteWellFormed(retraced, "retrace");
            AssertTrue(SuitabilityRoutes.RetraceOnRoad(city.Roads, new float2Like(9000f, 9000f), new float2Like(1500f, 1200f), city.Stops, scratch) is null, "an end off the network cannot be re-traced");
        }

        private static void NetworkHelpersArePinned()
        {
            var city = new SyntheticCity();
            var nodes = new List<int>();
            AssertTrue(city.Roads.TracePath(0, city.Roads.NodeCount - 1, 20000f, nodes) && nodes.Count == 21, "corner to corner");
            AssertBits(0x42D3FB2Au, city.Roads.FlowAlong(nodes), "flow along the trace");
            AssertBits(0x42980000u, city.Roads.FlowNear(nodes, new float2Like(100f, 100f)), "flow near a point of the trace");
            AssertTrue(city.Roads.NearestNode(new float2Like(0f, 0f), 300f) == 105 && city.Roads.NearestNode(new float2Like(9000f, 0f), 300f) == -1, "nearest node within reach, none beyond");
            AssertTrue(!city.Roads.TracePath(5, 6, 100f, nodes) && nodes.Count == 0, "a cost cap below the edge finds no path and leaves the list empty");
            var path = new List<float2Like>();
            city.Roads.MaterialisePath(new List<int> { 0, 1 }, path);
            AssertTrue(path.Count == 2 && path[0].x == city.Xs[0] && path[1].y == city.Zs[1], "an adopted lattice materialises node to node");
        }
    }
}
