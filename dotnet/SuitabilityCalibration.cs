using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Colossal.Entities;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // Builds a ridership time series the game does not keep, then fits the scoring
    // weights to it.
    //
    // Why sampling at all: Cities: Skylines II exposes no per-stop ridership
    // history. WaitingPassengers.m_Count is an instantaneous queue length that
    // WaitingPassengersSystem zeroes and re-tallies every 256 simulation frames,
    // m_OngoingAccumulation is reset on the same tick, and
    // StatisticType.PassengerCount* exists only as a city-wide per-mode total with
    // no per-stop breakdown. So the mod accumulates its own observations.
    //
    // Why arrival rate rather than queue length: a queue is a congestion signal, not
    // a demand signal — a busy stop with good service drains its queue and looks
    // idle, so regressing on m_Count would actively mislead. Little's law (L = λW)
    // turns the two quantities the game does maintain, queue length and average
    // wait, into an arrival rate, which is the throughput the placement model is
    // actually trying to predict.
    internal sealed class SuitabilityCalibration
    {
        public const int MinSamplesPerStop = 30;
        public const int MinStops = 8;
        private const int MaxTrackedStops = 256;
        private const int FeatureCount = 4;

        // Per-stop running totals. Keyed by rounded world position because Entity
        // ids are not stable across a save/load.
        private sealed class StopRecord
        {
            public int m_Samples;
            public double m_ArrivalRateSum;
            public double m_WaitSum;
            public float[] m_Features = new float[FeatureCount];
        }

        private readonly Dictionary<long, StopRecord> m_Records = new Dictionary<long, StopRecord>();
        private readonly float[] m_Fitted = new float[FeatureCount];
        private bool m_HasFit;
        private float m_RSquared;
        private int m_FittedStops;

        public bool HasFit => m_HasFit;
        public float RSquared => m_RSquared;
        public int TrackedStops => m_Records.Count;

        public float FittedDemand => m_Fitted[0];
        public float FittedJobs => m_Fitted[1];
        public float FittedAccess => m_Fitted[2];
        public float FittedFuture => m_Fitted[3];

        public int ReadyStops
        {
            get
            {
                int ready = 0;
                foreach (StopRecord record in m_Records.Values)
                {
                    if (record.m_Samples >= MinSamplesPerStop)
                    {
                        ready++;
                    }
                }

                return ready;
            }
        }

        public void Clear()
        {
            m_Records.Clear();
            m_HasFit = false;
            m_RSquared = 0f;
            m_FittedStops = 0;
        }

        private static long KeyOf(float2 position)
        {
            // 1 m resolution is far finer than stops are ever placed together, and
            // stops never move, so this is a stable identity across sessions.
            long x = (long)math.round(position.x);
            long z = (long)math.round(position.y);
            return (x << 20) ^ (z & 0xFFFFF);
        }

        // One observation per served stop of the active mode. `sampleFeatures`
        // returns the normalized term values at a world position, or false if the
        // position is outside the computed grid.
        public void Sample(
            EntityManager entityManager,
            EntityQuery stopQuery,
            Game.Prefabs.PrefabSystem prefabSystem,
            Setting.ModePreset mode,
            Func<float2, float[], bool> sampleFeatures)
        {
            using var entities = stopQuery.ToEntityArray(Allocator.Temp);
            using var transforms = stopQuery.ToComponentDataArray<Transform>(Allocator.Temp);
            using var prefabs = stopQuery.ToComponentDataArray<Game.Prefabs.PrefabRef>(Allocator.Temp);

            var features = new float[FeatureCount];

            for (int i = 0; i < entities.Length; i++)
            {
                if (!SuitabilityInputs.IsStopOfMode(prefabSystem, prefabs[i].m_Prefab, mode))
                {
                    continue;
                }

                Entity stop = entities[i];
                if (!entityManager.TryGetBuffer(stop, true, out DynamicBuffer<ConnectedRoute> routes) || routes.Length == 0)
                {
                    continue;
                }

                if (!TryReadPassengers(entityManager, stop, routes, out float queue, out float wait))
                {
                    continue;
                }

                float3 pos3 = transforms[i].m_Position;
                var position = new float2(pos3.x, pos3.z);
                if (!sampleFeatures(position, features))
                {
                    continue;
                }

                long key = KeyOf(position);
                if (!m_Records.TryGetValue(key, out StopRecord record))
                {
                    if (m_Records.Count >= MaxTrackedStops)
                    {
                        continue;
                    }

                    record = new StopRecord();
                    m_Records[key] = record;
                }

                // Little's law: arrival rate = queue length / average wait.
                float arrivalRate = queue / math.max(wait, 1f);
                record.m_Samples++;
                record.m_ArrivalRateSum += arrivalRate;
                record.m_WaitSum += wait;
                Array.Copy(features, record.m_Features, FeatureCount);
            }
        }

        // Passengers are counted on the stop itself and on each of its per-line
        // waypoints, matching how Game.UI.InGame.LinesSection totals them for the
        // stop panel. Average wait is taken as the maximum across those, since it is
        // already a smoothed per-queue estimate rather than something additive.
        private static bool TryReadPassengers(
            EntityManager entityManager,
            Entity stop,
            DynamicBuffer<ConnectedRoute> routes,
            out float queue,
            out float wait)
        {
            queue = 0f;
            wait = 0f;
            bool any = false;

            if (entityManager.TryGetComponent(stop, out WaitingPassengers stopPassengers))
            {
                queue += stopPassengers.m_Count;
                wait = math.max(wait, stopPassengers.m_AverageWaitingTime);
                any = true;
            }

            for (int i = 0; i < routes.Length; i++)
            {
                if (entityManager.TryGetComponent(routes[i].m_Waypoint, out WaitingPassengers waypointPassengers))
                {
                    queue += waypointPassengers.m_Count;
                    wait = math.max(wait, waypointPassengers.m_AverageWaitingTime);
                    any = true;
                }
            }

            return any;
        }

        // Fits demand/jobs/access/future against the observed arrival rate. Runs
        // only when enough stops have enough samples, so an early fit on two noisy
        // observations cannot be mistaken for a calibrated model.
        public bool TryFit()
        {
            var usable = new List<StopRecord>();
            foreach (StopRecord record in m_Records.Values)
            {
                if (record.m_Samples >= MinSamplesPerStop)
                {
                    usable.Add(record);
                }
            }

            if (usable.Count < MinStops)
            {
                return false;
            }

            int rows = usable.Count;
            var features = new float[rows, FeatureCount];
            var target = new float[rows];
            for (int r = 0; r < rows; r++)
            {
                StopRecord record = usable[r];
                for (int c = 0; c < FeatureCount; c++)
                {
                    features[r, c] = record.m_Features[c];
                }

                target[r] = (float)(record.m_ArrivalRateSum / record.m_Samples);
            }

            if (!SuitabilityScoring.FitNonNegativeLeastSquares(features, target, rows, FeatureCount, m_Fitted))
            {
                m_HasFit = false;
                return false;
            }

            m_RSquared = SuitabilityScoring.RSquared(features, target, rows, FeatureCount, m_Fitted);
            m_FittedStops = rows;
            m_HasFit = true;
            return true;
        }

        // One line, because the options page renders this as a single read-only
        // field. The full breakdown is logged instead.
        public string BuildSummary()
        {
            var builder = new StringBuilder();

            if (m_HasFit)
            {
                builder.Append("R² ");
                builder.Append(m_RSquared.ToString("F2", CultureInfo.InvariantCulture));
                builder.Append(" over ");
                builder.Append(m_FittedStops);
                builder.Append(" stops — suggested: demand ");
                builder.Append(m_Fitted[0].ToString("F2", CultureInfo.InvariantCulture));
                builder.Append(", jobs ");
                builder.Append(m_Fitted[1].ToString("F2", CultureInfo.InvariantCulture));
                builder.Append(", access ");
                builder.Append(m_Fitted[2].ToString("F2", CultureInfo.InvariantCulture));
                builder.Append(", future ");
                builder.Append(m_Fitted[3].ToString("F2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }

            builder.Append("Collecting while unpaused: ");
            builder.Append(ReadyStops);
            builder.Append(" of ");
            builder.Append(MinStops);
            builder.Append(" stops ready, ");
            builder.Append(m_Records.Count);
            builder.Append(" tracked (need ");
            builder.Append(MinSamplesPerStop);
            builder.Append(" samples each)");
            return builder.ToString();
        }

        // Compact CSV so the whole series survives in the settings file. One record
        // per stop, aggregates only — never raw samples.
        public string Serialize()
        {
            var builder = new StringBuilder();
            foreach (KeyValuePair<long, StopRecord> pair in m_Records)
            {
                StopRecord record = pair.Value;
                builder.Append(pair.Key.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(record.m_Samples.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(record.m_ArrivalRateSum.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(record.m_WaitSum.ToString("R", CultureInfo.InvariantCulture));
                for (int c = 0; c < FeatureCount; c++)
                {
                    builder.Append(':');
                    builder.Append(record.m_Features[c].ToString("R", CultureInfo.InvariantCulture));
                }

                builder.Append(';');
            }

            return builder.ToString();
        }

        public void Deserialize(string data)
        {
            m_Records.Clear();
            m_HasFit = false;
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            string[] records = data.Split(';');
            for (int i = 0; i < records.Length; i++)
            {
                if (records[i].Length == 0)
                {
                    continue;
                }

                string[] parts = records[i].Split(':');
                if (parts.Length < 4 + FeatureCount)
                {
                    continue;
                }

                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long key) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int samples) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double rateSum) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double waitSum))
                {
                    continue;
                }

                var record = new StopRecord
                {
                    m_Samples = samples,
                    m_ArrivalRateSum = rateSum,
                    m_WaitSum = waitSum,
                };

                bool ok = true;
                for (int c = 0; c < FeatureCount; c++)
                {
                    if (!float.TryParse(parts[4 + c], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                    {
                        ok = false;
                        break;
                    }

                    record.m_Features[c] = value;
                }

                if (ok && m_Records.Count < MaxTrackedStops)
                {
                    m_Records[key] = record;
                }
            }
        }
    }
}
