using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>射影の窓のチャネルの選択欄の見せ方。</summary>
internal enum ProjectionChannelChoice
{
    /// <summary>チャネル分割表示でないので出さない。</summary>
    None,

    /// <summary>チャネル分割表示で ROI がないので選べる(選んだチャネル全体の射影を取る)。</summary>
    Choosable,

    /// <summary>チャネル分割表示で ROI を描いているので ROI を優先する(選択は使わない)。</summary>
    RoiTakesPriority,
}

/// <summary>
/// 射影の窓で選んだチャネル(チャネル分割表示で ROI がないとき、そのチャネル全体 = その象限全体の射影を取る)。
/// 水平・垂直の窓で共有する(ROI と同じく、何の射影を取るかは表示の側で1つに決まる)。
/// </summary>
/// <remarks>
/// <para>
/// 初めは未選択で、ソフトの側でチャネルを選ばない。選んだチャネルは同じ大きさの画像の間(フレーム・ページ・連番のファイルの
/// 送り)、ROI を描いて消したとき、Bayer を直したとき(チャネルは同じで象限が変わるだけ)、窓を閉じて開き直したときは保つ。
/// </para>
/// <para>
/// チャネル分割表示を抜けたとき、画像の大きさが変わったとき(ビニングの結果など)、別のファイルを開いたとき
/// (<see cref="Forget"/>)は忘れる。抜けた後に戻ったり、別の画像を見たりしたときに、前に選んだチャネルを
/// 黙って使わない。
/// </para>
/// </remarks>
internal sealed class ProjectionChannelSelection
{
    private BayerChannel? _channel;

    // 選んだときの画像の大きさ(大きさが変わったら忘れる)
    private (int Width, int Height) _size;

    /// <summary>選んでいるチャネル(未選択なら null)。</summary>
    internal BayerChannel? Channel => _channel;

    /// <summary>チャネルを選ぶ(未選択に戻すなら null)。</summary>
    /// <param name="channel">チャネル。</param>
    /// <param name="view">選んだときの表示の状態(画像がない・チャネル分割表示でなければ選ばない)。</param>
    internal void Select(BayerChannel? channel, ProjectionView? view)
    {
        if (channel is null || view is null || !view.ChannelSplitLayout)
        {
            Forget();
            return;
        }

        _channel = channel;
        _size = (view.Image.Width, view.Image.Height);
    }

    /// <summary>選択を忘れる(別のファイルを開いた・チャネル分割表示を抜けた)。</summary>
    internal void Forget()
    {
        _channel = null;
        _size = default;
    }

    /// <summary>いまの表示で使う選択(チャネル分割表示を抜けた・画像の大きさが変わったなら忘れる)。</summary>
    /// <param name="view">いまの表示の状態。</param>
    /// <returns>チャネル(未選択なら null)。</returns>
    internal BayerChannel? Current(ProjectionView? view)
    {
        if (_channel is not null
            && (view is null || !view.ChannelSplitLayout || (view.Image.Width, view.Image.Height) != _size))
        {
            Forget();
        }

        return _channel;
    }
}
