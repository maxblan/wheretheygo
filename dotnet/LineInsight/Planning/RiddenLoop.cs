using System;
using System.Globalization;

namespace WhereTheyGo
{
    // What holding a line's ridden loop to the game's clamp decided, so the caller can
    // log it once rather than on every collection.
    internal readonly struct RiddenLoopVerdict
    {
        public RiddenLoopVerdict(bool clamped, bool intervalCapped, float riddenSeconds, float trustedSeconds, int fleetTarget)
        {
            Clamped = clamped;
            IntervalCapped = intervalCapped;
            RiddenSeconds = riddenSeconds;
            TrustedSeconds = trustedSeconds;
            FleetTarget = fleetTarget;
        }

        // Whether the line's ride times were scaled down to the trusted loop.
        public bool Clamped { get; }

        // Whether the game's interval sat at its cap, so the trusted loop is a floor
        // rather than the loop itself.
        public bool IntervalCapped { get; }

        // The loop as the route segments summed it, before any scaling.
        public float RiddenSeconds { get; }

        // interval x fleet target: the longest loop the game's own clamp admits to.
        public float TrustedSeconds { get; }

        public int FleetTarget { get; }

        public string Describe(string lineName)
        {
            string ratio = (RiddenSeconds / TrustedSeconds).ToString("F1", CultureInfo.InvariantCulture);
            string cause = IntervalCapped
                ? "The interval is at the game's cap, so the clamp is only a floor on the true loop; " +
                  "an overshoot this large is still read as an average travel time that has wrapped, and the line is charged the floor."
                : "The cause is normally an average travel time that has wrapped; the line is charged the clamped figure.";
            return $"Line \"{lineName}\": the route segments add up to a ridden loop of " +
                $"{RiddenSeconds.ToString("F0", CultureInfo.InvariantCulture)}s, {ratio}x the " +
                $"{TrustedSeconds.ToString("F0", CultureInfo.InvariantCulture)}s the game's own clamp admits " +
                $"({(TrustedSeconds / FleetTarget).ToString("F0", CultureInfo.InvariantCulture)}s interval x {FleetTarget.ToString(CultureInfo.InvariantCulture)} vehicles). " +
                cause;
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
    // is scaled down to the game's figure. AT the cap the interval only says "at least
    // this long", so a slow line honestly reads above it and is left alone; only an
    // overshoot past RiddenLoopWrapMultiple is treated as wrapped there.
    internal static class RiddenLoop
    {
        public static RiddenLoopVerdict Clamp(ExistingLine line)
        {
            if (line.m_TargetInterval <= 0f || line.m_VehicleInterval <= 0f || line.m_LineDurationSeconds <= 0f)
            {
                return new RiddenLoopVerdict(clamped: false, intervalCapped: false, line.m_LineDurationSeconds, line.m_LineDurationSeconds, 1);
            }

            // TransportLineSystem.CalculateVehicleCount, mirrored: Unity's math.round
            // is Math.Round, which rounds a tie to even.
            int fleetTarget = Math.Max(1, (int)Math.Round(line.m_StableDurationSeconds / Math.Max(1f, line.m_TargetInterval), MidpointRounding.ToEven));
            float trusted = line.m_VehicleInterval * fleetTarget;
            bool capped = line.m_VehicleInterval >= line.m_TargetInterval * Assumptions.VehicleIntervalCapMultiple;
            float allowed = capped
                ? trusted * Assumptions.RiddenLoopWrapMultiple
                : trusted + (fleetTarget * Assumptions.RiddenLoopSlackSecondsPerVehicle);
            float ridden = line.m_LineDurationSeconds;
            if (ridden <= allowed)
            {
                return new RiddenLoopVerdict(clamped: false, capped, ridden, trusted, fleetTarget);
            }

            float scale = trusted / ridden;
            for (int i = 0; i < line.m_RideSeconds.Count; i++)
            {
                line.m_RideSeconds[i] *= scale;
            }

            line.m_ClosingRideSeconds *= scale;
            line.m_LineDurationSeconds = trusted;
            return new RiddenLoopVerdict(clamped: true, capped, ridden, trusted, fleetTarget);
        }
    }
}
