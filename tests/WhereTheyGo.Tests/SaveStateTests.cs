using System;
using System.Collections.Generic;
using System.IO;

namespace WhereTheyGo.Tests
{
    // The save payload: what survives a save and a load, and — the point of the
    // sectioned framing — what survives one half of the format moving on. Both kinds
    // of measurement in there cost game days to gather again, so losing them to a
    // format change is the failure these tests exist to prevent.
    internal static partial class Program
    {
        private static void SavePayloadRoundTrips()
        {
            var window = new ObservedTripWindow(Assumptions.ObservationWindowFrames);
            for (int i = 0; i < 5; i++)
            {
                window.Record(new ObservedTrip
                {
                    m_Frame = (uint)(1000 + (i * 100)),
                    m_OriginX = 10f * i,
                    m_OriginZ = -20f * i,
                    m_DestinationX = 300f + i,
                    m_DestinationZ = 400f - i,
                    m_Purpose = (byte)(i % 2 == 0 ? 3 : 7),
                    m_TimeOfDay = 0.25f + (0.1f * i),
                });
            }

            var history = new LineHistory(Assumptions.FramesPerGameDay);
            uint frame = 2000u;
            for (int i = 0; i < 4; i++)
            {
                frame += 3000u;
                history.Record(11, new LineObservation { m_Frame = frame, m_Passengers = 7 * i, m_Capacity = 80, m_IntervalSeconds = 150f, m_Vehicles = 3, m_TimeOfDay = 0.4f });
                history.Record(12, new LineObservation { m_Frame = frame + 100u, m_Passengers = 2 * i, m_Capacity = 40, m_IntervalSeconds = 200f, m_Vehicles = 1, m_TimeOfDay = 0.6f });
            }

            byte[] payload = SavePayload.Write(window, history);

            var loadedWindow = new ObservedTripWindow(Assumptions.ObservationWindowFrames);
            var loadedHistory = new LineHistory(Assumptions.FramesPerGameDay);
            SaveRestore restored = SavePayload.Read(payload, loadedWindow, loadedHistory);

            AssertEqual(5, restored.Trips, 0, "every observed journey comes back");
            AssertEqual(2, restored.Lines, 0, "both lines come back");
            AssertEqual(8, restored.Readings, 0, "every reading comes back");
            AssertEqual(0, restored.SkippedSections, 0, "a payload this build wrote has nothing to skip");

            for (int i = 0; i < 5; i++)
            {
                ObservedTrip before = window[i];
                ObservedTrip after = loadedWindow[i];
                AssertTrue(before.m_Frame == after.m_Frame, "the frame survives");
                AssertEqual(before.m_OriginX, after.m_OriginX, 0f, "the origin survives");
                AssertEqual(before.m_DestinationZ, after.m_DestinationZ, 0f, "the destination survives");
                AssertTrue(before.m_Purpose == after.m_Purpose, "the purpose survives");
                AssertEqual(before.m_TimeOfDay, after.m_TimeOfDay, 0f, "the hour survives");
            }

            AssertTrue(history.TryAverage(11, out LineAverage was) & loadedHistory.TryAverage(11, out LineAverage now),
                "line 11 has an average on both sides");
            AssertEqual(was.m_Passengers, now.m_Passengers, 0f, "the readings mean the same after a load");
            AssertEqual(was.m_Usage, now.m_Usage, 0f, "so does what they say about how full it runs");
        }

        // Nothing written and nothing held: a brand-new city must not throw its way
        // through a save.
        private static void SavePayloadHandlesAnEmptyCity()
        {
            byte[] payload = SavePayload.Write(
                new ObservedTripWindow(Assumptions.ObservationWindowFrames),
                new LineHistory(Assumptions.FramesPerGameDay));
            var window = new ObservedTripWindow(Assumptions.ObservationWindowFrames);
            var history = new LineHistory(Assumptions.FramesPerGameDay);
            SaveRestore restored = SavePayload.Read(payload, window, history);
            AssertEqual(0, restored.Trips + restored.Readings + restored.SkippedSections, 0, "nothing in, nothing out, nothing skipped");
            AssertEqual(0, window.Count, 0, "the window stays empty");
        }

