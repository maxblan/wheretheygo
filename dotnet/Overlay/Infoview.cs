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

namespace WhereTheyGo
{
    // The mod's presentation in the game's infoview menu: the infoview and infomode
    // prefabs, the repair of the game's infoview buffer, the undo of vanilla
    // auto-activation, and which colour-group index each active layer landed in.
    //
    // The three queries are created by the overlay system (GetEntityQuery), so they
    // stay registered with that system's dependencies exactly as before.
    internal sealed class Infoview
    {
        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_PrefabSystem;
        private readonly ToolSystem m_ToolSystem;
        private readonly EntityQuery m_ActiveInfomodeQuery;
        private readonly EntityQuery m_PlaceableInfoviewQuery;
        private readonly EntityQuery m_PlaceableInfoviewChangedQuery;

        public Infoview(
            EntityManager entityManager,
            PrefabSystem prefabSystem,
            ToolSystem toolSystem,
            EntityQuery activeInfomodeQuery,
            EntityQuery placeableInfoviewQuery,
            EntityQuery placeableInfoviewChangedQuery)
        {
            m_EntityManager = entityManager;
            m_PrefabSystem = prefabSystem;
            m_ToolSystem = toolSystem;
            m_ActiveInfomodeQuery = activeInfomodeQuery;
            m_PlaceableInfoviewQuery = placeableInfoviewQuery;
            m_PlaceableInfoviewChangedQuery = placeableInfoviewChangedQuery;
        }

        // Leaving a city drops the caches that belong to it.
        public void Release()
        {
            m_VanillaPlaceableInfoviews = null;
        }

        // Opens or closes our infoview on behalf of the Options page. Activation goes
        // through ToolSystem.infoview, which is what assigns each infomode its index in
        // its colour group.
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
        private readonly Dictionary<OverlayLayer, InfomodeBasePrefab> m_LayerPrefabs =
            new Dictionary<OverlayLayer, InfomodeBasePrefab>();

        private readonly Dictionary<Entity, OverlayLayer> m_InfomodeLayers = new Dictionary<Entity, OverlayLayer>();

        private InfoviewPrefab? m_InfoviewPrefab;

        private int m_LastLoggedIndex = int.MinValue;

        private bool m_PrefabsAdded;

        private bool m_InfoviewLinkChecked;

        private bool m_InfoviewLinkWaitLogged;

        private bool m_PlaceableSweepDone;

        private float m_LastPlaceableSweep;

        private int m_LastPlaceableCount = -1;

        private Dictionary<Entity, PlaceableInfoviewItem[]>? m_VanillaPlaceableInfoviews;


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
            OverlayLayer[] layers = OverlayLayers.All;
            for (int i = 0; i < layers.Length; i++)
            {
                OverlayLayer layer = layers[i];
                InfomodeBasePrefab prefab = CreateLayerPrefab(layer);
                m_LayerPrefabs[layer] = prefab;

                if (!m_PrefabSystem.AddPrefab(prefab))
                {
                    DeferredLog.Warn($"Failed to register infomode prefab for layer {layer}.");
                }

                var info = new InfomodeInfo();
                SetField(info, "m_Mode", prefab);
                SetField(info, "m_Priority", InfomodePriority - i);
                SetField(info, "m_Supplemental", value: false);
                SetField(info, "m_Optional", value: false);
                infomodeInfos.Add(info);
            }

            m_InfoviewPrefab = PrefabBase.Create<InfoviewPrefab>("WhereTheyGo");
            SetField(m_InfoviewPrefab, "m_Infomodes", infomodeInfos.ToArray());
            SetField(m_InfoviewPrefab, "m_IconPath", "coui://wheretheygo/WhereTheyGo.svg");
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
            DeferredLog.Info($"Registered {(layers.Length).ToString(CultureInfo.InvariantCulture)} infomodes and the infoview prefab.");
        }

