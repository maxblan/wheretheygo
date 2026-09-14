using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // Joins the pedestrian network back together where the game's data cuts it apart.
    //
    // The walk graph takes one edge per road segment that carries a pedestrian lane, and
    // drops the rest — a motorway is not walkable, and that much is right. But a segment
    // without a pavement in the middle of a street cuts everything past it off, and on a
    // real save that happens constantly: Valmare's graph had 3845 nodes in **582
    // separate pieces**, the largest holding 46 % of them. The fragments are not remote
    // places. Their median distance to the main network is 34 m, and 475 of the 581 are
    // within 60 m — they are the same streets, split by one segment.
    //
    // What that cost: only 769 of 3845 nodes were within ten minutes' walk of a served
    // stop. Two houses on one street reported one minute and "no stop in reach"
    // depending on which side of a cut they snapped to, the equity figure was far too
    // low, and the heat map's coverage penalty was blind next to half the stops in the
    // city. Bridging at 50 m takes it to 2477 nodes, and 3824 of 3845 into one piece.
    //
    // A bridge is a real walk: a person crosses 30 m of junction on foot, and the link
    // is charged at walking speed over the straight-line distance. 50 m is the limit
    // because that is about as far as one can walk in a straight line without a
    // pavement; wider, and the graph starts stepping over canals.
    internal static class WalkBridging
    {
        // Adds an undirected edge between the nearest pair of nodes in two different
        // components, repeatedly, while a pair within `radius` exists. Returns how many
        // were added.
        //
        // Deterministic by construction, because the offline checker reproduces this
        // graph edge for edge: nodes are visited in index order and ties go to the lower
        // index, so the result depends on nothing but the inputs.
        public static int Bridge(
            float[] nodeX, float[] nodeZ, List<int> edgeA, List<int> edgeB, List<float> edgeMetres, float radius)
        {
            if (nodeX is null || nodeZ is null || edgeA is null || edgeB is null || edgeMetres is null)
            {
                throw new ArgumentNullException(nameof(nodeX));
            }

            int count = nodeX.Length;
            if (count < 2 || radius <= 0f)
            {
                return 0;
            }

            var parent = new int[count];
            for (int i = 0; i < count; i++)
            {
                parent[i] = i;
            }

            for (int e = 0; e < edgeA.Count; e++)
            {
                _ = Union(parent, edgeA[e], edgeB[e]);
            }

            // One bucket per cell of the radius, so a node's candidates are the nine
            // cells around it whatever the map's size.
            float cell = Math.Max(radius, 1f);
            var buckets = new Dictionary<long, List<int>>();
            for (int i = 0; i < count; i++)
            {
                long key = Key(nodeX[i], nodeZ[i], cell);
                if (!buckets.TryGetValue(key, out List<int>? bucket))
                {
                    bucket = new List<int>();
                    buckets[key] = bucket;
                }

                bucket.Add(i);
            }

            float radiusSq = radius * radius;
            int added = 0;
            bool changed = true;
            for (int pass = 0; changed && pass < Assumptions.WalkBridgePasses; pass++)
            {
                changed = false;
                for (int i = 0; i < count; i++)
                {
                    int best = -1;
                    float bestSq = radiusSq;
                    int root = Find(parent, i);
                    int cx = (int)Math.Floor(nodeX[i] / cell);
                    int cz = (int)Math.Floor(nodeZ[i] / cell);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!buckets.TryGetValue(CellKey(cx + dx, cz + dz), out List<int>? bucket))
                            {
                                continue;
                            }

                            for (int k = 0; k < bucket.Count; k++)
                            {
                                int j = bucket[k];
                                if (Find(parent, j) == root)
                                {
                                    continue;
                                }

                                float ddx = nodeX[i] - nodeX[j];
                                float ddz = nodeZ[i] - nodeZ[j];
                                float distanceSq = (ddx * ddx) + (ddz * ddz);
                                if (distanceSq < bestSq || (distanceSq == bestSq && best >= 0 && j < best))
                                {
                                    bestSq = distanceSq;
                                    best = j;
                                }
                            }
                        }
                    }

                    if (best < 0)
                    {
                        continue;
                    }

                    _ = Union(parent, i, best);
                    edgeA.Add(i);
                    edgeB.Add(best);
                    edgeMetres.Add((float)Math.Sqrt(bestSq));
                    added++;
                    changed = true;
                }
            }

            return added;
        }

        private static long Key(float x, float z, float cell)
        {
            return CellKey((int)Math.Floor(x / cell), (int)Math.Floor(z / cell));
        }

        // The project builds with overflow checking on, so the low half is masked rather
        // than cast: a negative cell index is ordinary here, the map's origin is its
        // centre.
        private static long CellKey(int x, int z)
        {
            return ((long)x << 32) | ((long)z & 0xFFFFFFFFL);
        }

        private static int Find(int[] parent, int node)
        {
            while (parent[node] != node)
            {
                parent[node] = parent[parent[node]];
                node = parent[node];
            }

            return node;
        }

        private static bool Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA == rootB)
            {
                return false;
            }

            parent[rootA] = rootB;
            return true;
        }
    }
}
