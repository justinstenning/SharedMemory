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
    /// the buffer as it was. A copy that starts outside the buffer throws <see cref="ArgumentOutOfRangeException"/>, one that
    /// starts inside and runs past the end throws <see cref="ArgumentException"/>, and neither copies anything.
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

        #region BufferReadWrite out of range

        [TestMethod]
        [DataRow("bytes")]
        [DataRow("elements")]
        [DataRow("pointer")]
        public void Write_RunningPastTheEnd_ThrowsArgumentExceptionAndWritesNothing(string overload)
        {
            string name = Guid.NewGuid().ToString();
            byte[] before = Pattern(Size, 11);
            using (var owner = new BufferReadWrite(name, Size))
            {
                owner.Write(before);

                var e = Assert.ThrowsExactly<ArgumentException>(() => WriteThrough(owner, overload, Pattern(64, 13), 1000, null));

                Assert.AreEqual("bufferPosition", e.ParamName);
                CollectionAssert.AreEqual(before, Contents(name), "Bytes 1,000 to 1,023 changed.");
            }
        }

        [TestMethod]
        [DataRow("bytes")]
        [DataRow("elements")]
        [DataRow("pointer")]
        public void Read_RunningPastTheEnd_ThrowsArgumentExceptionAndCopiesNothing(string overload)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            {
                owner.Write(Pattern(Size, 11));
                byte[] destination = Filled(64, 0xEE);

                var e = Assert.ThrowsExactly<ArgumentException>(() => ReadThrough(owner, overload, destination, 1000, null));

                Assert.AreEqual("bufferPosition", e.ParamName);
                CollectionAssert.AreEqual(Filled(64, 0xEE), destination);
            }
        }

        [TestMethod]
        [DataRow("bytes")]
        [DataRow("elements")]
        [DataRow("pointer")]
        [DataRow("delegate")]
        public void Write_FarPastTheEnd_ThrowsArgumentOutOfRangeException(string overload)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            {
                bool ran = false;

                var e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WriteThrough(owner, overload, Pattern(64, 13), 1L << 30, () => ran = true));

                Assert.AreEqual("bufferPosition", e.ParamName);
                Assert.IsFalse(ran, "The delegate ran.");
                CollectionAssert.AreEqual(new byte[Size], Contents(name));
            }
        }

        [TestMethod]
        [DataRow("bytes")]
        [DataRow("elements")]
        [DataRow("pointer")]
        [DataRow("delegate")]
        public void Read_FarPastTheEnd_ThrowsArgumentOutOfRangeException(string overload)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            {
                byte[] destination = Filled(64, 0xEE);
                bool ran = false;

                var e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ReadThrough(owner, overload, destination, 1L << 30, () => ran = true));

                Assert.AreEqual("bufferPosition", e.ParamName);
                Assert.IsFalse(ran, "The delegate ran.");
                CollectionAssert.AreEqual(Filled(64, 0xEE), destination);
            }
        }

        [TestMethod]
        [DataRow("bytes")]
        [DataRow("elements")]
        [DataRow("pointer")]
        [DataRow("delegate")]
        public void Read_BeforeTheStart_ThrowsArgumentOutOfRangeExceptionAndCopiesNothing(string overload)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            {
                byte[] destination = Filled(16, 0xEE);
                bool ran = false;

                var e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ReadThrough(owner, overload, destination, -16, () => ran = true));

                Assert.AreEqual("bufferPosition", e.ParamName);
                Assert.IsFalse(ran, "The delegate ran.");
                CollectionAssert.AreEqual(Filled(16, 0xEE), destination, "The header was copied.");
            }
        }

        [TestMethod]
        [DataRow("bytes")]
        [DataRow("elements")]
        [DataRow("pointer")]
        [DataRow("delegate")]
        public void Write_BeforeTheStart_ThrowsArgumentOutOfRangeExceptionAndLeavesTheHeaderAlone(string overload)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            using (var opener = new BufferReadWrite(name))
            {
                bool ran = false;

                // 8 bytes before the buffer is the header's shutdown flag
                var e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WriteThrough(owner, overload, new byte[] { 1, 0, 0, 0 }, -8, () => ran = true));

                Assert.AreEqual("bufferPosition", e.ParamName);
                Assert.IsFalse(ran, "The delegate ran.");
                Assert.IsFalse(opener.ShuttingDown, "The write reached the header's shutdown flag.");
                using (var later = new BufferReadWrite(name))
                    Assert.AreEqual(Size, later.BufferSize);
            }
        }

        /// <summary>
        /// A structure that starts inside the buffer and runs past its end writes nothing. The .NET Framework 3.5 build's own view
        /// accessor throws ArgumentOutOfRangeException for it, and every other build ArgumentException.
        /// </summary>
        [TestMethod]
        public void Structure_RunningPastTheEnd_ThrowsAndWritesNothing()
        {
            string name = Guid.NewGuid().ToString();
            byte[] before = Pattern(Size, 11);
            using (var owner = new BufferReadWrite(name, Size))
            {
                owner.Write(before);
                long value = -1;

                Assert.Throws<ArgumentException>(() => owner.Write(ref value, Size - 4));

                CollectionAssert.AreEqual(before, Contents(name), "Bytes 1,020 to 1,023 changed.");
            }
        }

        [TestMethod]
        public void Structure_BeforeTheStart_ThrowsArgumentOutOfRangeExceptionAndLeavesTheHeaderAlone()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            using (var opener = new BufferReadWrite(name))
            {
                int one = 1;
                int read;

                var write = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => owner.Write(ref one, -8));
                var readError = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => owner.Read(out read, -8));

                Assert.AreEqual("position", write.ParamName);
                Assert.AreEqual("position", readError.ParamName);
                Assert.IsFalse(opener.ShuttingDown, "The write reached the header's shutdown flag.");
            }
        }

        [TestMethod]
        [DataRow("write bytes")]
        [DataRow("read bytes")]
        [DataRow("write elements")]
        [DataRow("read elements")]
        [DataRow("write pointer")]
        [DataRow("read pointer")]
        [DataRow("write delegate")]
        [DataRow("read delegate")]
        public void EmptyCopy_AtTheEnd_ThrowsArgumentOutOfRangeException(string call)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            {
                bool ran = false;

                var e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Call(owner, call, new byte[0], Size, () => ran = true));

                Assert.AreEqual("bufferPosition", e.ParamName);
                Assert.IsFalse(ran, "The delegate ran.");
            }
        }

        [TestMethod]
        [DataRow("write bytes")]
        [DataRow("read bytes")]
        [DataRow("write elements")]
        [DataRow("read elements")]
        [DataRow("write pointer")]
        [DataRow("read pointer")]
        [DataRow("write delegate")]
        [DataRow("read delegate")]
        public void Position_NearLongMaxValue_ThrowsArgumentOutOfRangeExceptionWithoutOverflow(string call)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new BufferReadWrite(name, Size))
            {
                bool ran = false;

                // position + 64 overflows a long
                var e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Call(owner, call, new byte[64], long.MaxValue - 10, () => ran = true));

                Assert.AreEqual("bufferPosition", e.ParamName);
                Assert.IsFalse(ran, "The delegate ran.");
                CollectionAssert.AreEqual(new byte[Size], Contents(name));
            }
        }

        [TestMethod]
        [DataRow("write pointer")]
        [DataRow("read pointer")]
        public void Pointer_NegativeLength_ThrowsArgumentOutOfRangeException(string call)
        {
            string name = Guid.NewGuid().ToString();
            var bytes = new byte[16];
            using (var owner = new BufferReadWrite(name, Size))
            {
                var e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    Pinned(bytes, p => { if (call == "write pointer") owner.Write(p, -1, 0); else owner.Read(p, -1, 0); }));

                Assert.AreEqual("length", e.ParamName);
                CollectionAssert.AreEqual(new byte[Size], Contents(name));
                CollectionAssert.AreEqual(new byte[16], bytes);
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

        [TestMethod]
        [DataRow("WriteArray", -8L, typeof(ArgumentOutOfRangeException))]
        [DataRow("WriteArray", 1000L, typeof(ArgumentException))]
        [DataRow("ReadArray", -8L, typeof(ArgumentOutOfRangeException))]
        [DataRow("ReadArray", 1000L, typeof(ArgumentException))]
        [DataRow("WriteFrom", -8L, typeof(ArgumentOutOfRangeException))]
        [DataRow("WriteFrom", 1000L, typeof(ArgumentException))]
        public void ProtectedArrayCopies_OutOfRange_ThrowAndCopyNothing(string call, long position, Type expected)
        {
            string name = Guid.NewGuid().ToString();
            using (var buffer = new ArrayCopies(name, Size))
            using (var opener = new ArrayCopies(name))
            {
                // Before the buffer, 1 where the header's shutdown flag is; past its end, 64 bytes from 1,000
                byte[] data = position < 0 ? new byte[] { 1, 0, 0, 0 } : Pattern(64, 17);
                byte[] destination = Filled(data.Length, 0xEE);
                Exception thrown = null;
                try
                {
                    if (call == "WriteArray")
                        buffer.CallWriteArray(data, 0, data.Length, position);
                    else if (call == "ReadArray")
                        buffer.CallReadArray(destination, 0, destination.Length, position);
                    else
                        buffer.CallWriteFrom(data, 0, position);
                }
                catch (Exception e)
                {
                    thrown = e;
                }

                Assert.IsNotNull(thrown, "Nothing was thrown.");
                Assert.AreEqual(expected, thrown.GetType());
                Assert.AreEqual("bufferPosition", ((ArgumentException)thrown).ParamName);
                Assert.IsFalse(opener.ShuttingDown, "The write reached the header's shutdown flag.");
                CollectionAssert.AreEqual(Filled(data.Length, 0xEE), destination, "The read copied bytes.");
                CollectionAssert.AreEqual(new byte[Size], opener.ReadBytes(Size), "The buffer changed.");
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

        static byte[] Filled(int count, byte value)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++)
                bytes[i] = value;
            return bytes;
        }

        static int[] ToInts(byte[] bytes)
        {
            var values = new int[bytes.Length / sizeof(int)];
            Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(int));
            return values;
        }

        // Writes bytes at position through one overload of BufferReadWrite: "bytes" Write(byte[]), "elements" Write(int[]) of the
        // same bytes, "pointer" Write(IntPtr, int), or "delegate" Write(Action<IntPtr>), whose delegate calls ran, then copies.
        // An unknown overload throws InvalidOperationException, which no test expects.
        static void WriteThrough(BufferReadWrite buffer, string overload, byte[] bytes, long position, Action ran)
        {
            switch (overload)
            {
                case "bytes":
                    buffer.Write(bytes, position);
                    break;
                case "elements":
                    buffer.Write(ToInts(bytes), position);
                    break;
                case "pointer":
                    Pinned(bytes, p => buffer.Write(p, bytes.Length, position));
                    break;
                case "delegate":
                    buffer.Write(p => { ran(); Marshal.Copy(bytes, 0, p, bytes.Length); }, position);
                    break;
                default:
                    throw new InvalidOperationException("Unknown overload " + overload);
            }
        }

        // Reads destination.Length bytes at position into destination through one overload, as WriteThrough writes. Whatever an
        // overload copied before it threw is left in destination.
        static void ReadThrough(BufferReadWrite buffer, string overload, byte[] destination, long position, Action ran)
        {
            switch (overload)
            {
                case "bytes":
                    buffer.Read(destination, position);
                    break;
                case "elements":
                    {
                        int[] values = ToInts(destination);
                        try
                        {
                            buffer.Read(values, position);
                        }
                        finally
                        {
                            Buffer.BlockCopy(values, 0, destination, 0, values.Length * sizeof(int));
                        }
                        break;
                    }
                case "pointer":
                    Pinned(destination, p => buffer.Read(p, destination.Length, position));
                    break;
                case "delegate":
                    buffer.Read(p => { ran(); Marshal.Copy(p, destination, 0, destination.Length); }, position);
                    break;
                default:
                    throw new InvalidOperationException("Unknown overload " + overload);
            }
        }

        // "write <overload>" or "read <overload>", with bytes as the source or the destination
        static void Call(BufferReadWrite buffer, string call, byte[] bytes, long position, Action ran)
        {
            string[] parts = call.Split(' ');
            if (parts[0] == "write")
                WriteThrough(buffer, parts[1], bytes, position, ran);
            else if (parts[0] == "read")
                ReadThrough(buffer, parts[1], bytes, position, ran);
            else
                throw new InvalidOperationException("Unknown call " + call);
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
