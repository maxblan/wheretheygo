using System;
using System.Collections.Generic;
using System.Globalization;

namespace WhereTheyGo.Tests
{
    // The pedestrian network offline: the bridging that stitches the game's data back
    // together, and the tile snap the access field is laid out on.
    internal static partial class Program
    {
        // Bridging the pedestrian network. Two streets of three nodes each, 40 m apart
        // and joined by nothing, plus one node 300 m away that nothing should reach.
        internal static void WalkBridgingJoinsWhatTheDataCuts()
        {
            var nodeX = new[] { 0f, 30f, 60f, 0f, 30f, 60f, 400f };
            var nodeZ = new[] { 0f, 0f, 0f, 40f, 40f, 40f, 0f };
            var a = new List<int> { 0, 1, 3, 4 };
            var b = new List<int> { 1, 2, 4, 5 };
            var metres = new List<float> { 30f, 30f, 30f, 30f };

            int added = WalkBridging.Bridge(nodeX, nodeZ, a, b, metres, Assumptions.WalkBridgeMetres);
            AssertTrue(added == 1, $"one link joins the two streets, got {added.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(a.Count == 5 && b.Count == 5 && metres.Count == 5, "the link is appended to the same three lists");
            AssertBits(0x42200000u, metres[4], "and is charged its straight-line 40 m");

            // Determinism: the same inputs give the same link, and it is the nearest
            // pair with the lowest indices, not whichever the buckets happened to hit.
            AssertTrue(a[4] == 0 && b[4] == 3, $"the lowest-index nearest pair, got {a[4].ToString(CultureInfo.InvariantCulture)}->{b[4].ToString(CultureInfo.InvariantCulture)}");

            // The far node stays out: 400 m is not a walk across a junction.
            var reach = new HashSet<int>();
            for (int e = 0; e < a.Count; e++)
            {
                _ = reach.Add(a[e]);
                _ = reach.Add(b[e]);
            }

            AssertTrue(!reach.Contains(6), "a node 340 m from anything is left alone");

            // Idempotent: a graph already in one piece gains nothing.
            AssertTrue(WalkBridging.Bridge(nodeX, nodeZ, a, b, metres, Assumptions.WalkBridgeMetres) == 0,
                "a connected graph is left alone");
        }

        // The tile snap: every tile takes the nearest SITEABLE node within the access
        // walk, and -1 where there is none. A straight line of nodes 72 m apart along
        // z = 0, on a 4x2 grid of 32 m tiles starting at the origin.
        private static void TileSnapAttachesTilesToTheNearestPavement()
        {
            WalkGraph graph = LineGraph(3, 72f);
            graph.Siteable[1] = false;
            TileSnap snap = WalkAccess.SnapTiles(graph, 4, 2, 0f, 0f, 32f, Assumptions.AccessWalkMs);

            AssertTrue(snap.Index is not null, "the snap carries the index it searched with");
            AssertTrue(ReferenceEquals(snap.Graph, graph), "and the graph its indices belong to");
            AssertTrue(snap.TileNode.Length == 8 && snap.TileWalkMs.Length == 8, "one entry per tile");
            // Tile 0's centre is (16, 16); node 0 sits at (0, 0) and node 1 is not
            // siteable, so every tile on the first row takes node 0 or node 2.
            AssertTrue(snap.TileNode[0] == 0, "the first tile takes the node it stands on");
            AssertTrue(snap.TileNode[1] != 1 && snap.TileNode[3] != 1, "a node in a tunnel is never snapped to");
            AssertTrue(snap.TileWalkMs[0] == WalkGraph.WalkMilliseconds(Math.Sqrt((16.0 * 16.0) + (16.0 * 16.0))),
                "the walk is the straight line to the node, in whole milliseconds");

            for (int i = 0; i < snap.TileNode.Length; i++)
            {
                AssertTrue((snap.TileNode[i] >= 0) == (snap.TileWalkMs[i] >= 0), "a tile has a node and a walk, or neither");
            }

            AssertTrue(snap.TilesOnNetwork is > 0 and <= 8, "the count of attached tiles is reported");

            var empty = WalkGraph.Build(Array.Empty<float>(), Array.Empty<float>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<float>(), 0);
            TileSnap none = WalkAccess.SnapTiles(empty, 2, 2, 0f, 0f, 32f, Assumptions.AccessWalkMs);
            AssertTrue(none.TilesOnNetwork == 0 && none.TileNode[0] == -1 && none.TileWalkMs[3] == -1,
                "a city with no pedestrian network snaps nothing");
        }
        // The panel draws the walk to transit as four classes, and the classes move
        // with the horizon the player set rather than sitting at fixed minutes. What
        // matters is that they partition: every journey lands in exactly one, and the
        // last one holds everything the field never reached.
        internal static void WalkClassesPartitionEveryJourney()
        {
            // Thirty nodes a minute apart, one served stop at node 1, ten-minute
            // horizon, and a field searched four times as far - the same arrangement
            // the coverage pass uses.
            WalkGraph graph = LineGraph(30, 72f);
            var dijkstra = new IntDijkstra(graph.NodeCount);
            const int horizon = 600_000;
            int[] served = Coverage.ServedWalkMs(
                graph, dijkstra, new[] { 1 }, new[] { 0 }, 1, horizon * Assumptions.AccessFieldHorizonMultiple);

            AssertEqual(0, Coverage.WalkClassOf(0, horizon), 0, "at the stop is the nearest class");
            AssertEqual(0, Coverage.WalkClassOf(299_999, horizon), 0, "just under five minutes still is");
            AssertEqual(1, Coverage.WalkClassOf(300_000, horizon), 0, "five minutes exactly starts the second class");
            AssertEqual(2, Coverage.WalkClassOf(horizon, horizon), 0, "the horizon itself starts the third");
            AssertEqual(3, Coverage.WalkClassOf(1_200_000, horizon), 0, "twice the horizon is the last class");
            AssertEqual(3, Coverage.WalkClassOf(long.MaxValue, horizon), 0, "and so is a stop nothing reaches");

            // Origins at 2, 7, 13 and 25 minutes' walk, plus one off the network.
            var origin = new[] { 3, 8, 14, 26, -1 };
            var originAccess = new[] { 0, 0, 0, 0, 0 };
            var dest = new[] { 3, 8, 14, 26, 3 };
            var destAccess = new[] { 0, 0, 0, 0, 0 };
            var weight = new[] { 4f, 3f, 2f, 1f, 2f };
            CoverageReport report = Coverage.Measure(served, horizon, origin, originAccess, dest, destAccess, weight, 5);

            AssertEqual(4f / 12f, report.WalkClassShare[0], 1e-4f, "two minutes out is the nearest class");
            AssertEqual(3f / 12f, report.WalkClassShare[1], 1e-4f, "seven minutes is inside the horizon");
            AssertEqual(2f / 12f, report.WalkClassShare[2], 1e-4f, "thirteen minutes is past it but measured");
            AssertEqual(3f / 12f, report.WalkClassShare[3], 1e-4f, "twenty-five minutes and the off-network end share the last class");

            float sum = 0f;
            for (int c = 0; c < Coverage.WalkClassCount; c++)
            {
                sum += report.WalkClassShare[c];
            }

            AssertEqual(1f, sum, 1e-4f, "the four classes are the whole city and nothing twice");

            // No journeys at all must not divide by a total of zero.
            CoverageReport nothing = Coverage.Measure(
                served, horizon, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<float>(), 0);
            for (int c = 0; c < Coverage.WalkClassCount; c++)
            {
                AssertEqual(0f, nothing.WalkClassShare[c], 0f, "an empty city has no classes, not a division by zero");
            }
        }

        // The memo is the search's own answer, kept: every journey end it snaps reads
        // exactly what WalkAccess.SnapPoint would, and a door seen twice is searched once.
        internal static void SnapMemoAnswersAsTheSearchDoes()
        {
            var x = new float[] { 0f, 100f, 100f, 400f };
            var z = new float[] { 0f, 0f, 0f, 0f };
            WalkGraph graph = WalkGraph.Build(x, z, new[] { 0, 1, 2 }, new[] { 1, 2, 3 }, new[] { 100f, 1f, 300f }, 3);
            var index = new WalkNodeIndex(graph, 144.0);
            var memo = new SnapMemo(index, Assumptions.AccessWalkMs, Assumptions.SnapMemoCapacity);

            // Two households to one workplace, one of them twice, and a door far off
            // the network.
            var journeys = new List<Journey>
            {
                new Journey { m_Origin = new float2Like(0f, 60f), m_Destination = new float2Like(390f, 10f), m_Weight = 1f },
                new Journey { m_Origin = new float2Like(0f, 60f), m_Destination = new float2Like(390f, 10f), m_Weight = 2f },
                new Journey { m_Origin = new float2Like(60f, 0f), m_Destination = new float2Like(390f, 10f), m_Weight = 1f },
                new Journey { m_Origin = new float2Like(250f, 900f), m_Destination = new float2Like(60f, 0f), m_Weight = 1f },
            };
            JourneyEnds ends = JourneyEnds.Snap(journeys, memo, null);
            AssertEqual(4, ends.Count, 0, "one entry per journey");
            for (int i = 0; i < journeys.Count; i++)
            {
                int origin = WalkAccess.SnapPoint(index, journeys[i].m_Origin.x, journeys[i].m_Origin.y, Assumptions.AccessWalkMs, out int originMs);
                int destination = WalkAccess.SnapPoint(index, journeys[i].m_Destination.x, journeys[i].m_Destination.y, Assumptions.AccessWalkMs, out int destinationMs);
                string which = $"journey {i.ToString(CultureInfo.InvariantCulture)}";
                AssertEqual(origin, ends.OriginNode[i], 0, $"{which}: origin node as the search finds it");
                AssertEqual(originMs, ends.OriginAccessMs[i], 0, $"{which}: origin walk as the search finds it");
                AssertEqual(destination, ends.DestinationNode[i], 0, $"{which}: destination node as the search finds it");
                AssertEqual(destinationMs, ends.DestinationAccessMs[i], 0, $"{which}: destination walk as the search finds it");
                AssertBits((uint)BitConverter.SingleToInt32Bits(journeys[i].m_Weight), ends.Weight[i], $"{which}: weight carried over");
            }

            AssertEqual(-1, ends.OriginNode[3], 0, "the door 900 m off the network stays off it");
            AssertEqual(4, memo.Searches, 0, "four distinct doors, four searches");
            AssertEqual(4, memo.Count, 0, "and four remembered");

            // The next refresh, same doors: nothing is searched again.
            memo.ResetSearches();
            JourneyEnds again = JourneyEnds.Snap(journeys.GetRange(0, 2), memo, ends);
            AssertEqual(0, memo.Searches, 0, "a refresh over the same doors searches none");
            AssertTrue(ReferenceEquals(ends, again) && again.Count == 2 && again.Weight.Length == 4,
                "a smaller refresh snaps into the same buffers, and Count says how much of them it filled");
            AssertEqual(2f, again.Weight[1], 0f, "and answers the same");
        }

        // The memo decides its own validity: it is kept for the index it was made for
        // and replaced for any other, whose node numbers belong to another graph; and
        // at its capacity it starts over rather than growing.
        internal static void SnapMemoBelongsToOneIndexAndIsBounded()
        {
            var x = new float[] { 0f, 100f, 200f };
            var z = new float[3];
            WalkGraph graph = WalkGraph.Build(x, z, new[] { 0, 1 }, new[] { 1, 2 }, new[] { 100f, 100f }, 2);
            var index = new WalkNodeIndex(graph, 144.0);
            var other = new WalkNodeIndex(graph, 144.0);

            SnapMemo memo = SnapMemo.For(null, index, Assumptions.AccessWalkMs, 2);
            AssertTrue(ReferenceEquals(memo, SnapMemo.For(memo, index, Assumptions.AccessWalkMs, 2)), "the same index keeps the memo");
            AssertTrue(!ReferenceEquals(memo, SnapMemo.For(memo, other, Assumptions.AccessWalkMs, 2)), "another index, even of an equal graph, gets a fresh one");
            AssertTrue(!ReferenceEquals(memo, SnapMemo.For(memo, index, Assumptions.AccessWalkMs / 2, 2)), "and so does another access walk");

            _ = memo.Snap(0f, 10f, out _);
            _ = memo.Snap(100f, 10f, out _);
            AssertEqual(2, memo.Count, 0, "two doors fill a memo of two");
            int node = memo.Snap(200f, 10f, out int walkMs);
            int expected = WalkAccess.SnapPoint(index, 200f, 10f, Assumptions.AccessWalkMs, out int expectedMs);
            AssertEqual(1, memo.Count, 0, "a third starts it over instead of growing it");
            AssertTrue(node == expected && walkMs == expectedMs, "and is still answered as the search answers it");
        }

        // The tile field: a tile's walk is its node's walk to a stop plus its own walk
        // to the node, ramped to bytes over the horizon; a grid that does not match the
        // snap gives no field at all rather than a misaligned one.
        internal static void ServedWalkFieldRampsOverTheHorizon()
        {
            var x = new float[] { 16f, 336f };
            var z = new float[] { 16f, 16f };
            WalkGraph graph = WalkGraph.Build(x, z, new[] { 0 }, new[] { 1 }, new[] { 320f }, 1);
            TileSnap snap = WalkAccess.SnapTiles(graph, 12, 1, 0f, 0f, Assumptions.TileSize, Assumptions.AccessWalkMs);
            // The stop stands on node 0; node 1 is a minute's walk from it.
            var served = new[] { 0, 60_000 };
            int horizonMs = 300_000;

            ServedWalkField? field = ServedWalkField.Build(snap, served, horizonMs, new int2Like(12, 1));
            AssertTrue(field is not null, "a matching grid gives a field");
            if (field is null)
            {
                return;
            }

            AssertEqual(0, field.WalkMs[0], 0, "the tile on the stop's node walks nothing");
            AssertTrue(field.ByTile[0] == 0, "and is the near end of the ramp");
            AssertTrue(field.WalkMs[11] > field.WalkMs[1] && field.ByTile[11] >= field.ByTile[1], "walks grow away from the stop");
            AssertTrue(field.Reached == 12 && field.ByTile.Length == 12, "every tile of this street is within five minutes");
            AssertTrue(ServedWalkField.Build(snap, served, horizonMs, new int2Like(6, 1)) is null, "a grid that does not match the snap gives none");
            AssertTrue(ServedWalkField.Build(snap, served, 0, new int2Like(12, 1)) is null, "and so does no horizon");

            float2Like[] stops = { new float2Like(16f, 16f) };
            long signature = ServedWalkField.Signature(stops, graph, horizonMs);
            AssertTrue(signature == ServedWalkField.Signature(new[] { new float2Like(16.2f, 16f) }, graph, horizonMs), "a stop moved by a fraction of a metre is the same field");
            AssertTrue(signature != ServedWalkField.Signature(stops, graph, horizonMs * 2), "another horizon is another field");
        }

        // The demand stage end to end: whatever order the trips arrive in, the
        // journeys come out in one order, the ends are snapped index-aligned with
        // THOSE journeys, the share is measured on them and the pairs built from them.
        internal static void DemandStageMeasuresAndPairsTheSameJourneys()
        {
            var x = new float[] { 16f, 336f, 656f };
            var z = new float[] { 16f, 16f, 16f };
            WalkGraph graph = WalkGraph.Build(x, z, new[] { 0, 1 }, new[] { 1, 2 }, new[] { 320f, 320f }, 2);
            TileSnap snap = WalkAccess.SnapTiles(graph, 24, 2, 0f, 0f, Assumptions.TileSize, Assumptions.AccessWalkMs);
            var trips = new List<Journey>
            {
                new Journey { m_Origin = new float2Like(20f, 40f), m_Destination = new float2Like(650f, 30f), m_Weight = 1f, m_OutHour = 8, m_BackHour = 17 },
                new Journey { m_Origin = new float2Like(340f, 20f), m_Destination = new float2Like(20f, 40f), m_Weight = 2f, m_OutHour = 9, m_BackHour = 18 },
                new Journey { m_Origin = new float2Like(20f, 40f), m_Destination = new float2Like(650f, 30f), m_Weight = 1f, m_OutHour = 8, m_BackHour = 17 },
                new Journey { m_Origin = new float2Like(660f, 20f), m_Destination = new float2Like(340f, 20f), m_Weight = 1f, m_OutHour = 23, m_BackHour = 3 },
            };

            WalkNodeIndex index = snap.Index ?? throw new TestFailedException("the snap has an index");
            DemandStage first = RunStage(snap, trips, rebuild: true, served: null);
            var reversed = new List<Journey>(trips);
            reversed.Reverse();
            DemandStage second = RunStage(snap, reversed, rebuild: false, served: first.Served);

            AssertTrue(first.Done && second.Done, "a finished stage says so");
            AssertTrue(first.Trips.Count == 0, "the drained trips are emptied once ordered");
            AssertTrue(first.FieldRebuilt && first.Field is not null && first.Served is not null, "asked to, the stage builds the field");
            AssertTrue(!second.FieldRebuilt && second.Field is null && ReferenceEquals(second.Served, first.Served), "otherwise it reads the one in place");
            AssertEqual(4, first.TripCount, 0, "every trip between two zones is a journey");
            AssertEqual(5f, first.TotalWeight, 0f, "and their weight is summed");

            JourneyEnds? ends = first.Ends;
            AssertTrue(ends is not null && ends.Count == first.Journeys.Count, "one snapped entry per ordered journey");
            for (int i = 0; ends is not null && i < first.Journeys.Count; i++)
            {
                Journey journey = first.Journeys[i];
                string which = $"journey {i.ToString(CultureInfo.InvariantCulture)}";
                AssertTrue(Same(journey, second.Journeys[i]), $"{which}: the order does not depend on the order trips arrived in");
                int origin = WalkAccess.SnapPoint(index, journey.m_Origin.x, journey.m_Origin.y, Assumptions.AccessWalkMs, out _);
                AssertEqual(origin, ends.OriginNode[i], 0, $"{which}: its ends are snapped where it starts, index for index");
            }

            AssertTrue(first.Report is not null && second.Report is not null, "the share is measured");
            AssertTrue(first.Report is not null && second.Report is not null
                && first.Report.CoveredWeight == second.Report.CoveredWeight
                && first.Report.GiniWalk == second.Report.GiniWalk,
                "the same journeys measure the same, to the bit");
            AssertEqual(3, first.Pairs.PairCount, 0, "the two identical trips are one door pair");
            AssertTrue(first.Pairs.PairOx[0] == first.Journeys[0].m_Origin.x, "pairs follow the ordered journeys");

            var empty = new DemandStage { Trips = new List<Journey>(trips), HorizonMs = 300_000 };
            empty.Run(new float2Like(0f, 0f), new int2Like(4, 1));
            AssertTrue(empty.Done && empty.Report is null && empty.Pairs.PairCount == 3, "with no pedestrian network yet the journeys are still ordered and paired, and nothing is measured");
        }

        private static DemandStage RunStage(TileSnap snap, List<Journey> trips, bool rebuild, int[]? served)
        {
            WalkNodeIndex index = snap.Index ?? throw new TestFailedException("the snap has an index");
            var stage = new DemandStage
            {
                Trips = new List<Journey>(trips),
                Snap = snap,
                Memo = new SnapMemo(index, Assumptions.AccessWalkMs, Assumptions.SnapMemoCapacity),
                Stops = new[] { new float2Like(16f, 16f) },
                HorizonMs = 300_000,
                RebuildField = rebuild,
                Served = served,
                FieldGrid = new int2Like(24, 2),
            };
            // Zones of 256 m along the street: the three nodes fall in three of them.
            stage.Run(new float2Like(0f, 0f), new int2Like(4, 1));
            return stage;
        }

        private static bool Same(Journey a, Journey b)
        {
            return a.m_Origin.x == b.m_Origin.x && a.m_Origin.y == b.m_Origin.y
                && a.m_Destination.x == b.m_Destination.x && a.m_Destination.y == b.m_Destination.y
                && a.m_Weight == b.m_Weight && a.m_OutHour == b.m_OutHour;
        }

        // The pair table merges trips between the same two doors, in first-seen order,
        // and a selection pass attached to it shares the arrays rather than copying.
        internal static void DoorPairsMergeTheSameTwoDoors()
        {
            var journeys = new List<Journey>
            {
                new Journey { m_Origin = new float2Like(0f, 0f), m_Destination = new float2Like(500f, 0f), m_Weight = 1f, m_OutHour = 8, m_BackHour = 17 },
                new Journey { m_Origin = new float2Like(0f, 0f), m_Destination = new float2Like(0f, 500f), m_Weight = 1f, m_OutHour = 8, m_BackHour = 17 },
                new Journey { m_Origin = new float2Like(0f, 0f), m_Destination = new float2Like(500f, 0f), m_Weight = 2f, m_OutHour = 23, m_BackHour = 3 },
            };
            RoutingProblem pairs = DoorPairs.Build(journeys);
            AssertEqual(2, pairs.PairCount, 0, "three trips between two door pairs");
            AssertEqual(500f, pairs.PairDx[0], 0f, "the first pair is the first seen");
            AssertEqual(3f, pairs.PairWeight[0], 0f, "its weight is the two trips summed");
            AssertEqual(1f / 3f, pairs.PairDayShare[0], 1e-6f, "its day share is weighted by trip");
            AssertTrue(pairs.Geometry is not null, "the geometry is built with the table");

            var problem = new RoutingProblem();
            DoorPairs.Attach(problem, pairs);
            AssertTrue(problem.PairCount == 2 && ReferenceEquals(problem.PairOx, pairs.PairOx) && ReferenceEquals(problem.Geometry, pairs.Geometry),
                "an attached problem shares the table's arrays and geometry");
        }
    }
}
