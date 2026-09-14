namespace WhereTheyGo
{
    // The city-wide numbers the infoview panel shows, in the units they are shown in.
    //
    // Plain numbers rather than the delimited string this used to be: the panel formats
    // them in the player's own locale and unit system (the game's LocalizedNumber), and
    // a string assembled here cannot be reformatted there. It also ends a whole class of
    // bug — the panel used to index these by position, so inserting a field in the
    // middle silently moved every later one.
    internal struct PanelFigures
    {
        // How much of the city's travel the network carries, 0..1
        // (JourneyRouting.MarkCarried).
        public float CarriedShare;

        // How much of it has BOTH ends within walking distance of a served stop, 0..1,
        // and what "walking distance" is set to.
        public float CoverageShare;
        public int CoverageWalkMinutes;

        // How much observed history the line readings rest on: hours covered, how many
        // readings that was, and how long the window is. A mean over twenty minutes and
        // a mean over a full day are the same number on screen and mean very different
        // things.
        public float CoveredHours;
        public int Readings;
        public float WindowHours;

        // Shopping and leisure journeys seen so far, and over how long. The commutes
        // are read from the save and need no watching; these do.
        public int ObservedJourneys;
        public float ObservedHours;
    }
}
