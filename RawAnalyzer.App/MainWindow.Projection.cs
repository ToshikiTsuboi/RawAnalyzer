using System.Windows;
using System.Windows.Controls.Primitives;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

/// <summary>
/// 水平射影・垂直射影の窓のつなぎ込み(ツールバーのトグルとの同期、表示の状態から求め方を作る、計算し直しの契機)。
/// </summary>
/// <remarks>
/// <para>
/// 向きは EMVA 1288 方式(水平射影 = 各列を縦に平均して x に並べる、垂直射影 = 各行を横に平均して y に並べる)。
/// 対象は ROI があれば ROI、なければ画像全体(<see cref="ProjectionTargets"/>)。窓の開閉・計算・取り消しは
/// <see cref="ProjectionWindowController"/>。
/// </para>
/// <para>
/// 計算し直しの契機: ROI の変更・解除(OnViewportRoiChanged)、フレーム・ページ・ファイルの送り(再生中は停止したとき。
/// RefreshAfterSequenceMove)、別ファイルを開く・処理結果での差し替え・HDR 表示の出入り(ラインプロファイル窓と同じ箇所)、
/// Bayer の変更、チャネル分割表示の出入り(ROI がなくても対象が変わる)。送り・画像の差し替えでは CancelAnalysis で
/// 前の計算をすぐ取り消す。
/// </para>
/// </remarks>
public partial class MainWindow
{
    private ProjectionWindowController? _projections;

    /// <summary>射影の窓の管理(初回に作る)。</summary>
    private ProjectionWindowController Projections => _projections ??= CreateProjectionController();

    private ProjectionWindowController CreateProjectionController()
    {
        var controller = new ProjectionWindowController(
            Dispatcher,
            BuildProjectionRequest,
            () => _playTimer?.IsEnabled == true,
            (image, frame) => new AnalysisSource(image, frame).IsCurrent(ActiveImage, Viewport.Frame),
            CreateProjectionWindow,
            window => window.Show());

        // 窓の ✕ で閉じたらトグル(とメニューのチェック)も戻す
        controller.WindowClosed += direction => ProjectionToggleFor(direction).IsChecked = false;
        return controller;
    }

    /// <summary>ツールバーのトグルと、メニュー・キー(H / V)・コマンドパレットからの切り替え。</summary>
    private void OnProjectionToggleChanged(object sender, RoutedEventArgs e)
    {
        ProjectionDirection direction = ReferenceEquals(sender, HorizontalProjectionToggle)
            ? ProjectionDirection.Horizontal
            : ProjectionDirection.Vertical;
        if (((ToggleButton)sender).IsChecked == true)
        {
            Projections.Open(direction);
        }
        else
        {
            _projections?.Close(direction);
        }
    }

    private ToggleButton ProjectionToggleFor(ProjectionDirection direction) =>
        direction == ProjectionDirection.Horizontal ? HorizontalProjectionToggle : VerticalProjectionToggle;

    private ProjectionWindow CreateProjectionWindow(ProjectionDirection direction, ProjectionWindow? other)
    {
        // 開いてもメインウィンドウのフォーカスは移さない(続けて H / V・ROI の操作ができる。所有された窓なので前面に出る)
        var window = new ProjectionWindow(direction) { Owner = this, ShowActivated = false };

        // もう一方の窓が開いていれば、少しずらして重ならないように開く(並べて見比べられる)
        if (other is { IsLoaded: true })
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = other.Left + 40;
            window.Top = other.Top + 40;
        }

        return window;
    }

    /// <summary>
    /// 開いている射影の窓を、いまの表示(画像・フレーム・ROI・表示モード)で計算し直すよう求める
    /// (同じ UI の手番で重なる契機はまとめて1回だけ判断する。窓を開いていなければ何もしない)。
    /// </summary>
    private void RefreshProjections()
    {
        if (!_closed)
        {
            _projections?.Refresh();
        }
    }

    /// <summary>射影の計算を取り消す(送り・画像の差し替えのとき。取り消した計算は終わるときに計算し直しを求める)。</summary>
    private void CancelProjectionJob() => _projections?.CancelRunning();

    /// <summary>いまの表示での求め方を作る(画像がなければ null)。</summary>
    private ProjectionRequest? BuildProjectionRequest(ProjectionDirection direction)
    {
        if (ActiveImage is not { } image || ActiveFormat is not { } format)
        {
            return null;
        }

        // 画像の外(余白)だけをドラッグした画素数0の ROI は、ヒストグラムと同じく ROI なしとみなす
        RegionOfInterest? roi = Viewport.Roi is { PixelCount: > 0 } r ? r : null;
        RoiAnalysisTarget target = ProjectionTargets.Resolve(
            direction, roi, Viewport.IsChannelSplitLayout, image.Width, image.Height, format.Bayer,
            HdrSplitSegmentWidth);
        string header = ProjectionTargets.Describe(roi, target, image.Width, image.Height, ProjectionSourceNote(image));
        return new ProjectionRequest(direction, image, Viewport.Frame, target, header, (1 << format.BitDepth) - 1);
    }

    /// <summary>窓の上部に出す、表示中の画像の説明(HDR 表示・ページ・フレーム)。</summary>
    private string ProjectionSourceNote(RawImage image)
    {
        if (_derivedImage is not null)
        {
            // HDR 表示の画像は1フレーム。行交互 HDR は元にしたフレーム(1回の撮影)を示す。フレーム連結はフレームが
            // 露光そのものなので示さない
            string view = _hdrFloatImage is null ? "HDR分割ビュー" : "HDR合成ビュー";
            bool lineInterleaved = _currentFormat is { } format && HdrExposureMix.InFrame(format);
            return ProjectionTargets.SourceNote(
                _hdrSourceFrame.Frame, lineInterleaved ? _currentImage?.FrameCount ?? 1 : 1, 0, 0, view);
        }

        return ProjectionTargets.SourceNote(
            Viewport.Frame, image.FrameCount, _tiffPageIndex, _tiffStack?.PageCount ?? 0, hdrView: null);
    }

    /// <summary>HDR 分割ビューの段の幅(分割ビューでなければ0。合成ビュー・通常表示は段がない)。</summary>
    private int HdrSplitSegmentWidth => _derivedImage is not null && _hdrFloatImage is null ? _hdrSegmentWidth : 0;
}
