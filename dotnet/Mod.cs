using System;
using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.UI;
using Game;
using Game.Modding;
using Game.Rendering;
using Game.SceneFlow;

namespace WhereTheyGo
{
    public class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger(name: $"{nameof(WhereTheyGo)}.{nameof(Mod)}").SetShowsErrorsInUI(showsErrorsInUI: false);
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

                // Serve the mod's icons at coui://wheretheygo/.
                string iconsPath = Path.Combine(Path.GetDirectoryName(asset.path), "Icons");
                UIManager.defaultUISystem.AddHostLocation("wheretheygo", iconsPath, shouldWatch: false);
            }

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            GameManager.instance.localizationManager.AddSource("de-DE", new LocaleDE(Settings));
            GameManager.instance.localizationManager.AddSource("fr-FR", new LocaleFR(Settings));
            GameManager.instance.localizationManager.AddSource("pt-BR", new LocalePTBR(Settings));
            GameManager.instance.localizationManager.AddSource("ru-RU", new LocaleRU(Settings));
            GameManager.instance.localizationManager.AddSource("zh-HANS", new LocaleZHHANS(Settings));

            AssetDatabase.global.LoadSettings(nameof(WhereTheyGo), Settings, new Setting(this));
            Settings.ClampAll();

            // Must run in PreCulling between OverlayInfomodeSystem (which clears the
            // terrain override overlay every frame) and TerrainRenderSystem (which
            // consumes it into the terrain material).
            updateSystem.UpdateAt<WhereTheyGoSystem>(SystemUpdatePhase.PreCulling);

            // BOTH of these are ordered against a named game system, not merely put in
            // the right phase. Game.UpdateSystem keeps its own list sorted by
            // (phase, registration index) and IGNORES [UpdateBefore]/[UpdateAfter]
            // entirely (decompiled 2026-09-06: UpdateSystem.Register/Refresh/SystemData.
            // CompareTo). A mod registers after every game system, so UpdateAt alone puts
            // a system LAST in its phase — which is why the building colours never
            // appeared: ObjectColorSystem reset them to grey and BatchDataSystem had
            // already uploaded that by the time we wrote ours. The two-type overloads
            // below are the game's own answer to this, and the only one that works.

            // The desire bands go through OverlayRenderSystem, which drains and clears
            // its buffer when it updates. Drawing after that is a frame late, every
            // frame.
            updateSystem.UpdateBefore<BandRenderer, OverlayRenderSystem>(SystemUpdatePhase.Rendering);

            // Buildings coloured by walk time to transit: written into Game.Objects.
            // Color after ObjectColorSystem has had its say and before BatchDataSystem
            // reads it (both are Rendering, in that order).
            updateSystem.UpdateAfter<BuildingAccessColorSystem, ObjectColorSystem>(SystemUpdatePhase.Rendering);

            // The walk-to-transit row in the game's own selected-building window. A
            // section registers itself with SelectedInfoUISystem in OnCreate, so it only
            // has to exist and be updated.
            updateSystem.UpdateAt<BuildingAccessSection>(SystemUpdatePhase.UIUpdate);

            // And the reading in the window of a line the player clicks.
            updateSystem.UpdateAt<LineInsightSection>(SystemUpdatePhase.UIUpdate);

            // Bindings for the in-game control panel. The panel's own code ships as
            // WhereTheyGo.mjs beside the DLL, which the game loads by
            // matching the assembly name.
            // Which band the pointer is over. UIUpdate because that is where the
            // answer is consumed, and because it must run after the camera has moved
            // for the frame rather than before it.
            updateSystem.UpdateAt<BandPickSystem>(SystemUpdatePhase.UIUpdate);

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
