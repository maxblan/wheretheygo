namespace WhereTheyGo
{
    // The city-wide numbers the infoview panel shows, in the units they are shown in.
    //
    // Plain numbers rather than the delimited string this used to be: the panel formats
    // them in the player's own locale and unit system (the game's LocalizedNumber), and
    // a string assembled here cannot be reformatted there. It also ends a whole class of
    // bug: the panel used to index these by position, so inserting a field in the
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

        // The walk from a journey's start to the nearest served stop, in four classes
        // as shares that sum to 1 (Coverage.WalkClassOf). The figure above says how
        // much of the city is within the horizon; this says what the rest of it walks.
        public float[] WalkClassShare;

        // What everything else is measured against: the lines, and the stops they
        // actually call at. Without them a player reads "5 294 journeys" with nothing
        // to read it against. The city's journeys a day sit on the map state instead,
        // beside the purpose weights they are the sum of.
        public int LineCount;
        public int ServedStopCount;
    }
}
