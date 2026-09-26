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
}
