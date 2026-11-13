using System.Text;
using System.Windows;
using System.Windows.Media;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Zero-dependency ISO/IEC 18004 QR Code encoder supporting Versions 1-6 (Byte mode, EC Level L)
/// with Reed-Solomon GF(256) error correction, alignment patterns, and optimal mask selection.
/// Produces frozen WPF StreamGeometry ready for crisp high-DPI vector rendering.
/// </summary>
public static class QrCodeGenerator
{
    private readonly record struct VersionSpec(
        int Version,
        int Size,
        int DataCodewords,
        int EccCodewordsPerBlock,
        int BlockCount,
        int[] AlignmentCenters);

    private static readonly VersionSpec[] Versions =
    [
        new(1, 21, 19, 7, 1, []),
        new(2, 25, 34, 10, 1, [6, 18]),
        new(3, 29, 55, 15, 1, [6, 22]),
        new(4, 33, 80, 20, 1, [6, 26]),
        new(5, 37, 108, 26, 1, [6, 30]),
        new(6, 41, 136, 18, 2, [6, 34])
    ];

    public static bool[,] Encode(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text ?? string.Empty);

        VersionSpec spec = Versions[^1];
        foreach (var v in Versions)
        {
            // Byte mode overhead: 4-bit mode (0100) + 8-bit count + 4-bit terminator ~= 2 bytes
            if (payload.Length + 2 <= v.DataCodewords)
            {
                spec = v;
                break;
            }
        }

        int maxBytes = Math.Max(0, spec.DataCodewords - 2);
        if (payload.Length > maxBytes)
        {
            payload = payload[..maxBytes];
        }

        byte[] dataCodewords = BuildDataCodewords(payload, spec.DataCodewords);
        byte[] fullCodewords = AppendReedSolomonAndInterleave(dataCodewords, spec);

