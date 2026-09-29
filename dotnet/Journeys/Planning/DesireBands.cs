using System;
using System.Collections.Generic;

namespace WhereTheyGo
{
    // One band on the map: everybody travelling between roughly these two places.
    //
    // A band is undirected, its two ends being just A and B, but its traffic is not,
    // so the two flows are kept apart. The departures are held per purpose AND per
    // hour, not summed: the panel's two filters are meant to compose, and a band that
    // draws its whole weight when only shopping is ticked is a wrong number on the
    // map, not a rounding.
    internal sealed class Band
    {
        public float Ax;
        public float Az;
        public float Bx;
        public float Bz;

        // Journeys a day between the two ends that transit could serve, and how many
        // of them the network already carries (JourneyRouting.MarkCarried). The share
        // of the second in the first is the band's colour. The journeys within the
        // walking horizon are not here at all: they are walks,
        // and folding them in painted a 300 m corridor warm for people on foot.
        public float Weight;
        public float CarriedWeight;

        // Of that weight, how much rides the line the player has selected
        // (RoutingProblem.TargetLine). Zero when no line is selected, which is what
        // makes the highlight disappear again.
        public float TargetWeight;

        // Departures by purpose and hour, one array per direction, indexed
        // [purpose * HoursPerDay + hour]. Every journey appears twice, its outbound
        // hour in one direction and its way home in the other, so a whole-day weight
        // is half of the sum (DayWeight).
        private readonly float[] m_AtoB = new float[PurposeCount * HoursPerDay];
        private readonly float[] m_BtoA = new float[PurposeCount * HoursPerDay];

        // How many zone pairs with journeys transit could serve were merged into this
        // band. Not shown; logged, because a band of one pair and a band of forty read
        // the same on screen.
        public int Pairs;

        public const int HoursPerDay = 24;

        // The four JourneyPurpose values. Named here rather than counted from the enum
        // because the arrays above are sized by it.
        public const int PurposeCount = 4;

        public const int AllPurposes = (1 << PurposeCount) - 1;

        public float CarriedShare => Weight > 0f ? CarriedWeight / Weight : 0f;

        public bool CarriesTarget => TargetWeight > 0f;

        public float LengthMetres => (float)Math.Sqrt(((Bx - Ax) * (Bx - Ax)) + ((Bz - Az) * (Bz - Az)));

        // Which purposes travel here at all, as bits of 1 << (int)JourneyPurpose.
        public int PurposeMask
        {
            get
            {
                int mask = 0;
                for (int purpose = 0; purpose < PurposeCount; purpose++)
                {
                    if (DayWeight(1 << purpose) > 0f)
                    {
                        mask |= 1 << purpose;
                    }
                }

                return mask;
            }
        }

        public float AtoB(int purpose, int hour) => m_AtoB[(purpose * HoursPerDay) + hour];

        public float BtoA(int purpose, int hour) => m_BtoA[(purpose * HoursPerDay) + hour];

        public void Add(int purpose, int hour, float atoB, float btoA)
        {
            m_AtoB[(purpose * HoursPerDay) + hour] += atoB;
            m_BtoA[(purpose * HoursPerDay) + hour] += btoA;
        }

        // Journeys a day between the two ends for the chosen purposes. Half the sum of
        // the departures, because each journey departs twice.
        public float DayWeight(int purposeMask)
        {
            float sum = 0f;
            for (int purpose = 0; purpose < PurposeCount; purpose++)
            {
                if ((purposeMask & (1 << purpose)) == 0)
                {
                    continue;
                }

                int start = purpose * HoursPerDay;
                for (int hour = 0; hour < HoursPerDay; hour++)
                {
                    sum += m_AtoB[start + hour] + m_BtoA[start + hour];
                }
            }

            return sum * 0.5f;
        }

