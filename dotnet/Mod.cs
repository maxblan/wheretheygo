using System;
using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.UI;
using Game;
using Game.Modding;
using Game.Rendering;
using Game.SceneFlow;

namespace TransitArchitect
{
    public class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger(name: $"{nameof(TransitArchitect)}.{nameof(Mod)}").SetShowsErrorsInUI(showsErrorsInUI: false);
        public static Setting? Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            if (updateSystem is null)
            {
                throw new ArgumentNullException(nameof(updateSystem));
            }

            Log.Info(nameof(OnLoad));
            DeferredLog.Sink = static (level, text) =>
            {
                switch (level)
                {
                    case DeferredLogLevel.Warn:
                        Log.Warn(text);
                        break;
                    case DeferredLogLevel.Error:
                        Log.Error(text);
                        break;
                    default:
                        Log.Info(text);
                        break;
                }
            };

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                Log.Info($"Current mod asset at {asset.path}");

                // Serve the mod's icons at coui://transitarchitect/.
                string iconsPath = Path.Combine(Path.GetDirectoryName(asset.path), "Icons");
                UIManager.defaultUISystem.AddHostLocation("transitarchitect", iconsPath, shouldWatch: false);
            }

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            GameManager.instance.localizationManager.AddSource("de-DE", new LocaleDE(Settings));

            // Zero the tuning fields so ClampAll can distinguish "loaded from file"
            // from "absent in a pre-1.1 file" and fill in mode-aware defaults.
            Settings.MarkTuningUnset();
            AssetDatabase.global.LoadSettings(nameof(TransitArchitect), Settings, new Setting(this));
            Settings.ClampAll();

            // Must run in PreCulling between OverlayInfomodeSystem (which clears the
            // terrain override overlay every frame) and TerrainRenderSystem (which
            // consumes it into the terrain material).
            updateSystem.UpdateAt<TransitArchitectSystem>(SystemUpdatePhase.PreCulling);

            // BOTH of these are ordered against a named game system, not merely put in
            // the right phase. Game.UpdateSystem keeps its own list sorted by
            // (phase, registration index) and IGNORES [UpdateBefore]/[UpdateAfter]
            // entirely (decompiled 2026-09-06: UpdateSystem.Register/Refresh/SystemData.
            // CompareTo). A mod registers after every game system, so UpdateAt alone puts
            // a system LAST in its phase — which is why the building colours never
            // appeared: ObjectColorSystem reset them to grey and BatchDataSystem had
            // already uploaded that by the time we wrote ours. The two-type overloads
            // below are the game's own answer to this, and the only one that works.

            // Route polylines go through OverlayRenderSystem, which drains and clears
            // its buffer when it updates. Drawing after that is a frame late, every
            // frame.
            updateSystem.UpdateBefore<RouteRenderer, OverlayRenderSystem>(SystemUpdatePhase.Rendering);

            // Buildings coloured by walk time to transit: written into Game.Objects.
            // Color after ObjectColorSystem has had its say and before BatchDataSystem
            // reads it (both are Rendering, in that order).
            updateSystem.UpdateAfter<BuildingAccessColorSystem, ObjectColorSystem>(SystemUpdatePhase.Rendering);

            // Bindings for the in-game control panel. The panel's own code ships as
            // TransitArchitect.mjs beside the DLL, which the game loads by
            // matching the assembly name.
            updateSystem.UpdateAt<PanelUISystem>(SystemUpdatePhase.UIUpdate);
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
