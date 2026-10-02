using System;

namespace WhereTheyGo
{
    // The served-walk field and the tile field the buildings are coloured from, built
    // from the stops and the pedestrian network alone: nothing here depends on the
    // journeys, which is why it is rebuilt only when its Signature moves. Built on the
    // worker with the rest of the demand stage and published whole, so the colours,
    // the horizon and the coverage share always describe the same measurement.
    internal sealed class ServedWalkField
    {
        // Each tile's walk to the nearest served stop in milliseconds, int.MaxValue
        // beyond the search: what the selected-building row reads.
        public int[] WalkMs = Array.Empty<int>();

        // The same walks as 0 (at a stop) to 255 (at or beyond the horizon): what the
        // buildings are coloured from.
        public byte[] ByTile = Array.Empty<byte>();

        public int2Like Grid;

        // Tiles within the horizon of a served stop.
        public int Reached;

        // Enough of the inputs to tell one field from another: how many stops there
        // are and where, the graph the walk runs over, and the horizon the player set.
        public static long Signature(float2Like[] stops, WalkGraph graph, int horizonMs)
        {
            unchecked
            {
                long signature = stops.Length;
                for (int i = 0; i < stops.Length; i++)
                {
                    signature = (signature * 31) + (long)Math.Round(stops[i].x, MidpointRounding.ToEven);
                    signature = (signature * 31) + (long)Math.Round(stops[i].y, MidpointRounding.ToEven);
                }

                signature = (signature * 31) + graph.NodeCount;
                signature = (signature * 31) + graph.EdgeMetres.Length;
                signature = (signature * 31) + horizonMs;
                return signature;
            }
        }

        // The served-walk field rasterised onto the tile grid: how long a
        // walk from each tile to the nearest stop the city's lines actually serve, as 0
        // (at a stop) to 255 (at or beyond the walking horizon). This is what colours the
        // buildings and what the selected-building row reports.
        //
        // Two steps, and the second one is the point. TileSnap.TileNode only
        // holds a node for a tile within the ACCESS budget of the pedestrian network,
        // 8110 of 200704 tiles on Valmare, because that budget answers a different
        // question: how far a STOP may stand from a road. Reading it as "this tile has no
        // transit" put a house at one minute and the house next door at over ten, all
        // over the city, and painted a tram terminus as unserved. So tiles without a node
        // of their own take the walk of a nearby tile that has one, plus the walk between
        // them.
        //
        // Fresh arrays every build: the previous field stays on the map, untouched,
        // until this one is published. Null when the snap and the grid disagree, or
        // there is no horizon to measure against.
        public static ServedWalkField? Build(TileSnap access, int[] served, int horizonMs, int2Like grid)
        {
            if (horizonMs <= 0 || access.TileNode.Length == 0)
            {
                return null;
            }

            int count = access.TileNode.Length;
            // The grid the snap was laid out on, which is the population map's. Not the
            // terrain's playable grid: the two agree on every map seen so far, but a map
            // where they did not would have failed this check and silently left every
            // building grey.
            if (grid.x <= 0 || grid.y <= 0 || grid.x * grid.y != count)
            {
                return null;
            }

            var walkMs = new int[count];
            for (int i = 0; i < count; i++)
            {
                int node = access.TileNode[i];
                long walk = node >= 0 && node < served.Length && served[node] != Coverage.NotServed
                    ? (long)served[node] + access.TileWalkMs[i]
                    : int.MaxValue;
                walkMs[i] = walk >= int.MaxValue ? int.MaxValue : (int)walk;
            }

            Spread(walkMs, new int[count], grid);

            // The COLOUR ramp still runs over the horizon and no further: past it every
            // walk is equally bad to look at, and stretching the ramp to the longest
            // walk on the map would wash out the difference between two and eight
            // minutes, which is the difference that matters.
            var field = new ServedWalkField { WalkMs = walkMs, ByTile = new byte[count], Grid = grid };
            for (int i = 0; i < count; i++)
            {
                if (walkMs[i] == int.MaxValue || walkMs[i] >= horizonMs)
                {
                    field.ByTile[i] = 255;
                    continue;
                }

                field.Reached++;
                field.ByTile[i] = (byte)((long)walkMs[i] * 255L / horizonMs);
            }

            return field;
        }

        public static void SnapStops(float2Like[] stops, WalkNodeIndex index, int accessMs, out int[] nodes, out int[] access)
        {
            nodes = new int[stops.Length];
            access = new int[stops.Length];
            for (int i = 0; i < stops.Length; i++)
            {
                nodes[i] = WalkAccess.SnapPoint(index, stops[i].x, stops[i].y, accessMs, out access[i]);
            }
        }

        // Fills in tiles that have no pedestrian node of their own from tiles that do,
        // charging the walk between tile centres. Two sweeps of a chamfer distance
        // transform, forward over increasing indices and backward over decreasing, which
        // is exact for this cost pattern and costs two passes over the grid.
        //
        // Bounded to Assumptions.AccessFieldSpreadTiles deliberately. Unbounded, it would
        // walk straight over a river to a stop on the far bank and report a walk nobody
        // can make; bounded, it bridges a house to its own street and no further.
        private static void Spread(int[] walkMs, int[] spreadMs, int2Like grid)
        {
            int straight = (int)(Assumptions.TileSize / Assumptions.WalkSpeed * 1000f);
            // A diagonal step is sqrt(2) tiles, charged as such rather than as one:
            // rounding it down is what turns a distance transform into a square.
            int diagonal = (int)(Assumptions.TileSize * 1.41421356f / Assumptions.WalkSpeed * 1000f);
            int budget = Assumptions.AccessFieldSpreadTiles * straight;

            for (int i = 0; i < walkMs.Length; i++)
            {
                spreadMs[i] = walkMs[i] == int.MaxValue ? int.MaxValue : 0;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                bool forward = pass == 0;
                for (int step = 0; step < grid.y; step++)
                {
                    int z = forward ? step : grid.y - 1 - step;
                    for (int inner = 0; inner < grid.x; inner++)
                    {
                        int x = forward ? inner : grid.x - 1 - inner;
                        int index = (z * grid.x) + x;
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0)
                                {
                                    continue;
                                }

                                int nx = x + dx;
                                int nz = z + dz;
                                if (nx < 0 || nz < 0 || nx >= grid.x || nz >= grid.y)
                                {
                                    continue;
                                }

                                int from = (nz * grid.x) + nx;
                                if (spreadMs[from] == int.MaxValue)
                                {
                                    continue;
                                }

                                int cost = dx != 0 && dz != 0 ? diagonal : straight;
                                // The budget is on the SPREAD, not on the walk: a tile
                                // may inherit a long walk from its street, but only over
                                // a few tiles of open ground. Without this the field
                                // would carry a walk straight over a river to a stop on
                                // the far bank and report a walk nobody can make.
                                long carried = (long)spreadMs[from] + cost;
                                if (carried > budget)
                                {
                                    continue;
                                }

                                long candidate = (long)walkMs[from] + cost;
                                if (candidate < walkMs[index])
                                {
                                    walkMs[index] = (int)candidate;
                                    spreadMs[index] = (int)carried;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
