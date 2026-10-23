// SharedMemory (File: SharedMemoryTests\FastStructureTests.cs)
// Copyright (c) 2014 Justin Stenning
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.InteropServices;
using SharedMemory;

namespace SharedMemoryTests
{
    [TestClass]
    public class FastStructureTests
    {
        #region Test Structures

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public unsafe struct CompatibleStructure
        {
            public int Integer1;

            public IntPtr Pointer1;

            public IntPtr Pointer2;

            public IntPtr Pointer3;

            public IntPtr Pointer4;

            public fixed byte Contents[8];

            public int Bookend;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IncompatibleNestedStructure
        {
            public int IncompatibleNestedStructure_One;

            public object IncompatibleNestedStructure_Two;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IncompatibleNestedStructure2
        {
            public int IncompatibleNestedStructure_One;

            public object IncompatibleNestedStructure_Two;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512, ArraySubType = UnmanagedType.I2)]
            public char[] Filename;
        }

        public struct HasIncompatibleStructure
        {
            public int HasIncompatibleStructure_One;

            public IncompatibleNestedStructure HasIncompatibleStructure_Two;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ComplexStructure
        {

            public int FirstElement;

            public CompatibleStructure Compatible;

            public int FinalElement;
        }

        #endregion

        [TestMethod]
        public void FastStructure_IncompabitibleNestedType()
        {
            try
            {
                var size = FastStructure<HasIncompatibleStructure>.Size;
            }
            catch (TypeInitializationException e)
            {
                return;
            }

            Assert.Fail("Did not throw TypeInitializationException for incompatible nested type: IncompatibleNestedStructure.");
        }

        [TestMethod]
        public void FastStructure_IncompatibleStructure()
        {
            try
            {
                var size = FastStructure<IncompatibleNestedStructure2>.Size;
            }
            catch (TypeInitializationException e)
            {
                return;
            }

            Assert.Fail("Did not throw TypeInitializationException for incompatible type: NestedStructure2.");
        }

        [TestMethod]
        public void FastStructure_CompatibleStructureSize()
        {
            Assert.AreEqual(IntPtr.Size * 4 + 8 + (sizeof(int) * 2), FastStructure<CompatibleStructure>.Size);
        }

        [TestMethod]
        public void FastStructure_ComplexStructureSize()
        {
            var sizeOfCompatibleStructure = IntPtr.Size * 4 + 8 + (sizeof(int) * 2);
            var sizeOfComplexStructure = (sizeof(int) * 2) + sizeOfCompatibleStructure;
            Assert.AreEqual(sizeOfComplexStructure, FastStructure<ComplexStructure>.Size);
        }

        [TestMethod]
        public void FastStructure_AllocHGlobalReadWrite()
        {
            IntPtr mem = Marshal.AllocHGlobal(FastStructure.SizeOf<ComplexStructure>());

            ComplexStructure n = new ComplexStructure();

            n.Compatible.Integer1 = 1;
            n.Compatible.Bookend = 2;

            n.FirstElement = 3;
            n.FinalElement = 9;
            unsafe
            {
                n.Compatible.Contents[0] = 4;
                n.Compatible.Contents[7] = 5;
            }

            FastStructure.StructureToPtr(ref n, mem);

            // Assert that the reading and writing result in same structure
            ComplexStructure m = FastStructure.PtrToStructure<ComplexStructure>(mem);
            Assert.AreEqual(n, m);
            Assert.AreEqual(n.Compatible.Integer1, m.Compatible.Integer1);
            Assert.AreEqual(n.Compatible.Bookend, m.Compatible.Bookend);
            unsafe
            {
                Assert.AreEqual(n.Compatible.Contents[0], m.Compatible.Contents[0]);
                Assert.AreEqual(n.Compatible.Contents[7], m.Compatible.Contents[7]);
            }

            // Assert that Marshal.PtrToStructure is compatible
            m = (ComplexStructure)Marshal.PtrToStructure(mem, typeof(ComplexStructure));
            Assert.AreEqual(n, m);
            Assert.AreEqual(n.Compatible.Integer1, m.Compatible.Integer1);
            Assert.AreEqual(n.Compatible.Bookend, m.Compatible.Bookend);
            unsafe
            {
                Assert.AreEqual(n.Compatible.Contents[0], m.Compatible.Contents[0]);
                Assert.AreEqual(n.Compatible.Contents[7], m.Compatible.Contents[7]);
            }

            Marshal.FreeHGlobal(mem);
        }

