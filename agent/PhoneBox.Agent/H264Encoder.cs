#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>One access unit out of the encoder: Annex B bytes, whether it starts a new picture on its own, and its time.</summary>
internal readonly record struct EncodedFrame(byte[] Data, bool Key, long TimeUs);

/// <summary>
/// H.264 from NV12 pictures with Windows' own encoders (Media Foundation): the graphics card's when there is one that
/// takes pictures from memory (Intel Quick Sync, NVIDIA NVENC, AMD VCE), else Microsoft's software encoder, which
/// every Windows 10 and 11 has. Set for live video: constant bit rate, no B frames, low latency, a key frame every two
/// seconds and on request.
/// </summary>
/// <remarks>
/// The COM interfaces are called through their tables of methods directly (<see cref="Com"/>): Media Foundation has no
/// projection in .NET, and these few calls do not justify a dependency. The slot numbers follow the order of the
/// methods in mfobjects.h / mftransform.h / strmif.h.
/// </remarks>
internal sealed unsafe class H264Encoder : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string Name { get; private set; } = "";
    public bool Hardware { get; private set; }

    private nint _mft, _events, _codec;
    private bool _async, _providesSamples, _disposed;
    private int _outSize, _needInput;
    private byte[]? _sequenceHeader;
    private readonly List<EncodedFrame> _pending = [];

    private H264Encoder(int width, int height, int fps)
    {
        Width = width;
        Height = height;
        Fps = fps;
    }

    /// <summary>An encoder for this size and rate: hardware first (unless not wanted), then software; throws when none works.</summary>
    public static H264Encoder Create(int width, int height, int fps, int bitrate, bool hardware, ILogger? log, Predicate<string>? accept = null)
    {
        Mf.Start();
        var errors = new List<string>();
        foreach (var hw in hardware ? new[] { true, false } : [false])
        {
            foreach (var activate in Mf.Encoders(hw))
            {
                var name = "";
                try
                {
                    name = Mf.FriendlyName(activate);
                    if (accept is not null && !accept(name)) continue;
                    var e = new H264Encoder(width, height, fps) { Name = name, Hardware = hw };
                    try
                    {
                        e.Open(activate, hw, bitrate);
                        log?.LogInformation("video: H.264 encoder {Name} ({Kind}), {W}x{H} at {Fps} fps, {Kbps} kbit/s",
                            name, hw ? "hardware" : "software", width, height, fps, bitrate / 1000);
                        return e;
                    }
                    catch { e.Dispose(); throw; }
                }
                catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                finally { Marshal.Release(activate); }
            }
        }
        throw new InvalidOperationException("no H.264 encoder could be opened (" + string.Join("; ", errors) + ")");
    }

    private void Open(nint activate, bool hardware, int bitrate)
    {
        var iid = Mf.IID_IMFTransform;
        nint mft;
        Com.Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Vt(activate)[33])(activate, &iid, &mft), "ActivateObject");
        _mft = mft;

        nint attrs = 0;
        if (((delegate* unmanaged<nint, nint*, int>)Com.Vt(_mft)[8])(_mft, &attrs) < 0) attrs = 0;
        if (attrs != 0)
        {
            if (hardware)
            {
                // an asynchronous encoder refuses every call until it is told the caller knows it is one;
                // hardware encoders are asynchronous by rule, and one that says otherwise is taken at its word
                _async = !(Mf.GetU32(attrs, Mf.MF_TRANSFORM_ASYNC, out var isAsync) >= 0 && isAsync == 0);
                if (_async) Com.Check(Mf.SetU32(attrs, Mf.MF_TRANSFORM_ASYNC_UNLOCK, 1), "async unlock");
            }
        }
        if (_async) _events = Com.Query(_mft, Mf.IID_IMFMediaEventGenerator);

        // the codec's own settings go in before the types: some encoders fix the rate control when the type is set
        _codec = Com.TryQuery(_mft, Mf.IID_ICodecAPI);
        if (_codec != 0)
        {
            // Low latency through the codec API only. AMD's encoder told it twice (this and MF_LOW_LATENCY on its
            // attributes) wrote streams decoders could not read (measured on an RX 550: 43 decoding errors in 90
            // frames); either one alone is clean, and this one keeps every frame out at once.
            SetCodec(Mf.CODECAPI_AVLowLatencyMode, Variant.Bool(true));
            SetCodec(Mf.CODECAPI_AVEncCommonRateControlMode, Variant.U4(0)); // CBR: an even stream suits Wi-Fi best
            SetCodec(Mf.CODECAPI_AVEncCommonMeanBitRate, Variant.U4((uint)bitrate));
            SetCodec(Mf.CODECAPI_AVEncMPVGOPSize, Variant.U4((uint)(Fps * 2)));
            SetCodec(Mf.CODECAPI_AVEncMPVDefaultBPictureCount, Variant.U4(0));
        }
        else if (attrs != 0) Mf.SetU32(attrs, Mf.MF_LOW_LATENCY, 1);
        if (attrs != 0) Marshal.Release(attrs);

        var output = Mf.NewType();
        try
        {
            Com.Check(Mf.SetGuid(output, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video), "major");
            Com.Check(Mf.SetGuid(output, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264), "subtype");
            Com.Check(Mf.SetU32(output, Mf.MF_MT_AVG_BITRATE, (uint)bitrate), "bitrate");
            Com.Check(Mf.SetU64(output, Mf.MF_MT_FRAME_SIZE, Mf.Pack(Width, Height)), "size");
            Com.Check(Mf.SetU64(output, Mf.MF_MT_FRAME_RATE, Mf.Pack(Fps, 1)), "rate");
            Com.Check(Mf.SetU64(output, Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1)), "aspect");
            Com.Check(Mf.SetU32(output, Mf.MF_MT_INTERLACE_MODE, 2), "progressive");
            // Main profile: every phone decodes it in hardware, and it compresses better than Baseline
            Mf.SetU32(output, Mf.MF_MT_MPEG2_PROFILE, 77);
            Com.Check(((delegate* unmanaged<nint, uint, nint, uint, int>)Com.Vt(_mft)[16])(_mft, 0, output, 0), "SetOutputType");
        }
        finally { Marshal.Release(output); }

        var input = Mf.NewType();
        try
        {
            Com.Check(Mf.SetGuid(input, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video), "major");
            Com.Check(Mf.SetGuid(input, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_NV12), "subtype");
            Com.Check(Mf.SetU64(input, Mf.MF_MT_FRAME_SIZE, Mf.Pack(Width, Height)), "size");
            Com.Check(Mf.SetU64(input, Mf.MF_MT_FRAME_RATE, Mf.Pack(Fps, 1)), "rate");
            Com.Check(Mf.SetU64(input, Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1)), "aspect");
            Com.Check(Mf.SetU32(input, Mf.MF_MT_INTERLACE_MODE, 2), "progressive");
            Mf.SetU32(input, Mf.MF_MT_YUV_MATRIX, 1);          // BT.709, as Nv12 writes it
            Mf.SetU32(input, Mf.MF_MT_VIDEO_NOMINAL_RANGE, 2); // 16..235
            Com.Check(((delegate* unmanaged<nint, uint, nint, uint, int>)Com.Vt(_mft)[15])(_mft, 0, input, 0), "SetInputType");
        }
        finally { Marshal.Release(input); }

        Mf.OutputStreamInfo info;
        Com.Check(((delegate* unmanaged<nint, uint, Mf.OutputStreamInfo*, int>)Com.Vt(_mft)[7])(_mft, 0, &info), "GetOutputStreamInfo");
        _providesSamples = (info.Flags & 0x300) != 0; // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | CAN_PROVIDE_SAMPLES
        _outSize = (int)Math.Max(info.Size, (uint)(Width * Height * 3 / 2));

        Message(0x10000000); // MFT_MESSAGE_NOTIFY_BEGIN_STREAMING
        Message(0x10000003); // MFT_MESSAGE_NOTIFY_START_OF_STREAM
    }

    private void SetCodec(Guid key, Variant value) =>
        ((delegate* unmanaged<nint, Guid*, Variant*, int>)Com.Vt(_codec)[9])(_codec, &key, &value);

    private void Message(uint message) =>
        Com.Check(((delegate* unmanaged<nint, uint, nuint, int>)Com.Vt(_mft)[23])(_mft, message, 0), "ProcessMessage");

    /// <summary>Asks for the next picture to be a key frame (a new viewer, or one that fell behind); false if it cannot.</summary>
    public bool RequestKeyFrame()
    {
        if (_codec == 0) return false;
        var key = Mf.CODECAPI_AVEncVideoForceKeyFrame;
        var value = Variant.U4(1);
        return ((delegate* unmanaged<nint, Guid*, Variant*, int>)Com.Vt(_codec)[9])(_codec, &key, &value) >= 0;
    }

    /// <summary>Encodes one NV12 picture; what the encoder has finished by now is added to <paramref name="output"/>.</summary>
    public void Encode(ReadOnlySpan<byte> nv12, long timeUs, List<EncodedFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sample = Mf.SampleOf(nv12, timeUs * 10, 10_000_000L / Fps);
        try
        {
            if (_async)
            {
                // the encoder says when it wants a picture; a busy one that does not ask soon costs this frame, not the
                // stream. Its answer is not waited for: Poll collects it a few milliseconds later, so the capture thread
                // is free for the next picture (an RX 550 takes some 20 ms a frame).
                var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50;
                while (_needInput == 0 && Stopwatch.GetTimestamp() < until)
                    if (!Pump()) Thread.Sleep(1);
                if (_needInput == 0) { Drain(output); return; }
                Com.Check(Input(sample), "ProcessInput");
                _needInput--;
                while (Pump()) { }
            }
            else
            {
                var hr = Input(sample);
                if (hr == Mf.MF_E_NOTACCEPTING) { PullOutputs(); hr = Input(sample); }
                Com.Check(hr, "ProcessInput");
                PullOutputs();
            }
        }
        finally { Marshal.Release(sample); }
        Drain(output);
    }

    /// <summary>What an asynchronous encoder has finished since the last call (a synchronous one hands it over in Encode).</summary>
    public void Poll(List<EncodedFrame> output)
    {
        if (_disposed || !_async) return;
        while (Pump()) { }
        Drain(output);
    }

    private int Input(nint sample) => ((delegate* unmanaged<nint, uint, nint, uint, int>)Com.Vt(_mft)[24])(_mft, 0, sample, 0);

    private void Drain(List<EncodedFrame> output)
    {
        output.AddRange(_pending);
        _pending.Clear();
    }

    /// <summary>Handles one event of an asynchronous encoder; false when there was none.</summary>
    private bool Pump()
    {
        nint ev;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Vt(_events)[3])(_events, 1u, &ev); // MF_EVENT_FLAG_NO_WAIT
        if (hr == Mf.MF_E_NO_EVENTS_AVAILABLE) return false;
        Com.Check(hr, "GetEvent");
        try
        {
            uint type;
            Com.Check(((delegate* unmanaged<nint, uint*, int>)Com.Vt(ev)[33])(ev, &type), "GetType");
            if (type == 601) _needInput++;          // METransformNeedInput
            else if (type == 602) PullOne();        // METransformHaveOutput
        }
        finally { Marshal.Release(ev); }
        return true;
    }

    private void PullOutputs()
    {
        while (PullOne()) { }
    }

    /// <summary>Takes one finished access unit from the encoder; false when it has none ready.</summary>
    private bool PullOne()
    {
        var buffer = new Mf.OutputDataBuffer();
        if (!_providesSamples) buffer.Sample = Mf.SampleOf(_outSize);
        try
        {
            uint status;
            var hr = ((delegate* unmanaged<nint, uint, uint, Mf.OutputDataBuffer*, uint*, int>)Com.Vt(_mft)[25])(_mft, 0, 1, &buffer, &status);
            if (hr == Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
            if (hr == Mf.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // the encoder settled its output format (some do on the first picture): take what it now offers
                nint type;
                Com.Check(((delegate* unmanaged<nint, uint, uint, nint*, int>)Com.Vt(_mft)[14])(_mft, 0, 0, &type), "GetOutputAvailableType");
                try { Com.Check(((delegate* unmanaged<nint, uint, nint, uint, int>)Com.Vt(_mft)[16])(_mft, 0, type, 0), "SetOutputType"); }
                finally { Marshal.Release(type); }
                _sequenceHeader = null;
                return true;
            }
            Com.Check(hr, "ProcessOutput");
            if (buffer.Sample == 0) return true;
            var bytes = Mf.BytesOf(buffer.Sample, out var time);
            var key = Mf.GetU32(buffer.Sample, Mf.MFSampleExtension_CleanPoint, out var clean) >= 0 && clean != 0;
            if (key && !VideoWire.HasSps(bytes))
            {
                // a key frame must carry the parameter sets for a viewer that starts on it: some encoders keep them in
                // the output type only
                _sequenceHeader ??= SequenceHeader();
                if (_sequenceHeader is { Length: > 0 } header) bytes = [.. header, .. bytes];
            }
            if (bytes.Length > 0) _pending.Add(new EncodedFrame(bytes, key, time / 10));
            return true;
        }
        finally
        {
            if (buffer.Sample != 0) Marshal.Release(buffer.Sample);
            if (buffer.Events != 0) Marshal.Release(buffer.Events);
        }
    }

    private byte[]? SequenceHeader()
    {
        nint type;
        if (((delegate* unmanaged<nint, uint, nint*, int>)Com.Vt(_mft)[18])(_mft, 0, &type) < 0) return null;
        try { return Mf.GetBlob(type, Mf.MF_MT_MPEG_SEQUENCE_HEADER); }
        finally { Marshal.Release(type); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mft != 0)
        {
            try { ((delegate* unmanaged<nint, uint, nuint, int>)Com.Vt(_mft)[23])(_mft, 0x10000001, 0); } catch { } // END_STREAMING
        }
        Com.Release(ref _codec);
        Com.Release(ref _events);
        Com.Release(ref _mft);
    }

    /// <summary>A VARIANT as ICodecAPI takes it: the type, then the value 8 bytes in.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct Variant
    {
        public ushort Vt;
        private ushort _r1, _r2, _r3;
        public ulong Value;
        public static Variant U4(uint v) => new() { Vt = 19, Value = v };            // VT_UI4
        public static Variant Bool(bool v) => new() { Vt = 11, Value = v ? 0xFFFFu : 0 }; // VT_BOOL, VARIANT_TRUE = -1
    }
}

