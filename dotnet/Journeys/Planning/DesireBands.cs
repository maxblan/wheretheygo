using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // One band on the map: everybody travelling between roughly these two places.
    //
    // A band is undirected — the two ends are just A and B — but its traffic is not,
    // so the two hourly profiles are kept apart. HourlyAtoB[7] is how many journeys
    // leave A for B at seven in the morning. That is what lets the map breathe with
    // the clock instead of drawing one arrow per pair of buildings.
    internal sealed class Band
    {
        public float Ax;
        public float Az;
        public float Bx;
        public float Bz;

        // Journeys a day between the two ends, and how many of them the network
        // already carries (JourneyRouting.MarkCarried). The share of the second in the
        // first is the band's colour.
        public float Weight;
        public float CarriedWeight;

        // Which purposes travel here, as bits of 1 << (int)JourneyPurpose.
        public byte PurposeMask;

        public readonly float[] HourlyAtoB = new float[HoursPerDay];
        public readonly float[] HourlyBtoA = new float[HoursPerDay];

        // How many zone pairs were merged into this band. Not shown; logged, because a
        // band of one pair and a band of forty read the same on screen.
        public int Pairs;

        public const int HoursPerDay = 24;

        public float CarriedShare => Weight > 0f ? CarriedWeight / Weight : 0f;

        public float LengthMetres => (float)Math.Sqrt(((Bx - Ax) * (Bx - Ax)) + ((Bz - Az) * (Bz - Az)));

        // The busiest hour of the day over both directions, and what travels in it.
        public int PeakHour
        {
            get
            {
                int peak = 0;
                float best = -1f;
                for (int h = 0; h < HoursPerDay; h++)
                {
                    float total = HourlyAtoB[h] + HourlyBtoA[h];
                    if (total > best)
                    {
                        best = total;
                        peak = h;
                    }
                }

                return peak;
            }
        }

        public float WeightAtHour(int hour)
        {
            return hour is < 0 or >= HoursPerDay ? Weight : HourlyAtoB[hour] + HourlyBtoA[hour];
        }
    }

    // What one bundling produced, with what it had to leave out.
    internal sealed class BandSet
    {
        public Band[] Bands = Array.Empty<Band>();

        // Zone pairs that found no band because the cap was reached, and the journeys
        // a day they stand for. Logged, never hidden: a map that silently dropped a
        // tenth of the city's travel reads as a map of the whole city.
        public int HiddenPairs;
        public float HiddenWeight;

        public int MergedPairs;

        public float HeaviestWeight;
    }

    // Bundling the city's journeys into the bands the map draws.
    //
    // Two levels, and both are needed. Door-to-door pairs are far too many to draw —
    // a city has tens of thousands — so they are first summed per 256 m zone pair,
    // which is the same grid the demand has always been aggregated on. That still
    // leaves neighbouring corridors as separate hairs, so zone pairs whose BOTH ends
    // lie within a merge radius of an already-accepted band are folded into it. The
    // order is heaviest first, which makes the result independent of the order the
    // journeys arrived in and puts the city's real corridors in charge of where the
    // bands sit.
    internal static class DesireBands
    {
        // One zone pair before bundling: everybody travelling between two 256 m cells.
        private struct Bucket
        {
            public int KeyA;
            public int KeyB;
            public double SumAx;
            public double SumAz;
            public double SumBx;
            public double SumBz;
            public float Weight;
            public float Carried;
            public byte PurposeMask;
            public float[]? HourlyAtoB;
            public float[]? HourlyBtoA;
        }

        public static BandSet Build(
            IReadOnlyList<Journey> journeys,
            float[] pairOx,
            float[] pairOz,
            float[] pairDx,
            float[] pairDz,
            float[] pairWeight,
            bool[] carried,
            int pairCount,
            float2Like worldMin,
            int2Like zoneGrid,
            float mergeMetres,
            int maxBands)
        {
            var buckets = new List<Bucket>();
            var index = new Dictionary<long, int>();
            SumPairs(buckets, index, pairOx, pairOz, pairDx, pairDz, pairWeight, carried, pairCount, worldMin, zoneGrid);
            AddHours(buckets, index, journeys, worldMin, zoneGrid);
            return Agglomerate(buckets, mergeMetres, maxBands);
        }

        // Every door pair summed into its zone pair, with the ends kept as the weighted
        // mean of the real doors rather than the zone centres: a band between two
        // districts should start where the people are, not on a grid line.
        private static void SumPairs(
            List<Bucket> buckets, Dictionary<long, int> index,
            float[] pairOx, float[] pairOz, float[] pairDx, float[] pairDz, float[] pairWeight,
            bool[] carried, int pairCount, float2Like worldMin, int2Like zoneGrid)
        {
            for (int i = 0; i < pairCount; i++)
            {
                var origin = new float2Like(pairOx[i], pairOz[i]);
                var destination = new float2Like(pairDx[i], pairDz[i]);
                if (!TryKey(origin, destination, worldMin, zoneGrid, out int keyA, out int keyB, out bool flipped))
                {
                    continue;
                }

                int bucket = Ensure(buckets, index, keyA, keyB);
                Bucket entry = buckets[bucket];
                float weight = pairWeight[i];
                float2Like a = flipped ? destination : origin;
                float2Like b = flipped ? origin : destination;
                entry.SumAx += (double)a.x * weight;
                entry.SumAz += (double)a.y * weight;
                entry.SumBx += (double)b.x * weight;
                entry.SumBz += (double)b.y * weight;
                entry.Weight += weight;
                if (carried is not null && i < carried.Length && carried[i])
                {
                    entry.Carried += weight;
                }

                buckets[bucket] = entry;
            }
        }

        // The hours and the purposes come from the journeys themselves: a pair is
        // journeys merged by their two doors, and merging threw both away.
        private static void AddHours(
            List<Bucket> buckets, Dictionary<long, int> index,
            IReadOnlyList<Journey> journeys, float2Like worldMin, int2Like zoneGrid)
        {
            for (int i = 0; i < journeys.Count; i++)
            {
                Journey journey = journeys[i];
                if (!TryKey(journey.m_Origin, journey.m_Destination, worldMin, zoneGrid, out int keyA, out int keyB, out bool flipped)
                    || !index.TryGetValue(Key(keyA, keyB), out int bucket))
                {
                    continue;
                }

                Bucket entry = buckets[bucket];
                entry.HourlyAtoB ??= new float[Band.HoursPerDay];
                entry.HourlyBtoA ??= new float[Band.HoursPerDay];
                entry.PurposeMask |= (byte)(1 << (int)journey.m_Purpose);
                // A journey's outbound ride goes A to B unless its origin was the end
                // that sorted second, in which case it is the return direction that
                // leaves A.
                float[] outbound = flipped ? entry.HourlyBtoA : entry.HourlyAtoB;
                float[] homeward = flipped ? entry.HourlyAtoB : entry.HourlyBtoA;
                outbound[journey.m_OutHour] += journey.m_Weight;
                homeward[journey.m_BackHour] += journey.m_Weight;
                buckets[bucket] = entry;
            }
        }

        // Zone pairs, heaviest first, folded into bands whose two ends they share.
        private static BandSet Agglomerate(List<Bucket> buckets, float mergeMetres, int maxBands)
        {
            // Heaviest first, so the corridors that matter claim their position before
            // anything is folded into them. The key tie-break makes the order total.
            buckets.Sort(static (x, y) =>
            {
                int byWeight = y.Weight.CompareTo(x.Weight);
                if (byWeight != 0)
                {
                    return byWeight;
                }

                int byA = x.KeyA.CompareTo(y.KeyA);
                return byA != 0 ? byA : x.KeyB.CompareTo(y.KeyB);
            });

            var set = new BandSet();
            var bands = new List<Band>();
            float mergeSq = mergeMetres * mergeMetres;
            for (int i = 0; i < buckets.Count; i++)
            {
                Bucket entry = buckets[i];
                if (entry.Weight <= 0f)
                {
                    continue;
                }

                float ax = (float)(entry.SumAx / entry.Weight);
                float az = (float)(entry.SumAz / entry.Weight);
                float bx = (float)(entry.SumBx / entry.Weight);
                float bz = (float)(entry.SumBz / entry.Weight);

                int host = FindBand(bands, ax, az, bx, bz, mergeSq, out bool crossed);
                if (host < 0)
                {
                    if (bands.Count >= maxBands)
                    {
                        set.HiddenPairs++;
                        set.HiddenWeight += entry.Weight;
                        continue;
                    }

                    bands.Add(new Band { Ax = ax, Az = az, Bx = bx, Bz = bz });
                    host = bands.Count - 1;
                }
                else
                {
                    set.MergedPairs++;
                }

                Absorb(bands[host], in entry, ax, az, bx, bz, crossed);
            }

            // Heaviest first in the OUTPUT too, not only in the order they were
            // accepted: absorbing buckets moves weight around, so the accepted order
            // is not the final one. The panel, the log and the renderer all want the
            // city's biggest corridor first, and the tie-break keeps it total.
            bands.Sort(static (x, y) =>
            {
                int byWeight = y.Weight.CompareTo(x.Weight);
                if (byWeight != 0)
                {
                    return byWeight;
                }

                int byAx = x.Ax.CompareTo(y.Ax);
                return byAx != 0 ? byAx : x.Az.CompareTo(y.Az);
            });

            set.Bands = bands.ToArray();
            if (set.Bands.Length > 0)
            {
                set.HeaviestWeight = set.Bands[0].Weight;
            }

            return set;
        }

        // The two zones a journey runs between, ordered so that A is always the lower
        // index — one band per pair of places, whichever way round it was travelled.
        // `flipped` says the journey's own origin was the second end.
        private static bool TryKey(
            float2Like origin, float2Like destination, float2Like worldMin, int2Like zoneGrid,
            out int keyA, out int keyB, out bool flipped)
        {
            keyA = DemandZones.ZoneOf(origin, worldMin, zoneGrid);
            keyB = DemandZones.ZoneOf(destination, worldMin, zoneGrid);
            flipped = keyB < keyA;
            if (flipped)
            {
                (keyA, keyB) = (keyB, keyA);
            }

            // Off the map, or a journey that never leaves its own 256 m cell: neither
            // is a band.
            return keyA >= 0 && keyB >= 0 && keyA != keyB;
        }

        private static long Key(int keyA, int keyB) => ((long)keyA << 32) | (uint)keyB;

        private static int Ensure(List<Bucket> buckets, Dictionary<long, int> index, int keyA, int keyB)
        {
            long key = Key(keyA, keyB);
            if (index.TryGetValue(key, out int existing))
            {
                return existing;
            }

            buckets.Add(new Bucket { KeyA = keyA, KeyB = keyB });
            index.Add(key, buckets.Count - 1);
            return buckets.Count - 1;
        }

        // The first band both of whose ends lie within the merge radius of this
        // bucket's ends. `crossed` reports that it matched the other way round, which
        // decides where the bucket's two directions go.
        private static int FindBand(List<Band> bands, float ax, float az, float bx, float bz, float mergeSq, out bool crossed)
        {
            for (int i = 0; i < bands.Count; i++)
            {
                Band band = bands[i];
                if (Near(band.Ax, band.Az, ax, az, mergeSq) && Near(band.Bx, band.Bz, bx, bz, mergeSq))
                {
                    crossed = false;
                    return i;
                }

                if (Near(band.Ax, band.Az, bx, bz, mergeSq) && Near(band.Bx, band.Bz, ax, az, mergeSq))
                {
                    crossed = true;
                    return i;
                }
            }

            crossed = false;
            return -1;
        }

        private static bool Near(float x1, float z1, float x2, float z2, float mergeSq)
        {
            float dx = x1 - x2;
            float dz = z1 - z2;
            return ((dx * dx) + (dz * dz)) <= mergeSq;
        }

        private static void Absorb(Band band, in Bucket entry, float ax, float az, float bx, float bz, bool crossed)
        {
            // The band's ends follow the traffic: each absorbed bucket pulls them
            // towards itself in proportion to what it brings.
            float total = band.Weight + entry.Weight;
            if (total > 0f)
            {
                float hostShare = band.Weight / total;
                float addedShare = entry.Weight / total;
                float newAx = (band.Ax * hostShare) + ((crossed ? bx : ax) * addedShare);
                float newAz = (band.Az * hostShare) + ((crossed ? bz : az) * addedShare);
                float newBx = (band.Bx * hostShare) + ((crossed ? ax : bx) * addedShare);
                float newBz = (band.Bz * hostShare) + ((crossed ? az : bz) * addedShare);
                band.Ax = newAx;
                band.Az = newAz;
                band.Bx = newBx;
                band.Bz = newBz;
            }

            band.Weight += entry.Weight;
            band.CarriedWeight += entry.Carried;
            band.PurposeMask |= entry.PurposeMask;
            band.Pairs++;
            float[]? atoB = crossed ? entry.HourlyBtoA : entry.HourlyAtoB;
            float[]? btoA = crossed ? entry.HourlyAtoB : entry.HourlyBtoA;
            for (int h = 0; h < Band.HoursPerDay; h++)
            {
                band.HourlyAtoB[h] += atoB is null ? 0f : atoB[h];
                band.HourlyBtoA[h] += btoA is null ? 0f : btoA[h];
            }
        }
    }
}
