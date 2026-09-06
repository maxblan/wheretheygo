using System.Globalization;
using Colossal.Localization;
using Game.SceneFlow;

namespace TransitArchitect
{
    // Text the mod writes into places the game will NOT translate for it.
    //
    // Labels and tooltips on the Options page go through the game's own locale lookup,
    // so they need nothing here. A read-only string PROPERTY does not: whatever it
    // returns is printed verbatim, which is how "5 suggested: #1 Tram 2.7km…" ended up
    // in the middle of a German options page. So those few strings resolve their own
    // key here and format the numbers themselves.
    //
    // Deliberately not in a Planning folder: it touches GameManager, and the planning
    // code is compiled by the offline harness with no game assemblies at all.
    internal static class Loc
    {
        // The mod's own keys live under this prefix in both locale files.
        private const string Prefix = "TransitArchitect.Status[";

        // The English text passed in is the fallback, so a key missing from a locale
        // degrades to English rather than to a blank field or a raw key.
        public static string Text(string key, string english)
        {
            LocalizationDictionary? dictionary = GameManager.instance?.localizationManager?.activeDictionary;
            return dictionary is not null
                && dictionary.TryGetValue(Prefix + key + "]", out string value)
                && !string.IsNullOrEmpty(value)
                    ? value
                    : english;
        }

        public static string Text(string key, string english, params object[] arguments)
        {
            return string.Format(CultureInfo.CurrentCulture, Text(key, english), arguments);
        }
    }
}
