using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private TilePyramid? _mainPyramid;
    private BayerPyramid? _mainBayerPyramid;

    // ピラミッドは生成元フレーム専用。派生ビューからの復帰時に別フレーム産を
    // frame=0として誤登録しないよう、生成元フレームを一緒に記録する
    private int _mainPyramidFrame;
    private int _mainBayerPyramidFrame;

    // 進行中の縮小ピラミッド(グレー・Bayer)の生成。同じ画像・フレーム・世代の生成を重ねて始めない
    private readonly PyramidBuildCoalescer _pyramidBuilds = new();
    private readonly PyramidBuildCoalescer _bayerPyramidBuilds = new();

    /// <summary>
    /// 縮小ピラミッドを作り、生成元の画像・フレーム・世代のままなら取り付ける。
    /// </summary>
    /// <remarks>
    /// 同じ画像・フレーム・世代(トークン)の生成が進行中なら新たに始めず、その完了を待つ。
    /// 送り後の作り直し(RefreshAfterSequenceMove)は取り付け済みかどうかしか見ないので、生成中に
    /// 同じフレームの作り直しを重ねて求めると(再生中に「次」を押して停止するなど)、同じ全走査が
    /// 二重に走っていた。フレームの送り・画像の差し替えは世代を進めるので、旧世代の生成には相乗りしない。
    /// </remarks>
    /// <param name="image">生成元の画像。</param>
    /// <param name="ct">表示中の画像・フレームの世代のトークン。</param>
    /// <param name="frame">生成元のフレーム番号。</param>
    /// <returns>取り付け(または結果の破棄)の完了を表すタスク。</returns>
    private Task BuildPyramidAsync(RawImage image, CancellationToken ct, int frame = 0)
    {
        return _pyramidBuilds.RunAsync(
            image, frame, ct, () => BuildPyramidCoreAsync(image, ct, frame));
    }

    private async Task BuildPyramidCoreAsync(RawImage image, CancellationToken ct, int frame)
    {
        TilePyramid pyramid;
        try
        {
            pyramid = await TilePyramid.CreateAsync(image, frame, cancellationToken: ct);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            return;
        }

        // 生成中にフレームを移した・画像を差し替えた場合は捨てる。フレームの送りは表示中の画像・
        // フレームの世代(_loadCts)を進めてトークンを取り消すので、取り消しの前に生成が終わっていても、
        // 前フレーム用のピラミッドで現フレームのものを上書きしない(画像の差し替えは画像でも照合する)
        if (ct.IsCancellationRequested || !ReferenceEquals(image, _currentImage))
        {
            return;
        }

        _mainPyramid = pyramid;
        _mainPyramidFrame = frame;
        if (_derivedImage is null)
        {
            Viewport.SetPyramid(pyramid, frame);
        }
    }

    /// <summary>
    /// カラー系表示に入る直前に、Bayer位相を保った縮小ピラミッドを用意する。
    /// </summary>
    /// <remarks>
    /// 全画像で先に作ると読み込みのたびに元画像をもう一度全走査することになるため、
    /// 実際に縮小描画が必要なカラー表示へ入るときだけ生成する。
    /// </remarks>
    private async Task EnsureBayerPyramidAsync()
    {
        RawImage? image = ActiveImage;
        RawFormat? format = ActiveFormat;
        if (image is null || format is null || format.Bayer == BayerPattern.None)
        {
            return;
        }

        bool derived = _derivedImage is not null;
        if ((derived ? _derivedBayerPyramid : _mainBayerPyramid) is not null)
        {
            return;
        }

        int frame = derived ? 0 : Viewport.Frame;

        // 表示中の画像・フレームの世代(_loadCts)のトークンで作る。フレームの送り・画像の差し替えで取り消される
        CancellationToken ct = _loadCts?.Token ?? default;

        // 生成中はまだ取り付けていないので、上の確認だけでは表示モードの切替(Bayerカラー→現像など)で
        // 同じピラミッドの生成を重ねて始め、後から終わった方が先に取り付けた方を描画中に破棄していた。
        // 同じ画像・フレーム・世代の生成が進行中なら新たに始めず、その完了(取り付け)を待つ
        await _bayerPyramidBuilds.RunAsync(
            image, frame, ct, () => BuildBayerPyramidAsync(image, format, derived, frame, ct));
    }

    /// <summary>
    /// Bayerピラミッドを作り、生成元の画像・フレーム・世代のままなら取り付ける。
    /// </summary>
    /// <param name="image">生成元の画像(元画像、またはHDR派生ビューの画像)。</param>
    /// <param name="format">Bayerパターンを含むフォーマット。</param>
    /// <param name="derived">生成元がHDR派生ビューの画像か。</param>
    /// <param name="frame">生成元のフレーム番号。</param>
    /// <param name="ct">表示中の画像・フレームの世代のトークン。</param>
    /// <returns>取り付け(または結果の破棄)の完了を表すタスク。</returns>
    private async Task BuildBayerPyramidAsync(
        RawImage image, RawFormat format, bool derived, int frame, CancellationToken ct)
    {
        BayerPyramid bayer;
        try
        {
            bayer = await BayerPyramid.CreateAsync(
                image, format, frame, cancellationToken: ct);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // キャンセル、または画像切替と競合して生成元が破棄された。
            // 作り直しは次の表示切替に任せる
            return;
        }

        // 取り消しの前に生成が終わっていた前の世代の結果も捨てる(グレーのピラミッドと同じ規約)。
        // フレームを移して戻ったときに、戻った後で始めた生成の結果と二重に取り付けない
        if (ct.IsCancellationRequested
            || !ReferenceEquals(image, ActiveImage) || frame != (derived ? 0 : Viewport.Frame))
        {
            bayer.Dispose();
            return;
        }

        if (derived)
        {
            _derivedBayerPyramid?.Dispose();
            _derivedBayerPyramid = bayer;
        }
        else
        {
            _mainBayerPyramid?.Dispose();
            _mainBayerPyramid = bayer;
            _mainBayerPyramidFrame = frame;
        }

        Viewport.SetBayerPyramid(bayer, frame);
    }
}
