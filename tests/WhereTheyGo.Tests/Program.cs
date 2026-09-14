using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WhereTheyGo.Tests
{
    // Minimal self-contained harness: no test framework, so it runs offline with
    // nothing to restore. Exit code is the number of failed tests.
    // Distinguishes an assertion failure from a genuine crash in the code under test.
    internal sealed class TestFailedException : Exception
    {
        public TestFailedException()
        {
        }

        public TestFailedException(string message)
            : base(message)
        {
        }

        public TestFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal static partial class Program
    {
        private static int s_Failures;

        private static int Main()
        {
            Run("Walk graph converts metres to whole milliseconds at the planning speed", WalkGraphMilliseconds);
            Run("Integer Dijkstra is exact, bounded and reusable", IntDijkstraExactBoundedReusable);
            Run("Nearest node breaks ties by index and respects the access walk", NearestNodeTiesAndReach);
            Run("Tiles and sites skip tunnel and bridge nodes that homes still walk from", NearestSiteSkipsOffGroundNodes);
            Run("Observed trips are held for a game day and scaled to a day's rate", ObservedTripWindowHoldsADay);
            Run("Observed trips restart on a rewound clock and stop at the cap", ObservedTripWindowRestartsAndCaps);
            Run("Coverage counts journeys served at both ends within the horizon", CoverageShare);

            Run("Graph adjacency covers both directions", GraphAdjacencyBothDirections);
            Run("Dijkstra finds the cheapest path, not the fewest hops", DijkstraPrefersCheapPath);
            Run("Dijkstra respects the cost limit", DijkstraRespectsMaxCost);
            Run("Dijkstra workspace reuse gives identical results", DijkstraWorkspaceReuse);
            Run("Workspace reuse survives the graph changing size", DijkstraWorkspaceResize);

            Run("Direct service beats an equal-time transfer", DirectBeatsTransfer);
            Run("Each change of vehicle costs a boarding", TransfersCostBoardings);
            Run("Walking links nearby stops into one interchange", WalkLinksStops);
            Run("Bucketed walk edges match an exhaustive sweep", WalkEdgesMatchAnExhaustiveSweep);
            Run("Vanilla wait model floors at zero", ExpectedWaitModel);
            Run("A line is judged over the window, not one reading", WindowAveragesLineReadings);
            Run("Readings older than the window are evicted", WindowEvictsPastADay);
            Run("Usage is averaged per sample, not as a ratio of sums", WindowUsageIsPerSample);
            Run("Loading another save restarts the window", WindowResetsWhenFramesRewind);
            Run("Windows can be written out and re-recorded in frame order without changing their averages", WindowsRoundTripThroughTheSave);
            Run("Daytime: the game's night is 22:00–06:00, shifts place a commute's rides, and a schedule follows the emptier period", DaytimeRules);
            Run("Daytime: period averages read only the readings of that period with vehicles out", PeriodAverages);
            Run("A deleted line stops being tracked", WindowForgetsDeletedLines);

            Run("The served ceiling follows the city's own median journey", ServedCeilingScalesToTheCity);
            Run("A thin network falls back to the fixed hour", ServedCeilingFallsBack);

            Run("Interchange weight is capacity relative to a bus, zero when unknown", CapacityWeightIsRelativeToBus);

            // F4/F5 — the alignment stage, pure since 2026-09-05 (float2Like). The pinned
            // figures are the game-typed code's own outputs on the same synthetic city,
            // recorded before the conversion; a change here is a behaviour change.
            Run("float2Like reproduces Unity's vector formulas bit for bit", Float2LikeMatchesUnityFormulas);
            Run("TileGrid clamps to the grid and a cell centre inverts its cell", TileGridClampsAndInverts);
            Run("Zones: trips aggregate per pair in total order, self and off-map trips drop", ZonesAggregate);

            // F3 steps 3–4, the panel contract and the disagreement pass, pure since 2026-09-05.
            Run("Journeys route door to door over the existing lines, and each line gets its riders", JourneysRouteOverTheExistingNetwork);
            Run("Carried counts the journeys transit makes faster, under this city's own ceiling", CarriedIsFasterThanWalkingAndUnderTheCeiling);
            Run("Bands: neighbouring corridors bundle into one, in both directions", BandsBundleNeighbouringCorridors);
            Run("Bands: the bundling is deterministic, heaviest first, and says what the cap left out", BandsAreDeterministicAndBounded);
            Run("Bands: a journey inside one zone or off the map is not a band", BandsDropWhatIsNotAJourney);
            Run("Bands: the direction follows the hour, and a whole day has none", BandDirectionFollowsTheHour);
            Run("Bands: the arc bows left, the width follows the square root", BandGeometryBowsLeftAndScalesByLog);
            Run("Bands: the colour runs warm to cool and falls in lightness all the way", BandColourRunsWarmToCoolAndMonotone);
            Run("Panel payload rows keep their field order and formatting", PanelPayloadRowsKeepTheirFieldOrder);

            // F1's grid pass and F2's candidate set, pure since 2026-09-05.
            Run("Walk network: the pieces the game's data leaves are bridged", WalkBridgingJoinsWhatTheDataCuts);
            Run("Walk network: every tile attaches to the nearest pavement within the access walk", TileSnapAttachesTilesToTheNearestPavement);

            Console.WriteLine();
            if (s_Failures == 0)
            {
                Console.WriteLine("All tests passed.");
                return 0;
            }

            Console.WriteLine($"{s_Failures} test(s) FAILED.");
            return s_Failures;
        }

        // ---- tests ----------------------------------------------------------

        private static long BruteForceSites(int[] candidates, float[] candidateScores, int count, int width, int separation, int maxSites)
        {
            long best = 0;
            var chosen = new int[Math.Max(1, maxSites)];
            Recurse(0, 0, 0);
            return best;

            void Recurse(int from, int depth, long value)
            {
                if (value > best)
                {
                    best = value;
                }

                if (depth == maxSites)
                {
                    return;
                }

                for (int i = from; i < count; i++)
                {
                    bool ok = true;
                    for (int j = 0; j < depth && ok; j++)
                    {
                        int dx = Math.Abs((candidates[i] % width) - (chosen[j] % width));
                        int dy = Math.Abs((candidates[i] / width) - (chosen[j] / width));
                        ok = Math.Max(dx, dy) >= separation;
                    }

                    if (ok)
                    {
                        chosen[depth] = candidates[i];
                        Recurse(i + 1, depth + 1, value + (long)candidateScores[i]);
                    }
                }
            }
        }

        private static void AssertSitesFeasible(int[] indices, int count, int width, int separation)
        {
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    int dx = Math.Abs((indices[i] % width) - (indices[j] % width));
                    int dy = Math.Abs((indices[i] / width) - (indices[j] / width));
                    AssertTrue(Math.Max(dx, dy) >= separation, "chosen sites respect the separation");
                }
            }
        }

        private static long[][] AllPairsMs(WalkGraph graph)
        {
            int n = graph.NodeCount;
            var dist = new long[n][];
            for (int i = 0; i < n; i++)
            {
                dist[i] = new long[n];
                for (int j = 0; j < n; j++)
                {
                    dist[i][j] = i == j ? 0 : long.MaxValue / 4;
                }
            }

            for (int e = 0; e < graph.EdgeMs.Length; e++)
            {
                int u = graph.EdgeA[e];
                int v = graph.EdgeB[e];
                dist[u][v] = Math.Min(dist[u][v], graph.EdgeMs[e]);
                dist[v][u] = Math.Min(dist[v][u], graph.EdgeMs[e]);
            }

            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        dist[i][j] = Math.Min(dist[i][j], dist[i][k] + dist[k][j]);
                    }
                }
            }

            return dist;
        }

        private static void CoverageShare()
        {
            // Line 0..7 one minute apart; one served stop at node 1 (access 0), horizon 10 min.
            WalkGraph graph = LineGraph(8, 72f);
            var dijkstra = new IntDijkstra(graph.NodeCount);
            int[] served = Coverage.ServedWalkMs(graph, dijkstra, new[] { 1 }, new[] { 0 }, 1, 600000);
            AssertEqual(0, served[1], 0, "the stop's node is served at once");
            AssertEqual(60000, served[0], 0, "one minute to node 0");
            AssertEqual(360000, served[7], 0, "six minutes to node 7");

            // Trips: 0->7 (both within 10 min), 0->7 with 5 min access at the destination
            // (6+5 = 11 min: not served), one with an off-network end, weights 2/1/1.
            var origin = new[] { 0, 0, 0 };
            var originAccess = new[] { 0, 0, 0 };
            var dest = new[] { 7, 7, -1 };
            var destAccess = new[] { 0, 300000, 0 };
            var weight = new[] { 2f, 1f, 1f };
            CoverageReport report = Coverage.Measure(served, 600000, origin, originAccess, dest, destAccess, weight, 3);
            AssertEqual(1, report.TripsCovered, 0, "only the first journey is served at both ends");
            AssertEqual(1, report.TripsOffNetwork, 0, "one journey has an end off the network");
            AssertEqual(0.5f, report.Share, 0f, "2 of 4 weight covered");
            AssertTrue(Coverage.EndServed(served, 7, 240000, 600000), "6 + 4 minutes fits the horizon exactly");
            AssertTrue(!Coverage.EndServed(served, 7, 240001, 600000), "one millisecond over does not");

            // Adding a stop at node 7 covers the second journey as well (5 min access ≤ 10).
            int[] merged = Coverage.WithStops(graph, dijkstra, served, new[] { 7 }, new[] { 0 }, 1, 600000);
            AssertEqual(0, merged[7], 0, "node 7 is now a stop");
            AssertEqual(0, served[7] == 360000 ? 0 : 1, 0, "the original field is untouched");
            CoverageReport after = Coverage.Measure(merged, 600000, origin, originAccess, dest, destAccess, weight, 3);
            AssertEqual(0.75f, after.Share, 0f, "3 of 4 weight covered with the new stop");
        }

        private static ObservedTrip TripAt(uint frame, byte purpose)
        {
            return new ObservedTrip { m_Frame = frame, m_OriginX = 1f, m_OriginZ = 2f, m_DestinationX = 3f, m_DestinationZ = 4f, m_Purpose = purpose };
        }

        private static void ObservedTripWindowHoldsADay()
        {
            uint day = Assumptions.FramesPerGameDay;
            var window = new ObservedTripWindow(day);
            AssertEqual(1f, window.ScaleFor(day), 0f, "an empty window scales by 1");
            window.Record(TripAt(1000u, 1));
            window.Record(TripAt(1000u + day / 4, 2));
            window.Record(TripAt(1000u + day / 2, 1));
            AssertEqual(3, window.Count, 0, "three trips held");
            AssertEqual(2, window.CountOf(1), 0, "two shopping");
            AssertEqual(1, window.CountOf(2), 0, "one leisure");
            AssertEqual(day / 2, (int)window.SpanFrames, 0, "span is half a day");
            AssertEqual(2f, window.ScaleFor(day), 0f, "half a day of readings scales by 2");

            window.Prune(1000u + day / 4 + day);
            AssertEqual(2, window.Count, 0, "the first trip fell out of the window");
            AssertEqual(1, window.CountOf(1), 0, "per-purpose count follows");
            AssertEqual(1, window.EvictedSinceLastReport, 0, "one eviction reported");
            AssertEqual(1000u + day / 4, (int)window[0].m_Frame, 0, "the oldest is now the second");

            var brief = new ObservedTripWindow(day);
            brief.Record(TripAt(10u, 1));
            brief.Record(TripAt(20u, 1));
            AssertEqual(Assumptions.ObservedTripMaxDayScale, brief.ScaleFor(day), 0f, "ten frames of readings cannot be scaled past the cap");
        }

        private static void ObservedTripWindowRestartsAndCaps()
        {
            var window = new ObservedTripWindow(1000u);
            window.Record(TripAt(500u, 1));
            window.Record(TripAt(600u, 1));
            window.Record(TripAt(100u, 2));
            AssertEqual(1, window.Count, 0, "a frame below the newest means another save: the window restarts");
            AssertEqual(0, window.CountOf(1), 0, "old purposes cleared");
            AssertEqual(1, window.CountOf(2), 0, "the new trip is kept");

            var full = new ObservedTripWindow(uint.MaxValue);
            for (int i = 0; i < Assumptions.ObservedTripCapacity + 5; i++)
            {
                full.Record(TripAt((uint)i, 1));
            }

            AssertEqual(Assumptions.ObservedTripCapacity, full.Count, 0, "the cap holds");
            AssertEqual(5, full.DroppedAtCapSinceLastReport, 0, "drops are counted, not hidden");
            full.ClearCounters();
            AssertEqual(0, full.DroppedAtCapSinceLastReport, 0, "counters reset on report");
        }

        private static WalkGraph LineGraph(int nodes, float metres)
        {
            var x = new float[nodes];
            var z = new float[nodes];
            var a = new int[Math.Max(0, nodes - 1)];
            var b = new int[a.Length];
            var len = new float[a.Length];
            for (int i = 0; i < nodes; i++)
            {
                x[i] = i * metres;
            }

            for (int e = 0; e < a.Length; e++)
            {
                a[e] = e;
                b[e] = e + 1;
                len[e] = metres;
            }

            return WalkGraph.Build(x, z, a, b, len, a.Length);
        }

        private static void WalkGraphMilliseconds()
        {
            // 1.2f widened is 1.2000000476837158, so 1.2 m is fractionally under a
            // second and rounds to 1000 ms; 0 m is never free.
            AssertEqual(1000, WalkGraph.WalkMilliseconds(1.2), 0, "1.2 m at 1.2 m/s");
            AssertEqual(0, WalkGraph.WalkMilliseconds(0.0), 0, "a zero walk is zero milliseconds");
            AssertEqual(100000, WalkGraph.WalkMilliseconds(120.0), 0, "120 m is 100 s");
            WalkGraph graph = LineGraph(3, 72f);
            AssertEqual(60000, graph.EdgeMs[0], 0, "72 m is one minute");
            WalkGraph degenerate = WalkGraph.Build(new float[2], new float[2], new[] { 0 }, new[] { 1 }, new[] { 0f }, 1);
            AssertEqual(1, degenerate.EdgeMs[0], 0, "a zero-length edge still costs a millisecond");
            AssertEqual(2, graph.Offsets[2] - graph.Offsets[1], 0, "middle node has two adjacencies");
        }

        private static void IntDijkstraExactBoundedReusable()
        {
            // 0-1-2-3-4 at one minute per edge, plus a 0-4 shortcut of 150 s: the
            // shortcut wins for node 4 (150 < 240) and settles before node 3 (180).
            WalkGraph line = LineGraph(5, 72f);
            var a = new List<int>(line.EdgeA) { 0 };
            var b = new List<int>(line.EdgeB) { 4 };
            var len = new List<float> { 72f, 72f, 72f, 72f, 180f };
            WalkGraph graph = WalkGraph.Build(line.NodeX, line.NodeZ, a.ToArray(), b.ToArray(), len.ToArray(), 5);

            var dijkstra = new IntDijkstra(graph.NodeCount);
            dijkstra.Run(graph, 0, 0, 1_000_000);
            AssertEqual(5, dijkstra.SettledCount, 0, "everything is within the bound");
            AssertTrue(dijkstra.Dist[4] == 150000, "shortcut time is exact");
            AssertTrue(dijkstra.Dist[3] == 180000, "line time is exact");
            AssertEqual(4, dijkstra.Settled[3], 0, "node 4 settles before node 3");

            dijkstra.Run(graph, 2, 30000, 90000);
            AssertEqual(3, dijkstra.SettledCount, 0, "start 30 s + one minute reaches the neighbours only");
            AssertTrue(dijkstra.Dist[2] == 30000, "start cost is the source's time");
            AssertTrue(dijkstra.Dist[0] == IntDijkstra.Unreached, "beyond the bound is unreached");
            AssertTrue(dijkstra.Dist[4] == IntDijkstra.Unreached, "the previous run's labels are gone");

            var fresh = new IntDijkstra(graph.NodeCount);
            fresh.Run(graph, 2, 30000, 90000);
            for (int i = 0; i < graph.NodeCount; i++)
            {
                AssertTrue(fresh.Dist[i] == dijkstra.Dist[i], "reused workspace equals a fresh one");
            }
        }

        private static void NearestNodeTiesAndReach()
        {
            var x = new float[] { 0f, 100f, 100f, 400f };
            var z = new float[] { 0f, 0f, 0f, 0f };
            WalkGraph graph = WalkGraph.Build(x, z, new[] { 0, 1, 2 }, new[] { 1, 2, 3 }, new[] { 100f, 1f, 300f }, 3);
            var index = new WalkNodeIndex(graph, 144.0);

            int node = index.Nearest(60f, 0f, 144.0, out double metres);
            AssertEqual(1, node, 0, "co-located nodes 1 and 2: the lower index wins");
            AssertEqual(40f, (float)metres, 0f, "distance to it");
            AssertEqual(-1, index.Nearest(250f, 0f, 144.0, out _), 0, "nothing within reach");
            AssertEqual(1, index.Nearest(250f, 0f, 200.0, out _), 0, "150 m to both sides: the lower index wins");
            AssertEqual(3, index.Nearest(260f, 0f, 200.0, out _), 0, "wider reach finds the nearer node 3");

            int snapped = WalkAccess.SnapPoint(index, 0f, 60f, Assumptions.AccessWalkMs, out int walkMs);
            AssertEqual(0, snapped, 0, "60 m off node 0");
            AssertEqual(50000, walkMs, 0, "60 m is 50 s");
        }

        private static void NearestSiteSkipsOffGroundNodes()
        {
            var x = new float[] { 0f, 100f, 200f };
            var z = new float[3];
            WalkGraph graph = WalkGraph.Build(x, z, new[] { 0, 1 }, new[] { 1, 2 }, new[] { 100f, 100f }, 2, new[] { true, false, true });
            var index = new WalkNodeIndex(graph, 144.0);

            AssertEqual(1, index.Nearest(90f, 0f, 144.0, out _), 0, "a home snaps to the tunnel pavement it can walk from");
            AssertEqual(0, index.NearestSite(90f, 0f, 144.0, out double metres), 0, "a tile skips the tunnel node for the nearest ground node");
            AssertEqual(90f, (float)metres, 0f, "distance to the ground node");
            AssertEqual(-1, index.NearestSite(110f, 0f, 60.0, out _), 0, "no ground node in reach: no site");

            int node = WalkAccess.SnapSite(index, 100f, 0f, Assumptions.AccessWalkMs, out int walkMs);
            AssertEqual(0, node, 0, "the tile over the tunnel node reads node 0, 100 m away");
            AssertEqual(83333, walkMs, 0, "100 m at 1.2 m/s");
            AssertEqual(1, WalkAccess.SnapPoint(index, 100f, 0f, Assumptions.AccessWalkMs, out walkMs), 0, "a source at the same spot keeps the tunnel node");
            AssertEqual(0, walkMs, 0, "at no walk");

            WalkGraph plain = WalkGraph.Build(x, z, new[] { 0, 1 }, new[] { 1, 2 }, new[] { 100f, 100f }, 2);
            AssertTrue(plain.Siteable.Length == 3 && plain.Siteable[0] && plain.Siteable[1] && plain.Siteable[2], "a graph built without flags has every node on the ground");
        }

        // ---- graph / routing tests ------------------------------------------

        // A 6-node chain 0-1-2-3-4-5 plus a long shortcut edge 0-5.
        private static CompactGraph BuildChain(out float[] flow)
        {
            var a = new[] { 0, 1, 2, 3, 4, 0 };
            var b = new[] { 1, 2, 3, 4, 5, 5 };
            var cost = new[] { 1f, 1f, 1f, 1f, 1f, 100f };
            flow = new float[a.Length];
            return CompactGraph.Build(6, a, b, cost, a.Length);
        }

        private static void GraphAdjacencyBothDirections()
        {
            CompactGraph graph = BuildChain(out _);

            // Node 2 sits mid-chain, so it must see the edges on both sides.
            int start = graph.NodeOffsets[2];
            int end = graph.NodeOffsets[2 + 1];
            AssertEqual(2, end - start, 0, "node 2 degree");

            var seen = new List<int>();
            for (int i = start; i < end; i++)
            {
                seen.Add(graph.AdjOther[i]);
            }
            AssertTrue(seen.Contains(1) && seen.Contains(3), "node 2 must reach both neighbours");

            // Node 0 has the chain edge plus the shortcut.
            AssertEqual(2, graph.NodeOffsets[1] - graph.NodeOffsets[0], 0, "node 0 degree");
            AssertEqual(3, graph.OtherEnd(2, 2), 0, "OtherEnd resolves the far end");
            AssertEqual(2, graph.OtherEnd(2, 3), 0, "OtherEnd is symmetric");
        }

        private static void DijkstraPrefersCheapPath()
        {
            CompactGraph graph = BuildChain(out _);
            var ws = new DijkstraWorkspace(graph.NodeCount);
            ws.Run(graph, 0, 1000f);

            // Five hops of cost 1 beat one hop of cost 100.
            AssertEqual(5f, ws.Dist[5], 1e-4f, "distance to node 5");
            AssertEqual(2f, ws.Dist[2], 1e-4f, "distance to node 2");
            AssertEqual(0f, ws.Dist[0], 0f, "source distance");
        }

        private static void DijkstraRespectsMaxCost()
        {
            CompactGraph graph = BuildChain(out _);
            var ws = new DijkstraWorkspace(graph.NodeCount);
            ws.Run(graph, 0, 2f);

            AssertEqual(2f, ws.Dist[2], 1e-4f, "node 2 is within the limit");
            AssertTrue(ws.Dist[4] == float.MaxValue, "node 4 is beyond the limit and must stay unreached");
        }

        private static void DijkstraWorkspaceReuse()
        {
            CompactGraph graph = BuildChain(out _);
            var ws = new DijkstraWorkspace(graph.NodeCount);

            ws.Run(graph, 0, 1000f);
            float first = ws.Dist[5];

            // A different source, then back again: stale state from the previous
            // search must not leak into the result.
            ws.Run(graph, 3, 1000f);
            ws.Run(graph, 0, 1000f);
            AssertEqual(first, ws.Dist[5], 0f, "repeat search must match");

            ws.Run(graph, 5, 1000f);
            AssertEqual(5f, ws.Dist[0], 1e-4f, "reverse direction is symmetric");
        }

        // ---- transit graph tests --------------------------------------------

        // Stops 0..3 in a line, plus stop 4 off to the side.
        private static TransitNetwork BuildTwoLineNetwork(float wait, out float[] xs, out float[] zs)
        {
            xs = new[] { 0f, 1000f, 2000f, 3000f, 1000f };
            zs = new[] { 0f, 0f, 0f, 0f, 500f };

            var lines = new List<TransitLine>
            {
                // Line 0: the trunk, all four stops in a row.
                new TransitLine { m_Stops = new[] { 0, 1, 2, 3 }, m_ExpectedWait = wait, m_SpeedMetresPerSecond = 10f },
                // Line 1: a feeder from the side stop into the trunk at stop 1.
                new TransitLine { m_Stops = new[] { 4, 1 }, m_ExpectedWait = wait, m_SpeedMetresPerSecond = 10f },
            };

            return TransitGraph.Build(xs, zs, 5, lines, 100f, Assumptions.DefaultBoardPenaltySeconds);
        }

        private static void DirectBeatsTransfer()
        {
            TransitNetwork net = BuildTwoLineNetwork(60f, out _, out _);
            var ws = new DijkstraWorkspace(net.Graph.NodeCount);

            // 0 -> 3 is a single ride on the trunk: one boarding.
            ws.Run(net.Graph, 0, 100000f);
            AssertTrue(TransitGraph.Inspect(net, ws, 0, 3, -1, out int boardings, out _, out float direct),
                "trunk journey should be routable");
            AssertEqual(1, boardings, 0, "riding one line is one boarding");

            // 4 -> 3 needs the feeder then the trunk: two boardings, and must cost
            // more than the direct trip even though the ride distance is shorter.
            ws.Run(net.Graph, 4, 100000f);
            AssertTrue(TransitGraph.Inspect(net, ws, 4, 3, -1, out int viaFeeder, out _, out float changed),
                "feeder journey should be routable");
            AssertEqual(2, viaFeeder, 0, "changing vehicle is a second boarding");
            AssertTrue(changed > direct, $"a change must cost extra ({changed} vs {direct})");
        }

        private static void TransfersCostBoardings()
        {
            // With a big wait, the second boarding should dominate the cost.
            TransitNetwork cheap = BuildTwoLineNetwork(10f, out _, out _);
            TransitNetwork dear = BuildTwoLineNetwork(600f, out _, out _);

            var wsCheap = new DijkstraWorkspace(cheap.Graph.NodeCount);
            wsCheap.Run(cheap.Graph, 4, 100000f);
            _ = TransitGraph.Inspect(cheap, wsCheap, 4, 3, -1, out _, out _, out float cheapTime);

            var wsDear = new DijkstraWorkspace(dear.Graph.NodeCount);
            wsDear.Run(dear.Graph, 4, 100000f);
            _ = TransitGraph.Inspect(dear, wsDear, 4, 3, -1, out _, out _, out float dearTime);

            // Two boardings, each paying the extra wait: the gap is about 2x.
            AssertTrue(dearTime > cheapTime + 1000f, $"longer headways must cost more ({dearTime} vs {cheapTime})");
        }

        private static void WalkLinksStops()
        {
            // Two stops 80 m apart on different lines: within the walk radius they are
            // one interchange, beyond it the journey cannot be made at all.
            var xs = new[] { 0f, 1000f, 1080f, 2000f };
            var zs = new[] { 0f, 0f, 0f, 0f };
            var lines = new List<TransitLine>
            {
                new TransitLine { m_Stops = new[] { 0, 1 }, m_ExpectedWait = 30f, m_SpeedMetresPerSecond = 10f },
                new TransitLine { m_Stops = new[] { 2, 3 }, m_ExpectedWait = 30f, m_SpeedMetresPerSecond = 10f },
            };

            TransitNetwork linked = TransitGraph.Build(xs, zs, 4, lines, 200f, 5f);
            var ws = new DijkstraWorkspace(linked.Graph.NodeCount);
            ws.Run(linked.Graph, 0, 100000f);
            AssertTrue(TransitGraph.Inspect(linked, ws, 0, 3, -1, out int boardings, out _, out _),
                "a short walk must join the two lines");
            AssertEqual(2, boardings, 0, "one boarding per line");

            TransitNetwork split = TransitGraph.Build(xs, zs, 4, lines, 50f, 5f);
            var ws2 = new DijkstraWorkspace(split.Graph.NodeCount);
            ws2.Run(split.Graph, 0, 100000f);
            AssertTrue(!TransitGraph.Inspect(split, ws2, 0, 3, -1, out _, out _, out _),
                "too far to walk means no itinerary");
        }

        private static void ExpectedWaitModel()
        {
            // max(interval/2, observed) - dwell, floored at zero.
            AssertEqual(25f, TransitGraph.ExpectedWait(60f, 0f, 5f), 1e-4f, "half the headway less dwell");
            AssertEqual(85f, TransitGraph.ExpectedWait(60f, 90f, 5f), 1e-4f, "observed wait dominates when longer");
            AssertEqual(0f, TransitGraph.ExpectedWait(10f, 0f, 100f), 0f, "never negative");
        }

        // Resize replaces every array the touched list addresses, so its count has to
        // go with them; carrying it over made the next search index past the new ones.
        private static void DijkstraWorkspaceResize()
        {
            CompactGraph big = BuildChain(out _);
            var ws = new DijkstraWorkspace(big.NodeCount);
            ws.Run(big, 0, 1000f);

            var a = new[] { 0, 1 };
            var b = new[] { 1, 2 };
            var cost = new[] { 3f, 4f };
            CompactGraph small = CompactGraph.Build(3, a, b, cost, 2);
            ws.Run(small, 0, 1000f);
            AssertEqual(7f, ws.Dist[2], 1e-4f, "a reused workspace must give the fresh answer on a smaller graph");

            var fresh = new DijkstraWorkspace(small.NodeCount);
            fresh.Run(small, 0, 1000f);
            AssertEqual(fresh.Dist[2], ws.Dist[2], 0f, "reuse must match a fresh instance");

            // And back up again, including the degenerate empty graph.
            ws.Run(new CompactGraph(), 0, 1000f);
            ws.Run(big, 0, 1000f);
            AssertEqual(5f, ws.Dist[5], 1e-4f, "the workspace must still be usable after an empty graph");
        }

        private static ExistingLine HealthLine(int id, ModePreset mode, int vehicles, int perVehicle, float roundTrip, int stops, float loopMetres = 6000f)
        {
            var line = new ExistingLine
            {
                m_Id = id,
                m_Name = "L" + id.ToString(CultureInfo.InvariantCulture),
                m_Mode = mode,
                m_Vehicles = vehicles,
                m_Capacity = vehicles * perVehicle,
                m_StableDurationSeconds = roundTrip,
                m_LineDurationSeconds = roundTrip - (stops * 15f),
                m_TargetInterval = roundTrip / Math.Max(1, vehicles),
                m_LengthMetres = loopMetres,
                m_StopDuration = 15f,
                m_VehicleInterval = roundTrip / Math.Max(1, vehicles),
            };
            for (int s = 0; s < stops; s++)
            {
                line.m_StopIndices.Add(s);
            }

            return line;
        }

        // `aboard` per reading over the last part of a game day, a quarter of an hour
        // apart, by day; `idle` readings with nothing out are appended.
        private static void Readings(LineReadings problem, ExistingLine line, int[] aboard, int idle = 0, float timeOfDay = 0.5f)
        {
            var samples = new List<LineObservation>();
            uint frame = 1000u;
            for (int i = 0; i < aboard.Length; i++)
            {
                frame += Assumptions.ReadingIntervalFrames;
                samples.Add(new LineObservation { m_Frame = frame, m_Passengers = aboard[i], m_Capacity = line.m_Capacity, m_Vehicles = line.m_Vehicles, m_IntervalSeconds = line.m_VehicleInterval, m_TimeOfDay = timeOfDay });
            }

            for (int i = 0; i < idle; i++)
            {
                frame += Assumptions.ReadingIntervalFrames;
                samples.Add(new LineObservation { m_Frame = frame, m_Passengers = 0, m_Capacity = 0, m_Vehicles = 0, m_IntervalSeconds = 0f, m_TimeOfDay = 0.95f });
            }

            problem.Samples[line.m_Id] = samples.ToArray();
            line.m_Passengers = aboard.Length > 0 ? aboard[aboard.Length - 1] : 0;
        }

        private static LineReadings HealthProblem(params ExistingLine[] lines)
        {
            var problem = new LineReadings
            {
                WindowFrames = Assumptions.FramesPerGameDay,
            };
            problem.Lines.AddRange(lines);
            return problem;
        }

        // The walk pass is bucketed because route scoring rebuilds this network once per
        // candidate. A bucket that drops a pair silently un-links an interchange, so
        // the result is checked against the exhaustive sweep it replaced.
        private static void WalkEdgesMatchAnExhaustiveSweep()
        {
            var random = new Random(11);
            const int stops = 120;
            const float radius = 250f;
            var xs = new float[stops];
            var zs = new float[stops];
            for (int i = 0; i < stops; i++)
            {
                xs[i] = (float)(random.NextDouble() * 3000.0);
                zs[i] = (float)(random.NextDouble() * 3000.0);
            }

            TransitNetwork net = TransitGraph.Build(
                xs, zs, stops, new List<TransitLine>(), radius, Assumptions.DefaultBoardPenaltySeconds);

            var built = new HashSet<long>();
            for (int e = 0; e < net.Graph.EdgeCount; e++)
            {
                if (net.EdgeKind[e] != TransitEdgeKind.Walk)
                {
                    continue;
                }

                int a = Math.Min(net.Graph.EdgeA[e], net.Graph.EdgeB[e]);
                int b = Math.Max(net.Graph.EdgeA[e], net.Graph.EdgeB[e]);
                AssertTrue(built.Add(((long)a << 32) | (uint)b), $"pair {a}-{b} must be joined once");
            }

            int expected = 0;
            for (int a = 0; a < stops; a++)
            {
                for (int b = a + 1; b < stops; b++)
                {
                    float dx = xs[a] - xs[b];
                    float dz = zs[a] - zs[b];
                    if ((dx * dx) + (dz * dz) > radius * radius)
                    {
                        AssertTrue(!built.Contains(((long)a << 32) | (uint)b), $"pair {a}-{b} is out of range");
                        continue;
                    }

                    expected++;
                    AssertTrue(built.Contains(((long)a << 32) | (uint)b), $"pair {a}-{b} is within range and must be joined");
                }
            }

            AssertEqual(expected, built.Count, 0, "exactly the pairs within the radius");
            AssertTrue(expected > 20, $"the fixture must actually produce interchanges, got {expected}");
        }

        // "Already served" has to mean slow FOR HERE. Against a fixed hour a ten-minute
        // trip kept a sixth of its weight whether the city was three kilometres across
        // or thirty, so a compact well-served city absorbed 94% of all its travel.
        private static void ServedCeilingScalesToTheCity()
        {
            // A brisk city: journeys carried in about three minutes.
            var brisk = new float[40];
            for (int i = 0; i < brisk.Length; i++)
            {
                brisk[i] = 180f + i;
            }

            float ceiling = TransitGraph.ServedCeiling(
                brisk, brisk.Length, multiple: 3f, fallback: 3600f, minSamples: 20, out float median);
            AssertEqual(200f, median, 20f, "the median of the carried journeys");
            AssertEqual(median * 3f, ceiling, 1e-3f, "the ceiling is a multiple of it");
            AssertTrue(ceiling < 3600f, "and well inside the fixed hour");

            // The same journey is discounted far less here than against the fixed hour.
            float keptNow = 180f / ceiling;
            float keptBefore = 180f / 3600f;
            AssertTrue(keptNow > keptBefore * 3f,
                $"a typical journey must keep meaningfully more of its weight ({keptNow} vs {keptBefore})");

            // A slow city's ceiling is capped at the point a trip stops being transit.
            var slow = new float[40];
            for (int i = 0; i < slow.Length; i++)
            {
                slow[i] = 2000f;
            }

            float slowCeiling = TransitGraph.ServedCeiling(
                slow, slow.Length, multiple: 3f, fallback: 3600f, minSamples: 20, out _);
            AssertEqual(3600f, slowCeiling, 1e-3f, "never past the router's own horizon");
        }

        // A median over a handful of journeys is noise, which is the risk this approach
        // carries on a thin network.
        private static void ServedCeilingFallsBack()
        {
            var few = new[] { 100f, 120f, 140f };
            AssertEqual(
                3600f,
                TransitGraph.ServedCeiling(few, few.Length, 3f, 3600f, minSamples: 20, out float median),
                1e-3f,
                "too few carried journeys falls back to the fixed hour");
            AssertEqual(0f, median, 0f, "and reports no median, rather than a misleading one");

            AssertEqual(
                3600f,
                TransitGraph.ServedCeiling(Array.Empty<float>(), 0, 3f, 3600f, 20, out _),
                1e-3f,
                "a network carrying nothing falls back too");
        }

        private static float[] NewNovelty(int nodes)
        {
            var novelty = new float[nodes];
            for (int i = 0; i < nodes; i++)
            {
                novelty[i] = 1f;
            }

            return novelty;
        }

        // A ferry with one boat reads zero passengers whenever that boat is mid
        // crossing. Judged on the reading the refresh happened to land on, the line
        // is condemned as "nearly empty — reroute or remove"; judged over a day it is
        // simply a small line that is busy some of the time.
        private static void WindowAveragesLineReadings()
        {
            var history = new LineHistory(Assumptions.FramesPerGameDay);

            // Six readings a few game hours apart: full, empty, full, empty, ...
            int[] aboard = { 80, 0, 60, 0, 40, 0 };
            for (int i = 0; i < aboard.Length; i++)
            {
                history.Record(7, new LineObservation
                {
                    m_Frame = (uint)(i * 10000),
                    m_Passengers = aboard[i],
                    m_Capacity = 100,
                    m_IntervalSeconds = 300f,
                    m_Vehicles = 1,
                });
            }

            AssertTrue(history.TryAverage(7, out LineAverage average), "the line has history");
            AssertTrue(average.m_Samples == 6, "every reading inside the window counts");
            AssertEqual(30f, average.m_Passengers, 1e-3f, "mean passengers over the window");
            AssertEqual(0.3f, average.m_Usage, 1e-3f, "mean usage, not the reading it landed on");
            AssertEqual(0.8f, average.m_PeakUsage, 1e-3f, "the busiest sample is kept alongside the mean");
            AssertEqual(300f, average.m_IntervalSeconds, 1e-3f, "interval averages too");

            // The verdict must know how much of a day it is actually looking at.
            AssertTrue(average.m_SpanFrames == 50000u, "the span covered is reported");
            AssertTrue(LineHistory.GameHours(average.m_SpanFrames) < 24f, "under a full day of coverage");

            AssertTrue(!history.TryAverage(99, out LineAverage _), "a line never seen has no history");
        }

        private static void WindowEvictsPastADay()
        {
            var history = new LineHistory(1000u);

            // Three readings, then one a full window later: only the last survives
            // together with anything inside the window behind it.
            history.Record(1, Reading(0u, 10));
            history.Record(1, Reading(700u, 20));
            history.Record(1, Reading(900u, 30));
            AssertTrue(history.TryAverage(1, out LineAverage before), "history exists");
            AssertTrue(before.m_Samples == 3, "nothing evicted while inside the window");

            // Window 1000, newest 1600, so the cutoff is 600 and only the reading at
            // frame 0 falls out.
            history.Record(1, Reading(1600u, 40));
            AssertTrue(history.TryAverage(1, out LineAverage after), "history survives eviction");
            AssertTrue(after.m_Samples == 3, "the reading older than the window is gone");
            AssertEqual(30f, after.m_Passengers, 1e-3f, "the evicted reading no longer weighs on the mean");
            AssertTrue(history.EvictedSinceLastReport == 1, "the eviction is counted so the log can say so");

            // A window that has nothing left in it must not report a stale average.
            history.Record(1, Reading(100000u, 5));
            AssertTrue(history.TryAverage(1, out LineAverage far), "the newest reading is kept");
            AssertTrue(far.m_Samples == 1, "everything a window older than the newest is dropped");
            AssertEqual(5f, far.m_Passengers, 1e-3f, "only the surviving reading counts");
        }

        // A line whose fleet doubles mid-window: 10/100 then 90/200. A ratio of sums
        // says 100/300 = 33%; the honest answer is the mean of 10% and 45%.
        private static void WindowUsageIsPerSample()
        {
            var history = new LineHistory(Assumptions.FramesPerGameDay);
            history.Record(3, new LineObservation
            {
                m_Frame = 0u,
                m_Passengers = 10,
                m_Capacity = 100,
                m_IntervalSeconds = 120f,
                m_Vehicles = 1,
            });
            history.Record(3, new LineObservation
            {
                m_Frame = 5000u,
                m_Passengers = 90,
                m_Capacity = 200,
                m_IntervalSeconds = 60f,
                m_Vehicles = 2,
            });

            AssertTrue(history.TryAverage(3, out LineAverage average), "history exists");
            AssertEqual(0.275f, average.m_Usage, 1e-4f, "mean of the per-sample usages");
            AssertEqual(0.45f, average.m_PeakUsage, 1e-4f, "the fuller sample is the peak");
            AssertEqual(1.5f, average.m_Vehicles, 1e-4f, "fleet size averages across the change");
        }

        private static void DaytimeRules()
        {
            // TransportLineSystem: isNight = normalizedTime < 0.25 || normalizedTime >= 11/12.
            AssertTrue(Daytime.IsNight(0f) && Daytime.IsNight(0.2499f) && !Daytime.IsNight(0.25f), "night ends at 06:00");
            AssertTrue(!Daytime.IsNight(0.9166f) && Daytime.IsNight(11f / 12f) && Daytime.IsNight(0.99f), "night starts at 22:00");
            AssertTrue(Daytime.IsNight(1.1f) && !Daytime.IsNight(1.5f), "times wrap around the day");

            // The hour a journey carries, and the two rides of a commute.
            AssertTrue(Daytime.HourOf(0f) == 0 && Daytime.HourOf(9f / 24f) == 9 && Daytime.HourOf(0.9999f) == 23, "whole hours since midnight");
            AssertTrue(Daytime.HourOf(1f + (7f / 24f)) == 7 && Daytime.HourOf(-1f / 24f) == 23, "hours wrap around the day");
            AssertTrue(Daytime.HourOf(23f / 24f) == 23 && Daytime.HourOf(7f / 24f) == 7, "a time exactly on the hour is that hour, float arithmetic notwithstanding");
            AssertTrue(Daytime.IsNightHour(23) && Daytime.IsNightHour(5) && !Daytime.IsNightHour(6) && !Daytime.IsNightHour(21), "night is 22:00-06:00, by the hour");

            // A 9-to-17 city. The shift offsets are 0.33 and 0.67 of a day, which the
            // game ROUNDS to whole hours (WorkerSystem.GetTimeToWork): the evening shift
            // leaves at 17:00 and returns at 01:00, the night shift at 01:00 and 09:00.
            // Without that rounding both land an hour early.
            const float start = 9f / 24f;
            const float end = 17f / 24f;
            Daytime.CommuteHours(0, start, end, out byte dayOut, out byte dayBack);
            AssertTrue(dayOut == 9 && dayBack == 17, $"the day shift leaves at 9 and returns at 17, got {dayOut.ToString(CultureInfo.InvariantCulture)}/{dayBack.ToString(CultureInfo.InvariantCulture)}");
            Daytime.CommuteHours(1, start, end, out byte eveOut, out byte eveBack);
            AssertTrue(eveOut == 17 && eveBack == 1, $"the evening shift is eight hours later, got {eveOut.ToString(CultureInfo.InvariantCulture)}/{eveBack.ToString(CultureInfo.InvariantCulture)}");
            Daytime.CommuteHours(2, start, end, out byte nightOut, out byte nightBack);
            AssertTrue(nightOut == 1 && nightBack == 9, $"and the night shift sixteen, got {nightOut.ToString(CultureInfo.InvariantCulture)}/{nightBack.ToString(CultureInfo.InvariantCulture)}");

            AssertEqual(1f, Daytime.DayShareOfHours(dayOut, dayBack), 1e-6f, "day shift");
            AssertEqual(0.5f, Daytime.DayShareOfHours(eveOut, eveBack), 1e-6f, "evening shift straddles the night");
            AssertEqual(0.5f, Daytime.DayShareOfHours(nightOut, nightBack), 1e-6f, "night shift straddles the night");
            AssertEqual(1f, new Journey { m_OutHour = dayOut, m_BackHour = dayBack }.DayShare, 1e-6f, "a journey states its own day share from its two hours");

            // 4369 s a day at 300 s and 80 seats both ways = 2330 seats a day, 1553 of
            // them by day (16 h) and 777 by night; 500 riders by day = 1000 boardings /
            // 1553 = 64.4 %, 20 riders by night = 40 / 777 = 5.1 %.
            float dayUtil = Daytime.UtilisationInPeriod(500f, 300f, 80f, Assumptions.DayShareOfDay);
            AssertTrue(dayUtil is > 0.64f and < 0.65f, $"day utilisation {dayUtil}");
            float nightUtil = Daytime.UtilisationInPeriod(20f, 300f, 80f, 1f - Assumptions.DayShareOfDay);
            AssertTrue(nightUtil is > 0.05f and < 0.06f, $"night utilisation {nightUtil}");
            AssertTrue(Daytime.Recommend(dayUtil, nightUtil, 0.15f) == LineSchedule.Day, "empty nights: run by day");
            AssertTrue(Daytime.Recommend(nightUtil, dayUtil, 0.15f) == LineSchedule.Night, "empty days: run by night");
            AssertTrue(Daytime.Recommend(dayUtil, dayUtil, 0.15f) == LineSchedule.DayAndNight, "both full: all day");
            AssertTrue(Daytime.Recommend(nightUtil, nightUtil, 0.15f) == LineSchedule.DayAndNight, "both empty is not a schedule question");
            AssertTrue(Daytime.Advise(LineSchedule.Day, nightUtil, nightUtil, 0.15f) == LineSchedule.Day, "an existing line under the floor in both periods keeps its schedule");
            AssertTrue(Daytime.Advise(LineSchedule.Day, dayUtil, dayUtil, 0.15f) == LineSchedule.DayAndNight, "demand in both periods extends it");
            AssertTrue(Daytime.Advise(LineSchedule.DayAndNight, dayUtil, nightUtil, 0.15f) == LineSchedule.Day, "otherwise the same rule as for suggestions");

            // Period utilisation is the one formula on the period's share of a vehicle's seats.
            AssertEqual(Coverage.Utilisation(500f, 300f, 80f * Assumptions.DayShareOfDay), dayUtil, 0f, "the period share scales the seats");
            AssertEqual(0f, Daytime.UtilisationInPeriod(500f, 300f, 80f, 0f), 0f, "a period of no length has no utilisation");
        }

        private static void PeriodAverages()
        {
            var history = new LineHistory(Assumptions.FramesPerGameDay);
            uint frame = 1000u;
            // Six day readings at 50 %, six night readings at 10 %, two night readings with no vehicles out.
            for (int i = 0; i < 6; i++)
            {
                frame += 1000u;
                history.Record(3, new LineObservation { m_Frame = frame, m_Passengers = 50, m_Capacity = 100, m_IntervalSeconds = 100f, m_Vehicles = 2, m_TimeOfDay = 0.5f });
                frame += 1000u;
                history.Record(3, new LineObservation { m_Frame = frame, m_Passengers = 10, m_Capacity = 100, m_IntervalSeconds = 100f, m_Vehicles = 2, m_TimeOfDay = 0.95f });
            }

            for (int i = 0; i < 2; i++)
            {
                frame += 1000u;
                history.Record(3, new LineObservation { m_Frame = frame, m_Passengers = 0, m_Capacity = 0, m_IntervalSeconds = 0f, m_Vehicles = 0, m_TimeOfDay = 0.1f });
            }

            AssertTrue(history.TryAveragePeriod(3, night: false, out LineAverage day) && day.m_Samples == 6, "six day readings");
            AssertEqual(0.5f, day.m_Usage, 1e-6f, "day usage");
            AssertTrue(history.TryAveragePeriod(3, night: true, out LineAverage night) && night.m_Samples == 6, "six night readings with vehicles out; the two idle ones do not count");
            AssertEqual(0.1f, night.m_Usage, 1e-6f, "night usage");
            AssertTrue(!history.TryAveragePeriod(4, night: true, out _), "an unknown line has no period average");
        }

        // The save file holds the windows' contents; a load re-records them. Samples
        // of several lines interleave in time, so the restore must go by frame across
        // lines — Record treats an older frame as a rewound clock and clears everything.
        private static void WindowsRoundTripThroughTheSave()
        {
            var history = new LineHistory(Assumptions.FramesPerGameDay);
            uint frame = 1000u;
            for (int i = 0; i < 6; i++)
            {
                frame += 2000u;
                history.Record(7, new LineObservation { m_Frame = frame, m_Passengers = 10 * i, m_Capacity = 100, m_IntervalSeconds = 120f, m_Vehicles = 2 });
                history.Record(9, new LineObservation { m_Frame = frame + 500u, m_Passengers = 5 * i, m_Capacity = 50, m_IntervalSeconds = 90f, m_Vehicles = 1 });
            }

            bool had7 = history.TryAverage(7, out LineAverage before7);
            bool had9 = history.TryAverage(9, out LineAverage before9);
            AssertTrue(had7 && had9, "both lines have averages");

            var flat = new List<(int line, LineObservation sample)>();
            foreach (int line in history.LineIds)
            {
                IReadOnlyList<LineObservation> samples = history.SamplesOf(line);
                for (int i = 0; i < samples.Count; i++)
                {
                    flat.Add((line, samples[i]));
                }
            }

            AssertEqual(12, flat.Count, 0, "every sample is exported");
            flat.Sort((a, b) => a.sample.m_Frame.CompareTo(b.sample.m_Frame));
            var restored = new LineHistory(Assumptions.FramesPerGameDay);
            for (int i = 0; i < flat.Count; i++)
            {
                restored.Record(flat[i].line, flat[i].sample);
            }

            bool has7 = restored.TryAverage(7, out LineAverage after7);
            bool has9 = restored.TryAverage(9, out LineAverage after9);
            AssertTrue(has7 && has9, "the restored window judges both lines");
            AssertEqual(before7.m_Usage, after7.m_Usage, 0f, "line 7 keeps its usage");
            AssertEqual(before9.m_Passengers, after9.m_Passengers, 0f, "line 9 keeps its passengers");
            AssertEqual(2, restored.TrackedLines, 0, "two lines tracked");

            var window = new ObservedTripWindow(Assumptions.FramesPerGameDay);
            for (int i = 0; i < 5; i++)
            {
                window.Record(new ObservedTrip { m_Frame = 100u * (uint)(i + 1), m_OriginX = i, m_OriginZ = 1f, m_DestinationX = 2f, m_DestinationZ = 3f, m_Purpose = (byte)(i % 2) });
            }

            var copy = new ObservedTripWindow(Assumptions.FramesPerGameDay);
            IReadOnlyList<ObservedTrip> trips = window.Trips;
            for (int i = 0; i < trips.Count; i++)
            {
                copy.Record(trips[i]);
            }

            AssertEqual(window.Count, copy.Count, 0, "every observed journey survives the round trip");
            AssertEqual(window.CountOf(1), copy.CountOf(1), 0, "the per-purpose counters are rebuilt");
            AssertEqual(window.SpanFrames, copy.SpanFrames, 0, "the span is unchanged");
        }

        // The simulation frame counts up within one city and rewinds when another
        // save is loaded. Averaging the previous city's lines into this one would be
        // worse than starting over.
        private static void WindowResetsWhenFramesRewind()
        {
            var history = new LineHistory(Assumptions.FramesPerGameDay);
            history.Record(2, Reading(500000u, 80));
            history.Record(2, Reading(500100u, 80));
            AssertTrue(history.TryAverage(2, out LineAverage loaded), "the first city has history");
            AssertEqual(80f, loaded.m_Passengers, 1e-3f, "from the first city");

            history.Record(2, Reading(120u, 4));
            AssertTrue(history.TryAverage(2, out LineAverage fresh), "the new city starts recording");
            AssertTrue(fresh.m_Samples == 1, "the previous city's readings are gone");
            AssertEqual(4f, fresh.m_Passengers, 1e-3f, "only this city's reading counts");
        }

        // Lines the player has deleted stop being tracked, so the history is bounded
        // by the network that exists rather than by everything ever built.
        private static void WindowForgetsDeletedLines()
        {
            var history = new LineHistory(Assumptions.FramesPerGameDay);
            history.Record(1, Reading(0u, 10));
            history.Record(2, Reading(0u, 20));
            AssertTrue(history.TrackedLines == 2, "both lines tracked");

            history.RetainOnly(new System.Collections.Generic.HashSet<int> { 2 });
            AssertTrue(history.TrackedLines == 1, "the deleted line is forgotten");
            AssertTrue(!history.TryAverage(1, out LineAverage _), "and has no history left");
            AssertTrue(history.TryAverage(2, out LineAverage _), "the surviving line keeps its history");
        }

        private static LineObservation Reading(uint frame, int passengers)
        {
            return new LineObservation
            {
                m_Frame = frame,
                m_Passengers = passengers,
                m_Capacity = 100,
                m_IntervalSeconds = 120f,
                m_Vehicles = 1,
            };
        }

        // ---- harness --------------------------------------------------------

        private static int CountSaturated(byte[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == 255)
                {
                    count++;
                }
            }
            return count;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A test runner must report any failure and continue; narrowing this " +
                "would let one unexpected exception abort the whole suite.")]
        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (TestFailedException failure)
            {
                s_Failures++;
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {failure.Message}");
            }
            catch (Exception ex)
            {
                // Distinguished from an assertion failure on purpose: a crash in the
                // code under test is a different problem from a property not holding,
                // and printing them identically hid which one had happened.
                s_Failures++;
                Console.WriteLine($"  CRASH {name}");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AssertTrue(bool condition, string because)
        {
            if (!condition)
            {
                throw new TestFailedException(because);
            }
        }

        private static void CapacityWeightIsRelativeToBus()
        {
            // CS2's base subway (1080 seats) against its bus (80): 13.5 buses' worth.
            AssertEqual(13.5f, TransitModes.CapacityWeight(1080f, 80f), 1e-6f, "subway vs bus");
            AssertEqual(1f, TransitModes.CapacityWeight(80f, 80f), 1e-6f, "a bus is one bus");
            AssertEqual(0f, TransitModes.CapacityWeight(0f, 80f), 0f, "unknown mode is no partner");
            AssertEqual(0f, TransitModes.CapacityWeight(1080f, 0f), 0f, "no bus to compare against");
        }

        private static void AssertEqual(float expected, float actual, float tolerance, string because)
        {
            if (float.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            {
                throw new TestFailedException($"{because}: expected {expected}, got {actual}");
            }
        }

        private static void AssertEqual(int expected, int actual, int tolerance, string because)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new TestFailedException($"{because}: expected {expected}, got {actual}");
            }
        }
    }
}
