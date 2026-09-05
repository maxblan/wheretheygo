using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Colossal.Serialization.Entities;
using Unity.Collections;
using Unity.Mathematics;
using BinaryReader = System.IO.BinaryReader;
using BinaryWriter = System.IO.BinaryWriter;

namespace StationSuitabilityOverlay
{
    // What the mod keeps across a save and a load, so a loaded city does not start
    // cold: the observed shopping/leisure journeys (a game day to refill), the line
    // readings (a game day and four samples before a verdict uses them), and the
    // current suggestions (drawn at once; the first route pass then waits its normal
    // interval instead of running on the first frame).
    //
    // The game serialises every world system that implements IDefaultSerializable
    // (Colossal.Serialization.Entities.SystemSerializerLibrary), keyed by the
    // system's assembly-qualified type name. A save carrying this block loads fine
    // without the mod: SystemSerializer.DeserializeType logs "Not serializable type"
    // and the block is skipped whole. The block MUST be consumed exactly on load —
    // ComponentSystemSerializer throws "Data size mismatch" otherwise — so the
    // content is one length-prefixed byte payload behind a format version: any
    // version can read the length and skip, and only a matching version parses.
    public sealed partial class StationSuitabilityOverlaySystem : IDefaultSerializable
    {
        private const int SaveFormatVersion = 2;
        private const int MaxSavePayloadBytes = 64 * 1024 * 1024;
        private bool m_RestoredRoutes;

        public void SetDefaults(Context context)
        {
            m_ObservedTrips.Clear();
            m_LineHistory.Clear();
            m_Routes.Clear();
            m_RestoredRoutes = false;
            DeferredLog.Info($"Save state reset ({context.purpose})");
        }

        public void Serialize<TWriter>(TWriter writer)
            where TWriter : IWriter
        {
            byte[] payload = BuildSavePayload();
            writer.Write(SaveFormatVersion);
            writer.Write(payload.Length);
            using var bytes = new NativeArray<byte>(payload, Allocator.Temp);
            writer.Write(bytes);
            DeferredLog.Info(
                $"Save state written: {(payload.Length).ToString(CultureInfo.InvariantCulture)} bytes — " +
                $"{(m_Routes.Count).ToString(CultureInfo.InvariantCulture)} suggestions, {(m_ObservedTrips.Count).ToString(CultureInfo.InvariantCulture)} observed journeys, " +
                $"{(m_LineHistory.TrackedLines).ToString(CultureInfo.InvariantCulture)} lines of readings");
        }

        public void Deserialize<TReader>(TReader reader)
            where TReader : IReader
        {
            reader.Read(out int version);
            reader.Read(out int length);
            if (length is < 0 or > MaxSavePayloadBytes)
            {
                // Cannot be consumed exactly, so the load must fail loudly rather than
                // read garbage into the rest of the save.
                throw new InvalidDataException($"Station Suitability save block claims {length.ToString(CultureInfo.InvariantCulture)} bytes");
            }

            using var bytes = new NativeArray<byte>(length, Allocator.Temp);
            reader.Read(bytes);
            if (version != SaveFormatVersion)
            {
                DeferredLog.Info($"Save state of format {version.ToString(CultureInfo.InvariantCulture)} skipped (this build reads {SaveFormatVersion.ToString(CultureInfo.InvariantCulture)}); starting cold");
                return;
            }

            try
            {
                ReadSavePayload(bytes.ToArray());
            }
            catch (EndOfStreamException e)
            {
                DeferredLog.Warn($"Save state truncated; starting cold: {e.Message}");
                SetDefaults(default);
            }
            catch (InvalidDataException e)
            {
                DeferredLog.Warn($"Save state malformed; starting cold: {e.Message}");
                SetDefaults(default);
            }
        }

        private byte[] BuildSavePayload()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(m_Routes.Count);
                for (int r = 0; r < m_Routes.Count; r++)
                {
                    SuggestedRoute route = m_Routes[r];
                    w.Write((byte)route.Mode);
                    w.Write((byte)route.Network);
                    w.Write(route.Length);
                    w.Write(route.CapturedFlow);
                    w.Write(route.EnabledDemand);
                    w.Write(route.Vehicles);
                    w.Write(route.BentThroughHub);
                    w.Write(route.Group);
                    w.Write((byte)route.Schedule);
                    w.Write(route.DayUtilisation);
                    w.Write(route.NightUtilisation);
                    WritePoints(w, route.Path);
                    WritePoints(w, route.Stops);
                }

                IReadOnlyList<ObservedTrip> trips = m_ObservedTrips.Trips;
                w.Write(trips.Count);
                for (int i = 0; i < trips.Count; i++)
                {
                    ObservedTrip trip = trips[i];
                    w.Write(trip.m_Frame);
                    w.Write(trip.m_OriginX);
                    w.Write(trip.m_OriginZ);
                    w.Write(trip.m_DestinationX);
                    w.Write(trip.m_DestinationZ);
                    w.Write(trip.m_Purpose);
                    w.Write(trip.m_TimeOfDay);
                }

