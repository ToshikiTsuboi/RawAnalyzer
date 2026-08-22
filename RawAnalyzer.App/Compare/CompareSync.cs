namespace RawAnalyzer.App.Compare;

/// <summary>ペイン間同期のモード。</summary>
public enum CompareSyncMode
{
    /// <summary>同期しない(各ペイン独立)。</summary>
    Off,

    /// <summary>
    /// 視野同期: 画像に対する相対的な表示範囲を合わせる。
    /// 解像度の違うカメラで同じシーンを撮った画像の構図比較向け。
    /// </summary>
    FieldOfView,

    /// <summary>
    /// 等倍同期: ズーム倍率(1画素=1px)を合わせ、位置は相対で追従する。
    /// ノイズ感・解像感を等倍で見比べる用途向け。
    /// </summary>
    PixelZoom,
}

/// <summary>ビュー変換(ズームと表示原点)。</summary>
/// <param name="Zoom">ズーム倍率。</param>
/// <param name="OriginX">表示原点X(画面左上に写る画像X座標)。</param>
/// <param name="OriginY">表示原点Y。</param>
public readonly record struct ViewTransform(double Zoom, double OriginX, double OriginY);

/// <summary>ペインのビュー状態(同期計算の入力)。</summary>
/// <param name="Zoom">ズーム倍率。</param>
/// <param name="OriginX">表示原点X。</param>
/// <param name="OriginY">表示原点Y。</param>
/// <param name="ViewWidth">ビューポートの幅(px)。</param>
/// <param name="ViewHeight">ビューポートの高さ(px)。</param>
/// <param name="ImageWidth">画像の幅(画素)。</param>
/// <param name="ImageHeight">画像の高さ(画素)。</param>
public readonly record struct PaneViewState(
    double Zoom,
    double OriginX,
    double OriginY,
    double ViewWidth,
    double ViewHeight,
    int ImageWidth,
    int ImageHeight);

/// <summary>
/// ペイン間のビュー・カーソル同期の座標計算(純関数)。
/// </summary>
/// <remarks>
/// 解像度の違う画像間に「同じ画素」は存在しないため、位置は常に
/// 画像に対する相対位置(0〜1)を介して写像する。
/// </remarks>
public static class CompareSync
{
    /// <summary>
    /// ソースペインのビュー状態を、ターゲットペインのビュー変換へ写像する。
    /// </summary>
    /// <param name="mode">同期モード(Offは呼び出し側で除外すること)。</param>
    /// <param name="source">操作されたペインの状態。</param>
    /// <param name="target">追従させるペインの状態(Zoom等は現在値)。</param>
    /// <returns>ターゲットへ適用するビュー変換。</returns>
    public static ViewTransform MapView(
        CompareSyncMode mode, PaneViewState source, PaneViewState target)
    {
        // 表示中心の相対位置(ソース基準)
        double centerX = source.OriginX + source.ViewWidth / (2 * source.Zoom);
        double centerY = source.OriginY + source.ViewHeight / (2 * source.Zoom);
        double relX = centerX / source.ImageWidth;
        double relY = centerY / source.ImageHeight;

        double zoom;
        if (mode == CompareSyncMode.PixelZoom)
        {
            zoom = source.Zoom;
        }
        else
        {
            // 視野同期: 画面に写る画像幅の「画像幅に対する比率」を合わせる。
            // ソースで写っている相対幅 = (viewW/zoom) / imageW。
            // ターゲットで同じ相対幅になる zoom を解く
            double relWidth = source.ViewWidth / source.Zoom / source.ImageWidth;
            zoom = target.ViewWidth / (relWidth * target.ImageWidth);
        }

        double targetCenterX = relX * target.ImageWidth;
        double targetCenterY = relY * target.ImageHeight;
        return new ViewTransform(
            zoom,
            targetCenterX - target.ViewWidth / (2 * zoom),
            targetCenterY - target.ViewHeight / (2 * zoom));
    }

    /// <summary>
    /// カーソル画素座標を相対位置でターゲット画像の座標へ写像する。
    /// </summary>
    /// <param name="x">ソースの画素X。</param>
    /// <param name="y">ソースの画素Y。</param>
    /// <param name="source">ソースの画像サイズ。</param>
    /// <param name="target">ターゲットの画像サイズ。</param>
    /// <returns>ターゲット画像上の座標(小数。画素中心基準)。</returns>
    public static (double X, double Y) MapCursor(
        int x, int y, (int Width, int Height) source, (int Width, int Height) target)
    {
        // 画素中心 (x+0.5) の相対位置を保って写像する
        double relX = (x + 0.5) / source.Width;
        double relY = (y + 0.5) / source.Height;
        return (relX * target.Width - 0.5, relY * target.Height - 0.5);
    }
}
