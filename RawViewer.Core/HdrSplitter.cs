namespace RawViewer.Core;

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
    public static IReadOnlyList<RawImage> Split(RawImage image)
    {
        RawFormat format = image.Format;
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

        if (format.FrameCount == stages)
        {
            return SplitFrameSequential(image, stages);
        }

        if (format.FrameCount == 1)
        {
            return SplitLineInterleaved(image, stages);
        }

        throw new InvalidOperationException(
            $"フレーム数({format.FrameCount})がHDR段数({stages})と一致しないため分割できません。");
    }

    private static IReadOnlyList<RawImage> SplitFrameSequential(RawImage image, int stages)
    {
        var frames = new RawImage[stages];
        RawFormat subFormat = image.Format with
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

    private static IReadOnlyList<RawImage> SplitLineInterleaved(RawImage image, int stages)
    {
        int width = image.Width;
        int subHeight = image.Height / stages;
        if (subHeight == 0)
        {
            throw new InvalidOperationException("高さがHDR段数より小さいため分割できません。");
        }

        RawFormat subFormat = image.Format with
        {
            Height = subHeight,
            FrameCount = 1,
            Hdr = HdrMode.None,
        };

        var frames = new RawImage[stages];
        for (int stage = 0; stage < stages; stage++)
        {
            var pixels = new ushort[(long)width * subHeight];
            int stageIndex = stage;
            Parallel.For(0, subHeight, y =>
            {
                image.CopyRegion(
                    0, 0, y * stages + stageIndex, width, 1, pixels.AsSpan(y * width, width));
            });
            frames[stage] = new RawImage(subFormat, pixels);
        }

        return frames;
    }
}
