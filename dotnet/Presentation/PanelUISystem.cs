using Colossal.UI.Binding;
using Game.UI;

namespace WhereTheyGo
{
    // What the infoview panel reads and what its controls raise.
    //
    // Everything goes across as JSON rather than as delimited strings: the panel reads
    // it by name, so a field added in the middle moves nothing, and the numbers arrive
    // as numbers so the panel can format them in the player's own locale. The game's
    // own UI does the same — a selected-object section writes through this very
    // IJsonWriter.
    public sealed partial class PanelUISystem : UISystemBase
    {
        private const string Group = "wheretheygo";

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state (OnCreate not yet run) in which nothing works anyway.
        private WhereTheyGoSystem m_OverlaySystem;
#pragma warning restore CS8618

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlaySystem = World.GetOrCreateSystemManaged<WhereTheyGoSystem>();

            AddUpdateBinding(new GetterValueBinding<bool>(Group, "infoviewActive", () =>
                m_OverlaySystem is not null && m_OverlaySystem.IsInfoviewActive));
            AddUpdateBinding(new RawValueBinding(Group, "figures", WriteFigures));
            AddUpdateBinding(new RawValueBinding(Group, "mapState", WriteMapState));
            AddUpdateBinding(new RawValueBinding(Group, "hoveredBand", WriteHoveredBand));

            AddBinding(new TriggerBinding<int>(Group, "selectHour", static hour => WhereTheyGoSystem.SelectHour(hour)));
            AddBinding(new TriggerBinding<int>(Group, "setPurposes", static mask => WhereTheyGoSystem.SetPurposeFilter(mask)));
            AddBinding(new TriggerBinding<int>(Group, "setBandThreshold", static percent => WhereTheyGoSystem.SetBandThreshold(percent)));
            AddBinding(new TriggerBinding<bool>(Group, "setHourPlay", static playing => WhereTheyGoSystem.SetHourPlay(playing)));
            // The toolbar button, which is a toggle rather than a setter: the button
            // has no state of its own, it reads `infoviewActive` back like everything
            // else in the panel does.
            AddBinding(new TriggerBinding(Group, "toggleInfoview", ToggleInfoview));
        }

        private void ToggleInfoview()
        {
            m_OverlaySystem.SetInfoviewActive(!m_OverlaySystem.IsInfoviewActive);
        }

        // The city-wide figures and what they rest on.
        private static void WriteFigures(IJsonWriter writer)
        {
            PanelFigures figures = WhereTheyGoSystem.Figures;
            writer.TypeBegin("WhereTheyGo.Figures");
            writer.PropertyName("carriedShare");
            writer.Write(figures.CarriedShare);
            writer.PropertyName("coverageShare");
            writer.Write(figures.CoverageShare);
            writer.PropertyName("coverageWalkMinutes");
            writer.Write(figures.CoverageWalkMinutes);
            // The walk to transit as four shares, and the network the whole panel is
            // read against.
            writer.PropertyName("walkClasses");
            WriteFloats(writer, figures.WalkClassShare, Coverage.WalkClassCount);
            writer.PropertyName("lineCount");
            writer.Write(figures.LineCount);
            writer.PropertyName("servedStops");
            writer.Write(figures.ServedStopCount);
            writer.TypeEnd();
        }

