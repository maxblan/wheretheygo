namespace WhereTheyGo
{
    // The transit modes the mod reads lines of. Kept out of Setting on purpose: Setting
    // imports Colossal, Game.Modding, Game.Settings and Game.UI, and a Unity-free home is
    // what lets the per-mode table in Assumptions be linked into the offline tests.
    //
    // The name stays ModePreset: it was the persisted enum behind the old Setting.Mode,
    // and the word the codebase and both locale files use.
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
}
