using System;
using System.Collections.Generic;
using System.Text;

namespace PhoneScreen;

/// <summary>
/// A small QR code maker (byte mode, error correction level M, versions 1 to 10: up to 213 bytes). Made here so the
/// pairing link never leaves the PC: no web service draws it. Follows ISO/IEC 18004 the way Nayuki's well-known
/// reference implementation lays it out.
/// </summary>
public static class Qr
{
    // error correction level M, by version (index 0 unused)
    static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    static readonly int[] Blocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
    const int FormatM = 0;

    /// <summary>The modules, [y, x], true for dark; size = 17 + 4 x version.</summary>
    public static bool[,] Encode(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = 1;
        for (; version <= 10; version++)
            if (data.Length + 2 + (version >= 10 ? 1 : 0) <= DataCodewords(version)) break;   // mode + count + data
        if (version > 10) throw new ArgumentException("Too long for a QR code of this maker.");

        // the bits: byte mode, the count, the bytes, a terminator, padding
        var bits = new List<bool>();
        void Put(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Put(0b0100, 4);
        Put(data.Length, version >= 10 ? 16 : 8);
        foreach (byte b in data) Put(b, 8);
        int capacity = DataCodewords(version) * 8;
        Put(0, Math.Min(4, capacity - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Put(pad, 8);
        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        byte[] all = Interleave(codewords, version);
        int size = 17 + 4 * version;
        var modules = new bool[size, size];
        var function = new bool[size, size];
        DrawFunctionPatterns(modules, function, version);
        DrawCodewords(modules, function, all);

        // the mask that makes the code easiest to read
        int best = 0, bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, function, mask);
            DrawFormatBits(modules, function, mask);
            int penalty = Penalty(modules);
            if (penalty < bestPenalty) { bestPenalty = penalty; best = mask; }
            ApplyMask(modules, function, mask);   // (xor: undone)
        }
        ApplyMask(modules, function, best);
        DrawFormatBits(modules, function, best);
        return modules;
    }

    static int RawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    static int DataCodewords(int ver) => RawDataModules(ver) / 8 - EccPerBlock[ver] * Blocks[ver];

    static byte[] Interleave(byte[] data, int ver)
    {
        int numBlocks = Blocks[ver], eccLen = EccPerBlock[ver];
        int raw = RawDataModules(ver) / 8;
        int numShort = numBlocks - raw % numBlocks;
        int shortLen = raw / numBlocks;
        byte[] divisor = Divisor(eccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int len = shortLen - eccLen + (i < numShort ? 0 : 1);
            var dat = new byte[len];
            Array.Copy(data, k, dat, 0, len);
            k += len;
            var block = new byte[shortLen + 1];
            Array.Copy(dat, block, len);
            byte[] ecc = Remainder(dat, divisor);
            Array.Copy(ecc, 0, block, block.Length - eccLen, eccLen);
            blocks.Add(block);
        }
        var result = new byte[raw];
        for (int i = 0, k = 0; i < blocks[0].Length; i++)
            for (int j = 0; j < blocks.Count; j++)
                if (i != shortLen - eccLen || j >= numShort) result[k++] = blocks[j][i];
        return result;
    }

    static int Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    static byte[] Divisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    static byte[] Remainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    static void Set(bool[,] m, bool[,] f, int x, int y, bool dark)
    {
        m[y, x] = dark;
        f[y, x] = true;
    }

    static void DrawFunctionPatterns(bool[,] m, bool[,] f, int ver)
    {
        int size = m.GetLength(0);
        for (int i = 0; i < size; i++)
        {
            Set(m, f, 6, i, i % 2 == 0);
            Set(m, f, i, 6, i % 2 == 0);
        }
        void Finder(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy)), xx = x + dx, yy = y + dy;
                    if (xx >= 0 && xx < size && yy >= 0 && yy < size) Set(m, f, xx, yy, dist != 2 && dist != 4);
                }
        }
        Finder(3, 3);
        Finder(size - 4, 3);
        Finder(3, size - 4);

        int[] pos = AlignmentPositions(ver, size);
        int n = pos.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Set(m, f, pos[i] + dx, pos[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }

        DrawFormatBits(m, f, 0);   // (reserves the area; drawn again with the chosen mask)
        if (ver >= 7)
        {
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = ver << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(m, f, a, b, bit);
                Set(m, f, b, a, bit);
            }
        }
    }

    static int[] AlignmentPositions(int ver, int size)
    {
        if (ver == 1) return Array.Empty<int>();
        int numAlign = ver / 7 + 2;
        int step = (ver * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = result.Length - 1, p = size - 7; i >= 1; i--, p -= step) result[i] = p;
        return result;
    }

    static void DrawFormatBits(bool[,] m, bool[,] f, int mask)
    {
        int size = m.GetLength(0);
        int data = FormatM << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        for (int i = 0; i <= 5; i++) Set(m, f, 8, i, Bit(i));
        Set(m, f, 8, 7, Bit(6));
        Set(m, f, 8, 8, Bit(7));
        Set(m, f, 7, 8, Bit(8));
        for (int i = 9; i < 15; i++) Set(m, f, 14 - i, 8, Bit(i));
        for (int i = 0; i < 8; i++) Set(m, f, size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) Set(m, f, 8, size - 15 + i, Bit(i));
        Set(m, f, 8, size - 8, true);   // the dark module
    }

    static void DrawCodewords(bool[,] m, bool[,] f, byte[] data)
    {
        int size = m.GetLength(0);
        int i = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (!f[y, x] && i < data.Length * 8)
                    {
                        m[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    static void ApplyMask(bool[,] m, bool[,] f, int mask)
    {
        int size = m.GetLength(0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (f[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) m[y, x] = !m[y, x];
            }
    }

    /// <summary>The standard's four penalty rules (long runs, 2x2 blocks, finder-like patterns, dark balance).</summary>
    static int Penalty(bool[,] m)
    {
        int size = m.GetLength(0), result = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                int run = 1;
                for (int b = 1; b < size; b++)
                {
                    bool cur = pass == 0 ? m[a, b] : m[b, a], prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                    if (cur == prev) run++;
                    else { if (run >= 5) result += run - 2; run = 1; }
                }
                if (run >= 5) result += run - 2;
                // 1:1:3:1:1 with four light modules on a side
                for (int b = 0; b + 11 <= size; b++)
                {
                    bool At(int k) => pass == 0 ? m[a, b + k] : m[b + k, a];
                    bool core = At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
                    bool coreLeft = At(0) && !At(1) && At(2) && At(3) && At(4) && !At(5) && At(6);
                    if (core && !At(0) && !At(1) && !At(2) && !At(3)) result += 40;
                    if (coreLeft && !At(7) && !At(8) && !At(9) && !At(10)) result += 40;
                }
            }
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
            {
                bool c = m[y, x];
                if (c == m[y, x + 1] && c == m[y + 1, x] && c == m[y + 1, x + 1]) result += 3;
            }
        int dark = 0;
        foreach (bool b in m) if (b) dark++;
        int total = size * size;
        int k2 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += Math.Max(0, k2) * 10;
        return result;
    }
}
