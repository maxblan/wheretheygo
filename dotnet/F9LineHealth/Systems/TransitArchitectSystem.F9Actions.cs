using System.Collections.Generic;
using System.Globalization;
using Colossal.Entities;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.UI.InGame;
using Unity.Entities;
using Unity.Mathematics;

namespace TransitArchitect
{
    // Carrying out a recommendation, through the game's own controls rather than
    // beside them (author's decision 17a, 2026-09-06): the fleet and the schedule are
    // set exactly as the line panel's own widgets set them, so the player sees the
    // slider and the schedule buttons move.
    //
    // The vehicle-count path is mirrored from Game.UI.InGame.VehicleCountSection
    // (decompiled 2026-09-06). Its arithmetic lives in a PRIVATE nested job, so the
    // three lines below are reimplemented rather than called; every helper they use
    // is public. Getting this wrong is quiet — the policy would just land on the wrong
    // notch — so the result is checked against the game's own fleet formula and logged.
    public sealed partial class TransitArchitectSystem
    {
        // Which line entity each health row belongs to. The panel round-trips the
        // packed identity (Lines.IdentityOf), never an entity, because a raw index
        // handed back after a rebuild would point at whatever took that slot.
        private readonly Dictionary<int, Entity> m_LineEntities = new Dictionary<int, Entity>();

        private static int s_FleetRequestLine = -1;

        private static int s_FleetRequestVehicles;

        private static int s_ScheduleRequestLine = -1;

        private static int s_ScheduleRequestSchedule;

        private static int s_FocusRequestLine = -1;

        public static void RequestFleet(int lineId, int vehicles)
        {
            s_FleetRequestLine = lineId;
            s_FleetRequestVehicles = vehicles;
        }

        public static void RequestSchedule(int lineId, int schedule)
        {
            s_ScheduleRequestLine = lineId;
            s_ScheduleRequestSchedule = schedule;
        }

        public static void RequestFocusLine(int lineId) => s_FocusRequestLine = lineId;

        // What the overview row's button does: carry out the part of the plan the game
        // can apply by itself. A fleet change and a schedule change are settings the
        // player could make in the line panel, so one click makes them; a verdict that
        // means building work (a bigger mode, a split, a removal) is never acted on —
        // the line is shown instead, and the player decides.
        public static void RequestApplyPlan(int lineId) => s_ApplyPlanLine = lineId;

        private static int s_ApplyPlanLine = -1;

        private void ApplyPlan(int lineId)
        {
            if (!TryGetLine(lineId, out Entity _, out ExistingLine? line) || line is null)
            {
                return;
            }

            for (int i = 0; i < m_LineHealth.Count; i++)
            {
                LineHealth health = m_LineHealth[i];
                if (health.m_Id != lineId)
                {
                    continue;
                }

                switch (health.m_Verdict)
                {
                    case LineVerdict.FleetShort:
                        ApplyFleet(lineId, health.m_TargetVehicles);
                        return;
                    case LineVerdict.FleetUp:
                    case LineVerdict.FleetDown:
                        ApplyFleet(lineId, health.m_RecommendedFleet);
                        return;
                    case LineVerdict.Schedule:
                        ApplySchedule(lineId, health.m_ScheduleAdvice);
                        return;
                    default:
                        FocusLine(lineId);
                        return;
                }
            }
        }

        private void HandleLineActionRequests()
        {
            if (s_FleetRequestLine >= 0)
            {
                int line = s_FleetRequestLine;
                int vehicles = s_FleetRequestVehicles;
                s_FleetRequestLine = -1;
                ApplyFleet(line, vehicles);
            }

            if (s_ScheduleRequestLine >= 0)
            {
                int line = s_ScheduleRequestLine;
                var schedule = (LineSchedule)s_ScheduleRequestSchedule;
                s_ScheduleRequestLine = -1;
                ApplySchedule(line, schedule);
            }

            if (s_ApplyPlanLine >= 0)
            {
                int line = s_ApplyPlanLine;
                s_ApplyPlanLine = -1;
                ApplyPlan(line);
            }

            if (s_FocusRequestLine >= 0)
            {
                int line = s_FocusRequestLine;
                s_FocusRequestLine = -1;
                FocusLine(line);
            }
        }

