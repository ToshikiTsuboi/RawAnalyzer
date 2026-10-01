using System.IO;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Compare;

/// <summary>
/// 比較ペイン1枚ぶんの資源一式(画像・フォーマット・ピラミッド・表示調整)。
/// </summary>
/// <remarks>
/// MainWindowが単一画像用の状態(画像・ピラミッド・LUT…)をフィールドで
/// 直接抱えているのに対し、比較モードでは同じ束をペイン数ぶん持つ必要がある。
/// その「束」を自己完結のオブジェクトとして切り出したもの。
/// 生成(<see cref="LoadAsync"/>)から破棄(<see cref="Dispose"/>)までの
/// 資源の所有権はこのクラスが持つ。
/// ピラミッドをビューポートへ装着している間に<see cref="Dispose"/>を呼ばないこと
/// (描画中の破棄になる。先にビューポートから切り離すのは呼び出し側の責務)。
/// </remarks>
internal sealed class ComparePane : IDisposable
{
    /// <summary>rawとして直接開く拡張子。</summary>
    public static readonly string[] RawExtensions = { ".raw", ".bin" };

    private bool _disposed;

    private ComparePane(
        string path, RawImage image, RawFormat format, ColorImage? color, int pageCount = 1,
        string? valueNote = null)
    {
        Path = path;
        Image = image;
        Format = format;
        Color = color;
        PageCount = pageCount;
        ValueNote = valueNote;
        Display = DisplaySettings.CreateDefault(format.BitDepth);
    }

    /// <summary>読み込み元のファイルパス。</summary>
    public string Path { get; }

    /// <summary>
    /// 表示用のファイル名。TIFFのページ数、マルチフレーム raw のフレーム数(どちらも先頭を表示する)と、
    /// 32bit等の値域換算があればその説明を添える。
    /// </summary>
    /// <remarks>
    /// 比較モードへ入ったときにメイン表示の画像を読み直すペインも先頭フレームを表示するので、メイン表示で
    /// 別のフレームを見ていても、どのフレームを比べているかが分かるようにする。
    /// </remarks>
    public string FileName => System.IO.Path.GetFileName(Path)
        + (PageCount > 1 ? $" [TIFFページ 1/{PageCount}]" : "")
        + (Image.FrameCount > 1 ? $" [フレーム 1/{Image.FrameCount}]" : "")
        + (ValueNote is null ? "" : $" · {ValueNote}");

    /// <summary>元データを16bitへ写した対応関係の説明(該当しなければnull)。</summary>
    public string? ValueNote { get; }

    /// <summary>画像ファイル内のページ数。比較ペインは先頭ページを表示する。</summary>
    public int PageCount { get; }

    /// <summary>画像(raw、または画像ファイルの輝度)。</summary>
    public RawImage Image { get; }

    /// <summary>
    /// フォーマット。rawは指定された解釈、画像ファイルは実フォーマット。
    /// </summary>
    public RawFormat Format { get; }

    /// <summary>カラー画像(JPEG/PNG等のカラーの場合のみ)。</summary>
    public ColorImage? Color { get; }

    /// <summary>このペインの表示調整。</summary>
    public DisplaySettings Display { get; set; }

    /// <summary>縮小表示用のピラミッド。<see cref="EnsureTilePyramidAsync"/>で生成。</summary>
    public TilePyramid? Pyramid { get; private set; }

    /// <summary><see cref="Pyramid"/>の生成元フレーム。</summary>
    public int PyramidFrame { get; private set; }

    /// <summary>指定パスをrawとして開くべきか。</summary>
    /// <param name="path">ファイルパス。</param>
    /// <returns>raw拡張子ならtrue。</returns>
    public static bool IsRawFile(string path)
    {
        return RawExtensions.Contains(
            System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ファイルを読み込んでペインを作る。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    /// <param name="rawFormat">
    /// rawの場合の解釈方法(記憶フォーマットやダイアログで解決したもの)。
    /// 画像ファイル(TIFF/PNG等)では無視される。
    /// </param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>読み込まれたペイン。呼び出し側でDisposeすること。</returns>
    /// <exception cref="ArgumentNullException">rawなのにフォーマット未指定の場合。</exception>
    public static async Task<ComparePane> LoadAsync(
        string path, RawFormat? rawFormat, CancellationToken cancellationToken = default)
    {
        if (IsRawFile(path))
        {
            if (rawFormat is null)
            {
                throw new ArgumentNullException(
                    nameof(rawFormat), "rawを開くにはフォーマットの指定が必要です。");
            }

            RawImage image = await Task.Run(
                () => RawLoader.Load(path, rawFormat, cancellationToken), cancellationToken);
            return new ComparePane(path, image, rawFormat, color: null);
        }

        DecodedImage decoded = await Task.Run(
            () => ImageFileLoader.Load(path, cancellationToken), cancellationToken);
        return new ComparePane(
            path, decoded.Luminance, decoded.Luminance.Format, decoded.Color, decoded.PageCount,
            decoded.ValueNote);
    }

    /// <summary>
    /// 縮小表示用ピラミッドを用意する(同じフレームのものがあれば再利用)。
    /// </summary>
    /// <param name="frame">生成元フレーム。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成完了を表すタスク。</returns>
    public async Task EnsureTilePyramidAsync(
        int frame = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Pyramid is not null && PyramidFrame == frame)
        {
            return;
        }

        TilePyramid built = await TilePyramid.CreateAsync(
            Image, frame, cancellationToken: cancellationToken);
        Pyramid = built;
        PyramidFrame = frame;
    }

    /// <summary>現在の表示調整からLUTを作る。</summary>
    /// <returns>このペイン用の表示LUT。</returns>
    public DisplayLut BuildLut()
    {
        return DisplayLut.Create(Display.ToDisplayParameters(Format.BitDepth));
    }

    /// <summary>保持している画像・ピラミッドを破棄する。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Pyramid = null;
        Image.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
