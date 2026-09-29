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
    // number did, and those journeys cost real game time to gather again. A known
    // section whose bytes do not parse is dropped the same way (Read), so a bad
    // readings section costs the readings and nothing else.
    internal static class SavePayload
    {
        // The FRAMING version: how the sections are laid out inside the payload, not
        // what any of them contains. Bumping it discards every older block whole, so it
        // moves only if this list-of-sections shape itself does. To change what a
        // section holds, bump that section's own version and leave this alone.
        //
        // FROZEN AT 4 for the first public release. Every player who has
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

        // Also the writer's cap: ReadingBytes drops lines past it, least recently
        // observed first, so a payload this build writes is one this build reads.
        internal const int MaxSavedLines = 4096;
        private const int MaxSamplesPerLine = 4096;

        // Bytes one trip occupies in the section (uint, four floats, byte, float), so a
        // claimed count can be checked against what the section could possibly hold
        // before it is used to size a list.
        private const int TripByteSize = (4 * 6) + 1;

        public static byte[] Write(ObservedTripWindow window, LineHistory history)
        {
            // Counted rather than written as a literal: a third section behind a count
            // that still said two would be written into the payload and silently
            // ignored by every reader, which is a whole feature's data lost with
            // nothing to show for it and no version to catch it.
            var sections = new List<(int Id, int Version, byte[] Bytes)>
            {
                (ObservedTripsSection, ObservedTripsVersion, TripBytes(window)),
                (LineReadingsSection, LineReadingsVersion, ReadingBytes(history)),
            };

            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(sections.Count);
                for (int i = 0; i < sections.Count; i++)
                {
                    WriteSection(w, sections[i].Id, sections[i].Version, sections[i].Bytes);
                }
            }

            return stream.ToArray();
        }

        // Restores what this build understands and leaves the rest alone.
        //
        // The FRAMING (the section count and each section's id, version and length)
        // must be consumed exactly, so a framing error throws and the caller starts
        // cold. A section's CONTENT is another matter: each restorer parses its own
        // bytes whole before it replaces the state it owns, and a known section whose
        // bytes will not parse is dropped and counted (CorruptSections) rather than
        // thrown, so the sections beside it still restore. Deserialize clears both
        // halves before calling this, so the half a dropped section would have filled
        // is empty afterwards, not the previous city's; a caller that has not cleared
        // keeps whatever that half held.
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
            int corrupt = 0;
            for (int i = 0; i < sections; i++)
            {
                int id = r.ReadInt32();
                int version = r.ReadInt32();
                byte[] bytes = SectionBytes(r, stream);
                if (!IsKnownSection(id, version))
                {
                    skipped++;
                    DeferredLog.Info(
                        $"Save state section {(id).ToString(CultureInfo.InvariantCulture)} " +
                        $"version {(version).ToString(CultureInfo.InvariantCulture)} is not one this build reads; " +
                        $"{(bytes.Length).ToString(CultureInfo.InvariantCulture)} bytes skipped, the rest of the block kept.");
                    continue;
                }

                // Only the restorers are guarded: the framing above must still throw.
                try
                {
                    if (id == ObservedTripsSection)
                    {
                        trips = RestoreTrips(bytes, window);
                    }
                    else
                    {
                        readings = RestoreReadings(bytes, history, out lines);
                    }
                }
                catch (InvalidDataException e)
                {
                    corrupt++;
                    WarnCorruptSection(id, version, e.Message);
                }
                catch (EndOfStreamException e)
                {
                    corrupt++;
                    WarnCorruptSection(id, version, e.Message);
                }
            }

            return new SaveRestore(trips, lines, readings, skipped, corrupt);
        }

        private static bool IsKnownSection(int id, int version)
        {
            return (id == ObservedTripsSection && version == ObservedTripsVersion)
                || (id == LineReadingsSection && version == LineReadingsVersion);
        }

        private static void WarnCorruptSection(int id, int version, string message)
        {
            DeferredLog.Warn(
                $"Save state section {(id).ToString(CultureInfo.InvariantCulture)} " +
                $"version {(version).ToString(CultureInfo.InvariantCulture)} could not be parsed: {message}; " +
                "its bytes skipped, the rest of the block kept.");
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
            List<int> lineIds = LinesToSave(history);
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
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

        // The lines the readings section carries: at most MaxSavedLines, which is what
        // RestoreReadings refuses more than, so the writer never produces a section the
        // reader throws away. Past the cap the LEAST RECENTLY OBSERVED lines go first
        // (by each line's newest sample frame; equal frames break on the lower line id),
        // since a line nobody has read for longest is the one whose window is closest
        // to draining anyway. The section's layout is unchanged, only its line count
        // is bounded, so LineReadingsVersion stays where it is.
        private static List<int> LinesToSave(LineHistory history)
        {
            var lineIds = new List<int>(history.LineIds);
            if (lineIds.Count <= MaxSavedLines)
            {
                return lineIds;
            }

            lineIds.Sort((a, b) =>
            {
                int byNewest = history.NewestFrameOf(b).CompareTo(history.NewestFrameOf(a));
                return byNewest != 0 ? byNewest : a.CompareTo(b);
            });
            int dropped = lineIds.Count - MaxSavedLines;
            lineIds.RemoveRange(MaxSavedLines, dropped);
            DeferredLog.Info(
                $"Save state keeps readings for {(MaxSavedLines).ToString(CultureInfo.InvariantCulture)} lines; " +
                $"{(dropped).ToString(CultureInfo.InvariantCulture)} least recently observed lines dropped from the save.");
            return lineIds;
        }

        private static int RestoreTrips(byte[] bytes, ObservedTripWindow window)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int count = ReadCount(r, Assumptions.ObservedTripCapacity);

            // The count is only a claim until the bytes back it up, and the cap alone
            // lets a four-byte field ask for a 600 000-element list out of an eight-byte
            // section. The section's own remaining length is the honest bound.
            var trips = new List<ObservedTrip>(Math.Min(count, (int)((stream.Length - stream.Position) / TripByteSize)));
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
        public SaveRestore(int trips, int lines, int readings, int skippedSections, int corruptSections)
        {
            Trips = trips;
            Lines = lines;
            Readings = readings;
            SkippedSections = skippedSections;
            CorruptSections = corruptSections;
        }

        public int Trips { get; }

        public int Lines { get; }

        public int Readings { get; }

        // Sections this build did not understand and left alone. Not a failure: it is
        // how a payload written by a newer build still gives up what it can.
        public int SkippedSections { get; }

        // Sections this build does understand whose bytes would not parse. Each cost
        // only itself; the count is here so the log says a load was partial.
        public int CorruptSections { get; }
    }
}
