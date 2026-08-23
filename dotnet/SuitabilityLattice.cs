using System.Collections.Generic;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // Which network a mode's routes are traced over.
    internal enum RouteNetwork
    {
        // Streets: buses and trams have to use the road network.
        Road = 0,
        // Land lattice blended with existing rail. Trains prefer to reuse track that
        // already exists and only strike out on new alignment when they must; metros
        // are the other way round, since a tunnel goes wherever it likes.
        Rail = 1,
        // Open water, for ferries.
        Water = 2,
    }

    // A free-form graph laid over the map on a regular grid, for modes that are not
    // bound to the road network. Nodes exist only where the mask allows, and
    // 8-connected neighbours are joined so diagonals are available.
    //
    // This is what lets metro and ferry routes exist at all: the road graph cannot
    // express a tunnel or a boat crossing.
    internal static class SuitabilityLattice
    {
        // Lattice pitch. Coarse enough that a city-sized map stays a few thousand
        // nodes, fine enough that a corridor still bends around obstacles.
        public const float Spacing = 128f;

        // Builds a lattice over the tiles the mask admits. `costScale` is applied per
        // edge according to the midpoint's tile, which is how "prefer existing
        // track" is expressed: cheap where track exists, dear where it does not.
        public static CompactGraph Build(
            int2 tileGrid,
            float2 worldMin,
            float tileSize,
            System.Func<int, bool> tilePassable,
            System.Func<int, float>? tileCostScale,
            out float[] nodeX,
            out float[] nodeZ)
        {
            int cols = math.max(1, (int)math.floor(tileGrid.x * tileSize / Spacing));
            int rows = math.max(1, (int)math.floor(tileGrid.y * tileSize / Spacing));

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
                    float2 world = worldMin + new float2((gx + 0.5f) * Spacing, (gy + 0.5f) * Spacing);
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
            float diagonal = Spacing * 1.41421356f;

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
                    TryLink(index, cols, rows, gx, gy, gx + 1, gy, from, Spacing, edgeA, edgeB, edgeCost, xs, zs, worldMin, tileSize, tileGrid, tileCostScale);
                    TryLink(index, cols, rows, gx, gy, gx, gy + 1, from, Spacing, edgeA, edgeB, edgeCost, xs, zs, worldMin, tileSize, tileGrid, tileCostScale);
                    TryLink(index, cols, rows, gx, gy, gx + 1, gy + 1, from, diagonal, edgeA, edgeB, edgeCost, xs, zs, worldMin, tileSize, tileGrid, tileCostScale);
                    TryLink(index, cols, rows, gx, gy, gx + 1, gy - 1, from, diagonal, edgeA, edgeB, edgeCost, xs, zs, worldMin, tileSize, tileGrid, tileCostScale);
                }
            }

            nodeX = xs.ToArray();
            nodeZ = zs.ToArray();
            return CompactGraph.Build(xs.Count, edgeA.ToArray(), edgeB.ToArray(), edgeCost.ToArray(), edgeA.Count);
        }

        private static void TryLink(
            int[] index,
            int cols,
            int rows,
            int gx,
            int gy,
            int nx,
            int ny,
            int from,
            float baseCost,
            List<int> edgeA,
            List<int> edgeB,
            List<float> edgeCost,
            List<float> xs,
            List<float> zs,
            float2 worldMin,
            float tileSize,
            int2 tileGrid,
            System.Func<int, float>? tileCostScale)
        {
            if (nx < 0 || nx >= cols || ny < 0 || ny >= rows)
            {
                return;
            }

            int to = index[nx + ny * cols];
            if (to < 0)
            {
                return;
            }

            float cost = baseCost;
            if (tileCostScale is not null)
            {
                // Scale by the midpoint's tile, so an edge running along existing
                // track is cheap even though its endpoints straddle the grid.
                var mid = new float2((xs[from] + xs[to]) * 0.5f, (zs[from] + zs[to]) * 0.5f);
                int tile = TileOf(mid, worldMin, tileSize, tileGrid);
                if (tile >= 0)
                {
                    cost *= math.max(0.05f, tileCostScale(tile));
                }
            }

            edgeA.Add(from);
            edgeB.Add(to);
            edgeCost.Add(cost);
        }

        private static int TileOf(float2 world, float2 worldMin, float tileSize, int2 tileGrid)
        {
            int2 cell = SuitabilityInputs.WorldToCell(world, worldMin, tileSize, tileGrid);
            int tile = cell.x + cell.y * tileGrid.x;
            return tile >= 0 && tile < tileGrid.x * tileGrid.y ? tile : -1;
        }

        // Marks the tiles existing rail runs through, by walking straight segments
        // between the endpoints of every track edge. Used both to make train routes
        // prefer existing alignment and to keep the lattice honest about what is
        // already built.
        public static void RasterizeTracks(
            List<float2> trackStarts,
            List<float2> trackEnds,
            int2 tileGrid,
            float2 worldMin,
            float tileSize,
            byte[] trackMask)
        {
            if (trackMask is null)
            {
                return;
            }

            System.Array.Clear(trackMask, 0, trackMask.Length);

            for (int i = 0; i < trackStarts.Count && i < trackEnds.Count; i++)
            {
                float2 a = (trackStarts[i] - worldMin) / tileSize;
                float2 b = (trackEnds[i] - worldMin) / tileSize;
                MarkLine(trackMask, tileGrid, a, b);
            }
        }

        private static void MarkLine(byte[] mask, int2 grid, float2 a, float2 b)
        {
            float dx = b.x - a.x;
            float dy = b.y - a.y;
            int steps = (int)math.ceil(math.max(math.abs(dx), math.abs(dy)));
            if (steps <= 0)
            {
                Mark(mask, grid, (int)math.floor(a.x), (int)math.floor(a.y));
                return;
            }

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Mark(mask, grid, (int)math.floor(a.x + dx * t), (int)math.floor(a.y + dy * t));
            }
        }

        private static void Mark(byte[] mask, int2 grid, int x, int y)
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

        public static RouteNetwork NetworkFor(Setting.ModePreset mode)
        {
            switch (mode)
            {
                case Setting.ModePreset.Metro:
                case Setting.ModePreset.Train:
                    return RouteNetwork.Rail;
                case Setting.ModePreset.Ferry:
                    return RouteNetwork.Water;
                default:
                    return RouteNetwork.Road;
            }
        }

        // Cost multiplier for a lattice edge, given whether existing rail runs there.
        //
        // Train reuses track wherever possible because new heavy rail is expensive to
        // acquire, but will strike out on fresh alignment when it has to. Metro is the
        // other way round: tunnelling is its normal mode, and existing track is merely
        // an option. Both stay usable on either.
        public static float RailCostScale(Setting.ModePreset mode, bool onExistingTrack)
        {
            if (mode == Setting.ModePreset.Train)
            {
                return onExistingTrack ? 0.35f : 1.6f;
            }

            // Metro
            return onExistingTrack ? 0.9f : 1f;
        }
    }
}
