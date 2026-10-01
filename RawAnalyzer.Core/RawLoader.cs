using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace RawAnalyzer.Core;

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

    /// <summary>
    /// ネットワーク上の大きなファイルを開くときに作るローカルの一時コピーのフォルダ(%TEMP%\RawAnalyzer)。
    /// </summary>
    public static string TemporaryCopyFolder => Path.Combine(Path.GetTempPath(), "RawAnalyzer");

    /// <summary>
    /// 一時コピーのフォルダに残っている、使われていない一時コピーを消す。
    /// </summary>
    /// <remarks>
    /// 一時コピーは閉じたら OS が消すように開くが、電源断などで OS が後始末できなかったものや、以前の版が
    /// 残したもの(終了時の破棄に届かなかった数GBの複製)は残り続けるので、起動時に呼ぶ。
    /// 実行中の別のインスタンスが開いている一時コピーは削除の共有を許さずに開いているので消せず、そのまま残す。
    /// </remarks>
    /// <param name="folder">一時コピーのフォルダ(通常は <see cref="TemporaryCopyFolder"/>)。</param>
    /// <returns>消したファイルの数。</returns>
    public static int DeleteUnusedTemporaryCopies(string folder)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 使用中(実行中の別のインスタンスが開いている)。次の起動に任せる
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // フォルダを列挙できない。消せた分だけ返す
        }

        return deleted;
    }

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
    /// キャンセル可能なRawファイル読み込み。
    /// </summary>
    /// <param name="path">Rawファイルのパス。</param>
    /// <param name="format">ファイルの解釈方法。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">読み込みの進捗(0〜1)。低速なストレージ向けの表示用。</param>
    /// <returns>読み込まれた画像。呼び出し側でDisposeすること。</returns>
    /// <exception cref="InvalidDataException">ファイルサイズがフォーマットに対して不足している場合。</exception>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public static RawImage Load(
        string path,
        RawFormat format,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        return Load(path, format, DefaultInMemoryPixelThreshold, cancellationToken, progress);
    }

    /// <summary>
    /// 閾値を指定してRawファイルを読み込む。
    /// 全フレーム合計画素数が閾値以下ならヒープ(ushort[])へ展開し、
    /// 超過する場合はMemoryMappedFile経由のオンデマンド読み出しとなる。
    /// </summary>
    /// <param name="path">Rawファイルのパス。</param>
    /// <param name="format">ファイルの解釈方法。</param>
    /// <param name="inMemoryPixelThreshold">ヒープ展開する画素数の上限。</param>
    /// <param name="cancellationToken">キャンセルトークン(MMF経路では無視される)。</param>
    /// <param name="progress">読み込みの進捗(0〜1)。MMF経路は即座に1.0が報告される。</param>
    /// <returns>読み込まれた画像。呼び出し側でDisposeすること。</returns>
    /// <exception cref="InvalidDataException">ファイルサイズがフォーマットに対して不足している場合。</exception>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public static RawImage Load(
        string path,
        RawFormat format,
        long inMemoryPixelThreshold,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        format.Validate();
        long requiredBytes = format.RequiredBytes();
        long fileLength = new FileInfo(path).Length;
        if (fileLength < requiredBytes)
        {
            throw new InvalidDataException(
                $"ファイルサイズ {fileLength} バイトはフォーマットが要求する {requiredBytes} バイトに足りません。");
        }

        if (format.TotalPixels <= inMemoryPixelThreshold)
        {
            return LoadInMemory(path, format, cancellationToken, progress);
        }

        // ネットワーク上のファイルは直接マップしない。表示中に接続が切れたり
        // サーバが再起動するとページインが EXCEPTION_IN_PAGE_ERROR になり、
        // .NETでは捕捉できずプロセスごと落ちる(解析セッションが全損する)。
        // かといってヒープ展開では数GBを一度に確保することになるため、
        // ローカルの一時ファイルへ写してからマップする
        //
        // 一時ファイルは閉じたら OS が消すように開いたまま(FileOptions.DeleteOnClose)マップする。RawImage の
        // 破棄に届かないまま終了した(ウィンドウの終了処理の await の後で破棄する、読み出し中で解放が遅延された)
        // 場合や異常終了でも、プロセスのハンドルが閉じられた時点で消え、数GBの複製が残り続けない
        //
        // 写すのはヘッダの後の読む範囲(全フレーム)だけ。多ページTIFFのページは HeaderOffset=ページの先頭とした
        // 1フレームとして開くので、ファイル全体(全ページ)を写すとページを送るたびにファイルサイズぶん転送する。
        // ビューの先頭は元ファイルの HeaderOffset に当たる(RawImage に mapOrigin として渡す)
        MemoryMappedFile mmf;
        string? temporaryCopyPath = null;
        long mapOrigin = 0;
        if (ForceTemporaryCopy.Value || IsNetworkPath(path))
        {
            mapOrigin = format.HeaderOffset;
            FileStream temporaryCopy = CopyToLocalTemporary(
                path, mapOrigin, requiredBytes - mapOrigin, cancellationToken, progress);
            temporaryCopyPath = temporaryCopy.Name;
            try
            {
                mmf = MemoryMappedFile.CreateFromFile(
                    temporaryCopy, mapName: null, capacity: 0, MemoryMappedFileAccess.Read,
                    HandleInheritability.None, leaveOpen: false);
            }
            catch
            {
                temporaryCopy.Dispose();
                throw;
            }
        }
        else
        {
            mmf = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        }

        try
        {
            MemoryMappedViewAccessor accessor =
                mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            // MMFはマップするだけで実データの転送は表示時に発生するため、ここで完了扱い
            progress?.Report(1.0);
            return new RawImage(format, mmf, accessor, temporaryCopyPath, mapOrigin);
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

    /// <summary>1回に読み込むバイト数の目安(チャンク境界は行に揃える)。</summary>
    private const int LoadChunkBytes = 8 << 20;

    /// <summary>
    /// 読み込みチャンク用のプール。
    /// </summary>
    /// <remarks>
    /// チャンクは数MBでLOH行き。連番再生ではフレームごとに確保・破棄され、
    /// GC停止でコマ落ちの原因になる。ArrayPool.Shared は 1MB 超を貯めないので
    /// チャンクサイズに合わせた専用プールを持つ。
    /// </remarks>
    private static readonly ArrayPool<byte> ChunkPool =
        ArrayPool<byte>.Create(LoadChunkBytes, maxArraysPerBucket: 4);

    /// <summary>
    /// テスト用: 真にすると、このフローの読み込みはローカルのファイルでもネットワーク上のファイルと同じく、
    /// マップする前にローカルの一時ファイルへ写す(ネットワーク上のパスはテストで用意できない)。
    /// </summary>
    internal static readonly AsyncLocal<bool> ForceTemporaryCopy = new();

    /// <summary>
    /// パスがネットワーク上(UNC またはネットワークドライブ)かを判定する。
    /// </summary>
    /// <remarks>
    /// デバイスパス(\\?\C:\… や \\.\C:\…)は \\ で始まるが UNC ではなく、接頭辞の後ろのドライブで判定する
    /// (<see cref="IsUncPath"/>)。判定できない場合はローカル扱い(false)にして従来どおりの経路を選ぶ。
    /// </remarks>
    /// <param name="path">対象のパス。</param>
    /// <returns>ネットワーク上ならtrue。</returns>
    public static bool IsNetworkPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (IsUncPath(full, out string? driveRoot))
            {
                return true;
            }

            return driveRoot is not null
                && new DriveInfo(driveRoot).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException
                or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 完全パスが UNC(ネットワーク上の共有)かを判定し、そうでなければドライブ文字のパスのドライブの根を返す。
    /// </summary>
    /// <remarks>
    /// UNC は \\server\share\… と、デバイスパスの形の \\?\UNC\…・\\.\UNC\…。デバイスパス(\\?\ と \\.\)は
    /// \\ で始まっても UNC ではなく、接頭辞の後ろで判断する。\\?\C:\… はドライブ C: のパス(根は C:\)。
    /// ボリューム GUID(\\?\Volume{…}\…)・パイプ・物理ドライブなどは根を返さない(ローカルのデバイスとして扱う)。
    /// ファイルシステムには触れない。
    /// </remarks>
    /// <param name="fullPath">完全パス(<see cref="Path.GetFullPath(string)"/> の結果)。</param>
    /// <param name="driveRoot">UNC でなくドライブ文字のパスなら、そのドライブの根(C:\)。それ以外は null。</param>
    /// <returns>UNC なら true。</returns>
    internal static bool IsUncPath(string fullPath, out string? driveRoot)
    {
        driveRoot = null;
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            string target = fullPath[4..];
            if (target.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (target.Length >= 2 && char.IsAsciiLetter(target[0]) && target[1] == ':')
            {
                driveRoot = target[..2] + @"\";
            }

            return false;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true; // \\server\share\…
        }

        string? root = Path.GetPathRoot(fullPath);
        driveRoot = string.IsNullOrEmpty(root) ? null : root;
        return false;
    }

    /// <summary>
    /// ネットワーク上のファイルの指定範囲をローカルの一時ファイルへ複製する。
    /// </summary>
    /// <remarks>
    /// 複製先は閉じたら OS が消すように開き(<see cref="FileOptions.DeleteOnClose"/>)、閉じずに返す。
    /// 呼び出し側はこのストリームをマップに渡して開いたままにし、画像の破棄でマップと一緒に閉じる
    /// (閉じた時点で消える。破棄に届かないままプロセスが終わっても、OS がハンドルを閉じて消す)。
    /// </remarks>
    /// <param name="path">元のパス。</param>
    /// <param name="offset">複製する範囲の先頭(元ファイル上の位置)。</param>
    /// <param name="length">複製するバイト数。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">転送の進捗(0〜1)。</param>
    /// <returns>複製先の開いたストリーム(呼び出し側が寿命を持つ)。</returns>
    private static FileStream CopyToLocalTemporary(
        string path, long offset, long length, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        string directory = TemporaryCopyFolder;
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(
            directory, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        var target = new FileStream(
            destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.DeleteOnClose);
        try
        {
            using var source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
            byte[] buffer = ChunkPool.Rent(LoadChunkBytes);
            try
            {
                source.Seek(offset, SeekOrigin.Begin);
                long done = 0;
                double lastReported = -1;
                while (done < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = (int)Math.Min(buffer.Length, length - done);
                    source.ReadExactly(buffer, 0, read);
                    target.Write(buffer, 0, read);
                    done += read;
                    double ratio = (double)done / Math.Max(1, length);
                    if (ratio - lastReported >= 0.01 || done == length)
                    {
                        lastReported = ratio;
                        progress?.Report(ratio);
                    }
                }
            }
            finally
            {
                ChunkPool.Return(buffer);
            }

            // マップはこのストリームのハンドルで作るので、書き込みのバッファを先にファイルへ出す
            target.Flush();
            return target;
        }
        catch
        {
            target.Dispose(); // 閉じれば消える
            throw;
        }
    }

    private static RawImage LoadInMemory(
        string path, RawFormat format, CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        int width = format.Width;
        int rowBytes = width * format.BytesPerPixel;

        // 高さ×フレーム数はintに収まらないことがある(ギガピクセル・多フレーム)
        long totalRows = (long)format.Height * format.FrameCount;

        // ファイル全体の byte[] と ushort[] を同時に持つとピークが約2倍になる
        // (16bit・1億画素で約400MB)。数MBのチャンクへストリーミングする
        int rowsPerChunk = Math.Max(1, LoadChunkBytes / Math.Max(1, rowBytes));
        int chunkBytes = (int)((long)rowBytes * Math.Min(rowsPerChunk, totalRows));
        ushort[] pixels = new ushort[format.TotalPixels];
        byte[] chunk = ChunkPool.Rent(chunkBytes);
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
            stream.Seek(format.HeaderOffset, SeekOrigin.Begin);

            for (long firstRow = 0; firstRow < totalRows; firstRow += rowsPerChunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rows = (int)Math.Min(rowsPerChunk, totalRows - firstRow);
                stream.ReadExactly(chunk, 0, rows * rowBytes);

                long chunkFirstRow = firstRow;
                Parallel.For(
                    0,
                    rows,
                    new ParallelOptions { CancellationToken = cancellationToken },
                    row =>
                    {
                        PixelNormalizer.Normalize(
                            chunk.AsSpan(row * rowBytes, rowBytes),
                            pixels.AsSpan((int)((chunkFirstRow + row) * width), width),
                            format.BitDepth, format.Packing, format.Endianness);
                    });

                // チャンク(既定8MB)単位の報告なので、UIスレッドを圧迫する頻度にはならない
                progress?.Report((double)(firstRow + rows) / totalRows);
            }
        }
        finally
        {
            ChunkPool.Return(chunk);
        }

        return new RawImage(format, pixels);
    }
}
