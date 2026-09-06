using System;
using System.Collections.Generic;

namespace StationSuitabilityOverlay
{
    // One log line produced off the main thread, waiting for it.
    internal readonly struct DeferredLogLine
    {
        public DeferredLogLine(DeferredLogLevel level, string text)
        {
            Level = level;
            Text = text;
        }

        public DeferredLogLevel Level { get; }

        public string Text { get; }
    }

    internal enum DeferredLogLevel
    {
        Info,
        Warn,
        Error,
    }

    // The game's logger writes through an unguarded StreamWriter, so two threads
    // logging at once can corrupt its buffer. A worker therefore binds a buffer to
    // its own thread before it starts; every line it writes waits there until the
    // main thread adopts the worker's result and flushes them in order. On a thread
    // with no buffer bound — the main thread — the calls write straight through, so
    // code shared between the two paths logs the same way in both.
    //
    // Lives in the pure core because the alignment stage logs its diagnostics from
    // here: the game's logger is reached through Sink, which Mod.OnLoad sets. With
    // no sink — the offline harness and the verification subject — the lines are
    // dropped, unless a test binds a buffer to read them.
    internal static class DeferredLog
    {
        [ThreadStatic]
        private static List<DeferredLogLine>? s_Buffer;

        public static Action<DeferredLogLevel, string>? Sink { get; set; }

        public static void Bind(List<DeferredLogLine> buffer)
        {
            s_Buffer = buffer;
        }

        public static void Unbind()
        {
            s_Buffer = null;
        }

        public static void Info(string text)
        {
            Write(DeferredLogLevel.Info, text);
        }

        public static void Warn(string text)
        {
            Write(DeferredLogLevel.Warn, text);
        }

        public static void Error(string text)
        {
            Write(DeferredLogLevel.Error, text);
        }

        // Main thread only: hands the worker's lines to the game's logger in order.
        public static void Flush(List<DeferredLogLine> buffer)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                Emit(buffer[i].Level, buffer[i].Text);
            }

            buffer.Clear();
        }

        private static void Write(DeferredLogLevel level, string text)
        {
            List<DeferredLogLine>? buffer = s_Buffer;
            if (buffer is null)
            {
                Emit(level, text);
            }
            else
            {
                buffer.Add(new DeferredLogLine(level, text));
            }
        }

        private static void Emit(DeferredLogLevel level, string text)
        {
            Sink?.Invoke(level, text);
        }
    }
}
