using System;
using System.Globalization;

namespace WhereTheyGo
{
    // What holding a line's ridden loop to the game's clamp decided, so the caller can
    // log it once rather than on every collection.
    internal readonly struct RiddenLoopVerdict
    {
        public RiddenLoopVerdict(bool clamped, bool intervalCapped, bool loopIsFloor, float riddenSeconds, float trustedSeconds, int fleetTarget)
        {
            Clamped = clamped;
            IntervalCapped = intervalCapped;
            LoopIsFloor = loopIsFloor;
            RiddenSeconds = riddenSeconds;
            TrustedSeconds = trustedSeconds;
            FleetTarget = fleetTarget;
        }

        // Whether the line's ride times were scaled down to the trusted loop.
        public bool Clamped { get; }

        // Whether the game's interval sat at its cap, so the interval says nothing
        // about the loop beyond "at least this long".
        public bool IntervalCapped { get; }

        // Whether the figure the line ends up with is a LOWER BOUND rather than a
        // measurement, which is what the panel has to say out loud. True when the
        // game's own timing for this line was unusable and the loop fell back to free
        // flow plus dwell.
        public bool LoopIsFloor { get; }

        // The loop as the route segments summed it, before any scaling.
        public float RiddenSeconds { get; }

        // What the line was actually charged: interval x fleet target where the game's
        // interval still means something, free flow plus dwell where it does not.
        public float TrustedSeconds { get; }

        public int FleetTarget { get; }

        // Whether this clamp is worth a line in the log. Clamping and saying are
        // different questions: snapping a loop three seconds over back onto the game's
        // own figure is right and costs nothing, while announcing that the line's travel
        // time has wrapped is simply false. So only a material overshoot, or a loop that
        // had to fall back to its floor and therefore changes what the player is shown.
        public bool WorthSaying => Clamped
            && (LoopIsFloor || RiddenSeconds >= TrustedSeconds * Assumptions.RiddenLoopWarnMultiple);

        public string Describe(string lineName)
        {
            string ratio = (RiddenSeconds / Math.Max(1f, TrustedSeconds)).ToString("F1", CultureInfo.InvariantCulture);
            string charged = LoopIsFloor
                ? "The interval is pinned at the game's cap, which makes it cap x target and nothing about this line, " +
                  "so the loop falls back to the floor of free flow plus dwell. Every figure from it is a LOWER BOUND: " +
                  "the ride cost the router charges, and the round trip the line's window shows."
                : "The cause is normally an average travel time that has wrapped; the line is charged the game's own figure.";
            return $"Line \"{lineName}\": the route segments add up to a ridden loop of " +
                $"{RiddenSeconds.ToString("F0", CultureInfo.InvariantCulture)}s, {ratio}x the " +
                $"{TrustedSeconds.ToString("F0", CultureInfo.InvariantCulture)}s it is charged " +
                $"(fleet target {FleetTarget.ToString(CultureInfo.InvariantCulture)} vehicles). " +
                charged;
        }
    }