        // The reason the framing exists. A payload from a build whose line readings
        // have moved on, carrying a section this build has never heard of: the observed
        // journeys — three game days of watching — still come back.
        private static void SavePayloadKeepsWhatItStillUnderstands()
        {
            var trips = new List<ObservedTrip>
            {
                new ObservedTrip { m_Frame = 500u, m_OriginX = 1f, m_OriginZ = 2f, m_DestinationX = 3f, m_DestinationZ = 4f, m_Purpose = 3, m_TimeOfDay = 0.5f },
                new ObservedTrip { m_Frame = 600u, m_OriginX = 5f, m_OriginZ = 6f, m_DestinationX = 7f, m_DestinationZ = 8f, m_Purpose = 7, m_TimeOfDay = 0.75f },
            };

            // Written by hand against the documented framing, which is the contract
            // this test pins: sectionCount, then per section id, version, length, bytes.
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(3);
                WriteTestSection(w, id: 1, version: 1, ObservedTripBytes(trips));
                // Line readings (id 2) at a version this build does not read.
                WriteTestSection(w, id: 2, version: 99, new byte[] { 1, 2, 3, 4 });
                // A section id that did not exist when this build was written.
                WriteTestSection(w, id: 77, version: 1, new byte[] { 9, 9 });
            }

            var window = new ObservedTripWindow(Assumptions.ObservationWindowFrames);
            var history = new LineHistory(Assumptions.FramesPerGameDay);
            history.Record(4, new LineObservation { m_Frame = 10u, m_Passengers = 1, m_Capacity = 10, m_IntervalSeconds = 60f, m_Vehicles = 1 });

            SaveRestore restored = SavePayload.Read(stream.ToArray(), window, history);

            AssertEqual(2, restored.Trips, 0, "the journeys are read although the readings beside them could not be");
            AssertEqual(2, restored.SkippedSections, 0, "both unreadable sections are counted, not thrown");
            AssertEqual(2, window.Count, 0, "and they are in the window");
            AssertTrue(window[1].m_Purpose == 7, "in the order they were written");
            // A section that could not be read leaves its own state alone rather than
            // clearing it: the reading this city already had is still there.
            AssertEqual(1, history.TrackedLines, 0, "an unreadable readings section does not wipe the history");
        }

        // A length the reader cannot consume must throw rather than size an array from
        // it: reading past this block corrupts the rest of the player's save.
        private static void SavePayloadRefusesBytesItCannotConsume()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(1);
                w.Write(1);
                w.Write(1);
                w.Write(int.MaxValue);
                w.Write(new byte[] { 1, 2, 3 });
            }

            AssertThrows<InvalidDataException>(
                () => SavePayload.Read(stream.ToArray(), new ObservedTripWindow(0u), new LineHistory(0u)),
                "a section longer than the payload is refused");

            using var counted = new MemoryStream();
            using (var w = new BinaryWriter(counted, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(-3);
            }

            AssertThrows<InvalidDataException>(
                () => SavePayload.Read(counted.ToArray(), new ObservedTripWindow(0u), new LineHistory(0u)),
                "a negative section count is refused");

            using var truncated = new MemoryStream();
            using (var w = new BinaryWriter(truncated, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(1);
                w.Write(1);
                w.Write(1);
                w.Write(8);
                // Says eight bytes of trips, gives a count that claims two and no trip.
                w.Write(2);
                w.Write(0);
            }

            AssertThrows<EndOfStreamException>(
                () => SavePayload.Read(truncated.ToArray(), new ObservedTripWindow(0u), new LineHistory(0u)),
                "a section that ends inside a journey is refused");
        }

        private static void WriteTestSection(BinaryWriter w, int id, int version, byte[] bytes)
        {
            w.Write(id);
            w.Write(version);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        private static byte[] ObservedTripBytes(List<ObservedTrip> trips)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
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

        private static void AssertThrows<TException>(Action action, string because)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new TestFailedException($"{because}: nothing was thrown");
        }
    }
}
