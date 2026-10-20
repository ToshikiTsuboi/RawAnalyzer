using System.Text.Json;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// RawFormat.Validate の検証。
/// </summary>
public class RawFormatTests
{
    [Fact]
    public void Validate_RejectsNonFiniteExposureRatio()
    {
        // NaN は大小比較を素通りするので、有限性を明示的に確かめること
        // (NaN のまま HDR 合成へ入ると全画素が NaN になる。Codexレビュー 2026-08-23 #9 の回帰)
        var format = new RawFormat
        {
            Width = 4,
            Height = 4,
            BitDepth = 12,
            ExposureRatio = double.NaN,
        };

        Assert.Throws<ArgumentException>(format.Validate);
    }

    public static IEnumerable<object[]> UndefinedEnumFormats()
    {
        var basis = new RawFormat { Width = 2, Height = 1, BitDepth = 12 };
        yield return new object[] { basis with { Packing = (BitPacking)7 } };
        yield return new object[] { basis with { Endianness = (Endianness)5 } };
        yield return new object[] { basis with { Bayer = (BayerPattern)9 } };
        yield return new object[] { basis with { Bayer = (BayerPattern)(-1) } };
        yield return new object[] { basis with { Hdr = (HdrMode)99 } };
    }

    [Theory]
    [MemberData(nameof(UndefinedEnumFormats))]
    public void Validate_RejectsUndefinedEnumValues(RawFormat format)
    {
        // 回帰テスト: 手で編集した・壊れた記憶の JSON から未定義の列挙値が入っても Validate を通り、
        // Endianness=5 は読み込みでは Little、保存・取り込みダイアログでは Big と場所によって逆に解釈されていた
        Assert.Throws<ArgumentException>(format.Validate);
    }

    [Theory]
    [InlineData("\"99\"", HdrMode.None)]
    [InlineData("\"-1\"", HdrMode.None)]
    [InlineData("\"Auto, LineInterleaved\"", HdrMode.None)]
    [InlineData("99", HdrMode.None)]
    [InlineData("\"2\"", HdrMode.LineInterleaved)]
    [InlineData("2", HdrMode.LineInterleaved)]
    [InlineData("\"lineinterleaved\"", HdrMode.LineInterleaved)]
    [InlineData("\"Dol\"", HdrMode.Auto)]
    public void Json_HdrMode_UndefinedOrCombinedFallsBackToNone(string json, HdrMode expected)
    {
        // 回帰テスト: 文字列は Enum.TryParse に渡すだけだったので、"99" が未定義の (HdrMode)99 のまま、
        // "Auto, LineInterleaved" が 1|2 = FrameSequential として通っていた。数値の文字列は数値と同じく
        // 定義された値だけを読み、それ以外は既定の None へ戻す
        RawFormat format = JsonSerializer.Deserialize<RawFormat>(
            $"{{\"Width\":2,\"Height\":1,\"Hdr\":{json}}}")!;

        Assert.Equal(expected, format.Hdr);
    }

    [Theory]
    [InlineData("\"Rggb, Bggr\"")]   // 1|2 = Grbg として通っていた
    [InlineData("\"Purple\"")]
    public void Json_BayerCombinedOrUnknownName_IsRejected(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RawFormat>(
            $"{{\"Width\":2,\"Height\":1,\"Bayer\":{json}}}"));
    }

    [Theory]
    [InlineData("\"Endianness\":5")]
    [InlineData("\"Endianness\":\"5\"")]
    [InlineData("\"Packing\":7")]
    [InlineData("\"Bayer\":9")]
    public void Json_UndefinedIntegerEnum_IsReadButFailsValidate(string member)
    {
        // 数値は読み込むが、未定義の値は Validate で弾く(記憶の読み込みはその記憶だけを捨てる)
        RawFormat format = JsonSerializer.Deserialize<RawFormat>(
            $"{{\"Width\":2,\"Height\":1,\"BitDepth\":12,{member}}}")!;

        Assert.Throws<ArgumentException>(format.Validate);
        Assert.False(FormatHistory.CanOpen(format, fileSize: 1000));
    }

    [Fact]
    public void Json_DefinedNamesAndIntegers_RoundTrip()
    {
        var format = new RawFormat
        {
            Width = 2, Height = 1, BitDepth = 12, Packing = BitPacking.Msb, Endianness = Endianness.Big,
            Bayer = BayerPattern.Gbrg, Hdr = HdrMode.LineInterleaved,
        };
        string json = JsonSerializer.Serialize(format);

        Assert.Contains("\"Packing\":\"Msb\"", json);
        Assert.Contains("\"Endianness\":\"Big\"", json);
        Assert.Contains("\"Bayer\":\"Gbrg\"", json);
        Assert.Equal(format, JsonSerializer.Deserialize<RawFormat>(json));
        Assert.Equal(BayerPattern.Grbg, JsonSerializer.Deserialize<RawFormat>(
            "{\"Width\":2,\"Height\":1,\"Bayer\":\"grbg\"}")!.Bayer);
        Assert.Equal(Endianness.Big, JsonSerializer.Deserialize<RawFormat>(
            "{\"Width\":2,\"Height\":1,\"Endianness\":1}")!.Endianness);
    }
}
