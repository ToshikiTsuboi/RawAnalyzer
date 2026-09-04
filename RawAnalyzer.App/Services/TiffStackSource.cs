using System.IO;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>TIFFページの参照元。デコーダや全ページの画素は保持しない。</summary>
internal sealed class TiffStackSource(string path, int pageCount, bool pageNavigationEnabled = true)
{
    // WICの1回のCopyPixelsは途中中断できない。高速なスライダー操作でも
    // 複数の巨大ページを並行デコードせず、古い要求の中断完了を待つ。
    private readonly SemaphoreSlim _readGate = new(1, 1);

    internal string Path { get; } = System.IO.Path.GetFullPath(path);
    internal int PageCount { get; } = pageCount > 1 ? pageCount
        : throw new ArgumentOutOfRangeException(nameof(pageCount));

    // ファイル連番で到達したTIFFは先頭ページ固定。出典情報は保存時の上書き防止にも使う。
    internal bool PageNavigationEnabled { get; } = pageNavigationEnabled;

    // 表示中のRGBページのBayer=Noneとは独立に、ユーザーが選んだCFAを保持する。
    internal BayerPattern BayerOverride { get; set; } = BayerPattern.None;

    internal RawFormat GetPageFormat(DecodedImage decoded) => decoded.Luminance.Format with
    {
        Bayer = decoded.Color is null ? BayerOverride : BayerPattern.None,
    };

    internal async Task<DecodedImage> LoadPageAsync(
        int index, CancellationToken ct, IProgress<double>? progress = null)
    {
        if ((uint)index >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        await _readGate.WaitAsync(ct);
        try
        {
            DecodedImage decoded = await Task.Run(() => ImageFileLoader.Load(Path, ct, progress, index), ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (decoded.PageCount != PageCount)
                {
                    throw new InvalidDataException("TIFFのページ数が変わりました。ファイルを開き直してください。");
                }

                return decoded;
            }
            catch
            {
                decoded.Luminance.Dispose();
                throw;
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    internal bool IsSourcePath(string path) => string.Equals(Path, System.IO.Path.GetFullPath(path),
        StringComparison.OrdinalIgnoreCase);

    internal string PageSuffix(int index) => $"_p{index + 1:D4}";
}
