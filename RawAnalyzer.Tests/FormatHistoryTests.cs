using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// サイズ別フォーマット記憶(FormatHistory)の記録・更新・自動で開く判定・訂正・削除。
/// </summary>
public class FormatHistoryTests
{
    // 640×480 12bit(2byte/画素)1フレーム
    private const long Size = 640 * 480 * 2;
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RawFormat Fmt(
        int width = 640, int height = 480, int bitDepth = 12,
        BayerPattern bayer = BayerPattern.None)
    {
        return new RawFormat { Width = width, Height = height, BitDepth = bitDepth, Bayer = bayer };
    }

    [Fact]
    public void Record_NewKey_RemembersWholeFormatWithTimeAndAutoOpen()
    {
        var history = new FormatHistory();
        RawFormat format = Fmt(bayer: BayerPattern.Rggb) with
        {
            Packing = BitPacking.Msb,
            Endianness = Endianness.Big,
            Hdr = HdrMode.LineInterleaved,
            ExposureRatio = 8,
        };

        history.Record(Size, ".RAW", format, autoOpen: null, T0);

        FormatHistoryEntry entry = Assert.Single(history.Entries);
        Assert.Equal(Size, entry.FileSize);
        Assert.Equal(".raw", entry.Extension);
        Assert.Equal(format, entry.Format);
        Assert.Equal(T0, entry.LastUsedUtc);
        Assert.True(entry.AutoOpen, "記憶のないキーは自動で開く(チェックの既定と同じ)");

        // 拡張子は大文字小文字・ドットの有無を問わず同じキー
        Assert.Equal(format, history.FindAutoOpenFormat(Size, "raw"));
        Assert.Equal(format, history.FindAutoOpenFormat(Size, ".Raw"));
    }

    [Fact]
    public void Record_SameFormatAgain_UpdatesLastUsedAndFlagWithoutDuplicating()
    {
        var history = new FormatHistory();
        RawFormat format = Fmt();
        history.Record(Size, ".raw", format, null, T0);
        history.Record(Size * 2, ".raw", Fmt(bitDepth: 16), null, T0.AddMinutes(1));

        history.Record(Size, ".raw", format, autoOpen: false, T0.AddMinutes(2));

        Assert.Equal(2, history.Entries.Count);
        FormatHistoryEntry updated = history.Entries[0];
        Assert.Equal(format, updated.Format);
        Assert.Equal(T0.AddMinutes(2), updated.LastUsedUtc);
        Assert.False(updated.AutoOpen);

        // 選択なし(記憶から開いた)の記録はフラグを保つ
        history.Record(Size, ".raw", format, autoOpen: null, T0.AddMinutes(3));
        Assert.False(history.Find(Size, ".raw", format)!.AutoOpen);
        Assert.Equal(T0.AddMinutes(3), history.Find(Size, ".raw", format)!.LastUsedUtc);
        Assert.Equal(2, history.Entries.Count);
    }

    [Fact]
    public void Record_DifferentInterpretationOfSameKey_KeepsBoth()
    {
        var history = new FormatHistory();
        RawFormat first = Fmt();
        RawFormat second = Fmt(width: 480, height: 640, bitDepth: 16);
        history.Record(Size, ".raw", first, null, T0);

        history.Record(Size, ".raw", second, null, T0.AddMinutes(1));

        IReadOnlyList<FormatHistoryEntry> entries = history.Find(Size, ".raw");
        Assert.Equal(new[] { second, first }, entries.Select(e => e.Format));

        // 既に記憶のあるキーに選択なしで加わった解釈は候補だけ(自動で開く形式は変えない)
        Assert.False(entries[0].AutoOpen);
        Assert.Equal(first, history.FindAutoOpenFormat(Size, ".raw"));
    }