        int size = spec.Size;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(modules, isFunction, spec);
        DrawCodewords(modules, isFunction, fullCodewords);

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, isFunction, mask);
            DrawFormatBits(modules, isFunction, mask);
            int penalty = ComputePenaltyScore(modules);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
            }
            ApplyMask(modules, isFunction, mask); // undo
        }

        ApplyMask(modules, isFunction, bestMask);
        DrawFormatBits(modules, isFunction, bestMask);
        return modules;
    }

    public static Geometry BuildVectorGeometry(string text, double moduleSize = 4.0)
    {
        bool[,] matrix = Encode(text);
        int size = matrix.GetLength(0);
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            for (int y = 0; y < size; y++)
            {
                int x = 0;
                while (x < size)
                {
                    if (!matrix[y, x])
                    {
                        x++;
                        continue;
                    }

                    // Coalesce horizontal adjacent dark modules into a single rectangle for fast WPF rendering
                    int startX = x;
                    while (x < size && matrix[y, x]) x++;
                    double left = startX * moduleSize;
                    double top = y * moduleSize;
                    double width = (x - startX) * moduleSize;

                    ctx.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(left + width, top), isStroked: false, isSmoothJoin: false);
                    ctx.LineTo(new Point(left + width, top + moduleSize), isStroked: false, isSmoothJoin: false);
                    ctx.LineTo(new Point(left, top + moduleSize), isStroked: false, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static byte[] BuildDataCodewords(byte[] payload, int capacityBytes)
    {
        var bits = new List<int>(capacityBytes * 8);
        AppendBits(bits, 0b0100, 4); // Byte mode
        AppendBits(bits, payload.Length, 8); // Char count indicator for Versions 1-9
        foreach (byte b in payload)
        {
            AppendBits(bits, b, 8);
        }

        int targetBits = capacityBytes * 8;
        int terminatorLen = Math.Min(4, targetBits - bits.Count);
        AppendBits(bits, 0, terminatorLen);

        while (bits.Count % 8 != 0)
        {
            bits.Add(0);
        }

        var result = new byte[capacityBytes];
        int cwIdx = 0;
        for (int i = 0; i + 7 < bits.Count && cwIdx < capacityBytes; i += 8)
        {
            int val = 0;
            for (int b = 0; b < 8; b++)
            {
                val = (val << 1) | bits[i + b];
            }
            result[cwIdx++] = (byte)val;
        }

        bool padToggle = true;
        while (cwIdx < capacityBytes)
        {
            result[cwIdx++] = padToggle ? (byte)0xEC : (byte)0x11;
            padToggle = !padToggle;
        }

        return result;
    }

    private static void AppendBits(List<int> bits, int value, int length)
    {
        for (int i = length - 1; i >= 0; i--)
        {
            bits.Add((value >> i) & 1);
        }
    }

    private static byte[] AppendReedSolomonAndInterleave(byte[] data, VersionSpec spec)
    {
        int blocks = spec.BlockCount;
        int blockDataLen = spec.DataCodewords / blocks;
        int eccLen = spec.EccCodewordsPerBlock;
        byte[] generator = BuildRsGenerator(eccLen);

        var dataBlocks = new byte[blocks][];
        var eccBlocks = new byte[blocks][];

        int offset = 0;
        for (int b = 0; b < blocks; b++)
        {
            byte[] blockData = new byte[blockDataLen];
            Array.Copy(data, offset, blockData, 0, blockDataLen);
            offset += blockDataLen;
            dataBlocks[b] = blockData;
            eccBlocks[b] = ComputeRsRemainder(blockData, generator, eccLen);
        }

        var output = new List<byte>(spec.DataCodewords + blocks * eccLen);
        for (int i = 0; i < blockDataLen; i++)
        {
            for (int b = 0; b < blocks; b++) output.Add(dataBlocks[b][i]);
        }
        for (int i = 0; i < eccLen; i++)
        {
            for (int b = 0; b < blocks; b++) output.Add(eccBlocks[b][i]);
        }

        return [.. output];
    }

    private static byte[] BuildRsGenerator(int degree)
    {
        var poly = new byte[degree];
        poly[^1] = 1;
        byte root = 1;

        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                poly[j] = GfMultiply(poly[j], root);
                if (j + 1 < degree)
                {
                    poly[j] ^= poly[j + 1];
                }
            }
            root = GfMultiply(root, 0x02);
        }
        return poly;
    }

    private static byte[] ComputeRsRemainder(byte[] data, byte[] generator, int degree)
    {
        var result = new byte[degree];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, degree - 1);
            result[^1] = 0;
            for (int j = 0; j < degree; j++)
            {
                result[j] ^= GfMultiply(generator[j], factor);
            }
        }
        return result;
    }

    private static byte GfMultiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ (((z >> 7) & 1) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, VersionSpec spec)
    {
        int size = spec.Size;

        // Timing patterns
        for (int i = 0; i < size; i++)
        {
            SetFunctionModule(modules, isFunction, 6, i, i % 2 == 0);
            SetFunctionModule(modules, isFunction, i, 6, i % 2 == 0);
        }

        // Three 7x7 Finder patterns + separators
        DrawFinderPattern(modules, isFunction, 3, 3);
        DrawFinderPattern(modules, isFunction, size - 4, 3);
        DrawFinderPattern(modules, isFunction, 3, size - 4);

        // Alignment patterns (Versions >= 2)
        int[] centers = spec.AlignmentCenters;
        for (int i = 0; i < centers.Length; i++)
        {
            for (int j = 0; j < centers.Length; j++)
            {
                bool overlapsCornerFinder =
                    (i == 0 && j == 0) ||
                    (i == 0 && j == centers.Length - 1) ||
                    (i == centers.Length - 1 && j == 0);
                if (!overlapsCornerFinder)
                {
                    DrawAlignmentPattern(modules, isFunction, centers[i], centers[j]);
                }
            }
        }

        // Reserve Format Information strips (filled after mask evaluation)
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6)
            {
                SetFunctionModule(modules, isFunction, 8, i, false);
                SetFunctionModule(modules, isFunction, i, 8, false);
            }
        }
        for (int i = size - 8; i < size; i++)
        {
            SetFunctionModule(modules, isFunction, 8, i, false);
            SetFunctionModule(modules, isFunction, i, 8, false);
        }

        // Dark module
        SetFunctionModule(modules, isFunction, size - 8, 8, true);
    }

    private static void DrawFinderPattern(bool[,] modules, bool[,] isFunction, int centerRow, int centerCol)
    {
        int size = modules.GetLength(0);
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int r = centerRow + dy;
                int c = centerCol + dx;
                if (r < 0 || r >= size || c < 0 || c >= size) continue;

                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                bool isDark = dist != 2 && dist != 4;
                SetFunctionModule(modules, isFunction, r, c, isDark);
            }
        }
    }

    private static void DrawAlignmentPattern(bool[,] modules, bool[,] isFunction, int centerRow, int centerCol)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunctionModule(modules, isFunction, centerRow + dy, centerCol + dx, dist != 1);
            }
        }
    }

    private static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int row, int col, bool isDark)
    {
        modules[row, col] = isDark;
        isFunction[row, col] = true;
    }

    private static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] data)
    {
        int size = modules.GetLength(0);
        int bitIndex = 0;
        int totalBits = data.Length * 8;

        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int col = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int row = upward ? size - 1 - vert : vert;

                    if (!isFunction[row, col] && bitIndex < totalBits)
                    {
                        bool bit = ((data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                        modules[row, col] = bit;
                        bitIndex++;
                    }
                }
            }
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
    {
        int size = modules.GetLength(0);
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                if (isFunction[r, c]) continue;
                bool invert = mask switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => (r / 2 + c / 3) % 2 == 0,
                    5 => ((r * c) % 2) + ((r * c) % 3) == 0,
                    6 => (((r * c) % 2) + ((r * c) % 3)) % 2 == 0,
                    7 => (((r + c) % 2) + ((r * c) % 3)) % 2 == 0,
                    _ => false
                };
                if (invert) modules[r, c] = !modules[r, c];
            }
        }
    }

    private static void DrawFormatBits(bool[,] modules, bool[,] isFunction, int mask)
    {
        // EC Level L = 0b01 -> 5-bit format data = (0b01 << 3) | mask
        int data = (1 << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        }
        int bits = ((data << 10) | rem) ^ 0x5412;

        int size = modules.GetLength(0);
        for (int i = 0; i <= 5; i++) SetFunctionModule(modules, isFunction, 8, i, GetBit(bits, i));
        SetFunctionModule(modules, isFunction, 8, 7, GetBit(bits, 6));
        SetFunctionModule(modules, isFunction, 8, 8, GetBit(bits, 7));
        SetFunctionModule(modules, isFunction, 7, 8, GetBit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunctionModule(modules, isFunction, 14 - i, 8, GetBit(bits, i));

        for (int i = 0; i < 8; i++) SetFunctionModule(modules, isFunction, size - 1 - i, 8, GetBit(bits, i));
        for (int i = 8; i < 15; i++) SetFunctionModule(modules, isFunction, 8, size - 15 + i, GetBit(bits, i));
        SetFunctionModule(modules, isFunction, size - 8, 8, true);
    }

    private static bool GetBit(int x, int i) => ((x >> (14 - i)) & 1) != 0;

    private static int ComputePenaltyScore(bool[,] modules)
    {
        int size = modules.GetLength(0);
        int penalty = 0;

        // Rule 1: 5+ consecutive same-colored modules
        for (int r = 0; r < size; r++)
        {
            bool runColor = modules[r, 0];
            int runLen = 1;
            for (int c = 1; c < size; c++)
            {
                if (modules[r, c] == runColor)
                {
                    runLen++;
                }
                else
                {
                    if (runLen >= 5) penalty += 3 + (runLen - 5);
                    runColor = modules[r, c];
                    runLen = 1;
                }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }

        for (int c = 0; c < size; c++)
        {
            bool runColor = modules[0, c];
            int runLen = 1;
            for (int r = 1; r < size; r++)
            {
                if (modules[r, c] == runColor)
                {
                    runLen++;
                }
                else
                {
                    if (runLen >= 5) penalty += 3 + (runLen - 5);
                    runColor = modules[r, c];
                    runLen = 1;
                }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }

        // Rule 2: 2x2 blocks of same color
        for (int r = 0; r < size - 1; r++)
        {
            for (int c = 0; c < size - 1; c++)
            {
                bool color = modules[r, c];
                if (color == modules[r, c + 1] && color == modules[r + 1, c] && color == modules[r + 1, c + 1])
                {
                    penalty += 3;
                }
            }
        }

        // Rule 4: dark/light balance deviation from 50%
        int darkCount = 0;
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                if (modules[r, c]) darkCount++;

        int total = size * size;
        int k = Math.Abs(darkCount * 20 - total * 10) / total;
        penalty += k * 10;
        return penalty;
    }
}
