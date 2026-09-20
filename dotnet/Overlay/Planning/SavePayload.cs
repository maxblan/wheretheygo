using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WhereTheyGo
{
    // The bytes the mod puts in the save, and the only thing that knows their layout.
    // WhereTheyGoSystem.SaveState does the game's IReader/IWriter dance around it.
    //
    // Pure on purpose: this is the one part of the mod that can silently lose days of
    // a player's measurements, and it can only be tested on this side of the boundary.
    //
    // The payload is a list of SECTIONS, each behind its own id, version and byte
    // length. A reader skips a section whose id it does not know or whose version has
    // moved past its own, and keeps the ones it does know. That is the whole point of
    // the shape: changing how the line readings are written must not throw away three
    // game days of observed journeys, which is exactly what one payload-wide version
    // number did, and those journeys cost real game time to gather again.
    internal static class SavePayload
    {
        // The FRAMING version: how the sections are laid out inside the payload, not
        // what any of them contains. Bumping it discards every older block whole, so it
        // moves only if this list-of-sections shape itself does. To change what a
        // section holds, bump that section's own version and leave this alone.
        //
        // FROZEN AT 4 for the first public release (2026-09-20). Every player who has
        // ever run this build has three game days of observed shopping and leisure
        // journeys and a game day of line readings behind this number, and only game
        // time can gather them again. The sectioned layout exists precisely so that
        // nothing needs to move it: a new kind of data is a NEW SECTION, and a change
        // to existing data is that section's own version. A test pins this value, so
        // changing it fails the build rather than quietly costing every player a day.
        public const int SaveFormatVersion = 4;

        // Section ids and their versions. An id is never reused for a different
        // meaning; a version says how that id's bytes are laid out.
        private const int ObservedTripsSection = 1;
        private const int ObservedTripsVersion = 1;
        private const int LineReadingsSection = 2;
        private const int LineReadingsVersion = 1;

        // Format guards, not tuning: a corrupt length must be rejected before it is
        // used to size anything. Same job as MaxSavePayloadBytes in SaveState.
        private const int MaxSections = 64;
        private const int MaxSavedLines = 4096;
        private const int MaxSamplesPerLine = 4096;

        public static byte[] Write(ObservedTripWindow window, LineHistory history)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(2);
                WriteSection(w, ObservedTripsSection, ObservedTripsVersion, TripBytes(window));
                WriteSection(w, LineReadingsSection, LineReadingsVersion, ReadingBytes(history));
            }

            return stream.ToArray();
        }

        // Restores what this build understands and leaves the rest alone. A section
        // that cannot be parsed takes only its own half of the state down with it: the
        // window and the history are replaced one at a time, each only once its own
        // bytes have been read whole.
        public static SaveRestore Read(byte[] payload, ObservedTripWindow window, LineHistory history)
        {
            if (payload is null || window is null || history is null)
            {
                throw new ArgumentNullException(payload is null ? nameof(payload) : window is null ? nameof(window) : nameof(history));
            }

            using var stream = new MemoryStream(payload, writable: false);
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int sections = ReadCount(r, MaxSections);
            int trips = 0;
            int lines = 0;
            int readings = 0;
            int skipped = 0;
            for (int i = 0; i < sections; i++)
            {
                int id = r.ReadInt32();
                int version = r.ReadInt32();
                byte[] bytes = SectionBytes(r, stream);

                if (id == ObservedTripsSection && version == ObservedTripsVersion)
                {
                    trips = RestoreTrips(bytes, window);
                }
                else if (id == LineReadingsSection && version == LineReadingsVersion)
                {
                    readings = RestoreReadings(bytes, history, out lines);
                }
                else
                {
                    skipped++;
                    DeferredLog.Info(
                        $"Save state section {(id).ToString(CultureInfo.InvariantCulture)} " +
                        $"version {(version).ToString(CultureInfo.InvariantCulture)} is not one this build reads; " +
                        $"{(bytes.Length).ToString(CultureInfo.InvariantCulture)} bytes skipped, the rest of the block kept.");
                }
            }

            return new SaveRestore(trips, lines, readings, skipped);
        }

        private static void WriteSection(BinaryWriter w, int id, int version, byte[] bytes)
        {
            w.Write(id);
            w.Write(version);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        // One section's bytes, bounded by what is actually left in the payload: a
        // length the reader cannot consume must be refused before it sizes an array.
        private static byte[] SectionBytes(BinaryReader r, MemoryStream stream)
        {
            int length = r.ReadInt32();
            long remaining = stream.Length - stream.Position;
            if (length < 0 || length > remaining)
            {
                throw new InvalidDataException(
                    $"section claims {length.ToString(CultureInfo.InvariantCulture)} bytes " +
                    $"with {remaining.ToString(CultureInfo.InvariantCulture)} left");
            }

            byte[] bytes = r.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException("section shorter than its length");
            }

            return bytes;
        }

        private static byte[] TripBytes(ObservedTripWindow window)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                IReadOnlyList<ObservedTrip> trips = window.Trips;
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
            }

            return stream.ToArray();
        }

        private static byte[] ReadingBytes(LineHistory history)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                var lineIds = new List<int>(history.LineIds);
                w.Write(lineIds.Count);
                for (int l = 0; l < lineIds.Count; l++)
                {
                    IReadOnlyList<LineObservation> samples = history.SamplesOf(lineIds[l]);
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

        private static int RestoreTrips(byte[] bytes, ObservedTripWindow window)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int count = ReadCount(r, Assumptions.ObservedTripCapacity);
            var trips = new List<ObservedTrip>(count);
            for (int i = 0; i < count; i++)
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

            // Parsed whole: only now does the live window go.
            window.Clear();
            for (int i = 0; i < trips.Count; i++)
            {
                window.Record(trips[i]);
            }

            return trips.Count;
        }

        private static int RestoreReadings(byte[] bytes, LineHistory history, out int lines)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            lines = ReadCount(r, MaxSavedLines);
            var readings = new List<(int Line, LineObservation Sample)>();
            for (int l = 0; l < lines; l++)
            {
                int lineId = r.ReadInt32();
                int sampleCount = ReadCount(r, MaxSamplesPerLine);
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

            // Frame order across lines: a sample older than the newest seen would
            // otherwise clear the window (that is how a rewound clock is detected).
            readings.Sort(static (a, b) => a.Sample.m_Frame.CompareTo(b.Sample.m_Frame));
            history.Clear();
            for (int i = 0; i < readings.Count; i++)
            {
                history.Record(readings[i].Line, readings[i].Sample);
            }

            return readings.Count;
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

    // What a load actually restored, for the line the save state logs.
    internal readonly struct SaveRestore
    {
        public SaveRestore(int trips, int lines, int readings, int skippedSections)
        {
            Trips = trips;
            Lines = lines;
            Readings = readings;
            SkippedSections = skippedSections;
        }

        public int Trips { get; }

        public int Lines { get; }

        public int Readings { get; }

        // Sections this build did not understand and left alone. Not a failure: it is
        // how a payload written by a newer build still gives up what it can.
        public int SkippedSections { get; }
    }
}
