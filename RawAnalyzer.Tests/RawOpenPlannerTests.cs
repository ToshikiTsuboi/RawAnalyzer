using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// raw を開くときの判断順(同じパスの記憶 → 同じサイズの記憶 → ダイアログ)と、ダイアログの初期値・通知の文。
/// MainWindow の配線(OpenPath・比較ペイン・F2)はこの判断をそのまま使う。
/// </summary>
public class RawOpenPlannerTests
{
    private const long Size = 640 * 480 * 2;
    private const string Path = @"D:\cap\shot_0001.raw";
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RawFormat Fmt(int width, int height, int bitDepth = 12,
        BayerPattern bayer = BayerPattern.None)
    {
        return new RawFormat { Width = width, Height = height, BitDepth = bitDepth, Bayer = bayer };
    }

    [Fact]
    public void Plan_PathMemoryComesFirst()
    {
        var history = new FormatHistory();
        history.Record(Size, ".raw", Fmt(640, 480, bayer: BayerPattern.Rggb), null, T0);
        RawFormat pathMemory = Fmt(480, 640, bitDepth: 16);

        RawOpenPlan plan = RawOpenPlanner.Plan(Path, Size, pathMemory, history, displayedFormat: null);

        // 同じパスの記憶があれば従来どおりそのまま開く(同じサイズの記憶より優先)
        Assert.Equal(new RawOpenPlan(RawFormatOrigin.PathMemory, pathMemory, null), plan);
    }

    [Fact]
    public void Plan_SingleAutoOpenSizeMemory_OpensWithoutDialog()
    {
        var history = new FormatHistory();
        RawFormat remembered = Fmt(640, 480, bayer: BayerPattern.Rggb);
        history.Record(Size, ".RAW", remembered, null, T0);

        RawOpenPlan plan = RawOpenPlanner.Plan(Path, Size, null, history, Fmt(1920, 1080));

        Assert.Equal(new RawOpenPlan(RawFormatOrigin.SizeMemory, remembered, null), plan);

        // 拡張子が違えば別のキー(ダイアログ)
        Assert.Equal(RawFormatOrigin.Dialog,
            RawOpenPlanner.Plan(@"D:\cap\shot_0001.bin", Size, null, history, null).Origin);

        // サイズ不明なら記憶では開かない
        Assert.Equal(RawFormatOrigin.Dialog,
            RawOpenPlanner.Plan(Path, -1, null, history, null).Origin);
    }

    [Fact]
    public void Plan_AutoOpenOffOrAmbiguous_ShowsDialogStartingFromNewestMemory()
    {
        RawFormat older = Fmt(640, 480, bayer: BayerPattern.Rggb);
        RawFormat newer = Fmt(480, 640, bitDepth: 16);

        // 自動適用オフの記憶だけ: ダイアログ(初期値はその記憶)
        var off = new FormatHistory();
        off.Record(Size, ".raw", older, autoOpen: false, T0);
        Assert.Equal(new RawOpenPlan(RawFormatOrigin.Dialog, null, older),
            RawOpenPlanner.Plan(Path, Size, null, off, Fmt(1920, 1080)));

        // 自動適用オンが2つ(どちらか決められない): ダイアログ(初期値は新しい方)
        var ambiguous = new FormatHistory(new[]
        {
            new FormatHistoryEntry { FileSize = Size, Extension = ".raw", Format = older, LastUsedUtc = T0 },
            new FormatHistoryEntry
            {
                FileSize = Size, Extension = ".raw", Format = newer, LastUsedUtc = T0.AddMinutes(1),
            },
        });
        Assert.Equal(new RawOpenPlan(RawFormatOrigin.Dialog, null, newer),
            RawOpenPlanner.Plan(Path, Size, null, ambiguous, null));
    }