        private bool TryGetLine(int lineId, out Entity entity, out ExistingLine? line)
        {
            line = null;
            if (!m_LineEntities.TryGetValue(lineId, out entity) || !EntityManager.Exists(entity))
            {
                DeferredLog.Warn($"Line action for id {(lineId).ToString(CultureInfo.InvariantCulture)}: that line no longer exists.");
                return false;
            }

            for (int i = 0; i < m_ExistingLines.Count; i++)
            {
                if (m_ExistingLines[i].m_Id == lineId)
                {
                    line = m_ExistingLines[i];
                    return true;
                }
            }

            return false;
        }

        // The player sets a vehicle COUNT; the game stores it as a position on the
        // vehicle-count policy's slider, which modifies the line prefab's default
        // interval. So the count is turned back into an interval, the interval into a
        // modifier delta, and the delta into the slider position.
        private void ApplyFleet(int lineId, int vehicles)
        {
            if (!TryGetLine(lineId, out Entity entity, out ExistingLine? line) || line is null)
            {
                return;
            }

            if (m_VehicleCountPolicyEntity == Entity.Null
                || !EntityManager.TryGetComponent(m_VehicleCountPolicyEntity, out PolicySliderData slider)
                || !EntityManager.TryGetBuffer(m_VehicleCountPolicyEntity, isReadOnly: true, out DynamicBuffer<RouteModifierData> modifiers)
                || !EntityManager.TryGetComponent(entity, out PrefabRef prefabRef)
                || !EntityManager.TryGetComponent(prefabRef.m_Prefab, out TransportLineData lineData))
            {
                DeferredLog.Warn($"Cannot set the fleet of \"{line.m_Name}\": the vehicle-count policy is not available in this save.");
                return;
            }

            float roundTrip = line.m_StableDurationSeconds;
            if (roundTrip <= 0f)
            {
                DeferredLog.Warn($"Cannot set the fleet of \"{line.m_Name}\": its round trip is not known yet.");
                return;
            }

            float wanted = TransportLineSystem.CalculateVehicleInterval(roundTrip, vehicles);
            if (!TryAdjustment(modifiers, slider, lineData.m_DefaultVehicleInterval, wanted, out float adjustment))
            {
                DeferredLog.Warn($"Cannot set the fleet of \"{line.m_Name}\": the policy carries no vehicle-interval modifier.");
                return;
            }

            m_PoliciesUISystem.SetPolicy(entity, m_VehicleCountPolicyEntity, active: true, adjustment);

            // What the game will make of it, by its own formula on its own inputs. The
            // slider is a coarse notch, so landing one vehicle away is possible and the
            // log says so rather than the player wondering.
            float applied = IntervalAtAdjustment(modifiers, slider, lineData.m_DefaultVehicleInterval, adjustment);
            int result = TransportLineSystem.CalculateVehicleCount(applied, roundTrip);
            DeferredLog.Info(
                $"Fleet of \"{line.m_Name}\" set to {(vehicles).ToString(CultureInfo.InvariantCulture)} vehicle(s): " +
                $"interval {(wanted).ToString("F0", CultureInfo.InvariantCulture)} s, slider {(adjustment).ToString("F1", CultureInfo.InvariantCulture)} " +
                $"-> the game will run {(result).ToString(CultureInfo.InvariantCulture)}" +
                $"{(result == vehicles ? string.Empty : " (the slider has no notch for the exact count)")}");
        }

