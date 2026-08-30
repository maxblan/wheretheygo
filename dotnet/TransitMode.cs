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
        public static float MinLengthFor(ModePreset mode)
        {
            switch (mode)
            {
                // Lowered after a run where 51 of 53 grown corridors died here and
                // nothing at all was suggested. Corridors on a real street grid come
                // out shorter than these originally assumed.
                case ModePreset.Tram: return 1200f;
                case ModePreset.Metro: return 2000f;
                case ModePreset.Train: return 4000f;
                case ModePreset.Ferry: return 1200f;
                default: return 500f;
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