        // What the map is showing, and what it is showing it of. The panel's controls
        // are drawn from this rather than from state of their own, so the hour strip
        // and the map cannot drift apart.
        private void WriteMapState(IJsonWriter writer)
        {
            // One cached view, rebuilt only when a filter moves or a pass finishes.
            // This runs every frame the panel is open, and summing four hundred bands
            // over twenty-four hours here would be an expensive answer to a question
            // whose answer does not change.
            BandView view = m_OverlaySystem.CurrentBandView;
            int purposes = WhereTheyGoSystem.PurposeFilter;

            writer.TypeBegin("WhereTheyGo.MapState");
            writer.PropertyName("hour");
            writer.Write(WhereTheyGoSystem.SelectedHour);
            writer.PropertyName("purposes");
            writer.Write(purposes);
            writer.PropertyName("thresholdPercent");
            writer.Write(WhereTheyGoSystem.BandThresholdPercent);
            writer.PropertyName("thresholdMaxPercent");
            writer.Write(Assumptions.BandThresholdMaxPercent);
            writer.PropertyName("playing");
            writer.Write(WhereTheyGoSystem.PlayingHours);

            // The shape of the city's day, which is both the hour strip's picture and
            // its scale. Under the purposes in force, so switching shopping off
            // reshapes the strip rather than leaving it describing a different map.
            writer.PropertyName("hourly");
            WriteFloats(writer, view.HourlyProfile, Band.HoursPerDay);

            // And the part of each hour the network already carries, drawn inside the
            // column rather than beside it.
            writer.PropertyName("hourlyCarried");
            WriteFloats(writer, view.CarriedHourlyProfile, Band.HoursPerDay);

            // What the four switches are worth, so each carries its own weight beside
            // its name, and their sum, which is the city every other figure is read
            // against.
            writer.PropertyName("purposeWeights");
            WriteFloats(writer, view.PurposeWeights, Band.PurposeCount);
            writer.PropertyName("journeysPerDay");
            writer.Write(view.DayWeight);

            // How much of the map the threshold is currently hiding. Said out loud:
            // a map that quietly dropped a third of the city's travel reads as a map of
            // the whole city.
            writer.PropertyName("bandsShown");
            writer.Write(view.Drawn.Length);
            writer.PropertyName("bandsTotal");
            writer.Write(view.TotalCount);
            writer.PropertyName("hiddenShare");
            float total = view.ShownWeight + view.HiddenWeight;
            writer.Write(total > 0f ? view.HiddenWeight / total : 0f);

            // The legend: what each width class means, in journeys a day, and how wide
            // each is drawn so the panel can show a sample of the real thing.
            writer.PropertyName("classBreaks");
            WriteFloats(writer, view.ClassBreaks, BandView.ClassCount - 1);
            writer.PropertyName("classWidths");
            WriteFloats(writer, Assumptions.BandClassWidthsMetres, BandView.ClassCount);

            // The two colour ramps, sent rather than copied into the panel: they are
            // defined once, in the code that paints the map with them, and a second
            // copy in JavaScript is a copy that drifts.
            writer.PropertyName("bandRamp");
            WriteRamp(writer, OverlayLayer.DesireBands);
            writer.PropertyName("walkRamp");
            WriteRamp(writer, OverlayLayer.TransitAccess);
            writer.TypeEnd();
        }

        private static void WriteRamp(IJsonWriter writer, OverlayLayer layer)
        {
            OverlayLayers.ColorsOf(layer, out UnityEngine.Color low, out UnityEngine.Color medium, out UnityEngine.Color high);
            writer.ArrayBegin(3u);
            writer.Write(Hex(low));
            writer.Write(Hex(medium));
            writer.Write(Hex(high));
            writer.ArrayEnd();
        }

        private static string Hex(UnityEngine.Color colour)
        {
            return "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(colour);
        }

        // The band under the pointer, or nothing. Written as its own object rather than
        // folded into the map state: it changes on every mouse move, and the rest does
        // not.
        private void WriteHoveredBand(IJsonWriter writer)
        {
            Band? band = WhereTheyGoSystem.HoveredBand;
            if (band is null)
            {
                writer.WriteNull();
                return;
            }

            int purposes = WhereTheyGoSystem.PurposeFilter;
            writer.TypeBegin("WhereTheyGo.HoveredBand");
            writer.PropertyName("journeys");
            writer.Write(band.WeightAtHour(WhereTheyGoSystem.SelectedHour, purposes));
            writer.PropertyName("dayJourneys");
            writer.Write(band.DayWeight(purposes));
            writer.PropertyName("carriedShare");
            writer.Write(band.CarriedShare);
            writer.PropertyName("peakHour");
            writer.Write(band.PeakHour(purposes));
            writer.PropertyName("lengthMetres");
            writer.Write(band.LengthMetres);
            writer.PropertyName("purposeWeights");
            var weights = new float[Band.PurposeCount];
            for (int purpose = 0; purpose < weights.Length; purpose++)
            {
                weights[purpose] = band.DayWeight(1 << purpose);
            }

            WriteFloats(writer, weights, Band.PurposeCount);
            writer.TypeEnd();
        }

        // An array of a known length, with a row of zeroes where there is no data yet:
        // the panel draws a flat strip rather than disappearing, which is the honest
        // picture of a city that has not been measured.
        private static void WriteFloats(IJsonWriter writer, float[]? values, int length)
        {
            writer.ArrayBegin((uint)length);
            for (int i = 0; i < length; i++)
            {
                writer.Write(values is not null && i < values.Length ? values[i] : 0f);
            }

            writer.ArrayEnd();
        }
    }
}