        #region Element sizes

        private class SizeOfCheck : CharacterisedTypes.ICheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public void Check<T>(string name, int expectedSize) where T : struct
            {
                int size = FastStructure.SizeOf<T>();
                if (size != expectedSize || FastStructure<T>.Size != expectedSize)
                    Mismatches.Add(name + ": expected " + expectedSize + ", SizeOf gives " + size + ", Size gives " + FastStructure<T>.Size);
            }
        }

        [TestMethod]
        public void SizeOf_TypesWithoutBoolOrChar_SameAsBefore()
        {
            var check = new SizeOfCheck();
            CharacterisedTypes.ForEach(check);
            Assert.AreEqual(0, check.Mismatches.Count, string.Join("; ", check.Mismatches.ToArray()));
        }

        [TestMethod]
        public void ToBytes_PackedStructures_SameBytesAsBefore()
        {
            var mismatches = new List<string>();
            CharacterisedTypes.ForEachPackedLayout((name, bytes, expected) =>
            {
                if (!CharacterisedTypes.MatchesLayout(bytes, expected))
                    mismatches.Add(name + ": expected " + expected + ", got " + BitConverter.ToString(bytes));
            });
            Assert.AreEqual(0, mismatches.Count, string.Join("; ", mismatches.ToArray()));
        }

        [TestMethod]
        public void SizeOf_Bool_IsOne()
        {
            Assert.AreEqual(1, FastStructure.SizeOf<bool>());
        }

        [TestMethod]
        public void SizeOf_Char_IsTwo()
        {
            Assert.AreEqual(2, FastStructure.SizeOf<char>());
        }

        [TestMethod]
        public unsafe void SizeOf_StructsWithBoolOrChar_IsDotNetSize()
        {
            Assert.AreEqual(8, sizeof(BoolAndCharTypes.FlagLetterNumber));
            Assert.AreEqual(16, sizeof(BoolAndCharTypes.EightLetters));
            Assert.AreEqual(8, FastStructure.SizeOf<BoolAndCharTypes.FlagLetterNumber>());
            Assert.AreEqual(16, FastStructure.SizeOf<BoolAndCharTypes.EightLetters>());
        }

        private const byte Untouched = 0xEE;

        private static unsafe void Fill(byte* memory, int length)
        {
            for (int i = 0; i < length; i++)
                memory[i] = Untouched;
        }

        private static unsafe void AssertUntouched(byte* memory, int from, int length)
        {
            for (int i = from; i < length; i++)
                Assert.AreEqual(Untouched, memory[i], "byte " + i + " lies past the copied elements");
        }

        [TestMethod]
        public unsafe void WriteArray_Bool_WritesOneBytePerElement()
        {
            var values = BoolAndCharTypes.Flags;
            byte* memory = stackalloc byte[64];
            Fill(memory, 64);

            FastStructure.WriteArray((IntPtr)memory, values, 0, values.Length);

            for (int i = 0; i < values.Length; i++)
                Assert.AreEqual((byte)(values[i] ? 1 : 0), memory[i], "byte " + i);
            AssertUntouched(memory, values.Length, 64);
        }

        [TestMethod]
        public unsafe void ReadArray_Bool_FillsTheArrayAndNothingAfterIt()
        {
            var values = BoolAndCharTypes.Flags;
            byte* memory = stackalloc byte[64];
            Fill(memory, 64);
            for (int i = 0; i < values.Length; i++)
                memory[i] = (byte)(values[i] ? 1 : 0);

            var read = new bool[values.Length];
            var guard = BoolAndCharTypes.NewGuard();
            FastStructure.ReadArray(read, (IntPtr)memory, 0, read.Length);

            BoolAndCharTypes.AssertGuardIntact(guard);
            CollectionAssert.AreEqual(values, read);
        }

