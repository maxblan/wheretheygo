using System;
using System.Globalization;

namespace WhereTheyGo
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
        // Land lattice for TRAINS, cheap along existing train track (TrackTypes.Train).
        Rail = 1,
        // Open water, for ferries.
        Water = 2,
        // Land lattice for METROS, cheap along existing metro track (TrackTypes.Subway).
        // A metro cannot run on train track nor a train on metro track, so each has its
        // own lattice and its own bonus (register A4.5: the same bonus, each for its
        // own kind of track).
        Metro = 3,
    }

    // What a suggested route is grown to maximise.
    public enum RouteGoal
    {
        Ridership = 0,
        Balanced = 1,
        Coverage = 2,
    }

    // What each mode is like: how fast it runs, how far apart its stops sit, how much
    // demand it needs to be worth building, and what colour it is drawn in —
    // everything true of a mode, in one place (see the note on ModePreset). The mode
    // choice itself, the capacity ladder, is the F6 half of this class.
    //
    // Deliberately a switch per property rather than a table of records: adding a mode
    // is then one visible edit per property, and the compiler cannot silently fill in
    // a default for the one that was forgotten.
    internal static partial class TransitModes
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

        public static float CapacityWeight(float capacity, float busCapacity)
        {
            return busCapacity > 0f && capacity > 0f ? capacity / busCapacity : 0f;
        }

        // Modes a given alignment can carry, best capacity first. Choosing among these
        // is what lets an under-used road corridor come back as something feasible
        // instead of being dropped for not justifying a tram.
        //
        // Streets carry no mode-specific cost, so a road corridor is genuinely open to
        // either mode that can drive it. A LATTICE alignment is not: see
        // ModesForTraced.
        // The modes a network can carry, smallest vehicle first: the ladder ChooseMode
        // climbs until the riders fit. The lattices carry one mode each; the ladder
        // ACROSS networks — a rail alignment is only offered once the street modes
        // would be overloaded — lives in the system's ResolveCandidate.
        public static ModePreset[] ModesFor(RouteNetwork network)
        {
            switch (network)
            {
                case RouteNetwork.Rail:
                    return new[] { ModePreset.Train };
                case RouteNetwork.Metro:
                    return new[] { ModePreset.Metro };
                case RouteNetwork.Water:
                    return new[] { ModePreset.Ferry };
                default:
                    return new[] { ModePreset.Bus, ModePreset.Tram };
            }
        }

        // The network a mode's lines run on — the inverse of ModesFor, so an existing
        // line climbs the same ladder a suggestion does (register A6.x for both).
        public static RouteNetwork NetworkOf(ModePreset mode)
        {
            switch (mode)
            {
                case ModePreset.Train: return RouteNetwork.Rail;
                case ModePreset.Metro: return RouteNetwork.Metro;
                case ModePreset.Ferry: return RouteNetwork.Water;
                default: return RouteNetwork.Road;
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
                longest = Math.Max(longest, Assumptions.MaxRideSecondsFor(modes[i]) * Assumptions.CruiseSpeedFor(modes[i]));
            }

            return longest;
        }

        // Ride time of a line: driving plus one stop delay per intermediate stop.
        public static float RideSeconds(float lengthMetres, int stops, float cruiseSpeed, float delayPerStop)
        {
            float driving = lengthMetres / Math.Max(1f, cruiseSpeed);
            return driving + (Math.Max(0, stops - 2) * delayPerStop);
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
