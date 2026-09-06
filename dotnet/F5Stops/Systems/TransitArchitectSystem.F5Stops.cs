using Unity.Mathematics;
using Block = Game.Zones.Block;
using Transform = Game.Objects.Transform;

namespace StationSuitabilityOverlay
{
    // F5 — what every stop plan of a pass shares, gathered from the system's state:
    // the journeys' doors, the fleet facts, the interchanges and the score oracle.
    public sealed partial class StationSuitabilityOverlaySystem
    {
        // Everything a stop plan reads that is not the alignment itself: the journeys'
        // doors (each journey contributes its origin and its destination, both at the
        // journey's weight), the prefab facts, the score oracle and the interchanges.
        private StopContext BuildStopContext()
        {
            var ends = new float2Like[m_Journeys.Count * 2];
            var weights = new float[m_Journeys.Count * 2];
            for (int i = 0; i < m_Journeys.Count; i++)
            {
                Trip trip = m_Journeys[i];
                ends[2 * i] = trip.m_Origin;
                ends[(2 * i) + 1] = trip.m_Destination;
                weights[2 * i] = trip.m_Weight;
                weights[(2 * i) + 1] = trip.m_Weight;
            }

            int2 gridSize = m_IntensityGrid;
            return new StopContext
            {
                Ends = ends,
                EndWeights = weights,
                Facts = ReadFleetFacts(),
                Hubs = m_Interchanges,
                ScoreAt = (point, mode) => ScoreForMode(new float2(point.x, point.y), gridSize, mode),
            };
        }
    }
}
