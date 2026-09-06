using System.Collections.Generic;
using System.Globalization;
using Game.Notifications;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace TransitArchitect
{
    // Marking the lines that need building work on the map, with the game's own
    // notification icons (author's decision 16a, 2026-09-06): a line that is too big
    // for its mode, one no fleet of any mode can carry, and one that carries nobody.
    // Clicking such an icon selects its line, which opens the vanilla line panel —
    // Game.UI.InGame.SelectedInfoUISystem.FilterSelection rewrites an icon hit to the
    // icon's owner, so nothing more is needed for that.
    //
    // Deliberately NO icon prefab of our own. Game.Rendering.NotificationIconRenderSystem
    // packs every registered icon into one Texture2DArray whose format it takes from the
    // last prefab it walks, then Graphics.CopyTexture's them in; a mod texture of another
    // format would corrupt the atlas the whole game draws from. So this reuses a prefab
    // the game already ships, resolved by name. Until a name is configured the feature
    // stays off and the log lists the names available in this save, which is how the
    // right one gets chosen.
    public sealed partial class TransitArchitectSystem
    {
        // The verdicts worth a map marker: the three that mean "rebuild something",
        // as opposed to "change a number", which the panel and the overview row say.
        private static bool NeedsNotification(LineVerdict verdict)
        {
            return verdict is LineVerdict.ModeUp or LineVerdict.SplitRoute or LineVerdict.Remove;
        }

        private EntityQuery m_NotificationIconQuery;

        private Entity m_LineNotificationIcon;

        private bool m_NotificationIconsLogged;

        // Which lines currently carry our icon, so one is removed when its verdict
        // improves rather than left on the map for ever.
        private readonly HashSet<int> m_NotifiedLines = new HashSet<int>();

        private void CreateNotificationQuery()
        {
            m_NotificationIconQuery = GetEntityQuery(
                ComponentType.ReadOnly<NotificationIconData>(),
                ComponentType.ReadOnly<PrefabData>());
        }

        // The icon prefab this mod marks lines with, by name. Empty while none is
        // chosen: the names differ between game versions, so the first run logs what
        // this save offers instead of guessing. Not a const — the analyzer would fold
        // the empty string into the comparison below and read it as a length test.
        private static readonly string s_LineNotificationIconName = string.Empty;

        private bool TryResolveNotificationIcon()
        {
            if (m_LineNotificationIcon != Entity.Null)
            {
                return true;
            }

            if (m_NotificationIconQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            using var entities = m_NotificationIconQuery.ToEntityArray(Allocator.Temp);
            if (!m_NotificationIconsLogged)
            {
                m_NotificationIconsLogged = true;
                var names = new List<string>();
                for (int i = 0; i < entities.Length && names.Count < 40; i++)
                {
                    if (m_PrefabSystem.TryGetPrefab(entities[i], out PrefabBase prefab))
                    {
                        names.Add(prefab.name);
                    }
                }

                DeferredLog.Info(
                    $"Notification icons available in this save ({(entities.Length).ToString(CultureInfo.InvariantCulture)}): {string.Join(", ", names)}. " +
                    "Set s_LineNotificationIconName to one of them to mark lines that need rebuilding; the mod ships no icon of its own because the game packs them all into one texture array.");
            }

            if (string.IsNullOrEmpty(s_LineNotificationIconName))
            {
                return false;
            }

            for (int i = 0; i < entities.Length; i++)
            {
                if (m_PrefabSystem.TryGetPrefab(entities[i], out PrefabBase prefab)
                    && string.Equals(prefab.name, s_LineNotificationIconName, System.StringComparison.Ordinal))
                {
                    m_LineNotificationIcon = entities[i];
                    return true;
                }
            }

            DeferredLog.Warn($"Notification icon \"{s_LineNotificationIconName}\" is not in this save; lines needing work are marked in the panel only.");
            return false;
        }

        // Brings the map markers in line with the current verdicts. Called from the
        // health refresh, so it runs on the main thread with the collection settled.
        private void RefreshLineNotifications()
        {
            if (!TryResolveNotificationIcon())
            {
                return;
            }

            IconCommandBuffer icons = m_IconCommandSystem.CreateCommandBuffer();
            var wanted = new HashSet<int>();
            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                LineHealth health = m_LineHealth[i];
                if (!NeedsNotification(health.m_Verdict) || !m_LineEntities.TryGetValue(health.m_Id, out Entity entity))
                {
                    continue;
                }

                _ = wanted.Add(health.m_Id);
                if (m_NotifiedLines.Add(health.m_Id))
                {
                    icons.Add(entity, m_LineNotificationIcon, IconPriority.Problem);
                }
            }

            if (m_NotifiedLines.Count == wanted.Count)
            {
                return;
            }

            var stale = new List<int>();
            foreach (int id in m_NotifiedLines)
            {
                if (!wanted.Contains(id))
                {
                    stale.Add(id);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                if (m_LineEntities.TryGetValue(stale[i], out Entity entity))
                {
                    icons.Remove(entity, m_LineNotificationIcon);
                }

                _ = m_NotifiedLines.Remove(stale[i]);
            }
        }
    }
}
