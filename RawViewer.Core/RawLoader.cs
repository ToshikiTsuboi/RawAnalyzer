using System.IO.MemoryMappedFiles;

namespace RawViewer.Core;

/// <summary>幅×高さの解像度候補。</summary>
/// <param name="Width">幅(画素数)。</param>
/// <param name="Height">高さ(画素数)。</param>
public readonly record struct DimensionCandidate(int Width, int Height);

/// <summary>
/// RawファイルをRawImageへ読み込むローダ。
/// </summary>
public static class RawLoader
{
    /// <summary>この画素数(全フレーム合計)以下ならヒープに展開し、超過ならMemoryMappedFileを使う既定閾値。</summary>
    public const long DefaultInMemoryPixelThreshold = 100_000_000;

    private static readonly DimensionCandidate[] KnownResolutionsTable =
    {
        new(640, 480),
        new(720, 480),
        new(800, 600),
        new(1024, 768),
        new(1280, 720),
        new(1280, 800),
        new(1280, 960),
        new(1280, 1024),
        new(1440, 1080),
        new(1600, 1200),
        new(1920, 1080),
        new(1920, 1200),
        new(2048, 1080),
        new(2048, 1536),
        new(2560, 1440),
        new(2560, 1600),
        new(2592, 1944),
        new(3072, 2048),
        new(3264, 2448),
        new(3840, 2160),
        new(4000, 3000),
        new(4056, 3040),
        new(4096, 2160),
        new(4096, 3072),
        new(4608, 3456),
        new(5120, 2880),
        new(5472, 3648),
        new(6000, 4000),
        new(6144, 4096),
        new(7680, 4320),
        new(8192, 4320),
        new(8192, 6144),
        new(9152, 6944),
        new(9344, 7000),
        new(11648, 8736),
        new(13376, 9528),
        // 評価・テスト用途で一般的な正方形サイズ
        new(512, 512),
        new(1024, 1024),
        new(2048, 2048),
        new(4096, 4096),
        new(8192, 8192),
        new(16384, 16384),
        new(32768, 32768),
    };

    /// <summary>一般的なセンサ解像度の候補テーブル。</summary>
    public static IReadOnlyList<DimensionCandidate> KnownResolutions => KnownResolutionsTable;

    /// <summary>
    /// Rawファイルを読み込む。ヘッダオフセットを読み飛ばし、
    /// エンディアン変換と詰め方向の正規化を行い内部16bitフルスケール表現にする。
    /// </summary>
    /// <param name="path">Rawファイルのパス。</param>
    /// <param name="format">ファイルの解釈方法。</param>
    /// <returns>読み込まれた画像。呼び出し側でDisposeすること。</returns>
    /// <exception cref="InvalidDataException">ファイルサイズがフォーマットに対して不足している場合。</exception>
    public static RawImage Load(string path, RawFormat format)
    {
        return Load(path, format, DefaultInMemoryPixelThreshold);
    }

    /// <summary>
    /// 閾値を指定してRawファイルを読み込む。
    /// 全フレーム合計画素数が閾値以下ならヒープ(ushort[])へ展開し、
    /// 超過する場合はMemoryMappedFile経由のオンデマンド読み出しとなる。
    /// </summary>
    /// <param name="path">Rawファイルのパス。</param>
    /// <param name="format">ファイルの解釈方法。</param>
    /// <param name="inMemoryPixelThreshold">ヒープ展開する画素数の上限。</param>
    /// <returns>読み込まれた画像。呼び出し側でDisposeすること。</returns>
    /// <exception cref="InvalidDataException">ファイルサイズがフォーマットに対して不足している場合。</exception>
    public static RawImage Load(string path, RawFormat format, long inMemoryPixelThreshold)
    {
        format.Validate();
        long requiredBytes = format.HeaderOffset + format.FrameSizeInBytes * format.FrameCount;
        long fileLength = new FileInfo(path).Length;
        if (fileLength < requiredBytes)
        {
            throw new InvalidDataException(
                $"ファイルサイズ {fileLength} バイトはフォーマットが要求する {requiredBytes} バイトに足りません。");
        }

        if (format.TotalPixels <= inMemoryPixelThreshold)
        {
            return LoadInMemory(path, format);
        }

        var mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        try
        {
            MemoryMappedViewAccessor accessor =
                mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new RawImage(format, mmf, accessor);
        }
        catch
        {
            mmf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// ファイルサイズから幅×高さの候補を推定する。
    /// フォーマットのヘッダオフセット・フレーム数・画素あたりバイト数を考慮し、
    /// 一般的なセンサ解像度の候補テーブルから一致するものを返す。
    /// </summary>
    /// <param name="fileSize">ファイルの総バイト数。</param>
    /// <param name="format">ビット深度・ヘッダオフセット・フレーム数の参照元(幅/高さは無視される)。</param>
    /// <returns>一致した解像度候補のリスト。一致がなければ空。</returns>
    public static IReadOnlyList<DimensionCandidate> GuessDimensions(long fileSize, RawFormat format)
    {
        long dataBytes = fileSize - format.HeaderOffset;
        if (dataBytes <= 0)
        {
            return Array.Empty<DimensionCandidate>();
        }

        long bytesPerPixelAllFrames = (long)format.BytesPerPixel * format.FrameCount;
        if (dataBytes % bytesPerPixelAllFrames != 0)
        {
            return Array.Empty<DimensionCandidate>();
        }

        long pixelsPerFrame = dataBytes / bytesPerPixelAllFrames;
        return KnownResolutionsTable
            .Where(c => (long)c.Width * c.Height == pixelsPerFrame)
            .ToArray();
    }

    private static RawImage LoadInMemory(string path, RawFormat format)
    {
        int width = format.Width;
        int bytesPerPixel = format.BytesPerPixel;
        int rowBytes = width * bytesPerPixel;
        int totalRows = format.Height * format.FrameCount;

        byte[] raw = new byte[(long)rowBytes * totalRows];
        using (var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20))
        {
            stream.Seek(format.HeaderOffset, SeekOrigin.Begin);
            stream.ReadExactly(raw);
        }

        ushort[] pixels = new ushort[format.TotalPixels];
        Parallel.For(0, totalRows, row =>
        {
            PixelNormalizer.Normalize(
                raw.AsSpan(row * rowBytes, rowBytes),
                pixels.AsSpan(row * width, width),
                format.BitDepth, format.Packing, format.Endianness);
        });

        return new RawImage(format, pixels);
    }
}
