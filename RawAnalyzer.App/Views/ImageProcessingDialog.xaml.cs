using System.Windows;
using System.Windows.Controls;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>ビニングまたはフィルタの選択結果。</summary>
/// <param name="Factor">ビニング係数。</param>
/// <param name="Mode">ビニング集約方法。</param>
/// <param name="Filter">フィルタ設定。nullならビニング。</param>
/// <param name="Label">処理履歴用ラベル。</param>
public sealed record ImageProcessingChoice(
    int Factor, BinningMode Mode, ImageFilterOptions? Filter, string Label);

/// <summary>デジタルビニング・空間フィルタの設定と出力寸法の確認画面。</summary>
public partial class ImageProcessingDialog : Window
{
    private readonly int _width;
    private readonly int _height;
    private readonly BayerPattern _pattern;
    private readonly bool _color;
    private bool _ready;

    /// <summary>入力形式に応じた処理設定画面を生成する。</summary>
    /// <param name="sourceName">対象の表示名。</param>
    /// <param name="width">入力幅。</param>
    /// <param name="height">入力高さ。</param>
    /// <param name="pattern">現在のCFA。</param>
    /// <param name="color">RGB画像か。</param>
    /// <param name="binning">最初にビニングを選ぶか。</param>
    public ImageProcessingDialog(string sourceName, int width, int height,
        BayerPattern pattern, bool color, bool binning)
    {
        _width = width;
        _height = height;
        _pattern = color ? BayerPattern.None : pattern;
        _color = color;
        InitializeComponent();
        string layout = color ? "RGB" : pattern == BayerPattern.None ? "モノクロ" : $"Bayer {pattern.ToString().ToUpperInvariant()}";
        SourceText.Text = $"{sourceName}\n{width}×{height} · {layout}";
        SourceText.ToolTip = SourceText.Text;
        OperationCombo.SelectedIndex = binning ? 0 : 2;
        _ready = true;
        UpdatePreview();
    }

