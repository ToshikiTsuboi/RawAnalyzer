// Models/FileSystemItem.cs
using System.Collections.ObjectModel;
using System.IO;
using ImgProcessWpfApp.Helpers;

namespace ImgProcessWpfApp.Models
{
    public class FileSystemItem : ObservableObject
    {
        public string Name { get; }
        public string? FullPath { get; }
        public bool IsGroup { get; }
        public bool IsDrive { get; }

        public ObservableCollection<FileSystemItem> Children { get; } = new();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value) && value)
                    EnsureChildrenLoaded();
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool HasDummy { get; private set; }

        public FileSystemItem(string name, string? fullPath = null, bool isGroup = false, bool isDrive = false)
        {
            Name = name;
            FullPath = fullPath;
            IsGroup = isGroup;
            IsDrive = isDrive;

            if (!isGroup && (isDrive || (fullPath != null && Directory.Exists(fullPath))))
                AddDummy();
        }

        public static FileSystemItem Group(string name) => new(name, null, isGroup: true);

        public static FileSystemItem FromDirectory(string path)
            => new(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), path);

        public void AddDummy()
        {
            if (HasDummy) return;
            Children.Add(new FileSystemItem("loading...", isGroup: true));
            HasDummy = true;
            OnPropertyChanged(nameof(Children));
        }

        private void EnsureChildrenLoaded()
        {
            if (IsGroup || !HasDummy) return;

            Children.Clear();
            if (FullPath == null) { HasDummy = false; return; }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(FullPath))
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    Children.Add(FromDirectory(dir));
                }
            }
            catch { /* 読めない場所は無視 */ }

            HasDummy = false;
            OnPropertyChanged(nameof(Children));
        }
    }
}
