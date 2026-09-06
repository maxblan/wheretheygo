using System.Globalization;
using System.Text;
using Colossal.Entities;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TransitArchitect
{
    // Reads the game's own facts about each mode from the loaded prefabs (register
    // A5.5, A6.x): the largest vehicle's seats (carriages included), its acceleration
    // and braking, and the passenger line prefab's default interval and stop duration.
    // Every rider floor derives from these, so they must be the real figures and not a
    // table in this mod that nothing keeps in step. Logged once with what they rest on.
    internal static class Fleet
    {
        // The game's own facts about each mode, read once from the loaded prefabs
        // (register A5.5, A6.x): the largest vehicle's seats (carriages included), its
        // acceleration and braking, and the passenger line prefab's default interval and
        // stop duration. Prefabs do not change while a save is loaded.
        public static ModeFacts[] Read(EntityManager entityManager, EntityQuery vehiclePrefabQuery, EntityQuery linePrefabQuery, VehicleCountPolicy policy, out float[] capacityByType)
        {
            var byMode = new ModeFacts[TransitModes.All.Length];
            for (int m = 0; m < byMode.Length; m++)
            {
                byMode[m] = new ModeFacts();
            }

            var byType = new float[(int)TransportType.Count];
            using (var entities = vehiclePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity prefab = entities[i];
                    if (!entityManager.TryGetComponent(prefab, out PublicTransportVehicleData vehicle))
                    {
                        continue;
                    }

                    int capacity = vehicle.m_PassengerCapacity;
                    if (entityManager.TryGetBuffer(prefab, isReadOnly: true, out DynamicBuffer<VehicleCarriageElement> carriages))
                    {
                        for (int c = 0; c < carriages.Length; c++)
                        {
                            VehicleCarriageElement carriage = carriages[c];
                            if (carriage.m_Prefab == Entity.Null
                                || !entityManager.TryGetComponent(carriage.m_Prefab, out PublicTransportVehicleData unit))
                            {
                                continue;
                            }

                            capacity += unit.m_PassengerCapacity * carriage.m_Count.x;
                        }
                    }

                    ReadMotion(entityManager, prefab, out float acceleration, out float braking);
                    int typeIndex = (int)vehicle.m_TransportType;
                    if (typeIndex >= 0 && typeIndex < byType.Length)
                    {
                        byType[typeIndex] = math.max(byType[typeIndex], capacity);
                    }

                    for (int m = 0; m < TransitModes.All.Length; m++)
                    {
                        ModePreset mode = TransitModes.All[m];
                        if (SuitabilityInputs.TransportTypeOf(mode) != vehicle.m_TransportType)
                        {
                            continue;
                        }

                        ModeFacts facts = byMode[(int)mode];
                        if (capacity > facts.Capacity)
                        {
                            facts.Capacity = capacity;
                            facts.Acceleration = acceleration;
                            facts.Braking = braking;
                        }
                    }
                }
            }

            using (var lines = linePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!entityManager.TryGetComponent(lines[i], out TransportLineData line) || !line.m_PassengerTransport)
                    {
                        continue;
                    }

                    for (int m = 0; m < TransitModes.All.Length; m++)
                    {
                        ModePreset mode = TransitModes.All[m];
                        ModeFacts facts = byMode[(int)mode];
                        if (SuitabilityInputs.TransportTypeOf(mode) != line.m_TransportType || facts.PrefabIntervalSeconds > 0f)
                        {
                            continue;
                        }

                        facts.PrefabIntervalSeconds = line.m_DefaultVehicleInterval;
                        facts.StopDurationSeconds = line.m_StopDuration;
                    }
                }
            }

            capacityByType = byType;
            var result = new FleetFacts(byMode, policy);
            var report = new StringBuilder("Fleet facts from the loaded prefabs (Game.Prefabs.PublicTransportVehicleData with carriages, CarData/TrainData/WatercraftData, TransportLineData): ");
            for (int m = 0; m < TransitModes.All.Length; m++)
            {
                ModePreset mode = TransitModes.All[m];
                ModeFacts facts = byMode[(int)mode];
                _ = report.Append(mode.ToString())
                    .Append(" seats ").Append(facts.Capacity.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(", prefab interval ").Append(facts.PrefabIntervalSeconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" s")
                    .Append(", stop ").Append(facts.StopDurationSeconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" s")
                    .Append(", accel ").Append(facts.Acceleration.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(", brake ").Append(facts.Braking.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(" -> ").Append(result.DelayPerStopSeconds(mode).ToString("F0", CultureInfo.InvariantCulture)).Append(" s per stop; ");
            }

            _ = report.Append("seats 0 means no vehicle of that mode is installed and the mode is never chosen. ");
            _ = report.Append(policy.Known
                ? $"Vehicle-count slider: {policy.Mode} modifier over [{policy.DeltaMin.ToString("R", CultureInfo.InvariantCulture)}, {policy.DeltaMax.ToString("R", CultureInfo.InvariantCulture)}] (slider range [{policy.SliderMin.ToString("R", CultureInfo.InvariantCulture)}, {policy.SliderMax.ToString("R", CultureInfo.InvariantCulture)}]) — a 1000 s bus round trip may run {SpanText(result, ModePreset.Bus, 1000f)} buses, a metro {SpanText(result, ModePreset.Metro, 1000f)}"
                : "Vehicle-count slider: policy prefab NOT found — fleets are unbounded above and the log will say so per line");
            DeferredLog.Info(report.ToString());
            return byMode;
        }

        private static string SpanText(FleetFacts facts, ModePreset mode, float roundTrip)
        {
            facts.FleetSpanFor(mode, roundTrip, out int min, out int max);
            return $"[{min.ToString(CultureInfo.InvariantCulture)}, {(max == int.MaxValue ? "?" : max.ToString(CultureInfo.InvariantCulture))}]";
        }

        // The vehicle-count policy as VehicleCountSection reads it: the UI transport
        // configuration singleton names the policy prefab; its PolicySliderData carries the
        // slider range and its RouteModifierData buffer the VehicleInterval modifier's mode
        // and range (RouteModifierInitializeSystem lerps the one onto the other).
        public static VehicleCountPolicy ReadVehicleCountPolicy(EntityManager entityManager, PrefabSystem prefabSystem, EntityQuery configQuery)
        {
            var policy = new VehicleCountPolicy();
            if (configQuery.IsEmptyIgnoreFilter)
            {
                DeferredLog.Warn("Vehicle-count policy: no UITransportConfigurationData singleton in this save");
                return policy;
            }

            UITransportConfigurationPrefab configuration = prefabSystem.GetSingletonPrefab<UITransportConfigurationPrefab>(configQuery);
            if (configuration?.m_VehicleCountPolicy is null)
            {
                DeferredLog.Warn("Vehicle-count policy: the transport configuration names no vehicle-count policy");
                return policy;
            }

            Entity policyEntity = prefabSystem.GetEntity(configuration.m_VehicleCountPolicy);
            if (!entityManager.TryGetComponent(policyEntity, out PolicySliderData slider)
                || !entityManager.TryGetBuffer(policyEntity, isReadOnly: true, out DynamicBuffer<RouteModifierData> modifiers))
            {
                DeferredLog.Warn("Vehicle-count policy: prefab has no slider data or modifier buffer");
                return policy;
            }

            for (int i = 0; i < modifiers.Length; i++)
            {
                RouteModifierData modifier = modifiers[i];
                if (modifier.m_Type != Game.Routes.RouteModifierType.VehicleInterval)
                {
                    continue;
                }

                policy.Known = true;
                policy.Mode = (IntervalModifierMode)(int)modifier.m_Mode;
                policy.DeltaMin = modifier.m_Range.min;
                policy.DeltaMax = modifier.m_Range.max;
                policy.SliderMin = slider.m_Range.min;
                policy.SliderMax = slider.m_Range.max;
                return policy;
            }

            DeferredLog.Warn("Vehicle-count policy: no VehicleInterval modifier on the policy prefab");
            return policy;
        }

        // Acceleration and braking of a vehicle prefab, whichever motion component it has.
        private static void ReadMotion(EntityManager entityManager, Entity prefab, out float acceleration, out float braking)
        {
            if (entityManager.TryGetComponent(prefab, out CarData car))
            {
                acceleration = car.m_Acceleration;
                braking = car.m_Braking;
            }
            else if (entityManager.TryGetComponent(prefab, out TrainData train))
            {
                acceleration = train.m_Acceleration;
                braking = train.m_Braking;
            }
            else if (entityManager.TryGetComponent(prefab, out WatercraftData boat))
            {
                acceleration = boat.m_Acceleration;
                braking = boat.m_Braking;
            }
            else
            {
                acceleration = 0f;
                braking = 0f;
            }
        }
    }
}
