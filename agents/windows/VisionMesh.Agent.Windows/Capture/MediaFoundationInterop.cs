using System.Runtime.InteropServices;

namespace VisionMesh.Agent.Windows.Capture;

/// <summary>
/// Media Foundation COM declarations, hand-written because there is no supported managed binding
/// for MF on .NET and the alternatives all mean shipping something extra.
///
/// Using MF directly rather than driving an external tool matters here: the agent installs as a
/// single executable with no runtime dependency beyond Windows itself, it opens the camera the
/// same way the OS does (so the privacy indicator lights up as it should), and when a webcam
/// offers MJPEG natively the frames can be forwarded without ever being decoded.
///
/// The vtable order of every interface below is fixed by COM and must match mfobjects.h and
/// mfreadwrite.h exactly. Methods this agent never calls are still declared, with opaque
/// parameters, purely to keep the following slots at the right offsets.
/// </summary>
internal static class MediaFoundation
{
    public const uint MF_VERSION = 0x00020070;      // MF_SDK_VERSION 2, MF_API_VERSION 0x70
    public const uint MFSTARTUP_LITE = 1;

    /// <summary>Read from the first video stream, whatever index it happens to be.</summary>
    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;

    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    public const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000010;
    public const uint MF_SOURCE_READERF_STREAMTICK = 0x00000100;
    public const uint MF_SOURCE_READERF_ERROR = 0x00000001;

    // ---- attribute GUIDs ----
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    public static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");

    public static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid MF_READWRITE_DISABLE_CONVERTERS = new("98d5b065-1374-4847-8d5d-31520fee7156");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");

    // Video subtype GUIDs are FourCC-derived: the first field is the FourCC itself.
    public static readonly Guid MFVideoFormat_MJPG = FromFourCC("MJPG");
    public static readonly Guid MFVideoFormat_NV12 = FromFourCC("NV12");
    public static readonly Guid MFVideoFormat_YUY2 = FromFourCC("YUY2");
    public static readonly Guid MFVideoFormat_RGB32 = FromD3DFormat(22);
    public static readonly Guid MFVideoFormat_RGB24 = FromD3DFormat(20);
    public static readonly Guid MFVideoFormat_H264 = FromFourCC("H264");

    public static readonly Guid IID_IMFMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");

    /// <summary>Builds an MF video subtype GUID from a four character code.</summary>
    public static Guid FromFourCC(string fourCC)
    {
        var value = (uint)(fourCC[0] | (fourCC[1] << 8) | (fourCC[2] << 16) | (fourCC[3] << 24));
        return FromD3DFormat(value);
    }

    /// <summary>MF video subtypes share a fixed suffix: XXXXXXXX-0000-0010-8000-00AA00389B71.</summary>
    public static Guid FromD3DFormat(uint format)
        => new(format, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    /// <summary>Recovers the four character code from a video subtype, for display.</summary>
    public static string DescribeSubtype(Guid subtype)
    {
        var bytes = subtype.ToByteArray();
        var fourCC = new string(new[] { (char)bytes[0], (char)bytes[1], (char)bytes[2], (char)bytes[3] });
        if (fourCC.All(c => c is >= ' ' and <= '~')) return fourCC;

        var value = BitConverter.ToUInt32(bytes, 0);
        return value switch
        {
            20 => "RGB24",
            22 => "RGB32",
            _ => $"0x{value:X8}",
        };
    }

    // ---- exports ----
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mf.dll", ExactSpelling = true)]
    public static extern int MFEnumDeviceSources(IMFAttributes attributes, out IntPtr activateArray, out uint count);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSourceReaderFromMediaSource(
        IMFMediaSource mediaSource, IMFAttributes? attributes, out IMFSourceReader reader);

    /// <summary>Splits a packed UINT64 attribute into its high and low 32-bit halves.</summary>
    public static (uint High, uint Low) Unpack(ulong value) => ((uint)(value >> 32), (uint)(value & 0xFFFFFFFF));

    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    // Slots 1-4: never called, declared to preserve vtable offsets.
    [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType([In] ref Guid key, out int type);
    [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
    [PreserveSig] int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
    [PreserveSig] int GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint bufferSize, out uint length);
    [PreserveSig] int GetAllocatedString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out uint length);
    [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
    [PreserveSig] int GetBlob([In] ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint size);
    [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object value);

    [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem([In] ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
    [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
    [PreserveSig] int SetDouble([In] ref Guid key, double value);
    [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob([In] ref Guid key, [In] byte[] buffer, uint bufferSize);
    [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate : IMFAttributes
{
    // IMFAttributes slots repeated so this interface's own methods land at slot 31.
    [PreserveSig] new int GetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] new int GetItemType([In] ref Guid key, out int type);
    [PreserveSig] new int CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int GetUINT32([In] ref Guid key, out uint value);
    [PreserveSig] new int GetUINT64([In] ref Guid key, out ulong value);
    [PreserveSig] new int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] new int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] new int GetStringLength([In] ref Guid key, out uint length);
    [PreserveSig] new int GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint bufferSize, out uint length);
    [PreserveSig] new int GetAllocatedString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out uint length);
    [PreserveSig] new int GetBlobSize([In] ref Guid key, out uint size);
    [PreserveSig] new int GetBlob([In] ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint size);
    [PreserveSig] new int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] new int GetUnknown([In] ref Guid key, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] new int SetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] new int DeleteItem([In] ref Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32([In] ref Guid key, uint value);
    [PreserveSig] new int SetUINT64([In] ref Guid key, ulong value);
    [PreserveSig] new int SetDouble([In] ref Guid key, double value);
    [PreserveSig] new int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] new int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] new int SetBlob([In] ref Guid key, [In] byte[] buffer, uint bufferSize);
    [PreserveSig] new int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint count);
    [PreserveSig] new int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] new int CopyAllItems(IMFAttributes destination);

    [PreserveSig] int ActivateObject([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig] new int GetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] new int GetItemType([In] ref Guid key, out int type);
    [PreserveSig] new int CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int GetUINT32([In] ref Guid key, out uint value);
    [PreserveSig] new int GetUINT64([In] ref Guid key, out ulong value);
    [PreserveSig] new int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] new int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] new int GetStringLength([In] ref Guid key, out uint length);
    [PreserveSig] new int GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint bufferSize, out uint length);
    [PreserveSig] new int GetAllocatedString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out uint length);
    [PreserveSig] new int GetBlobSize([In] ref Guid key, out uint size);
    [PreserveSig] new int GetBlob([In] ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint size);
    [PreserveSig] new int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] new int GetUnknown([In] ref Guid key, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] new int SetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] new int DeleteItem([In] ref Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32([In] ref Guid key, uint value);
    [PreserveSig] new int SetUINT64([In] ref Guid key, ulong value);
    [PreserveSig] new int SetDouble([In] ref Guid key, double value);
    [PreserveSig] new int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] new int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] new int SetBlob([In] ref Guid key, [In] byte[] buffer, uint bufferSize);
    [PreserveSig] new int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint count);
    [PreserveSig] new int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] new int CopyAllItems(IMFAttributes destination);

    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
    [PreserveSig] int IsEqual(IMFMediaType mediaType, out uint flags);
    [PreserveSig] int GetRepresentation([In] Guid representation, out IntPtr value);
    [PreserveSig] int FreeRepresentation([In] Guid representation, IntPtr value);
}

