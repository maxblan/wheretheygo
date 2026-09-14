using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // The panel bridge: the static strings and requests the options page and the panel
    // module read and raise. The settings object is built before the world exists, so
    // the panel talks to the system through statics rather than an instance, and the
    // payloads are the delimited strings ui-module.md describes.
    public sealed partial class TransitArchitectSystem
    {
        // The Options page's heat-map switch. A static request rather than a direct
        // call, because the settings object outlives the system and is edited from the
        // main menu, where there is no city and no system to talk to.
        private static int s_InfoviewRequest = -1;

        private static string s_DataCoverage = string.Empty;

        private static string s_Equity = string.Empty;

        public static void RequestInfoview(bool on) => s_InfoviewRequest = on ? 1 : 0;

        // "coveredHours|readings|windowHours" — how much observed history the figures
        // are actually resting on. The panel shows it because a mean over twenty
        // minutes and a mean over a full day are the same number on screen and mean
        // very different things.
        public static string DataCoverageText => s_DataCoverage;

        public static string EquityText => s_Equity;

        // Opens or closes our infoview on behalf of the Options page. Activation goes
        // through ToolSystem.infoview, which is what assigns the terrain overlay
        // channel the heat map is drawn into.
        public void SetInfoviewActive(bool active) => m_Infoview.SetActive(active);

        public bool IsInfoviewActive => m_Infoview.IsActive;

        // Some OTHER infoview is on screen. The suppression of the vanilla legend keys
        // off this rather than off IsInfoviewActive, so the vanilla legend cannot flash
        // back in for the frames between our infoview closing and the game unmounting
        // its panel.
        public bool ForeignInfoviewActive => m_Infoview.ForeignActive;

        private void HandleInfoviewRequest()
        {
            if (s_InfoviewRequest < 0)
            {
                return;
            }

            bool on = s_InfoviewRequest == 1;
            s_InfoviewRequest = -1;
            SetInfoviewActive(on);
        }
    }
}
