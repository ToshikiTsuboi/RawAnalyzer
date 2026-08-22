using System.IO;
using System.Runtime.InteropServices;

namespace RawAnalyzer.App.Services;

/// <summary>
/// Windows標準のMedia Foundation(H.264エンコーダ)によるMP4動画の書き出し。
/// 外部ライブラリを使わないため、OSに含まれるエンコーダをCOM経由で直接使う。
/// </summary>
/// <remarks>
/// フレームはRGB24(上から下)で受け取り、内部でMedia Foundationの
/// RGB32(下から上=DIB順)へ詰め替える。呼び出しは単一スレッドから行うこと
/// (バッチのTask.Run内で生成から<see cref="Finish"/>まで完結させる)。
/// 失敗・中断時は<see cref="Dispose"/>が書きかけのファイルを削除する。
/// </remarks>
internal sealed unsafe class Mp4H264Writer : IDisposable
{
    private readonly string _path;
    private readonly int _width;
    private readonly int _height;
    private readonly long _frameDuration100Ns;
    private IMFSinkWriter? _writer;
    private readonly int _streamIndex;
    private long _frameCount;
    private bool _finished;
    private bool _startedMf;

    /// <summary>ライタを生成し、出力ファイルを作成する。</summary>
    /// <param name="path">出力先パス(.mp4)。</param>
    /// <param name="width">フレーム幅(偶数)。</param>
    /// <param name="height">フレーム高さ(偶数)。</param>
    /// <param name="fps">フレームレート。</param>
    /// <exception cref="NotSupportedException">サイズが偶数でない場合。</exception>
    /// <exception cref="IOException">エンコーダを初期化できない場合。</exception>
    public Mp4H264Writer(string path, int width, int height, int fps)
    {
        if (width <= 0 || height <= 0 || fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "サイズとfpsは正の値が必要です。");
        }

        // H.264(4:2:0)は色差サブサンプリングの都合で偶数サイズが前提
        if ((width & 1) == 1 || (height & 1) == 1)
        {
            throw new NotSupportedException(
                $"MP4(H.264)の書き出しは幅・高さが偶数である必要があります: {width}×{height}");
        }

        _path = path;
        _width = width;
        _height = height;
        _frameDuration100Ns = 10_000_000L / fps;