/// <summary>Media Foundation: the few functions, GUIDs and attribute calls the encoder needs.</summary>
internal static unsafe class Mf
{
    internal static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    internal static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    internal static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    internal static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    internal static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    internal static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    internal static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    internal static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    internal static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    internal static readonly Guid IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
    internal static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    internal static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    internal static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    internal static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    internal static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    internal static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    internal const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    internal const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    internal const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    internal const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);

    [StructLayout(LayoutKind.Sequential)] internal struct TypeInfo { public Guid Major, Sub; }
    [StructLayout(LayoutKind.Sequential)] internal struct OutputStreamInfo { public uint Flags, Size, Alignment; }
    [StructLayout(LayoutKind.Sequential)] internal struct OutputDataBuffer { public uint StreamId; public nint Sample; public uint Status; public nint Events; }

    private static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        Com.Check(MFStartup(0x20070, 0), "MFStartup"); // MF_VERSION, MFSTARTUP_FULL
    }

    /// <summary>The H.264 encoders that take NV12, best first; the caller releases each.</summary>
    public static List<nint> Encoders(bool hardware)
    {
        var input = new TypeInfo { Major = MFMediaType_Video, Sub = MFVideoFormat_NV12 };
        var output = new TypeInfo { Major = MFMediaType_Video, Sub = MFVideoFormat_H264 };
        // HARDWARE | ASYNCMFT | SORTANDFILTER, or SYNCMFT | SORTANDFILTER
        var flags = hardware ? 0x4u | 0x2u | 0x40u : 0x1u | 0x40u;
        nint array;
        uint count;
        var category = MFT_CATEGORY_VIDEO_ENCODER;
        if (MFTEnumEx(category, flags, &input, &output, &array, &count) < 0 || array == 0) return [];
        var list = new List<nint>((int)count);
        for (var i = 0; i < count; i++) list.Add(((nint*)array)[i]);
        Marshal.FreeCoTaskMem(array);
        return list;
    }

    public static string FriendlyName(nint attributes)
    {
        var key = MFT_FRIENDLY_NAME_Attribute;
        char* text;
        uint length;
        if (((delegate* unmanaged<nint, Guid*, char**, uint*, int>)Com.Vt(attributes)[13])(attributes, &key, &text, &length) < 0) return "?";
        try { return new string(text, 0, (int)length); }
        finally { Marshal.FreeCoTaskMem((nint)text); }
    }

    public static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public static nint NewType()
    {
        nint t;
        Com.Check(MFCreateMediaType(&t), "MFCreateMediaType");
        return t;
    }

    public static int SetGuid(nint a, Guid key, Guid value) => ((delegate* unmanaged<nint, Guid*, Guid*, int>)Com.Vt(a)[24])(a, &key, &value);
    public static int SetU32(nint a, Guid key, uint value) => ((delegate* unmanaged<nint, Guid*, uint, int>)Com.Vt(a)[21])(a, &key, value);
    public static int SetU64(nint a, Guid key, ulong value) => ((delegate* unmanaged<nint, Guid*, ulong, int>)Com.Vt(a)[22])(a, &key, value);

    public static int GetU32(nint a, Guid key, out uint value)
    {
        uint v = 0;
        var hr = ((delegate* unmanaged<nint, Guid*, uint*, int>)Com.Vt(a)[7])(a, &key, &v);
        value = v;
        return hr;
    }

    public static byte[]? GetBlob(nint a, Guid key)
    {
        byte* data;
        uint size;
        if (((delegate* unmanaged<nint, Guid*, byte**, uint*, int>)Com.Vt(a)[16])(a, &key, &data, &size) < 0) return null; // GetAllocatedBlob
        try { return new ReadOnlySpan<byte>(data, (int)size).ToArray(); }
        finally { Marshal.FreeCoTaskMem((nint)data); }
    }

    /// <summary>A sample holding a copy of <paramref name="data"/>, with its time and length in 100 ns units.</summary>
    public static nint SampleOf(ReadOnlySpan<byte> data, long time, long duration)
    {
        var sample = SampleOf(data.Length);
        try
        {
            nint buffer;
            Com.Check(((delegate* unmanaged<nint, uint, nint*, int>)Com.Vt(sample)[40])(sample, 0, &buffer), "GetBufferByIndex");
            try
            {
                byte* at;
                uint max, current;
                Com.Check(((delegate* unmanaged<nint, byte**, uint*, uint*, int>)Com.Vt(buffer)[3])(buffer, &at, &max, &current), "Lock");
                data.CopyTo(new Span<byte>(at, (int)max));
                ((delegate* unmanaged<nint, int>)Com.Vt(buffer)[4])(buffer);
                Com.Check(((delegate* unmanaged<nint, uint, int>)Com.Vt(buffer)[6])(buffer, (uint)data.Length), "SetCurrentLength");
            }
            finally { Marshal.Release(buffer); }
            Com.Check(((delegate* unmanaged<nint, long, int>)Com.Vt(sample)[36])(sample, time), "SetSampleTime");
            Com.Check(((delegate* unmanaged<nint, long, int>)Com.Vt(sample)[38])(sample, duration), "SetSampleDuration");
            return sample;
        }
        catch { Marshal.Release(sample); throw; }
    }

    /// <summary>An empty sample with one buffer of <paramref name="size"/> bytes.</summary>
    public static nint SampleOf(int size)
    {
        nint sample, buffer;
        Com.Check(MFCreateSample(&sample), "MFCreateSample");
        if (MFCreateMemoryBuffer((uint)size, &buffer) < 0) { Marshal.Release(sample); throw new OutOfMemoryException("MFCreateMemoryBuffer"); }
        try { Com.Check(((delegate* unmanaged<nint, nint, int>)Com.Vt(sample)[42])(sample, buffer), "AddBuffer"); }
        catch { Marshal.Release(sample); throw; }
        finally { Marshal.Release(buffer); }
        return sample;
    }

    /// <summary>The bytes of a sample, and its time in 100 ns units.</summary>
    public static byte[] BytesOf(nint sample, out long time)
    {
        long t = 0;
        ((delegate* unmanaged<nint, long*, int>)Com.Vt(sample)[35])(sample, &t);
        time = t;
        nint buffer;
        Com.Check(((delegate* unmanaged<nint, nint*, int>)Com.Vt(sample)[41])(sample, &buffer), "ConvertToContiguousBuffer");
        try
        {
            byte* at;
            uint max, current;
            Com.Check(((delegate* unmanaged<nint, byte**, uint*, uint*, int>)Com.Vt(buffer)[3])(buffer, &at, &max, &current), "Lock");
            try { return new ReadOnlySpan<byte>(at, (int)current).ToArray(); }
            finally { ((delegate* unmanaged<nint, int>)Com.Vt(buffer)[4])(buffer); }
        }
        finally { Marshal.Release(buffer); }
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(nint* type);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(nint* sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint size, nint* buffer);
    [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, nint* activates, uint* count);
}

/// <summary>Calling COM objects through their method tables.</summary>
internal static unsafe class Com
{
    public static nint* Vt(nint obj) => *(nint**)obj;

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    public static nint Query(nint obj, Guid iid)
    {
        var q = TryQuery(obj, iid);
        return q != 0 ? q : throw new COMException($"no interface {iid}");
    }

    public static nint TryQuery(nint obj, Guid iid)
    {
        nint q = 0;
        return ((delegate* unmanaged<nint, Guid*, nint*, int>)Vt(obj)[0])(obj, &iid, &q) >= 0 ? q : 0;
    }

    public static void Release(ref nint obj)
    {
        if (obj == 0) return;
        Marshal.Release(obj);
        obj = 0;
    }
}
#endif
