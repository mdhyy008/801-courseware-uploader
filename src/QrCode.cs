using System;
using System.Collections.Generic;
using System.Text;

namespace _801CoursewareUploader
{
    internal static class QrCode
    {
        private const int Size = 29;
        private const int DataCodewords = 44;
        private const int EccCodewords = 26;
        private const int MaxPayloadBytes = 42;

        public static bool[,] Encode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            byte[] payload = Encoding.UTF8.GetBytes(text);
            if (payload.Length > MaxPayloadBytes) return null;

            List<bool> bits = new List<bool>();
            AppendBits(bits, 0x4, 4);
            AppendBits(bits, payload.Length, 8);
            for (int i = 0; i < payload.Length; i++) AppendBits(bits, payload[i], 8);

            int capacity = DataCodewords * 8;
            int terminator = Math.Min(4, capacity - bits.Count);
            for (int i = 0; i < terminator; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);

            List<byte> data = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int value = 0;
                for (int j = 0; j < 8; j++)
                    value = (value << 1) | (bits[i + j] ? 1 : 0);
                data.Add((byte)value);
            }
            byte pad = 0xEC;
            while (data.Count < DataCodewords)
            {
                data.Add(pad);
                pad = pad == 0xEC ? (byte)0x11 : (byte)0xEC;
            }

            byte[] divisor = ComputeDivisor(EccCodewords);
            byte[] ecc = ComputeRemainder(data.ToArray(), divisor);
            byte[] codewords = new byte[DataCodewords + EccCodewords];
            data.CopyTo(codewords, 0);
            Array.Copy(ecc, 0, codewords, DataCodewords, EccCodewords);

            bool[,] modules = new bool[Size, Size];
            bool[,] function = new bool[Size, Size];

            for (int i = 0; i < Size; i++)
            {
                SetFunction(modules, function, 6, i, i % 2 == 0);
                SetFunction(modules, function, i, 6, i % 2 == 0);
            }
            DrawFinder(modules, function, 3, 3);
            DrawFinder(modules, function, Size - 4, 3);
            DrawFinder(modules, function, 3, Size - 4);
            DrawAlignment(modules, function, 22, 22);
            DrawFormat(modules, function);

            int bitIndex = 0;
            int totalBits = codewords.Length * 8;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (function[y, x] || bitIndex >= totalBits) continue;
                        bool dark = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                        modules[y, x] = dark;
                        bitIndex++;
                    }
                }
            }

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (!function[y, x] && (x + y) % 2 == 0) modules[y, x] = !modules[y, x];
                }
            }
            return modules;
        }

        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        private static void SetFunction(bool[,] modules, bool[,] function, int x, int y, bool dark)
        {
            if (x < 0 || x >= Size || y < 0 || y >= Size) return;
            modules[y, x] = dark;
            function[y, x] = true;
        }

        private static void DrawFinder(bool[,] modules, bool[,] function, int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(modules, function, cx + dx, cy + dy, dist != 2 && dist != 4);
                }
            }
        }

        private static void DrawAlignment(bool[,] modules, bool[,] function, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(modules, function, cx + dx, cy + dy, dist != 1);
                }
            }
        }

        private static void DrawFormat(bool[,] modules, bool[,] function)
        {
            int data = 0;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) SetFunction(modules, function, 8, i, GetBit(bits, i));
            SetFunction(modules, function, 8, 7, GetBit(bits, 6));
            SetFunction(modules, function, 8, 8, GetBit(bits, 7));
            SetFunction(modules, function, 7, 8, GetBit(bits, 8));
            for (int i = 9; i < 15; i++) SetFunction(modules, function, 14 - i, 8, GetBit(bits, i));

            for (int i = 0; i < 8; i++) SetFunction(modules, function, Size - 1 - i, 8, GetBit(bits, i));
            for (int i = 8; i < 15; i++) SetFunction(modules, function, 8, Size - 15 + i, GetBit(bits, i));
            SetFunction(modules, function, 8, Size - 8, true);
        }

        private static bool GetBit(int value, int index)
        {
            return ((value >> index) & 1) != 0;
        }

        private static int Multiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z & 0xFF;
        }

        private static byte[] ComputeDivisor(int degree)
        {
            byte[] result = new byte[degree];
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

        private static byte[] ComputeRemainder(byte[] data, byte[] divisor)
        {
            int degree = divisor.Length;
            byte[] result = new byte[degree];
            for (int i = 0; i < data.Length; i++)
            {
                int factor = data[i] ^ result[0];
                Array.Copy(result, 1, result, 0, degree - 1);
                result[degree - 1] = 0;
                for (int j = 0; j < degree; j++)
                    result[j] = (byte)(result[j] ^ Multiply(divisor[j], factor));
            }
            return result;
        }
    }
}
