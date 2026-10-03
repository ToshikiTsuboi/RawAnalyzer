using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR分割ビューの表示画像(各露光の段を左から長秒→短秒の順に並べた1枚)と、ノイズ測定の2枚目の raw を
/// 同じ並びにする規約。
/// </summary>
internal static class HdrSplitComposite
{
    /// <summary>
    /// 分割した各段を左から順に並べた1枚にする。HDR分割ビューの表示画像(MainWindow の分割表示)と、
    /// ノイズ測定の2枚目の並置の両方がこれを使う。
    /// </summary>
    /// <remarks>
    /// 並置した画像には段の幅を区画の幅(<see cref="RawImage.SegmentWidth"/>)として持たせる。Bayer を使う解析
    /// (チャネル別ヒストグラム・欠陥検出の閾値・WB など)は各段の左端を列0とする位相でチャネルを決める。
    /// 段の幅が奇数だと、並置画像全体に1つのパターンを当てたのでは後ろの段(左端が奇数の列)の R/Gr/Gb/B を
    /// 取り違える。
    /// </remarks>
    /// <param name="frames">HdrSplitter.Split の結果(長秒→短秒、同じ寸法)。</param>
    /// <param name="splitFormat">分割に使ったフォーマット。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>並置した画像(単一フレーム・Hdr=None・区画の幅は段の幅)。</returns>
    /// <exception cref="OperationCanceledException">取り消された場合。</exception>
    internal static RawImage Compose(
        IReadOnlyList<RawImage> frames, RawFormat splitFormat, CancellationToken cancellationToken = default)
    {
        int stages = frames.Count;
        int subWidth = frames[0].Width;
        int subHeight = frames[0].Height;
        int compositeWidth = subWidth * stages;
        var pixels = new ushort[(long)compositeWidth * subHeight];
        Parallel.For(0, subHeight, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int stage = 0; stage < stages; stage++)
            {
                frames[stage].CopyRegion(0, 0, y, subWidth, 1,
                    pixels.AsSpan((y * compositeWidth) + (stage * subWidth), subWidth));
            }
        });
        RawFormat format = splitFormat with
        {
            Width = compositeWidth,
            Height = subHeight,
            FrameCount = 1,
            Hdr = HdrMode.None,

            // 負の行オフセットでは整列後の位相が元と変わる(分割フレーム側に合わせる)
            Bayer = frames[0].Format.Bayer,
        };
        return RawImage.FromPixels(format, pixels, segmentWidth: subWidth);
    }

    /// <summary>ノイズ測定の2枚目の raw を読むフォーマット(元の raw ファイルの形式)。</summary>
    /// <remarks>
    /// 行交互は1フレームが全露光を含む1回の撮影なので先頭フレームだけを、フレーム連結はフレームそのものが
    /// 各露光なので全段のフレームを読む。期待サイズ(<see cref="RawFormat.RequiredBytes"/>)も元のファイルの
    /// 大きさになる(並置画像の形式で求めると、行交互・フレーム連結では元のファイルとたまたま一致して
    /// サイズ警告が出なかった)。
    /// </remarks>
    /// <param name="splitFormat">対象(A)の分割に使ったフォーマット。</param>
    /// <returns>2枚目の raw の解釈に使うフォーマット。</returns>
    /// <exception cref="InvalidOperationException">格納レイアウトを決められない場合。</exception>
    internal static RawFormat ReferenceReadFormat(RawFormat splitFormat)
    {
        HdrMode layout = HdrSplitter.ResolveLayout(splitFormat, splitFormat.HdrStages);
        return layout == HdrMode.FrameSequential
            ? splitFormat with { Hdr = HdrMode.FrameSequential }
            : splitFormat with { FrameCount = 1, Hdr = HdrMode.LineInterleaved };
    }

    /// <summary>ノイズ測定の2枚目の raw を、対象(A)の分割ビューと同じ並びで読む。</summary>
    /// <remarks>
    /// 2枚目も元の raw ファイルの形式で読み、対象と同じフォーマット(段数・ライン単位・行オフセット)で分割して
    /// 同じく左右に並べる。並置画像の形式のまま読むと、2枚目の行は元ファイルの連続した行になり、対象の行
    /// [長秒の行 | 短秒の行] とは別の画素同士の差分になる(一致するのはモノクロ・ライン単位1・行オフセット0の
    /// 場合だけで、それも偶然)。
    /// </remarks>
    /// <param name="path">2枚目の raw。</param>
    /// <param name="splitFormat">対象(A)の分割に使ったフォーマット。</param>
    /// <param name="targetScaling">対象の値の対応(raw なので通常はnull)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>並置した2枚目(呼び出し側が破棄する)。</returns>
    internal static RawImage LoadReference(
        string path, RawFormat splitFormat, SampleScaling? targetScaling, CancellationToken cancellationToken)
    {
        RawFormat readFormat = ReferenceReadFormat(splitFormat);
        using RawImage raw = ReferenceImage.Load(path, isRaw: true, readFormat, targetScaling, cancellationToken);
        IReadOnlyList<RawImage> frames = HdrSplitter.Split(raw, readFormat, 0);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Compose(frames, readFormat);
        }
        finally
        {
            foreach (RawImage frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// 同一データの判定で2枚目と比べる、対象(A)の元フレーム(2枚目はファイルの先頭から分割する)。
    /// </summary>
    /// <remarks>
    /// 行交互は分割の元にしたフレームそのもの(先頭なら2枚目と同じデータ)。フレーム連結は全フレームを分割する
    /// ので、表示していたフレームによらず同じファイルなら同じデータ(先頭フレームと同じ扱い)。
    /// </remarks>
    /// <param name="splitFormat">対象(A)の分割に使ったフォーマット。</param>
    /// <param name="sourceFrame">対象(A)の分割の元にしたフレーム。</param>
    /// <returns>2枚目の先頭フレームと比べる元フレーム。</returns>
    internal static int EquivalentTargetFrame(RawFormat splitFormat, int sourceFrame)
    {
        return HdrSplitter.ResolveLayout(splitFormat, splitFormat.HdrStages) == HdrMode.FrameSequential
            ? 0
            : sourceFrame;
    }
}