[ComImport, Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaSource
{
    // IMFMediaEventGenerator, slots 1-4.
    [PreserveSig] int GetEvent(uint flags, out IntPtr mediaEvent);
    [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr result, out IntPtr mediaEvent);
    [PreserveSig] int QueueEvent(uint type, [In] ref Guid extendedType, int status, IntPtr value);

    [PreserveSig] int GetCharacteristics(out uint characteristics);
    [PreserveSig] int CreatePresentationDescriptor(out IntPtr presentationDescriptor);
    [PreserveSig] int Start(IntPtr presentationDescriptor, [In] ref Guid timeFormat, IntPtr startPosition);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
    [PreserveSig] int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [PreserveSig] int GetNativeMediaType(uint streamIndex, uint mediaTypeIndex, out IMFMediaType mediaType);
    [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
    [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
    [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, IntPtr position);
    [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex,
                                 out uint streamFlags, out long timestamp, out IMFSample? sample);
    [PreserveSig] int Flush(uint streamIndex);
    [PreserveSig] int GetServiceForStream(uint streamIndex, [In] ref Guid service, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int GetPresentationAttribute(uint streamIndex, [In] ref Guid attribute, IntPtr value);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    [PreserveSig] new int GetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] new int GetItemType([In] ref Guid key, out int type);
    [PreserveSig] new int CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] new int GetUINT32([In] ref Guid key, out uint value);
    [PreserveSig] new int GetUINT64([In] ref Guid key, out ulong value);
    [PreserveSig] new int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] new int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] new int GetStringLength([In] ref Guid key, out uint length);
    [PreserveSig] new int GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint bufferSize, out uint length);
    [PreserveSig] new int GetAllocatedString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] out string value, out uint length);
    [PreserveSig] new int GetBlobSize([In] ref Guid key, out uint size);
    [PreserveSig] new int GetBlob([In] ref Guid key, [Out] byte[] buffer, uint bufferSize, out uint size);
    [PreserveSig] new int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] new int GetUnknown([In] ref Guid key, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] new int SetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] new int DeleteItem([In] ref Guid key);
    [PreserveSig] new int DeleteAllItems();
    [PreserveSig] new int SetUINT32([In] ref Guid key, uint value);
    [PreserveSig] new int SetUINT64([In] ref Guid key, ulong value);
    [PreserveSig] new int SetDouble([In] ref Guid key, double value);
    [PreserveSig] new int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] new int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] new int SetBlob([In] ref Guid key, [In] byte[] buffer, uint bufferSize);
    [PreserveSig] new int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] new int LockStore();
    [PreserveSig] new int UnlockStore();
    [PreserveSig] new int GetCount(out uint count);
    [PreserveSig] new int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] new int CopyAllItems(IMFAttributes destination);

    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}
