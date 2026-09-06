using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;
using System.Diagnostics.CodeAnalysis;

namespace TransitArchitect
{
    // The heat map's presentation in the game: the infoview and infomode prefabs, the
    // repair of the game's infoview buffer, the undo of vanilla auto-activation, the
    // terrain channel each active layer landed in, and the reflection into
    // OverlayInfomodeSystem's terrain texture that paints the intensities the F1 pass
    // produced. Owns no scoring state: the intensities come in with every frame.
    //
    // The three queries are created by the overlay system (GetEntityQuery), so they
    // stay registered with that system's dependencies exactly as before.
    internal sealed class SuitabilityInfoview
    {
        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_PrefabSystem;
        private readonly ToolSystem m_ToolSystem;
        private readonly OverlayInfomodeSystem m_OverlayInfomodeSystem;
        private readonly EntityQuery m_ActiveInfomodeQuery;
        private readonly EntityQuery m_PlaceableInfoviewQuery;
        private readonly EntityQuery m_PlaceableInfoviewChangedQuery;
        // Where the one-shot "this game version moved the internals" report goes: the
        // options page shows it beside the calibration status.
        private readonly Action<string> m_OnPipelineFault;

        public SuitabilityInfoview(
            EntityManager entityManager,
            PrefabSystem prefabSystem,
            ToolSystem toolSystem,
            OverlayInfomodeSystem overlayInfomodeSystem,
            EntityQuery activeInfomodeQuery,
            EntityQuery placeableInfoviewQuery,
            EntityQuery placeableInfoviewChangedQuery,
            Action<string> onPipelineFault)
        {
            m_EntityManager = entityManager;
            m_PrefabSystem = prefabSystem;
            m_ToolSystem = toolSystem;
            m_OverlayInfomodeSystem = overlayInfomodeSystem;
            m_ActiveInfomodeQuery = activeInfomodeQuery;
            m_PlaceableInfoviewQuery = placeableInfoviewQuery;
            m_PlaceableInfoviewChangedQuery = placeableInfoviewChangedQuery;
            m_OnPipelineFault = onPipelineFault;
        }

        // Whether a layer is registered as an infomode and can therefore be drawn.
        // SuitabilityLayers.All decides which are; EnsurePrefabs registers exactly those.
        public bool IsRegistered(SuitabilityLayer layer) => m_LayerPrefabs.ContainsKey(layer);

        // Any layer's bytes may have changed: the interleaved buffer is rebuilt on the
        // next frame even if the active set is identical.
        public void InvalidateExpandedCache()
        {
            m_ExpandedSignature = -1;
        }

        // Leaving a city drops the caches that belong to it.
        public void Release()
        {
            m_ExpandedCache = null;
            m_VanillaPlaceableInfoviews = null;
        }

        // Opens or closes our infoview on behalf of the toolbar button. Activation
        // still goes through ToolSystem.infoview, which is what assigns the terrain
        // overlay channel our heat map is drawn into.
        public void SetActive(bool active)
        {
            if (m_InfoviewPrefab is null)
            {
                return;
            }

            if (active)
            {
                m_ToolSystem.infoview = m_InfoviewPrefab;
                bool took = m_ToolSystem.activeInfoview == m_InfoviewPrefab;
                DeferredLog.Info(
                    $"Infoview activation requested: activeInfoview matches={took}. " +
                    "If this is false the view is not registered as valid and the heat map will not draw.");
            }
            else if (m_ToolSystem.activeInfoview == m_InfoviewPrefab)
            {
                m_ToolSystem.infoview = null;
                DeferredLog.Info("Infoview deactivated.");
            }
        }

        public bool IsActive =>
            m_InfoviewPrefab is not null && m_ToolSystem.activeInfoview == m_InfoviewPrefab;

        // Some OTHER infoview is on screen. Route polylines and the suppression of the
        // vanilla legend both key off this rather than off IsInfoviewActive: the heat
        // map is its own toggle in the panel, and turning it off must not take the
        // routes with it, nor let the vanilla legend flash back in for the frames
        // between our infoview closing and the game unmounting its panel.
        public bool ForeignActive =>
            m_ToolSystem.activeInfoview is not null
            && m_ToolSystem.activeInfoview != m_InfoviewPrefab;

        private const float PlaceableReverifySeconds = 60f;

        private const int InfomodePriority = 200;






