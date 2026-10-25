// SharedMemory (File: SharedMemoryTests\ElementSizeTests.cs)
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
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// Element sizes in the shared-memory classes: the same as before for the blittable types in the table, and the .NET
    /// size for types with bool or char (#81). These need named shared memory, so they run on Windows.
    /// </summary>
    [TestClass]
    public class ElementSizeTests
    {
        private class SharedArrayCheck : CharacterisedTypes.ICheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public void Check<T>(string name, int expectedSize) where T : struct
            {
                const int length = 5;
                string mapName = Guid.NewGuid().ToString();
                using (var created = new SharedArray<T>(mapName, length))
                using (var opened = new SharedArray<T>(mapName))
                {
                    if (created.Length != length || created.BufferSize != expectedSize * length
                        || opened.Length != length || opened.BufferSize != expectedSize * length)
                    {
                        Mismatches.Add(name + ": expected Length " + length + " and BufferSize " + (expectedSize * length)
                            + ", created " + created.Length + " and " + created.BufferSize
                            + ", opened " + opened.Length + " and " + opened.BufferSize);
                    }
                }
            }
        }

        [TestMethod]
        public void SharedArray_TypesWithoutBoolOrChar_SameLengthAndBufferSizeAsBefore()
        {
            var check = new SharedArrayCheck();
            CharacterisedTypes.ForEach(check);
            Assert.AreEqual(0, check.Mismatches.Count, string.Join("; ", check.Mismatches.ToArray()));
        }

        /// <summary>
        /// Writes <paramref name="values"/> with <see cref="SharedArray{T}.Write(T[], int)"/>, then reads them back through
        /// <see cref="SharedArray{T}.CopyTo(T[], int)"/>, with a guard array allocated straight after the destination, and
        /// through the indexer of a second instance opened by name.
        /// </summary>
        private static void WriteThenCopyTo<T>(T[] values) where T : struct
        {
            string name = Guid.NewGuid().ToString();
            using (var array = new SharedArray<T>(name, values.Length))
            {
                array.Write(values);

                var read = new T[values.Length];
                var guard = BoolAndCharTypes.NewGuard();
                array.CopyTo(read);
                BoolAndCharTypes.AssertGuardIntact(guard);
                CollectionAssert.AreEqual(values, read, "CopyTo");

                using (var opened = new SharedArray<T>(name))
                {
                    Assert.AreEqual(values.Length, opened.Length, "Length when opened by name");
                    for (int i = 0; i < values.Length; i++)
                        Assert.AreEqual(values[i], opened[i], "indexer, element " + i);
                }
            }
        }

        /// <summary>
        /// Writes <paramref name="values"/> one by one through the indexer, then reads them back through the indexer of a
        /// second instance opened by name, and through <see cref="SharedArray{T}.CopyTo(T[], int)"/> with a guard array.
        /// </summary>
        private static void IndexerThenCopyTo<T>(T[] values) where T : struct
        {
            string name = Guid.NewGuid().ToString();
            using (var array = new SharedArray<T>(name, values.Length))
            {
                for (int i = 0; i < values.Length; i++)
                    array[i] = values[i];

                using (var opened = new SharedArray<T>(name))
                {
                    for (int i = 0; i < values.Length; i++)
                        Assert.AreEqual(values[i], opened[i], "indexer, element " + i);
                }

                var read = new T[values.Length];
                var guard = BoolAndCharTypes.NewGuard();
                array.CopyTo(read);
                BoolAndCharTypes.AssertGuardIntact(guard);
                CollectionAssert.AreEqual(values, read, "CopyTo");
            }
        }

        [TestMethod]
        public void SharedArray_Bool_WriteThenCopyTo_RoundTrips()
        {
            WriteThenCopyTo(BoolAndCharTypes.Flags);
        }

        [TestMethod]
        public void SharedArray_Bool_IndexerThenCopyTo_RoundTrips()
        {
            IndexerThenCopyTo(BoolAndCharTypes.Flags);
        }

        [TestMethod]
        public void SharedArray_Char_WriteThenCopyTo_RoundTrips()
        {
            WriteThenCopyTo(BoolAndCharTypes.Letters);
        }

        [TestMethod]
        public void SharedArray_Char_IndexerThenCopyTo_RoundTrips()
        {
            IndexerThenCopyTo(BoolAndCharTypes.Letters);
        }

        [TestMethod]
        public void SharedArray_StructWithBoolAndChar_WriteThenCopyTo_RoundTrips()
        {
            WriteThenCopyTo(BoolAndCharTypes.Mixed(8));
        }

        [TestMethod]
        public void SharedArray_StructWithBoolAndChar_IndexerThenCopyTo_RoundTrips()
        {
            IndexerThenCopyTo(BoolAndCharTypes.Mixed(8));
        }

        [TestMethod]
        public unsafe void SharedArray_StructWithBoolAndChar_BufferSizeIsDotNetSize()
        {
            using (var array = new SharedArray<BoolAndCharTypes.FlagLetterNumber>(Guid.NewGuid().ToString(), 8))
            {
                Assert.AreEqual(8 * sizeof(BoolAndCharTypes.FlagLetterNumber), array.BufferSize);
            }
        }

        [TestMethod]
        public unsafe void CircularBuffer_StructWithChars_WriteAndReadReturnDotNetSize()
        {
            int size = sizeof(BoolAndCharTypes.EightLetters);
            var value = new BoolAndCharTypes.EightLetters("SharedMe");
            string name = Guid.NewGuid().ToString();
            using (var producer = new CircularBuffer(name, 3, size))
            using (var consumer = new CircularBuffer(name))
            {
                Assert.AreEqual(size, producer.Write(ref value, 0), "bytes written");

                BoolAndCharTypes.EightLetters read;
                Assert.AreEqual(size, consumer.Read(out read, 0), "bytes read");
                Assert.AreEqual(value, read);
            }
        }

        [TestMethod]
        public unsafe void CircularBuffer_StructWithChars_NodeTooSmall_Refused()
        {
            // The nodes hold the structure's Marshal.SizeOf size (8 bytes), but not its .NET size (16 bytes).
            int nodeSize = sizeof(BoolAndCharTypes.EightLetters) - 4;
            var value = new BoolAndCharTypes.EightLetters("SharedMe");
            using (var buffer = new CircularBuffer(Guid.NewGuid().ToString(), 3, nodeSize))
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Write(ref value, 0), "Write");
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                {
                    BoolAndCharTypes.EightLetters read;
                    buffer.Read(out read, 0);
                }, "Read");
            }
        }
    }
}
