namespace RawAnalyzer.App.Services;

/// <summary>
/// 射影の窓の向き。EMVA 1288 にならい、結果を並べる軸で呼ぶ(平均する方向で呼ぶ流儀とは逆になる)。
/// </summary>
internal enum ProjectionDirection
{
    /// <summary>水平射影: 各列を縦に平均した値を x 座標に並べる。</summary>
    Horizontal,

    /// <summary>垂直射影: 各行を横に平均した値を y 座標に並べる。</summary>
    Vertical,
}

/// <summary>
/// 射影の向きと流儀の説明(ツールバー・メニュー・コマンドパレット・窓の「？」・横軸の見出しで同じ文言を使う)。
/// </summary>
/// <remarks>
/// 「水平射影」は流儀によって逆の向きを指す。EMVA 1288 は結果を並べる軸で呼び(水平射影 = 各列の平均を x に並べる
/// horizontal profile)、HALCON の gray_projections や文書解析では平均する方向で呼ぶ(各行を横に平均したものを
/// 水平射影と呼ぶ)。このアプリは EMVA 1288 方式なので、向きを読み違えないよう説明と横軸の見出しに向きを書く。
/// </remarks>
internal static class ProjectionText
{
    /// <summary>窓・ボタンの名前。</summary>
    /// <param name="direction">向き。</param>
    /// <returns>「水平射影」または「垂直射影」。</returns>
    internal static string Name(ProjectionDirection direction) =>
        direction == ProjectionDirection.Horizontal ? "水平射影" : "垂直射影";

    /// <summary>平均の取り方(「列ごとの平均」「行ごとの平均」)。</summary>
    /// <param name="direction">向き。</param>
    /// <returns>平均の取り方。</returns>
    internal static string Averaging(ProjectionDirection direction) =>
        direction == ProjectionDirection.Horizontal ? "列ごとの平均" : "行ごとの平均";

    /// <summary>座標の名前(「x」「y」)。</summary>
    /// <param name="direction">向き。</param>
    /// <returns>座標の名前。</returns>
    internal static string Axis(ProjectionDirection direction) =>
        direction == ProjectionDirection.Horizontal ? "x" : "y";

    /// <summary>
    /// 向きと流儀の説明。ツールバーのボタンのツールチップと窓の「？」の先頭に出す。
    /// </summary>
    /// <param name="direction">向き。</param>
    /// <returns>説明文。</returns>
    internal static string Explanation(ProjectionDirection direction) => direction == ProjectionDirection.Horizontal
        ? "水平射影: 各列を縦に平均した値を x 座標に並べます。縦筋・列ごとのオフセット(列 FPN)を見ます。" +
          "EMVA 1288 の horizontal profile と同じ向きです。" +
          "平均する方向で呼ぶ流儀(HALCON など)では「垂直射影」にあたります。"
        : "垂直射影: 各行を横に平均した値を y 座標に並べます。横筋・行ごとのオフセット(行 FPN)、" +
          "縦方向のシェーディングを見ます。EMVA 1288 の vertical profile と同じ向きです。" +
          "平均する方向で呼ぶ流儀(HALCON など)では「水平射影」にあたります。";

    /// <summary>ツールバーのボタンのツールチップ。</summary>
    /// <param name="direction">向き。</param>
    /// <returns>ツールチップの文。</returns>
    internal static string ToolbarToolTip(ProjectionDirection direction) =>
        Explanation(direction) + "\n\n" +
        $"押すとすぐ計算して{Name(direction)}の窓を開きます(もう一度押すと閉じます)。" +
        "対象は ROI があれば ROI、なければ画像全体です。";

    /// <summary>窓の「？」のツールチップ。</summary>
    /// <param name="direction">向き。</param>
    /// <returns>ツールチップの文。</returns>
    internal static string WindowHelp(ProjectionDirection direction)
    {
        string position = direction == ProjectionDirection.Horizontal ? "列" : "行";
        return Explanation(direction) + "\n\n" +
            $"最大・最小: 各{position}の画素の最大値・最小値の線です(EMVA 1288 と同じ)。チェックで消せます。\n" +
            "対象: ROI があれば ROI、なければ画像全体の全画素です(間引きません)。" +
            "Bayer 画像はチャネルを混ぜた画素値の平均です。\n" +
            "チャネル分割表示では、ROI を置いた象限のチャネルの画素だけで射影を取ります" +
            "(象限全体を囲むとそのチャネル全体になります)。ROI がなければ、上の「チャネル」で選んだチャネル全体の" +
            "射影を取ります(初めは未選択。ROI を描いているときは ROI を優先します)。\n" +
            "ROI の変更・解除、フレーム・ページ・ファイルの送り(再生中は停止したとき)、" +
            "別のファイル・処理結果・HDR 表示への切り替えで計算し直します。";
    }

    /// <summary>窓のチャネルの選択欄のツールチップ。</summary>
    internal const string ChannelChoiceToolTip =
        "チャネル分割表示で ROI がないときに、選んだチャネル全体(その象限全体)の射影を取ります。" +
        "初めは未選択です(ソフトの側でチャネルを選びません)。水平射影・垂直射影の窓で共通です。\n" +
        "同じ大きさの画像の間(フレーム・ページ・ファイルの送り)は保ち、チャネル分割表示を抜けたとき・別のファイルを" +
        "開いたとき・画像の大きさが変わったときは未選択に戻ります。ROI を描いているときは ROI を優先します。";

    /// <summary>ROI を優先していることの知らせのツールチップ。</summary>
    internal const string RoiTakesPriorityToolTip =
        "ROI を描いているので、ROI の画素で射影を取ります(チャネルの選択は使いません)。" +
        "ROI を解除すると、選んだチャネル全体の射影に戻ります。";

    /// <summary>横軸の見出し(向きと座標の種類を示す)。</summary>
    /// <param name="direction">向き。</param>
    /// <param name="splitDisplayCoordinates">チャネル分割表示の座標で並べるか。</param>
    /// <returns>見出し。</returns>
    internal static string AxisTitle(ProjectionDirection direction, bool splitDisplayCoordinates) =>
        $"{Name(direction)} — {Axis(direction)} 座標({Averaging(direction)}) " +
        $"[px・{(splitDisplayCoordinates ? "チャネル分割表示の座標" : "画像座標")}]";

    /// <summary>横軸のツールチップ。</summary>
    /// <param name="splitDisplayCoordinates">チャネル分割表示の座標で並べるか。</param>
    /// <returns>ツールチップの文。</returns>
    internal static string AxisToolTip(bool splitDisplayCoordinates) => splitDisplayCoordinates
        ? "チャネル分割表示(R/Gr/Gb/B の2×2並置)上で ROI を描いた座標。元画像の列・行ではありません" +
          "(コピー・CSV には元画像の座標の列も出します)。ホイールで横軸だけ拡大・縮小できます。"
        : "元画像上の画素座標。ホイールで横軸だけ拡大・縮小できます。";
}
