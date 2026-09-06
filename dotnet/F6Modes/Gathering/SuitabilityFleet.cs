using System.Globalization;
using System.Text;
using Colossal.Entities;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // Reads the game's own facts about each mode from the loaded prefabs (register
    // A5.5, A6.x): the largest vehicle's seats (carriages included), its acceleration
    // and braking, and the passenger line prefab's default interval and stop duration.
    // Every rider floor derives from these, so they must be the real figures and not a
    // table in this mod that nothing keeps in step. Logged once with what they rest on.
    internal static class SuitabilityFleet
    {
        // The game's own facts about each mode, read once from the loaded prefabs
        // (register A5.5, A6.x): the largest vehicle's seats (carriages included), its
        // acceleration and braking, and the passenger line prefab's default interval and
        // stop duration. Prefabs do not change while a save is loaded.
        public static ModeFacts[] Read(EntityManager entityManager, EntityQuery vehiclePrefabQuery, EntityQuery linePrefabQuery, out float[] capacityByType)
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
                        if (SuitabilityInputs.TransportTypeOf(mode) != line.m_TransportType || facts.HeadwaySeconds > 0f)
                        {
                            continue;
                        }

                        facts.HeadwaySeconds = line.m_DefaultVehicleInterval;
                        facts.StopDurationSeconds = line.m_StopDuration;
                    }
                }
            }

            capacityByType = byType;
            var result = new FleetFacts(byMode);
            var report = new StringBuilder("Fleet facts from the loaded prefabs (Game.Prefabs.PublicTransportVehicleData with carriages, CarData/TrainData/WatercraftData, TransportLineData): ");
            for (int m = 0; m < TransitModes.All.Length; m++)
            {
                ModePreset mode = TransitModes.All[m];
                ModeFacts facts = byMode[(int)mode];
                _ = report.Append(mode.ToString())
                    .Append(" seats ").Append(facts.Capacity.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(", prefab interval ").Append(facts.HeadwaySeconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" s")
                    .Append(" (planning headway ").Append(result.HeadwayFor(mode).ToString("F0", CultureInfo.InvariantCulture)).Append(" s)")
                    .Append(", stop ").Append(facts.StopDurationSeconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" s")
                    .Append(", accel ").Append(facts.Acceleration.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(", brake ").Append(facts.Braking.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(" -> ").Append(result.DelayPerStopSeconds(mode).ToString("F0", CultureInfo.InvariantCulture)).Append(" s per stop; ");
            }

            _ = report.Append("seats 0 means no vehicle of that mode is installed and the mode is never chosen.");
            DeferredLog.Info(report.ToString());
            return byMode;
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