        // Registered layer prefabs, and the mapping from the infomode entity the
        // game activates back to the layer it draws.
        private readonly Dictionary<SuitabilityLayer, InfomodeBasePrefab> m_LayerPrefabs =
            new Dictionary<SuitabilityLayer, InfomodeBasePrefab>();

        private readonly Dictionary<Entity, SuitabilityLayer> m_InfomodeLayers = new Dictionary<Entity, SuitabilityLayer>();

        private InfoviewPrefab? m_InfoviewPrefab;

        private byte[]? m_ExpandedCache;

        private long m_ExpandedSignature = -1;

        // Bound once rather than invoked reflectively every frame: InjectOverlay runs
        // on the render path while the overlay is on screen, and MethodInfo.Invoke
        // there allocated an object[], boxed the int2 argument and boxed the returned
        // NativeArray sixty times a second. Binding a typed delegate also checks the
        // signature the reflection lookup could not — GetMethod matches on parameters
        // only, so a changed return type would have surfaced as a per-frame cast
        // exception instead of the one-shot diagnostic below.
        private static Func<OverlayInfomodeSystem, int2, NativeArray<byte>>? s_GetTerrainTextureData;

        private static FieldInfo? s_TerrainTextureField;

        private static bool s_ReflectionChecked;

        private int m_LastLoggedIndex = int.MinValue;

        private bool m_PrefabsAdded;

        private bool m_InfoviewLinkChecked;

        private bool m_InfoviewLinkWaitLogged;

        private bool m_PlaceableSweepDone;

        private float m_LastPlaceableSweep;

        private int m_LastPlaceableCount = -1;

        private Dictionary<Entity, PlaceableInfoviewItem[]>? m_VanillaPlaceableInfoviews;

        private bool m_LastOverlayApplied;


        public void EnsurePrefabs()
        {
            if (m_PrefabsAdded)
            {
                return;
            }

            // Must happen before our infoview exists, or the snapshot already
            // contains the auto-activation entries we are trying to undo.
            SnapshotVanillaPlaceableInfoviews();

            var infomodeInfos = new List<InfomodeInfo>();
            SuitabilityLayer[] layers = SuitabilityLayers.All;
            for (int i = 0; i < layers.Length; i++)
            {
                SuitabilityLayer layer = layers[i];
                InfomodeBasePrefab prefab = CreateLayerPrefab(layer);
                m_LayerPrefabs[layer] = prefab;

                if (!m_PrefabSystem.AddPrefab(prefab))
                {
                    DeferredLog.Warn($"Failed to register infomode prefab for layer {layer}.");
                }

                var info = new InfomodeInfo();
                SetField(info, "m_Mode", prefab);
                SetField(info, "m_Priority", InfomodePriority - i);
                // Only the combined score is on by default; the rest are opt-in so
                // opening the infoview does not immediately burn all four channels.
                SetField(info, "m_Supplemental", layer != SuitabilityLayer.Score);
                SetField(info, "m_Optional", value: false);
                infomodeInfos.Add(info);
            }

            m_InfoviewPrefab = PrefabBase.Create<InfoviewPrefab>("TransitArchitect");
            SetField(m_InfoviewPrefab, "m_Infomodes", infomodeInfos.ToArray());
            SetField(m_InfoviewPrefab, "m_IconPath", "coui://transitarchitect/TransitArchitect.svg");
            SetField(m_InfoviewPrefab, "m_Priority", 900);
            SetField(m_InfoviewPrefab, "m_Group", 0);
            SetField(m_InfoviewPrefab, "m_DefaultColor", new Color(0.35f, 0.35f, 0.38f, 1f));
            SetField(m_InfoviewPrefab, "m_SecondaryColor", new Color(0.5f, 0.5f, 0.55f, 1f));
            // The view MUST stay valid and non-editor. InfoviewsUISystem.BindInfoviews
            // skips invalid views, which is tempting as a way to keep this out of the
            // Infoansicht menu — but ToolSystem.SetInfoview only activates a view's
            // infomodes while ToolSystem.activeInfoview is non-null, and that getter
            // returns null for an invalid view, so the heat map would never draw. The
            // menu row is hidden in the UI module instead (HideInfoviewMenuEntry).
            SetField(m_InfoviewPrefab, "m_Editor", value: false);
            SetField(m_InfoviewPrefab, "<isValid>k__BackingField", value: true);

            if (!m_PrefabSystem.AddPrefab(m_InfoviewPrefab))
            {
                DeferredLog.Warn("Failed to register infoview prefab.");
            }

            m_ToolSystem.EventInfomodesChanged?.Invoke();
            m_PrefabsAdded = true;
            DeferredLog.Info($"Registered {layers.Length} suitability infomodes and the infoview prefab.");
        }

