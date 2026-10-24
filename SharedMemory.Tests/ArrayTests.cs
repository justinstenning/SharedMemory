// SharedMemory (File: SharedMemoryTests\ArrayTests.cs)
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SharedMemory.Utilities;

namespace SharedMemoryTests
{
    [TestClass]
    public class ArrayTests
    {
        [TestMethod]
        public void Indexer_ReadWriteInteger_DataMatches()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 10))
            {
                sma[0] = 3;
                sma[4] = 10;

                using (var smr = new SharedArray<int>(name))
                {
                    Assert.AreEqual(0, smr[1], "");
                    Assert.AreEqual(3, smr[0], "");
                    Assert.AreEqual(10, smr[4], "");
                }

                IList<int> list = sma;
                list[0] = 5;
                list[4] = 55;

                using (var smr = new SharedArray<int>(name))
                {
                    IList<int> r = smr;

                    Assert.AreEqual(0, r[1], "");
                    Assert.AreEqual(5, r[0], "");
                    Assert.AreEqual(55, r[4], "");
                }

                list[3] = 68;
                IList<int> arraySlice = new ArraySlice<int>(list, 1, 8);
                arraySlice[0] = 67;

                using (var smr = new SharedArray<int>(name))
                {
                    IList<int> r = smr;
                    IList<int> rarraySlice = new ArraySlice<int>(r, 1, 8);

                    Assert.AreEqual(67, rarraySlice[0], "");
                    Assert.AreEqual(68, rarraySlice[2], "");
                    Assert.AreEqual(55, rarraySlice[3], "");
                }

            }
        }

        [TestMethod]
        public void Indexer_OutOfRange_ThrowsException()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 10))
            {
                bool exceptionThrown = false;
                try
                {
                    sma[-1] = 0;
                }
                catch (ArgumentOutOfRangeException)
                {
                    exceptionThrown = true;
                }

                Assert.IsTrue(exceptionThrown, "Index of -1 should result in ArgumentOutOfRangeException");

                exceptionThrown = false;
                IList<int> a = sma;
                try
                {
                    a[-1] = 0;
                }
                catch (ArgumentOutOfRangeException)
                {
                    exceptionThrown = true;
                }

                Assert.IsTrue(exceptionThrown, "Index of -1 should result in ArgumentOutOfRangeException");

                try
                {
                    exceptionThrown = false;
                    sma[sma.Length] = 0;
                }
                catch (ArgumentOutOfRangeException)
                {
                    exceptionThrown = true;
                }

                Assert.IsTrue(exceptionThrown, "Index of Length should result in ArgumentOutOfRangeException");


                try
                {
                    exceptionThrown = false;
                    a[a.Count] = 0;
                }
                catch (ArgumentOutOfRangeException)
                {
                    exceptionThrown = true;
                }

                Assert.IsTrue(exceptionThrown, "Index of Length should result in ArgumentOutOfRangeException");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        unsafe struct MyTestStruct
        {
            const int MAXLENGTH = 100;

            fixed char name[MAXLENGTH];

            public int ValueA;

            public string Name
            {
                get
                {
                    fixed (char* n = name)
                    {
                        return new String(n);
                    }
                }
                set
                {
                    fixed (char* n = name)
                    {
                        int indx = 0;
                        foreach (char c in value)
                        {
                            *(n + indx) = c;
                            indx++;
                            if (indx >= MAXLENGTH - 1)
                                break;
                        }
                        *(n + indx) = '\0';
                    }
                }
            }
        }

        [TestMethod]
        public void Test_MyTestStruct()
        {
            var my = new MyTestStruct();
            my.Name = "long string long string";
            my.Name = "short string";
            Assert.AreEqual("short string", my.Name);
        }

        [TestMethod]
        public void Indexer_ReadWriteComplexStruct_DataMatches()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<MyTestStruct>(name, 10))
            {
                sma[0] = new MyTestStruct { ValueA = 3, Name = "My Test Name" };
                sma[4] = new MyTestStruct { ValueA = 10, Name = "My Test Name2" };

                using (var smr = new SharedArray<MyTestStruct>(name))
                {
                    Assert.AreEqual(0, smr[1].ValueA, "");
                    Assert.AreEqual(3, smr[0].ValueA, "");
                    Assert.AreEqual("My Test Name", smr[0].Name, "");
                    Assert.AreEqual(10, smr[4].ValueA, "");
                    Assert.AreEqual("My Test Name2", smr[4].Name, "");
                }
            }
        }

        [TestMethod]
        public void CopyTo_NullArray_ThrowsException()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 10))
            {
                bool exceptionThrown = false;
                try
                {
                    sma.CopyTo(null);
                }
                catch (ArgumentNullException)
                {
                    exceptionThrown = true;
                }
                Assert.IsTrue(exceptionThrown, "null buffer should result in ArgumentNullException");
            }
        }

        [TestMethod]
        public void Write_NullArray_ThrowsException()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 10))
            {
                bool exceptionThrown = false;
                try
                {
                    sma.Write(null);
                }
                catch (ArgumentNullException)
                {
                    exceptionThrown = true;
                }
                Assert.IsTrue(exceptionThrown, "null buffer should result in ArgumentNullException");
            }
        }

        [TestMethod]
        public void GetEnumerator_IterateItems_DataMatches()
        {
            var name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 1024;
            byte[] data = new byte[bufSize];
            byte[] readBuf = new byte[bufSize];
            using (var sma = new SharedArray<byte>(name, bufSize))
            {
                sma.Write(data);

                int value = 0;
                foreach (var item in sma)
                {
                    Assert.AreEqual(data[value], item);
                    value++;
                }
            }
        }

        [TestMethod]
        public void AcquireWriteLock_ReadWrite_LocksCorrectly()
        {
            var name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 1024;
            byte[] data = new byte[bufSize];
            byte[] readBuf = new byte[bufSize];

            bool readIsFirst = false;
            bool readBlocked = false;
            int syncValue = 0;

            // Fill with random data
            r.NextBytes(data);

            using (var sma = new SharedArray<byte>(name, bufSize))
            {
                // Acquire write lock early
                sma.AcquireWriteLock();
                using (var smr = new SharedArray<byte>(name))
                {
                    var t1 = Task.Factory.StartNew(() =>
                        {
                            if (System.Threading.Interlocked.Exchange(ref syncValue, 1) == 0)
                                readIsFirst = true;
                            // Should block until write lock is released
                            smr.AcquireReadLock();
                            if (System.Threading.Interlocked.Exchange(ref syncValue, 3) == 4)
                                readBlocked = true;
                            smr.CopyTo(readBuf);
                            smr.ReleaseReadLock();
                        });

                    System.Threading.Thread.Sleep(10);

                    var t2 = Task.Factory.StartNew(() =>
                        {
                            var val = System.Threading.Interlocked.Exchange(ref syncValue, 2);
                            if (val == 0)
                                readIsFirst = false;
                            else if (val == 3)
                                readBlocked = false;
                            System.Threading.Thread.Sleep(10);
                            sma.Write(data);
                            System.Threading.Interlocked.Exchange(ref syncValue, 4);
                            sma.ReleaseWriteLock();
                        });

                    Task.WaitAll(t1, t2);

                    Assert.IsTrue(readIsFirst, "The read thread did not enter first.");
                    Assert.IsTrue(readBlocked, "The read thread did not block.");

                    // Check data was written before read
                    for (var i = 0; i < readBuf.Length; i++)
                    {
                        Assert.AreEqual(data[i], readBuf[i]);
                    }
                }
            }
        }

        [TestMethod]
        public void AcquireReadWriteLocks_ReadWrite_Blocks()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<byte>(name, 10))
            {
                using (var smr = new SharedArray<byte>(name))
                {
                    // Acquire write lock
                    sma.AcquireWriteLock();

                    // Should block (and fail to reset write signal)
                    Assert.IsFalse(smr.AcquireReadLock(10));

                    sma.ReleaseWriteLock();

                    smr.AcquireReadLock();

                    // Should block (and fail to reset read signal)
                    Assert.IsFalse(sma.AcquireWriteLock(10));

                    smr.ReleaseReadLock();
                }
            }
        }

        [TestMethod]
        public void IList_Contains()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 10))
            {
                sma[0] = 3;
                sma[4] = 10;

                IList<int> a = sma;

                Assert.IsTrue(a.Contains(10));
                Assert.IsFalse(a.Contains(11));
            }
        }

        [TestMethod]
        public void IList_IndexOf()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 10))
            {
                sma[0] = 3;
                sma[4] = 10;

                IList<int> a = sma;

                Assert.AreEqual(4, a.IndexOf(10));
                Assert.AreEqual(-1, a.IndexOf(11));
            }
        }

        [TestMethod]
        public void IList_IsReadOnly()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 10))
            {
                sma[0] = 3;
                sma[4] = 10;

                IList<int> a = sma;
                Assert.IsTrue(a.IsReadOnly);
            }
        }

        [TestMethod]
        public void CopyTo_EmptyArray_ReturnsNormally()
        {
            using (var sma = new SharedArray<int>(Guid.NewGuid().ToString(), 10))
            {
                sma.CopyTo(new int[0]);
            }
        }

        [TestMethod]
        public void Write_EmptyArray_ReturnsNormally()
        {
            using (var sma = new SharedArray<int>(Guid.NewGuid().ToString(), 10))
            {
                sma.Write(new int[0]);
            }
        }

        // SharedArray<T>.Write(T[], startIndex), size and offset checks, and ICollection<T>.CopyTo (#80, #83)

        static T[] ReadAll<T>(SharedArray<T> array) where T : struct
        {
            var all = new T[array.Length];
            array.CopyTo(all);
            return all;
        }

        [TestMethod]
        public void Write_StartIndex_WritesFromThatElement()
        {
            using (var sma = new SharedArray<byte>(Guid.NewGuid().ToString(), 16))
            {
                sma.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2);

                CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 0, 0 }, ReadAll(sma));
            }
        }

        [TestMethod]
        public void Write_IntsAtStartIndex_WritesFromThatElement()
        {
            using (var sma = new SharedArray<int>(Guid.NewGuid().ToString(), 16))
            {
                sma.Write(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, 2);

                CollectionAssert.AreEqual(new[] { 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0, 0 }, ReadAll(sma));
            }
        }

        [TestMethod]
        public void Write_ShortArrayAtStartIndex_Writes()
        {
            using (var sma = new SharedArray<int>(Guid.NewGuid().ToString(), 16))
            {
                sma.Write(new[] { 1, 2, 3, 4 }, 2);

                CollectionAssert.AreEqual(new[] { 0, 0, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ReadAll(sma));
            }
        }

        [TestMethod]
        public void Write_Array_WaitsAtTheWriteGate()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 16))
            using (var smr = new SharedArray<int>(name))
            {
                sma.ReadWriteTimeout = 50;
                // A reader's lock closes the write gate until it is released
                Assert.IsTrue(smr.AcquireReadLock(1000));
                try
                {
                    Assert.ThrowsExactly<TimeoutException>(() => sma.Write(new[] { 1, 2 }), "startIndex 0");
                    Assert.ThrowsExactly<TimeoutException>(() => sma.Write(new[] { 1, 2 }, 2), "startIndex 2");
                }
                finally
                {
                    smr.ReleaseReadLock();
                }

                CollectionAssert.AreEqual(new int[16], ReadAll(sma), "written while the write gate was closed");
            }
        }

        unsafe struct Big4096
        {
            public fixed byte Data[4096];
        }

        [TestMethod]
        public void Constructor_SizeWrapsPastInt32_ThrowsArgumentOutOfRange()
        {
            // 4096 x 1048577 bytes is 2^32 + 4096, which wrapped to 4096 as an int
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                using (new SharedArray<Big4096>(Guid.NewGuid().ToString(), 1048577))
                {
                }
            });
            Assert.AreEqual("length", ex.ParamName);
        }

        [TestMethod]
        public void Constructor_SizeOverInt32Max_ThrowsArgumentOutOfRangeForLength()
        {
            // 8 x 300,000,000 bytes wrapped to a negative int, reported as a bad bufferSize
            var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                using (new SharedArray<long>(Guid.NewGuid().ToString(), 300000000))
                {
                }
            });
            Assert.AreEqual("length", ex.ParamName);
        }

        [TestMethod]
        public void CopyTo_StartIndexNearInt32Max_ThrowsArgumentOutOfRange()
        {
            // buffer.Length + startIndex wrapped past the range check, and the read landed on the shared header
            using (var sma = new SharedArray<long>(Guid.NewGuid().ToString(), 4))
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sma.CopyTo(new long[2], int.MaxValue - 1));
                Assert.AreEqual("startIndex", ex.ParamName);
            }
        }

        [TestMethod]
        public void Write_StartIndexNearInt32Max_ThrowsArgumentOutOfRange()
        {
            using (var sma = new SharedArray<long>(Guid.NewGuid().ToString(), 4))
            {
                var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sma.Write(new long[] { 1, 2 }, int.MaxValue - 1));
                Assert.AreEqual("startIndex", ex.ParamName);
                CollectionAssert.AreEqual(new long[4], ReadAll(sma));
            }
        }

        [TestMethod]
        public void CopyTo_StartIndex_ReadsFromThatElement()
        {
            // The public CopyTo keeps its meaning: startIndex is the shared array's element to start reading from
            using (var sma = new SharedArray<int>(Guid.NewGuid().ToString(), 5))
            {
                sma.Write(new[] { 1, 2, 3, 4, 5 });
                var destination = new int[2];

                sma.CopyTo(destination, 3);

                CollectionAssert.AreEqual(new[] { 4, 5 }, destination);
            }
        }

        [TestMethod]
        public void ICollectionCopyTo_ArrayIndex_CopiesEveryElementThere()
        {
            using (var sma = new SharedArray<int>(Guid.NewGuid().ToString(), 3))
            {
                sma.Write(new[] { 7, 8, 9 });
                var destination = new int[5];

                ((ICollection<int>)sma).CopyTo(destination, 1);

                CollectionAssert.AreEqual(new[] { 0, 7, 8, 9, 0 }, destination);
            }
        }

        [TestMethod]
        public void ICollectionCopyTo_BadArguments_ThrowAsTheInterfaceSays()
        {
            using (var sma = new SharedArray<int>(Guid.NewGuid().ToString(), 3))
            {
                ICollection<int> collection = sma;

                Assert.ThrowsExactly<ArgumentException>(() => collection.CopyTo(new int[3], 1), "too small from arrayIndex");
                Assert.ThrowsExactly<ArgumentNullException>(() => collection.CopyTo(null, 0), "null");
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => collection.CopyTo(new int[5], -1), "negative arrayIndex");
            }
        }

        [TestMethod]
        public void ListAddRange_SharedArray_AppendsItsElements()
        {
            using (var sma = new SharedArray<int>(Guid.NewGuid().ToString(), 3))
            {
                sma.Write(new[] { 7, 8, 9 });
                var list = new List<int> { 1 };

                list.AddRange(sma);

                CollectionAssert.AreEqual(new[] { 1, 7, 8, 9 }, list);
            }
        }

        [TestMethod]
        public void Constructor_NegativeLengthWhoseSizeWrapped_ThrowsArgumentOutOfRange()
        {
            // 4 x -1073741823 bytes wrapped to 4 as an int, which made an array whose Length was negative
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                using (new SharedArray<int>(Guid.NewGuid().ToString(), -1073741823))
                {
                }
            });
        }

        [TestMethod]
        public void ICollectionCopyTo_WhileAWriterHoldsTheLock_WaitsAtTheReadGate()
        {
            var name = Guid.NewGuid().ToString();
            using (var sma = new SharedArray<int>(name, 3))
            using (var smr = new SharedArray<int>(name))
            {
                smr.ReadWriteTimeout = 50;
                // A writer's lock closes the read gate until it is released
                Assert.IsTrue(sma.AcquireWriteLock(1000));
                try
                {
                    Assert.ThrowsExactly<TimeoutException>(() => ((ICollection<int>)smr).CopyTo(new int[3], 0));
                }
                finally
                {
                    sma.ReleaseWriteLock();
                }
            }
        }
    }
}
