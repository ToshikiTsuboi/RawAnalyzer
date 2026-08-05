using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

/// <summary>
/// ソフト情報(バージョン・コミット・ビルド日時・ライセンス・リポジトリ)のダイアログ。
/// </summary>
/// <remarks>
/// 不具合報告のときに実行中のバイナリを特定できるよう、「情報をコピー」で
/// 環境まで含めた要約をクリップボードへ入れられるようにしている。
/// </remarks>
public partial class AboutWindow : Window
{
    /// <summary>ウィンドウを生成し、埋め込まれたビルド情報を表示する。</summary>
    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = BuildInfo.VersionLabel;
        CommitText.Text = FormatCommit();
        BuildText.Text = BuildInfo.FormatBuild();
        RuntimeText.Text = BuildInfo.Runtime;
        LicenseText.Text = BuildInfo.License;
        CopyrightText.Text = BuildInfo.Copyright;

        if (BuildInfo.RepositoryUrl.Length > 0)
        {
            RepositoryRun.Text = BuildInfo.RepositoryUrl;
            RepositoryLink.NavigateUri = new Uri(BuildInfo.RepositoryUrl);
        }
        else
        {
            RepositoryRun.Text = "不明";
            RepositoryLink.IsEnabled = false;
        }
    }

    private static string FormatCommit()
    {
        if (BuildInfo.Commit.Length == 0)
        {
            // git の無い環境でビルドされた場合。埋め込めなかったことを明示する
            return "不明 (gitの無い環境でビルド)";
        }

        string branch = BuildInfo.Branch.Length > 0 && BuildInfo.Branch != "HEAD"
            ? $" ({BuildInfo.Branch})"
            : "";
        string dirty = BuildInfo.IsDirty ? " +未コミットの変更" : "";
        return BuildInfo.ShortCommit + branch + dirty;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.TrySetText(BuildInfo.ToReportText());
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            // 既定のブラウザで開く。URLはビルド時に埋め込んだ固定値
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("リポジトリURLを開けませんでした", ex);
            MessageBox.Show(this, $"リンクを開けませんでした: {ex.Message}", "ソフト情報",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
