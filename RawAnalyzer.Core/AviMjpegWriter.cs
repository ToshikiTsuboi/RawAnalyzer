using System.Buffers.Binary;
using System.Text;

namespace RawAnalyzer.Core;

/// <summary>
/// MJPEG(Motion JPEG)圧縮のAVIファイルライタ。
/// フレームごとのJPEGバイト列を受け取り、標準的なRIFF/AVI構造
/// (hdrl/movi/idx1)へストリーミング書き出しする。NuGet不要の自前実装。
/// </summary>
public sealed class AviMjpegWriter : IDisposable
{
    /// <summary>
    /// 出力サイズの既定上限(2GiB)。AVIのRIFF/idx1はサイズとオフセットを32bitで持ち、
    /// 多くのリーダーがこれを符号付き32bitで扱うため安全側に倒している。
    /// </summary>
    public const long DefaultMaxFileBytes = 2L * 1024 * 1024 * 1024;

    private readonly long _maxFileBytes;
    private readonly FileStream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly List<(uint Offset, uint Size)> _index = new();

    private long _riffSizePosition;
    private long _totalFramesPosition;
    private long _streamLengthPosition;
    private long _suggestedBufferPositionAvih;
    private long _suggestedBufferPositionStrh;
    private long _moviSizePosition;
    private long _moviDataStart;
    private uint _maxFrameSize;
    private bool _finished;

