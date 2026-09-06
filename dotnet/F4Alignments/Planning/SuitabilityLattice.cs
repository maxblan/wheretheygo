using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // A free-form graph laid over the map on a regular grid, for modes that are not
    // bound to the road network. Nodes exist only where the mask allows, and
    // 8-connected neighbours are joined so diagonals are available.
    //
    // This is what lets metro and ferry routes exist at all: the road graph cannot
    // express a tunnel or a boat crossing.
    internal static class SuitabilityLattice
    {

        // Builds a lattice over the tiles the mask admits. `costScale` is applied per
        // edge according to the midpoint's tile, which is how "prefer existing
        // track" is expressed: cheap where track exists, dear where it does not.
        public static CompactGraph Build(
            int2Like tileGrid,
            float2Like worldMin,
            float tileSize,
            Func<int, bool> tilePassable,
            Func<int, float>? tileCostScale,
            out float[] nodeX,
            out float[] nodeZ)
        {
            int cols = Math.Max(1, (int)Math.Floor(tileGrid.x * tileSize / Assumptions.LatticeSpacing));
            int rows = Math.Max(1, (int)Math.Floor(tileGrid.y * tileSize / Assumptions.LatticeSpacing));

            var index = new int[cols * rows];
            for (int i = 0; i < index.Length; i++)
            {
                index[i] = -1;
            }

            var xs = new List<float>();
            var zs = new List<float>();

            for (int gy = 0; gy < rows; gy++)
            {
                for (int gx = 0; gx < cols; gx++)
                {
                    float2Like world = worldMin + new float2Like((gx + 0.5f) * Assumptions.LatticeSpacing, (gy + 0.5f) * Assumptions.LatticeSpacing);
                    int tile = TileOf(world, worldMin, tileSize, tileGrid);
                    if (tile < 0 || !tilePassable(tile))
                    {
                        continue;
                    }

                    index[gx + gy * cols] = xs.Count;
                    xs.Add(world.x);
                    zs.Add(world.y);
                }
            }

            var edgeA = new List<int>();
            var edgeB = new List<int>();
            var edgeCost = new List<float>();
            float diagonal = Assumptions.LatticeSpacing * 1.41421356f;
            var build = new LatticeBuild(index, cols, rows, xs, zs, edgeA, edgeB, edgeCost, worldMin, tileSize, tileGrid, tileCostScale);

            for (int gy = 0; gy < rows; gy++)
            {
                for (int gx = 0; gx < cols; gx++)
                {
                    int from = index[gx + gy * cols];
                    if (from < 0)
                    {
                        continue;
                    }

                    // Only forward neighbours, so each edge is added once.
                    TryLink(in build, gx + 1, gy, from, Assumptions.LatticeSpacing);
                    TryLink(in build, gx, gy + 1, from, Assumptions.LatticeSpacing);
                    TryLink(in build, gx + 1, gy + 1, from, diagonal);
                    TryLink(in build, gx + 1, gy - 1, from, diagonal);
                }
            }

            nodeX = xs.ToArray();
            nodeZ = zs.ToArray();
            return CompactGraph.Build(xs.Count, edgeA.ToArray(), edgeB.ToArray(), edgeCost.ToArray(), edgeA.Count);
        }

        // Everything a link needs that does not change from one link to the next.
        // Passed one argument at a time this made TryLink a seventeen-parameter call
        // whose arguments could only be checked by counting — and two of them, the
        // source cell's own coordinates, were never read.
        private readonly struct LatticeBuild
        {
            public LatticeBuild(
                int[] index,
                int cols,
                int rows,
                List<float> nodeX,
                List<float> nodeZ,
                List<int> edgeA,
                List<int> edgeB,
                List<float> edgeCost,
                float2Like worldMin,
                float tileSize,
                int2Like tileGrid,
                Func<int, float>? tileCostScale)
            {
                Index = index;
                Cols = cols;
                Rows = rows;
                NodeX = nodeX;
                NodeZ = nodeZ;
                EdgeA = edgeA;
                EdgeB = edgeB;
                EdgeCost = edgeCost;
                WorldMin = worldMin;
                TileSize = tileSize;
                TileGrid = tileGrid;
                TileCostScale = tileCostScale;
            }

            public int[] Index { get; }

            public int Cols { get; }

            public int Rows { get; }

            public List<float> NodeX { get; }

            public List<float> NodeZ { get; }

            public List<int> EdgeA { get; }

            public List<int> EdgeB { get; }

            public List<float> EdgeCost { get; }

            public float2Like WorldMin { get; }

            public float TileSize { get; }

            public int2Like TileGrid { get; }

            public Func<int, float>? TileCostScale { get; }
        }

        private static void TryLink(in LatticeBuild build, int nx, int ny, int from, float baseCost)
        {
            if (nx < 0 || nx >= build.Cols || ny < 0 || ny >= build.Rows)
            {
                return;
            }

            int to = build.Index[nx + ny * build.Cols];
            if (to < 0)
            {
                return;
            }

            float cost = baseCost;
            if (build.TileCostScale is not null)
            {
                // Scale by the midpoint's tile, so an edge running along existing
                // track is cheap even though its endpoints straddle the grid.
                var mid = new float2Like(
                    (build.NodeX[from] + build.NodeX[to]) * 0.5f,
                    (build.NodeZ[from] + build.NodeZ[to]) * 0.5f);
                int tile = TileOf(mid, build.WorldMin, build.TileSize, build.TileGrid);
                if (tile >= 0)
                {
                    cost *= Math.Max(0.05f, build.TileCostScale(tile));
                }
            }

            build.EdgeA.Add(from);
            build.EdgeB.Add(to);
            build.EdgeCost.Add(cost);
        }

        private static int TileOf(float2Like world, float2Like worldMin, float tileSize, int2Like tileGrid)
        {
            int2Like cell = TileGrid.WorldToCell(world, worldMin, tileSize, tileGrid);
            int tile = cell.x + cell.y * tileGrid.x;
            return tile >= 0 && tile < tileGrid.x * tileGrid.y ? tile : -1;
        }

        // Marks the tiles existing rail runs through, by walking straight segments
        // between the endpoints of every track edge. Used both to make train routes
        // prefer existing alignment and to keep the lattice honest about what is
        // already built.
        public static void RasterizeTracks(
            List<float2Like> trackStarts,
            List<float2Like> trackEnds,
            int2Like tileGrid,
            float2Like worldMin,
            float tileSize,
            byte[] trackMask)
        {
            if (trackMask is null)
            {
                return;
            }

            Array.Clear(trackMask, 0, trackMask.Length);

            if (trackStarts.Count != trackEnds.Count)
            {
                DeferredLog.Warn(
                    $"Track segments came back mismatched ({trackStarts.Count} starts, {trackEnds.Count} ends); " +
                    "rail reuse will be judged on the shorter list.");
            }

            for (int i = 0; i < trackStarts.Count && i < trackEnds.Count; i++)
            {
                float2Like a = (trackStarts[i] - worldMin) / tileSize;
                float2Like b = (trackEnds[i] - worldMin) / tileSize;
                MarkLine(trackMask, tileGrid, a, b);
            }
        }

        private static void MarkLine(byte[] mask, int2Like grid, float2Like a, float2Like b)
        {
            float dx = b.x - a.x;
            float dy = b.y - a.y;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
            if (steps <= 0)
            {
                Mark(mask, grid, (int)Math.Floor(a.x), (int)Math.Floor(a.y));
                return;
            }

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Mark(mask, grid, (int)Math.Floor(a.x + dx * t), (int)Math.Floor(a.y + dy * t));
            }
        }

        private static void Mark(byte[] mask, int2Like grid, int x, int y)
        {
            // A one-tile halo, so a lattice edge running roughly parallel to a track
            // still counts as reusing it.
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= grid.y)
                {
                    continue;
                }
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    if (nx < 0 || nx >= grid.x)
                    {
                        continue;
                    }
                    mask[nx + ny * grid.x] = 1;
                }
            }
        }

        // Cost multiplier for a lattice edge, given whether existing rail runs there.
        //
        // Train reuses track wherever possible because new heavy rail is expensive to
        // acquire, but will strike out on fresh alignment when it has to. Metro is the
        // other way round: tunnelling is its normal mode, and existing track is merely
        // an option. Both stay usable on either.
        public static float RailCostScale(bool onExistingTrack)
        {
            // Train and metro alike (register A4.5, decided 2026-09-05): an alignment
            // along track the city already has is cheap to build for either.
            return onExistingTrack ? 0.35f : 1.6f;
        }
    }
}
