using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace StationSuitabilityOverlay
{
    // Canonical JSON for the verification export, deliberately free of Unity types so
    // the harness can pin the wire format offline (see the golden-vector test).
    //
    // The format is a CONTRACT with verification/common/canonical.py, which recomputes
    // the SHA-256 over its own re-encoding of whatever it parses and refuses the file
    // when the two disagree. Matching it exactly is therefore not a style preference —
    // a divergence makes every exported instance unloadable. The rules, all from
    // Python's json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=True):
    //
    //   * object keys sorted by code point, no whitespace anywhere;
    //   * every number an INTEGER — floats travel as their binary32 bit pattern, so
    //     nothing depends on decimal formatting on either side;
    //   * non-ASCII escaped as \uXXXX with LOWERCASE hex, surrogate pairs as two
    //     escapes, which is what ensure_ascii produces for a city or line name.
    internal static class SuitabilityExportJson
    {
        // net48 has no BitConverter.SingleToUInt32Bits and the build forbids unsafe
        // blocks, so the reinterpretation goes through an explicit layout.
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float m_Value;
            [FieldOffset(0)] public uint m_Bits;
        }

        public static uint ToBits(float value)
        {
            var bits = default(FloatBits);
            bits.m_Value = value;
            return bits.m_Bits;
        }

        public static string Int(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static string Bits(float value)
        {
            return ToBits(value).ToString(CultureInfo.InvariantCulture);
        }

        public static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        public static string Null()
        {
            return "null";
        }

        public static string Str(string? value)
        {
            if (value is null)
            {
                return "null";
            }

            var builder = new StringBuilder(value.Length + 2);
            _ = builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': _ = builder.Append("\\\""); break;
                    case '\\': _ = builder.Append("\\\\"); break;
                    case '\b': _ = builder.Append("\\b"); break;
                    case '\f': _ = builder.Append("\\f"); break;
                    case '\n': _ = builder.Append("\\n"); break;
                    case '\r': _ = builder.Append("\\r"); break;
                    case '\t': _ = builder.Append("\\t"); break;
                    default:
                        if (c is < ' ' or > '~')
                        {
                            _ = builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _ = builder.Append(c);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }

        // Joins already-serialized elements. Arrays keep their order: it is part of the
        // instance, not an artifact of the writer.
        public static string Array(IReadOnlyList<string> elements)
        {
            var builder = new StringBuilder();
            _ = builder.Append('[');
            for (int i = 0; i < elements.Count; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append(',');
                }

                _ = builder.Append(elements[i]);
            }

            return builder.Append(']').ToString();
        }

        public static string BitsArray(IReadOnlyList<float> values)
        {
            var builder = new StringBuilder();
            _ = builder.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append(',');
                }

                _ = builder.Append(ToBits(values[i]).ToString(CultureInfo.InvariantCulture));
            }

            return builder.Append(']').ToString();
        }

        public static string IntArray(IReadOnlyList<int> values)
        {
            var builder = new StringBuilder();
            _ = builder.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append(',');
                }

                _ = builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }

            return builder.Append(']').ToString();
        }

        // SHA-256 over the canonical bytes, hex-encoded lowercase — the same digest
        // canonical.py computes, so a tampered file is rejected on load.
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance", "CA1850:Prefer static HashData",
            Justification = "This file compiles into the mod (net48) as well as the "
                + "test harness (net9). SHA256.HashData is .NET 5+, so the instance "
                + "method is the only form available on both.")]
        public static string Sha256Hex(string canonical)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(canonical);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    _ = builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }
    }

    // One JSON object under construction. Members may be added in any order; Build
    // sorts them, which is what keeps the canonical form independent of the order the
    // export code happens to gather things in.
    internal sealed class SuitabilityJsonObject
    {
        private readonly List<KeyValuePair<string, string>> m_Members =
            new List<KeyValuePair<string, string>>();

        public SuitabilityJsonObject Add(string key, string json)
        {
            m_Members.Add(new KeyValuePair<string, string>(key, json));
            return this;
        }

        public string Build()
        {
            // Ordinal: keys are ASCII by construction, where UTF-16 code-unit order and
            // Python's code-point order agree.
            m_Members.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

            var builder = new StringBuilder();
            _ = builder.Append('{');
            for (int i = 0; i < m_Members.Count; i++)
            {
                if (i > 0)
                {
                    _ = builder.Append(',');
                }

                _ = builder.Append(SuitabilityExportJson.Str(m_Members[i].Key))
                    .Append(':')
                    .Append(m_Members[i].Value);
            }

            return builder.Append('}').ToString();
        }

        // The instance as it goes to disk: the body plus the digest OF THE BODY, which
        // is what canonical.py re-derives by dropping the hash field again.
        //
        // The hash member is prepended textually rather than added and re-sorted: a
        // real city's body runs to tens of megabytes, and sorting and joining it a
        // second time to move one key into place would double the peak allocation for
        // no gain. Key order in the FILE is free — canonical.py re-canonicalizes
        // whatever it parses, and only that re-encoding is hashed.
        public string BuildHashed(int schemaVersion)
        {
            _ = Add("schema_version", SuitabilityExportJson.Int(schemaVersion));
            string body = Build();
            if (body.Length < 2)
            {
                return body;
            }

            string hash = SuitabilityExportJson.Sha256Hex(body);
            var builder = new StringBuilder(body.Length + 80);
            _ = builder.Append("{\"hash\":").Append(SuitabilityExportJson.Str(hash)).Append(',');
            // Append the body without its opening brace. Deliberately not Substring:
            // the body can be tens of megabytes and this avoids copying it twice.
            _ = builder.Append(body, 1, body.Length - 1);
            return builder.ToString();
        }
    }
}
