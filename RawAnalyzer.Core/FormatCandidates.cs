using System.Globalization;
using System.Text.RegularExpressions;

namespace RawAnalyzer.Core;

/// <summary>フォーマット候補の由来。</summary>
public enum FormatCandidateSource
{
    /// <summary>同じファイルサイズ・拡張子のファイルを開いたときの記憶。</summary>
    SameSizeMemory,

    /// <summary>表示中の画像のフォーマット。</summary>
    DisplayedImage,

    /// <summary>記憶のフォーマットのフレーム数だけを変えたもの。</summary>
    MemoryFrameCount,

    /// <summary>記憶のフォーマットの高さだけを変えたもの(1フレーム)。</summary>
    MemoryHeight,

    /// <summary>記憶にあるヘッダ・ビット深度・詰め・エンディアン・Bayer の組み合わせと組み込み解像度表から作ったもの。</summary>
    MemoryResolution,

    /// <summary>ファイル名の「幅x高さ」表記から作ったもの。</summary>
    FileName,

    /// <summary>組み込み解像度表(既定の 12bit・下詰め・Little・ヘッダ 0)から作ったもの。</summary>
    ResolutionTable,
}

/// <summary>ファイルを開くときのフォーマット候補。</summary>
/// <param name="Format">候補のフォーマット。</param>
/// <param name="Source">候補の由来。</param>
public sealed record FormatCandidate(RawFormat Format, FormatCandidateSource Source);

/// <summary>
/// ファイルサイズに合うフォーマットの候補を、記憶・表示中の画像・ファイル名・組み込み解像度表から作る。
/// </summary>
/// <remarks>
/// <para>
/// 候補は次の順に並べる。同じ <see cref="RawFormat"/> は先に出た方だけを残し、
/// 件数は上限(既定 <see cref="DefaultMaxCount"/> 件)で打ち切る。
/// </para>
/// <list type="number">
/// <item>キー(サイズ・拡張子)が一致する記憶(最終使用が新しい順)。記録時にそのサイズで開けたものなので、
/// 末尾に余りのあるフォーマットでもそのまま候補にする。</item>
/// <item>表示中の画像のフォーマット。</item>
/// <item>記憶からの推定: (a) フレーム数だけを変えたもの(HDR がフレーム連結・自動の記憶は、フレーム数を
/// 変えると HDR の意味が変わるので除く)、(b) 高さだけを変えたもの(1フレーム)、(c) 記憶にある
/// ヘッダ・ビット深度・詰め・エンディアン・Bayer の組み合わせと組み込み解像度表。(a)→(b)→(c) の順で、
/// それぞれ記憶の新しい順。</item>
/// <item>ファイル名の「幅x高さ」表記(複数あれば先頭から)。ヘッダ・ビット深度などは記憶の組み合わせ
/// (新しい順)と既定値(12bit・下詰め・Little・ヘッダ 0)を使う。同じ幅×高さでは1フレームで合うものを
/// 先に、フレーム数を変えると合うものを後に出す。</item>
/// <item>組み込み解像度表(既定値。従来の初期値の推定と同じ前提)。</item>
/// </list>
/// <para>1 以外は、ファイルサイズにちょうど合う(<see cref="RawFormat.RequiredBytes"/> がサイズと等しい)ものだけ。</para>
/// </remarks>
public static class FormatCandidates
{
    /// <summary>候補の件数の既定の上限。</summary>
    public const int DefaultMaxCount = 10;