        private static InfomodeBasePrefab CreateLayerPrefab(OverlayLayer layer)
        {
            InfomodeBasePrefab prefab = PrefabBase.Create<AccessInfomodePrefab>(OverlayLayers.NameOf(layer));
            OverlayLayers.ColorsOf(layer, out Color low, out Color medium, out Color high);

            SetField(prefab, "m_Priority", InfomodePriority);
            SetField(prefab, "editor", value: false);
            SetField(prefab, "m_Low", low);
            SetField(prefab, "m_Medium", medium);
            SetField(prefab, "m_High", high);
            SetField(prefab, "m_Steps", 16);
            SetField(prefab, "m_LegendType", GradientLegendType.Gradient);
            // A label per LAYER, not one pair for both: the two ramps mean different
            // things at their ends, and "Low — High" under each of them says neither.
            string layerKey = OverlayLayers.NameOf(layer);
            SetField(prefab, "m_LowLabelId", $"WhereTheyGo.Legend.{layerKey}.Low");
            SetField(prefab, "m_MediumLabelId", $"WhereTheyGo.Legend.{layerKey}.Medium");
            SetField(prefab, "m_HighLabelId", $"WhereTheyGo.Legend.{layerKey}.High");
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
            var entities = new List<KeyValuePair<Entity, OverlayLayer>>();
            OverlayLayer[] layers = OverlayLayers.All;
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

                entities.Add(new KeyValuePair<Entity, OverlayLayer>(entity, layers[i]));
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
                OverlayLayer layer = entities[i].Value;
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
                        supplemental: false,
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
        // The colour-group index each active OBJECT layer landed in, or 0 when it is
        // off. Read by BuildingAccessColorSystem, which writes it into Game.Objects.Color.
        private readonly int[] m_ObjectLayerIndex = new int[OverlayLayers.Count];

        // Whether the player has this layer switched on. The game hands an active
        // infomode a 1-based index within its colour group, so a zero here means the
        // infomode is not in the active set at all.
        public bool IsLayerActive(OverlayLayer layer) => ObjectLayerIndex(layer) > 0;

        public int ObjectLayerIndex(OverlayLayer layer)
        {
            int index = (int)layer;
            return index >= 0 && index < m_ObjectLayerIndex.Length ? m_ObjectLayerIndex[index] : 0;
        }

        // How many of the mod's own infomodes the player has switched on, and where
        // each object layer landed in its colour group.
        public int ActiveLayers()
        {
            System.Array.Clear(m_ObjectLayerIndex, 0, m_ObjectLayerIndex.Length);
            if (m_ActiveInfomodeQuery.IsEmptyIgnoreFilter)
            {
                return 0;
            }

            using var entities = m_ActiveInfomodeQuery.ToEntityArray(Allocator.Temp);
            using var actives = m_ActiveInfomodeQuery.ToComponentDataArray<InfomodeActive>(Allocator.Temp);

            int active = 0;
            int unlinked = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_InfomodeLayers.TryGetValue(entities[i], out OverlayLayer layer))
                {
                    unlinked++;
                    continue;
                }

                m_ObjectLayerIndex[(int)layer] = actives[i].m_Index;
                active++;
            }

            // The query only ever holds our own infomodes, so reaching this point with
            // none resolved is a fault, not the infoview being off: the view is on, the
            // legend is drawn, and nothing is ever coloured. Say so.
            if (m_LastLoggedIndex != active)
            {
                if (active > 0)
                {
                    DeferredLog.Info($"Active layers: {(active).ToString(CultureInfo.InvariantCulture)}.");
                }
                else
                {
                    DeferredLog.Warn(
                        $"No layer resolved from {(entities.Length).ToString(CultureInfo.InvariantCulture)} active infomode(s): " +
                        $"{(unlinked).ToString(CultureInfo.InvariantCulture)} not linked to a layer. Nothing will be coloured.");
                }

                m_LastLoggedIndex = active;
            }

            return active;
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
