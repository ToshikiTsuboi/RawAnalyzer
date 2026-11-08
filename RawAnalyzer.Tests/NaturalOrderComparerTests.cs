using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class NaturalOrderComparerTests
{
    [Theory]
    [InlineData("img2.raw", "img10.raw", -1)]      // 桁数違いの数値ブロック
    [InlineData("img002.raw", "img2.raw", 0)]      // 先頭の 0 は無視
    [InlineData("a.raw", "b.raw", -1)]             // 非数字の比較
    [InlineData("frame1a.raw", "frame1b.raw", -1)] // 数値ブロックが等しければ続きで決まる
    public void Compare_OrdersNaturally(string x, string y, int expectedSign)
    {
        int result = NaturalOrderComparer.Instance.Compare(x, y);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public void Sort_NumberedSequence_IsInNumericOrder()
    {
        var files = new List<string> { "f10.raw", "f2.raw", "f1.raw", "f20.raw", "f3.raw" };
        files.Sort(NaturalOrderComparer.Instance);
        Assert.Equal(new[] { "f1.raw", "f2.raw", "f3.raw", "f10.raw", "f20.raw" }, files);
    }

    /// <summary>
    /// IME がオンのまま付けた全角数字の連番も数値として比べる。以前は全角数字を1文字ずつ文字コードで比べ、
    /// 「暗室１, 暗室１０, 暗室１１, 暗室１２, 暗室２…」と並べた(連番の再生・送り・一括書き出しの順になる)。
    /// </summary>
    [Theory]
    [InlineData("img２.raw", "img１０.raw", -1)]
    [InlineData("img２.raw", "img10.raw", -1)]  // 全角と半角が混ざっても数値で比べる
    [InlineData("img2.raw", "img１０.raw", -1)]
    [InlineData("img０２.raw", "img2.raw", 0)]  // 全角の先頭の 0 も無視
    [InlineData("img１.raw", "img1.raw", 0)]
    [InlineData("img１a.raw", "img1b.raw", -1)]
    public void Compare_FullWidthDigits_AreComparedAsNumbers(string x, string y, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(NaturalOrderComparer.Instance.Compare(x, y)));
        Assert.Equal(-expectedSign, Math.Sign(NaturalOrderComparer.Instance.Compare(y, x)));
    }

    [Fact]
    public void Sort_FullWidthNumberedSequence_IsInNumericOrder()
    {
        string[] expected = Enumerable.Range(1, 12).Select(i => "暗室" + ToFullWidth(i) + ".raw").ToArray();
        var files = Enumerable.Reverse(expected).ToList();
        files.Sort(NaturalOrderComparer.Instance);
        Assert.Equal(expected, files);
    }

    /// <summary>
    /// 全角数字・半角数字・その他の文字が混ざっても、並べ替えに要る全順序の性質(反対称・推移律)を保つ。
    /// 全角数字を数値として比べるだけで、全角数字と他の文字の大小が半角数字と食い違うと推移律が崩れる。
    /// </summary>
    [Fact]
    public void Compare_MixedDigits_IsConsistentTotalOrder()
    {
        char[] alphabet = { '0', '1', '2', '9', '０', '１', '２', '９', 'a', 'B', '_', '.', '/', ':', '～' };
        var random = new Random(1234);
        var strings = new List<string>();
        for (int n = 0; n < 160; n++)
        {
            int length = random.Next(0, 6);
            var chars = new char[length];
            for (int k = 0; k < length; k++)
            {
                chars[k] = alphabet[random.Next(alphabet.Length)];
            }

            strings.Add(new string(chars));
        }

        NaturalOrderComparer comparer = NaturalOrderComparer.Instance;
        foreach (string a in strings)
        {
            foreach (string b in strings)
            {
                int ab = Math.Sign(comparer.Compare(a, b));
                Assert.True(ab == -Math.Sign(comparer.Compare(b, a)), $"antisymmetry: '{a}' '{b}'");
                foreach (string c in strings)
                {
                    if (ab <= 0 && comparer.Compare(b, c) <= 0)
                    {
                        Assert.True(comparer.Compare(a, c) <= 0, $"transitivity: '{a}' <= '{b}' <= '{c}'");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 戻り値は IComparer の慣例どおり符号だけが意味を持つ(XML ドキュメントのとおり -1/1 とは限らない)。
    /// </summary>
    [Theory]
    [InlineData("a1", "a_")]               // 文字コードの差(-46)を返す
    [InlineData("x", "xyzzzzzzzzzzzzzzz")] // 残りの長さの差(-16)を返す
    [InlineData("img2", "img10")]
    public void Compare_SignTellsOrder(string x, string y)
    {
        Assert.True(NaturalOrderComparer.Instance.Compare(x, y) < 0);
        Assert.True(NaturalOrderComparer.Instance.Compare(y, x) > 0);
    }

    internal static string ToFullWidth(int number)
    {
        return new string(number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            .Select(c => (char)('０' + (c - '0'))).ToArray());
    }
}