    [Fact]
    public void Plan_NoMemory_DialogStartsFromFirstCandidateThenDisplayedFormat()
    {
        var history = new FormatHistory();

        // 候補の先頭(ここではファイル名の表記)
        RawOpenPlan named = RawOpenPlanner.Plan(
            @"D:\cap\shot_320x960.raw", Size, null, history, Fmt(4000, 3000));
        Assert.Equal(new RawOpenPlan(RawFormatOrigin.Dialog, null, Fmt(320, 960)), named);

        // 候補がなければ従来どおり表示中の画像のフォーマット、それもなければダイアログ既定の推定(null)
        Assert.Equal(Fmt(4000, 3000),
            RawOpenPlanner.Plan(Path, 1001, null, history, Fmt(4000, 3000)).DialogInitial);
        Assert.Null(RawOpenPlanner.Plan(Path, 1001, null, history, null).DialogInitial);
    }

    [Fact]
    public void Plan_ChooseFormat_AlwaysShowsDialogAndPrefersRequestedInitial()
    {
        var history = new FormatHistory();
        RawFormat remembered = Fmt(640, 480, bayer: BayerPattern.Rggb);
        history.Record(Size, ".raw", remembered, null, T0);
        RawFormat current = Fmt(640, 480, bayer: BayerPattern.Bggr);

        // F2: 記憶(同じパス・同じサイズ)があってもダイアログを出し、渡された初期値を優先する
        RawOpenPlan plan = RawOpenPlanner.Plan(
            Path, Size, pathMemory: remembered, history, current, requestedInitial: current,
            chooseFormat: true);
        Assert.Equal(new RawOpenPlan(RawFormatOrigin.Dialog, null, current), plan);

        // 初期値が渡されなければ候補一覧の先頭
        Assert.Equal(remembered, RawOpenPlanner.Plan(
            Path, Size, null, history, current, requestedInitial: null, chooseFormat: true).DialogInitial);
    }

    [Fact]
    public void Notices_DescribeTheGuessAndHowToChangeIt()
    {
        Assert.Equal("同じサイズのファイルの記憶から推定して開きました(F2 で変更)", RawOpenPlanner.AutoOpenNotice);

        RawFormat format = Fmt(640, 480, bayer: BayerPattern.Rggb);
        string toolTip = RawOpenPlanner.AutoOpenToolTip(Path, Size, format);
        Assert.Contains("640×480 12bit RGGB", toolTip);
        Assert.Contains("614,400 バイト", toolTip);
        Assert.Contains(".raw", toolTip);
        Assert.Contains("F2", toolTip);

        string compare = RawOpenPlanner.CompareAutoOpenNotice(Path);
        Assert.Contains("shot_0001.raw", compare);
        Assert.Contains("通常表示で開いて F2", compare);
    }

    [Fact]
    public void CandidateText_ShowsSourceAndNonDefaultSettings()
    {
        Assert.Equal("記憶(同じサイズ)", FormatCandidateText.SourceLabel(FormatCandidateSource.SameSizeMemory));
        Assert.Equal("表示中の画像", FormatCandidateText.SourceLabel(FormatCandidateSource.DisplayedImage));
        Assert.Equal("記憶から推定", FormatCandidateText.SourceLabel(FormatCandidateSource.MemoryFrameCount));
        Assert.Equal("記憶から推定", FormatCandidateText.SourceLabel(FormatCandidateSource.MemoryHeight));
        Assert.Equal("記憶から推定", FormatCandidateText.SourceLabel(FormatCandidateSource.MemoryResolution));
        Assert.Equal("ファイル名", FormatCandidateText.SourceLabel(FormatCandidateSource.FileName));
        Assert.Equal("解像度表", FormatCandidateText.SourceLabel(FormatCandidateSource.ResolutionTable));

        Assert.Equal("1920×1080 12bit モノクロ", FormatCandidateText.Describe(Fmt(1920, 1080)));
        Assert.Equal(
            "ファイル名: 1920×1080 10bit GBRG 上詰め Big ヘッダ512B 2fr HDR フレーム連結 2段",
            FormatCandidateText.Display(new FormatCandidate(
                Fmt(1920, 1080, bitDepth: 10, bayer: BayerPattern.Gbrg) with
                {
                    Packing = BitPacking.Msb, Endianness = Endianness.Big, HeaderOffset = 512,
                    FrameCount = 2, Hdr = HdrMode.FrameSequential,
                },
                FormatCandidateSource.FileName)));
    }
}
