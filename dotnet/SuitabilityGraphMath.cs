using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // Minimal 2D point so the geometry helpers stay free of Unity types and remain
    // unit testable; the mod converts to and from float2 at the boundary.
    internal struct float2Like
    {
        public float x;
        public float y;

        public float2Like(float x, float y)
        {
            this.x = x;
            this.y = y;
        }
    }

    // How a suggested corridor is scored while it grows.
    internal enum RouteObjective
    {
        Ridership = 0,
        Balanced = 1,
        Coverage = 2,
    }

    // A road network flattened into index arrays, so the routing algorithms never
    // touch ECS types and can be unit tested directly. Adjacency is stored
    // compressed-row style: the incident half-edges of node n live in
    // AdjEdge/AdjOther between NodeOffsets[n] and NodeOffsets[n + 1].
    internal sealed class CompactGraph
    {
        public int NodeCount;
        public int EdgeCount;
        public int[] EdgeA = Array.Empty<int>();
        public int[] EdgeB = Array.Empty<int>();
        public float[] EdgeCost = Array.Empty<float>();
        public int[] NodeOffsets = Array.Empty<int>();
        public int[] AdjEdge = Array.Empty<int>();
        public int[] AdjOther = Array.Empty<int>();

        public static CompactGraph Build(int nodeCount, int[] edgeA, int[] edgeB, float[] edgeCost, int edgeCount)
        {
            var graph = new CompactGraph
            {
                NodeCount = Math.Max(0, nodeCount),
                EdgeCount = Math.Max(0, edgeCount),
                EdgeA = edgeA,
                EdgeB = edgeB,
                EdgeCost = edgeCost,
            };

            graph.NodeOffsets = new int[graph.NodeCount + 1];
            if (graph.NodeCount == 0 || graph.EdgeCount == 0)
            {
                graph.AdjEdge = Array.Empty<int>();
                graph.AdjOther = Array.Empty<int>();
                return graph;
            }

            // Count, prefix-sum, then fill — two passes, no per-node lists.
            var counts = new int[graph.NodeCount];
            for (int e = 0; e < graph.EdgeCount; e++)
            {
                int a = edgeA[e];
                int b = edgeB[e];
                if (!Valid(a, graph.NodeCount) || !Valid(b, graph.NodeCount) || a == b)
                {
                    continue;
                }

                counts[a]++;
                counts[b]++;
            }

            int running = 0;
            for (int n = 0; n < graph.NodeCount; n++)
            {
                graph.NodeOffsets[n] = running;
                running += counts[n];
            }
            graph.NodeOffsets[graph.NodeCount] = running;

            graph.AdjEdge = new int[running];
            graph.AdjOther = new int[running];
            var cursor = new int[graph.NodeCount];
            Array.Copy(graph.NodeOffsets, cursor, graph.NodeCount);

            for (int e = 0; e < graph.EdgeCount; e++)
            {
                int a = edgeA[e];
                int b = edgeB[e];
                if (!Valid(a, graph.NodeCount) || !Valid(b, graph.NodeCount) || a == b)
                {
                    continue;
                }

                graph.AdjEdge[cursor[a]] = e;
                graph.AdjOther[cursor[a]] = b;
                cursor[a]++;

                graph.AdjEdge[cursor[b]] = e;
                graph.AdjOther[cursor[b]] = a;
                cursor[b]++;
            }

            return graph;
        }

        private static bool Valid(int node, int nodeCount)
        {
            return node >= 0 && node < nodeCount;
        }

        public int OtherEnd(int edge, int node)
        {
            return EdgeA[edge] == node ? EdgeB[edge] : EdgeA[edge];
        }
    }

    // Reusable scratch for repeated single-source searches. Kept as an object so a
    // caller running hundreds of sources allocates once.
    internal sealed class DijkstraWorkspace
    {
        public float[] Dist = Array.Empty<float>();
        public int[] PrevEdge = Array.Empty<int>();
        private int[] m_Heap = Array.Empty<int>();
        private int m_HeapCount;
        // Touched nodes, so a search over a small neighbourhood does not pay to
        // clear a city-sized distance array.
        private int[] m_Touched = Array.Empty<int>();
        private int m_TouchedCount;

        public DijkstraWorkspace(int nodeCount)
        {
            Resize(nodeCount);
        }

        public void Resize(int nodeCount)
        {
            if (Dist is not null && Dist.Length == nodeCount)
            {
                return;
            }

            Dist = new float[nodeCount];
            PrevEdge = new int[nodeCount];
            m_Heap = new int[nodeCount + 1];
            m_Touched = new int[nodeCount];

            // The touched list addresses the arrays that were just replaced, so its
            // count must go with them: carrying it across a reallocation made the
            // next ClearTouched read past the end of a shorter m_Touched.
            m_TouchedCount = 0;
            m_HeapCount = 0;

            for (int i = 0; i < nodeCount; i++)
            {
                Dist[i] = float.MaxValue;
                PrevEdge[i] = -1;
            }
        }

        // Single-source shortest paths, stopping once every reachable node within
        // maxCost is settled. One call serves every destination from that source,
        // which is why flow assignment runs one search per origin zone rather than
        // one per origin/destination pair.
        public void Run(CompactGraph graph, int source, float maxCost)
        {
            Resize(graph.NodeCount);
            ClearTouched();

            if (source < 0 || source >= graph.NodeCount)
            {
                return;
            }

            m_HeapCount = 0;
            Touch(source);
            Dist[source] = 0f;
            PrevEdge[source] = -1;
            HeapPush(source);

            while (m_HeapCount > 0)
            {
                int node = HeapPop();
                float nodeDist = Dist[node];
                if (nodeDist > maxCost)
                {
                    continue;
                }

                int start = graph.NodeOffsets[node];
                int end = graph.NodeOffsets[node + 1];
                for (int i = start; i < end; i++)
                {
                    int edge = graph.AdjEdge[i];
                    int next = graph.AdjOther[i];
                    float candidate = nodeDist + graph.EdgeCost[edge];
                    if (candidate > maxCost)
                    {
                        continue;
                    }

                    Touch(next);
                    if (candidate >= Dist[next])
                    {
                        continue;
                    }

                    Dist[next] = candidate;
                    PrevEdge[next] = edge;
                    HeapPush(next);
                }
            }
        }

        // A node enters the touched list exactly once, on the relaxation that first
        // gives it a finite distance. Every caller of this sets Dist[node] to a
        // finite value immediately afterwards, which is what keeps that true.
        private void Touch(int node)
        {
            if (Dist[node] != float.MaxValue)
            {
                return;
            }

            m_Touched[m_TouchedCount++] = node;
        }

        private void ClearTouched()
        {
            for (int i = 0; i < m_TouchedCount; i++)
            {
                int node = m_Touched[i];
                Dist[node] = float.MaxValue;
                PrevEdge[node] = -1;
            }

            m_TouchedCount = 0;
        }

        // Binary min-heap over node indices keyed by Dist. Lazy: a node can be
        // pushed more than once, and stale pops are harmless because Dist only ever
        // decreases and the relaxation check rejects them.
        private void HeapPush(int node)
        {
            if (m_HeapCount + 1 >= m_Heap.Length)
            {
                Array.Resize(ref m_Heap, m_Heap.Length * 2);
            }

            m_Heap[++m_HeapCount] = node;
            int slot = m_HeapCount;
            while (slot > 1)
            {
                int parent = slot >> 1;
                if (Dist[m_Heap[parent]] <= Dist[m_Heap[slot]])
                {
                    break;
                }

                Swap(parent, slot);
                slot = parent;
            }
        }

        private int HeapPop()
        {
            int top = m_Heap[1];
            m_Heap[1] = m_Heap[m_HeapCount--];
            int slot = 1;
            while (true)
            {
                int left = slot << 1;
                if (left > m_HeapCount)
                {
                    break;
                }

                int best = left;
                int right = left + 1;
                if (right <= m_HeapCount && Dist[m_Heap[right]] < Dist[m_Heap[left]])
                {
                    best = right;
                }

                if (Dist[m_Heap[slot]] <= Dist[m_Heap[best]])
                {
                    break;
                }

                Swap(slot, best);
                slot = best;
            }

            return top;
        }

        private void Swap(int a, int b)
        {
            int tmp = m_Heap[a];
            m_Heap[a] = m_Heap[b];
            m_Heap[b] = tmp;
        }
    }

    // A grown corridor: the ordered edges it runs along, plus what it captured.
    internal sealed class Corridor
    {
        public readonly List<int> Edges = new List<int>();
        public readonly List<int> Nodes = new List<int>();
        // Length-weighted mean flow along the corridor: "how many trips does this
        // carry". Deliberately NOT a sum over edges — summing counts a trip once per
        // edge it traverses, so long corridors reported flow far above the city's
        // entire demand and every suggestion was classified as a metro.
        public float CapturedFlow;
        public float Length;

        // Why growth stopped. A corridor ends when neither end has an eligible next
        // edge, and these say which test did the rejecting on that final look. Without
        // them a short corridor is indistinguishable from a long one: a route that ran
        // out of demand, one hemmed in by corridors grown before it, and one that
        // simply reached a dead end all arrive as the same number of metres.
        public CorridorBlocks Blocks;

        public void Clear()
        {
            Edges.Clear();
            Nodes.Clear();
            CapturedFlow = 0f;
            Length = 0f;
            Blocks = default;
        }
    }

    // Counts of the edges rejected on the last, failed search for an extension —
    // across both ends of the corridor.
    internal struct CorridorBlocks
    {
        // Already spent on an earlier corridor.
        public int m_Used;
        // Below the flow floor: nobody travels this way.
        public int m_Flow;
        // Would revisit a node the corridor already passes through.
        public int m_Visited;
        // Would push the corridor past its maximum length.
        public int m_Length;
        // Leads to a node with no demand beside it.
        public int m_Demand;
        // The corridor stopped because it hit the length limit, not for want of a
        // next edge.
        public bool m_HitMaxLength;

        public readonly int Total => m_Used + m_Flow + m_Visited + m_Length + m_Demand;
    }

    // The network a corridor is grown over: its topology, what each edge carries, and
    // what is true of each node. Passed as one value because growth reads them
    // together and because handing them over one at a time had already made
    // GrowCorridor a twelve-parameter call whose arguments could only be checked by
    // counting.
    //
    // `NodeX`/`NodeZ` are optional. Without them growth has no idea which way it is
    // heading and will happily staircase across a city; see TurnPenalty.
    internal readonly struct CorridorNetwork
    {
        public CorridorNetwork(
            CompactGraph graph,
            float[] edgeFlow,
            bool[] edgeUsed,
            float[]? nodeNovelty = null,
            float[]? nodeDemand = null,
            float[]? nodeX = null,
            float[]? nodeZ = null)
        {
            Graph = graph;
            EdgeFlow = edgeFlow;
            EdgeUsed = edgeUsed;
            NodeNovelty = nodeNovelty;
            NodeDemand = nodeDemand;
            NodeX = nodeX;
            NodeZ = nodeZ;
        }

        public CompactGraph Graph { get; }

        public float[] EdgeFlow { get; }

        public bool[] EdgeUsed { get; }

        public float[]? NodeNovelty { get; }

        public float[]? NodeDemand { get; }

        public float[]? NodeX { get; }

        public float[]? NodeZ { get; }
    }

    internal static class SuitabilityGraphMath
    {
        // How much a corridor prefers to carry straight on.
        //
        // Growth picks the best adjacent edge on flow and novelty alone, and on a
        // lattice — a 128 m grid where flow is spread thin and nearly uniform — the
        // tiniest difference between two edges steers it. The result wandered across
        // the whole city in a staircase, which is not an alignment anyone would build
        // and not something Ramer-Douglas-Peucker can straighten afterwards: the
        // corridor genuinely went that way.
        //
        // A real line continues along the street or the alignment it is on and turns
        // only for a reason. At 0.6 a right-angle turn keeps 70% of its score and a
        // reversal 40%, so a genuinely busier direction still wins — this is a
        // preference, not a constraint.
        private const float TurnPenalty = 0.6f;

        // How much a corridor prefers to be GOING somewhere.
        //
        // The turn penalty is local: it stops a staircase but says nothing about the
        // shape overall, and a corridor can carry smoothly round a long arc back to
        // where it started. On a lattice that is exactly what happened — a metro was
        // proposed as a box around an empty field, and another as a ring around the
        // whole city.
        //
        // The cause is that a lattice is a UNIFORM grid, so the shortest path between
        // two zones is degenerate: hundreds of staircases cost the same, and which one
        // Dijkstra picks falls out of the order edges were added. The flow those paths
        // accumulate forms ridges that are an artifact of the grid rather than of where
        // anyone travels, and growth follows them faithfully.
        //
        // A line connects two places. Every extension is therefore weighed on whether
        // it takes this end FURTHER from the other one: at 0.7 an extension that curls
        // back keeps 30% of its score and one heading straight out keeps all of it.
        private const float SpreadPenalty = 0.7f;

        // A line whose ends are closer together than this share of the distance it
        // travels is a ring, not a route. The spread bias only helps where growth had
        // an alternative; on a corridor with nowhere else to go it still comes round,
        // and this is what catches that.
        public const float MinDirectness = 0.45f;

        // Whether a grown corridor actually gets somewhere.
        public static bool IsDirectEnough(float endToEndMetres, float lengthMetres)
        {
            return lengthMetres <= 0f || endToEndMetres / lengthMetres >= MinDirectness;
        }

        // How much longer an alignment may become to pass through an interchange.
        //
        // A judgement, and stated as one: every rider already on the line pays the
        // detour in minutes, while only those changing vehicle collect the benefit, so
        // the bound is about what the majority will tolerate rather than about what the
        // hub is worth. A quarter again is roughly the point at which a bend stops
        // reading on a map as "the line goes past the station" and starts reading as
        // "the line goes out of its way".
        //
        // Deliberately NOT scaled by how many modes the hub offers. A better hub is a
        // reason to prefer one via over another — which is what the ranking does — not
        // a reason to make the people on board travel further for it.
        public const float MaxViaDetour = 1.25f;

        // Whether bending an alignment through a via point is worth it. `viaMetres` is
        // the whole bent alignment, not the detour alone.
        //
        // Both bounds matter and they fail differently: past MaxViaDetour the line is
        // no longer the line anyone asked for, and past `maxMetres` it is not a line
        // the mode can hold a headway around at all.
        public static bool IsDetourWorthwhile(float directMetres, float viaMetres, float maxMetres)
        {
            if (viaMetres <= 0f || viaMetres > maxMetres)
            {
                return false;
            }

            // No direct path to compare against is not a licence to wander.
            return directMetres > 0f && viaMetres <= directMetres * MaxViaDetour;
        }
        // Consecutive quiet nodes a corridor may cross before giving up. Two is a
        // park, a river, a rail crossing or an industrial strip — the things that sit
        // between two busy districts — and not a licence to strike out into open
        // country, which is what the gate exists to prevent.
        public const int DefaultLowDemandBridge = 2;

        // How heavily a crossing is outranked by an extension into somewhere with
        // people. Low enough that a bridge is only ever taken when it is the only
        // thing on offer.
        private const float LowDemandBridgePenalty = 0.05f;

        // Removes a bridge the corridor never came out of, returning the length to
        // take back off the total.
        private static float DiscardBridge(
            CompactGraph graph,
            float[] edgeFlow,
            List<int> bridge,
            ref float weightedFlow)
        {
            float removed = 0f;
            for (int i = 0; i < bridge.Count; i++)
            {
                int edge = bridge[i];
                removed += graph.EdgeCost[edge];
                weightedFlow -= edgeFlow[edge] * graph.EdgeCost[edge];
            }

            bridge.Clear();
            return removed;
        }

        // Adds `weight` onto every edge along the shortest path from the source the
        // workspace was last run from, back to `target`. Returns false if the target
        // was not reachable within the search radius.
        public static bool AccumulatePath(
            CompactGraph graph,
            DijkstraWorkspace workspace,
            int source,
            int target,
            float weight,
            float[] edgeFlow)
        {
            if (graph is null || workspace is null || edgeFlow is null)
            {
                return false;
            }

            if (target < 0 || target >= graph.NodeCount || source < 0 || source >= graph.NodeCount)
            {
                return false;
            }

            if (target == source)
            {
                return false;
            }

            if (workspace.Dist[target] == float.MaxValue)
            {
                return false;
            }

            int node = target;
            int guard = graph.EdgeCount + 1;
            while (node != source && guard-- > 0)
            {
                int edge = workspace.PrevEdge[node];
                if (edge < 0)
                {
                    return false;
                }

                edgeFlow[edge] += weight;
                node = graph.OtherEnd(edge, node);
            }

            return node == source;
        }

        // Novelty multiplier for the objective: how much reaching an untouched area
        // counts against raw flow. Scaled by mean flow so it is unit-free and
        // behaves the same on a small town and a metropolis.
        // How much the SEED of the next corridor is pulled towards untouched ground,
        // from 0 (purely the busiest remaining edge) to 1 (flow scaled straight by
        // novelty). This is what actually makes the objective visible: novelty is
        // uniform while the first corridor is grown, so an objective that only tips
        // the extension choice cannot change a thing until something has been chosen,
        // and seeding used to ignore novelty entirely — which is why all three
        // objectives produced identical suggestions on a real city.
        public static float SeedNoveltyBias(RouteObjective objective)
        {
            switch (objective)
            {
                case RouteObjective.Ridership: return 0f;
                case RouteObjective.Coverage: return 1f;
                default: return 0.5f;
            }
        }

        public static float NoveltyWeight(RouteObjective objective, float meanFlow)
        {
            switch (objective)
            {
                case RouteObjective.Ridership: return 0f;
                case RouteObjective.Coverage: return meanFlow * 4f;
                default: return meanFlow * 1f;
            }
        }

        public static float MeanPositiveFlow(float[] edgeFlow, int edgeCount)
        {
            if (edgeFlow is null || edgeCount <= 0)
            {
                return 0f;
            }

            double sum = 0.0;
            int count = 0;
            for (int e = 0; e < edgeCount; e++)
            {
                if (edgeFlow[e] > 0f)
                {
                    sum += edgeFlow[e];
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        // Grows one corridor: seed at the strongest unused edge, then repeatedly
        // extend whichever end offers the better next edge, where "better" is flow
        // plus a novelty bonus for reaching nodes no earlier corridor covered.
        //
        // Nodes are never revisited, so a corridor cannot fold back on itself, and
        // growth is deterministic: ties break on the lower edge index.
        //
        // A `seedNoveltyBias` of 0 seeds purely on flow, which is both the Ridership
        // objective and the behaviour every caller had before it existed.
        public static bool GrowCorridor(
            in CorridorNetwork network,
            float noveltyWeight,
            float flowFloor,
            float maxLength,
            Corridor result,
            float demandFloor = 0f,
            float seedNoveltyBias = 0f,
            int maxLowDemandBridge = DefaultLowDemandBridge)
        {
            result.Clear();
            CompactGraph graph = network.Graph;
            float[] edgeFlow = network.EdgeFlow;
            if (graph is null || edgeFlow is null || network.EdgeUsed is null || graph.EdgeCount == 0)
            {
                return false;
            }

            int seed = SelectSeed(graph, edgeFlow, network.EdgeUsed, network.NodeNovelty, flowFloor, seedNoveltyBias);
            if (seed < 0)
            {
                return false;
            }

            var visited = new HashSet<int>();
            var front = new List<int>();
            var back = new List<int>();

            // Edges crossing quiet nodes, held back until the corridor reaches
            // somewhere with people again. Flushed into the corridor when it does,
            // discarded when it does not — which is what stops a line ending in a
            // field while still letting it cross one.
            var frontBridge = new List<int>();
            var backBridge = new List<int>();

            int headNode = graph.EdgeA[seed];
            int tailNode = graph.EdgeB[seed];
            // The head node growth continues from is NOT the head node of the finished
            // line: while the corridor is out on a bridge those edges are only
            // provisional, and a run that is never redeemed is handed back below. This
            // is the outermost node on the front side that is actually on the line, and
            // it is where BuildNodeSequence has to start its walk. Starting from
            // headNode instead put a discarded bridge's far end at the front of the
            // polyline, joined to the real corridor by a chord across the very
            // emptiness the discard exists to cut off.
            int frontTerminus = headNode;
            // Where each end came FROM, so an extension can be judged on whether it
            // carries straight on. The seed itself is the incoming direction at both
            // ends: growing off the head continues away from the tail, and vice versa.
            int headFrom = tailNode;
            int tailFrom = headNode;
            _ = visited.Add(headNode);
            _ = visited.Add(tailNode);
            float length = graph.EdgeCost[seed];
            float weightedFlow = edgeFlow[seed] * graph.EdgeCost[seed];

            var blocks = default(CorridorBlocks);
            while (length < maxLength)
            {
                int bestEdge = -1;
                int bestNext = -1;
                float bestScore = 0f;
                bool bestAtHead = true;

                // Reset each round: what matters is what blocked the LAST look, which
                // is the reason this corridor is the length it is.
                blocks = default;
                FindExtension(in network, noveltyWeight, flowFloor, visited,
                    headNode, headFrom, tailNode, length, maxLength, demandFloor,
                    ref bestEdge, ref bestNext, ref bestScore, ref bestAtHead, ref blocks,
                    frontBridge.Count, maxLowDemandBridge, atHead: true);
                FindExtension(in network, noveltyWeight, flowFloor, visited,
                    tailNode, tailFrom, headNode, length, maxLength, demandFloor,
                    ref bestEdge, ref bestNext, ref bestScore, ref bestAtHead, ref blocks,
                    backBridge.Count, maxLowDemandBridge, atHead: false);

                if (bestEdge < 0)
                {
                    break;
                }

                weightedFlow += edgeFlow[bestEdge] * graph.EdgeCost[bestEdge];
                length += graph.EdgeCost[bestEdge];
                _ = visited.Add(bestNext);

                bool crossingEmptiness = network.NodeDemand is not null
                    && bestNext < network.NodeDemand.Length
                    && network.NodeDemand[bestNext] < demandFloor;

                if (bestAtHead)
                {
                    Commit(front, frontBridge, bestEdge, crossingEmptiness);
                    if (!crossingEmptiness)
                    {
                        frontTerminus = bestNext;
                    }

                    headFrom = headNode;
                    headNode = bestNext;
                }
                else
                {
                    Commit(back, backBridge, bestEdge, crossingEmptiness);
                    tailFrom = tailNode;
                    tailNode = bestNext;
                }
            }

            // Read before the discard below: a corridor that grew until it hit the
            // limit and then handed back a crossing is still a corridor that hit the
            // limit, and reporting it as one that ran out of edges is exactly the
            // confusion CorridorBlocks exists to remove.
            bool hitMaxLength = length >= maxLength;

            // Growth ended mid-crossing: the corridor was heading into emptiness and
            // never came out, so those edges are not part of the line.
            length -= DiscardBridge(graph, edgeFlow, frontBridge, ref weightedFlow);
            length -= DiscardBridge(graph, edgeFlow, backBridge, ref weightedFlow);

            blocks.m_HitMaxLength = hitMaxLength;
            result.Blocks = blocks;

            // Emit front-to-back so the result is a drawable polyline.
            for (int i = front.Count - 1; i >= 0; i--)
            {
                result.Edges.Add(front[i]);
            }
            result.Edges.Add(seed);
            for (int i = 0; i < back.Count; i++)
            {
                result.Edges.Add(back[i]);
            }

            BuildNodeSequence(graph, result, frontTerminus);

            result.CapturedFlow = length > 0f ? weightedFlow / length : 0f;
            result.Length = length;
            return true;
        }

        // The one place a novelty value is read. Both call sites used to guard
        // differently — the seed loop dereferenced the array unconditionally while the
        // extension loop null-checked it — so a Balanced or Coverage objective with no
        // novelty array threw where Ridership did not.
        // Where the next corridor starts: the strongest edge still available, pulled
        // towards untouched ground by the objective's seed bias.
        //
        // Eligibility stays on RAW flow, so no objective can seed a corridor on a
        // street nobody travels; the bias only reorders what is already eligible.
        // Novelty is uniform while the first corridor grows, so an objective that
        // reached only the extension choice could not change a thing until something
        // had been chosen — which is why all three objectives once produced identical
        // suggestions on a real city.
        //
        // Returns -1 when nothing is eligible. Ties break on the lower edge index.
        private static int SelectSeed(
            CompactGraph graph,
            float[] edgeFlow,
            bool[] edgeUsed,
            float[]? nodeNovelty,
            float flowFloor,
            float seedNoveltyBias)
        {
            int seed = -1;
            float seedScore = 0f;
            for (int e = 0; e < graph.EdgeCount; e++)
            {
                if (edgeUsed[e] || edgeFlow[e] < flowFloor)
                {
                    continue;
                }

                float score = edgeFlow[e];
                if (seedNoveltyBias > 0f)
                {
                    float ends = Math.Min(
                        NoveltyAt(nodeNovelty, graph.EdgeA[e]),
                        NoveltyAt(nodeNovelty, graph.EdgeB[e]));
                    score *= (1f - seedNoveltyBias) + (seedNoveltyBias * ends);
                }

                if (score > seedScore)
                {
                    seedScore = score;
                    seed = e;
                }
            }

            return seed;
        }

        // Takes an extension into the corridor, or holds it back as part of a crossing
        // that has not yet earned its place. Flushing the held run when demand resumes
        // is what lets a line pass through emptiness but never terminate in it.
        private static void Commit(List<int> side, List<int> bridge, int edge, bool crossingEmptiness)
        {
            if (crossingEmptiness)
            {
                bridge.Add(edge);
                return;
            }

            side.AddRange(bridge);
            bridge.Clear();
            side.Add(edge);
        }

        // How much an extension carries straight on: 1 for continuing in the same
        // direction, 1 - TurnPenalty for a right angle, less for doubling back.
        //
        // Without node positions there is no direction to measure and every extension
        // scores the same, which is the behaviour every caller had before positions
        // existed.
        private static float Continuity(in CorridorNetwork network, int from, int at, int to)
        {
            float[]? x = network.NodeX;
            float[]? z = network.NodeZ;
            if (x is null || z is null || from < 0 || at < 0 || to < 0
                || from >= x.Length || at >= x.Length || to >= x.Length
                || from >= z.Length || at >= z.Length || to >= z.Length)
            {
                return 1f;
            }

            float inX = x[at] - x[from];
            float inZ = z[at] - z[from];
            float outX = x[to] - x[at];
            float outZ = z[to] - z[at];
            double inLength = Math.Sqrt((inX * inX) + (inZ * inZ));
            double outLength = Math.Sqrt((outX * outX) + (outZ * outZ));
            if (inLength <= 0.0 || outLength <= 0.0)
            {
                return 1f;
            }

            // -1 doubling back, 0 a right angle, 1 straight on.
            double cosine = (((inX * outX) + (inZ * outZ)) / inLength) / outLength;
            float straightness = (float)((cosine + 1.0) * 0.5);
            return 1f - (TurnPenalty * (1f - straightness));
        }

        // How much an extension takes this end of the corridor further from the other
        // one: 1 for heading straight out, down to 1 - SpreadPenalty for curling back.
        //
        // Measured against the edge's own length, so it means the same on a 128 m
        // lattice edge and a 400 m street.
        private static float Spread(in CorridorNetwork network, int thisEnd, int otherEnd, int next, float edgeCost)
        {
            float[]? x = network.NodeX;
            float[]? z = network.NodeZ;
            if (x is null || z is null || edgeCost <= 0f
                || thisEnd < 0 || otherEnd < 0 || next < 0
                || thisEnd >= x.Length || otherEnd >= x.Length || next >= x.Length
                || thisEnd >= z.Length || otherEnd >= z.Length || next >= z.Length)
            {
                return 1f;
            }

            double before = Separation(x[thisEnd] - x[otherEnd], z[thisEnd] - z[otherEnd]);
            double after = Separation(x[next] - x[otherEnd], z[next] - z[otherEnd]);

            // -1 straight back towards the other end, 1 straight away from it.
            double gain = (after - before) / edgeCost;
            float outward = (float)((Math.Max(-1.0, Math.Min(1.0, gain)) + 1.0) * 0.5);
            return 1f - (SpreadPenalty * (1f - outward));
        }

        private static double Separation(float dx, float dz)
        {
            return Math.Sqrt(((double)dx * dx) + ((double)dz * dz));
        }

        private static float NoveltyAt(float[]? nodeNovelty, int node)
        {
            return nodeNovelty is not null && node >= 0 && node < nodeNovelty.Length
                ? nodeNovelty[node]
                : 1f;
        }

        private static void FindExtension(
            in CorridorNetwork network,
            float noveltyWeight,
            float flowFloor,
            HashSet<int> visited,
            int fromNode,
            int cameFrom,
            int otherEnd,
            float length,
            float maxLength,
            float demandFloor,
            ref int bestEdge,
            ref int bestNext,
            ref float bestScore,
            ref bool bestAtHead,
            ref CorridorBlocks blocks,
            int lowDemandRun,
            int maxLowDemandBridge,
            bool atHead)
        {
            CompactGraph graph = network.Graph;
            float[] edgeFlow = network.EdgeFlow;
            bool[] edgeUsed = network.EdgeUsed;
            float[]? nodeDemand = network.NodeDemand;

            int start = graph.NodeOffsets[fromNode];
            int end = graph.NodeOffsets[fromNode + 1];
            for (int i = start; i < end; i++)
            {
                int edge = graph.AdjEdge[i];
                if (edgeUsed[edge])
                {
                    blocks.m_Used++;
                    continue;
                }

                if (edgeFlow[edge] < flowFloor)
                {
                    blocks.m_Flow++;
                    continue;
                }

                int next = graph.AdjOther[i];
                if (visited.Contains(next))
                {
                    blocks.m_Visited++;
                    continue;
                }

                if (length + graph.EdgeCost[edge] > maxLength)
                {
                    blocks.m_Length++;
                    continue;
                }

                // Flow alone is not enough to justify extending: a rural through-road
                // legitimately carries assigned trips while serving nobody along it.
                // Without this the corridor happily loops out into empty land.
                //
                // But a single quiet junction must not END the line. A real route
                // crosses the park, the river and the industrial strip that lie
                // between two busy districts, and a hard per-node veto stopped dead at
                // the first of them: on this city's streets the demand gate refused
                // more extensions than the flow floor and the used-edge test combined,
                // and no corridor of twenty came within a tenth of its length limit.
                // A bounded run of quiet nodes may therefore be crossed — and the
                // caller discards any such run the corridor ends on, so a line can
                // pass through emptiness but never terminate in it.
                bool lowDemand = nodeDemand is not null
                    && next < nodeDemand.Length
                    && nodeDemand[next] < demandFloor;
                if (lowDemand && lowDemandRun >= maxLowDemandBridge)
                {
                    blocks.m_Demand++;
                    continue;
                }

                float score = edgeFlow[edge] + (noveltyWeight * NoveltyAt(network.NodeNovelty, next));

                // A line continues along the alignment it is on and turns for a reason,
                // and it is on its way from somewhere to somewhere else.
                score *= Continuity(in network, cameFrom, fromNode, next);
                score *= Spread(in network, fromNode, otherEnd, next, graph.EdgeCost[edge]);

                // Crossing emptiness is a last resort, never a preference: any node
                // with people beside it outranks a bridge out of the same junction.
                if (lowDemand)
                {
                    score *= LowDemandBridgePenalty;
                }
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestEdge = edge;
                bestNext = next;
                bestAtHead = atHead;
            }
        }

        private static void BuildNodeSequence(CompactGraph graph, Corridor result, int startNode)
        {
            result.Nodes.Clear();
            if (result.Edges.Count == 0)
            {
                return;
            }

            int node = startNode;
            result.Nodes.Add(node);
            for (int i = 0; i < result.Edges.Count; i++)
            {
                node = graph.OtherEnd(result.Edges[i], node);
                result.Nodes.Add(node);
            }
        }

        // Removes the demand a chosen corridor satisfies. The corridor's own edges
        // lose `capture` of their flow; edges merely touching its nodes lose half
        // that, which is what stops the next suggestion being the parallel street
        // one block over.
        public static void PeelFlow(
            CompactGraph graph,
            Corridor corridor,
            float[] edgeFlow,
            bool[] edgeUsed,
            float capture)
        {
            if (graph is null || corridor is null || edgeFlow is null || edgeUsed is null)
            {
                return;
            }

            capture = SuitabilityScoring.Saturate(capture);

            for (int i = 0; i < corridor.Edges.Count; i++)
            {
                int edge = corridor.Edges[i];
                edgeFlow[edge] *= 1f - capture;
                edgeUsed[edge] = true;
            }

            // Once per edge, not once per incidence. A chord between two of the
            // corridor's own nodes is incident to both, so decaying it as each node is
            // visited compounded the capture to (1 - s)^2 — a 0.85 capture left a
            // parallel street at 33% of its flow where the intended figure was 58%.
            // Any edge shared with an already-processed node has had its share.
            var processed = new HashSet<int>();
            float sideCapture = capture * 0.5f;
            for (int i = 0; i < corridor.Nodes.Count; i++)
            {
                int node = corridor.Nodes[i];
                if (!processed.Add(node))
                {
                    continue;
                }

                int start = graph.NodeOffsets[node];
                int end = graph.NodeOffsets[node + 1];
                for (int a = start; a < end; a++)
                {
                    int edge = graph.AdjEdge[a];
                    if (edgeUsed[edge] || processed.Contains(graph.AdjOther[a]))
                    {
                        continue;
                    }

                    edgeFlow[edge] *= 1f - sideCapture;
                }
            }
        }

        // Once a corridor covers an area, further corridors get less credit for
        // going there. Spreads outward `hops` steps from the corridor's nodes.
        public static void DecayNovelty(CompactGraph graph, Corridor corridor, float[] nodeNovelty, int hops, float factor)
        {
            if (graph is null || corridor is null || nodeNovelty is null)
            {
                return;
            }

            var frontier = new List<int>(corridor.Nodes);
            var seen = new HashSet<int>(corridor.Nodes);
            float scale = factor;

            for (int hop = 0; hop <= hops; hop++)
            {
                for (int i = 0; i < frontier.Count; i++)
                {
                    int node = frontier[i];
                    if (node >= 0 && node < nodeNovelty.Length)
                    {
                        nodeNovelty[node] *= scale;
                    }
                }

                if (hop == hops)
                {
                    break;
                }

                var next = new List<int>();
                for (int i = 0; i < frontier.Count; i++)
                {
                    int node = frontier[i];
                    int start = graph.NodeOffsets[node];
                    int end = graph.NodeOffsets[node + 1];
                    for (int a = start; a < end; a++)
                    {
                        int other = graph.AdjOther[a];
                        if (seen.Add(other))
                        {
                            next.Add(other);
                        }
                    }
                }

                frontier = next;
                // Further hops are dampened less, so the penalty fades with distance.
                scale = 1f - (1f - scale) * 0.5f;
            }
        }

        // Ramer-Douglas-Peucker simplification of a polyline, in place.
        //
        // A lattice corridor is an 8-connected staircase, so drawn raw it zig-zags
        // even when the underlying route is essentially straight. Metro tunnels and
        // ferry crossings really are straight-ish, so collapsing near-collinear runs
        // is both prettier and more honest about the alignment.
        public static void SimplifyPolyline(List<float2Like> points, float tolerance)
        {
            if (points is null || points.Count < 3 || tolerance <= 0f)
            {
                return;
            }

            var keep = new bool[points.Count];
            keep[0] = true;
            keep[points.Count - 1] = true;
            SimplifyRange(points, 0, points.Count - 1, tolerance, keep);

            int write = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i])
                {
                    points[write++] = points[i];
                }
            }

            points.RemoveRange(write, points.Count - write);
        }

        private static void SimplifyRange(List<float2Like> points, int first, int last, float tolerance, bool[] keep)
        {
            if (last <= first + 1)
            {
                return;
            }

            float worst = -1f;
            int worstIndex = -1;
            for (int i = first + 1; i < last; i++)
            {
                float distance = PerpendicularDistance(points[i], points[first], points[last]);
                if (distance > worst)
                {
                    worst = distance;
                    worstIndex = i;
                }
            }

            if (worst <= tolerance || worstIndex < 0)
            {
                return;
            }

            keep[worstIndex] = true;
            SimplifyRange(points, first, worstIndex, tolerance, keep);
            SimplifyRange(points, worstIndex, last, tolerance, keep);
        }

        private static float PerpendicularDistance(float2Like point, float2Like a, float2Like b)
        {
            float dx = b.x - a.x;
            float dy = b.y - a.y;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq <= 1e-6f)
            {
                float ax = point.x - a.x;
                float ay = point.y - a.y;
                return (float)Math.Sqrt(ax * ax + ay * ay);
            }

            float t = ((point.x - a.x) * dx + (point.y - a.y) * dy) / lengthSq;
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            float projX = a.x + t * dx;
            float projY = a.y + t * dy;
            float ox = point.x - projX;
            float oy = point.y - projY;
            return (float)Math.Sqrt(ox * ox + oy * oy);
        }

        // Accumulates a straight desire line into a raster, depositing `weight` per
        // cell it crosses. Per-cell rather than per-trip, so the raster reads as
        // flow density: a long trip touches more cells but does not make any single
        // cell hotter than a short one carrying the same number of people.
        public static void RasterizeSegment(
            float[] raster,
            int width,
            int height,
            float x0,
            float y0,
            float x1,
            float y1,
            float weight)
        {
            if (raster is null || width <= 0 || height <= 0 || raster.Length < width * height)
            {
                return;
            }

            float dx = x1 - x0;
            float dy = y1 - y0;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
            int steps = (int)Math.Ceiling(distance);
            if (steps <= 0)
            {
                Deposit(raster, width, height, (int)Math.Floor(x0), (int)Math.Floor(y0), weight);
                return;
            }

            float stepX = dx / steps;
            float stepY = dy / steps;
            int lastCell = -1;
            for (int i = 0; i <= steps; i++)
            {
                int cx = (int)Math.Floor(x0 + stepX * i);
                int cy = (int)Math.Floor(y0 + stepY * i);
                if (cx < 0 || cx >= width || cy < 0 || cy >= height)
                {
                    continue;
                }

                // Sub-cell steps would otherwise deposit into the same cell twice.
                int cell = cx + cy * width;
                if (cell == lastCell)
                {
                    continue;
                }

                lastCell = cell;
                raster[cell] += weight;
            }
        }

        private static void Deposit(float[] raster, int width, int height, int x, int y, float weight)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                return;
            }

            raster[x + y * width] += weight;
        }
    }
}
