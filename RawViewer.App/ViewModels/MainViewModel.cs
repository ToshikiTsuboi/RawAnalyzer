using RawViewer.App.Mvvm;

namespace RawViewer.App.ViewModels;

/// <summary>
/// メインウィンドウのViewModel。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private string _statusText = "Ready";

    /// <summary>ステータスバーに表示するテキスト。</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }
}