        [TestMethod]
        public unsafe void WriteArray_Char_WritesTwoBytesPerElement()
        {
            var values = BoolAndCharTypes.Letters;
            byte* memory = stackalloc byte[64];
            Fill(memory, 64);

            FastStructure.WriteArray((IntPtr)memory, values, 0, values.Length);

            for (int i = 0; i < values.Length; i++)
                Assert.AreEqual(values[i], ((char*)memory)[i], "element " + i);
            AssertUntouched(memory, values.Length * sizeof(char), 64);
        }

        [TestMethod]
        public unsafe void ReadArray_Char_FillsTheWholeArray()
        {
            var values = BoolAndCharTypes.Letters;
            byte* memory = stackalloc byte[64];
            Fill(memory, 64);
            for (int i = 0; i < values.Length; i++)
                ((char*)memory)[i] = values[i];

            var read = new char[values.Length];
            var guard = BoolAndCharTypes.NewGuard();
            FastStructure.ReadArray(read, (IntPtr)memory, 0, read.Length);

            BoolAndCharTypes.AssertGuardIntact(guard);
            CollectionAssert.AreEqual(values, read);
        }

        [TestMethod]
        public unsafe void WriteArray_StructWithBoolAndChar_WritesDotNetSizePerElement()
        {
            var values = BoolAndCharTypes.Mixed(8);
            byte* memory = stackalloc byte[256];
            Fill(memory, 256);

            FastStructure.WriteArray((IntPtr)memory, values, 0, values.Length);

            var written = (BoolAndCharTypes.FlagLetterNumber*)memory;
            for (int i = 0; i < values.Length; i++)
                Assert.AreEqual(values[i], written[i], "element " + i);
            AssertUntouched(memory, values.Length * sizeof(BoolAndCharTypes.FlagLetterNumber), 256);
        }

        [TestMethod]
        public unsafe void ReadArray_StructWithBoolAndChar_FillsTheArrayAndNothingAfterIt()
        {
            var values = BoolAndCharTypes.Mixed(8);
            byte* memory = stackalloc byte[256];
            Fill(memory, 256);
            var source = (BoolAndCharTypes.FlagLetterNumber*)memory;
            for (int i = 0; i < values.Length; i++)
                source[i] = values[i];

            var read = new BoolAndCharTypes.FlagLetterNumber[values.Length];
            var guard = BoolAndCharTypes.NewGuard();
            FastStructure.ReadArray(read, (IntPtr)memory, 0, read.Length);

            BoolAndCharTypes.AssertGuardIntact(guard);
            CollectionAssert.AreEqual(values, read);
        }

        [TestMethod]
        public unsafe void ToBytes_StructWithChars_ReturnsTheWholeStructure()
        {
            var value = new BoolAndCharTypes.EightLetters("SharedMe");

            byte[] bytes = FastStructure.ToBytes(ref value);

            Assert.AreEqual(sizeof(BoolAndCharTypes.EightLetters), bytes.Length);
            Assert.AreEqual(value, FastStructure.FromBytes<BoolAndCharTypes.EightLetters>(bytes, 0));
        }

