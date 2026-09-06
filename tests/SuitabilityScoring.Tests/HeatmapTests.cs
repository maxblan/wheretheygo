using System;
using System.Globalization;

namespace StationSuitabilityOverlay.Tests
{
    // F1's pass over the tile field and F2's candidate set — the grid around the
    // combine, pure since 2026-09-05.
    internal static partial class Program
    {
        private static void HeatmapCombineFieldAndCaps()
        {
            var terms = new SuitabilityCell[4];
            terms[0] = new SuitabilityCell { m_Demand = 10f, m_Jobs = 4f, m_Coverage = 0.5f, m_Access = 1f, m_Future = 2f, m_Interchange = 1f, m_CrossCoverage = 0.5f };
            terms[1] = new SuitabilityCell { m_Demand = 20f, m_Access = 0.5f };
            terms[2] = new SuitabilityCell { m_Demand = 5f, m_Jobs = 8f, m_Access = 1f };
            var scratch = new float[4];
            var percentileScratch = new float[4];
            float capDemand = SuitabilityHeatmap.TermCap(terms, 4, CapTerm.Demand, scratch, percentileScratch);
            float capJobs = SuitabilityHeatmap.TermCap(terms, 4, CapTerm.Jobs, scratch, percentileScratch);
            float capFuture = SuitabilityHeatmap.TermCap(terms, 4, CapTerm.Future, scratch, percentileScratch);
            AssertTrue(capDemand == SuitabilityScoring.PositivePercentile(new[] { 10f, 20f, 5f, 0f }, 4, SuitabilityHeatmap.TermCapPercentile, new float[4]), "the demand cap is the positive percentile of the demand term");
            AssertTrue(capJobs == SuitabilityScoring.PositivePercentile(new[] { 4f, 0f, 8f, 0f }, 4, SuitabilityHeatmap.TermCapPercentile, new float[4]), "the jobs cap likewise");
            AssertTrue(capFuture == SuitabilityScoring.PositivePercentile(new[] { 2f, 0f, 0f, 0f }, 4, SuitabilityHeatmap.TermCapPercentile, new float[4]), "and the future cap");
            AssertTrue(capDemand > 0f && capJobs > 0f && capFuture > 0f, "every term has a positive member here");

            var weights = new CombineWeights(1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f / capDemand, 1f / capJobs, 1f / capFuture);
            int[] tileNode = { 0, -1, 2, 3 };
            var scores = new float[4];
            var layers = new SuitabilityHeatmap.TermLayers { Demand = new byte[4], Access = new byte[4] };
            AssertTrue(layers.Any && !new SuitabilityHeatmap.TermLayers().Any, "a layer set knows whether any layer is registered");
            SuitabilityHeatmap.CombineField(terms, 4, tileNode, in weights, 0.8f, scores, layers);
            AssertTrue(scores[0] == SuitabilityScoring.Combine(in terms[0], in weights, 0.8f) && scores[2] == SuitabilityScoring.Combine(in terms[2], in weights, 0.8f), "on-network tiles score by the combine");
            AssertTrue(scores[1] == 0f, "a tile with no node within the access walk scores zero whatever its terms");
            AssertTrue(scores[3] == 0f, "a tile with no terms scores zero");
            AssertTrue(layers.Demand[1] == SuitabilityHeatmap.IntensityByte(SuitabilityScoring.Saturate(20f * weights.InvDemand)) && layers.Demand[1] == 255, "the term layers show the raw, capped inputs even off the network");
            AssertTrue(layers.Access[1] == 128 && layers.Access[0] == 255, "the access layer is the kernel as a byte");

            var untouched = new float[4];
            SuitabilityHeatmap.CombineField(terms, 4, null, in weights, 0.8f, untouched, new SuitabilityHeatmap.TermLayers());
            AssertTrue(untouched[0] == 0f && untouched[2] == 0f, "without a tile-node map nothing is on the network");

            AssertTrue(SuitabilityHeatmap.IntensityByte(0.5f) == 128 && SuitabilityHeatmap.IntensityByte(2f) == 255 && SuitabilityHeatmap.IntensityByte(-1f) == 0 && SuitabilityHeatmap.IntensityByte(0.001f) == 0, "intensity bytes saturate and round half to even");
            SuitabilityHeatmap.WriteTerm(null, 0, 1f);
            var one = new byte[1];
            SuitabilityHeatmap.WriteTerm(one, 0, 0.25f);
            AssertTrue(one[0] == 64, "WriteTerm writes into a registered layer and ignores an absent one");
        }

