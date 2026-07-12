using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace RawViewer.Core;

/// <summary>
/// 正規化済み(16bitフルスケール)のRaw画像。
/// 画素数が閾値以下の場合はヒープ上の ushort[] に展開され、
/// 超過する場合は MemoryMappedFile 経由でオンデマンド読み出しされる。
/// </summary>
public sealed unsafe class RawImage : IDisposable
{
    private readonly ushort[]? _pixels;
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly bool _needSwap;
    private byte* _mapBase;
    private bool _disposed;

    internal RawImage(RawFormat format, ushort[] pixels)
    {
        Format = format;
        _pixels = pixels;
    }

    internal RawImage(RawFormat format, MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
    {
        Format = format;
        _mmf = mmf;
        _accessor = accessor;
        _needSwap = (format.Endianness == Endianness.Big) == BitConverter.IsLittleEndian;
        byte* pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _mapBase = pointer + accessor.PointerOffset;
    }

    /// <summary>この画像のフォーマット記述子。</summary>
    public RawFormat Format { get; }

    /// <summary>画像の幅(画素数)。</summary>
    public int Width => Format.Width;

    /// <summary>画像の高さ(画素数)。</summary>
    public int Height => Format.Height;

    /// <summary>フレーム数。</summary>
    public int FrameCount => Format.FrameCount;

    /// <summary>MemoryMappedFile 経由のオンデマンド読み出しかどうか。</summary>
    public bool IsMemoryMapped => _pixels is null;

    /// <summary>
    /// 指定座標の正規化済み画素値を取得する。
    /// </summary>
    /// <param name="x">X座標(0 ≤ x &lt; Width)。</param>
    /// <param name="y">Y座標(0 ≤ y &lt; Height)。</param>
    /// <param name="frame">フレーム番号(0 ≤ frame &lt; FrameCount)。</param>
    /// <returns>16bitフルスケールの画素値。</returns>
    /// <exception cref="ArgumentOutOfRangeException">座標が範囲外の場合。</exception>
    /// <exception cref="ObjectDisposedException">破棄済みの場合。</exception>
    public ushort GetPixel(int x, int y, int frame = 0)
    {
        ThrowIfDisposed();
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if ((uint)frame >= (uint)FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (_pixels is not null)
        {
            return _pixels[((long)frame * Height + y) * Width + x];
        }

        long offset = FileByteOffset(frame, y, x);
        if (Format.BytesPerPixel == 1)
        {
            return PixelNormalizer.NormalizeValue(_mapBase[offset], Format.BitDepth, Format.Packing);
        }

        ushort container = Unsafe.ReadUnaligned<ushort>(_mapBase + offset);
        if (_needSwap)
        {
            container = BinaryPrimitives.ReverseEndianness(container);
        }

        return PixelNormalizer.NormalizeValue(container, Format.BitDepth, Format.Packing);
    }

    /// <summary>
    /// 指定矩形領域の正規化済み画素値をバッファへコピーする(行優先)。
    /// </summary>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="x">領域左端のX座標。</param>
    /// <param name="y">領域上端のY座標。</param>
    /// <param name="width">領域の幅。</param>
    /// <param name="height">領域の高さ。</param>
    /// <param name="destination">width × height 以上の長さの出力バッファ。</param>
    /// <exception cref="ArgumentOutOfRangeException">領域が画像の範囲外の場合。</exception>
    /// <exception cref="ArgumentException">destinationが短すぎる場合。</exception>
    /// <exception cref="ObjectDisposedException">破棄済みの場合。</exception>
    public void CopyRegion(int frame, int x, int y, int width, int height, Span<ushort> destination)
    {
        ThrowIfDisposed();
        if ((uint)frame >= (uint)FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (x < 0 || width <= 0 || x + width > Width)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (y < 0 || height <= 0 || y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (destination.Length < width * height)
        {
            throw new ArgumentException("出力バッファが領域の画素数より短いです。", nameof(destination));
        }

        if (_pixels is not null)
        {
            for (int row = 0; row < height; row++)
            {
                long index = ((long)frame * Height + y + row) * Width + x;
                _pixels.AsSpan((int)index, width).CopyTo(destination.Slice(row * width, width));
            }

            return;
        }

        int bytesPerPixel = Format.BytesPerPixel;
        for (int row = 0; row < height; row++)
        {
            var source = new ReadOnlySpan<byte>(
                _mapBase + FileByteOffset(frame, y + row, x), width * bytesPerPixel);
            PixelNormalizer.Normalize(
                source, destination.Slice(row * width, width),
                Format.BitDepth, Format.Packing, Format.Endianness);
        }
    }

    /// <summary>
    /// 保持しているMemoryMappedFileリソースを解放する。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_accessor is not null)
        {
            _mapBase = null;
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
        }

        _mmf?.Dispose();
    }

    private long FileByteOffset(int frame, int y, int x)
    {
        return Format.HeaderOffset
            + frame * Format.FrameSizeInBytes
            + ((long)y * Width + x) * Format.BytesPerPixel;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
