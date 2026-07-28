using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace RawAnalyzer.Core;

/// <summary>
/// ファイル上の生画素データを内部表現(16bitフルスケール ushort)へ正規化する。
/// 下詰めNbitは value &lt;&lt; (16-N)、上詰めはパディングビットのマスクで正規化する。
/// </summary>
public static class PixelNormalizer
{
    /// <summary>
    /// 生バイト列を16bitフルスケール値へ正規化する。
    /// </summary>
    /// <param name="source">ファイル上の生バイト列(画素数 × コンテナバイト数)。</param>
    /// <param name="destination">正規化された16bit値の出力先。sourceの画素数以上の長さが必要。</param>
    /// <param name="bitDepth">ビット深度(8/10/12/14/16)。</param>
    /// <param name="packing">コンテナ内での詰め方向。</param>
    /// <param name="endianness">2バイトコンテナのバイト順。</param>
    /// <exception cref="ArgumentException">destinationが短すぎる場合。</exception>
    public static void Normalize(
        ReadOnlySpan<byte> source,
        Span<ushort> destination,
        int bitDepth,
        BitPacking packing,
        Endianness endianness)
    {
        int bytesPerPixel = bitDepth <= 8 ? 1 : 2;
        int count = source.Length / bytesPerPixel;
        if (destination.Length < count)
        {
            throw new ArgumentException("出力バッファが入力の画素数より短いです。", nameof(destination));
        }

        destination = destination[..count];

        if (bytesPerPixel == 1)
        {
            for (int i = 0; i < count; i++)
            {
                destination[i] = (ushort)(source[i] << 8);
            }

            return;
        }

        ReadOnlySpan<ushort> containers = MemoryMarshal.Cast<byte, ushort>(source[..(count * 2)]);
        bool needSwap = (endianness == Endianness.Big) == BitConverter.IsLittleEndian;
        if (needSwap)
        {
            BinaryPrimitives.ReverseEndianness(containers, destination);
        }
        else
        {
            containers.CopyTo(destination);
        }

        int shift = 16 - bitDepth;
        if (shift == 0)
        {
            return;
        }

        if (packing == BitPacking.Lsb)
        {
            ShiftLeftInPlace(destination, shift);
        }
        else
        {
            AndInPlace(destination, (ushort)(0xFFFF << shift));
        }
    }

    /// <summary>
    /// 単一のコンテナ値(ホストバイト順)を16bitフルスケール値へ正規化する。
    /// </summary>
    /// <param name="container">エンディアン変換済みのコンテナ値。8bitの場合はバイト値。</param>
    /// <param name="bitDepth">ビット深度(8/10/12/14/16)。</param>
    /// <param name="packing">コンテナ内での詰め方向。</param>
    /// <returns>16bitフルスケールに正規化された値。</returns>
    public static ushort NormalizeValue(ushort container, int bitDepth, BitPacking packing)
    {
        if (bitDepth <= 8)
        {
            return (ushort)(container << 8);
        }

        int shift = 16 - bitDepth;
        return packing == BitPacking.Lsb
            ? (ushort)(container << shift)
            : (ushort)(container & (0xFFFF << shift));
    }

    private static void ShiftLeftInPlace(Span<ushort> data, int shift)
    {
        int i = 0;
        ref ushort head = ref MemoryMarshal.GetReference(data);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + Vector256<ushort>.Count <= data.Length; i += Vector256<ushort>.Count)
            {
                Vector256.ShiftLeft(Vector256.LoadUnsafe(ref head, (nuint)i), shift)
                    .StoreUnsafe(ref head, (nuint)i);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + Vector128<ushort>.Count <= data.Length; i += Vector128<ushort>.Count)
            {
                Vector128.ShiftLeft(Vector128.LoadUnsafe(ref head, (nuint)i), shift)
                    .StoreUnsafe(ref head, (nuint)i);
            }
        }

        for (; i < data.Length; i++)
        {
            data[i] = (ushort)(data[i] << shift);
        }
    }

    private static void AndInPlace(Span<ushort> data, ushort mask)
    {
        int i = 0;
        ref ushort head = ref MemoryMarshal.GetReference(data);
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<ushort> vmask = Vector256.Create(mask);
            for (; i + Vector256<ushort>.Count <= data.Length; i += Vector256<ushort>.Count)
            {
                (Vector256.LoadUnsafe(ref head, (nuint)i) & vmask).StoreUnsafe(ref head, (nuint)i);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> vmask = Vector128.Create(mask);
            for (; i + Vector128<ushort>.Count <= data.Length; i += Vector128<ushort>.Count)
            {
                (Vector128.LoadUnsafe(ref head, (nuint)i) & vmask).StoreUnsafe(ref head, (nuint)i);
            }
        }

        for (; i < data.Length; i++)
        {
            data[i] = (ushort)(data[i] & mask);
        }
    }
}
