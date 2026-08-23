using System;
using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.UI;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace StationSuitabilityOverlay
{
    public class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger(name: $"{nameof(StationSuitabilityOverlay)}.{nameof(Mod)}").SetShowsErrorsInUI(showsErrorsInUI: false);
        public static Setting? Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            if (updateSystem is null)
            {
                throw new ArgumentNullException(nameof(updateSystem));
            }

            Log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                Log.Info($"Current mod asset at {asset.path}");

                // Serve the mod's icons at coui://stationsuitabilityoverlay/.
                string iconsPath = Path.Combine(Path.GetDirectoryName(asset.path), "Icons");
                UIManager.defaultUISystem.AddHostLocation("stationsuitabilityoverlay", iconsPath, shouldWatch: false);
            }

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            GameManager.instance.localizationManager.AddSource("de-DE", new LocaleDE(Settings));

            // Zero the tuning fields so ClampAll can distinguish "loaded from file"
            // from "absent in a pre-1.1 file" and fill in mode-aware defaults.
            Settings.MarkTuningUnset();
            AssetDatabase.global.LoadSettings(nameof(StationSuitabilityOverlay), Settings, new Setting(this));
            Settings.ClampAll();

            // Must run in PreCulling between OverlayInfomodeSystem (which clears the
            // terrain override overlay every frame) and TerrainRenderSystem (which
            // consumes it into the terrain material).
            updateSystem.UpdateAt<StationSuitabilityOverlaySystem>(SystemUpdatePhase.PreCulling);

            // Route polylines go through OverlayRenderSystem, which drains and
            // clears its buffer during the Rendering phase — that runs BEFORE
            // PreCulling, so drawing from the overlay system above would always be a
            // frame late. Hence a second system in the right phase.
            updateSystem.UpdateAt<SuitabilityRouteRenderer>(SystemUpdatePhase.Rendering);

            // Bindings for the in-game control panel. The panel's own code ships as
            // StationSuitabilityOverlay.mjs beside the DLL, which the game loads by
            // matching the assembly name.
            updateSystem.UpdateAt<SuitabilityPanelUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));

            if (Settings is not null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