    /// <summary>適用ボタンで確定された設定。</summary>
    public ImageProcessingChoice? Result { get; private set; }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            UpdatePreview();
        }
    }

    internal ImageProcessingChoice ReadChoice()
    {
        int factor = FactorCombo.SelectedIndex switch { 1 => 3, 2 => 4, 3 => 8, _ => 2 };
        BinningMode mode = AggregationCombo.SelectedIndex == 1 ? BinningMode.Sum : BinningMode.Average;
        if (OperationCombo.SelectedIndex == 0)
        {
            return new(factor, mode, null, $"{factor}×{factor}ビニング ({(mode == BinningMode.Sum ? "加算" : "平均")})");
        }

        ImageFilterKind kind = OperationCombo.SelectedIndex switch
        {
            1 => ImageFilterKind.Mean,
            2 => ImageFilterKind.Gaussian,
            3 => ImageFilterKind.Median,
            4 => ImageFilterKind.UnsharpMask,
            5 => ImageFilterKind.Sobel,
            6 => ImageFilterKind.Minimum,
            _ => ImageFilterKind.Maximum,
        };
        int radius = kind == ImageFilterKind.Sobel ? 1 : KernelCombo.SelectedIndex + 1;
        double sigma = kind is ImageFilterKind.Gaussian or ImageFilterKind.UnsharpMask
            ? ParseNumber(SigmaBox, "σ") : 1;
        double amount = kind == ImageFilterKind.UnsharpMask ? ParseNumber(AmountBox, "強度") : 1;
        var options = new ImageFilterOptions(kind, radius, sigma, amount);
        options.Validate();
        string name = kind switch
        {
            ImageFilterKind.Mean => "平均化",
            ImageFilterKind.Gaussian => "ガウシアン",
            ImageFilterKind.Median => "メディアン",
            ImageFilterKind.UnsharpMask => "アンシャープ",
            ImageFilterKind.Sobel => "Sobelエッジ",
            ImageFilterKind.Minimum => "最小値",
            _ => "最大値",
        };
        string label = $"{name} {radius * 2 + 1}×{radius * 2 + 1}";
        if (kind is ImageFilterKind.Gaussian or ImageFilterKind.UnsharpMask)
        {
            label += $" σ={sigma:G3}";
        }

        if (kind == ImageFilterKind.UnsharpMask)
        {
            label += $" 強度={amount:G3}";
        }

        return new(factor, mode, options, label);
    }

    private static double ParseNumber(TextBox box, string name)
    {
        // 他の数値入力欄と同じく、IME がオンのまま打った全角の数字・記号も読む
        if (!NumericInput.TryParseFinite(box.Text, out double value))
        {
            throw new FieldFormatException(box, $"{name}は有限の数値で指定してください。");
        }

        return value;
    }

    /// <summary>説明に出した不正の原因の欄。欄に結び付かない不正(画素数の上限など)なら null。</summary>
    private TextBox? InvalidField(Exception ex) => ex switch
    {
        FieldFormatException field => field.Field,
        ArgumentException { ParamName: nameof(ImageFilterOptions.Sigma) } => SigmaBox,
        ArgumentException { ParamName: nameof(ImageFilterOptions.Amount) } => AmountBox,
        _ => null,
    };

    private bool UpdatePreview()
    {
        bool binning = OperationCombo.SelectedIndex == 0;
        bool gaussian = OperationCombo.SelectedIndex is 2 or 4;
        BinningPanel.Visibility = binning ? Visibility.Visible : Visibility.Collapsed;
        KernelPanel.Visibility = !binning && OperationCombo.SelectedIndex != 5 ? Visibility.Visible : Visibility.Collapsed;
        SigmaPanel.Visibility = gaussian ? Visibility.Visible : Visibility.Collapsed;
        AmountPanel.Visibility = OperationCombo.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

        // 不正な欄は、ファイル一覧の絞り込み欄と同じく赤枠とツールチップの理由で示す(以前は下の説明だけで、
        // どの欄が悪いのかは欄の見た目で分からなかった)。使わない(隠れた)欄は読まないので知らせない
        InputFeedback.SetError(SigmaBox, null);
        InputFeedback.SetError(AmountBox, null);
        try
        {
            ImageProcessingChoice choice = ReadChoice();
            var size = binning
                ? ImageBinning.GetDimensions(_width, _height, choice.Factor, _pattern)
                : (Width: _width, Height: _height, CroppedRight: 0, CroppedBottom: 0);
            if ((long)size.Width * size.Height > RawLoader.DefaultInMemoryPixelThreshold)
            {
                throw new NotSupportedException("処理結果は1億画素以下にしてください。");
            }

            PreviewText.Text = $"{_width}×{_height} → {size.Width}×{size.Height} · 16bit"
                + (size.CroppedRight > 0 || size.CroppedBottom > 0
                    ? $"\n右端{size.CroppedRight}列・下端{size.CroppedBottom}行を除外します。" : "");
            string channels = _color ? "RGBの各成分を独立に処理します。"
                : _pattern == BayerPattern.None ? "モノクロ画素を処理します。"
                : "R・Gr・Gb・Bを個別に処理し、元のBayer配列を維持します。";
            PolicyText.Text = channels + (binning
                ? _pattern == BayerPattern.None ? "" : $"\n{choice.Factor * 2}×{choice.Factor * 2}の入力から2×2のCFAを生成します。"
                : "\n端は同色の端画素を複製します。"
                  + (choice.Filter!.Kind == ImageFilterKind.Sobel ? " Sobelは勾配強度を1/4倍します。" : ""))
                + (binning && choice.Mode == BinningMode.Sum
                    ? $"\n最大{choice.Factor * choice.Factor}倍に加算し、正規化した16bit表示値の65535で飽和します。"
                      + "\n12bit素材でも14bit等への階調拡張は行いません。" : "");
            ErrorText.Text = "";
            RunButton.IsEnabled = true;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            PreviewText.Text = "設定を確認してください。";
            PolicyText.Text = "";
            ErrorText.Text = UserMessage(ex);
            if (InvalidField(ex) is { } field)
            {
                InputFeedback.SetError(field, ErrorText.Text);
            }

            RunButton.IsEnabled = false;
            return false;
        }
    }

    /// <summary>例外の説明を利用者向けの文にする。</summary>
    /// <remarks>
    /// 引数名を持つ <see cref="ArgumentException"/> の Message には「 (Parameter 'Sigma')」のような開発用の
    /// 引数名が付く(範囲外の σ・強度で「σは0.1〜10の有限値です。 (Parameter 'Sigma')」と出ていた)。
    /// 付け足される文は実行環境の言語で決まるので、同じ引数名の空の例外の Message を作って末尾から外す。
    /// </remarks>
    private static string UserMessage(Exception ex)
    {
        string message = ex.Message;
        if (ex is ArgumentException { ParamName: { Length: > 0 } name })
        {
            string suffix = new ArgumentException("", name).Message;
            if (suffix.Length > 0 && message.EndsWith(suffix, StringComparison.Ordinal))
            {
                message = message[..^suffix.Length];
            }
        }

        return message;
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (UpdatePreview())
        {
            Result = ReadChoice();
            DialogResult = true;
        }
    }

    /// <summary>欄の数値が読めないことを示す例外。どの欄かを持つ。</summary>
    private sealed class FieldFormatException(TextBox field, string message) : FormatException(message)
    {
        public TextBox Field { get; } = field;
    }
}