        try
        {
            int hr = NativeMethods.MFStartup(NativeMethods.MfVersion, 0);
            Marshal.ThrowExceptionForHR(hr);
            _startedMf = true;

            Marshal.ThrowExceptionForHR(NativeMethods.MFCreateSinkWriterFromURL(
                path, IntPtr.Zero, IntPtr.Zero, out IMFSinkWriter writer));
            _writer = writer;

            // 出力: H.264。ビットレートは約0.1bit/画素(1〜40Mbpsへクランプ)
            long pixelRate = (long)width * height * fps;
            uint bitrate = (uint)Math.Clamp(pixelRate / 10, 1_000_000, 40_000_000);

            Marshal.ThrowExceptionForHR(NativeMethods.MFCreateMediaType(out IMFMediaType outType));
            try
            {
                outType.SetGUID(NativeMethods.MF_MT_MAJOR_TYPE, NativeMethods.MFMediaType_Video);
                outType.SetGUID(NativeMethods.MF_MT_SUBTYPE, NativeMethods.MFVideoFormat_H264);
                outType.SetUINT32(NativeMethods.MF_MT_AVG_BITRATE, bitrate);
                outType.SetUINT32(NativeMethods.MF_MT_INTERLACE_MODE, 2); // Progressive
                outType.SetUINT64(
                    NativeMethods.MF_MT_FRAME_SIZE, ((ulong)(uint)width << 32) | (uint)height);
                outType.SetUINT64(
                    NativeMethods.MF_MT_FRAME_RATE, ((ulong)(uint)fps << 32) | 1);
                outType.SetUINT64(NativeMethods.MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
                writer.AddStream(outType, out _streamIndex);
            }
            finally
            {
                Marshal.ReleaseComObject(outType);
            }

            // 入力: RGB32(DIB順=下から上)。変換はシンクライタ内蔵のコンバータに任せる
            Marshal.ThrowExceptionForHR(NativeMethods.MFCreateMediaType(out IMFMediaType inType));
            try
            {
                inType.SetGUID(NativeMethods.MF_MT_MAJOR_TYPE, NativeMethods.MFMediaType_Video);
                inType.SetGUID(NativeMethods.MF_MT_SUBTYPE, NativeMethods.MFVideoFormat_RGB32);
                inType.SetUINT32(NativeMethods.MF_MT_INTERLACE_MODE, 2);
                inType.SetUINT64(
                    NativeMethods.MF_MT_FRAME_SIZE, ((ulong)(uint)width << 32) | (uint)height);
                inType.SetUINT64(
                    NativeMethods.MF_MT_FRAME_RATE, ((ulong)(uint)fps << 32) | 1);
                inType.SetUINT64(NativeMethods.MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
                writer.SetInputMediaType(_streamIndex, inType, IntPtr.Zero);
            }
            finally
            {
                Marshal.ReleaseComObject(inType);
            }

            writer.BeginWriting();
        }
        catch (COMException ex)
        {
            Dispose();
            throw new IOException(
                "MP4(H.264)エンコーダを初期化できませんでした。" +
                $"解像度({width}×{height})がエンコーダの上限を超えている可能性があります。" +
                $" (HRESULT 0x{ex.HResult:X8})", ex);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>書き込んだフレーム数。</summary>
    public long FrameCount => _frameCount;

    /// <summary>
    /// RGB24(上から下、R,G,Bの順)のフレームを1枚追加する。
    /// </summary>
    /// <param name="rgb24">width×height×3 のバッファ。</param>
    /// <exception cref="ArgumentException">バッファ長が不正な場合。</exception>
    /// <exception cref="IOException">エンコードに失敗した場合。</exception>
    public void AddFrameRgb24(ReadOnlySpan<byte> rgb24)
    {
        if (_writer is null || _finished)
        {
            throw new InvalidOperationException("ライタは閉じられています。");
        }

        if (rgb24.Length != (long)_width * _height * 3)
        {
            throw new ArgumentException("フレームバッファの長さが不正です。", nameof(rgb24));
        }

        int stride = _width * 4;
        int cb = stride * _height;
        IMFMediaBuffer? buffer = null;
        IMFSample? sample = null;
        try
        {
            Marshal.ThrowExceptionForHR(
                NativeMethods.MFCreateMemoryBuffer((uint)cb, out buffer));
            buffer.Lock(out IntPtr data, out _, out _);
            try
            {
                // RGB24(上から下) → RGB32/DIB(下から上、メモリ順 B,G,R,X)
                byte* dest = (byte*)data;
                fixed (byte* src = rgb24)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        byte* srcRow = src + (long)y * _width * 3;
                        byte* destRow = dest + (long)(_height - 1 - y) * stride;
                        for (int x = 0; x < _width; x++)
                        {
                            destRow[x * 4] = srcRow[x * 3 + 2];     // B
                            destRow[x * 4 + 1] = srcRow[x * 3 + 1]; // G
                            destRow[x * 4 + 2] = srcRow[x * 3];     // R
                            destRow[x * 4 + 3] = 0xFF;
                        }
                    }
                }
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.SetCurrentLength((uint)cb);

            Marshal.ThrowExceptionForHR(NativeMethods.MFCreateSample(out sample));
            sample.AddBuffer(buffer);
            sample.SetSampleTime(_frameCount * _frameDuration100Ns);
            sample.SetSampleDuration(_frameDuration100Ns);
            _writer.WriteSample(_streamIndex, sample);
            _frameCount++;
        }
        catch (COMException ex)
        {
            throw new IOException(
                $"MP4フレームのエンコードに失敗しました (HRESULT 0x{ex.HResult:X8})", ex);
        }
        finally
        {
            if (sample is not null)
            {
                Marshal.ReleaseComObject(sample);
            }

            if (buffer is not null)
            {
                Marshal.ReleaseComObject(buffer);
            }
        }
    }

    /// <summary>ストリームを確定してMP4を完成させる。</summary>
    /// <exception cref="IOException">確定に失敗した場合。</exception>
    public void Finish()
    {
        if (_writer is null || _finished)
        {
            return;
        }

        try
        {
            _writer.DoFinalize();
            _finished = true;
        }
        catch (COMException ex)
        {
            throw new IOException(
                $"MP4の確定に失敗しました (HRESULT 0x{ex.HResult:X8})", ex);
        }
    }

    /// <summary>
    /// リソースを解放する。<see cref="Finish"/> 前なら書きかけのファイルを削除する。
    /// </summary>
    public void Dispose()
    {
        if (_writer is not null)
        {
            Marshal.ReleaseComObject(_writer);
            _writer = null;
        }

        if (_startedMf)
        {
            NativeMethods.MFShutdown();
            _startedMf = false;
        }

        if (!_finished)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>この環境でMedia Foundationが使えるか(Server Core等では無い)。</summary>
    public static bool IsSupported()
    {
        try
        {
            int hr = NativeMethods.MFStartup(NativeMethods.MfVersion, 0);
            if (hr < 0)
            {
                return false;
            }

            NativeMethods.MFShutdown();
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        public const int MfVersion = 0x0002_0070;

        public static readonly Guid MF_MT_MAJOR_TYPE =
            new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE =
            new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MFMediaType_Video =
            new("73646976-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFVideoFormat_H264 =
            new("34363248-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFVideoFormat_RGB32 =
            new("00000016-0000-0010-8000-00aa00389b71");
        public static readonly Guid MF_MT_AVG_BITRATE =
            new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MF_MT_INTERLACE_MODE =
            new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MF_MT_FRAME_SIZE =
            new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_FRAME_RATE =
            new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO =
            new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType mediaType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample sample);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        public static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string url,
            IntPtr byteStream,
            IntPtr attributes,
            out IMFSinkWriter writer);
    }

    // 以下、必要な呼び出しだけ正しいシグネチャを持たせたCOMインターフェイス定義。
    // vtableのスロット位置(宣言順)が実体と一致していることが唯一の要件で、
    // 呼ばないメソッドはプレースホルダにしてある(呼ぶと未定義動作になるので注意)。

    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        // IMFAttributes (30スロット)
        void Slot01(); // GetItem
        void Slot02(); // GetItemType
        void Slot03(); // CompareItem
        void Slot04(); // Compare
        void Slot05(); // GetUINT32
        void Slot06(); // GetUINT64
        void Slot07(); // GetDouble
        void Slot08(); // GetGUID
        void Slot09(); // GetStringLength
        void Slot10(); // GetString
        void Slot11(); // GetAllocatedString
        void Slot12(); // GetBlobSize
        void Slot13(); // GetBlob
        void Slot14(); // GetAllocatedBlob
        void Slot15(); // GetUnknown
        void Slot16(); // SetItem
        void Slot17(); // DeleteItem
        void Slot18(); // DeleteAllItems

        void SetUINT32([In] ref Guid key, uint value);

        void SetUINT64([In] ref Guid key, ulong value);

        void Slot21(); // SetDouble

        void SetGUID([In] ref Guid key, [In] ref Guid value);

        void Slot23(); // SetString
        void Slot24(); // SetBlob
        void Slot25(); // SetUnknown
        void Slot26(); // LockStore
        void Slot27(); // UnlockStore
        void Slot28(); // GetCount
        void Slot29(); // GetItemByIndex
        void Slot30(); // CopyAllItems

        // IMFMediaType 固有 (呼ばない)
        void Slot31(); // GetMajorType
        void Slot32(); // IsCompressedFormat
        void Slot33(); // IsEqual
        void Slot34(); // GetRepresentation
        void Slot35(); // FreeRepresentation
    }

    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        // IMFAttributes (30スロット)
        void Slot01();
        void Slot02();
        void Slot03();
        void Slot04();
        void Slot05();
        void Slot06();
        void Slot07();
        void Slot08();
        void Slot09();
        void Slot10();
        void Slot11();
        void Slot12();
        void Slot13();
        void Slot14();
        void Slot15();
        void Slot16();
        void Slot17();
        void Slot18();
        void Slot19();
        void Slot20();
        void Slot21();
        void Slot22();
        void Slot23();
        void Slot24();
        void Slot25();
        void Slot26();
        void Slot27();
        void Slot28();
        void Slot29();
        void Slot30();

        // IMFSample 固有
        void Slot31(); // GetSampleFlags
        void Slot32(); // SetSampleFlags
        void Slot33(); // GetSampleTime

        void SetSampleTime(long time100Ns);

        void Slot35(); // GetSampleDuration

        void SetSampleDuration(long duration100Ns);

        void Slot37(); // GetBufferCount
        void Slot38(); // GetBufferByIndex
        void Slot39(); // ConvertToContiguousBuffer

        void AddBuffer(IMFMediaBuffer buffer);

        void Slot41(); // RemoveBufferByIndex
        void Slot42(); // RemoveAllBuffers
        void Slot43(); // GetTotalLength
        void Slot44(); // CopyToBuffer
    }

    [ComImport]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        void Lock(out IntPtr data, out uint maxLength, out uint currentLength);

        void Unlock();

        void Slot03(); // GetCurrentLength

        void SetCurrentLength(uint length);

        void Slot05(); // GetMaxLength
    }

    [ComImport]
    [Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSinkWriter
    {
        void AddStream(IMFMediaType targetMediaType, out int streamIndex);

        void SetInputMediaType(
            int streamIndex, IMFMediaType inputMediaType, IntPtr encodingParameters);

        void BeginWriting();

        void WriteSample(int streamIndex, IMFSample sample);

        void Slot05(); // SendStreamTick
        void Slot06(); // PlaceMarker
        void Slot07(); // NotifyEndOfSegment
        void Slot08(); // Flush

        void DoFinalize();

        void Slot10(); // GetServiceForStream
        void Slot11(); // GetStatistics
    }
}