        /// <summary>
        /// Copies the first half of <paramref name="values"/> to memory with <c>FastStructure.WriteArray</c>, and back with
        /// <c>FastStructure.ReadArray</c>. Each element must sit in the layout 64-bit .NET 10 gives it, at a stride of that
        /// layout's <paramref name="size"/>, on every runtime, so that processes on different runtimes can share it; the copies
        /// must touch nothing past them; and <see cref="FastStructure.SizeOf{T}"/>, which also sets <c>SharedArray</c>'s stride,
        /// must give that size. <paramref name="put"/> and <paramref name="get"/> write and read one element in that layout.
        /// The second half of each array is spare, so a copy that moves too many bytes lands there and not in another object.
        /// </summary>
        private static unsafe void AssertArrayCopiesUseNet10Layout<T>(T[] values, int size, Action<IntPtr, T> put, Func<IntPtr, T> get)
            where T : struct
        {
            int count = values.Length / 2;
            int length = 2 * values.Length * size;
            byte* memory = stackalloc byte[length];

            Fill(memory, length);
            FastStructure.WriteArray((IntPtr)memory, values, 0, count);
            for (int i = 0; i < count; i++)
                Assert.AreEqual(values[i], get((IntPtr)(memory + i * size)), "WriteArray, element " + i);
            AssertUntouched(memory, count * size, length);

            Fill(memory, length);
            for (int i = 0; i < count; i++)
                put((IntPtr)(memory + i * size), values[i]);
            var read = new T[values.Length];
            for (int i = count; i < read.Length; i++)
                read[i] = values[i];
            FastStructure.ReadArray(read, (IntPtr)memory, 0, count);
            for (int i = 0; i < read.Length; i++)
                Assert.AreEqual(values[i], read[i], (i < count ? "ReadArray, element " : "ReadArray changed spare element ") + i);

            Assert.AreEqual(size, FastStructure.SizeOf<T>(), "FastStructure.SizeOf");
            Assert.AreEqual(size, FastStructure<T>.Size, "FastStructure<T>.Size");
        }

        /// <summary>{int First; DateTime When; int Last} in the .NET 10 layout: 0, 8 and 16, 24 bytes.</summary>
        [TestMethod]
        public unsafe void ArrayCopies_StructWithDateTime_UseNet10Layout()
        {
            AssertArrayCopiesUseNet10Layout(DateTimeAndCurrencyTypes.Dates(16), 24,
                (p, v) =>
                {
                    byte* e = (byte*)p;
                    *(int*)e = v.First;
                    *(long*)(e + 8) = v.When.ToBinary();
                    *(int*)(e + 16) = v.Last;
                },
                p =>
                {
                    byte* e = (byte*)p;
                    return new DateTimeAndCurrencyTypes.NumberDateNumber(*(int*)e, DateTime.FromBinary(*(long*)(e + 8)), *(int*)(e + 16));
                });
        }

        /// <summary>{int First; Currency decimal Amount; int Last} in the .NET 10 layout: 0, 8 and 24, 32 bytes. The decimal's
        /// 16 bytes are its flags, then the high, low and middle 32 bits.</summary>
        [TestMethod]
        public unsafe void ArrayCopies_StructWithCurrencyDecimal_UseNet10Layout()
        {
            AssertArrayCopiesUseNet10Layout(DateTimeAndCurrencyTypes.Amounts(16), 32,
                (p, v) =>
                {
                    byte* e = (byte*)p;
                    int[] bits = decimal.GetBits(v.Amount);
                    *(int*)e = v.First;
                    *(int*)(e + 8) = bits[3];
                    *(int*)(e + 12) = bits[2];
                    *(int*)(e + 16) = bits[0];
                    *(int*)(e + 20) = bits[1];
                    *(int*)(e + 24) = v.Last;
                },
                p =>
                {
                    byte* e = (byte*)p;
                    var amount = new decimal(new[] { *(int*)(e + 16), *(int*)(e + 20), *(int*)(e + 12), *(int*)(e + 8) });
                    return new DateTimeAndCurrencyTypes.NumberCurrencyNumber(*(int*)e, amount, *(int*)(e + 24));
                });
        }

        #endregion

        #region Bounds and empty arrays

        [StructLayout(LayoutKind.Sequential)]
        public struct ThirtyTwoBytes
        {
            public long A, B, C, D;
        }