    [Fact]
    public void Record_OtherSizeOrExtension_IsAnotherKey()
    {
        var history = new FormatHistory();
        history.Record(Size, ".raw", Fmt(), null, T0);
        history.Record(Size, ".bin", Fmt(bitDepth: 10), null, T0);
        history.Record(Size + 2, ".raw", Fmt(bitDepth: 14), null, T0);

        Assert.Equal(Fmt(), history.FindAutoOpenFormat(Size, ".raw"));
        Assert.Equal(Fmt(bitDepth: 10), history.FindAutoOpenFormat(Size, ".bin"));
        Assert.Equal(Fmt(bitDepth: 14), history.FindAutoOpenFormat(Size + 2, ".raw"));
        Assert.Null(history.FindAutoOpenFormat(Size + 4, ".raw"));
        Assert.Single(history.Find(Size, ".RAW"));
    }

    [Fact]
    public void Record_FormatThatCannotOpenAtThatSize_IsRejected()
    {
        // 記憶はどれもそのサイズで開けるもの(自動で開くときにそのまま使う)
        var history = new FormatHistory();
        Assert.Throws<ArgumentException>(() => history.Record(Size - 2, ".raw", Fmt(), null, T0));
        Assert.Throws<ArgumentException>(
            () => history.Record(Size, ".raw", Fmt() with { BitDepth = 11 }, null, T0));
        history.Record(Size, ".raw", Fmt(), null, T0);

        Assert.Throws<ArgumentException>(() => history.Replace(
            Size, ".raw", Fmt(), Fmt(bitDepth: 16) with { FrameCount = 2 }, true, T0));
        Assert.Equal(Fmt(), Assert.Single(history.Entries).Format);
    }

    [Fact]
    public void Record_OverLimit_DropsLeastRecentlyUsed()
    {
        var history = new FormatHistory();
        for (int i = 0; i < FormatHistory.MaxEntries; i++)
        {
            history.Record(10_000 + i, ".raw", Fmt(8, 8), null, T0.AddMinutes(i));
        }

        // 最古の1件目を使い直すと、次に古い2件目が上限超過で消える
        history.Record(10_000, ".raw", Fmt(8, 8), null, T0.AddMinutes(500));
        history.Record(99_999, ".raw", Fmt(8, 8), null, T0.AddMinutes(501));

        Assert.Equal(FormatHistory.MaxEntries, history.Entries.Count);
        Assert.NotEmpty(history.Find(10_000, ".raw"));
        Assert.Empty(history.Find(10_001, ".raw"));
        Assert.NotEmpty(history.Find(10_002, ".raw"));
        Assert.Equal(99_999, history.Entries[0].FileSize);
        Assert.Equal(10_000, history.Entries[1].FileSize);
        Assert.True(history.Entries.Zip(history.Entries.Skip(1))
            .All(pair => pair.First.LastUsedUtc >= pair.Second.LastUsedUtc), "最終使用が新しい順");
    }

    [Fact]
    public void FindAutoOpenFormat_OnlyWhenExactlyOneAutoOpenInterpretation()
    {
        RawFormat first = Fmt();
        RawFormat second = Fmt(width: 480, height: 640, bitDepth: 16);
        FormatHistoryEntry Entry(RawFormat format, bool autoOpen, int minutes) => new()
        {
            FileSize = Size, Extension = ".raw", Format = format,
            AutoOpen = autoOpen, LastUsedUtc = T0.AddMinutes(minutes),
        };

        Assert.Null(new FormatHistory().FindAutoOpenFormat(Size, ".raw"));
        Assert.Equal(first, new FormatHistory(new[] { Entry(first, true, 0) })
            .FindAutoOpenFormat(Size, ".raw"));
        Assert.Null(new FormatHistory(new[] { Entry(first, false, 0) })
            .FindAutoOpenFormat(Size, ".raw"));

        // 自動適用オフの解釈は候補だけで、オンの解釈が1つなら自動で開く
        Assert.Equal(first, new FormatHistory(new[] { Entry(first, true, 0), Entry(second, false, 1) })
            .FindAutoOpenFormat(Size, ".raw"));

        // オンが2つ以上あるとどちらか決められないのでダイアログにする
        Assert.Null(new FormatHistory(new[] { Entry(first, true, 0), Entry(second, true, 1) })
            .FindAutoOpenFormat(Size, ".raw"));
    }

