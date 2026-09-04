using System.Runtime.InteropServices;
using System.Windows;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

public class ImageFileLoaderErrorTests
{
    [Fact]
    public void WicErrors_AreLocalizedAndPreserveOriginalException()
    {
        Exception[] errors =
        {
            new FileFormatException("invalid image"),
            new ArgumentException("invalid bitmap"),
            new NotSupportedException("unsupported codec"),
            new COMException("WIC failure", unchecked((int)0x88982F60)),
        };
        foreach (Exception original in errors)
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => ImageFileLoader.DecodeWithWicErrorHandling<int>(() => throw original));
            Assert.StartsWith("画像をデコードできません:", error.Message);
            Assert.Contains(original.Message, error.Message);
            Assert.Same(original, error.InnerException);
        }
    }

    [Fact]
    public void NonDecodeErrors_KeepTheirTypeAndIdentity()
    {
        Exception[] errors =
        {
            new OperationCanceledException(), new IOException(), new UnauthorizedAccessException(),
        };
        foreach (Exception original in errors)
        {
            Exception? error = Record.Exception(
                () => ImageFileLoader.DecodeWithWicErrorHandling<int>(() => throw original));
            Assert.Same(original, error);
        }

        Assert.Equal(42, ImageFileLoader.DecodeWithWicErrorHandling(() => 42));
    }
}
