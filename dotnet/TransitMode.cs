using System;
using System.Globalization;

namespace StationSuitabilityOverlay
{
    // The transit modes this mod can plan, and what a suggested line is grown for.
    //
    // These live here rather than nested inside Setting because everything that is
    // true of a mode is keyed on them, and Setting imports Colossal, Game.Modding,
    // Game.Settings and Game.UI. Nested, there was no Unity-free file that could own
    // a per-mode table, so five of them grew separate copies in separate files — two
    // byte-for-byte identical, and a sixth in the panel's JavaScript.
    //
    // Moving them was safe on both persistence and localization: the settings file
    // keys on the PROPERTY name and writes the enum's member name ("Mode": "Bus"),
    // and Setting.GetEnumValueLocaleID composes its key from typeof(T).Name, which is
    // "ModePreset" whether the type is nested or not.
    //
    // The name stays ModePreset: it is the persisted enum behind Setting.Mode and the
    // word the whole codebase and both locale files already use.
    public enum ModePreset
    {
        // Bus and Metro keep their original numeric values so settings files
        // written by earlier versions still resolve to the same mode.
        Bus = 0,
        Metro = 1,
        Tram = 2,
        Train = 3,
        Ferry = 4,
    }

    // Which network a mode's routes are traced over. Here rather than beside the
    // lattice that builds one, because which alignment a mode may use is a fact about
    // the mode.
    internal enum RouteNetwork
    {
        // Streets: buses and trams have to use the road network.
        Road = 0,
        // Land lattice blended with existing rail. Trains prefer to reuse track that
        // already exists and only strike out on new alignment when they must; metros
        // are the other way round, since a tunnel goes wherever it likes.
        Rail = 1,
        // Open water, for ferries.
        Water = 2,
    }

    // Why no mode on an alignment was justified. One `false` from ChooseMode used to
    // cover both, and the log always blamed demand: four corridors carrying 965-1488
    // against a tram floor of 938 were reported as "below every floor" when every one
    // of them had failed on LENGTH. A wrong reason in the log sends the next person
    // diagnosing this at the wrong half of the pipeline.
    internal enum ModeRejection
    {
        None = 0,
        DemandTooLow = 1,
        TooShort = 2,
    }

    // What is known about a grown corridor when its mode is chosen.
    //
    // Passed as one value because the decision consumes them together: a mode is
    // justified by evidence, and which evidence counts differs by mode.
    internal readonly struct CorridorEvidence
    {
        public CorridorEvidence(float flow, float length, float enabledDemandShare, float trackShare)
        {
            Flow = flow;
            Length = length;
            EnabledDemandShare = enabledDemandShare;
            TrackShare = trackShare;
        }

        // Length-weighted mean demand per network edge along the corridor.
        public float Flow { get; }

        public float Length { get; }

        // Share of the city's whole travel weight this line would put on the network,
        // counting journeys it forms any leg of. Zero before transfer scoring has run.
        public float EnabledDemandShare { get; }

        // Share of the corridor that runs along rail that already exists. Only
        // meaningful on the rail lattice; zero everywhere else.
        public float TrackShare { get; }
    }

    // What a suggested route is grown to maximise.
    public enum RouteGoal
    {
        Ridership = 0,
        Balanced = 1,
        Coverage = 2,
    }

    // What each mode is like: how fast it runs, how far apart its stops sit, how much
    // demand it needs to be worth building, and what colour it is drawn in.
    //
    // Deliberately a switch per property rather than a table of records: adding a mode
    // is then one visible edit per property, and the compiler cannot silently fill in
    // a default for the one that was forgotten.
    internal static class TransitModes
    {
        // Every mode the mod can plan, ORDERED BY ENUM VALUE.
        //
        // The order is load-bearing: the panel indexes this list with the value the
        // mode binding carries and sends the index back through setMode. Its
        // hand-copied JavaScript equivalent had Bus, Tram, Metro — so picking Tram
        // sent 1, which is Metro, and picking Metro sent 2, which is Tram.
        public static ModePreset[] All =>
            new[]
            {
                ModePreset.Bus,
                ModePreset.Metro,
                ModePreset.Tram,
                ModePreset.Train,
                ModePreset.Ferry,
            };

        public static RouteGoal[] AllGoals =>
            new[] { RouteGoal.Ridership, RouteGoal.Balanced, RouteGoal.Coverage };

        // Typical running speed in metres per second, used both to estimate a fleet
        // for a suggested line and to price a ride on one when no pathfound duration
        // is available.
        // A mode as one bit, so "which modes meet here" is a set rather than a list.
        // Every per-mode fact lives in this file; a second copy of this mapping beside
        // the interchange code is exactly how five separate mode tables grew before.
        public static int ModeBit(ModePreset mode)
        {
            return 1 << (int)mode;
        }