    [Fact]
    public void Record_ExplicitAutoOpen_MakesItTheOnlyAutoOpenInterpretation()
    {
        var history = new FormatHistory();
        RawFormat first = Fmt();
        RawFormat second = Fmt(width: 480, height: 640, bitDepth: 16);
        history.Record(Size, ".raw", first, null, T0);
        history.Record(Size + 2, ".raw", first, null, T0);

        // ダイアログで「次回からこの形式で開く」をオンのまま second で確定した
        history.Record(Size, ".raw", second, autoOpen: true, T0.AddMinutes(1));

        Assert.Equal(second, history.FindAutoOpenFormat(Size, ".raw"));
        Assert.False(history.Find(Size, ".raw", first)!.AutoOpen);
        Assert.Equal(2, history.Find(Size, ".raw").Count);
        Assert.Equal(first, history.FindAutoOpenFormat(Size + 2, ".raw")); // 別のキーは変えない
    }

    [Fact]
    public void Record_ExplicitOff_RemembersButDoesNotOpenAutomatically()
    {
        var history = new FormatHistory();
        RawFormat first = Fmt();
        RawFormat second = Fmt(width: 480, height: 640, bitDepth: 16);

        history.Record(Size, ".raw", first, autoOpen: false, T0);
        Assert.Null(history.FindAutoOpenFormat(Size, ".raw"));
        Assert.Single(history.Find(Size, ".raw"));

        // オフで確定しても同じキーの他の解釈は変えない
        history.Record(Size, ".raw", second, autoOpen: true, T0.AddMinutes(1));
        history.Record(Size, ".raw", first, autoOpen: false, T0.AddMinutes(2));
        Assert.Equal(second, history.FindAutoOpenFormat(Size, ".raw"));
    }

    [Fact]
    public void Replace_RememberedOriginal_ReplacesItWithCorrection()
    {
        var history = new FormatHistory();
        RawFormat wrong = Fmt(bayer: BayerPattern.Rggb);
        RawFormat right = Fmt(bayer: BayerPattern.Bggr);
        history.Record(Size, ".raw", wrong, null, T0);
        history.Record(Size * 2, ".raw", wrong, null, T0);

        bool replaced = history.Replace(Size, ".RAW", wrong, right, autoOpen: true, T0.AddMinutes(1));

        Assert.True(replaced);
        FormatHistoryEntry entry = Assert.Single(history.Find(Size, ".raw"));
        Assert.Equal(right, entry.Format);
        Assert.True(entry.AutoOpen);
        Assert.Equal(T0.AddMinutes(1), entry.LastUsedUtc);
        Assert.Equal(right, history.FindAutoOpenFormat(Size, ".raw"));
        Assert.Equal(wrong, history.FindAutoOpenFormat(Size * 2, ".raw")); // 別のキーはそのまま
    }

    [Fact]
    public void Replace_WithoutChoice_InheritsAutoOpenOfOriginal()
    {
        var history = new FormatHistory();
        RawFormat original = Fmt();
        RawFormat corrected = Fmt(bayer: BayerPattern.Gbrg);
        history.Record(Size, ".raw", original, autoOpen: false, T0);

        Assert.True(history.Replace(Size, ".raw", original, corrected, autoOpen: null, T0));

        FormatHistoryEntry entry = Assert.Single(history.Entries);
        Assert.Equal(corrected, entry.Format);
        Assert.False(entry.AutoOpen);
    }

