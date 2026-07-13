using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace RawViewer.App.Views;

/// <summary>
/// 指定画素を通る水平/垂直ラインのraw値折れ線を表示するウィンドウ。
/// </summary>
public partial class LineProfileWindow : Window
{
    private ushort[] _rowProfile = Array.Empty<ushort>();
    private ushort[] _columnProfile = Array.Empty<ushort>();
    private int _pointX;
    private int _pointY;
    private int _maxCode = 65535;

    /// <summary>ウィンドウを生成する。</summary>
    public LineProfileWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// プロファイルデータを設定して再描画する。
    /// </summary>
    /// <param name="rowProfile">クリック行の水平プロファイル(raw code)。</param>
    /// <param name="columnProfile">クリック列の垂直プロファイル(raw code)。</param>
    /// <param name="pointX">クリック画素X。</param>
    /// <param name="pointY">クリック画素Y。</param>
    /// <param name="maxCode">ビット深度の最大raw code。</param>
    public void SetProfiles(
        ushort[] rowProfile, ushort[] columnProfile, int pointX, int pointY, int maxCode)
    {
        _rowProfile = rowProfile;
        _columnProfile = columnProfile;
        _pointX = pointX;
        _pointY = pointY;
        _maxCode = Math.Max(1, maxCode);
        Redraw();
    }

    private void OnDirectionChanged(object sender, RoutedEventArgs e)
    {
        Redraw();
    }

    private string? BuildTable(char separator)
    {
        bool horizontal = HorizontalRadio?.IsChecked != false;
        ushort[] data = horizontal ? _rowProfile : _columnProfile;
        if (data.Length == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append(horizontal ? "x" : "y").Append(separator).Append("raw_code").AppendLine();
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append(i).Append(separator).Append(data[i]).AppendLine();
        }

        return sb.ToString();
    }

    private void OnCopyDataClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildTable('\t');
        if (table is not null)
        {
            Clipboard.SetText(table);
        }
    }

    private void OnSaveCsvClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildTable(',');
        if (table is null)
        {
            return;
        }

        bool horizontal = HorizontalRadio?.IsChecked != false;
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = horizontal ? $"profile_y{_pointY}.csv" : $"profile_x{_pointX}.csv",
        };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, table, Encoding.UTF8);
        }
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Redraw();
    }

    private void Redraw()
    {
        if (PlotCanvas is null)
        {
            return;
        }

        PlotCanvas.Children.Clear();
        bool horizontal = HorizontalRadio?.IsChecked != false;
        ushort[] data = horizontal ? _rowProfile : _columnProfile;
        double width = PlotCanvas.ActualWidth;
        double height = PlotCanvas.ActualHeight;
        if (data.Length < 2 || width < 4 || height < 4)
        {
            return;
        }

        int min = int.MaxValue;
        int max = int.MinValue;
        foreach (ushort v in data)
        {
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }
        }

        InfoText.Text = horizontal
            ? $"y={_pointY}  N={data.Length}  min={min}  max={max}"
            : $"x={_pointX}  N={data.Length}  min={min}  max={max}";
        MaxLabel.Text = _maxCode.ToString();
        MinLabel.Text = "0";

        // キャンバス幅より点数が多い場合は列ごとにmin/maxを引いて情報を残す
        var points = new PointCollection();
        double scaleY = (height - 2) / _maxCode;
        if (data.Length <= (int)width)
        {
            double stepX = width / (data.Length - 1);
            for (int i = 0; i < data.Length; i++)
            {
                points.Add(new Point(i * stepX, height - 1 - data[i] * scaleY));
            }
        }
        else
        {
            int columns = (int)width;
            for (int c = 0; c < columns; c++)
            {
                long start = (long)c * data.Length / columns;
                long end = Math.Max(start + 1, (long)(c + 1) * data.Length / columns);
                int cmin = int.MaxValue;
                int cmax = int.MinValue;
                for (long i = start; i < end; i++)
                {
                    ushort v = data[i];
                    if (v < cmin)
                    {
                        cmin = v;
                    }

                    if (v > cmax)
                    {
                        cmax = v;
                    }
                }

                points.Add(new Point(c, height - 1 - cmax * scaleY));
                points.Add(new Point(c, height - 1 - cmin * scaleY));
            }
        }

        var line = new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0x9D, 0xD9)),
            StrokeThickness = 1,
        };
        PlotCanvas.Children.Add(line);

        // クリック位置マーカー
        int index = horizontal ? _pointX : _pointY;
        if (index >= 0 && index < data.Length)
        {
            double markerX = data.Length <= (int)width
                ? index * (width / (data.Length - 1))
                : (double)index / data.Length * width;
            var marker = new Line
            {
                X1 = markerX,
                X2 = markerX,
                Y1 = 0,
                Y2 = height,
                Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xD9, 0x9B, 0x5B)),
                StrokeThickness = 1,
            };
            PlotCanvas.Children.Add(marker);
        }
    }
}
