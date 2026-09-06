using System.Globalization;

namespace TransitArchitect
{
    // The log's disagreement pass: the sanity checks (SanityChecks) with the
    // invariant each one tests, suggestion churn between refreshes, and the
    // change-gated route log.
    public sealed partial class TransitArchitectSystem
    {
        private readonly SuggestionChurn m_Churn = new SuggestionChurn();

        private void LogSanityChecks(float totalZoneWeight)
        {
            int complaints = SanityChecks.Check(
                m_LineHealth, m_Routes, totalZoneWeight, m_ServedDemand.LongestJourneyMetres(m_ZoneFlows),
                static message => DeferredLog.Warn($"  SANITY: {message}"));

            DeferredLog.Info(complaints == 0
                ? "Sanity checks: all clear."
                : $"Sanity checks: {(complaints).ToString(CultureInfo.InvariantCulture)} problem(s) above are defects in the mod, not properties of the city.");

            LogSuggestionChurn();
        }

        // How much the suggestion list moved since the last refresh, reported as a
        // number so it can be watched rather than recalled.
        private void LogSuggestionChurn()
        {
            int held = m_Churn.CountHeld(m_Routes);
            int before = m_Churn.RememberedCount;
            DeferredLog.Info(
                $"Suggestion churn: {(held).ToString(CultureInfo.InvariantCulture)} of {m_Routes.Count} suggestions " +
                $"were also suggested last refresh (which offered {(before).ToString(CultureInfo.InvariantCulture)}). " +
                "A list that turns over every refresh is advice nobody can act on.");
            m_Churn.Remember(m_Routes);
        }

        // Only when the list actually moved. LogSites is gated the same way and for the
        // same reason: a refresh every thirty seconds that re-prints an unchanged list
        // buries the one that changed.
        private void LogRoutes()
        {
            if (!m_Churn.ChangedSinceLogged(m_Routes))
            {
                return;
            }

            for (int i = 0; i < m_Routes.Count; i++)
            {
                SuggestedRoute route = m_Routes[i];
                float2Like from = route.Stops.Count > 0 ? route.Stops[0] : float2Like.Zero;
                float2Like to = route.Stops.Count > 0 ? route.Stops[route.Stops.Count - 1] : float2Like.Zero;
                DeferredLog.Info(
                    $"Route #{(i + 1).ToString(CultureInfo.InvariantCulture)}: {route.Mode}, {(route.Length / 1000f).ToString("F2", CultureInfo.InvariantCulture)} km, {(route.Stops.Count).ToString(CultureInfo.InvariantCulture)} stops, " +
                    $"corridorFlow={(route.CapturedFlow).ToString("F0", CultureInfo.InvariantCulture)}, " +
                    $"enabledDemand={(route.EnabledDemand).ToString("F0", CultureInfo.InvariantCulture)}, {(route.Vehicles).ToString(CultureInfo.InvariantCulture)} vehicles, " +
                    $"{(route.BentThroughHub ? "bent through an interchange, " : string.Empty)}" +
                    $"({((int)from.x).ToString(CultureInfo.InvariantCulture)},{((int)from.y).ToString(CultureInfo.InvariantCulture)}) -> ({((int)to.x).ToString(CultureInfo.InvariantCulture)},{((int)to.y).ToString(CultureInfo.InvariantCulture)})");
            }
        }
    }
}
