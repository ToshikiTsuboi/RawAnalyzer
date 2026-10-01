namespace RawAnalyzer.App.Rendering;

/// <summary>
/// 表示倍率(Windows の表示スケール。1 DIP あたりのデバイス画素数)とビューポートのズームの換算(純関数)。
/// </summary>
/// <remarks>
/// ビューポートのズームは DIP 基準(表示 DIP / 元画像画素)で持ち、マウス位置・ROI・マーカーもこの単位で扱う。
/// 画面での細かさはデバイス画素で決まるので、描画の大きさ・縮小レベルの選択・等倍・raw 値オーバーレイの閾値・
/// 倍率の表示はデバイス基準(ズーム×表示倍率)で決める。表示倍率 1(100%)では DIP とデバイス画素が一致し、
/// どれも倍率を考えない場合と同じ値になる。
/// </remarks>
public static class DeviceScaling
{
    /// <summary>raw 値オーバーレイを描く最小のデバイス基準ズーム(元画像 1 画素が 32 デバイス画素以上)。</summary>
    public const double RawOverlayMinDeviceZoom = 32;

    /// <summary>ビットマップの dpi の基準(1 DIP = 1/96 インチ)。</summary>
    private const double DipsPerInch = 96;

    /// <summary>
    /// 整数倍(または 1/整数倍)とみなす相対誤差。DIP 基準とデバイス基準の換算(÷倍率→×倍率)で入る
    /// 丸め誤差だけを吸収する。
    /// </summary>
    private const double RatioTolerance = 1e-9;

