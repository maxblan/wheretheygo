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
        private const int FeatureCount = SuitabilityScoring.CalibrationFeatureCount;
        // Stamped into the persisted series. Records gathered under another scoring
        // model — before 2026-09-05 the access kernel was a fourth regressor and a
        // term of its own — are discarded on load rather than fitted against a model
        // that no longer produces them.
        private const int ScoringModel = 2;

        // Per-stop running totals. Keyed by rounded world position because Entity
        // ids are not stable across a save/load.
        private sealed class StopRecord
        {
            public int m_Samples;
            public double m_ArrivalRateSum;
            public double m_WaitSum;
            // A running MEAN of the term values at this stop, not the latest reading.
            // The regression target is a mean over the whole series, and overwriting
            // the predictors on every sample related a half-hour mean arrival rate to
            // the demand, jobs, access and future values as they stood in the last
            // sixty seconds — two different cities once a district built out during
            // collection. Kept as a mean rather than a sum so the persisted format is
            // unchanged and an older file's snapshot reads as a valid starting mean.
            public float[] m_Features = new float[FeatureCount];
        }

        private readonly Dictionary<long, StopRecord> m_Records = new Dictionary<long, StopRecord>();
        private readonly float[] m_Fitted = new float[FeatureCount];
        private bool m_HasFit;
        private float m_RSquared;
        private int m_FittedStops;
        // Said once rather than every minute: the cap is a backstop, and a player who
        // has more stops than it holds should hear about it, but only once.
        private bool m_CapReported;

        public bool HasFit => m_HasFit;
        public float RSquared => m_RSquared;
        public int TrackedStops => m_Records.Count;

        public float FittedDemand => m_Fitted[0];
        public float FittedJobs => m_Fitted[1];
        public float FittedFuture => m_Fitted[2];

        // Mean rider wait across the tracked stops, in seconds. Logged beside the fit
        // because the whole Little's-law derivation rests on this being seconds: the
        // first version divided by the pathfinder's accumulator instead and produced a
        // target in no unit at all, which pinned three of four coefficients at zero.
        public float MeanWaitSeconds
        {
            get
            {
                double waits = 0.0;
                long samples = 0;
                foreach (StopRecord record in m_Records.Values)
                {
                    waits += record.m_WaitSum;
                    samples += record.m_Samples;
                }

                return samples > 0 ? (float)(waits / samples) : 0f;
            }
        }

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

        // Leaves m_City alone: clearing the samples does not change which city we are
        // in, and RetargetTo sets it explicitly when it does.
        public void Clear()
        {
            m_Records.Clear();
            m_HasFit = false;
            m_RSquared = 0f;
            m_FittedStops = 0;
            m_CapReported = false;
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
        // `waitSecondsAt` gives the expected rider wait in SECONDS at a stop position,
        // or 0 when no known line serves it. It is supplied by the caller because it
        // comes from the lines the overlay system has already collected and verified.
        public void Sample(
            EntityManager entityManager,
            EntityQuery stopQuery,
            Game.Prefabs.PrefabSystem prefabSystem,
            ModePreset mode,
            Func<float2, float[], bool> sampleFeatures,
            Func<float2, float> waitSecondsAt)
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
                if (!entityManager.TryGetBuffer(stop, isReadOnly: true, out DynamicBuffer<ConnectedRoute> routes) || routes.Length == 0)
                {
                    continue;
                }

                if (!TryReadQueue(entityManager, stop, routes, out float queue))
                {
                    continue;
                }

                float3 pos3 = transforms[i].m_Position;
                var position = new float2(pos3.x, pos3.z);
                if (!sampleFeatures(position, features))
                {
                    continue;
                }

                // Little's law needs a wait in SECONDS. WaitingPassengers'
                // m_AverageWaitingTime is the pathfinder's accumulator in game units —
                // the same field SuitabilityLineHealth refuses to read — so dividing a
                // passenger count by it produced a target in no unit at all, which is
                // why the fit came back with three of its four coefficients pinned at
                // exactly zero. The wait now comes from the serving line's headway,
                // the same quantity every other part of this mod uses.
                float wait = waitSecondsAt(position);
                if (wait <= 0f)
                {
                    // No line we know of serves this stop, so there is no headway to
                    // divide by and no honest observation to record.
                    continue;
                }

                long key = KeyOf(position);
                if (!m_Records.TryGetValue(key, out StopRecord record))
                {
                    if (m_Records.Count >= MaxTrackedStops)
                    {
                        // A bound that truncates silently reads as "covered
                        // everything" when it did not.
                        if (!m_CapReported)
                        {
                            m_CapReported = true;
                            Mod.Log.Warn(
                                $"Ridership sampling is at its cap of {MaxTrackedStops.ToString(CultureInfo.InvariantCulture)} stops; " +
                                "further stops of this mode are not being tracked and cannot influence the fit.");
                        }

                        continue;
                    }

                    record = new StopRecord();
                    m_Records[key] = record;
                }

                // Little's law: arrival rate = queue length / average wait, in riders
                // per second.
                float arrivalRate = queue / math.max(wait, 1f);
                record.m_Samples++;
                record.m_ArrivalRateSum += arrivalRate;
                record.m_WaitSum += wait;

                // Incremental mean, so the predictors describe the same stretch of
                // play the target does. Accumulated in double and stored back as float
                // to keep the persisted format as it was.
                for (int c = 0; c < FeatureCount; c++)
                {
                    double previous = record.m_Features[c];
                    record.m_Features[c] = (float)(previous + ((features[c] - previous) / record.m_Samples));
                }
            }
        }

        // Passengers are counted on the stop itself and on each of its per-line
        // waypoints, matching how Game.UI.InGame.LinesSection totals them for the
        // stop panel. Average wait is taken as the maximum across those, since it is
        // already a smoothed per-queue estimate rather than something additive.
        // Riders queued at a stop. m_Count is a straight passenger count and is read
        // as one; the component's m_AverageWaitingTime is deliberately not touched
        // here — see the note at the call site.
        //
        // ConnectedRoute is used only to enumerate the waypoints that belong to this
        // stop, never to infer an order: the buffer is unordered and the game's own
        // travel order lives on the line.
        private static bool TryReadQueue(
            EntityManager entityManager,
            Entity stop,
            DynamicBuffer<ConnectedRoute> routes,
            out float queue)
        {
            queue = 0f;
            bool any = false;

            if (entityManager.TryGetComponent(stop, out WaitingPassengers stopPassengers))
            {
                queue += stopPassengers.m_Count;
                any = true;
            }

            for (int i = 0; i < routes.Length; i++)
            {
                if (entityManager.TryGetComponent(routes[i].m_Waypoint, out WaitingPassengers waypointPassengers))
                {
                    queue += waypointPassengers.m_Count;
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
            // Ordered by key rather than by however the dictionary enumerates: the row
            // order decides the summation order of the normal equations, and the
            // fitted weights reach the player's sliders.
            var keys = new List<long>(m_Records.Keys);
            keys.Sort();

            var usable = new List<StopRecord>();
            for (int i = 0; i < keys.Count; i++)
            {
                StopRecord record = m_Records[keys[i]];
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
                _ = builder.Append("R² ");
                _ = builder.Append(m_RSquared.ToString("F2", CultureInfo.InvariantCulture));
                _ = builder.Append(" over ");
                _ = builder.Append(m_FittedStops);
                _ = builder.Append(" stops — suggested: demand ");
                _ = builder.Append(m_Fitted[0].ToString("F2", CultureInfo.InvariantCulture));
                _ = builder.Append(", jobs ");
                _ = builder.Append(m_Fitted[1].ToString("F2", CultureInfo.InvariantCulture));
                _ = builder.Append(", future ");
                _ = builder.Append(m_Fitted[2].ToString("F2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }

            _ = builder.Append("Collecting while unpaused: ");
            _ = builder.Append(ReadyStops);
            _ = builder.Append(" of ");
            _ = builder.Append(MinStops);
            _ = builder.Append(" stops ready, ");
            _ = builder.Append(m_Records.Count);
            _ = builder.Append(" tracked (need ");
            _ = builder.Append(MinSamplesPerStop);
            _ = builder.Append(" samples each)");
            return builder.ToString();
        }

        // Compact CSV so the whole series survives in the settings file. One record
        // per stop, aggregates only — never raw samples.
        // The city these records were gathered in.
        //
        // Records are keyed by WORLD POSITION and persist in a mod setting, not in the
        // save — so without this, loading another city silently inherited the previous
        // one's ridership at the same coordinates and fitted weights to stops that do
        // not exist here. Nothing invalidated it but the manual reset button. The name
        // is stamped into the serialized data and checked on load.
        private string m_City = string.Empty;

        public string City => m_City;

        // Drops everything if this is a different city from the one the records came
        // from. Returns true when that happened, so the caller can say so.
        public bool RetargetTo(string city)
        {
            string next = city ?? string.Empty;
            if (string.Equals(m_City, next, StringComparison.Ordinal))
            {
                return false;
            }

            bool hadRecords = m_Records.Count > 0;
            Clear();
            m_City = next;
            return hadRecords;
        }

        public string Serialize()
        {
            var builder = new StringBuilder();
            // Leading city stamp, delimited like a record so an older payload without
            // one simply fails to parse as a record and is discarded.
            _ = builder.Append("city=");
            _ = builder.Append(m_City.Replace(';', ' ').Replace(':', ' '));
            _ = builder.Append(';');
            _ = builder.Append("model=");
            _ = builder.Append(ScoringModel.ToString(CultureInfo.InvariantCulture));
            _ = builder.Append(';');

            // Sorted so the same series always writes the same bytes: the settings
            // file is rewritten every few minutes, and dictionary order made it churn
            // for no reason.
            var keys = new List<long>(m_Records.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
            {
                StopRecord record = m_Records[keys[i]];
                _ = builder.Append(keys[i].ToString(CultureInfo.InvariantCulture));
                _ = builder.Append(':');
                _ = builder.Append(record.m_Samples.ToString(CultureInfo.InvariantCulture));
                _ = builder.Append(':');
                _ = builder.Append(record.m_ArrivalRateSum.ToString("R", CultureInfo.InvariantCulture));
                _ = builder.Append(':');
                _ = builder.Append(record.m_WaitSum.ToString("R", CultureInfo.InvariantCulture));
                for (int c = 0; c < FeatureCount; c++)
                {
                    _ = builder.Append(':');
                    _ = builder.Append(record.m_Features[c].ToString("R", CultureInfo.InvariantCulture));
                }

                _ = builder.Append(';');
            }

            return builder.ToString();
        }

        public void Deserialize(string data)
        {
            m_Records.Clear();
            m_HasFit = false;
            m_CapReported = false;
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            int malformed = 0;
            int overCap = 0;
            string[] records = data.Split(';');
            int model = 1;
            for (int i = 0; i < records.Length; i++)
            {
                if (records[i].StartsWith("model=", StringComparison.Ordinal)
                    && int.TryParse(records[i].Substring("model=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int stamped))
                {
                    model = stamped;
                }
            }

            if (model != ScoringModel)
            {
                Mod.Log.Info(
                    $"Ridership samples discarded: gathered under scoring model {model.ToString(CultureInfo.InvariantCulture)}, " +
                    $"this build fits model {ScoringModel.ToString(CultureInfo.InvariantCulture)} (access is a discount, not a regressor).");
                return;
            }

            for (int i = 0; i < records.Length; i++)
            {
                if (records[i].Length == 0 || records[i].StartsWith("model=", StringComparison.Ordinal))
                {
                    continue;
                }

                if (records[i].StartsWith("city=", StringComparison.Ordinal))
                {
                    m_City = records[i].Substring("city=".Length);
                    continue;
                }

                string[] parts = records[i].Split(':');
                if (parts.Length < 4 + FeatureCount)
                {
                    malformed++;
                    continue;
                }

                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long key) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int samples) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double rateSum) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double waitSum))
                {
                    malformed++;
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

                if (!ok)
                {
                    malformed++;
                }
                else if (m_Records.Count >= MaxTrackedStops)
                {
                    overCap++;
                }
                else
                {
                    m_Records[key] = record;
                }
            }

            // A payload that half-loads must say so: "collecting..." with no
            // explanation looks exactly like a fresh start.
            if (malformed > 0 || overCap > 0)
            {
                Mod.Log.Warn(
                    $"Ridership data loaded with losses: {malformed.ToString(CultureInfo.InvariantCulture)} record(s) unreadable, " +
                    $"{overCap.ToString(CultureInfo.InvariantCulture)} past the cap of {MaxTrackedStops.ToString(CultureInfo.InvariantCulture)}. " +
                    $"{m_Records.Count.ToString(CultureInfo.InvariantCulture)} stop(s) restored.");
            }
        }
    }
}
