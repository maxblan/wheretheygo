using System;
using System.Globalization;
using System.Text;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // The recommended sites: candidate network nodes scored under the last combine,
    // the exact selection under the mode's stop spacing, and the layer they are drawn in.
    public sealed partial class TransitArchitectSystem
    {
        private int[]? m_LoggedSiteIndices;

        private float[]? m_LoggedSiteScores;

        private int m_LoggedSiteCount = -1;

        private readonly int[] m_SiteIndices = new int[Setting.kSiteCountMax];

        private readonly float[] m_SiteScores = new float[Setting.kSiteCountMax];

        // The candidate set the last exact selection ran on, kept for the export.
        private int[] m_SiteCandidateNodes = Array.Empty<int>();

        private float[] m_SiteCandidateScores = Array.Empty<float>();

        private int m_SiteCandidateCount;

        private int m_SiteSeparationMs;

        private int m_SiteCount;

        // Non-maximum suppression turns the gradient into discrete candidate sites,
        // then each survivor is re-scored with a real walk-distance expansion over
        // the landmass — affordable here precisely because there are only a handful.
        // The chosen sites for the renderer, best first: where each one is and how it
        // ranks. Computed on the spot from the tile index rather than kept as a second
        // copy — a dozen sites once a frame is cheaper than a cache that can disagree
        // with the map.
        internal int SiteCount => m_SiteCount;

        internal bool TryGetSite(int rank, out float3 position, out int total)
        {
            total = m_SiteCount;
            position = default;
            if (rank < 0 || rank >= m_SiteCount || m_IntensityGrid.x <= 0)
            {
                return false;
            }

            int index = m_SiteIndices[rank];
            int x = index % m_IntensityGrid.x;
            int y = index / m_IntensityGrid.x;
            float2 world = m_ScoreWorldMin + new float2((x + 0.5f) * Assumptions.TileSize, (y + 0.5f) * Assumptions.TileSize);
            position = new float3(world.x, 0f, world.y);
            return true;
        }

        private void ExtractSites(Setting settings)
        {
            if (m_Scores is null)
            {
                return;
            }

            byte[]? sitesLayer = TermLayer(SuitabilityLayer.Sites);
            if (sitesLayer is not null)
            {
                Array.Clear(sitesLayer, 0, sitesLayer.Length);
            }

            m_SiteCount = 0;

            int width = m_IntensityGrid.x;
            int height = m_IntensityGrid.y;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            WalkAccessOutput? access = m_Access;
            WalkAccessInputs? inputs = m_AccessInputs;
            byte[]? buildable = m_Buildable;
            if (access is null || inputs is null || buildable is null)
            {
                return;
            }

            var worldMin = new float2Like(m_ScoreWorldMin.x, m_ScoreWorldMin.y);
            var grid = new int2Like(width, height);

            // Candidates are network nodes (register A2.1): every node whose own tile
            // is buildable and whose score for the map's mode is positive. Two
            // candidates conflict when the walk between them is shorter than the
            // mode's stop spacing (A2.2) — the same metric the stops are later set by.
            int wanted = Math.Min(settings.SiteCount, m_SiteIndices.Length);
            int separationMs = Math.Max(1, WalkGraph.WalkMilliseconds(Assumptions.StopSpacingFor(settings.Mode)));
            m_SiteCandidateCount = SuitabilityExactSites.CollectNetworkCandidates(
                inputs.Graph, access.Result, access.Class, access.SelfType, access.TypeWeight,
                buildable, worldMin, Assumptions.TileSize, grid, in m_Scored, m_ScoredInvSelf,
                ref m_SiteCandidateNodes, ref m_SiteCandidateScores);

            // Exact selection: the set of at most `wanted` candidates with the largest
            // score sum under the spacing, or — should the node budget run out on a
            // pathological field — the best set found with a proven ceiling. The
            // greedy ranking it replaced left up to 1.3 % of score on a real city
            // (docs/correctness-claims.md C2.3).
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ExactSiteSolution exact = SuitabilityExactSites.SolveOnNetwork(
                inputs.Graph, m_SiteCandidateNodes, m_SiteCandidateScores, m_SiteCandidateCount,
                separationMs, wanted, Assumptions.SiteSearchNodeBudget);
            stopwatch.Stop();
            m_SiteSeparationMs = separationMs;

            m_SiteCount = exact.Count;
            for (int i = 0; i < exact.Count; i++)
            {
                m_SiteIndices[i] = SuitabilityExactSites.TileOfNode(inputs.Graph, exact.Indices[i], worldMin, Assumptions.TileSize, grid);
                m_SiteScores[i] = exact.Scores[i];
            }

            LogSiteSelection(exact, separationMs, wanted, stopwatch.ElapsedMilliseconds);

            if (m_SiteCount == 0)
            {
                return;
            }

            if (sitesLayer is null)
            {
                LogSites();
                return;
            }

            // Paint each site as a small disc, brightest for the best rank, so the
            // layer reads as discrete markers rather than a gradient.
            SuitabilityHeatmap.PaintSites(sitesLayer, m_SiteIndices, m_SiteCount, width, height);

            LogSites();
        }

        private static void LogSiteSelection(ExactSiteSolution exact, int separationMs, int wanted, long elapsedMs)
        {
            double unit = Math.Pow(2.0, -exact.ScaleShift);
            string value = (exact.Value * unit).ToString("F4", CultureInfo.InvariantCulture);
            string closure = exact.Optimal
                ? "optimal (search closed)"
                : "best found, NOT proven optimal; ceiling " + (exact.UpperBound * unit).ToString("F4", CultureInfo.InvariantCulture);
            DeferredLog.Info(
                $"Site selection: {exact.Count.ToString(CultureInfo.InvariantCulture)} of up to {wanted.ToString(CultureInfo.InvariantCulture)} sites, "
                + $"spacing {(separationMs / 1000).ToString(CultureInfo.InvariantCulture)} s walk, "
                + $"{exact.Candidates.ToString(CultureInfo.InvariantCulture)} candidate network nodes, "
                + $"score sum {value} {closure}, "
                + $"{exact.Nodes.ToString(CultureInfo.InvariantCulture)} search nodes in {elapsedMs.ToString(CultureInfo.InvariantCulture)} ms"
                + (exact.WeightsExact ? string.Empty : " (integer weights floored: score spread beyond 2^33)"));

            if (exact.CandidatesTruncated)
            {
                DeferredLog.Warn("Site search hit its candidate budget; the reported sites may miss better ones.");
            }

            if (!exact.Optimal)
            {
                DeferredLog.Warn("Site search ran out of its node budget; the reported ranking is the best found, not proven optimal.");
            }
        }

        private bool SitesChangedMeaningfully()
        {
            if (m_LoggedSiteIndices is null || m_LoggedSiteScores is null)
            {
                return true;
            }

            if (m_LoggedSiteCount != m_SiteCount)
            {
                return true;
            }

            for (int s = 0; s < m_SiteCount; s++)
            {
                if (m_LoggedSiteIndices[s] != m_SiteIndices[s])
                {
                    return true;
                }

                float previous = m_LoggedSiteScores[s];
                float delta = math.abs(m_SiteScores[s] - previous);
                if (delta > math.max(1f, math.abs(previous) * Assumptions.SiteScoreLogThreshold))
                {
                    return true;
                }
            }

            return false;
        }

        private void LogSites()
        {
            // The overlay recomputes every ten seconds; only say something when the
            // recommendation actually changed.
            // Log when the recommendation actually changes. Scores count as changed
            // only past a relative threshold: a growing city nudges them by a
            // fraction of a percent every recompute, which is real but not worth
            // reporting, whereas the double-counting bug this guards against moved
            // them by multiples.
            if (!SitesChangedMeaningfully())
            {
                return;
            }

            if (m_LoggedSiteIndices is null || m_LoggedSiteIndices.Length != m_SiteIndices.Length)
            {
                m_LoggedSiteIndices = new int[m_SiteIndices.Length];
                m_LoggedSiteScores = new float[m_SiteScores.Length];
            }

            m_LoggedSiteCount = m_SiteCount;
            Array.Copy(m_SiteIndices, m_LoggedSiteIndices, m_SiteCount);
            Array.Copy(m_SiteScores, m_LoggedSiteScores, m_SiteCount);

            var builder = new StringBuilder();
            _ = builder.Append("Recommended sites (walk-distance ranked): ");
            for (int s = 0; s < m_SiteCount; s++)
            {
                int index = m_SiteIndices[s];
                int x = index % m_IntensityGrid.x;
                int y = index / m_IntensityGrid.x;
                float2 world = m_ScoreWorldMin + new float2((x + 0.5f) * Assumptions.TileSize, (y + 0.5f) * Assumptions.TileSize);
                if (s > 0)
                {
                    _ = builder.Append(", ");
                }

                _ = builder.Append('#');
                _ = builder.Append(s + 1);
                // float2 carries (x, z) in world space throughout this mod; the log
                // said "(x, y)" and invited the reader to look up the wrong axis.
                _ = builder.Append(" (x ");
                _ = builder.Append(((int)world.x).ToString(CultureInfo.InvariantCulture));
                _ = builder.Append(", z ");
                _ = builder.Append(((int)world.y).ToString(CultureInfo.InvariantCulture));
                _ = builder.Append(") score ");
                _ = builder.Append(m_SiteScores[s].ToString("F1", CultureInfo.InvariantCulture));
            }

            DeferredLog.Info(builder.ToString());
        }
    }
}