        private static InfomodeBasePrefab CreateLayerPrefab(SuitabilityLayer layer)
        {
            InfomodeBasePrefab prefab = SuitabilityLayers.IsObjectLayer(layer)
                ? PrefabBase.Create<AccessInfomodePrefab>(SuitabilityLayers.NameOf(layer))
                : PrefabBase.Create<SuitabilityInfomodePrefab>(SuitabilityLayers.NameOf(layer));
            SuitabilityLayers.ColorsOf(layer, out Color low, out Color medium, out Color high);

            SetField(prefab, "m_Priority", InfomodePriority);
            SetField(prefab, "editor", value: false);
            SetField(prefab, "m_Low", low);
            SetField(prefab, "m_Medium", medium);
            SetField(prefab, "m_High", high);
            SetField(prefab, "m_Steps", layer == SuitabilityLayer.Sites ? 4 : 16);
            SetField(prefab, "m_LegendType", GradientLegendType.Gradient);
            SetField(prefab, "m_LowLabelId", "TransitArchitect.Legend.Low");
            SetField(prefab, "m_MediumLabelId", "TransitArchitect.Legend.Medium");
            SetField(prefab, "m_HighLabelId", "TransitArchitect.Legend.High");
            return prefab;
        }

        // ToolSystem.GetInfomodes reads the infoview ENTITY's InfoviewMode buffer,
        // not the managed prefab. For runtime-registered prefabs that buffer can end
        // up missing or empty, which leaves the infoview panel without rows and
        // prevents activation entirely — so verify it and repair it if needed.
        public void EnsureInfoviewLinked()
        {
            if (m_InfoviewLinkChecked || m_InfoviewPrefab == null || m_LayerPrefabs.Count == 0)
            {
                return;
            }

            if (!m_PrefabSystem.TryGetEntity(m_InfoviewPrefab, out Entity infoviewEntity))
            {
                if (!m_InfoviewLinkWaitLogged)
                {
                    DeferredLog.Info("Infoview prefab entity not found yet; retrying.");
                    m_InfoviewLinkWaitLogged = true;
                }
                return;
            }

            // Resolve every layer's entity before touching the buffer, so a partial
            // link cannot latch the checked flag.
            var entities = new List<KeyValuePair<Entity, SuitabilityLayer>>();
            SuitabilityLayer[] layers = SuitabilityLayers.All;
            for (int i = 0; i < layers.Length; i++)
            {
                if (!m_LayerPrefabs.TryGetValue(layers[i], out InfomodeBasePrefab prefab) ||
                    !m_PrefabSystem.TryGetEntity(prefab, out Entity entity))
                {
                    if (!m_InfoviewLinkWaitLogged)
                    {
                        DeferredLog.Info("Infomode prefab entities not all present yet; retrying.");
                        m_InfoviewLinkWaitLogged = true;
                    }
                    return;
                }

                entities.Add(new KeyValuePair<Entity, SuitabilityLayer>(entity, layers[i]));
            }

            bool hadBuffer = m_EntityManager.HasComponent<InfoviewMode>(infoviewEntity);
            DynamicBuffer<InfoviewMode> buffer = hadBuffer
                ? m_EntityManager.GetBuffer<InfoviewMode>(infoviewEntity)
                : m_EntityManager.AddBuffer<InfoviewMode>(infoviewEntity);

            int added = 0;
            m_InfomodeLayers.Clear();
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i].Key;
                SuitabilityLayer layer = entities[i].Value;
                m_InfomodeLayers[entity] = layer;

                bool linked = false;
                for (int j = 0; j < buffer.Length; j++)
                {
                    if (buffer[j].m_Mode == entity)
                    {
                        linked = true;
                        break;
                    }
                }

                if (!linked)
                {
                    _ = buffer.Add(new InfoviewMode(
                        entity,
                        InfomodePriority - i,
                        supplemental: layer != SuitabilityLayer.Score,
                        optional: false));
                    added++;
                }
            }

            if (added > 0)
            {
                m_ToolSystem.EventInfomodesChanged?.Invoke();
            }

