using Game.Common;
using Game.Prefabs;
using Unity.Entities;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // The game's own facts about each mode, read once from the loaded prefabs: seats,
    // acceleration and braking, the passenger line's interval and stop duration.
    public sealed partial class StationSuitabilityOverlaySystem
    {
        private EntityQuery m_VehiclePrefabQuery;

        private EntityQuery m_LinePrefabQuery;

        // Capacity per mode, read once from the prefabs. Prefabs do not change while a
        // save is loaded, so this is built on the first compute and kept; a reload
        // rebuilds it with the rest of the system.
        private ModeFacts[]? m_FleetFacts;

        // Largest vehicle capacity per Game.Prefabs.TransportType (index = enum value),
        // read alongside m_FleetFacts. The interchange weights derive from it.
        private float[]? m_TypeCapacities;

        // The vehicle and passenger-line prefabs the fleet facts are read from.
        private void CreatePrefabQueries()
        {
            m_VehiclePrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PublicTransportVehicleData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_LinePrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TransportLineData>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_EconomyQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
        }

        // Capacity weight per Game.Prefabs.TransportType index, from the loaded prefabs
        // (register A1.10). Zero for types the save has no vehicle for.
        private float[] TypeWeights()
        {
            var weights = new float[(int)TransportType.Count];
            for (int type = 0; type < weights.Length; type++)
            {
                weights[type] = StopWeightOf((TransportType)type);
            }

            return weights;
        }

        // Passenger capacity of one vehicle of each mode, taken from the game's own
        // prefabs. This is what every rider floor is derived from, so it must be the
        // real figure and not a table in this mod that nothing keeps in step.
        //
        // What a stop of `type` is worth as a transfer partner: its vehicle's capacity
        // relative to a bus, from the loaded prefabs (register A1.10). The one owner of
        // that weight — the job's bucket weights, the combine's self weight and the
        // per-mode swap all call this, so they cannot disagree about what a train is
        // worth next to a bus stop. Logged once with the capacities it rests on.
        private float StopWeightOf(TransportType type)
        {
            if (m_TypeCapacities is null)
            {
                _ = ReadFleetFacts();
            }

            float[] byType = m_TypeCapacities ?? System.Array.Empty<float>();
            int index = (int)type;
            int bus = (int)TransportType.Bus;
            float capacity = index >= 0 && index < byType.Length ? byType[index] : 0f;
            float busCapacity = bus >= 0 && bus < byType.Length ? byType[bus] : 0f;
            return TransitModes.CapacityWeight(capacity, busCapacity);
        }

        // The facts for every mode, read once (SuitabilityFleet.Read) and kept: prefabs
        // do not change while a save is loaded, and a reload rebuilds the system.
        private FleetFacts ReadFleetFacts()
        {
            if (m_FleetFacts is null)
            {
                m_FleetFacts = SuitabilityFleet.Read(EntityManager, m_VehiclePrefabQuery, m_LinePrefabQuery, out float[] byType);
                m_TypeCapacities = byType;
            }

            return new FleetFacts(m_FleetFacts);
        }
    }
}