        // Mirrors VehicleCountSection.CalculateVehicleCountJob.CalculateAdjustmentFromVehicleCount.
        private static bool TryAdjustment(DynamicBuffer<RouteModifierData> modifiers, PolicySliderData slider, float prefabInterval, float wantedInterval, out float adjustment)
        {
            adjustment = 0f;
            for (int i = 0; i < modifiers.Length; i++)
            {
                RouteModifierData data = modifiers[i];
                if (data.m_Type != RouteModifierType.VehicleInterval)
                {
                    continue;
                }

                RouteModifier modifier = default;
                if (data.m_Mode == ModifierValueMode.Absolute)
                {
                    modifier.m_Delta.x = wantedInterval - prefabInterval;
                }
                else
                {
                    modifier.m_Delta.y = (0f - prefabInterval + wantedInterval) / prefabInterval;
                }

                float delta = RouteModifierInitializeSystem.RouteModifierRefreshData.GetDeltaFromModifier(modifier, data);
                adjustment = RouteModifierInitializeSystem.RouteModifierRefreshData.GetPolicyAdjustmentFromModifierDelta(data, delta, slider);
                return true;
            }

            return false;
        }

        // The interval the game will end up with for a slider position — the same lerp
        // and RouteUtils.ApplyModifier the policy system applies (TransitModes.IntervalAt).
        private static float IntervalAtAdjustment(DynamicBuffer<RouteModifierData> modifiers, PolicySliderData slider, float prefabInterval, float adjustment)
        {
            for (int i = 0; i < modifiers.Length; i++)
            {
                RouteModifierData data = modifiers[i];
                if (data.m_Type != RouteModifierType.VehicleInterval)
                {
                    continue;
                }

                float t = math.saturate(slider.m_Range.max == slider.m_Range.min
                    ? 0f
                    : (adjustment - slider.m_Range.min) / (slider.m_Range.max - slider.m_Range.min));
                float delta = math.lerp(data.m_Range.min, data.m_Range.max, t);
                return TransitModes.IntervalAt(prefabInterval, (IntervalModifierMode)(int)data.m_Mode, delta);
            }

            return prefabInterval;
        }

        // Day = the day policy on and the night policy off, and the mirror for night;
        // all day is both off (Game.UI.InGame.ScheduleSection).
        private void ApplySchedule(int lineId, LineSchedule schedule)
        {
            if (!TryGetLine(lineId, out Entity entity, out ExistingLine? line) || line is null)
            {
                return;
            }

            if (m_DayRoutePolicyEntity == Entity.Null || m_NightRoutePolicyEntity == Entity.Null)
            {
                DeferredLog.Warn($"Cannot set the schedule of \"{line.m_Name}\": the schedule policies are not available in this save.");
                return;
            }

            m_PoliciesUISystem.SetPolicy(entity, m_DayRoutePolicyEntity, schedule == LineSchedule.Day);
            m_PoliciesUISystem.SetPolicy(entity, m_NightRoutePolicyEntity, schedule == LineSchedule.Night);
            DeferredLog.Info($"Schedule of \"{line.m_Name}\" set to {schedule}.");
        }

        // Selecting the line is what opens the game's own line panel; following it with
        // the orbit camera is what the vanilla "focus" button does
        // (Game.UI.InGame.CameraUISystem.FocusEntity). A line resolves to a position
        // through its RouteWaypoint buffer, so no extra work is needed here.
        private void FocusLine(int lineId)
        {
            if (!TryGetLine(lineId, out Entity entity, out ExistingLine? line) || line is null)
            {
                return;
            }

            m_ToolSystem.selected = entity;
            var orbit = m_CameraUpdateSystem?.orbitCameraController;
            if (m_CameraUpdateSystem is not null && orbit is not null && orbit.followedEntity != entity)
            {
                orbit.followedEntity = entity;
                orbit.TryMatchPosition(m_CameraUpdateSystem.activeCameraController);
                m_CameraUpdateSystem.activeCameraController = orbit;
            }

            DeferredLog.Info($"Focused \"{line.m_Name}\".");
        }
    }
}
