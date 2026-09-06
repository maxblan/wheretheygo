using System;

namespace TransitArchitect
{
    // Pure scoring math, deliberately free of Unity and ECS types so it compiles
    // into both the mod (net48) and a plain test project. Every numerical bug this
    // mod has had lived in here, so this is the part that is unit tested — see
    // tests/TransitArchitect.Tests.
    //
    // net48 has neither MathF nor Math.Clamp, so everything goes through
    // System.Math with explicit float casts.
    internal static class SuitabilityScoring
    {

        public static int ClampInt(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        public static float Saturate(float value)
        {
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        // Hoare-partition quickselect: returns the k-th smallest of the first
        // `length` entries. O(n) average instead of the O(n log n) a percentile does
        // not need. Reorders those entries in place; entries past `length` are left
        // untouched, which lets callers reuse one oversized scratch buffer.
        public static float SelectKth(float[] values, int length, int k)
        {
            if (values is null || length <= 0)
            {
                return 0f;
            }

            k = ClampInt(k, 0, length - 1);
            int lo = 0;
            int hi = length - 1;
            while (lo < hi)
            {
                float pivot = values[(lo + hi) >> 1];
                int i = lo;
                int j = hi;
                while (i <= j)
                {
                    while (values[i] < pivot)
                    {
                        i++;
                    }
                    while (values[j] > pivot)
                    {
                        j--;
                    }
                    if (i <= j)
                    {
                        float tmp = values[i];
                        values[i] = values[j];
                        values[j] = tmp;
                        i++;
                        j--;
                    }
                }

                if (k <= j)
                {
                    hi = j;
                }
                else if (k >= i)
                {
                    lo = i;
                }
                else
                {
                    return values[k];
                }
            }

            return values[k];
        }

        // Percentile over the STRICTLY POSITIVE entries of `source`. Returns 0 when
        // nothing is positive. Populates `scratch` (must be at least `length` long).
        //
        // Positive-only is the whole point: most of the map is empty land sitting at
        // exactly zero, and coverage penalties push served cells below zero, so a
        // percentile over all cells collapses onto ~0 and every remaining cell
        // saturates — the "entire map turns red" failure this replaced.
        public static float PositivePercentile(float[] source, int length, float percentile, float[] scratch)
        {
            if (source is null || scratch is null || length <= 0)
            {
                return 0f;
            }

            int count = 0;
            for (int i = 0; i < length; i++)
            {
                if (source[i] > 0f)
                {
                    scratch[count++] = source[i];
                }
            }

            if (count == 0)
            {
                return 0f;
            }

            int k = ClampInt((int)Math.Floor(count * (double)percentile), 0, count - 1);
            return SelectKth(scratch, count, k);
        }

        // The part of a tile's score that depends on WHICH mode is being placed:
        // what is close enough to change to, what is close enough to compete, and how
        // much of the mode's own service is already here. `coverageShare` is the
        // coverage term already expressed against its own ceiling.
        //
        // `invSelf` scales both cross-mode terms by the placing mode's own capacity, so
        // a bus gains a great deal from sitting at a metro station while a metro gains
        // little from sitting at a bus stop. That asymmetry is the feeder relationship:
        // the smaller mode should come to the trunk.
        public static float CoverageShare(float coverage)
        {
            return Math.Min(coverage, Assumptions.MaxCoveragePenalty) / Assumptions.MaxCoveragePenalty;
        }

        // The one formula that turns seven raw terms into a score. Two rules the user
        // settled on 2026-09-05 (register A1.6 v2, A1.16) live here and nowhere else:
        //
        // Access is a DISCOUNT, not a term. A pavement two minutes from anything
        // scores nothing, whatever the weight; W4 says how much the walk from the tile
        // to its pavement lowers the pavement's score — at 1 the tile's score is the
        // node's score times the access kernel, at 0 the walk is free. Before, W4 ×
        // access was added and every tile within reach of any pavement lit up, tunnels
        // and empty country roads included.
        //
        // Zoned-but-unbuilt land counts only where somebody already lives or works
        // within the node's catchment. Zoning alone yields no journeys today, so a
        // stop there could not be justified by anything the mod measures.
        public static float Combine(in SuitabilityCell cell, in CombineWeights weights, float invSelf)
        {
            float discount = AccessDiscount(in cell, in weights);
            float atNode = (weights.Demand * Saturate(cell.m_Demand * weights.InvDemand))
                + (weights.Jobs * Saturate(cell.m_Jobs * weights.InvJobs))
                + (weights.Future * GatedFuture(in cell, in weights))
                + ModeTerms(
                    CoverageShare(cell.m_Coverage), cell.m_Interchange, cell.m_CrossCoverage, invSelf,
                    weights.Coverage, weights.Interchange, weights.Cross);
            return discount * atNode;
        }

        // The three regressors of the ridership fit, in the units W1, W2 and W5
        // multiply: Combine(cell) = W1·f[0] + W2·f[1] + W5·f[2] + discount·ModeTerms
        // for every cell, so a fitted coefficient means exactly what its slider means
        // under the CURRENT W4 (pinned by `CalibrationFeaturesMatchCombine`). The
        // discount is not fitted: at a stop the access kernel is 1 or nearly so, which
        // makes it an intercept, not a slope.
        public const int CalibrationFeatureCount = 3;

        public static void CalibrationFeatures(in SuitabilityCell cell, in CombineWeights weights, float[] features)
        {
            float discount = AccessDiscount(in cell, in weights);
            features[0] = discount * Saturate(cell.m_Demand * weights.InvDemand);
            features[1] = discount * Saturate(cell.m_Jobs * weights.InvJobs);
            features[2] = discount * GatedFuture(in cell, in weights);
        }

        private static float AccessDiscount(in SuitabilityCell cell, in CombineWeights weights)
        {
            return Saturate(1f - (weights.Access * (1f - cell.m_Access)));
        }

        private static float GatedFuture(in SuitabilityCell cell, in CombineWeights weights)
        {
            return cell.m_Demand > 0f || cell.m_Jobs > 0f ? Saturate(cell.m_Future * weights.InvFuture) : 0f;
        }

        public static float ModeTerms(
            float coverageShare,
            float interchange,
            float crossCoverage,
            float invSelf,
            float coverageWeight,
            float interchangeWeight,
            float crossWeight)
        {
            return (interchangeWeight * Saturate(interchange * invSelf))
                - (coverageWeight * coverageShare)
                - (crossWeight * Saturate(crossCoverage * invSelf));
        }

        // Turns scores into 0..255 gradient intensities. `highlightShare` is the
        // fraction of built-up (positive-scoring) tiles that should reach the top of
        // the gradient; `gamma` below 1 lifts the low end so sparse areas stay
        // visible next to a saturated core.
        public static void NormalizeIntensities(
            float[] scores,
            int length,
            float highlightShare,
            float gamma,
            byte[] intensities,
            float[] scratch)
        {
            if (scores is null || intensities is null || length <= 0)
            {
                return;
            }

            // The loop below writes both arrays up to `length`. Clamping only the
            // clear made a short output array look handled and then indexed past it.
            if (scores.Length < length || intensities.Length < length)
            {
                return;
            }

            Array.Clear(intensities, 0, length);

            float cap = PositivePercentile(scores, length, 1f - highlightShare, scratch);
            if (cap <= 0f)
            {
                return;
            }

            float invCap = 1f / cap;
            for (int i = 0; i < length; i++)
            {
                float t = Saturate(scores[i] * invCap);
                if (t <= 0f)
                {
                    continue;
                }

                float shaped = (float)Math.Pow(t, gamma);
                intensities[i] = (byte)Math.Round(shaped * 255f, MidpointRounding.AwayFromZero);
            }
        }

        // Non-maximum suppression over a score grid: returns discrete candidate
        // sites instead of a gradient smear. Only 3x3 local maxima are considered,
        // which cuts the candidate set enormously before sorting, then sites are
        // taken best-first while rejecting anything within `minSeparation` tiles
        // (Chebyshev) of an already-accepted site.
        //
        // Writes cell indices into `outIndices` and their scores into `outScores`,
        // both descending by score, and returns how many were found.
        // `truncated` reports that the candidate buffer filled up before the whole
        // grid was scanned, which biases the result toward low grid indices. Callers
        // must surface that rather than presenting a partial sweep as complete.
        public static int FindTopSites(
            float[] scores,
            int width,
            int height,
            int minSeparation,
            int maxSites,
            int[] outIndices,
            float[] outScores,
            out bool truncated)
        {
            truncated = false;
            if (scores is null || outIndices is null || outScores is null)
            {
                return 0;
            }

            if (width <= 0 || height <= 0 || maxSites <= 0)
            {
                return 0;
            }

            int candidates = CollectSiteCandidates(
                scores, width, height, out int[] candidateIndices, out float[] candidateScores, out truncated);
            if (candidates == 0)
            {
                return 0;
            }

            // Sort candidates descending by score. Negate to get a descending sort
            // out of the ascending Array.Sort overload.
            var sortKeys = new float[candidates];
            var sortValues = new int[candidates];
            for (int i = 0; i < candidates; i++)
            {
                sortKeys[i] = -candidateScores[i];
                sortValues[i] = candidateIndices[i];
            }
            Array.Sort(sortKeys, sortValues);
            for (int i = 0; i < candidates; i++)
            {
                sortKeys[i] = -sortKeys[i];
            }

            return GreedySelect(sortValues, sortKeys, candidates, width, minSeparation, maxSites, outIndices, outScores);
        }

        // The F2 candidate set: cells with a positive score that are 3x3 local maxima,
        // in row-major order. Shared by the greedy ranking above and the exact
        // selection in SuitabilityExactSites, so both solve the same problem — a
        // candidate rule that existed in one place only would let the two disagree
        // about which cells are even eligible.
        //
        // `truncated` reports that the candidate buffer filled up before the whole
        // grid was scanned, which biases the result toward low grid indices. Callers
        // must surface that rather than presenting a partial sweep as complete.
        public static int CollectSiteCandidates(
            float[] scores,
            int width,
            int height,
            out int[] candidateIndices,
            out float[] candidateScores,
            out bool truncated)
        {
            truncated = false;
            candidateIndices = Array.Empty<int>();
            candidateScores = Array.Empty<float>();
            if (scores is null || width <= 0 || height <= 0)
            {
                return 0;
            }

            int cells = width * height;
            if (scores.Length < cells)
            {
                return 0;
            }

            // The budget is well above the count a real city produces, but a noisy
            // score field could exceed it, so overflow is reported rather than hidden.
            //
            // Grown into rather than allocated at the bound: starting at the cap meant
            // half a megabyte of candidate buffer on every recompute, whatever the map
            // actually held.
            int cap = Math.Min(cells, Assumptions.MaxSiteCandidates);
            candidateIndices = new int[Math.Min(cap, 1024)];
            candidateScores = new float[candidateIndices.Length];
            int candidates = 0;

            for (int y = 0; y < height; y++)
            {
                if (truncated)
                {
                    break;
                }

                for (int x = 0; x < width; x++)
                {
                    if (candidates == candidateIndices.Length)
                    {
                        if (candidates >= cap)
                        {
                            truncated = true;
                            break;
                        }

                        int grown = Math.Min(cap, candidateIndices.Length * 2);
                        Array.Resize(ref candidateIndices, grown);
                        Array.Resize(ref candidateScores, grown);
                    }

                    int index = x + y * width;
                    float score = scores[index];
                    if (score <= 0f)
                    {
                        continue;
                    }

                    if (!IsLocalMaximum(scores, width, height, x, y, score))
                    {
                        continue;
                    }

                    candidateIndices[candidates] = index;
                    candidateScores[candidates] = score;
                    candidates++;
                }
            }

            return candidates;
        }

        // Best-first acceptance over candidates already ordered by descending score,
        // rejecting anything within `minSeparation` tiles (Chebyshev) of an accepted
        // site. Writes at most `maxSites` cell indices and their scores.
        public static int GreedySelect(
            int[] orderedIndices,
            float[] orderedScores,
            int count,
            int width,
            int minSeparation,
            int maxSites,
            int[] outIndices,
            float[] outScores)
        {
            int accepted = 0;
            int separation = Math.Max(0, minSeparation);
            for (int i = 0; i < count && accepted < maxSites; i++)
            {
                int index = orderedIndices[i];
                int x = index % width;
                int y = index / width;

                bool tooClose = false;
                for (int j = 0; j < accepted; j++)
                {
                    int other = outIndices[j];
                    int dx = Math.Abs((other % width) - x);
                    int dy = Math.Abs((other / width) - y);
                    if (Math.Max(dx, dy) < separation)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (tooClose)
                {
                    continue;
                }

                outIndices[accepted] = index;
                outScores[accepted] = orderedScores[i];
                accepted++;
            }

            return accepted;
        }

        private static bool IsLocalMaximum(float[] scores, int width, int height, int x, int y, float score)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= height)
                {
                    continue;
                }

                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    int nx = x + dx;
                    if (nx < 0 || nx >= width)
                    {
                        continue;
                    }

                    // Strictly greater loses ties; >= on one side only would admit
                    // both cells of a plateau. Comparing against the linear index
                    // breaks ties deterministically so exactly one plateau cell wins.
                    float other = scores[nx + ny * width];
                    if (other > score)
                    {
                        return false;
                    }

                    if (other == score && (nx + ny * width) < (x + y * width))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // Least-squares fit of `target` against `features` with all coefficients
        // constrained to be non-negative (a negative weight would tell the player
        // that residents make a location worse, which the model must not express).
        //
        // Solves the normal equations by Gaussian elimination with partial
        // pivoting; any column that comes back negative is pinned to zero and the
        // remaining columns are refitted, repeating until every active coefficient
        // is non-negative. That is the standard active-set idea, which converges
        // here because each pass permanently removes at least one column.
        //
        // features is [rows, cols], row-major per observation. Returns false when
        // the system is underdetermined or singular, leaving `weights` zeroed.
        public static bool FitNonNegativeLeastSquares(
            float[,] features,
            float[] target,
            int rows,
            int cols,
            float[] weights)
        {
            if (features is null || target is null || weights is null)
            {
                return false;
            }

            if (rows <= cols || cols <= 0 || weights.Length < cols)
            {
                return false;
            }

            for (int c = 0; c < cols; c++)
            {
                weights[c] = 0f;
            }

            var active = new bool[cols];
            for (int c = 0; c < cols; c++)
            {
                active[c] = true;
            }

            for (int pass = 0; pass < cols; pass++)
            {
                int activeCount = 0;
                for (int c = 0; c < cols; c++)
                {
                    if (active[c])
                    {
                        activeCount++;
                    }
                }

                if (activeCount == 0)
                {
                    return false;
                }

                var map = new int[activeCount];
                int m = 0;
                for (int c = 0; c < cols; c++)
                {
                    if (active[c])
                    {
                        map[m++] = c;
                    }
                }

                // Normal equations over the active columns only.
                var normal = new double[activeCount, activeCount + 1];
                for (int i = 0; i < activeCount; i++)
                {
                    for (int j = 0; j < activeCount; j++)
                    {
                        double sum = 0.0;
                        for (int r = 0; r < rows; r++)
                        {
                            sum += (double)features[r, map[i]] * features[r, map[j]];
                        }
                        normal[i, j] = sum;
                    }

                    double rhs = 0.0;
                    for (int r = 0; r < rows; r++)
                    {
                        rhs += (double)features[r, map[i]] * target[r];
                    }
                    normal[i, activeCount] = rhs;
                }

                if (!SolveInPlace(normal, activeCount, out double[] solution))
                {
                    return false;
                }

                // Pin the most negative coefficient and refit, or accept.
                int worst = -1;
                double worstValue = 0.0;
                for (int i = 0; i < activeCount; i++)
                {
                    if (solution[i] < worstValue)
                    {
                        worstValue = solution[i];
                        worst = i;
                    }
                }

                if (worst < 0)
                {
                    for (int i = 0; i < activeCount; i++)
                    {
                        weights[map[i]] = (float)solution[i];
                    }

                    return true;
                }

                active[map[worst]] = false;
            }

            return false;
        }

        // Gaussian elimination with partial pivoting on an augmented [n, n+1]
        // matrix. Returns false if the matrix is singular to working precision.
        private static bool SolveInPlace(double[,] augmented, int n, out double[] solution)
        {
            solution = new double[n];

            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                double best = Math.Abs(augmented[col, col]);
                for (int r = col + 1; r < n; r++)
                {
                    double magnitude = Math.Abs(augmented[r, col]);
                    if (magnitude > best)
                    {
                        best = magnitude;
                        pivot = r;
                    }
                }

                if (best < 1e-9)
                {
                    return false;
                }

                if (pivot != col)
                {
                    for (int c = col; c <= n; c++)
                    {
                        double tmp = augmented[col, c];
                        augmented[col, c] = augmented[pivot, c];
                        augmented[pivot, c] = tmp;
                    }
                }

                double diagonal = augmented[col, col];
                for (int r = col + 1; r < n; r++)
                {
                    double factor = augmented[r, col] / diagonal;
                    if (factor == 0.0)
                    {
                        continue;
                    }

                    for (int c = col; c <= n; c++)
                    {
                        augmented[r, c] -= factor * augmented[col, c];
                    }
                }
            }

            for (int row = n - 1; row >= 0; row--)
            {
                double sum = augmented[row, n];
                for (int c = row + 1; c < n; c++)
                {
                    sum -= augmented[row, c] * solution[c];
                }

                solution[row] = sum / augmented[row, row];
            }

            return true;
        }

        // How much of the target the fitted weights account for, measured ABOUT ZERO
        // rather than about the mean.
        //
        // FitNonNegativeLeastSquares has no intercept column, and for a model with no
        // intercept the familiar mean-centred R² is the wrong measure: its denominator
        // describes a model the fit cannot express, so the result is unbounded below
        // and the Options page could show the player "R² -3.10" as a model quality.
        // Against zero the comparison is one the fit CAN make — every weight at zero
        // predicts zero — so the value lands in 0..1, with 0 meaning "no better than
        // predicting nothing" and 1 an exact fit.
        //
        // Returns 0 when the target is all zeros, where there is nothing to explain.
        public static float RSquared(float[,] features, float[] target, int rows, int cols, float[] weights)
        {
            if (features is null || target is null || weights is null || rows <= 0 || cols <= 0)
            {
                return 0f;
            }

            double residual = 0.0;
            double total = 0.0;
            for (int r = 0; r < rows; r++)
            {
                double predicted = 0.0;
                for (int c = 0; c < cols; c++)
                {
                    predicted += (double)features[r, c] * weights[c];
                }

                double error = target[r] - predicted;
                residual += error * error;
                total += (double)target[r] * target[r];
            }

            if (total <= 1e-9)
            {
                return 0f;
            }

            double r2 = 1.0 - (residual / total);
            return (float)r2;
        }
    }

    // The weights and caps a score field was combined under, pinned so a later query
    // (ScoreForMode) reproduces exactly the map's arithmetic. The inverses are 1/cap
    // of the 98th percentile of each term, or 0 when the term has no positive member.
    internal readonly struct CombineWeights
    {
        public readonly float Demand;
        public readonly float Jobs;
        public readonly float Coverage;
        public readonly float Access;
        public readonly float Future;
        public readonly float Interchange;
        public readonly float Cross;
        public readonly float InvDemand;
        public readonly float InvJobs;
        public readonly float InvFuture;

        public CombineWeights(
            float demand, float jobs, float coverage, float access, float future, float interchange, float cross,
            float invDemand, float invJobs, float invFuture)
        {
            Demand = demand;
            Jobs = jobs;
            Coverage = coverage;
            Access = access;
            Future = future;
            Interchange = interchange;
            Cross = cross;
            InvDemand = invDemand;
            InvJobs = invJobs;
            InvFuture = invFuture;
        }
    }
}