    /// <summary>表示倍率として使える値にする(0 以下・NaN・無限大は 1)。</summary>
    /// <param name="scale">表示倍率。</param>
    /// <returns>正の有限な倍率。</returns>
    public static double Normalize(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1.0;

    /// <summary>DIP の長さを、描画するデバイス画素数にする(最低 1)。</summary>
    /// <param name="dips">長さ(DIP)。</param>
    /// <param name="scale">表示倍率。</param>
    /// <returns>デバイス画素数。</returns>
    public static int DevicePixels(double dips, double scale) =>
        Math.Max(1, (int)Math.Round(dips * Normalize(scale)));

    /// <summary>描画するビットマップの解像度(dpi)。DIP の大きさで描くと 1 画素が 1 デバイス画素に写る。</summary>
    /// <param name="scale">表示倍率。</param>
    /// <returns>dpi。</returns>
    public static double BitmapDpi(double scale) => DipsPerInch * Normalize(scale);

    /// <summary>DIP 基準のズームをデバイス基準(デバイス画素 / 元画像画素)にする。</summary>
    /// <remarks>
    /// 整数倍・1/整数倍に丸め誤差の範囲で近い値はその値にそろえる。等倍(1/倍率)を倍率で戻した値が
    /// 1 からわずかにずれたまま描くと、最近傍の描画で元画像の列の対応が途中で 1 つずれて 1 列だけ
    /// 2 デバイス画素に写り、raw 値オーバーレイの閾値(32 倍)も 31.999… で外れる。
    /// </remarks>
    /// <param name="zoom">DIP 基準のズーム。</param>
    /// <param name="scale">表示倍率。</param>
    /// <returns>デバイス基準のズーム。</returns>
    public static double ToDeviceZoom(double zoom, double scale) => SnapToIntegerRatio(zoom * Normalize(scale));

    /// <summary>デバイス基準のズームを DIP 基準にする。</summary>
    /// <param name="deviceZoom">デバイス基準のズーム。</param>
    /// <param name="scale">表示倍率。</param>
    /// <returns>DIP 基準のズーム。</returns>
    public static double FromDeviceZoom(double deviceZoom, double scale) => deviceZoom / Normalize(scale);

    /// <summary>等倍(元画像 1 画素 = 1 デバイス画素)の DIP 基準ズーム。</summary>
    /// <param name="scale">表示倍率。</param>
    /// <returns>DIP 基準のズーム(1/倍率)。</returns>
    public static double ActualSizeZoom(double scale) => FromDeviceZoom(1, scale);

    /// <summary>
    /// 表示倍率が変わったとき(別の倍率のモニタへ移した・表示スケールを変えたとき)の DIP 基準のズーム。
    /// </summary>
    /// <remarks>
    /// 等倍・整数倍の拡大(デバイス基準で 1, 2, 3, …倍)はデバイス基準のズームを保ち、元画像の 1 画素を
    /// 同じ数のデバイス画素に写し続ける(DIP 基準のまま保つと非整数倍になり、最近傍の縞が出る)。
    /// それ以外(全体表示や縮小表示など)は DIP 基準のズームを保ち、画面に占める大きさを変えない。
    /// </remarks>
    /// <param name="zoom">変わる前の DIP 基準のズーム。</param>
    /// <param name="oldScale">変わる前の表示倍率。</param>
    /// <param name="newScale">変わった後の表示倍率。</param>
    /// <returns>変わった後の DIP 基準のズーム。</returns>
    public static double ZoomAfterScaleChange(double zoom, double oldScale, double newScale)
    {
        double device = ToDeviceZoom(zoom, oldScale);
        return device >= 1 && device == Math.Floor(device) ? FromDeviceZoom(device, newScale) : zoom;
    }

    /// <summary>raw 値オーバーレイを描くズームか(元画像 1 画素が 32 デバイス画素以上)。</summary>
    /// <param name="zoom">DIP 基準のズーム。</param>
    /// <param name="scale">表示倍率。</param>
    /// <returns>描くならtrue。</returns>
    public static bool ShowsRawOverlay(double zoom, double scale) =>
        ToDeviceZoom(zoom, scale) >= RawOverlayMinDeviceZoom;

    /// <summary>raw 値オーバーレイが出る最小の DIP 基準ズーム(拡大して画素値を見せる操作が使う)。</summary>
    /// <param name="scale">表示倍率。</param>
    /// <returns>DIP 基準のズーム。</returns>
    public static double RawOverlayZoom(double scale) => FromDeviceZoom(RawOverlayMinDeviceZoom, scale);

    /// <summary>raw 値オーバーレイの文字の大きさ(DIP)。</summary>
    /// <remarks>
    /// 1 画素のマスの幅(DIP 基準のズーム)の 1/4.5 を 9〜15 DIP に収める。高DPIではオーバーレイの出る
    /// マスが 32 DIP より小さい(200% で 16 DIP)ので、最小の 9 DIP ではコード値 5 桁がマスからはみ出す。
    /// そのときはマスの 1/3.2(Consolas の 5 桁でマスの約 86%)まで小さくする。倍率 1 で出るズーム(32 以上)では
    /// 従来と同じ大きさ。
    /// </remarks>
    /// <param name="zoom">DIP 基準のズーム。</param>
    /// <returns>文字の大きさ(DIP)。</returns>
    public static double RawOverlayFontSize(double zoom) => Math.Clamp(zoom / 4.5, Math.Min(9, zoom / 3.2), 15);

    /// <summary>描画に使う縮小レベルの縮小率を選ぶ。</summary>
    /// <remarks>
    /// 品質パスはデバイス基準のズームで選ぶ(デバイス 1 画素にレベルの 1〜2 画素。全体表示もモニタの解像度で
    /// 細かく描く)。読む画素数はデバイス画素数(倍率の 2 乗)に比例して増えるので、操作中の速報は
    /// DIP 基準のズームで選んだレベルから 1 段粗いレベルにし、読み出し量を倍率 1 のときと同じに保つ。
    /// ただしデバイス基準で等倍以上(品質パスの縮小率が 1)では読み出し画素数が元々少なく利点がないうえ、
    /// 平均がブロックとして見えるため落とさない(高DPIの等倍で操作中だけ粗いレベルに替わらないように)。
    /// </remarks>
    /// <param name="selectFactor">ズームに見合う縮小率を返す関数(<c>TilePyramid.SelectFactor</c> など)。</param>
    /// <param name="hasLevel">その縮小率のレベルがあるか。</param>
    /// <param name="zoom">DIP 基準のズーム。</param>
    /// <param name="scale">表示倍率。</param>
    /// <param name="fast">操作中の速報か。</param>
    /// <returns>縮小率(1=元画像)。</returns>
    public static int SelectRenderFactor(
        Func<double, int> selectFactor, Func<int, bool> hasLevel, double zoom, double scale, bool fast)
    {
        int quality = selectFactor(ToDeviceZoom(zoom, scale));
        if (!fast || quality <= 1)
        {
            return quality;
        }

        int factor = selectFactor(zoom);
        return factor > 1 && hasLevel(factor * 2) ? factor * 2 : factor;
    }

    private static double SnapToIntegerRatio(double value)
    {
        if (!(value > 0) || !double.IsFinite(value))
        {
            return value;
        }

        if (value >= 1)
        {
            double whole = Math.Round(value);
            return Math.Abs(value - whole) <= RatioTolerance * whole ? whole : value;
        }

        double inverse = 1 / value;
        double divisor = Math.Round(inverse);
        return Math.Abs(inverse - divisor) <= RatioTolerance * divisor ? 1 / divisor : value;
    }
}