    [Fact]
    public void Replace_OriginalNotRemembered_ChangesNothing()
    {
        var history = new FormatHistory();
        RawFormat remembered = Fmt();
        history.Record(Size, ".raw", remembered, null, T0);

        Assert.False(history.Replace(
            Size, ".raw", Fmt(bitDepth: 10), Fmt(bitDepth: 16), true, T0.AddMinutes(1)));
        Assert.False(history.Replace(
            Size, ".bin", remembered, Fmt(bitDepth: 16), true, T0.AddMinutes(1)));

        FormatHistoryEntry entry = Assert.Single(history.Entries);
        Assert.Equal(remembered, entry.Format);
        Assert.Equal(T0, entry.LastUsedUtc);
    }

    [Fact]
    public void Replace_WithAlreadyRememberedFormat_MergesIntoOne()
    {
        var history = new FormatHistory();
        RawFormat first = Fmt();
        RawFormat second = Fmt(width: 480, height: 640, bitDepth: 16);
        history.Record(Size, ".raw", first, null, T0);
        history.Record(Size, ".raw", second, null, T0.AddMinutes(1));

        Assert.True(history.Replace(Size, ".raw", first, second, true, T0.AddMinutes(2)));

        FormatHistoryEntry entry = Assert.Single(history.Find(Size, ".raw"));
        Assert.Equal(second, entry.Format);
        Assert.True(entry.AutoOpen);
    }

    [Fact]
    public void Remove_DeletesEveryInterpretationOfTheKeyOnly()
    {
        var history = new FormatHistory();
        history.Record(Size, ".raw", Fmt(), null, T0);
        history.Record(Size, ".raw", Fmt(bitDepth: 16), null, T0);
        history.Record(Size, ".bin", Fmt(), null, T0);

        Assert.Equal(2, history.Remove(Size, "RAW"));

        Assert.Empty(history.Find(Size, ".raw"));
        Assert.Single(history.Find(Size, ".bin"));
        Assert.Equal(0, history.Remove(Size, ".raw"));

        // 消した後に記録すると、記憶のないキーとして自動で開く
        history.Record(Size, ".raw", Fmt(bitDepth: 16), null, T0.AddMinutes(1));
        Assert.Equal(Fmt(bitDepth: 16), history.FindAutoOpenFormat(Size, ".raw"));
    }

    [Fact]
    public void Constructor_SanitizesLoadedEntries()
    {
        FormatHistoryEntry Entry(long size, string extension, RawFormat format, int minutes) => new()
        {
            FileSize = size, Extension = extension, Format = format,
            LastUsedUtc = T0.AddMinutes(minutes),
        };

        var history = new FormatHistory(new FormatHistoryEntry?[]
        {
            Entry(Size, ".RAW", Fmt(), 1),
            null,
            Entry(-1, ".raw", Fmt(), 2),                         // サイズが負
            Entry(Size, ".raw", Fmt() with { Width = 0 }, 3),    // 不正なフォーマット
            Entry(Size - 2, ".raw", Fmt(), 4),                    // そのサイズでは開けない
            Entry(Size, "raw", Fmt(), 0),                         // 重複(古い方)
            Entry(Size, ".bin", Fmt(bitDepth: 16), 5),
        });

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal((".bin", T0.AddMinutes(5)), (history.Entries[0].Extension, history.Entries[0].LastUsedUtc));
        Assert.Equal((".raw", T0.AddMinutes(1)), (history.Entries[1].Extension, history.Entries[1].LastUsedUtc));

        // 上限を超えて読み込んだら新しい方を残す
        var many = new FormatHistory(Enumerable.Range(0, FormatHistory.MaxEntries + 5)
            .Select(i => Entry(10_000 + i, ".raw", Fmt(8, 8), i)));
        Assert.Equal(FormatHistory.MaxEntries, many.Entries.Count);
        Assert.Equal(10_000 + FormatHistory.MaxEntries + 4, many.Entries[0].FileSize);
        Assert.Empty(many.Find(10_004, ".raw"));
        Assert.NotEmpty(many.Find(10_005, ".raw"));
    }
}
