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
        // TransportLineData.m_DefaultVehicleInterval: the interval a new line starts
        // with and the base the vehicle-count slider modifies (VehicleCountPolicy).
        public float PrefabIntervalSeconds;
        public float StopDurationSeconds;
        public float Acceleration;
        public float Braking;
    }

    // How a vehicle-count policy changes a line's interval. The same three modes,
    // with the same numbering, as Game.Prefabs.ModifierValueMode.
    internal enum IntervalModifierMode
    {
        Relative = 0,
        Absolute = 1,
        InverseRelative = 2,
    }

    // The game's vehicle-count slider, as the prefabs define it (decompiled 2026-09-06:
    // VehicleCountSection, RouteModifierInitializeSystem.RouteModifierRefreshData,
    // RouteUtils.ApplyModifier). The player picks a vehicle COUNT; the game turns it
    // into a slider position, the position into a modifier delta lerped over
    // [DeltaMin, DeltaMax], and the delta into the line's interval; the fleet then
    // follows from interval and round trip by TransitModes.GameFleet. The two ends of
    // the slider therefore bound the fleet a line may run (TransitModes.FleetSpan).
    internal sealed class VehicleCountPolicy
    {
        // False until the policy prefab has been read; then the span is unbounded and
        // the log says so.
        public bool Known;
        public IntervalModifierMode Mode;
        // RouteModifierData.m_Range of the VehicleInterval modifier.
        public float DeltaMin;
        public float DeltaMax;
        // PolicySliderData.m_Range, kept for the log only: the fleet ends depend on
        // the modifier range, the slider merely maps onto it linearly.
        public float SliderMin;
        public float SliderMax;
    }

    internal readonly struct FleetFacts
    {
        private readonly ModeFacts[] m_ByMode;
        private readonly VehicleCountPolicy m_Policy;

        public FleetFacts(ModeFacts[] byMode)
            : this(byMode, new VehicleCountPolicy())
        {
        }

        public FleetFacts(ModeFacts[] byMode, VehicleCountPolicy policy)
        {
            m_ByMode = byMode;
            m_Policy = policy ?? new VehicleCountPolicy();
        }

        public VehicleCountPolicy Policy => m_Policy;

        private ModeFacts? Of(ModePreset mode)
        {
            int index = (int)mode;
            return m_ByMode is not null && index >= 0 && index < m_ByMode.Length ? m_ByMode[index] : null;
        }

        public float CapacityFor(ModePreset mode)
        {
            return Of(mode)?.Capacity ?? 0f;
        }

        // The line prefab's default interval — the base of the vehicle-count slider,
        // NOT a planning headway: the fleet a line runs is bounded by the slider's
        // span (register A5.5, decided 2026-09-06), never set from a table.
        public float PrefabIntervalFor(ModePreset mode)
        {
            return Of(mode)?.PrefabIntervalSeconds ?? 0f;
        }

        // The vehicle prefab's acceleration and braking as read (0 = no prefab); the
        // delay per stop below applies the fallbacks.
        public float AccelerationFor(ModePreset mode)
        {
            return Of(mode)?.Acceleration ?? 0f;
        }

        public float BrakingFor(ModePreset mode)
        {
            return Of(mode)?.Braking ?? 0f;
        }

        public float StopDurationFor(ModePreset mode)
        {
            float duration = Of(mode)?.StopDurationSeconds ?? 0f;
            return duration > 0f ? duration : Assumptions.DefaultStopDurationSeconds;
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
            float speed = Assumptions.CruiseSpeedFor(mode);
            float acceleration = facts is not null && facts.Acceleration > 0f ? facts.Acceleration : Assumptions.DefaultAcceleration;
            float braking = facts is not null && facts.Braking > 0f ? facts.Braking : Assumptions.DefaultAcceleration;
            float physics = StopDurationFor(mode) + (speed / (2f * acceleration)) + (speed / (2f * braking));
            return Math.Max(physics, Assumptions.DefaultStopDurationSeconds);
        }

        // The fleet the game lets a line of this mode run, for its round trip
        // (TransitModes.FleetSpan on the prefab interval and the slider policy).
        public void FleetSpanFor(ModePreset mode, float roundTripSeconds, out int min, out int max)
        {
            TransitModes.FleetSpan(m_Policy, PrefabIntervalFor(mode), roundTripSeconds, out min, out max);
        }
    }

    // A fleet as the game and the planner size it: the vehicles, the interval the game
    // derives from them, the span the slider allows, and the utilisation the riders
    // give at that fleet.
    internal struct FleetPlan
    {
        public int Vehicles;
        public float HeadwaySeconds;
        public int Min;
        public int Max;
        public float Utilisation;
    }

    // F6 — the fleet arithmetic the game defines and the mode choice built on it
    // (formal-specification.md §5 v3, register A5.5/A6.x/A6.8, decided 2026-09-06).
    internal static partial class TransitModes
    {
        // TransportLineSystem.CalculateVehicleCount: the vehicles a line runs for an
        // interval and a round trip (path durations plus the dwell at every stop).
        // Unity's math.round is Math.Round without an argument, i.e. to even.
        public static int GameFleet(float intervalSeconds, float roundTripSeconds)
        {
            return Math.Max(1, (int)Math.Round(roundTripSeconds / Math.Max(1f, intervalSeconds), MidpointRounding.ToEven));
        }

        // TransportLineSystem.CalculateVehicleInterval: the interval a fleet yields.
        public static float GameInterval(float roundTripSeconds, int vehicles)
        {
            return roundTripSeconds / Math.Max(1, vehicles);
        }

        // RouteUtils.ApplyModifier after RouteModifierRefreshData.AddModifierData on a
        // fresh modifier: an absolute delta adds seconds, a relative one a share, an
        // inverse-relative one the share 1/(1+δ) − 1; then value += x, value += value·y.
        public static float IntervalAt(float prefabIntervalSeconds, IntervalModifierMode mode, float delta)
        {
            float x = 0f;
            float y = 0f;
            switch (mode)
            {
                case IntervalModifierMode.Absolute:
                    x = delta;
                    break;
                case IntervalModifierMode.InverseRelative:
                    y = (1f / Math.Max(0.001f, 1f + delta)) - 1f;
                    break;
                default:
                    y = delta;
                    break;
            }

            float value = prefabIntervalSeconds;
            value += x;
            value += value * y;
            return value;
        }

        // The fleet the slider allows a line of this round trip: the game's fleet at
        // each end of the modifier range (GetModifierDelta lerps the range by the
        // slider position, so its ends are min + 0·(max − min) and min + 1·(max − min)
        // in binary32), the smaller and the larger of the two. Unbounded above while the
        // policy is unknown, and never below one vehicle.
        public static void FleetSpan(VehicleCountPolicy? policy, float prefabIntervalSeconds, float roundTripSeconds, out int min, out int max)
        {
            if (policy is null || !policy.Known || prefabIntervalSeconds <= 0f)
            {
                min = 1;
                max = int.MaxValue;
                return;
            }

            float low = policy.DeltaMin + (0f * (policy.DeltaMax - policy.DeltaMin));
            float high = policy.DeltaMin + (1f * (policy.DeltaMax - policy.DeltaMin));
            int a = GameFleet(IntervalAt(prefabIntervalSeconds, policy.Mode, low), roundTripSeconds);
            int b = GameFleet(IntervalAt(prefabIntervalSeconds, policy.Mode, high), roundTripSeconds);
            min = Math.Min(a, b);
            max = Math.Max(a, b);
        }

        // A line's round trip as the game measures it for fleet sizing: the whole loop
        // at cruise speed plus the dwell at every stop the loop calls at
        // (TransportLineSystem.RefreshLineSegments' stableDuration).
        public static float RoundTripSeconds(float loopMetres, int loopStops, float cruiseSpeed, float delayPerStop)
        {
            return (loopMetres / Math.Max(1f, cruiseSpeed)) + (Math.Max(0, loopStops) * delayPerStop);
        }

        // The smallest fleet whose vehicles carry `peakRiders` at the target fill
        // (register A8.3): ⌈peak / (target · capacity)⌉, at least one vehicle. The
        // division is binary32, the ceiling exact.
        public static int FleetForLoad(float peakRiders, float capacityPerVehicle, float targetLoad)
        {
            if (capacityPerVehicle <= 0f || targetLoad <= 0f || peakRiders <= 0f)
            {
                return 1;
            }

            return Math.Max(1, (int)Math.Ceiling(peakRiders / (targetLoad * capacityPerVehicle)));
        }

        // The smallest fleet whose seats a day the boardings do not fill past
        // `ceiling` (A6.8): utilisation(v) = riders·T / (v·D·capacity), so
        // v ≥ riders·T / (ceiling·D·capacity), taken as ⌈(riders·T) / ((ceiling·D)·capacity)⌉
        // in double in exactly that bracketing (D = the game day's movement seconds), at
        // least one vehicle.
        public static int FleetForDemand(float ridersPerDay, float roundTripSeconds, float capacityPerVehicle, float ceiling)
        {
            if (ridersPerDay <= 0f || roundTripSeconds <= 0f || capacityPerVehicle <= 0f || ceiling <= 0f)
            {
                return 1;
            }

            double needed = ((double)ridersPerDay * roundTripSeconds) / (((double)ceiling * Assumptions.MovementSecondsPerGameDay) * capacityPerVehicle);
            return Math.Max(1, (int)Math.Ceiling(needed));
        }

        public static int Clamp(int vehicles, int min, int max)
        {
            return Math.Min(max, Math.Max(min, vehicles));
        }

        // The fleet for a line's daily boardings within the game's span, with the
        // interval that fleet yields and the utilisation the boardings give at it
        // (SuitabilityEquity.Utilisation on the derived interval).
        public static FleetPlan PlanFleet(float ridersPerDay, float roundTripSeconds, float capacityPerVehicle, int min, int max, float ceiling)
        {
            int vehicles = Clamp(FleetForDemand(ridersPerDay, roundTripSeconds, capacityPerVehicle, ceiling), min, max);
            float headway = GameInterval(roundTripSeconds, vehicles);
            return new FleetPlan
            {
                Vehicles = vehicles,
                HeadwaySeconds = headway,
                Min = min,
                Max = max,
                Utilisation = SuitabilityEquity.Utilisation(ridersPerDay, headway, capacityPerVehicle),
            };
        }

        // Picks the smallest mode the network can carry whose largest allowed fleet
        // the riders do not overload: for each rung, the fleet is sized to the riders
        // (PlanFleet) and clamped to the game's span for the mode's round trip; the
        // first rung whose utilisation at that fleet stays under the ceiling wins, the
        // largest rung when every one is overloaded. Whether the riders also reach the
        // utilisation FLOOR is the set selection's question, asked on the set's riders
        // — a feeder alone rarely fills anything and still belongs in the set beside
        // its trunk. Returns false only when no mode on the network has a vehicle
        // installed. `roundTripOf` gives the line's round trip as it would be run by
        // each mode (the caller knows the alignment and its stops).
        public static bool ChooseMode(
            RouteNetwork network,
            float ridersPerDay,
            FleetFacts facts,
            Func<ModePreset, float> roundTripOf,
            out ModePreset mode,
            out FleetPlan fleet)
        {
            ModePreset[] ladder = ModesFor(network);
            mode = ladder[0];
            fleet = default;
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
                float roundTrip = roundTripOf(option);
                facts.FleetSpanFor(option, roundTrip, out int min, out int max);
                fleet = PlanFleet(ridersPerDay, roundTrip, capacity, min, max, Assumptions.MaxPlannedUtilisation);
                if (fleet.Utilisation <= Assumptions.MaxPlannedUtilisation)
                {
                    return true;
                }
            }

            return anyVehicle;
        }
    }
}