        // What the band weighs at one hour, or over the whole day when no hour is
        // chosen. An hour's weight is the departures in it, not halved, because a
        // departure at seven is a journey being made at seven.
        public float WeightAtHour(int hour, int purposeMask)
        {
            if (hour is < 0 or >= HoursPerDay)
            {
                return DayWeight(purposeMask);
            }

            float sum = 0f;
            for (int purpose = 0; purpose < PurposeCount; purpose++)
            {
                if ((purposeMask & (1 << purpose)) != 0)
                {
                    sum += m_AtoB[(purpose * HoursPerDay) + hour] + m_BtoA[(purpose * HoursPerDay) + hour];
                }
            }

            return sum;
        }

        // The busiest hour of the day over both directions, for the chosen purposes.
        public int PeakHour(int purposeMask)
        {
            int peak = 0;
            float best = -1f;
            for (int hour = 0; hour < HoursPerDay; hour++)
            {
                float total = WeightAtHour(hour, purposeMask);
                if (total > best)
                {
                    best = total;
                    peak = hour;
                }
            }

            return peak;
        }

        // Which way the traffic runs at this hour: +1 from A to B, -1 from B to A, 0
        // when neither dominates or no hour is chosen. Over a whole day the two
        // directions balance by construction, since every journey is made both ways,
        // so there is a direction to show only once an hour is picked.
        public int DirectionAtHour(int hour, int purposeMask)
        {
            if (hour is < 0 or >= HoursPerDay)
            {
                return 0;
            }

            float atoB = 0f;
            float btoA = 0f;
            for (int purpose = 0; purpose < PurposeCount; purpose++)
            {
                if ((purposeMask & (1 << purpose)) != 0)
                {
                    atoB += m_AtoB[(purpose * HoursPerDay) + hour];
                    btoA += m_BtoA[(purpose * HoursPerDay) + hour];
                }
            }

            float total = atoB + btoA;
            if (total <= 0f)
            {
                return 0;
            }

            // A near-even hour has no story to tell, and an arrow would invent one.
            float lead = Math.Abs(atoB - btoA) / total;
            return lead < Assumptions.BandDirectionLead ? 0 : atoB > btoA ? 1 : -1;
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

        // Zone pairs every journey of which is a walk (JourneyRouting.MarkWalked), and
        // so were folded into no band, and the city's WHOLE walked weight in journeys a
        // day, the walked part of every mixed zone pair included. The panel says the
        // second beside the corridor count, so a map with no band for the short hops
        // does not read as a map of a city that never makes them.
        public int WalkedPairs;
        public float WalkedWeight;

        public float HeaviestWeight;

        // The city's departures hour by hour, for the chosen purposes. Summed from the
        // same bands the map draws, so the panel's hour strip and the map can never
        // tell two different stories about the same hour.
        public float[] HourlyProfile(int purposeMask)
        {
            var profile = new float[Band.HoursPerDay];
            for (int i = 0; i < Bands.Length; i++)
            {
                for (int hour = 0; hour < Band.HoursPerDay; hour++)
                {
                    profile[hour] += Bands[i].WeightAtHour(hour, purposeMask);
                }
            }

            return profile;
        }

        // The same profile, but only the departures the network already carries. The
        // routing has no clock. A door pair is carried or it is not, whatever hour it
        // travels at, so a band's carried share applies unchanged to each of its
        // hours. Summed over the SAME bands as HourlyProfile, so the two can be drawn
        // as one column and the carried part can never exceed it.
        public float[] CarriedHourlyProfile(int purposeMask)
        {
            var profile = new float[Band.HoursPerDay];
            for (int i = 0; i < Bands.Length; i++)
            {
                float carried = Bands[i].CarriedShare;
                for (int hour = 0; hour < Band.HoursPerDay; hour++)
                {
                    profile[hour] += Bands[i].WeightAtHour(hour, purposeMask) * carried;
                }
            }

            return profile;
        }

        // Journeys a day per purpose, in JourneyPurpose order. What the panel's four
        // switches are worth, so they carry their own weight beside their name.
        public float[] PurposeWeights()
        {
            var weights = new float[Band.PurposeCount];
            for (int purpose = 0; purpose < weights.Length; purpose++)
            {
                for (int i = 0; i < Bands.Length; i++)
                {
                    weights[purpose] += Bands[i].DayWeight(1 << purpose);
                }
            }

            return weights;
        }

        // The heaviest band under the filters in force. The width scale and the
        // threshold both hang off this rather than off the unfiltered maximum: with
        // three purposes switched off, scaling against the city's biggest commuter
        // corridor leaves every remaining band a hairline.
        public float HeaviestAt(int hour, int purposeMask)
        {
            float heaviest = 0f;
            for (int i = 0; i < Bands.Length; i++)
            {
                float weight = Bands[i].WeightAtHour(hour, purposeMask);
                if (weight > heaviest)
                {
                    heaviest = weight;
                }
            }

            return heaviest;
        }
    }