    /// <summary>
    /// ライタを生成しヘッダを書き出す。
    /// </summary>
    /// <param name="path">出力先パス。</param>
    /// <param name="width">フレーム幅(px)。</param>
    /// <param name="height">フレーム高さ(px)。</param>
    /// <param name="fps">フレームレート。</param>
    /// <param name="maxFileBytes">出力サイズの上限(既定2GiB)。超えるとAddFrameが例外を投げる。</param>
    /// <exception cref="ArgumentOutOfRangeException">サイズ・fps・上限が正でない場合。</exception>
    public AviMjpegWriter(
        string path, int width, int height, int fps, long maxFileBytes = DefaultMaxFileBytes)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "フレームサイズは正の値である必要があります。");
        }

        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), "fpsは正の値である必要があります。");
        }

        if (maxFileBytes <= 0 || maxFileBytes > DefaultMaxFileBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFileBytes), "上限は正かつ2GiB以下である必要があります。");
        }

        _maxFileBytes = maxFileBytes;
        _width = width;
        _height = height;
        _fps = fps;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        WriteHeaders();
    }

    /// <summary>書き込んだフレーム数。</summary>
    public int FrameCount => _index.Count;

    /// <summary>
    /// JPEGフレームを1枚追加する。
    /// </summary>
    /// <param name="jpegBytes">JPEGエンコード済みのフレームデータ。</param>
    /// <exception cref="InvalidOperationException">Finish後に呼ばれた場合。</exception>
    /// <exception cref="NotSupportedException">
    /// 追加するとAVIの32bitサイズ上限(2GiB)を超える場合。
    /// </exception>
    public void AddFrame(ReadOnlySpan<byte> jpegBytes)
    {
        if (_finished)
        {
            throw new InvalidOperationException("Finish後にフレームは追加できません。");
        }

        // 上限を超えると idx1 のオフセットと RIFF サイズが 32bit で巻き戻り、
        // 「書き出し完了」と表示されるのに先頭数フレームしか再生できない壊れたAVIになる
        long chunkBytes = 8 + jpegBytes.Length + (jpegBytes.Length & 1);

        // idx1(16バイト/フレーム)とそのヘッダ8バイトも最終ファイルサイズに含める
        long fileAfter = _stream.Position + chunkBytes + ((_index.Count + 1) * 16L) + 8;
        if (fileAfter > _maxFileBytes)
        {
            throw new NotSupportedException(
                $"AVIのサイズ上限({_maxFileBytes:N0} バイト)を超えるため、" +
                $"これ以上フレームを追加できません(現在 {_index.Count} フレーム)。" +
                "分割して書き出してください。");
        }

        uint offset = (uint)(_stream.Position - _moviDataStart + 4);
        uint size = (uint)jpegBytes.Length;
        WriteFourCc("00dc");
        WriteU32(size);
        _stream.Write(jpegBytes);
        if ((jpegBytes.Length & 1) == 1)
        {
            _stream.WriteByte(0);
        }

        _index.Add((offset, size));
        _maxFrameSize = Math.Max(_maxFrameSize, size);
    }

    /// <summary>
    /// インデックスを書き出し、ヘッダのサイズ・フレーム数を確定する。
    /// </summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        // movi LISTサイズを確定
        long moviEnd = _stream.Position;
        uint moviSize = (uint)(moviEnd - _moviDataStart + 4);

        // idx1
        WriteFourCc("idx1");
        WriteU32((uint)(_index.Count * 16));
        foreach ((uint offset, uint size) in _index)
        {
            WriteFourCc("00dc");
            WriteU32(0x10);   // AVIIF_KEYFRAME
            WriteU32(offset);
            WriteU32(size);
        }

        long fileEnd = _stream.Position;

        PatchU32(_riffSizePosition, (uint)(fileEnd - 8));
        PatchU32(_totalFramesPosition, (uint)_index.Count);
        PatchU32(_streamLengthPosition, (uint)_index.Count);
        PatchU32(_suggestedBufferPositionAvih, _maxFrameSize);
        PatchU32(_suggestedBufferPositionStrh, _maxFrameSize);
        PatchU32(_moviSizePosition, moviSize);
        _stream.Flush();
    }

    /// <summary>
    /// 未確定ならFinishしてストリームを閉じる。
    /// </summary>
    public void Dispose()
    {
        // Finish が throw してもストリームを閉じる
        // (閉じないと FileShare.None のロックが残り、再実行が IOException になる)
        try
        {
            Finish();
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private void WriteHeaders()
    {
        WriteFourCc("RIFF");
        _riffSizePosition = _stream.Position;
        WriteU32(0);
        WriteFourCc("AVI ");

        // hdrl
        WriteFourCc("LIST");
        WriteU32(4 + 8 + 56 + 8 + 4 + 8 + 56 + 8 + 40);
        WriteFourCc("hdrl");

        // avih (MainAVIHeader)
        WriteFourCc("avih");
        WriteU32(56);
        WriteU32((uint)(1_000_000 / _fps));   // dwMicroSecPerFrame
        WriteU32(0);                          // dwMaxBytesPerSec
        WriteU32(0);                          // dwPaddingGranularity
        WriteU32(0x10);                       // dwFlags = AVIF_HASINDEX
        _totalFramesPosition = _stream.Position;
        WriteU32(0);                          // dwTotalFrames (後で確定)
        WriteU32(0);                          // dwInitialFrames
        WriteU32(1);                          // dwStreams
        _suggestedBufferPositionAvih = _stream.Position;
        WriteU32(0);                          // dwSuggestedBufferSize
        WriteU32((uint)_width);
        WriteU32((uint)_height);
        WriteU32(0);
        WriteU32(0);
        WriteU32(0);
        WriteU32(0);

        // strl
        WriteFourCc("LIST");
        WriteU32(4 + 8 + 56 + 8 + 40);
        WriteFourCc("strl");

        // strh (AVIStreamHeader)
        WriteFourCc("strh");
        WriteU32(56);
        WriteFourCc("vids");
        WriteFourCc("MJPG");
        WriteU32(0);                          // dwFlags
        WriteU32(0);                          // wPriority + wLanguage
        WriteU32(0);                          // dwInitialFrames
        WriteU32(1);                          // dwScale
        WriteU32((uint)_fps);                 // dwRate
        WriteU32(0);                          // dwStart
        _streamLengthPosition = _stream.Position;
        WriteU32(0);                          // dwLength (後で確定)
        _suggestedBufferPositionStrh = _stream.Position;
        WriteU32(0);                          // dwSuggestedBufferSize
        unchecked
        {
            WriteU32((uint)-1);               // dwQuality
        }

        WriteU32(0);                          // dwSampleSize
        WriteU16(0);                          // rcFrame.left
        WriteU16(0);                          // rcFrame.top
        WriteU16((ushort)_width);             // rcFrame.right
        WriteU16((ushort)_height);            // rcFrame.bottom

        // strf (BITMAPINFOHEADER)
        WriteFourCc("strf");
        WriteU32(40);
        WriteU32(40);                         // biSize
        WriteU32((uint)_width);
        WriteU32((uint)_height);
        WriteU16(1);                          // biPlanes
        WriteU16(24);                         // biBitCount
        WriteFourCc("MJPG");                  // biCompression
        WriteU32((uint)(_width * _height * 3));
        WriteU32(0);
        WriteU32(0);
        WriteU32(0);
        WriteU32(0);

        // movi
        WriteFourCc("LIST");
        _moviSizePosition = _stream.Position;
        WriteU32(0);                          // moviサイズ (後で確定)
        WriteFourCc("movi");
        _moviDataStart = _stream.Position;
    }

    private void WriteFourCc(string fourCc)
    {
        Span<byte> buffer = stackalloc byte[4];
        Encoding.ASCII.GetBytes(fourCc, buffer);
        _stream.Write(buffer);
    }

    private void WriteU32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    private void WriteU16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    private void PatchU32(long position, uint value)
    {
        long current = _stream.Position;
        _stream.Position = position;
        WriteU32(value);
        _stream.Position = current;
    }
}
