// SharedMemory (File: SharedMemoryTests\ElementTypes.cs)
// Copyright (c) 2026 Justin Stenning
// http://spazzarama.com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// The SharedMemory library is inspired by the following Code Project article:
//   "Fast IPC Communication Using Shared Memory and InterlockedCompareExchange"
//   http://www.codeproject.com/Articles/14740/Fast-IPC-Communication-Using-Shared-Memory-and-Int

using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// Element types without <c>bool</c> or <c>char</c>, each with the size in bytes that SharedMemory 2.3.3 gives it.
    /// The size sets the stride of a shared array and the bytes an array copy moves, so it is part of the shared-memory
    /// layout that another process reads: these values must never change.
    /// </summary>
    public static class CharacterisedTypes
    {
        /// <summary>
        /// Receives each type in the table with its expected size.
        /// </summary>
        public interface ICheck
        {
            void Check<T>(string name, int expectedSize) where T : struct;
        }

        // Private on purpose: the library must handle element types it cannot see.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Pack1 { public byte A; public int B; public short C; public long D; }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct Pack2 { public byte A; public int B; public short C; public long D; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Pack4 { public byte A; public int B; public short C; public long D; }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct Pack8 { public byte A; public int B; public short C; public long D; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DefaultPack { public byte A; public int B; public short C; public long D; }

        [StructLayout(LayoutKind.Explicit)]
        private struct Overlapping
        {
            [FieldOffset(0)] public int A;
            [FieldOffset(0)] public float B;
            [FieldOffset(4)] public long C;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Nested { public Pack8 Inner; public byte Tail; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithPointer { public IntPtr P; public int I; }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct FixedBytes { public fixed byte Bytes[9]; public int Tail; }

        /// <summary>
        /// Calls <paramref name="check"/> once for every type in the table.
        /// </summary>
        public static void ForEach(ICheck check)
        {
            check.Check<byte>("byte", 1);
            check.Check<sbyte>("sbyte", 1);
            check.Check<short>("short", 2);
            check.Check<ushort>("ushort", 2);
            check.Check<int>("int", 4);
            check.Check<uint>("uint", 4);
            check.Check<long>("long", 8);
            check.Check<ulong>("ulong", 8);
            check.Check<float>("float", 4);
            check.Check<double>("double", 8);
            check.Check<decimal>("decimal", 16);
            check.Check<IntPtr>("IntPtr", IntPtr.Size);
            check.Check<UIntPtr>("UIntPtr", IntPtr.Size);
            check.Check<Guid>("Guid", 16);
            check.Check<SharedHeader>("SharedHeader", 16);
            check.Check<CircularBuffer.NodeHeader>("CircularBuffer.NodeHeader", 24);
            check.Check<CircularBuffer.Node>("CircularBuffer.Node", 32);
            check.Check<Pack1>("Pack1", 15);
            check.Check<Pack2>("Pack2", 16);
            check.Check<Pack4>("Pack4", 20);
            check.Check<Pack8>("Pack8", 24);
            check.Check<DefaultPack>("DefaultPack", 24);
            check.Check<Overlapping>("Overlapping", 16);
            check.Check<Nested>("Nested", 32);
            check.Check<WithPointer>("WithPointer", IntPtr.Size == 8 ? 16 : 8);
            check.Check<FixedBytes>("FixedBytes", 16);
            check.Check<FastStructureTests.CompatibleStructure>("CompatibleStructure", 16 + 4 * IntPtr.Size);
            check.Check<FastStructureTests.ComplexStructure>("ComplexStructure", 24 + 4 * IntPtr.Size);
        }

        /// <summary>
        /// The bytes 2.3.3's <see cref="FastStructure.ToBytes{T}(ref T)"/> gives for the packed structures, with A = 0x11,
        /// B = 0x22222222, C = 0x3333 and D = 0x4444444444444444: the field offsets and the size. "??" marks a padding
        /// byte, whose value is undefined: the .NET Framework leaves whatever was in memory there.
        /// </summary>
        public static void ForEachPackedLayout(Action<string, byte[], string> check)
        {
            check("Pack1", ToBytes(new Pack1 { A = 0x11, B = 0x22222222, C = 0x3333, D = 0x4444444444444444 }),
                "11-22-22-22-22-33-33-44-44-44-44-44-44-44-44");
            check("Pack2", ToBytes(new Pack2 { A = 0x11, B = 0x22222222, C = 0x3333, D = 0x4444444444444444 }),
                "11-??-22-22-22-22-33-33-44-44-44-44-44-44-44-44");
            check("Pack4", ToBytes(new Pack4 { A = 0x11, B = 0x22222222, C = 0x3333, D = 0x4444444444444444 }),
                "11-??-??-??-22-22-22-22-33-33-??-??-44-44-44-44-44-44-44-44");
            check("Pack8", ToBytes(new Pack8 { A = 0x11, B = 0x22222222, C = 0x3333, D = 0x4444444444444444 }),
                "11-??-??-??-22-22-22-22-33-33-??-??-??-??-??-??-44-44-44-44-44-44-44-44");
            check("DefaultPack", ToBytes(new DefaultPack { A = 0x11, B = 0x22222222, C = 0x3333, D = 0x4444444444444444 }),
                "11-??-??-??-22-22-22-22-33-33-??-??-??-??-??-??-44-44-44-44-44-44-44-44");
        }

        private static byte[] ToBytes<T>(T value) where T : struct
        {
            return FastStructure.ToBytes(ref value);
        }

        /// <summary>
        /// Whether <paramref name="bytes"/> has the length of <paramref name="expected"/> and its bytes, "??" matching any byte.
        /// </summary>
        public static bool MatchesLayout(byte[] bytes, string expected)
        {
            string[] want = expected.Split('-');
            string[] got = BitConverter.ToString(bytes).Split('-');
            if (want.Length != got.Length)
                return false;
            for (int i = 0; i < want.Length; i++)
            {
                if (want[i] != "??" && want[i] != got[i])
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Element types that are or contain <c>bool</c> or <c>char</c>, whose .NET size differs from their
    /// <see cref="Marshal.SizeOf(Type)"/> size.
    /// </summary>
    public static class BoolAndCharTypes
    {
        /// <summary>.NET size 8; Marshal.SizeOf gives 12.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FlagLetterNumber
        {
            public bool Flag;
            public char Letter;
            public int Number;

            public FlagLetterNumber(bool flag, char letter, int number)
            {
                Flag = flag;
                Letter = letter;
                Number = number;
            }

            public override string ToString()
            {
                return Flag + "/" + Letter + "/" + Number;
            }
        }

        /// <summary>.NET size 16; Marshal.SizeOf gives 8.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct EightLetters
        {
            public char C0, C1, C2, C3, C4, C5, C6, C7;

            public EightLetters(string letters)
            {
                C0 = letters[0]; C1 = letters[1]; C2 = letters[2]; C3 = letters[3];
                C4 = letters[4]; C5 = letters[5]; C6 = letters[6]; C7 = letters[7];
            }

            public override string ToString()
            {
                return new string(new[] { C0, C1, C2, C3, C4, C5, C6, C7 });
            }
        }

        public static readonly bool[] Flags = { true, false, true, true, false, true, false, false };

        public static readonly char[] Letters = "SharedMe".ToCharArray();

        public static FlagLetterNumber[] Mixed(int count)
        {
            var values = new FlagLetterNumber[count];
            for (int i = 0; i < count; i++)
                values[i] = new FlagLetterNumber(i % 3 == 0, (char)('A' + i % 26), i * 1000 + 7);
            return values;
        }

        public static long[] NewGuard()
        {
            return new long[] { 0x1111111111111111, 0x2222222222222222 };
        }

        public static void AssertGuardIntact(long[] guard)
        {
            Assert.AreEqual(2, guard.Length, "the guard array after the destination changed length");
            Assert.AreEqual(0x1111111111111111, guard[0], "the guard array after the destination changed");
            Assert.AreEqual(0x2222222222222222, guard[1], "the guard array after the destination changed");
        }
    }

    /// <summary>
    /// Element types without <c>bool</c> or <c>char</c> whose .NET size differs from their
    /// <see cref="Marshal.SizeOf(Type)"/> size on some runtimes.
    /// </summary>
    public static class DateTimeAndCurrencyTypes
    {
        /// <summary>Marshal.SizeOf gives 24; the .NET size is 16 on .NET Framework and 24 on .NET 10.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct NumberDateNumber
        {
            public int First;
            public DateTime When;
            public int Last;

            public NumberDateNumber(int first, DateTime when, int last)
            {
                First = first;
                When = when;
                Last = last;
            }

            public override string ToString()
            {
                return First + "/" + When.ToString("o") + "/" + Last;
            }
        }

        /// <summary>Marshal.SizeOf gives 24; the .NET size is 24 on .NET Framework and 32 on .NET 10.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct NumberCurrencyNumber
        {
            public int First;
#pragma warning disable CS0618 // Obsolete on .NET 5 and later, but existing callers' structures still carry it
            [MarshalAs(UnmanagedType.Currency)]
#pragma warning restore CS0618
            public decimal Amount;
            public int Last;

            public NumberCurrencyNumber(int first, decimal amount, int last)
            {
                First = first;
                Amount = amount;
                Last = last;
            }

            public override string ToString()
            {
                return First + "/" + Amount + "/" + Last;
            }
        }

        public static NumberDateNumber[] Dates(int count)
        {
            var start = new DateTime(2026, 10, 24, 9, 30, 0, DateTimeKind.Utc);
            var values = new NumberDateNumber[count];
            for (int i = 0; i < count; i++)
                values[i] = new NumberDateNumber(i * 1000 + 7, start.AddMinutes(i * 61), -i - 1);
            return values;
        }

        public static NumberCurrencyNumber[] Amounts(int count)
        {
            var values = new NumberCurrencyNumber[count];
            for (int i = 0; i < count; i++)
                values[i] = new NumberCurrencyNumber(i * 1000 + 7, 1234.5678m * (i + 1), -i - 1);
            return values;
        }
    }
}
