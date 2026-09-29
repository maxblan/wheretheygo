using System.Globalization;
using System.IO;
using Colossal.Serialization.Entities;
using Unity.Collections;

namespace WhereTheyGo
{
    // What the mod keeps across a save and a load, so a loaded city does not start
    // cold: the observed shopping/leisure journeys (three game days to refill) and the
    // line readings (a game day before the hourly chart is full). Both cost game time
    // that cannot be hurried, which is why the payload is sectioned; see SavePayload,
    // which owns the layout; this file only hands it to the game's reader and writer.
    //
    // The game serialises every world system that implements IDefaultSerializable
    // (Colossal.Serialization.Entities.SystemSerializerLibrary), keyed by the
    // system's assembly-qualified type name. A save carrying this block loads fine
    // without the mod: SystemSerializer.DeserializeType logs "Not serializable type"
    // and the block is skipped whole. The block MUST be consumed exactly on load,
    // because ComponentSystemSerializer throws "Data size mismatch" otherwise, so the
    // content is one length-prefixed byte payload behind a format version: any
    // version can read the length and skip, and only a matching version parses.
    public sealed partial class WhereTheyGoSystem : IDefaultSerializable
    {
        private const int MaxSavePayloadBytes = 64 * 1024 * 1024;

        public void SetDefaults(Context context)
        {
            ClearSaveState();
            DeferredLog.Info($"Save state reset ({context.purpose})");
        }

        // Both halves at once, always: the window and the history come from the same
        // city, and one of them holding another city's data is worse than either
        // starting empty.
        private void ClearSaveState()
        {
            m_TripObserver.Window.Clear();
            m_LineHistory.Clear();
        }

        public void Serialize<TWriter>(TWriter writer)
            where TWriter : IWriter
        {
            byte[] payload = SavePayload.Write(m_TripObserver.Window, m_LineHistory);
            writer.Write(SavePayload.SaveFormatVersion);
            writer.Write(payload.Length);
            using var bytes = new NativeArray<byte>(payload, Allocator.Temp);
            writer.Write(bytes);
            DeferredLog.Info(
                $"Save state written: {(payload.Length).ToString(CultureInfo.InvariantCulture)} bytes, " +
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
                throw new InvalidDataException($"Where They Go save block claims {length.ToString(CultureInfo.InvariantCulture)} bytes");
            }

            using var bytes = new NativeArray<byte>(length, Allocator.Temp);
            reader.Read(bytes);

            // The block is consumed; from here nothing can upset the game's reader.
            // Whatever the window and the history held belongs to the previous city (the
            // game does not call SetDefaults ahead of Deserialize when a block is
            // present), and it goes BEFORE anything is restored: a payload that gives
            // back only one half must not leave the other half showing that city.
            ClearSaveState();
            if (version != SavePayload.SaveFormatVersion)
            {
                DeferredLog.Info($"Save state of framing {version.ToString(CultureInfo.InvariantCulture)} skipped (this build reads {SavePayload.SaveFormatVersion.ToString(CultureInfo.InvariantCulture)}); starting cold");
                return;
            }

            try
            {
                // A known section whose bytes do not parse is dropped inside Read and
                // counted; only the framing itself still throws, and then cold is right.
                SaveRestore restored = SavePayload.Read(bytes.ToArray(), m_TripObserver.Window, m_LineHistory);
                DeferredLog.Info(
                    $"Save state restored: {(restored.Trips).ToString(CultureInfo.InvariantCulture)} observed journeys, " +
                    $"{(restored.Lines).ToString(CultureInfo.InvariantCulture)} lines with {(restored.Readings).ToString(CultureInfo.InvariantCulture)} readings " +
                    $"spanning {(LineHistory.GameHours(m_LineHistory.SpanFrames)).ToString("F1", CultureInfo.InvariantCulture)} game hours " +
                    $"of the {(LineHistory.GameHours(m_LineHistory.WindowFrames)).ToString("F0", CultureInfo.InvariantCulture)} h window, " +
                    $"{(restored.SkippedSections).ToString(CultureInfo.InvariantCulture)} sections skipped, " +
                    $"{(restored.CorruptSections).ToString(CultureInfo.InvariantCulture)} sections corrupt");
            }
            catch (EndOfStreamException e)
            {
                DeferredLog.Warn($"Save state framing truncated; starting cold: {e.Message}");
                ClearSaveState();
            }
            catch (InvalidDataException e)
            {
                DeferredLog.Warn($"Save state framing malformed; starting cold: {e.Message}");
                ClearSaveState();
            }
        }
    }
}