                var lineIds = new List<int>(m_LineHistory.LineIds);
                w.Write(lineIds.Count);
                for (int l = 0; l < lineIds.Count; l++)
                {
                    IReadOnlyList<LineObservation> samples = m_LineHistory.SamplesOf(lineIds[l]);
                    w.Write(lineIds[l]);
                    w.Write(samples.Count);
                    for (int i = 0; i < samples.Count; i++)
                    {
                        LineObservation sample = samples[i];
                        w.Write(sample.m_Frame);
                        w.Write(sample.m_Passengers);
                        w.Write(sample.m_Capacity);
                        w.Write(sample.m_IntervalSeconds);
                        w.Write(sample.m_Vehicles);
                        w.Write(sample.m_TimeOfDay);
                    }
                }
            }

            return stream.ToArray();
        }

        private void ReadSavePayload(byte[] payload)
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int routeCount = ReadCount(r, 1024);
            var routes = new List<SuggestedRoute>(routeCount);
            for (int i = 0; i < routeCount; i++)
            {
                var route = new SuggestedRoute
                {
                    Mode = (ModePreset)r.ReadByte(),
                    Network = (RouteNetwork)r.ReadByte(),
                    Length = r.ReadSingle(),
                    CapturedFlow = r.ReadSingle(),
                    EnabledDemand = r.ReadSingle(),
                    Vehicles = r.ReadInt32(),
                    BentThroughHub = r.ReadBoolean(),
                    Group = r.ReadInt32(),
                    Schedule = (LineSchedule)r.ReadByte(),
                    DayUtilisation = r.ReadSingle(),
                    NightUtilisation = r.ReadSingle(),
                    DemandScored = true,
                };
                ReadPoints(r, route.Path);
                ReadPoints(r, route.Stops);
                routes.Add(route);
            }

            int tripCount = ReadCount(r, ObservedTripWindow.Capacity);
            var trips = new List<ObservedTrip>(tripCount);
            for (int i = 0; i < tripCount; i++)
            {
                trips.Add(new ObservedTrip
                {
                    m_Frame = r.ReadUInt32(),
                    m_OriginX = r.ReadSingle(),
                    m_OriginZ = r.ReadSingle(),
                    m_DestinationX = r.ReadSingle(),
                    m_DestinationZ = r.ReadSingle(),
                    m_Purpose = r.ReadByte(),
                    m_TimeOfDay = r.ReadSingle(),
                });
            }

            int lineCount = ReadCount(r, 4096);
            var readings = new List<(int line, LineObservation sample)>();
            for (int l = 0; l < lineCount; l++)
            {
                int lineId = r.ReadInt32();
                int sampleCount = ReadCount(r, 4096);
                for (int i = 0; i < sampleCount; i++)
                {
                    readings.Add((lineId, new LineObservation
                    {
                        m_Frame = r.ReadUInt32(),
                        m_Passengers = r.ReadInt32(),
                        m_Capacity = r.ReadInt32(),
                        m_IntervalSeconds = r.ReadSingle(),
                        m_Vehicles = r.ReadInt32(),
                        m_TimeOfDay = r.ReadSingle(),
                    }));
                }
            }

            // Everything parsed: only now replace the live state.
            m_Routes.Clear();
            m_Routes.AddRange(routes);
            m_RestoredRoutes = routes.Count > 0;
            m_ObservedTrips.Clear();
            for (int i = 0; i < trips.Count; i++)
            {
                m_ObservedTrips.Record(trips[i]);
            }

            // Frame order across lines: a sample older than the newest seen would
            // otherwise clear the window (that is how a rewound clock is detected).
            readings.Sort(static (a, b) => a.sample.m_Frame.CompareTo(b.sample.m_Frame));
            m_LineHistory.Clear();
            for (int i = 0; i < readings.Count; i++)
            {
                m_LineHistory.Record(readings[i].line, readings[i].sample);
            }

            DeferredLog.Info(
                $"Save state restored: {(routes.Count).ToString(CultureInfo.InvariantCulture)} suggestions, {(trips.Count).ToString(CultureInfo.InvariantCulture)} observed journeys, " +
                $"{(lineCount).ToString(CultureInfo.InvariantCulture)} lines with {(readings.Count).ToString(CultureInfo.InvariantCulture)} readings");
        }

        private static int ReadCount(BinaryReader reader, int limit)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > limit)
            {
                throw new InvalidDataException($"count {count.ToString(CultureInfo.InvariantCulture)} outside 0..{limit.ToString(CultureInfo.InvariantCulture)}");
            }

            return count;
        }

        private static void WritePoints(BinaryWriter writer, List<float2> points)
        {
            writer.Write(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                writer.Write(points[i].x);
                writer.Write(points[i].y);
            }
        }

        private static void ReadPoints(BinaryReader reader, List<float2> into)
        {
            int count = ReadCount(reader, 65536);
            for (int i = 0; i < count; i++)
            {
                float x = reader.ReadSingle();
                float z = reader.ReadSingle();
                into.Add(new float2(x, z));
            }
        }

        // Restored suggestions are shown as they were; the first route pass then keeps
        // its normal interval instead of running on the first frame after a load.
        private void AnnounceRestoredRoutes()
        {
            if (!m_RestoredRoutes)
            {
                return;
            }

            m_RestoredRoutes = false;
            m_LastRoutePassStart = UnityEngine.Time.realtimeSinceStartup;
            UpdateRouteSummary(-1, -1);
            LogRoutes();
            DeferredLog.Info($"Suggestions restored from the save: {(m_Routes.Count).ToString(CultureInfo.InvariantCulture)}; the next route pass is due in {RoutePassIntervalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s");
        }
    }
}
