using System.Windows;
using RawViewer.App.ViewModels;

namespace RawViewer.App;

/// <summary>
/// メインウィンドウ。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// メインウィンドウを生成し、ViewModelをバインドする。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
