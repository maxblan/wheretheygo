using Game.Citizens;
using Game.Simulation;
using Unity.Entities;

namespace WhereTheyGo
{
    // Observed shopping and leisure journeys: the system's cadence for
    // the live-city scan; the scan itself is TripObserver.
    public sealed partial class WhereTheyGoSystem
    {

        private EntityQuery m_QueuedQuery;

        private EntityQuery m_TravellingQuery;

        private EntityQuery m_InsideQuery;

        private float m_LastTripObservation;

        private void ObserveTrips()
        {
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            uint frame = simulation?.frameIndex ?? 0u;
            m_TransformLookup.Update(this);
            m_PropertyRenterLookup.Update(this);
            m_TripObserver.Scan(
                frame, TimeOfDay, m_TransformLookup, m_PropertyRenterLookup,
                GetEntityTypeHandle(), GetBufferTypeHandle<TripNeeded>(isReadOnly: true), GetComponentTypeHandle<CurrentBuilding>(isReadOnly: true));
        }
    }
}
