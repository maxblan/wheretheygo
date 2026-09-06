using System.Collections.Generic;
using System.Globalization;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // The panel bridge: the static strings and requests the options page, the panel
    // module and the route renderer read and raise. The settings object is built before
    // the world exists, so the panel talks to the system through statics rather than an
    // instance, and the payloads are the delimited strings ui-module.md describes.
    public sealed partial class TransitArchitectSystem
    {
        // The Options page prints this verbatim, so it is built here rather than in
        // PanelPayload: the payload code is compiled by the offline harness with no
        // game assemblies, and so cannot reach the player's language. The suggestions
        // themselves are a list in the Transportation overview; this is the one line
        // that says whether there are any.
        private static string s_RouteSummary = string.Empty;

        private static string s_RouteList = string.Empty;

        private static string s_OverviewRows = string.Empty;

        private static int s_ImproveRequest = -1;

        private static string s_ImprovePlan = string.Empty;

        private static int s_ImprovedLine = -1;

        private static bool s_ImprovedRouteDrawn;

        // Which suggestion the panel is pointing at, and which one it has narrowed the
        // map down to. Both are positions in the CURRENT suggestion list, which is only
        // a safe key because both are cleared the moment a new list arrives — see
        // UpdateRouteSummary.
        private static int s_HighlightedRoute = -1;

        private static int s_SelectedRoute = -1;

        public static string RouteSummaryText => s_RouteSummary;

        // One route per line as "mode|km|stops|vehicles|colour|reachPercent", for the
        // panel to render as a colour-keyed list. A compact string avoids hand-rolling a JSON
        // writer for what is at most a dozen rows. The colour travels with the row
        // because the panel used to carry its own copy of the mode palette, kept in
        // step with the renderer's by a comment.
        public static string RouteListText => s_RouteList;

        // "N" while a finished route pass waits for the player to take it over, "" otherwise.
        private static string s_RouteUpdate = string.Empty;

        private static bool s_ApplyRouteUpdate;

        public static string RouteUpdateText => s_RouteUpdate;

        public static void RequestApplyRouteUpdate() => s_ApplyRouteUpdate = true;

        // The Options page's heat-map switch. A static request rather than a direct
        // call, because the settings object outlives the system and is edited from the
        // main menu, where there is no city and no system to talk to.
        private static int s_InfoviewRequest = -1;

        public static void RequestInfoview(bool on) => s_InfoviewRequest = on ? 1 : 0;

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

        // One row per existing line for the vanilla transport overview.
        public static string OverviewRowsText => s_OverviewRows;

        // "coveredHours|readings|windowHours" — how much observed history the verdicts
        // and the served-demand discount are actually resting on. The panel shows it
        // because a mean over twenty minutes and a mean over a full day are the same
        // number on screen and mean very different things.
        public static string DataCoverageText => s_DataCoverage;

        public static string EquityText => s_Equity;

        // The panel asks for one line's improvement plan by the line's own id — never
        // by its position, since the list is re-sorted worst-first on every refresh.
        public static void RequestImprovement(int lineId) => s_ImproveRequest = lineId;

        // The suggestion the pointer is over, drawn heavier so a glance answers "which
        // one is this row". -1 for none.
        public static void HighlightRoute(int index) => s_HighlightedRoute = index;

        // Narrows the map to one suggestion, or back to all of them. Clicking the row
        // that is already selected clears it, which is the only way back.
        public static void SelectRoute(int index) => s_SelectedRoute = s_SelectedRoute == index ? -1 : index;

        // Which suggestion is drawn alone, or -1 for all of them. The panel renders its
        // row highlight from this rather than from state of its own, so the two cannot
        // disagree about what the map is showing.
        public static int SelectedRouteIndex => s_SelectedRoute;

        internal static int HighlightedRouteIndex => s_HighlightedRoute;

        public static string ImprovePlanText => s_ImprovePlan;

        // Which line the plan belongs to, so the panel can show it against the right
        // row instead of at the bottom of a long list.
        public static int ImprovedLineIndex => s_ImprovedLine;

        // The re-traced alignment for that line, drawn on the map.
        internal SuggestedRoute? ImprovedRoute => m_ImprovedRoute;

        // Whether that alignment exists. The panel's plan carries a line saying the
        // white dashed route is on the map, and BuildImprovedRoute has three ways to
        // come back with nothing — so the panel has to be told which it got.
        public static bool ImprovedRouteDrawn => s_ImprovedRouteDrawn;

        private static string s_DataCoverage = string.Empty;

        private static string s_Equity = string.Empty;

        internal List<SuggestedRoute> SuggestedRoutes => m_Routes;

        // Whether the mod's own panel is open. Owned here rather than in the UI system
        // Opens or closes our infoview on behalf of the toolbar button. Activation
        // still goes through ToolSystem.infoview, which is what assigns the terrain
        // overlay channel our heat map is drawn into.
        public void SetInfoviewActive(bool active) => m_Infoview.SetActive(active);

        public bool IsInfoviewActive => m_Infoview.IsActive;

        // Some OTHER infoview is on screen. Route polylines and the suppression of the
        // vanilla legend both key off this rather than off IsInfoviewActive: the heat
        // map is its own toggle in the panel, and turning it off must not take the
        // routes with it, nor let the vanilla legend flash back in for the frames
        // between our infoview closing and the game unmounting its panel.
        public bool ForeignInfoviewActive => m_Infoview.ForeignActive;

        private void UpdateOverviewRows()
        {
            s_OverviewRows = PanelPayload.OverviewRows(m_LineHealth);
        }

        // Why there is nothing to suggest, in the three cases that mean different
        // things to the player: the mod has not seen the city yet, it has seen it and
        // found no journeys, or it looked and everything worth carrying is carried.
        private static string EmptyRouteSummary(int tripCount, int assignedPairs)
        {
            if (tripCount < 0)
            {
                return Loc.Text("NoCorridor", "No corridor was strong enough to suggest a line.");
            }

            if (tripCount == 0)
            {
                return Loc.Text("NoJourneys", "No journeys found yet — load a city and let it run.");
            }

            return Loc.Text(
                "NothingUnserved",
                "{0} journeys, {1} routed, but no new line would improve enough of what is still unserved.",
                tripCount.ToString(CultureInfo.InvariantCulture),
                assignedPairs.ToString(CultureInfo.InvariantCulture));
        }

        private void UpdateRouteSummary(int tripCount, int assignedPairs)
        {
            // Both are positions in the list about to be replaced. Nothing may be shown
            // as selected that is not in the list on screen, so they go with it.
            s_HighlightedRoute = -1;
            s_SelectedRoute = -1;

            if (m_Routes.Count == 0)
            {
                s_RouteList = string.Empty;
                s_RouteSummary = EmptyRouteSummary(tripCount, assignedPairs);
                return;
            }

            s_RouteList = PanelPayload.RouteRows(m_Routes, m_UnservedTravelWeight);
            // One key per grammatical number. "{0} Vorschlag/Vorschläge" is what one
            // key doing both jobs looked like on the page.
            s_RouteSummary = m_Routes.Count == 1
                ? Loc.Text("Suggestions.One", "1 suggestion ready — open the Transportation overview to see it.")
                : Loc.Text(
                    "Suggestions",
                    "{0} suggestions ready — open the Transportation overview to see them.",
                    m_Routes.Count.ToString(CultureInfo.InvariantCulture));
        }
    }
}
