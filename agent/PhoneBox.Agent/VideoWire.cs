using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// The Now playing screen's video stream as it travels to the phone: one long HTTP response made of messages, each a
/// 16-byte header and its payload. The phone's transport forwards a response body as it arrives, so this works over
/// USB, Wi-Fi and through a hub alike, where a WebSocket would not.
/// </summary>
/// <remarks>
/// Header: type (1 byte), flags (1 byte, bit 0 = key frame), 2 reserved, payload length (uint32 LE), time in
/// microseconds (int64 LE). Types: <see cref="Config"/> (JSON: codec, size), <see cref="Chunk"/> (one H.264 access
/// unit, Annex B), <see cref="State"/> (JSON: why there is no picture), <see cref="Ping"/> (nothing; keeps an idle
/// stream from timing out on the phone, which gives up on a silent read after a few seconds).
/// </remarks>
internal static class VideoWire
{
    internal const byte Config = 1, Chunk = 2, State = 3, Ping = 4;
    internal const int HeaderSize = 16;
    internal const string ContentType = "application/vnd.phonebox.video";

    public static byte[] Message(byte type, bool key, long timeUs, ReadOnlySpan<byte> payload)
    {
        var m = new byte[HeaderSize + payload.Length];
        m[0] = type;
        m[1] = key ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(4), (uint)payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(m.AsSpan(8), timeUs);
        payload.CopyTo(m.AsSpan(HeaderSize));
        return m;
    }

    public static byte[] Json(byte type, object value) =>
        Message(type, false, 0, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    /// <summary>The NAL units of an Annex B stream: where each one's header byte starts, its length, and its type.</summary>
    public static List<(int Start, int Length, int Type)> Nals(ReadOnlySpan<byte> s)
    {
        var starts = new List<int>();
        for (var i = 0; i + 2 < s.Length; i++)
        {
            if (s[i] != 0 || s[i + 1] != 0) continue;
            if (s[i + 2] == 1) { starts.Add(i + 3); i += 2; }
            else if (s[i + 2] == 0 && i + 3 < s.Length && s[i + 3] == 1) { starts.Add(i + 4); i += 3; }
        }
        var nals = new List<(int, int, int)>(starts.Count);
        for (var n = 0; n < starts.Count; n++)
        {
            var start = starts[n];
            var end = n + 1 < starts.Count ? starts[n + 1] - 3 : s.Length;
            // a four-byte start code leaves one more zero behind the previous unit
            while (end > start && n + 1 < starts.Count && s[end - 1] == 0) end--;
            if (start < s.Length) nals.Add((start, end - start, s[start] & 0x1f));
        }
        return nals;
    }

    public static bool HasSps(ReadOnlySpan<byte> annexB) => Nals(annexB).Any(n => n.Type == 7);

    /// <summary>
    /// The codec string a WebCodecs decoder is configured with ("avc1.42E01F"): profile, constraints and level, read
    /// from the stream's own sequence parameter set; null when there is none in this access unit.
    /// </summary>
    public static string? CodecOf(ReadOnlySpan<byte> annexB)
    {
        foreach (var (start, length, type) in Nals(annexB))
            if (type == 7 && length >= 4)
                return $"avc1.{annexB[start + 1]:X2}{annexB[start + 2]:X2}{annexB[start + 3]:X2}";
        return null;
    }
}