    // The ridden duration of a line, held to what the game itself is willing to believe.
    //
    // RouteInfo.m_Duration is scaled by VehicleTiming.m_AverageTravelTime, and that
    // field can be garbage: RouteUtils.UpdateAverageTravelTime computes
    // (arrivalFrame - departureFrame) / 60f on two UNSIGNED frame counters, and a
    // vehicle whose next departure is scheduled ahead of now makes the subtraction
    // wrap. 2^32 / 60 = 71 582 788 seconds, and that is very nearly what a line's
    // RouteInfo durations then add up to - seen at 71 587 250 s for a six-stop metro
    // loop. Later halvings of the running average leave smaller but equally false
    // numbers behind.
    //
    // The game has the same problem and answers it by CLAMPING: the interval it
    // publishes is min(cap x target, lineDuration / fleetTarget), so
    // m_VehicleInterval x fleetTarget is the longest loop it will admit to. Below the
    // cap the two agree to within the game's one-second write hysteresis per vehicle
    // (RiddenLoopSlackSecondsPerVehicle); anything further off is a wrapped average and
    // is scaled down to the game's figure.
    //
    // AT the cap that reconstruction is worthless, and this is the part that was wrong
    // until 2026-09-20. When lineDuration has wrapped, the min picks the cap, so
    // m_VehicleInterval IS cap x target and carries nothing about this line. Charging
    // interval x fleetTarget then replaces one fabricated number with another: on
    // Buslinie 1 it read 2975 s for a 6149 m bus loop, which is 7.4 km/h, while the
    // line's own free-flow time was 280 s. Those ride times are the transit graph's
    // edge costs, so the router believed riding that bus took ten times as long as it
    // does, and the round trip in its window contradicted the free-flow figure printed
    // beside it.
    //
    // So a capped line falls back to m_StableDurationSeconds, free flow plus dwell,
    // built from PathInformation.m_Duration, which the wrap never touches. It is a
    // LOWER BOUND rather than a measurement, and LoopIsFloor says so all the way out to
    // the panel. It is also a floor worth holding everywhere: no line can drive its
    // loop faster than free flow plus the time it stands at its own stops, so the
    // trusted figure is never allowed below it.
    internal static class RiddenLoop
    {
        public static RiddenLoopVerdict Clamp(ExistingLine line)
        {
            if (line.m_TargetInterval <= 0f || line.m_VehicleInterval <= 0f || line.m_LineDurationSeconds <= 0f)
            {
                return new RiddenLoopVerdict(clamped: false, intervalCapped: false, loopIsFloor: false, line.m_LineDurationSeconds, line.m_LineDurationSeconds, 1);
            }

            // TransportLineSystem.CalculateVehicleCount, mirrored: Unity's math.round
            // is Math.Round, which rounds a tie to even.
            int fleetTarget = Math.Max(1, (int)Math.Round(line.m_StableDurationSeconds / Math.Max(1f, line.m_TargetInterval), MidpointRounding.ToEven));
            bool capped = line.m_VehicleInterval >= line.m_TargetInterval * Assumptions.VehicleIntervalCapMultiple;

            // Free flow plus dwell. Zero only if the pathfinder gave nothing for this
            // line, in which case there is no floor to hold and the old reconstruction
            // is still the best on offer.
            float stable = line.m_StableDurationSeconds;
            bool haveStable = stable > 0f;

            // At the cap the interval means nothing, so the floor IS the figure.
            // Below the cap the interval is a real measurement, unless it claims a loop
            // faster than free flow plus dwell, which no line can drive; then the floor
            // wins there too. Either way, a figure that came from the floor is a lower
            // bound and has to admit it.
            float fromInterval = line.m_VehicleInterval * fleetTarget;
            bool loopIsFloor = capped && haveStable;
            float trusted = loopIsFloor ? stable : fromInterval;
            if (haveStable && trusted < stable)
            {
                trusted = stable;
                loopIsFloor = true;
            }

            // What counts as broken is a SEPARATE question from what to charge, and
            // conflating them clamps honest lines. At the cap the interval still says
            // "the loop is at least this long", so the benchmark is the more generous of
            // the two candidates: a tram crawling at eight times its free-flow time is
            // slow, not wrapped, and must keep its own measurement. Only once the ridden
            // figure clears even that is it read as a wrapped counter, and only THEN
            // does the fabricated interval get replaced by the floor.
            float allowed = capped
                ? Math.Max(fromInterval, trusted) * Assumptions.RiddenLoopWrapMultiple
                : trusted + (fleetTarget * Assumptions.RiddenLoopSlackSecondsPerVehicle);
            float ridden = line.m_LineDurationSeconds;
            if (ridden <= allowed)
            {
                return new RiddenLoopVerdict(clamped: false, capped, loopIsFloor: false, ridden, trusted, fleetTarget);
            }

            float scale = trusted / ridden;
            for (int i = 0; i < line.m_RideSeconds.Count; i++)
            {
                line.m_RideSeconds[i] *= scale;
            }

            line.m_ClosingRideSeconds *= scale;
            line.m_LineDurationSeconds = trusted;
            return new RiddenLoopVerdict(clamped: true, capped, loopIsFloor, ridden, trusted, fleetTarget);
        }
    }
}
