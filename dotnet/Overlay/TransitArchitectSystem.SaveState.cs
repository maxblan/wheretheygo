using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Colossal.Serialization.Entities;
using Unity.Collections;
using BinaryReader = System.IO.BinaryReader;
using BinaryWriter = System.IO.BinaryWriter;

namespace TransitArchitect
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
    public sealed partial class TransitArchitectSystem : IDefaultSerializable
    {
        private const int SaveFormatVersion = 3;
        private const int MaxSavePayloadBytes = 64 * 1024 * 1024;

        public void SetDefaults(Context context)
        {
            m_TripObserver.Window.Clear();
            m_LineHistory.Clear();
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
                $"{(m_TripObserver.Window.Count).ToString(CultureInfo.InvariantCulture)} observed journeys, " +
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
                throw new InvalidDataException($"Transit Architect save block claims {length.ToString(CultureInfo.InvariantCulture)} bytes");
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
                IReadOnlyList<ObservedTrip> trips = m_TripObserver.Window.Trips;
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
            int tripCount = ReadCount(r, Assumptions.ObservedTripCapacity);
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
            m_TripObserver.Window.Clear();
            for (int i = 0; i < trips.Count; i++)
            {
                m_TripObserver.Window.Record(trips[i]);
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
                $"Save state restored: {(trips.Count).ToString(CultureInfo.InvariantCulture)} observed journeys, " +
                $"{(lineCount).ToString(CultureInfo.InvariantCulture)} lines with {(readings.Count).ToString(CultureInfo.InvariantCulture)} readings " +
                $"spanning {(LineHistory.GameHours(m_LineHistory.SpanFrames)).ToString("F1", CultureInfo.InvariantCulture)} game hours " +
                $"of the {(LineHistory.GameHours(m_LineHistory.WindowFrames)).ToString("F0", CultureInfo.InvariantCulture)} h window");
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

    }
}
