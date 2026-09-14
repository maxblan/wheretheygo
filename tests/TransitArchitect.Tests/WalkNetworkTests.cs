using System;
using System.Collections.Generic;
using System.Globalization;

namespace TransitArchitect.Tests
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
            TileSnap snap = SuitabilityWalkAccess.SnapTiles(graph, 4, 2, 0f, 0f, 32f, Assumptions.AccessWalkMs);

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
            TileSnap none = SuitabilityWalkAccess.SnapTiles(empty, 2, 2, 0f, 0f, 32f, Assumptions.AccessWalkMs);
            AssertTrue(none.TilesOnNetwork == 0 && none.TileNode[0] == -1 && none.TileWalkMs[3] == -1,
                "a city with no pedestrian network snaps nothing");
        }
    }
}
