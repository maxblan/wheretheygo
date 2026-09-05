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

    // What a suggested route is grown to maximise.
    // What the game's own prefabs say about one mode's vehicles and lines: seats per
    // vehicle (carriages included), the line prefab's default interval and stop
    // duration, and the vehicle's acceleration and braking. Zero where no prefab of
    // that mode is loaded; the readers fall back to the tables below and say so.
    internal sealed class ModeFacts
    {
        public float Capacity;
        public float HeadwaySeconds;
        public float StopDurationSeconds;
        public float Acceleration;
        public float Braking;
    }

    internal readonly struct FleetFacts
    {
        private readonly ModeFacts[] m_ByMode;

        public FleetFacts(ModeFacts[] byMode)
        {
            m_ByMode = byMode;
        }

        private ModeFacts? Of(ModePreset mode)
        {
            int index = (int)mode;
            return m_ByMode is not null && index >= 0 && index < m_ByMode.Length ? m_ByMode[index] : null;
        }

        public float CapacityFor(ModePreset mode)
        {
            return Of(mode)?.Capacity ?? 0f;
        }

        // The planning headway of a suggested line: the line prefab's default vehicle
        // interval (register A5.5, A6.x), the table when the prefab is not loaded.
        public float HeadwayFor(ModePreset mode)
        {
            float headway = Of(mode)?.HeadwaySeconds ?? 0f;
            return headway > 0f ? headway : TransitModes.TargetHeadwayFor(mode);
        }

        // Vanilla's wait model: half the interval.
        public float ExpectedWaitFor(ModePreset mode)
        {
            return HeadwayFor(mode) * 0.5f;
        }

        public float StopDurationFor(ModePreset mode)
        {
            float duration = Of(mode)?.StopDurationSeconds ?? 0f;
            return duration > 0f ? duration : TransitModes.DefaultStopDurationSeconds;
        }

        // What one intermediate stop costs everyone riding through it: the dwell plus
        // the time lost braking from and accelerating back to cruise speed. Braking
        // from v at b covers v²/2b in v/b seconds, which at cruise would have taken
        // v/2b — so the loss is v/2b, and the same again for the acceleration.
        public float DelayPerStopSeconds(ModePreset mode)
        {
            ModeFacts? facts = Of(mode);
            float speed = TransitModes.CruiseSpeedFor(mode);
            float acceleration = facts is not null && facts.Acceleration > 0f ? facts.Acceleration : TransitModes.DefaultAcceleration;
            float braking = facts is not null && facts.Braking > 0f ? facts.Braking : TransitModes.DefaultAcceleration;
            return StopDurationFor(mode) + (speed / (2f * acceleration)) + (speed / (2f * braking));
        }
    }

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

        // Walking-time horizons of the access model (register A1.1/A1.2, decided
        // 2026-09-05). Catchment: how long a rider walks to a stop of the mode —
        // TCQSM's 400 m bus / 800 m rail standard read as time, stretched to the
        // measured 85th percentiles (El-Geneidy et al. 2014: bus 484 m, metro 873 m,
        // commuter rail 1 259 m at 1.2 m/s ≈ 6 / 11 / 16 min); ferry like metro, no
        // source of its own. Access: the straight-line walk from a door to the
        // pavement network beyond which a point is treated as off-network. Transfer:
        // how long a rider walks to change vehicle (A1.11).
        public const int AccessWalkMs = 120_000;
        public const int TransferWalkMs = 180_000;

        // The distinct catchment horizons over all modes, ascending: one access pass
        // computes every class at once so the map (one mode) and stop placement (any
        // mode) read the same numbers. Pinned by a test against CatchmentMs.
        public static readonly int[] CatchmentClassesMs = { 360_000, 660_000, 960_000 };

        public static int CatchmentMs(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Metro: return 660_000;
                case ModePreset.Train: return 960_000;
                case ModePreset.Ferry: return 660_000;
                default: return 360_000;
            }
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

        // What a stop of another mode is worth as a transfer partner: its vehicle's
        // trunk capacity relative to a bus, read from the loaded prefabs
        // (assumptions register A1.10 — this replaced a hand-typed table of 1 / 1.2 /
        // 1.5 / 2.5 / 3). Zero when either capacity is unknown: a mode the save has
        // no vehicle for is not a transfer partner, and the caller logs it.
        public static float CapacityWeight(float capacity, float busCapacity)
        {
            return busCapacity > 0f && capacity > 0f ? capacity / busCapacity : 0f;
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

        // Modes a given alignment can carry, best capacity first. Choosing among these
        // is what lets an under-used road corridor come back as something feasible
        // instead of being dropped for not justifying a tram.
        //
        // Streets carry no mode-specific cost, so a road corridor is genuinely open to
        // either mode that can drive it. A LATTICE alignment is not: see
        // ModesForTraced.
        // The modes a network can carry, smallest vehicle first: the ladder ChooseMode
        // climbs until the riders fit.
        public static ModePreset[] ModesFor(RouteNetwork network)
        {
            switch (network)
            {
                case RouteNetwork.Rail:
                    return new[] { ModePreset.Metro, ModePreset.Train };
                case RouteNetwork.Water:
                    return new[] { ModePreset.Ferry };
                default:
                    return new[] { ModePreset.Bus, ModePreset.Tram };
            }
        }

        public const float RidesPerJourney = 2f;

        // The mode a struggling line should grow into. Ordered by capacity, so a bus
        // becomes a tram before it becomes a metro — suggesting the largest possible
        // jump would rarely be actionable.
        // Fallbacks for prefab facts the save does not carry (A5.5: the game's own
        // values are read at run time and logged; these only stand in for a missing
        // vehicle or line prefab).
        public const float DefaultStopDurationSeconds = 15f;
        public const float DefaultAcceleration = 1.5f;

        // A suggested line has at least this many stops: two stops are a shuttle, not a
        // service (register A4.6/A6.1, 2026-09-05).
        public const int MinStops = 3;

        // End-to-end ride time a line of this mode may ask of its riders, replacing the
        // length floors and ceilings (A4.6/A6.1): Bus 30, Tram 35, Metro 30, Train 60,
        // Ferry 45 minutes.
        public static float MaxRideSecondsFor(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Tram: return 35f * 60f;
                case ModePreset.Metro: return 30f * 60f;
                case ModePreset.Train: return 60f * 60f;
                case ModePreset.Ferry: return 45f * 60f;
                default: return 30f * 60f;
            }
        }

        // The longest alignment a mode's ride limit can hold at cruise speed with no
        // stops — the growth and trace bound; the real limit is checked with stops.
        public static float MaxAlignmentMetresFor(RouteNetwork network)
        {
            ModePreset[] modes = ModesFor(network);
            float longest = 0f;
            for (int i = 0; i < modes.Length; i++)
            {
                longest = Math.Max(longest, MaxRideSecondsFor(modes[i]) * CruiseSpeedFor(modes[i]));
            }

            return longest;
        }

        // Ride time of a line: driving plus one stop delay per intermediate stop.
        public static float RideSeconds(float lengthMetres, int stops, float cruiseSpeed, float delayPerStop)
        {
            float driving = lengthMetres / Math.Max(1f, cruiseSpeed);
            return driving + (Math.Max(0, stops - 2) * delayPerStop);
        }

        // Past this share of its seats a mode is overloaded and the next one up is
        // wanted (A6.x: the game's own capacities decide the mode).
        public const float MaxPlannedUtilisation = 1f;

        // Picks the smallest mode the network can carry whose vehicles are not
        // overloaded by the riders at the mode's own headway; the largest when every
        // mode is. Whether the riders also reach the utilisation FLOOR is the set
        // selection's question, asked on the set's riders — a feeder alone rarely
        // fills anything and still belongs in the set beside its trunk. Returns false
        // only when no mode on the network has a vehicle installed.
        public static bool ChooseMode(
            RouteNetwork network,
            float ridersPerDay,
            FleetFacts facts,
            out ModePreset mode,
            out float utilisation)
        {
            ModePreset[] ladder = ModesFor(network);
            mode = ladder[0];
            utilisation = 0f;
            bool anyVehicle = false;
            for (int i = 0; i < ladder.Length; i++)
            {
                ModePreset option = ladder[i];
                float capacity = facts.CapacityFor(option);
                if (capacity <= 0f)
                {
                    continue;
                }

                anyVehicle = true;
                mode = option;
                utilisation = SuitabilityEquity.Utilisation(ridersPerDay, facts.HeadwayFor(option), capacity);
                if (utilisation <= MaxPlannedUtilisation)
                {
                    return true;
                }
            }

            return anyVehicle;
        }

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
