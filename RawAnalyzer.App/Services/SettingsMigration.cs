using System.IO;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 旧名(RawViewer)で保存された設定を現在の保存先へ引き継ぐ。
/// </summary>
/// <remarks>
/// アプリ名の変更で %AppData% の保存先フォルダが変わったため、
/// フォーマットプリセット・セッション・最近使ったファイルが失われないようにする。
/// 一度移行したら旧フォルダは残したまま(手動で確認・削除できるように)、
/// 新フォルダが既にあれば何もしない。
/// </remarks>
internal static class SettingsMigration
{
    private const string LegacyFolderName = "RawViewer";
    private const string CurrentFolderName = "RawAnalyzer";

    /// <summary>
    /// 旧フォルダから設定ファイルを引き継ぐ。新フォルダが既にあれば何もしない。
    /// </summary>
    /// <returns>引き継いだファイル数。移行しなかった場合は0。</returns>
    public static int MigrateFromLegacyFolder()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string legacy = Path.Combine(appData, LegacyFolderName);
        string current = Path.Combine(appData, CurrentFolderName);
        return Migrate(legacy, current);
    }

    /// <summary>
    /// 指定フォルダ間で設定を引き継ぐ(テスト用に分離)。
    /// </summary>
    /// <remarks>
    /// 判定はフォルダ単位ではなくファイル単位で行う。ログ出力が先に
    /// 現行フォルダを作ってしまうため、フォルダの有無では判定できない。
    /// 既に存在する設定ファイルは上書きしないので、何度呼んでも安全。
    /// </remarks>
    /// <param name="legacyFolder">旧フォルダ。</param>
    /// <param name="currentFolder">現行フォルダ。</param>
    /// <returns>コピーしたファイル数。</returns>
    public static int Migrate(string legacyFolder, string currentFolder)
    {
        if (!Directory.Exists(legacyFolder))
        {
            return 0;
        }

        int copied = 0;
        try
        {
            foreach (string name in new[] { "presets.json", "session.json", "recent.json" })
            {
                string source = Path.Combine(legacyFolder, name);
                string destination = Path.Combine(currentFolder, name);
                if (!File.Exists(source) || File.Exists(destination))
                {
                    continue;
                }

                Directory.CreateDirectory(currentFolder);
                File.Copy(source, destination, overwrite: false);
                copied++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"旧設定の引き継ぎに失敗: {ex.Message}");
        }

        if (copied > 0)
        {
            AppLog.Info($"旧設定を引き継ぎました ({copied} ファイル): {legacyFolder} → {currentFolder}");
        }

        return copied;
    }
}