    // Bundling the city's journeys into the bands the map draws.
    //
    // Two levels, and both are needed. Door-to-door pairs are far too many to draw,
    // tens of thousands in a city, so they are first summed per 256 m zone pair,
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
            // The walked part of Weight. The rest, Weight - Walked, is what transit
            // could serve and what the band is built from.
            public float Walked;
            public float Target;
            // Departures by purpose and hour, as Band holds them.
            public float[]? AtoB;
            public float[]? BtoA;

            public readonly float Servable => Weight - Walked;
        }

        // The journeys as the routing left them: `pairs` holds where each door-to-door
        // pair runs and what it weighs, `routed` what the network does with it.
        public static BandSet Build(
            IReadOnlyList<Journey> journeys,
            RoutingProblem pairs,
            RoutingResult routed,
            float2Like worldMin,
            int2Like zoneGrid,
            float mergeMetres,
            int maxBands)
        {
            var buckets = new List<Bucket>();
            var index = new Dictionary<long, int>();
            SumPairs(buckets, index, pairs, routed, worldMin, zoneGrid);
            AddHours(buckets, index, journeys, worldMin, zoneGrid);
            return Agglomerate(buckets, mergeMetres, maxBands);
        }

        // Every door pair summed into its zone pair, with the ends kept as the weighted
        // mean of the real doors rather than the zone centres: a band between two
        // districts should start where the people are, not on a grid line. The doors
        // of a WALKED pair are left out of that mean, since those people are not on
        // the band; its weight is summed so the bucket knows how much of it is a walk.
        private static void SumPairs(
            List<Bucket> buckets, Dictionary<long, int> index,
            RoutingProblem pairs, RoutingResult routed, float2Like worldMin, int2Like zoneGrid)
        {
            bool[] carried = routed.Carried;
            bool[] walked = routed.Walked;
            bool[] ridesTarget = routed.RidesTarget;
            for (int i = 0; i < pairs.PairCount; i++)
            {
                var origin = new float2Like(pairs.PairOx[i], pairs.PairOz[i]);
                var destination = new float2Like(pairs.PairDx[i], pairs.PairDz[i]);
                if (!TryKey(origin, destination, worldMin, zoneGrid, out int keyA, out int keyB, out bool flipped))
                {
                    continue;
                }

                int bucket = Ensure(buckets, index, keyA, keyB);
                Bucket entry = buckets[bucket];
                float weight = pairs.PairWeight[i];
                entry.Weight += weight;
                if (walked is not null && i < walked.Length && walked[i])
                {
                    entry.Walked += weight;
                    buckets[bucket] = entry;
                    continue;
                }

                float2Like a = flipped ? destination : origin;
                float2Like b = flipped ? origin : destination;
                entry.SumAx += (double)a.x * weight;
                entry.SumAz += (double)a.y * weight;
                entry.SumBx += (double)b.x * weight;
                entry.SumBz += (double)b.y * weight;
                if (carried is not null && i < carried.Length && carried[i])
                {
                    entry.Carried += weight;
                }

                if (ridesTarget is not null && i < ridesTarget.Length && ridesTarget[i])
                {
                    entry.Target += weight;
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
                entry.AtoB ??= new float[Band.PurposeCount * Band.HoursPerDay];
                entry.BtoA ??= new float[Band.PurposeCount * Band.HoursPerDay];
                // A journey's outbound ride goes A to B unless its origin was the end
                // that sorted second, in which case it is the return direction that
                // leaves A.
                float[] outbound = flipped ? entry.BtoA : entry.AtoB;
                float[] homeward = flipped ? entry.AtoB : entry.BtoA;
                int row = (int)journey.m_Purpose * Band.HoursPerDay;
                outbound[row + journey.m_OutHour] += journey.m_Weight;
                homeward[row + journey.m_BackHour] += journey.m_Weight;
                buckets[bucket] = entry;
            }
        }

        // Zone pairs, heaviest first, folded into bands whose two ends they share.
        private static BandSet Agglomerate(List<Bucket> buckets, float mergeMetres, int maxBands)
        {
            // Heaviest first, so the corridors that matter claim their position before
            // anything is folded into them. Heaviest in what transit could serve, since
            // that is what the band will weigh. The key tie-break makes the order total.
            buckets.Sort(static (x, y) =>
            {
                int byWeight = y.Servable.CompareTo(x.Servable);
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

                // A zone pair every journey of which is a walk is not a band: drawing
                // it painted the shortest hops in the city as the network's worst
                // failures. It is counted, never hidden, and every bucket's walked part
                // goes into the city's walked total.
                set.WalkedWeight += entry.Walked;
                float servable = entry.Servable;
                if (servable <= 0f)
                {
                    set.WalkedPairs++;
                    continue;
                }

                float ax = (float)(entry.SumAx / servable);
                float az = (float)(entry.SumAz / servable);
                float bx = (float)(entry.SumBx / servable);
                float bz = (float)(entry.SumBz / servable);

                int host = FindBand(bands, ax, az, bx, bz, mergeSq, out bool crossed);
                if (host < 0)
                {
                    if (bands.Count >= maxBands)
                    {
                        set.HiddenPairs++;
                        set.HiddenWeight += servable;
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
        // index: one band per pair of places, whichever way round it was travelled.
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
            // towards itself in proportion to what it brings, which is the part of it
            // transit could serve.
            float servable = entry.Servable;
            float total = band.Weight + servable;
            if (total > 0f)
            {
                float hostShare = band.Weight / total;
                float addedShare = servable / total;
                float newAx = (band.Ax * hostShare) + ((crossed ? bx : ax) * addedShare);
                float newAz = (band.Az * hostShare) + ((crossed ? bz : az) * addedShare);
                float newBx = (band.Bx * hostShare) + ((crossed ? ax : bx) * addedShare);
                float newBz = (band.Bz * hostShare) + ((crossed ? az : bz) * addedShare);
                band.Ax = newAx;
                band.Az = newAz;
                band.Bx = newBx;
                band.Bz = newBz;
            }

            band.Weight += servable;
            band.CarriedWeight += entry.Carried;
            band.TargetWeight += entry.Target;
            band.Pairs++;
            // The hours come from the journeys, keyed by zone pair, and a door pair's
            // walked flag cannot be carried onto a single journey there, so the
            // bucket's hours are scaled to its servable share instead. An
            // approximation per zone pair: it assumes the walked
            // and the servable door pairs of one zone pair share its hour profile. What
            // it buys is a band whose hours sum to its weight, so the hour strip, the
            // widths and the colour all describe the same journeys.
            float servableShare = entry.Weight > 0f ? servable / entry.Weight : 0f;
            float[]? atoB = crossed ? entry.BtoA : entry.AtoB;
            float[]? btoA = crossed ? entry.AtoB : entry.BtoA;
            for (int purpose = 0; purpose < Band.PurposeCount; purpose++)
            {
                for (int hour = 0; hour < Band.HoursPerDay; hour++)
                {
                    int slot = (purpose * Band.HoursPerDay) + hour;
                    band.Add(
                        purpose,
                        hour,
                        atoB is null ? 0f : atoB[slot] * servableShare,
                        btoA is null ? 0f : btoA[slot] * servableShare);
                }
            }
        }
    }
}
