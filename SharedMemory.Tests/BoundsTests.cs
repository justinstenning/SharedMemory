using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// Where a read or write may start and end: every array, pointer, delegate and structure overload of
    /// <see cref="BufferReadWrite"/>, and the members of <see cref="SharedArray{T}"/> that reach them, at the start of the
    /// buffer, in its middle, and ending at its last byte. Each copy round-trips between two instances and leaves the rest of
    /// the buffer as it was.
    /// </summary>
    [TestClass]
    public class BoundsTests
    {
        const int Size = 1024;
        const int Elements = Size / sizeof(int);

        #region BufferReadWrite in range

        [TestMethod]
        [DataRow(0L, 24)]
        [DataRow(500L, 24)]
        [DataRow(1000L, 24)]
        [DataRow(1023L, 1)]
        [DataRow(0L, Size)]
        public void Bytes_InRange_RoundTrip(long position, int count)
        {
            string name = Guid.NewGuid().ToString();
            byte[] data = Pattern(count, 3);
            using (var owner = new BufferReadWrite(name, Size))
            using (var opener = new BufferReadWrite(name))
            {
                owner.Write(data, position);
                var read = new byte[count];
                opener.Read(read, position);

                CollectionAssert.AreEqual(data, read);
                AssertOnlyRangeWritten(name, data, position);
            }
        }

        [TestMethod]
        [DataRow(0L, 6)]
        [DataRow(500L, 6)]
        [DataRow(1000L, 6)]
        [DataRow(1020L, 1)]
        [DataRow(0L, Elements)]
        public void Elements_InRange_RoundTrip(long position, int count)
        {
            string name = Guid.NewGuid().ToString();
            int[] data = Enumerable.Range(0, count).Select(i => 0x01020304 * (i + 1)).ToArray();
            using (var owner = new BufferReadWrite(name, Size))
            using (var opener = new BufferReadWrite(name))
            {
                owner.Write(data, position);
                var read = new int[count];
                opener.Read(read, position);

                CollectionAssert.AreEqual(data, read);
                AssertOnlyRangeWritten(name, ToBytes(data), position);
            }
        }

        [TestMethod]
        [DataRow(0L, 24)]
        [DataRow(500L, 24)]
        [DataRow(1000L, 24)]
        [DataRow(1023L, 1)]
        [DataRow(0L, Size)]
        public void Pointer_InRange_RoundTrip(long position, int length)
        {
            string name = Guid.NewGuid().ToString();
            byte[] data = Pattern(length, 5);
            var read = new byte[length];
            using (var owner = new BufferReadWrite(name, Size))
            using (var opener = new BufferReadWrite(name))
            {
                Pinned(data, p => owner.Write(p, length, position));
                Pinned(read, p => opener.Read(p, length, position));

                CollectionAssert.AreEqual(data, read);
                AssertOnlyRangeWritten(name, data, position);
            }
        }

        [TestMethod]
        [DataRow(0L, 24)]
        [DataRow(500L, 24)]
        [DataRow(1000L, 24)]
        [DataRow(1023L, 1)]
        [DataRow(0L, Size)]
        public void Delegate_InRange_RoundTrip(long position, int count)
        {
            string name = Guid.NewGuid().ToString();
            byte[] data = Pattern(count, 7);
            var read = new byte[count];
            using (var owner = new BufferReadWrite(name, Size))
            using (var opener = new BufferReadWrite(name))
            {
                owner.Write(p => Marshal.Copy(data, 0, p, count), position);
                opener.Read(p => Marshal.Copy(p, read, 0, count), position);

                CollectionAssert.AreEqual(data, read);
                AssertOnlyRangeWritten(name, data, position);
            }
        }

        [TestMethod]
        [DataRow(0L)]
        [DataRow(500L)]
        [DataRow(1016L)]
        public void Structure_InRange_RoundTrip(long position)
        {
            string name = Guid.NewGuid().ToString();
            long value = 0x0102030405060708;
            using (var owner = new BufferReadWrite(name, Size))
            using (var opener = new BufferReadWrite(name))
            {
                owner.Write(ref value, position);
                long read;
                opener.Read(out read, position);

                Assert.AreEqual(value, read);
                AssertOnlyRangeWritten(name, BitConverter.GetBytes(value), position);
            }
        }

        [TestMethod]
        [DataRow(0L)]
        [DataRow(500L)]
        [DataRow(1023L)]
        public void EmptyCopies_InsideTheBuffer_ChangeNothing(long position)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            {
                owner.Write(new byte[0], position);
                owner.Write(new int[0], position);
                owner.Read(new byte[0], position);
                owner.Read(new int[0], position);
                Pinned(new byte[1], p => owner.Write(p, 0, position));
                Pinned(new byte[1], p => owner.Read(p, 0, position));

                CollectionAssert.AreEqual(new byte[Size], Contents(name));
            }
        }

        #endregion

        #region SharedArray in range

        [TestMethod]
        [DataRow(0)]
        [DataRow(128)]
        [DataRow(Elements - 1)]
        public void SharedArray_Indexer_RoundTrips(int index)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new SharedArray<int>(name, Elements))
            using (var opener = new SharedArray<int>(name))
            {
                owner[index] = 0x7A7B7C7D;

                Assert.AreEqual(0x7A7B7C7D, opener[index]);
                Assert.AreEqual(1, opener.Count(v => v != 0));
            }
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(128)]
        [DataRow(Elements - 1)]
        public void SharedArray_WriteAndReadElement_RoundTrip(int index)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new SharedArray<int>(name, Elements))
            using (var opener = new SharedArray<int>(name))
            {
                int value = 0x6A6B6C6D;
                owner.Write(ref value, index);
                int read;
                opener.Read(out read, index);

                Assert.AreEqual(value, read);
                Assert.AreEqual(1, opener.Count(v => v != 0));
            }
        }

        [TestMethod]
        [DataRow(0, 6)]
        [DataRow(100, 6)]
        [DataRow(Elements - 6, 6)]
        [DataRow(Elements - 1, 1)]
        [DataRow(0, Elements)]
        public void SharedArray_WriteAndCopyTo_RoundTrip(int startIndex, int count)
        {
            string name = Guid.NewGuid().ToString();
            int[] data = Enumerable.Range(1, count).ToArray();
            using (var owner = new SharedArray<int>(name, Elements))
            using (var opener = new SharedArray<int>(name))
            {
                owner.Write(data, startIndex);
                var read = new int[count];
                opener.CopyTo(read, startIndex);

                CollectionAssert.AreEqual(data, read);
                int[] expected = new int[Elements];
                Array.Copy(data, 0, expected, startIndex, count);
                CollectionAssert.AreEqual(expected, opener.ToArray());
            }
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(128)]
        [DataRow(Elements)]
        public void SharedArray_EmptyArrays_UpToTheLengthChangeNothing(int startIndex)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new SharedArray<int>(name, Elements))
            {
                owner.Write(new int[0], startIndex);
                owner.CopyTo(new int[0], startIndex);

                CollectionAssert.AreEqual(new int[Elements], owner.ToArray());
            }
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(3)]
        public void SharedArray_CollectionCopyTo_CopiesEveryElement(int arrayIndex)
        {
            string name = Guid.NewGuid().ToString();
            int[] data = Enumerable.Range(1, Elements).ToArray();
            using (var owner = new SharedArray<int>(name, Elements))
            using (var opener = new SharedArray<int>(name))
            {
                owner.Write(data);
                var read = new int[arrayIndex + Elements];
                ((ICollection<int>)opener).CopyTo(read, arrayIndex);

                CollectionAssert.AreEqual(data, read.Skip(arrayIndex).ToArray());
                CollectionAssert.AreEqual(new int[arrayIndex], read.Take(arrayIndex).ToArray());
            }
        }

        [TestMethod]
        public void SharedArray_Enumeration_ReadsEveryElementToTheLast()
        {
            string name = Guid.NewGuid().ToString();
            int[] data = Enumerable.Range(1, Elements).ToArray();
            using (var owner = new SharedArray<int>(name, Elements))
            using (var opener = new SharedArray<int>(name))
            {
                owner.Write(data);

                var read = new List<int>();
                foreach (int value in opener)
                    read.Add(value);

                CollectionAssert.AreEqual(data, read);
                Assert.AreEqual(Elements - 1, opener.IndexOf(Elements));
                Assert.IsTrue(opener.Contains(Elements));
            }
        }

        #endregion

        #region Protected array copies

        [TestMethod]
        [DataRow("WriteArray null", "ArgumentNullException buffer")]
        [DataRow("WriteArray count", "ArgumentOutOfRangeException count")]
        [DataRow("WriteArray index", "ArgumentOutOfRangeException index")]
        [DataRow("WriteArray length", "ArgumentException")]
        [DataRow("ReadArray null", "ArgumentNullException buffer")]
        [DataRow("ReadArray count", "ArgumentOutOfRangeException count")]
        [DataRow("ReadArray index", "ArgumentOutOfRangeException index")]
        [DataRow("ReadArray length", "ArgumentException")]
        [DataRow("WriteFrom index", "ArgumentOutOfRangeException count")]
        public void ProtectedArrayCopies_BadArrayAndBadPosition_ReportTheArray(string call, string expected)
        {
            string name = Guid.NewGuid().ToString();
            using (var buffer = new ArrayCopies(name, Size))
            using (var opener = new ArrayCopies(name))
            {
                Exception thrown = null;
                try
                {
                    BadArrayCall(buffer, call, -8);
                }
                catch (Exception e)
                {
                    thrown = e;
                }

                Assert.IsNotNull(thrown, "Nothing was thrown.");
                var argument = thrown as ArgumentException;
                Assert.AreEqual(expected, thrown.GetType().Name + (argument != null && argument.ParamName != null ? " " + argument.ParamName : ""));
                Assert.IsFalse(opener.ShuttingDown, "The call reached the header's shutdown flag.");
            }
        }

        #endregion

        #region Helpers

        // Distinct bytes, so that a copy from the wrong place shows
        static byte[] Pattern(int count, int seed)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++)
                bytes[i] = (byte)(seed + i * 7 + 1);
            return bytes;
        }

        static byte[] ToBytes(int[] values)
        {
            var bytes = new byte[values.Length * sizeof(int)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        static void Pinned(byte[] bytes, Action<IntPtr> action)
        {
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                action(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        // The protected array copies that CircularBuffer and other subclasses call
        class ArrayCopies : EventFreeBuffer
        {
            public ArrayCopies(string name, long bufferSize) : base(name, bufferSize) { }
            public ArrayCopies(string name) : base(name) { }

            public void CallWriteArray(byte[] source, int index, int count, long bufferPosition)
            {
                WriteArray(source, index, count, bufferPosition);
            }

            public void CallReadArray(byte[] destination, int index, int count, long bufferPosition)
            {
                ReadArray(destination, index, count, bufferPosition);
            }

            public void CallWriteFrom(byte[] source, int index, long bufferPosition)
            {
                Write(source, index, bufferPosition);
            }
        }

        // A call with a bad array: null, a negative count or index, or more elements than the array holds from the index
        static void BadArrayCall(ArrayCopies buffer, string call, long position)
        {
            switch (call)
            {
                case "WriteArray null": buffer.CallWriteArray(null, 0, 1, position); break;
                case "WriteArray count": buffer.CallWriteArray(new byte[4], 0, -1, position); break;
                case "WriteArray index": buffer.CallWriteArray(new byte[4], -1, 1, position); break;
                case "WriteArray length": buffer.CallWriteArray(new byte[4], 2, 4, position); break;
                case "ReadArray null": buffer.CallReadArray(null, 0, 1, position); break;
                case "ReadArray count": buffer.CallReadArray(new byte[4], 0, -1, position); break;
                case "ReadArray index": buffer.CallReadArray(new byte[4], -1, 1, position); break;
                case "ReadArray length": buffer.CallReadArray(new byte[4], 2, 4, position); break;
                case "WriteFrom index": buffer.CallWriteFrom(new byte[4], 5, position); break;
                default: throw new InvalidOperationException("Unknown call " + call);
            }
        }

        // The whole buffer, read through an instance of its own
        static byte[] Contents(string name)
        {
            using (var reader = new BufferReadWrite(name))
            {
                var bytes = new byte[Size];
                reader.Read(bytes);
                return bytes;
            }
        }

        // The buffer, which started as zeros, holds data at position and zeros everywhere else
        static void AssertOnlyRangeWritten(string name, byte[] data, long position)
        {
            var expected = new byte[Size];
            Array.Copy(data, 0, expected, position, data.Length);
            CollectionAssert.AreEqual(expected, Contents(name));
        }

        #endregion
    }
}