        [TestMethod]
        public void CopyTo_StructDoesNotFit_ThrowsArgumentException()
        {
            var value = new ThirtyTwoBytes { A = 1, B = 2, C = 3, D = 4 };

            var small = new byte[4];
            var guard = BoolAndCharTypes.NewGuard();
            Assert.Throws<ArgumentException>(() => FastStructure.CopyTo(ref value, small, 0));
            BoolAndCharTypes.AssertGuardIntact(guard);
            CollectionAssert.AreEqual(new byte[4], small);

            var larger = new byte[40];
            Assert.Throws<ArgumentException>(() => FastStructure.CopyTo(ref value, larger, 9));
            Assert.Throws<ArgumentException>(() => FastStructure.CopyTo(ref value, larger, 40));
            CollectionAssert.AreEqual(new byte[40], larger);
        }

        [TestMethod]
        public void CopyTo_StructFits_Copies()
        {
            var value = new ThirtyTwoBytes { A = 1, B = 2, C = 3, D = 4 };

            var exact = new byte[32];
            FastStructure.CopyTo(ref value, exact, 0);
            Assert.AreEqual(value, FastStructure.FromBytes<ThirtyTwoBytes>(exact, 0));

            var larger = new byte[40];
            FastStructure.CopyTo(ref value, larger, 8);
            Assert.AreEqual(value, FastStructure.FromBytes<ThirtyTwoBytes>(larger, 8));
        }