    // 前後が数字に続かない 2〜5 桁 × 2〜5 桁(123456x100 の途中や 1x2 は拾わない)
    private static readonly Regex DimensionPattern = new(
        "(?<![0-9])([0-9]{2,5})\\s*[xX×]\\s*([0-9]{2,5})(?![0-9])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 既定のフォーマット(12bit・下詰め・Little・ヘッダ 0・Bayer なし・1フレーム)。幅・高さは 1。
    /// ファイル名の表記と組み込み解像度表の候補はこれを元に作る。
    /// </summary>
    public static RawFormat DefaultFormat { get; } = new() { Width = 1, Height = 1, BitDepth = 12 };

    /// <summary>
    /// ファイルサイズに合うフォーマットの候補を作る。
    /// </summary>
    /// <param name="fileSize">開こうとしているファイルのサイズ(バイト)。0以下なら候補なし。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <param name="fileName">ファイル名またはパス(「幅x高さ」表記を拾う)。</param>
    /// <param name="history">サイズ別フォーマット記憶。</param>
    /// <param name="displayedFormat">表示中の画像のフォーマット(なければ null)。</param>
    /// <param name="maxCount">候補の件数の上限。</param>
    /// <returns>候補(先頭ほど有力)。</returns>
    public static IReadOnlyList<FormatCandidate> Build(
        long fileSize, string extension, string fileName, IReadOnlyFormatHistory history,
        RawFormat? displayedFormat, int maxCount = DefaultMaxCount)
    {
        ArgumentNullException.ThrowIfNull(history);
        var collector = new Collector(maxCount);
        if (fileSize <= 0 || maxCount <= 0)
        {
            return collector.Results;
        }

        IReadOnlyList<FormatHistoryEntry> memories = history.Entries;

        // 1. 同じキーの記憶(記録時にそのサイズで開けたもの)
        foreach (FormatHistoryEntry entry in history.Find(fileSize, extension))
        {
            if (FormatHistory.CanOpen(entry.Format, fileSize)
                && !collector.Add(entry.Format, FormatCandidateSource.SameSizeMemory))
            {
                return collector.Results;
            }
        }

        // 2. 表示中の画像
        if (displayedFormat is not null && FitsExactly(displayedFormat, fileSize)
            && !collector.Add(displayedFormat, FormatCandidateSource.DisplayedImage))
        {
            return collector.Results;
        }

        // 3(a). フレーム数だけを変える
        foreach (FormatHistoryEntry entry in memories)
        {
            RawFormat format = entry.Format;
            if (FitCount(fileSize, format.HeaderOffset, format.FrameSizeInBytes) is not { } frames)
            {
                continue;
            }

            // フレーム連結・自動の HDR は、フレーム数が段数と一致するかどうかで意味が変わる
            if (frames != format.FrameCount
                && format.Hdr is HdrMode.FrameSequential or HdrMode.Auto)
            {
                continue;
            }

            if (!collector.AddIfFits(
                format with { FrameCount = frames }, fileSize, FormatCandidateSource.MemoryFrameCount))
            {
                return collector.Results;
            }
        }

        // 3(b). 同じ幅・ヘッダ・ビット深度・詰め・エンディアン・Bayer で高さだけを変える(1フレーム)
        foreach (FormatHistoryEntry entry in memories)
        {
            RawFormat format = entry.Format;
            long rowBytes = (long)format.Width * format.BytesPerPixel;
            if (FitCount(fileSize, format.HeaderOffset, rowBytes) is not { } height)
            {
                continue;
            }

            RawFormat single = format with { Height = height, FrameCount = 1 };

            // 複数フレームのフレーム連結・自動の HDR は、1フレームにすると意味が変わる(HDR なしにする)
            if (format.FrameCount != 1 && format.Hdr is HdrMode.FrameSequential or HdrMode.Auto)
            {
                single = WithoutHdr(single);
            }

            if (!collector.AddIfFits(single, fileSize, FormatCandidateSource.MemoryHeight))
            {
                return collector.Results;
            }
        }

        // 3(c). 記憶にある組み合わせ × 組み込み解像度表
        List<RawFormat> combinations = MemoryCombinations(memories);
        foreach (RawFormat probe in combinations)
        {
            foreach (DimensionCandidate size in RawLoader.GuessDimensions(fileSize, probe))
            {
                if (!collector.AddIfFits(
                    probe with { Width = size.Width, Height = size.Height }, fileSize,
                    FormatCandidateSource.MemoryResolution))
                {
                    return collector.Results;
                }
            }
        }

        // 4. ファイル名の「幅x高さ」表記(記憶の組み合わせ → 既定値)
        List<RawFormat> nameProbes = combinations.Contains(DefaultFormat)
            ? combinations
            : combinations.Append(DefaultFormat).ToList();
        foreach (DimensionCandidate size in ParseFileNameDimensions(fileName))
        {
            // 1フレームで合うものを先に、フレーム数を変えると合うものを後に出す
            foreach (bool singleFrame in new[] { true, false })
            {
                foreach (RawFormat probe in nameProbes)
                {
                    RawFormat sized = probe with { Width = size.Width, Height = size.Height };
                    if (FitCount(fileSize, sized.HeaderOffset, sized.FrameSizeInBytes) is not { } frames
                        || (frames == 1) != singleFrame)
                    {
                        continue;
                    }

                    if (!collector.AddIfFits(
                        sized with { FrameCount = frames }, fileSize, FormatCandidateSource.FileName))
                    {
                        return collector.Results;
                    }
                }
            }
        }

        // 5. 組み込み解像度表(既定値)
        foreach (DimensionCandidate size in RawLoader.GuessDimensions(fileSize, DefaultFormat))
        {
            if (!collector.AddIfFits(
                DefaultFormat with { Width = size.Width, Height = size.Height }, fileSize,
                FormatCandidateSource.ResolutionTable))
            {
                return collector.Results;
            }
        }

        return collector.Results;
    }

    /// <summary>
    /// ファイル名から「幅x高さ」の表記(<c>1920x1080</c>・<c>640X480</c>・<c>1280 × 720</c> など、
    /// それぞれ 2〜5 桁)を先頭から順に拾う。同じ幅×高さは1つにまとめる。
    /// </summary>
    /// <param name="fileName">ファイル名またはパス(フォルダ名は見ない)。</param>
    /// <returns>拾った幅×高さ(出てきた順)。なければ空。</returns>
    public static IReadOnlyList<DimensionCandidate> ParseFileNameDimensions(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var results = new List<DimensionCandidate>();
        foreach (Match match in DimensionPattern.Matches(Path.GetFileName(fileName)))
        {
            int width = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int height = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var size = new DimensionCandidate(width, height);
            if (width > 0 && height > 0 && !results.Contains(size))
            {
                results.Add(size);
            }
        }

        return results;
    }

    /// <summary>フォーマットがファイルサイズにちょうど合うか(値が妥当で、必要なバイト数がサイズと等しい)。</summary>
    /// <param name="format">フォーマット。</param>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <returns>ちょうど合うなら true。</returns>
    public static bool FitsExactly(RawFormat format, long fileSize)
    {
        ArgumentNullException.ThrowIfNull(format);
        try
        {
            format.Validate();
            return format.RequiredBytes() == fileSize;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// ヘッダの後ろがちょうど <paramref name="unitBytes"/> の n 倍(n は 1 以上)になるときの n。
    /// </summary>
    private static int? FitCount(long fileSize, long headerOffset, long unitBytes)
    {
        if (unitBytes <= 0 || headerOffset < 0)
        {
            return null;
        }

        long dataBytes = fileSize - headerOffset;
        if (dataBytes <= 0 || dataBytes % unitBytes != 0)
        {
            return null;
        }

        long count = dataBytes / unitBytes;
        return count <= int.MaxValue ? (int)count : null;
    }

    /// <summary>HDR の設定を既定(なし)に戻す。</summary>
    private static RawFormat WithoutHdr(RawFormat format)
    {
        var defaults = new RawFormat { Width = 1, Height = 1 };
        return format with
        {
            Hdr = HdrMode.None,
            HdrStages = defaults.HdrStages,
            ExposureRatio = defaults.ExposureRatio,
            HdrLineBlock = defaults.HdrLineBlock,
            HdrRowOffset = defaults.HdrRowOffset,
        };
    }

    /// <summary>
    /// 記憶にあるヘッダ・ビット深度・詰め・エンディアン・Bayer の組み合わせ(記憶の新しい順、重複なし)。
    /// 幅・高さは 1、フレーム数 1、HDR なし。
    /// </summary>
    private static List<RawFormat> MemoryCombinations(IReadOnlyList<FormatHistoryEntry> memories)
    {
        var combinations = new List<RawFormat>();
        foreach (FormatHistoryEntry entry in memories)
        {
            RawFormat probe = DefaultFormat with
            {
                BitDepth = entry.Format.BitDepth,
                Packing = entry.Format.Packing,
                Endianness = entry.Format.Endianness,
                HeaderOffset = entry.Format.HeaderOffset,
                Bayer = entry.Format.Bayer,
            };
            if (!combinations.Contains(probe))
            {
                combinations.Add(probe);
            }
        }

        return combinations;
    }

    /// <summary>重複を除きながら上限まで候補を集める。</summary>
    private sealed class Collector
    {
        private readonly int _maxCount;
        private readonly HashSet<RawFormat> _seen = new();
        private readonly List<FormatCandidate> _results = new();

        public Collector(int maxCount)
        {
            _maxCount = maxCount;
        }

        public IReadOnlyList<FormatCandidate> Results => _results;

        /// <summary>候補を加える(重複は捨てる)。</summary>
        /// <returns>まだ上限に達していなければ true。</returns>
        public bool Add(RawFormat format, FormatCandidateSource source)
        {
            if (_results.Count < _maxCount && _seen.Add(format))
            {
                _results.Add(new FormatCandidate(format, source));
            }

            return _results.Count < _maxCount;
        }

        /// <summary>ファイルサイズにちょうど合う場合だけ候補を加える。</summary>
        /// <returns>まだ上限に達していなければ true。</returns>
        public bool AddIfFits(RawFormat format, long fileSize, FormatCandidateSource source)
        {
            return FitsExactly(format, fileSize)
                ? Add(format, source)
                : _results.Count < _maxCount;
        }
    }
}