            DeferredLog.Info($"Infoview link check: buffer existed={hadBuffer}, added {(added).ToString(CultureInfo.InvariantCulture)} entries, {buffer.Length} total.");
            m_InfoviewLinkChecked = true;
        }

        private void SnapshotVanillaPlaceableInfoviews()
        {
            m_VanillaPlaceableInfoviews = new Dictionary<Entity, PlaceableInfoviewItem[]>();
            using var entities = m_PlaceableInfoviewQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                DynamicBuffer<PlaceableInfoviewItem> buffer = m_EntityManager.GetBuffer<PlaceableInfoviewItem>(entities[i], isReadOnly: true);
                if (buffer.Length == 0)
                {
                    continue;
                }

                var items = new PlaceableInfoviewItem[buffer.Length];
                for (int j = 0; j < buffer.Length; j++)
                {
                    items[j] = buffer[j];
                }

                m_VanillaPlaceableInfoviews[entities[i]] = items;
            }

            DeferredLog.Info($"Captured vanilla auto-activation for {m_VanillaPlaceableInfoviews.Count} placeable prefabs.");
        }

        // InfoviewInitializeSystem scores every registered infoview against every
        // placeable prefab to fill its PlaceableInfoviewItem buffer, which
        // ToolBaseSystem uses to auto-activate an infoview when the player selects
        // that asset. Our infomodes carry none of the vanilla match data, so our
        // infoview always scores a neutral 0 — which beats any asset whose vanilla
        // infomodes all score negative, making the overlay pop up for seemingly
        // random build-menu selections. Restore the pre-mod choice instead.
        public void SweepPlaceableInfoviews()
        {
            if (!m_InfoviewLinkChecked)
            {
                return;
            }

            int placeableCount = m_PlaceableInfoviewQuery.CalculateEntityCount();
            float now = UnityEngine.Time.realtimeSinceStartup;
            bool full = !m_PlaceableSweepDone
                || placeableCount != m_LastPlaceableCount
                || now - m_LastPlaceableSweep >= PlaceableReverifySeconds;

            if (!full && m_PlaceableInfoviewChangedQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            if (!m_PrefabSystem.TryGetEntity(m_InfoviewPrefab, out Entity infoviewEntity))
            {
                return;
            }

            EntityQuery query = full ? m_PlaceableInfoviewQuery : m_PlaceableInfoviewChangedQuery;
            int restored = StripPlaceableInfoviewItems(query, infoviewEntity, out int cleared);
            if (!m_PlaceableSweepDone || restored > 0 || cleared > 0)
            {
                DeferredLog.Info($"Placeable infoview sweep ({(full ? "full" : "incremental")}): restored vanilla auto-activation on {(restored).ToString(CultureInfo.InvariantCulture)} prefabs, disabled it on {(cleared).ToString(CultureInfo.InvariantCulture)}.");
            }

            m_LastPlaceableCount = placeableCount;
            if (full)
            {
                m_LastPlaceableSweep = now;
            }

            m_PlaceableSweepDone = true;
        }

        private int StripPlaceableInfoviewItems(EntityQuery query, Entity infoviewEntity, out int cleared)
        {
            int restored = 0;
            cleared = 0;
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                DynamicBuffer<PlaceableInfoviewItem> buffer = m_EntityManager.GetBuffer<PlaceableInfoviewItem>(entity);
                if (buffer.Length == 0)
                {
                    continue;
                }

                if (buffer[0].m_Item == infoviewEntity)
                {
                    buffer.Clear();
                    if (m_VanillaPlaceableInfoviews is not null
                        && m_VanillaPlaceableInfoviews.TryGetValue(entity, out PlaceableInfoviewItem[] vanilla))
                    {
                        for (int j = 0; j < vanilla.Length; j++)
                        {
                            _ = buffer.Add(vanilla[j]);
                        }

                        restored++;
                    }
                    else
                    {
                        cleared++;
                    }

                    continue;
                }

                bool removed = false;
                for (int j = buffer.Length - 1; j >= 0; j--)
                {
                    if (buffer[j].m_Item == infoviewEntity || m_InfomodeLayers.ContainsKey(buffer[j].m_Item))
                    {
                        buffer.RemoveAt(j);
                        removed = true;
                    }
                }

                if (removed)
                {
                    cleared++;
                }
            }

            return restored;
        }

        // Which layers the player has toggled on, and which terrain channel each
        // landed in. ToolSystem hands out m_Index = colorGroup * 4 + (1-based active
        // count) with NO bounds check, so a fifth active layer gets an index past
        // our four channels and must be dropped here.
        private readonly List<KeyValuePair<int, SuitabilityLayer>> m_ActiveChannels =
            new List<KeyValuePair<int, SuitabilityLayer>>();

        // The colour-group index each active OBJECT layer landed in, or 0 when it is
        // off. Read by BuildingAccessColorSystem, which writes it into Game.Objects.Color.
        private readonly int[] m_ObjectLayerIndex = new int[SuitabilityLayers.Count];

        public int ObjectLayerIndex(SuitabilityLayer layer)
        {
            int index = (int)layer;
            return index >= 0 && index < m_ObjectLayerIndex.Length ? m_ObjectLayerIndex[index] : 0;
        }

        public int ResolveActiveLayers(out long signature)
        {
            m_ActiveChannels.Clear();
            System.Array.Clear(m_ObjectLayerIndex, 0, m_ObjectLayerIndex.Length);
            signature = 0;

            if (m_ActiveInfomodeQuery.IsEmptyIgnoreFilter)
            {
                return 0;
            }

            using var entities = m_ActiveInfomodeQuery.ToEntityArray(Allocator.Temp);
            using var actives = m_ActiveInfomodeQuery.ToComponentDataArray<InfomodeActive>(Allocator.Temp);

            int skipped = 0;
            int unlinked = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_InfomodeLayers.TryGetValue(entities[i], out SuitabilityLayer layer))
                {
                    unlinked++;
                    continue;
                }

                if (SuitabilityLayers.IsObjectLayer(layer))
                {
                    // Object layers colour buildings, not the terrain: their index
                    // belongs to another colour group and is not a terrain channel.
                    m_ObjectLayerIndex[(int)layer] = actives[i].m_Index;
                    continue;
                }

                int channel = actives[i].m_Index - 1;
                if (channel is < 0 or >= SuitabilityLayers.MaxActiveLayers)
                {
                    skipped++;
                    continue;
                }

                m_ActiveChannels.Add(new KeyValuePair<int, SuitabilityLayer>(channel, layer));
                signature |= ((long)((int)layer + 1)) << (channel * 8);
            }

            if (skipped > 0 && signature != m_ExpandedSignature)
            {
                DeferredLog.Warn($"{(skipped).ToString(CultureInfo.InvariantCulture)} suitability layer(s) skipped: the terrain overlay only has {SuitabilityLayers.MaxActiveLayers} channels. Turn one off to see another.");
            }

            // The query only ever holds our own infomodes, so reaching this point with
            // none resolved is a fault, not the overlay being off — and it is the one
            // fault that looks like a working overlay: the infoview is on, so
            // TerrainRenderSystem keeps painting our gradient, but no intensity is ever
            // written and the whole map sits at the low end of the ramp. It also stops
            // every compute, so the route suggestions go with it. Say so.
            if (m_LastLoggedIndex != m_ActiveChannels.Count)
            {
                if (m_ActiveChannels.Count > 0)
                {
                    DeferredLog.Info($"Active suitability layers: {m_ActiveChannels.Count}.");
                }
                else
                {
                    DeferredLog.Warn(
                        $"No suitability layer resolved from {(entities.Length).ToString(CultureInfo.InvariantCulture)} active infomode(s): " +
                        $"{(unlinked).ToString(CultureInfo.InvariantCulture)} not linked to a layer, " +
                        $"{(skipped).ToString(CultureInfo.InvariantCulture)} outside the {SuitabilityLayers.MaxActiveLayers} terrain channels. " +
                        "Nothing will be computed or drawn, and the map will show the low end of the gradient everywhere.");
                }

                m_LastLoggedIndex = m_ActiveChannels.Count;
            }

            return m_ActiveChannels.Count;
        }

        // Paints the active layers' intensities into the terrain overlay while the
        // infoview is on and the system has a computed field to show.
        public void ApplyOverlayState(bool active, long signature, bool hasData, byte[][] layerIntensities, int2 grid)
        {
            bool applied = false;
            if (active && hasData && CheckPipeline())
            {
                BuildExpandedCache(signature, layerIntensities, grid);
                applied = InjectOverlay(grid);
            }

            if (applied != m_LastOverlayApplied)
            {
                DeferredLog.Info($"Overlay map {(applied ? "attached" : "detached")} (active={active}, data={(hasData ? "yes" : "no")})");
                m_LastOverlayApplied = applied;
            }
        }

        // Verify the reflected members once and report loudly if a game update moved
        // them, rather than silently rendering nothing forever.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Reading the game's version string is only for the diagnostic below. " +
                "Any failure there must not stop the check from reporting what it found.")]
        private bool CheckPipeline()
        {
            if (s_ReflectionChecked)
            {
                return s_GetTerrainTextureData is not null && s_TerrainTextureField != null;
            }

            s_ReflectionChecked = true;
            MethodInfo? getter = typeof(OverlayInfomodeSystem).GetMethod(
                "GetTerrainTextureData",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(int2) },
                modifiers: null);
            s_TerrainTextureField = typeof(OverlayInfomodeSystem).GetField(
                "m_TerrainTexture",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (getter != null)
            {
                s_GetTerrainTextureData = Delegate.CreateDelegate(
                    typeof(Func<OverlayInfomodeSystem, int2, NativeArray<byte>>),
                    getter,
                    throwOnBindFailure: false) as Func<OverlayInfomodeSystem, int2, NativeArray<byte>>;
            }

            if (s_GetTerrainTextureData is not null && s_TerrainTextureField != null)
            {
                return true;
            }

            string version = "unknown";
            try
            {
                version = Colossal.Core.Version.current.fullVersion;
            }
            catch
            {
                // Version lookup is best-effort diagnostics only.
            }

            m_OnPipelineFault(
                "The overlay cannot draw: this game version moved the internals it renders through " +
                $"(game {version}). The mod needs an update.");
            DeferredLog.Error(
                "OverlayInfomodeSystem internals not found or the wrong shape " +
                $"(GetTerrainTextureData bound={s_GetTerrainTextureData is not null}, " +
                $"m_TerrainTexture={s_TerrainTextureField != null}) on game {version}; the overlay cannot render.");
            return false;
        }

        private void BuildExpandedCache(long signature, byte[][] layerIntensities, int2 grid)
        {
            int cells = grid.x * grid.y;
            if (cells <= 0)
            {
                return;
            }

            if (m_ExpandedCache is null || m_ExpandedCache.Length != cells * 4)
            {
                m_ExpandedCache = new byte[cells * 4];
                m_ExpandedSignature = -1;
            }

            if (m_ExpandedSignature == signature)
            {
                return;
            }

            Array.Clear(m_ExpandedCache, 0, m_ExpandedCache.Length);
            for (int a = 0; a < m_ActiveChannels.Count; a++)
            {
                int channel = m_ActiveChannels[a].Key;
                byte[] source = layerIntensities[(int)m_ActiveChannels[a].Value];
                if (source is null || source.Length < cells)
                {
                    continue;
                }

                for (int i = 0; i < cells; i++)
                {
                    m_ExpandedCache[i * 4 + channel] = source[i];
                }
            }

            m_ExpandedSignature = signature;
        }

        // Feed intensities through OverlayInfomodeSystem's own terrain texture — the
        // exact path the vanilla heatmaps use. GetTerrainTextureData resizes the
        // texture, assigns it to TerrainRenderSystem.overrideOverlaymap and schedules
        // a clear only when the texture instance changed; ApplyOverlay completes that
        // job, then we copy our data in and re-upload. Runs every frame while active
        // because the vanilla system clears the override at the start of each frame.
        private bool InjectOverlay(int2 grid)
        {
            // CheckPipeline binds both reflected members before anything calls this.
            if (s_GetTerrainTextureData is null || s_TerrainTextureField is null)
            {
                return false;
            }

            NativeArray<byte> data = s_GetTerrainTextureData(m_OverlayInfomodeSystem, grid);
            m_OverlayInfomodeSystem.ApplyOverlay();

            int expected = grid.x * grid.y * 4;
            if (data.Length != expected || m_ExpandedCache is null || m_ExpandedCache.Length != expected)
            {
                return false;
            }

            // Pattern-matched rather than cast: the texture is only created once the
            // game has sized it, and an unguarded cast turned "not ready yet" into an
            // exception on the render path.
            if (s_TerrainTextureField.GetValue(m_OverlayInfomodeSystem) is not Texture2D texture)
            {
                return false;
            }

            data.CopyFrom(m_ExpandedCache);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return true;
        }

        private static void SetField(object target, string name, object value)
        {
            if (target is null)
            {
                return;
            }

            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                DeferredLog.Warn($"Field not found: {target.GetType().Name}.{name}");
                return;
            }

            field.SetValue(target, value);
        }
    }
}
