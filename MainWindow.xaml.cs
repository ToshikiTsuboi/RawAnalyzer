// MainWindow.xaml.cs
using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImgProcessWpfApp.Helpers;
using ImgProcessWpfApp.Models;
using ImgProcessWpfApp.ViewModels;

namespace ImgProcessWpfApp
{
    public partial class MainWindow : Window
    {
        private const double MinZoom = 10;
        private const double MaxZoom = 400;
        private const double ZoomStep = 10;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private MainViewModel VM => (MainViewModel)DataContext;

        // ───────── ディレクトリ／アドレスバー ─────────
        private void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FileSystemItem node)
            {
                if (!node.IsGroup && !string.IsNullOrEmpty(node.FullPath))
                {
                    if (VM.SelectedFolderPath != node.FullPath)
                        VM.SelectedFolderPath = node.FullPath;
                }
            }
        }

        private void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (!VM.TryNavigateTo(VM.AddressPath ?? string.Empty))
            {
                MessageBox.Show(this, "フォルダ/ファイルが見つかりません。", "移動できません",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                AddressBox.SelectAll();
                return;
            }

            ExpandTreeToPath(VM.SelectedFolderPath);
        }

        private void UpFolder_Click(object sender, RoutedEventArgs e)
        {
            var cur = VM.SelectedFolderPath;
            if (string.IsNullOrEmpty(cur)) return;

            try
            {
                var parent = Directory.GetParent(cur);
                if (parent != null && Directory.Exists(parent.FullName))
                {
                    VM.TryNavigateTo(parent.FullName);
                    ExpandTreeToPath(VM.SelectedFolderPath);
                }
            }
            catch { }
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(VM.AddressPath))
            {
                try { Clipboard.SetText(VM.AddressPath); } catch { }
            }
        }

        private void ExpandTreeToPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (DirectoryTree.Items.Count == 0) return;

            FileSystemItem? thisPc = null;
            foreach (var obj in DirectoryTree.Items)
            {
                if (obj is FileSystemItem item && item.IsGroup &&
                    string.Equals(item.Name, "This PC", StringComparison.OrdinalIgnoreCase))
                {
                    thisPc = item;
                    break;
                }
            }
            if (thisPc == null) return;

            var root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(root)) return;

            thisPc.IsExpanded = true;

            FileSystemItem? drive = null;
            foreach (var child in thisPc.Children)
            {
                if (child.IsDrive &&
                    string.Equals(child.FullPath?.TrimEnd(Path.DirectorySeparatorChar),
                                  root, StringComparison.OrdinalIgnoreCase))
                {
                    drive = child;
                    break;
                }
            }
            if (drive == null) return;

            drive.IsExpanded = true;

            var remaining = path.Substring(root.Length).Trim(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(remaining))
            {
                drive.IsSelected = true;
                return;
            }

            var parts = remaining.Split(Path.DirectorySeparatorChar);
            var current = drive;
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                current.IsExpanded = true;

                FileSystemItem? next = null;
                foreach (var ch in current.Children)
                {
                    if (string.Equals(ch.Name, part, StringComparison.OrdinalIgnoreCase))
                    {
                        next = ch; break;
                    }
                }
                if (next == null) break;
                current = next;
            }
            current.IsSelected = true;
        }

        // ───────── ズーム（ホイール位置中心 / Fit / 比較ビュー対応）─────────
        private System.Windows.Controls.ScrollViewer ActiveScrollViewer =>
            (VM.IsCompare ? CompareScroll : ImageScroll);

        private void MainImage_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (VM.ImageSource == null) return;

            var sv = ActiveScrollViewer;
            var mouse = e.GetPosition(sv);

            double prevScale = VM.ZoomScale;
            double prevHx = sv.HorizontalOffset;
            double prevVy = sv.VerticalOffset;

            double contentX = (prevHx + mouse.X) / prevScale;
            double contentY = (prevVy + mouse.Y) / prevScale;

            double np = VM.ZoomPercent + (e.Delta > 0 ? ZoomStep : -ZoomStep);
            np = Math.Max(MinZoom, Math.Min(MaxZoom, np));
            if (Math.Abs(np - VM.ZoomPercent) < 0.1) return;

            VM.IsFitToScreen = false;
            VM.ZoomPercent = np;

            sv.UpdateLayout();

            double newScale = VM.ZoomScale;
            double newHx = contentX * newScale - mouse.X;
            double newVy = contentY * newScale - mouse.Y;
            if (newHx < 0) newHx = 0;
            if (newVy < 0) newVy = 0;

            sv.ScrollToHorizontalOffset(newHx);
            sv.ScrollToVerticalOffset(newVy);

            e.Handled = true;
        }

        private void FitButton_Click(object sender, RoutedEventArgs e)
        {
            VM.IsFitToScreen = true;
            FitToScreenNow();
        }

        private void ImageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (VM.IsFitToScreen && !VM.IsCompare) FitToScreenNow();
        }

        private void CompareScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (VM.IsFitToScreen && VM.IsCompare) FitToScreenNow();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            VM.PropertyChanged += (s, ev) =>
            {
                if ((ev.PropertyName == nameof(VM.ImageSource) ||
                     ev.PropertyName == nameof(VM.OriginalImageSource) ||
                     ev.PropertyName == nameof(VM.IsCompare)) && VM.IsFitToScreen)
                {
                    FitToScreenNow();
                }
            };
        }

        private void FitToScreenNow()
        {
            var sv = ActiveScrollViewer;

            double imgW = 0, imgH = 0;
            if (VM.ImageSource is BitmapSource p)
            {
                double dx = p.DpiX > 0 ? p.DpiX : 96;
                double dy = p.DpiY > 0 ? p.DpiY : 96;
                imgW = p.PixelWidth * 96.0 / dx;
                imgH = p.PixelHeight * 96.0 / dy;
            }
            if (imgW <= 0 || imgH <= 0) return;

            double contentW = VM.IsCompare ? (imgW * 2 + 8) : imgW;
            double contentH = imgH;

            double vpW = sv.ViewportWidth > 0 ? sv.ViewportWidth : sv.ActualWidth;
            double vpH = sv.ViewportHeight > 0 ? sv.ViewportHeight : sv.ActualHeight;
            if (vpW <= 0 || vpH <= 0) return;

            double scale = Math.Min(vpW / contentW, vpH / contentH);
            double percent = Math.Max(MinZoom, Math.Min(MaxZoom, Math.Round(scale * 100.0, 1)));
            VM.ZoomPercent = percent;

            sv.ScrollToHorizontalOffset(0);
            sv.ScrollToVerticalOffset(0);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (DataContext is MainViewModel vm)
            {
                SettingsStore.Save(new AppPreferences
                {
                    RawWidth = vm.RawWidth,
                    RawHeight = vm.RawHeight,
                    HeaderBytes = vm.HeaderBytes
                });
            }
        }
    }
}
