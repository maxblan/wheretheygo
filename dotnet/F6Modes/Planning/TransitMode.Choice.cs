using System;

namespace StationSuitabilityOverlay
{
    // What the game's own prefabs say about one mode's vehicles and lines: seats per
    // vehicle (carriages included), the line prefab's default interval and stop
    // duration, and the vehicle's acceleration and braking. Zero where no prefab of
    // that mode is loaded; the readers fall back to the tables below and say so.
    internal sealed class ModeFacts
    {
        public float Capacity;
        public float HeadwaySeconds;
        public float StopDurationSeconds;
        public float Acceleration;
        public float Braking;
    }

    internal readonly struct FleetFacts
    {
        private readonly ModeFacts[] m_ByMode;

        public FleetFacts(ModeFacts[] byMode)
        {
            m_ByMode = byMode;
        }

        private ModeFacts? Of(ModePreset mode)
        {
            int index = (int)mode;
            return m_ByMode is not null && index >= 0 && index < m_ByMode.Length ? m_ByMode[index] : null;
        }

        public float CapacityFor(ModePreset mode)
        {
            return Of(mode)?.Capacity ?? 0f;
        }

        // The planning headway of a suggested line: TransitModes.TargetHeadwayFor. NOT
        // the line prefab's default interval, deliberately: the prefabs say 45 s for a
        // bus and 60 s for a metro, intervals no player leaves in place and against
        // which the 15 % utilisation floor was never calibrated (a 257-rider bus that
        // is 29 % full at 300 s is 3 % full at 45 s). The prefab value is kept for the
        // log (ModeFacts.HeadwaySeconds); register A5.5/A6.x carries the open question.
        // An instance member although it reads no prefab today: the open question in
        // register A5.5 is whether the prefab interval should become the planning
        // headway, and that switch must not touch every caller.
#pragma warning disable CA1822
        public float HeadwayFor(ModePreset mode)
        {
            return TransitModes.TargetHeadwayFor(mode);
        }
#pragma warning restore CA1822

        // Vanilla's wait model: half the interval.
        public float ExpectedWaitFor(ModePreset mode)
        {
            return HeadwayFor(mode) * 0.5f;
        }

        public float StopDurationFor(ModePreset mode)
        {
            float duration = Of(mode)?.StopDurationSeconds ?? 0f;
            return duration > 0f ? duration : TransitModes.DefaultStopDurationSeconds;
        }

        // What one intermediate stop costs everyone riding through it: the dwell plus
        // the time lost braking from and accelerating back to cruise speed. Braking
        // from v at b covers v²/2b in v/b seconds, which at cruise would have taken
        // v/2b — so the loss is v/2b, and the same again for the acceleration. Floored
        // at DefaultStopDurationSeconds: the prefabs say a bus dwells 1 s and pulls
        // away at 6 m/s², which makes a stop nearly free and a plan call every 175 m;
        // the floor is the mod's long-standing dwell assumption (register A5.5, RF).
        public float DelayPerStopSeconds(ModePreset mode)
        {
            ModeFacts? facts = Of(mode);
            float speed = TransitModes.CruiseSpeedFor(mode);
            float acceleration = facts is not null && facts.Acceleration > 0f ? facts.Acceleration : TransitModes.DefaultAcceleration;
            float braking = facts is not null && facts.Braking > 0f ? facts.Braking : TransitModes.DefaultAcceleration;
            float physics = StopDurationFor(mode) + (speed / (2f * acceleration)) + (speed / (2f * braking));
            return Math.Max(physics, TransitModes.DefaultStopDurationSeconds);
        }
    }

    // F6 — mode choice (formal-specification.md §5): the smallest mode of the network
    // whose vehicles the candidate's own riders do not overload at the planning headway.
    internal static partial class TransitModes
    {
        // Picks the smallest mode the network can carry whose vehicles are not
        // overloaded by the riders at the mode's own headway; the largest when every
        // mode is. Whether the riders also reach the utilisation FLOOR is the set
        // selection's question, asked on the set's riders — a feeder alone rarely
        // fills anything and still belongs in the set beside its trunk. Returns false
        // only when no mode on the network has a vehicle installed.
        public static bool ChooseMode(
            RouteNetwork network,
            float ridersPerDay,
            FleetFacts facts,
            out ModePreset mode,
            out float utilisation)
        {
            ModePreset[] ladder = ModesFor(network);
            mode = ladder[0];
            utilisation = 0f;
            bool anyVehicle = false;
            for (int i = 0; i < ladder.Length; i++)
            {
                ModePreset option = ladder[i];
                float capacity = facts.CapacityFor(option);
                if (capacity <= 0f)
                {
                    continue;
                }

                anyVehicle = true;
                mode = option;
                utilisation = SuitabilityEquity.Utilisation(ridersPerDay, facts.HeadwayFor(option), capacity);
                if (utilisation <= MaxPlannedUtilisation)
                {
                    return true;
                }
            }

            return anyVehicle;
        }
    }
}