        [TestMethod]
        public void FromBytes_StructDoesNotFit_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => FastStructure.FromBytes<ThirtyTwoBytes>(new byte[4], 0));
            Assert.Throws<ArgumentException>(() => FastStructure.FromBytes<ThirtyTwoBytes>(new byte[40], 9));
            Assert.Throws<ArgumentException>(() => FastStructure.FromBytes<ThirtyTwoBytes>(new byte[40], 40));
        }

        [TestMethod]
        public void ReadArray_EmptyArray_ReturnsNormally()
        {
            IntPtr memory = Marshal.AllocHGlobal(16);
            try
            {
                FastStructure.ReadArray(new int[0], memory, 0, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        [TestMethod]
        public void WriteArray_EmptyArray_ReturnsNormally()
        {
            IntPtr memory = Marshal.AllocHGlobal(16);
            try
            {
                FastStructure.WriteArray(memory, new int[0], 0, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        [TestMethod]
        public void ReadBytes_EmptyArray_ReturnsNormally()
        {
            IntPtr memory = Marshal.AllocHGlobal(16);
            try
            {
                FastStructure.ReadBytes(new byte[0], memory, 0, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        [TestMethod]
        public void WriteBytes_EmptyArray_ReturnsNormally()
        {
            IntPtr memory = Marshal.AllocHGlobal(16);
            try
            {
                FastStructure.WriteBytes(memory, new byte[0], 0, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        #endregion

        #region Pinning

        /// <summary>
        /// How long each forced-collection test runs: 1 second, unless the environment variable
        /// SHAREDMEMORY_TEST_GC_SECONDS gives another number of seconds.
        /// </summary>
        private static int ForcedCollectionSeconds()
        {
            int seconds;
            string setting = Environment.GetEnvironmentVariable("SHAREDMEMORY_TEST_GC_SECONDS");
            return setting != null && int.TryParse(setting, out seconds) && seconds > 0 ? seconds : 1;
        }

        /// <summary>
        /// Calls <paramref name="copy"/> in a loop for <paramref name="seconds"/> while another thread forces compacting
        /// collections, and returns how many calls reported lost data. A copy through a pointer to an array that is not
        /// pinned goes to, or comes from, wherever the array was before a collection moved it.
        /// </summary>
        private static long CountLostCopies(Func<bool> copy, int seconds, out string summary)
        {
            int stop = 0;
            long collections = 0;
            var collector = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    GC.Collect(0, GCCollectionMode.Forced, true, true);
                    collections++;
                    Thread.Sleep(0);
                }
            });
            collector.IsBackground = true;
            collector.Start();

            long copies = 0, lost = 0;
            var junk = new List<byte[]>();
            var timer = Stopwatch.StartNew();
            try
            {
                while (timer.Elapsed.TotalSeconds < seconds)
                {
                    junk.Add(new byte[256]);   // keeps generation 0 busy, so that each collection moves objects
                    if (junk.Count > 200)
                        junk.Clear();
                    if (!copy())
                        lost++;
                    copies++;
                }
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                collector.Join();
            }

            summary = lost + " of " + copies + " copies lost data, in " + seconds + " s with " + collections + " forced compacting collections";
            Console.WriteLine(summary);
            return lost;
        }

        [TestMethod]
        public unsafe void ReadArrayAndWriteArray_WhileCollectionsCompact_LoseNoCopies()
        {
            const int count = 16 * 1024;   // 64 KB: small enough to stay off the large object heap, which does not compact
            IntPtr source = Marshal.AllocHGlobal(count * sizeof(int));
            IntPtr destination = Marshal.AllocHGlobal(count * sizeof(int));
            try
            {
                for (int i = 0; i < count; i++)
                    ((int*)source)[i] = i + 1;
                int stamp = 0;

                string summary;
                long lost = CountLostCopies(() =>
                {
                    var read = new int[count];
                    FastStructure.ReadArray(read, source, 0, count);
                    bool readWhole = read[0] == 1 && read[count / 2] == count / 2 + 1 && read[count - 1] == count;

                    stamp++;
                    var written = new int[count];
                    written[0] = written[count / 2] = written[count - 1] = stamp;
                    FastStructure.WriteArray(destination, written, 0, count);
                    int* to = (int*)destination;
                    bool writtenWhole = to[0] == stamp && to[count / 2] == stamp && to[count - 1] == stamp;

                    return readWhole && writtenWhole;
                }, ForcedCollectionSeconds(), out summary);

                Assert.AreEqual(0L, lost, summary);
            }
            finally
            {
                Marshal.FreeHGlobal(source);
                Marshal.FreeHGlobal(destination);
            }
        }

        private static bool Same(BoolAndCharTypes.FlagLetterNumber a, BoolAndCharTypes.FlagLetterNumber b)
        {
            return a.Flag == b.Flag && a.Letter == b.Letter && a.Number == b.Number;
        }

        [TestMethod]
        public unsafe void ReadArrayAndWriteArray_StructsWithBoolAndChar_WhileCollectionsCompact_LoseNoCopies()
        {
            const int count = 8 * 1024;   // 64 KB of 8-byte elements
            var values = BoolAndCharTypes.Mixed(count);
            int size = sizeof(BoolAndCharTypes.FlagLetterNumber);
            IntPtr source = Marshal.AllocHGlobal(count * size);
            IntPtr destination = Marshal.AllocHGlobal(count * size);
            try
            {
                for (int i = 0; i < count; i++)
                    ((BoolAndCharTypes.FlagLetterNumber*)source)[i] = values[i];
                int stamp = 0;

                string summary;
                long lost = CountLostCopies(() =>
                {
                    var read = new BoolAndCharTypes.FlagLetterNumber[count];
                    FastStructure.ReadArray(read, source, 0, count);
                    bool readWhole = Same(read[0], values[0]) && Same(read[count / 2], values[count / 2]) && Same(read[count - 1], values[count - 1]);

                    stamp++;
                    var mark = new BoolAndCharTypes.FlagLetterNumber(true, 'Z', stamp);
                    var written = new BoolAndCharTypes.FlagLetterNumber[count];
                    written[0] = written[count / 2] = written[count - 1] = mark;
                    FastStructure.WriteArray(destination, written, 0, count);
                    var to = (BoolAndCharTypes.FlagLetterNumber*)destination;
                    bool writtenWhole = Same(to[0], mark) && Same(to[count / 2], mark) && Same(to[count - 1], mark);

                    return readWhole && writtenWhole;
                }, ForcedCollectionSeconds(), out summary);

                Assert.AreEqual(0L, lost, summary);
            }
            finally
            {
                Marshal.FreeHGlobal(source);
                Marshal.FreeHGlobal(destination);
            }
        }

        #endregion
    }
}
