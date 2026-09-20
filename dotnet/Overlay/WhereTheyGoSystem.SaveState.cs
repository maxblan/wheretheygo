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
            m_TripObserver.Window.Clear();
            m_LineHistory.Clear();
            DeferredLog.Info($"Save state reset ({context.purpose})");
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
            if (version != SavePayload.SaveFormatVersion)
            {
                DeferredLog.Info($"Save state of framing {version.ToString(CultureInfo.InvariantCulture)} skipped (this build reads {SavePayload.SaveFormatVersion.ToString(CultureInfo.InvariantCulture)}); starting cold");
                return;
            }

            try
            {
                SaveRestore restored = SavePayload.Read(bytes.ToArray(), m_TripObserver.Window, m_LineHistory);
                DeferredLog.Info(
                    $"Save state restored: {(restored.Trips).ToString(CultureInfo.InvariantCulture)} observed journeys, " +
                    $"{(restored.Lines).ToString(CultureInfo.InvariantCulture)} lines with {(restored.Readings).ToString(CultureInfo.InvariantCulture)} readings " +
                    $"spanning {(LineHistory.GameHours(m_LineHistory.SpanFrames)).ToString("F1", CultureInfo.InvariantCulture)} game hours " +
                    $"of the {(LineHistory.GameHours(m_LineHistory.WindowFrames)).ToString("F0", CultureInfo.InvariantCulture)} h window, " +
                    $"{(restored.SkippedSections).ToString(CultureInfo.InvariantCulture)} sections skipped");
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
    }
}
