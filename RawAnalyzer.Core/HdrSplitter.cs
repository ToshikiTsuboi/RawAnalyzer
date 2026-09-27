namespace RawAnalyzer.Core;

/// <summary>
/// DOL/Staggered HDRデータを長秒/短秒フレームへ分割する。
/// ライン交互(2段=偶奇行、3段=3行周期)とフレーム連結の両形式に対応する。
/// </summary>
public static class HdrSplitter
{
    /// <summary>
    /// HDRフレームへ分割する。返却リストは長秒→短秒の順。
    /// フォーマットのFrameCountが段数と等しい場合はフレーム連結、
    /// FrameCount=1 の場合はライン交互として分割する。
    /// </summary>
    /// <param name="image">分割する画像。</param>
    /// <returns>分割された各フレーム(長秒→短秒の順、各Hdr=None)。</returns>
    /// <exception cref="InvalidOperationException">
    /// HDR方式が未指定、フレーム構成が不正、または画像が大きすぎる場合。
    /// </exception>
    public static IReadOnlyList<RawImage> Split(RawImage image) => Split(image, image.Format);

    /// <summary>
    /// フォーマットを明示してHDRフレームへ分割する。
    /// 行交互レイアウトでは先頭フレームを分割する
    /// (表示中など任意のフレームを分割するには frame を指定するオーバーロードを使う)。
    /// </summary>
    /// <param name="image">分割する画像。</param>
    /// <param name="format">
    /// 使用するフォーマット記述子。画素レイアウト(幅・高さ・ビット深度)は
    /// <paramref name="image"/> と一致していること。Bayerパターンやフレーム構成を
    /// あとから変更した場合にこちらを渡す。
    /// </param>
    /// <returns>分割された各フレーム(長秒→短秒の順、各Hdr=None)。</returns>
    /// <exception cref="ArgumentException">画素レイアウトが画像と一致しない場合。</exception>
    /// <exception cref="InvalidOperationException">
    /// HDR方式が未指定、フレーム構成が不正、または画像が大きすぎる場合。
    /// </exception>
    public static IReadOnlyList<RawImage> Split(RawImage image, RawFormat format)
        => Split(image, format, frame: 0);