        // How many modes a mask holds. Used to rank one interchange against another:
        // a place where three modes meet is a hub, one where two meet is a change.
        public static int ModeCount(int modeMask)
        {
            int count = 0;
            while (modeMask != 0)
            {
                modeMask &= modeMask - 1;
                count++;
            }

            return count;
        }

        public static float CruiseSpeedFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 12f;
                case ModePreset.Metro: return 18f;
                case ModePreset.Train: return 28f;
                case ModePreset.Ferry: return 10f;
                default: return 9f;
            }
        }

        // How far apart stops belong, in metres. Used to place them on a suggestion
        // and to judge whether an existing line makes too many.
        public static float StopSpacingFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 450f;
                case ModePreset.Metro: return 800f;
                case ModePreset.Train: return 2000f;
                case ModePreset.Ferry: return 1200f;
                default: return 350f;
            }
        }

        // Below these lengths the mode is not worth building, whatever the demand.
        //
        // Roughly five calls at the mode's own stop spacing: a line that stops fewer
        // times than that is a pair of stops, not a service. These had been lowered
        // until almost anything qualified, and the result was a 659 m bus line looping
        // around one residential block and a 4 km "train" with three stops in a city
        // whose real trains run 25 and 33 km.
        //
        // The ferry is the exception, and deliberately: a crossing is two stops by
        // nature, and every ferry in a real city here is a 1.7-2.7 km hop.
        public static float MinLengthFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 1800f;
                case ModePreset.Metro: return 3200f;
                case ModePreset.Train: return 10000f;
                case ModePreset.Ferry: return 1200f;
                default: return 1400f;
            }
        }

        // Past this length a line cannot hold a headway and should be split.
        public static float MaxSensibleLength(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 12000f;
                case ModePreset.Metro: return 20000f;
                case ModePreset.Train: return 60000f;
                case ModePreset.Ferry: return 20000f;
                default: return 9000f;
            }
        }

        // The headway a healthy line of this mode should be able to hold, in seconds.
        public static float TargetHeadwayFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 240f;
                case ModePreset.Metro: return 200f;
                case ModePreset.Train: return 480f;
                case ModePreset.Ferry: return 600f;
                default: return 300f;
            }
        }

        // Capacity floors, expressed as a multiple of a network's own mean edge flow
        // so they hold on any size of city: a metro built for 361 trips while a tram
        // carries 1633 is the wrong way round. A bus has no floor, which is what makes
        // every road corridor yield a usable suggestion.
        public static float MinFlowMultipleFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 1.5f;
                case ModePreset.Metro: return 5f;
                case ModePreset.Train: return 8f;
                case ModePreset.Ferry: return 1f;
                default: return 0f;
            }
        }

        // Modes a given alignment can carry, best capacity first. Choosing among these
        // is what lets an under-used rail corridor come back as something feasible
        // instead of being dropped for not justifying a metro.
        public static ModePreset[] ModesFor(RouteNetwork network)
        {
            switch (network)
            {
                case RouteNetwork.Rail:
                    return new[] { ModePreset.Train, ModePreset.Metro };
                case RouteNetwork.Water:
                    return new[] { ModePreset.Ferry };
                default:
                    // Streets can host either, and a bus has no capacity floor, so a
                    // road corridor always yields a usable suggestion.
                    return new[] { ModePreset.Tram, ModePreset.Bus };
            }
        }

        // Minimum length of the least demanding mode this network can host.
        public static float ShortestModeLength(RouteNetwork network)
        {
            ModePreset[] options = ModesFor(network);
            float shortest = float.MaxValue;
            for (int i = 0; i < options.Length; i++)
            {
                shortest = Math.Min(shortest, MinLengthFor(options[i]));
            }

            return shortest;
        }

        // Share of the UNSERVED demand a line must unlock before its REACH alone can
        // justify the mode, with no demand floor met on any single edge.
        //
        // Against the demand the existing network has already taken its share of, not
        // against every journey in the city: the numerator is credited out of the
        // discounted pool, and measuring it against the undiscounted total compared two
        // different quantities. These bars are stated in the honest units and chosen to
        // sit where the old ones effectively did.
        //
        // Both RAIL modes have one. A rail alignment is justified by the places it
        // connects, and a corridor spanning a city necessarily spreads its flow thin
        // over every one of its edges — judging it on flow alone made it unreachable.
        // On a real city the rail lattice's mean edge flow was 224, so the train's 8x
        // multiple asked for 1796 while the best rail corridor anywhere carried 264.
        //
        // A road mode keeps flow as its only evidence. Density is the right test for a
        // tram or a bus, and letting reach speak for them would put a tram down an
        // empty street because the line happens to touch a busy interchange.
        //
        // The train's bar is the higher of the two: it is much the larger commitment.
        // Giving reach to the train ALONE was worse than not having it — ModesFor tries
        // the biggest mode first, so every rail corridor over the train's minimum
        // length became a train whatever its scale, and a 4 km three-stop line was
        // suggested as heavy rail. With the metro able to clear the same kind of bar,
        // LENGTH is what separates them, which is what it should have been all along.
        public static float MinEnabledDemandShareFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Train: return 0.04f;
                case ModePreset.Metro: return 0.02f;
                default: return 0f;
            }
        }

        // How much of the corridor has to run along existing track before it counts as
        // extending the rail network rather than laying a new one.
        public const float MostlyOnTrackShare = 0.6f;

        // What that earns: a train following track the city already has is an
        // extension, which is cheaper to build and likelier to be wanted, so it clears
        // the reach bar on half the demand. A preference, not a gate — a genuinely
        // good alignment across fresh ground is still allowed to justify itself.
        public const float OnTrackReachRelief = 0.5f;

        // Highest-capacity mode whose evidence and minimum length this corridor
        // actually meets. Returns false when nothing on this alignment is justified,
        // and says which test did the rejecting.
        public static bool ChooseMode(
            RouteNetwork network,
            CorridorEvidence evidence,
            float referenceFlow,
            out ModePreset mode,
            out ModeRejection rejection)
        {
            ModePreset[] options = ModesFor(network);
            bool metSomeBar = false;
            for (int i = 0; i < options.Length; i++)
            {
                ModePreset option = options[i];
                bool byFlow = evidence.Flow >= referenceFlow * MinFlowMultipleFor(option);

                float reachBar = MinEnabledDemandShareFor(option);
                if (reachBar > 0f && evidence.TrackShare >= MostlyOnTrackShare)
                {
                    reachBar *= OnTrackReachRelief;
                }

                bool byReach = reachBar > 0f && evidence.EnabledDemandShare >= reachBar;

                metSomeBar |= byFlow || byReach;
                if ((byFlow || byReach) && evidence.Length >= MinLengthFor(option))
                {
                    mode = option;
                    rejection = ModeRejection.None;
                    return true;
                }
            }

            // A corridor that cleared some mode's bar and still found nothing to run
            // was rejected for being short, not for carrying nobody.
            rejection = metSomeBar ? ModeRejection.TooShort : ModeRejection.DemandTooLow;
            mode = options[options.Length - 1];
            return false;
        }

        // The mode a struggling line should grow into. Ordered by capacity, so a bus
        // becomes a tram before it becomes a metro — suggesting the largest possible
        // jump would rarely be actionable.
        public static ModePreset NextModeUp(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Bus: return ModePreset.Tram;
                case ModePreset.Tram: return ModePreset.Metro;
                case ModePreset.Metro: return ModePreset.Train;
                default: return mode;
            }
        }

        public static ModePreset NextModeDown(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Train: return ModePreset.Metro;
                case ModePreset.Metro: return ModePreset.Tram;
                case ModePreset.Tram: return ModePreset.Bus;
                default: return mode;
            }
        }

        // How a line of this mode is drawn, roughly following transit-map convention.
        // Colour by MODE rather than by rank: rank is already visible in the ordered
        // list, whereas which vehicle a line is for cannot be read off the map any
        // other way.
        //
        // Channels are 0..255 so the one definition serves both the world-space
        // renderer and the panel, which used to carry its own copy in JavaScript kept
        // in step by a comment.
        public static void ColorFor(ModePreset mode, out byte red, out byte green, out byte blue)
        {
            switch (mode)
            {
                case ModePreset.Bus: red = 38; green = 140; blue = 255; break;
                case ModePreset.Tram: red = 255; green = 115; blue = 26; break;
                case ModePreset.Metro: red = 153; green = 64; blue = 242; break;
                case ModePreset.Train: red = 26; green = 191; blue = 89; break;
                case ModePreset.Ferry: red = 26; green = 217; blue = 242; break;
                default: red = 230; green = 230; blue = 230; break;
            }
        }

        // The same colour as the CSS the panel needs, so the panel is handed its
        // swatch rather than deriving one from a table of its own.
        public static string ColorCssFor(ModePreset mode)
        {
            ColorFor(mode, out byte red, out byte green, out byte blue);
            return "rgb("
                + red.ToString(CultureInfo.InvariantCulture) + ", "
                + green.ToString(CultureInfo.InvariantCulture) + ", "
                + blue.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
