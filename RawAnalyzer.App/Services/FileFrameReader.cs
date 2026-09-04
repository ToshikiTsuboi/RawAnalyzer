using System.IO;
using RawAnalyzer.App.Compare;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

// Imageは次のMoveNextまたは列挙子のDisposeまで有効。TIFFは常に1ページ分だけ所有する。
internal readonly record struct FileFrame(
    RawImage Image, ColorImage? Color, int Frame, int Index, int Count, bool IsTiffPage);

internal static class FileFrameReader
{
    internal static IEnumerable<FileFrame> Read(string path, RawFormat rawFormat, CancellationToken ct = default)
    {
        if (ComparePane.IsRawFile(path))
        {
            using RawImage image = RawLoader.Load(path, rawFormat, ct);
            for (int frame = 0; frame < image.FrameCount; frame++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new FileFrame(image, null, frame, frame, image.FrameCount, false);
            }

            yield break;
        }

        int count = 1;
        for (int page = 0; page < count; page++)
        {
            ct.ThrowIfCancellationRequested();
            DecodedImage decoded = ImageFileLoader.Load(path, ct, pageIndex: page);
            using RawImage image = decoded.Luminance;
            if (page == 0)
            {
                count = decoded.PageCount;
            }
            else if (count != decoded.PageCount)
            {
                throw new InvalidDataException("書き出し中にTIFFのページ数が変わりました。開き直してください。");
            }

            ct.ThrowIfCancellationRequested();
            yield return new FileFrame(image, decoded.Color, 0, page, count, count > 1);
        }
    }
}
