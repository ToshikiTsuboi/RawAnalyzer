using System.Buffers.Binary;
using RawAnalyzer.Core;

namespace RawAnalyzer.Tests;

/// <summary>テスト用のRawバイト列生成ヘルパ。</summary>
internal static class TestData
{
    /// <summary>Nビットの生値列をフォーマットに従ってファイルバイト列へエンコードする。</summary>
    public static byte[] EncodeRawFile(ushort[] rawValues, RawFormat format)
    {
        using var stream = new MemoryStream();
        for (long i = 0; i < format.HeaderOffset; i++)
        {
            stream.WriteByte(0xEE);
        }

        Span<byte> buffer = stackalloc byte[2];
        foreach (ushort value in rawValues)
        {
            if (format.BytesPerPixel == 1)
            {
                stream.WriteByte((byte)value);
                continue;
            }

            ushort container = format.Packing == BitPacking.Lsb
                ? value
                : (ushort)(value << (16 - format.BitDepth));
            if (format.Endianness == Endianness.Little)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, container);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer, container);
            }

            stream.Write(buffer);
        }

        return stream.ToArray();
    }

    /// <summary>決定的なNビットテストパターンを生成する。0と最大値を必ず含む。</summary>
    public static ushort[] MakePattern(int count, int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        var values = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = (ushort)((i * 2654435761u) % ((uint)max + 1));
        }

        values[0] = 0;
        if (count > 1)
        {
            values[1] = (ushort)max;
        }

        return values;
    }

    /// <summary>一時ファイルへ書き出し、パスを返す。</summary>
    public static string WriteTempFile(byte[] bytes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
