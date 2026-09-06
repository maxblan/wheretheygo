using System;

namespace TransitArchitect
{
    // The heat map's per-term caps (formal-specification.md §7.3).
    internal enum CapTerm
    {
        Demand,
        Jobs,
        Future,
    }

    // F1's pass over the whole tile field (formal-specification.md §7 v2, combine v3):
    // the caps from the term percentiles, the score per tile under the pinned weights,
    // the per-term intensity layers the infomodes draw, the node demand the corridor
    // search reads, and the site markers. The formula itself is
    // SuitabilityScoring.Combine; this is the grid around it.
    internal static class SuitabilityHeatmap
    {

        // The per-term intensity layers, null where a layer is not registered as an
        // infomode and therefore cannot be drawn.
        internal sealed class TermLayers
        {
            public byte[]? Demand;
            public byte[]? Jobs;
            public byte[]? Coverage;
            public byte[]? Access;
            public byte[]? Future;
            public byte[]? Interchange;
            public byte[]? CrossCoverage;

            public bool Any => Demand is not null || Jobs is not null || Coverage is not null || Access is not null
                || Future is not null || Interchange is not null || CrossCoverage is not null;
        }

        // The cap of one unbounded term: PositivePercentile over the term's values,
        // copied into `scratch` first. `percentileScratch` is the selection's own
        // workspace; both must hold at least `totalCells`.
        public static float TermCap(SuitabilityCell[] terms, int totalCells, CapTerm term, float[] scratch, float[] percentileScratch)
        {
            for (int i = 0; i < totalCells; i++)
            {
                SuitabilityCell cell = terms[i];
                switch (term)
                {
                    case CapTerm.Jobs:
                        scratch[i] = cell.m_Jobs;
                        break;
                    case CapTerm.Future:
                        scratch[i] = cell.m_Future;
                        break;
                    default:
                        scratch[i] = cell.m_Demand;
                        break;
                }
            }

            return SuitabilityScoring.PositivePercentile(scratch, totalCells, Assumptions.TermCapPercentile, percentileScratch);
        }

        // Every tile's score under `weights` (SuitabilityScoring.Combine), zero where
        // the tile has no network node within the access walk (register A1.5), and
        // the registered per-term layers as unweighted bytes.
        public static void CombineField(
            SuitabilityCell[] terms, int totalCells, int[]? tileNode, in CombineWeights weights, float invSelf, float[] scores, TermLayers layers)
        {
            bool anyTermLayer = layers.Any;
            for (int i = 0; i < totalCells; i++)
            {
                SuitabilityCell cell = terms[i];
                bool onNetwork = tileNode is not null && i < tileNode.Length && tileNode[i] >= 0;
                scores[i] = onNetwork ? SuitabilityScoring.Combine(in cell, in weights, invSelf) : 0f;

                if (anyTermLayer)
                {
                    WriteTerm(layers.Demand, i, SuitabilityScoring.Saturate(cell.m_Demand * weights.InvDemand));
                    WriteTerm(layers.Jobs, i, SuitabilityScoring.Saturate(cell.m_Jobs * weights.InvJobs));
                    WriteTerm(layers.Coverage, i, SuitabilityScoring.CoverageShare(cell.m_Coverage));
                    WriteTerm(layers.Access, i, cell.m_Access);
                    WriteTerm(layers.Future, i, SuitabilityScoring.Saturate(cell.m_Future * weights.InvFuture));
                    WriteTerm(layers.Interchange, i, SuitabilityScoring.Saturate(cell.m_Interchange * invSelf));
                    WriteTerm(layers.CrossCoverage, i, SuitabilityScoring.Saturate(cell.m_CrossCoverage * invSelf));
                }
            }
        }

        public static void WriteTerm(byte[]? layer, int index, float normalized)
        {
            if (layer is not null)
            {
                layer[index] = IntensityByte(normalized);
            }
        }

        // 0..1 to a byte, rounded half to even as Unity's math.round did.
        public static byte IntensityByte(float normalized)
        {
            float clamped = SuitabilityScoring.Saturate(normalized);
            return (byte)Math.Round(clamped * 255f, MidpointRounding.ToEven);
        }

        // Demand near each network node — sat(T1·invD) + sat(T2·invJ) at the node's
        // tile — so corridor growth can tell a street with people on it from a rural
        // through-road carrying only passing trips.
        public static float[] NodeDemand(
            AlignmentNetwork network, SuitabilityCell[] terms, float demandCap, float jobsCap, float2Like worldMin, float tileSize, int2Like grid)
        {
            var demand = new float[network.NodeCount];
            float invDemand = demandCap > 0f ? 1f / demandCap : 0f;
            float invJobs = jobsCap > 0f ? 1f / jobsCap : 0f;

            for (int n = 0; n < network.NodeCount; n++)
            {
                var position = new float2Like(network.NodePositionsX[n], network.NodePositionsZ[n]);
                int2Like cell = TileGrid.WorldToCell(position, worldMin, tileSize, grid);
                int index = cell.x + (cell.y * grid.x);
                if (index < 0 || index >= terms.Length)
                {
                    continue;
                }

                SuitabilityCell tile = terms[index];
                demand[n] = SuitabilityScoring.Saturate(tile.m_Demand * invDemand)
                    + SuitabilityScoring.Saturate(tile.m_Jobs * invJobs);
            }

            return demand;
        }

        // Paints each site as a small disc, brightest for the best rank, so the layer
        // reads as discrete markers rather than a gradient.
        public static void PaintSites(byte[] layer, int[] siteTiles, int count, int width, int height)
        {
            for (int s = 0; s < count; s++)
            {
                int index = siteTiles[s];
                int cx = index % width;
                int cy = index / width;
                byte intensity = (byte)Math.Max(55, Math.Min(255, 255 - (s * (200 / Math.Max(1, count)))));

                for (int dy = -Assumptions.SiteMarkerRadiusTiles; dy <= Assumptions.SiteMarkerRadiusTiles; dy++)
                {
                    int y = cy + dy;
                    if (y < 0 || y >= height)
                    {
                        continue;
                    }

                    for (int dx = -Assumptions.SiteMarkerRadiusTiles; dx <= Assumptions.SiteMarkerRadiusTiles; dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= width)
                        {
                            continue;
                        }

                        if ((dx * dx) + (dy * dy) > Assumptions.SiteMarkerRadiusTiles * Assumptions.SiteMarkerRadiusTiles)
                        {
                            continue;
                        }

                        layer[x + (y * width)] = intensity;
                    }
                }
            }
        }
    }
}
