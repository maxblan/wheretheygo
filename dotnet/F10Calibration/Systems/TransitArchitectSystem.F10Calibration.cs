using System.Globalization;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // Ridership calibration: sampling the served stops into Calibration on a
    // cadence, fitting, and the options-page requests to apply or reset the fit.
    public sealed partial class TransitArchitectSystem
    {

        // Static bridge for the options page. The settings object is constructed
        // before the world exists, so the button properties and the read-only status
        // text talk to the system through these.
        // The fit summary, for the log. The options page used to print it; the author
        // took that line out (2026-09-06) because an R² is not a thing to ask a player
        // to judge, and the buttons it described are behind the developer switch.
        private static string s_CalibrationStatus = string.Empty;
        // The one-shot report that the game moved the overlay internals, shown above the
        // calibration status because the options page has one read-only field for both.
        private static string s_PipelineStatus = string.Empty;

        private static bool s_ApplyFitRequested;

        private static bool s_ResetCalibrationRequested;

        public static void RequestApplyFittedWeights() => s_ApplyFitRequested = true;

        public static void RequestResetCalibration() => s_ResetCalibrationRequested = true;

        private readonly Calibration m_Calibration = new Calibration();

        private float m_LastRidershipSample;

        private float m_LastRidershipSave;

        private void SampleRidership(Setting settings, float now)
        {
            if (m_RawTerms is null || now - m_LastRidershipSample < Assumptions.RidershipSampleSeconds)
            {
                return;
            }

            // Only sample while the simulation is actually running; a paused game
            // would otherwise contribute many identical observations.
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            if (simulation is not null && simulation.selectedSpeed <= 0f)
            {
                return;
            }

            m_LastRidershipSample = now;

            // Records are keyed by world position and live in a mod setting rather
            // than the save, so a different city would otherwise inherit the last
            // one's ridership at the same coordinates.
            var configuration = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
            string city = configuration?.cityName ?? string.Empty;
            if (m_Calibration.RetargetTo(city))
            {
                DeferredLog.Info(
                    $"Ridership samples discarded: they were gathered in another city, now in \"{city}\". " +
                    "Records are keyed by world position, so they cannot be carried across.");
                settings.RidershipData = m_Calibration.Serialize();
                UpdateCalibrationStatus();
            }

            m_Calibration.Sample(EntityManager, m_StopQuery, m_PrefabSystem, settings.Mode,
                SampleFeaturesAt, ExpectedWaitAt);

            if (m_Calibration.TryFit())
            {
                DeferredLog.Info(
                    $"Ridership fit: R²={(m_Calibration.RSquared).ToString("F3", CultureInfo.InvariantCulture)}, demand={(m_Calibration.FittedDemand).ToString("F2", CultureInfo.InvariantCulture)}, " +
                    $"jobs={(m_Calibration.FittedJobs).ToString("F2", CultureInfo.InvariantCulture)}, future={(m_Calibration.FittedFuture).ToString("F2", CultureInfo.InvariantCulture)} (W4 is a discount and is not fitted), " +
                    $"meanWait={(m_Calibration.MeanWaitSeconds).ToString("F0", CultureInfo.InvariantCulture)}s (the divisor in Little's law; a value in the thousands means an accumulator is being read as seconds)");
            }

            settings.RidershipData = m_Calibration.Serialize();
            UpdateCalibrationStatus();

            // Setting a property does not touch disk, so the accumulated series
            // would be lost on exit without an occasional explicit save. Collecting
            // for half an hour and losing it would be worse than the write.
            if (now - m_LastRidershipSave >= Assumptions.RidershipSaveSeconds)
            {
                m_LastRidershipSave = now;
                settings.ApplyAndSave();
            }
        }

        // The fit's regressors at a world position, under the weights the map was
        // combined with, so a fitted weight means exactly what the slider means.
        private bool SampleFeaturesAt(float2 position, float[] features)
        {
            if (m_RawTerms is null || m_IntensityGrid.x <= 0)
            {
                return false;
            }

            int2 cell = SuitabilityInputs.WorldToCell(position, m_ScoreWorldMin, Assumptions.TileSize, m_IntensityGrid);
            int index = cell.x + cell.y * m_IntensityGrid.x;
            if (index < 0 || index >= m_RawTerms.Length)
            {
                return false;
            }

            // Caps and weights come from the last combine pass rather than being
            // recomputed here: this runs once per stop per sample.
            SuitabilityScoring.CalibrationFeatures(in m_RawTerms[index], in m_Scored, features);
            return true;
        }

        private void HandleCalibrationRequests(Setting settings)
        {
            if (s_ResetCalibrationRequested)
            {
                s_ResetCalibrationRequested = false;
                m_Calibration.Clear();
                settings.RidershipData = string.Empty;
                UpdateCalibrationStatus();
                DeferredLog.Info("Ridership samples reset.");
            }

            if (!s_ApplyFitRequested)
            {
                return;
            }

            s_ApplyFitRequested = false;
            if (!m_Calibration.HasFit)
            {
                DeferredLog.Info("Apply fitted weights requested, but there is no fit yet.");
                return;
            }

            settings.W1 = m_Calibration.FittedDemand;
            settings.W2 = m_Calibration.FittedJobs;
            settings.W5 = m_Calibration.FittedFuture;
            settings.ApplyAndSave();
            ScheduleRecompute(0f);
            DeferredLog.Info("Applied fitted weights to the scoring sliders.");
        }

        private void UpdateCalibrationStatus()
        {
            s_CalibrationStatus = m_Calibration.BuildSummary();
        }
    }
}
