using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 一括書き出しの16bitグレーTIFF。画素値を内部表現の16bitフルスケールで書くか、raw の code のまま書くか。
/// </summary>
/// <remarks>
/// 内部表現は Nbit の code を16bitへ左詰めした値(code×2^(16−N)。12bit の code 100 は 1600)。外部ツールで
/// code のまま統計を取るには、BitsPerSample は16のまま値だけを code(下詰め)にしたTIFFが要る。
/// 一括書き出しは付随テキストを書かないので、どちらで書いたかは完了の表示に添える。
/// </remarks>
internal static class BatchTiffOutput
{
    /// <summary>16bitグレーTIFFの形式か(1億画素の上限がなく、拡張子は .tif)。</summary>
    /// <param name="format">出力形式。</param>
    /// <returns>TIFFの形式ならtrue。</returns>
    internal static bool IsTiff(BatchFormat format)
    {
        return format is BatchFormat.Tiff16 or BatchFormat.Tiff16Code;
    }

    /// <summary>1フレームを形式どおりの値で16bitグレーTIFFへ書く。</summary>
    /// <param name="format">出力形式(<see cref="IsTiff"/> が true のもの)。</param>
    /// <param name="image">書き出す画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="path">出力先パス。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    internal static void Save(
        BatchFormat format, RawImage image, int frame, string path, CancellationToken cancellationToken)
    {
        if (format == BatchFormat.Tiff16Code)
        {
            TiffWriter.SaveGray16Codes(image, frame, path, null, cancellationToken);
        }
        else
        {
            TiffWriter.SaveGray16(image, frame, path, null, cancellationToken);
        }
    }

    /// <summary>完了の表示に添える、TIFFの画素値をどちらで書いたかの説明。</summary>
    /// <param name="format">出力形式。</param>
    /// <returns>説明。TIFFでなければ null。</returns>
    internal static string? CompletionNote(BatchFormat format)
    {
        return format switch
        {
            BatchFormat.Tiff16 => "画素値は16bitフルスケール (Nbit の code×2^(16−N))",
            BatchFormat.Tiff16Code => "画素値は raw の code のまま (下詰め・BitsPerSample=16)",
            _ => null,
        };
    }
}