    /// <summary>
    /// フォーマットと分割元のフレームを明示してHDRフレームへ分割する。
    /// </summary>
    /// <param name="image">分割する画像。</param>
    /// <param name="format">
    /// 使用するフォーマット記述子。画素レイアウト(幅・高さ・ビット深度・フレーム数)は
    /// <paramref name="image"/> と一致していること。
    /// </param>
    /// <param name="frame">
    /// 行交互レイアウトで分割するフレーム番号(0 ≤ frame &lt; FrameCount)。
    /// 行交互では1フレームが全露光を行ごとに含む1回の撮影なので、表示中のフレームを渡す。
    /// フレーム連結ではフレームそのものが各露光のため常に全フレームを使い、
    /// この値は範囲の検証にだけ使う(露光の選択には使わない)。
    /// </param>
    /// <returns>分割された各フレーム(長秒→短秒の順、各Hdr=None)。</returns>
    /// <exception cref="ArgumentException">画素レイアウトが画像と一致しない場合。</exception>
    /// <exception cref="ArgumentOutOfRangeException">フレーム番号が範囲外の場合。</exception>
    /// <exception cref="InvalidOperationException">
    /// HDR方式が未指定、フレーム構成が不正、または画像が大きすぎる場合。
    /// </exception>
    public static IReadOnlyList<RawImage> Split(RawImage image, RawFormat format, int frame)
    {
        // フォーマットパネルで変更した Bayer パターンや HDR 方式を反映するための経路。
        // 画素の読み出し位置は image.Format 側で決まるので、レイアウトの一致は必須。
        if (format.Width != image.Width || format.Height != image.Height
            || format.BitDepth != image.Format.BitDepth
            || format.FrameCount != image.FrameCount)
        {
            throw new ArgumentException(
                "画素レイアウト(幅・高さ・ビット深度・フレーム数)が画像と一致しません。",
                nameof(format));
        }

        if ((uint)frame >= (uint)image.FrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame), frame, $"フレーム番号は0〜{image.FrameCount - 1}で指定してください。");
        }

        if (format.Hdr == HdrMode.None)
        {
            throw new InvalidOperationException("HDR方式が指定されていません。");
        }

        int stages = format.HdrStages;
        if (stages is < 2 or > 3)
        {
            throw new InvalidOperationException($"HDR段数は2または3である必要があります: {stages}");
        }

        if (format.TotalPixels > RawLoader.DefaultInMemoryPixelThreshold)
        {
            throw new InvalidOperationException(
                "1億画素を超える画像のHDR分割はサポートされていません。");
        }

        HdrMode layout = ResolveLayout(format, stages);
        if (layout == HdrMode.FrameSequential)
        {
            if (format.FrameCount != stages)
            {
                throw new InvalidOperationException(
                    $"フレーム連結にはフレーム数({format.FrameCount})が" +
                    $"HDR段数({stages})と一致している必要があります。");
            }

            return SplitFrameSequential(image, format, stages);
        }

        return SplitLineInterleaved(image, format, stages, frame);
    }

    /// <summary>
    /// 実際に使う格納レイアウトを決める。Autoはフレーム数から推定する。
    /// </summary>
    /// <param name="format">フォーマット記述子。</param>
    /// <param name="stages">HDR段数。</param>
    /// <returns>LineInterleaved または FrameSequential。</returns>
    /// <exception cref="InvalidOperationException">Autoで推定できない場合。</exception>
    public static HdrMode ResolveLayout(RawFormat format, int stages)
    {
        if (format.Hdr != HdrMode.Auto)
        {
            return format.Hdr;
        }

        if (format.FrameCount == stages)
        {
            return HdrMode.FrameSequential;
        }

        if (format.FrameCount == 1)
        {
            return HdrMode.LineInterleaved;
        }

        throw new InvalidOperationException(
            $"フレーム数({format.FrameCount})がHDR段数({stages})と一致しないため" +
            "格納レイアウトを推定できません。「行交互」か「フレーム連結」を明示してください。");
    }

    private static IReadOnlyList<RawImage> SplitFrameSequential(
        RawImage image, RawFormat format, int stages)
    {
        var frames = new RawImage[stages];
        RawFormat subFormat = format with
        {
            FrameCount = 1,
            Hdr = HdrMode.None,
        };
        int width = image.Width;
        int height = image.Height;
        for (int stage = 0; stage < stages; stage++)
        {
            var pixels = new ushort[(long)width * height];
            int stageIndex = stage;
            Parallel.For(0, height, y =>
            {
                image.CopyRegion(stageIndex, 0, y, width, 1, pixels.AsSpan(y * width, width));
            });
            frames[stage] = new RawImage(subFormat, pixels);
        }

        return frames;
    }

    private static IReadOnlyList<RawImage> SplitLineInterleaved(
        RawImage image, RawFormat format, int stages, int frame)
    {
        int width = image.Width;

        // 既定はBayerなら色ペア(2行)単位。物理ライン1本ごとに長秒/短秒を読み出す
        // センサでは1を指定する(部分画像側では2行ごとに色位相が進むためモザイクは保たれる)
        int blockHeight = format.EffectiveHdrLineBlock;
        int period = stages * blockHeight;
        int fullSubHeight = image.Height / period * blockHeight;

        // 読み出しのパイプライン遅延ぶん段ごとに縦へずれているので、
        // 重なる範囲だけを切り出して各段を行方向に整列させる
        int offsetStep = format.HdrRowOffset;
        int maxOffset = offsetStep * (stages - 1);
        int lowest = Math.Min(0, maxOffset);
        int highest = Math.Max(0, maxOffset);
        int subHeight = fullSubHeight - (highest - lowest);
        if (subHeight <= 0)
        {
            throw new InvalidOperationException(
                "行オフセットが大きすぎて重なる領域がありません。設定値を見直してください。");
        }

        // Bayer位相は整列後のコンテンツ(=実際に読み出したセンサ行)で決まる。
        // パイプライン遅延は「どのセンサ行のデータがどのファイル行に書かれるか」を
        // ずらすだけで、画素のCFA色は変えない。整列後の各段は同一のセンサ行を
        // 含むため、位相は全段共通で -lowest 行ぶんのシフトになる
        // (切り出し位置 startRow で段ごとに回すと、奇数オフセットのとき
        // 非基準段のチャネルラベルが R↔Gb / Gr↔B で入れ替わってしまう)。
        BayerPattern alignedBayer = BayerHelper.ShiftOrigin(format.Bayer, 0, -lowest);

        var frames = new RawImage[stages];
        for (int stage = 0; stage < stages; stage++)
        {
            int startRow = offsetStep * stage - lowest;
            var pixels = new ushort[(long)width * subHeight];
            int stageIndex = stage;
            Parallel.For(0, subHeight, y =>
            {
                int subY = y + startRow;
                int sourceY = subY / blockHeight * period
                    + stageIndex * blockHeight + subY % blockHeight;
                image.CopyRegion(frame, 0, sourceY, width, 1, pixels.AsSpan(y * width, width));
            });

            RawFormat subFormat = format with
            {
                Height = subHeight,
                FrameCount = 1,
                Hdr = HdrMode.None,
                Bayer = alignedBayer,
            };
            frames[stage] = new RawImage(subFormat, pixels);
        }

        return frames;
    }
}
