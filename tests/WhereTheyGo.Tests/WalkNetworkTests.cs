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

    }
}
