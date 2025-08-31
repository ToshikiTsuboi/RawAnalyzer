using System;
using System.IO;
using ImgProcessWpfApp.Helpers;

namespace ImgProcessWpfApp.Models
{
    public enum FileKind { Raw, Image, Other }

    public class FileItem : ObservableObject
    {
        public string Name { get; }
        public string FullPath { get; }
        public string Extension { get; }
        public long Size { get; }
        public DateTime Modified { get; }
        public FileKind Kind { get; }

        public FileItem(string path)
        {
            FullPath = path;
            Name = Path.GetFileName(path);
            Extension = Path.GetExtension(path).ToLowerInvariant();
            Kind = DetectKind(Extension);

            try
            {
                var fi = new FileInfo(path);
                Size = fi.Length;
                Modified = fi.LastWriteTime;
            }
            catch { }
        }

        public static FileKind DetectKind(string ext)
        {
            switch (ext)
            {
                case ".raw":
                case ".bin":
                case ".dat":
                    return FileKind.Raw;
            }
            switch (ext)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tif":
                case ".tiff":
                case ".bmp":
                case ".gif":
                    return FileKind.Image;
            }
            return FileKind.Other;
        }
    }
}