        private static void HeatmapNodeDemandAndSitePainting()
        {
            var network = new AlignmentNetwork();
            network.Adopt(CompactGraph.Build(2, new[] { 0 }, new[] { 1 }, new[] { 100f }, 1), new[] { 16f, 48f }, new[] { 16f, 900f }, RouteNetwork.Road);
            var terms = new SuitabilityCell[16];
            terms[0].m_Demand = 5f;
            terms[0].m_Jobs = 20f;
            terms[13].m_Demand = 100f;
            float[] demand = SuitabilityHeatmap.NodeDemand(network, terms, 10f, 10f, new float2Like(0f, 0f), 32f, new int2Like(4, 4));
            AssertTrue(demand.Length == 2 && demand[0] == 1.5f, $"node 0 reads sat(5/10) + sat(20/10) at its tile, got {demand[0].ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(demand[1] == 1f, "node 1 clamps onto the grid's last row and saturates its demand");
            float[] uncapped = SuitabilityHeatmap.NodeDemand(network, terms, 0f, 0f, new float2Like(0f, 0f), 32f, new int2Like(4, 4));
            AssertTrue(uncapped[0] == 0f && uncapped[1] == 0f, "a zero cap drops the term");

            var layer = new byte[64];
            SuitabilityHeatmap.PaintSites(layer, new[] { 27, 0 }, 2, 8, 8);
            int bright = 0;
            int dim = 0;
            for (int i = 0; i < layer.Length; i++)
            {
                bright += layer[i] == 255 ? 1 : 0;
                dim += layer[i] == 155 ? 1 : 0;
            }

            AssertTrue(bright == 13 && dim == 6, $"the best site paints a 13-tile disc at full intensity, the second a clipped corner disc dimmer: {bright.ToString(CultureInfo.InvariantCulture)} / {dim.ToString(CultureInfo.InvariantCulture)}");
            AssertTrue(layer[27] == 255 && layer[27 - 2] == 255 && layer[27 - 16] == 255 && layer[27 - 9] == 255 && layer[27 - 17] == 0 && layer[9] == 155, "disc membership is dx² + dy² ≤ 4, and the corner disc reaches (1,1)");
        }

        private static void SiteCandidatesOnTheNetwork()
        {
            WalkGraph graph = LineGraph(4, 72f);
            graph.Siteable[2] = false;
            WalkAccessInputs inputs = LineInputs(4);
            inputs.Homes = Sources((0f, 10f));
            inputs.Jobs = Sources((216f, 5f));
            inputs.Future = Sources();
            inputs.StopCount = 0;
            inputs.StopX = Array.Empty<float>();
            inputs.StopZ = Array.Empty<float>();
            inputs.StopType = Array.Empty<int>();
            inputs.Graph = graph;
            var typeWeight = new float[14];
            var buildable = new byte[] { 1, 1, 1, 1, 1, 1, 1, 0 };
            WalkAccessOutput output = SuitabilityWalkAccess.Run(inputs, 8, 1, -16f, -16f, 32f, buildable, 0, 0, typeWeight);
            var worldMin = new float2Like(-16f, -16f);
            var grid = new int2Like(8, 1);
            AssertTrue(SuitabilityExactSites.TileOfNode(graph, 0, worldMin, 32f, grid) == 0 && SuitabilityExactSites.TileOfNode(graph, 3, worldMin, 32f, grid) == 7, "nodes map to the tiles under them");

            var weights = new CombineWeights(1f, 0f, 0f, 0f, 0f, 0f, 0f, 0.1f, 0f, 0f);
            int[] nodes = Array.Empty<int>();
            float[] scores = Array.Empty<float>();
            int count = SuitabilityExactSites.CollectNetworkCandidates(graph, output.Result, 0, 0, typeWeight, buildable, worldMin, 32f, grid, in weights, 1f, ref nodes, ref scores);
            AssertTrue(nodes.Length == 4 && scores.Length == 4, "the arrays grow to the node count");
            AssertTrue(count == 2 && nodes[0] == 0 && nodes[1] == 1, $"the node in a tunnel and the node on an unbuildable tile are no candidates: {count.ToString(CultureInfo.InvariantCulture)} kept");
            SuitabilityCell node0 = SuitabilityWalkAccess.NodeTerms(output.Result, 0, 0, 0, typeWeight);
            AssertTrue(scores[0] == SuitabilityScoring.Combine(in node0, in weights, 1f) && scores[0] > scores[1] && scores[1] > 0f, "candidates carry the combine of their node's terms, fading with the walk from the homes");

            var nothing = new CombineWeights(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
            AssertTrue(SuitabilityExactSites.CollectNetworkCandidates(graph, output.Result, 0, 0, typeWeight, buildable, worldMin, 32f, grid, in nothing, 1f, ref nodes, ref scores) == 0, "a node scoring zero is not a candidate");
        }
    }
}
