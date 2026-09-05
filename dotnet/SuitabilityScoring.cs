using System;

namespace StationSuitabilityOverlay
{
    // Pure scoring math, deliberately free of Unity and ECS types so it compiles
    // into both the mod (net48) and a plain test project. Every numerical bug this
    // mod has had lived in here, so this is the part that is unit tested — see
    // tests/SuitabilityScoring.Tests.
    //
    // net48 has neither MathF nor Math.Clamp, so everything goes through
    // System.Math with explicit float casts.
    internal static class SuitabilityScoring
    {
        // Ceiling on the 3x3 local maxima FindTopSites will consider. A real city
        // produces far fewer; a noisy score field could produce far more, and a
        // truncated sweep is biased towards low grid indices, so callers are told.
        private const int MaxSiteCandidates = 65536;

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

        // The S2 candidate set: cells with a positive score that are 3x3 local maxima,
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
            int cap = Math.Min(cells, MaxSiteCandidates);
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

        // Walk-distance catchment for one candidate site: Dijkstra over walkable
        // tiles (8-connected, diagonal costing sqrt(2) steps) out to `radius` metres
        // of NETWORK distance, summing the demand and jobs densities it can actually
        // reach, each weighted down linearly with distance.
        //
        // `distanceScratch` and `visitedScratch` must both be at least width*height
        // long; only the window the radius can reach is touched, so they do not need
        // clearing between calls.
        //
        // The visited check is load-bearing, not defensive: a tile is reachable by
        // many paths and therefore enters the frontier many times, so without
        // settling it once its density is added once per pop and the total silently
        // depends on pop order. That produced an 8x swing between identical
        // recomputes of the same site.
        public static float AccumulateWalkDistance(
            int siteIndex,
            int width,
            int height,
            float tileSize,
            float radius,
            byte[] land,
            float[] tileDemand,
            float[] tileJobs,
            float demandWeight,
            float jobsWeight,
            float[] distanceScratch,
            byte[] visitedScratch,
            out float reachedDemand,
            out float reachedJobs)
        {
            reachedDemand = 0f;
            reachedJobs = 0f;

            int cells = width * height;
            if (land is null || tileDemand is null || tileJobs is null
                || distanceScratch is null || visitedScratch is null
                || width <= 0 || height <= 0 || radius <= 0f || tileSize <= 0f
                || siteIndex < 0 || siteIndex >= cells
                || distanceScratch.Length < cells || visitedScratch.Length < cells
                || land.Length < cells || tileDemand.Length < cells || tileJobs.Length < cells)
            {
                return 0f;
            }

            if (land[siteIndex] == 0)
            {
                return 0f;
            }

            int span = (int)Math.Ceiling(radius / tileSize) + 1;
            int sx = siteIndex % width;
            int sy = siteIndex / width;
            int minX = Math.Max(0, sx - span);
            int maxX = Math.Min(width - 1, sx + span);
            int minY = Math.Max(0, sy - span);
            int maxY = Math.Min(height - 1, sy + span);

            for (int y = minY; y <= maxY; y++)
            {
                int row = y * width;
                for (int x = minX; x <= maxX; x++)
                {
                    distanceScratch[row + x] = float.MaxValue;
                    visitedScratch[row + x] = 0;
                }
            }

            var frontier = new TileFrontier(distanceScratch, (maxX - minX + 1) * (maxY - minY + 1));
            distanceScratch[siteIndex] = 0f;
            frontier.Push(siteIndex);

            float diagonal = (float)Math.Sqrt(2.0) * tileSize;

            while (!frontier.IsEmpty)
            {
                int current = frontier.Pop();

                // Stale duplicate of an already-settled tile.
                if (visitedScratch[current] != 0)
                {
                    continue;
                }

                visitedScratch[current] = 1;

                // Nothing past the radius is ever pushed, so a tile that settles is
                // always in range and its weight is always positive.
                float bestDistance = distanceScratch[current];
                float weight = 1f - bestDistance / radius;
                reachedDemand += tileDemand[current] * weight;
                reachedJobs += tileJobs[current] * weight;

                int cx = current % width;
                int cy = current / width;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (ny < minY || ny > maxY)
                    {
                        continue;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        int nx = cx + dx;
                        if (nx < minX || nx > maxX)
                        {
                            continue;
                        }

                        int neighbour = nx + ny * width;
                        if (land[neighbour] == 0 || visitedScratch[neighbour] != 0)
                        {
                            continue;
                        }

                        float step = (dx != 0 && dy != 0) ? diagonal : tileSize;
                        float candidate = bestDistance + step;
                        if (candidate > radius || candidate >= distanceScratch[neighbour])
                        {
                            continue;
                        }

                        distanceScratch[neighbour] = candidate;
                        frontier.Push(neighbour);
                    }
                }
            }

            return (reachedDemand * demandWeight) + (reachedJobs * jobsWeight);
        }

        // Lazy binary min-heap over tile indices, ordered by the caller's live
        // distance array. Same shape as DijkstraWorkspace's heap, and for the same
        // reason: a tile is reachable by many paths and therefore enters the frontier
        // many times, so stale entries have to be cheap to skip. A distance only ever
        // decreases, which can misplace a stale entry downward but never the live
        // minimum, so a pop still settles tiles in distance order.
        //
        // Replaces a List scanned linearly for its minimum and then RemoveAt-ed, both
        // O(n) per pop: over a 1000 m catchment the window is roughly 67x67 tiles, so
        // a single site cost on the order of ten million operations.
        private sealed class TileFrontier
        {
            private readonly float[] m_Distance;
            private int[] m_Heap;
            private int m_Count;

            public TileFrontier(float[] distance, int capacity)
            {
                m_Distance = distance;
                m_Heap = new int[Math.Max(8, capacity) + 1];
            }

            public bool IsEmpty => m_Count == 0;

            public void Push(int tile)
            {
                if (m_Count + 1 >= m_Heap.Length)
                {
                    Array.Resize(ref m_Heap, m_Heap.Length * 2);
                }

                m_Heap[++m_Count] = tile;
                int slot = m_Count;
                while (slot > 1)
                {
                    int parent = slot >> 1;
                    if (m_Distance[m_Heap[parent]] <= m_Distance[m_Heap[slot]])
                    {
                        break;
                    }

                    Swap(parent, slot);
                    slot = parent;
                }
            }

            public int Pop()
            {
                int top = m_Heap[1];
                m_Heap[1] = m_Heap[m_Count--];
                int slot = 1;
                while (true)
                {
                    int left = slot << 1;
                    if (left > m_Count)
                    {
                        break;
                    }

                    int best = left;
                    int right = left + 1;
                    if (right <= m_Count && m_Distance[m_Heap[right]] < m_Distance[m_Heap[left]])
                    {
                        best = right;
                    }

                    if (m_Distance[m_Heap[slot]] <= m_Distance[m_Heap[best]])
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
}
