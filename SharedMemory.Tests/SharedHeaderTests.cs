// SharedMemory (File: SharedMemoryTests\SharedHeaderTests.cs)
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
    /// Characterises the 4 spare bytes at the end of <see cref="SharedHeader"/>, bytes 12 to 15 of every buffer: the
    /// creator writes 0 there and an opener never reads them. A process on another version can open the same buffer, so
    /// these bytes are part of the layout. 2.4.0's layout marker is planned to go in these bytes, which changes these
    /// tests deliberately.
    /// </summary>
    [TestClass]
    public class SharedHeaderTests
    {
        const int SpareBytesOffset = 12;
        static readonly byte[] Marker = new byte[] { 0xA5, 0x5A, 0xC3, 0x3C };

        /// <summary>
        /// Opens any buffer's shared memory by name, to read and write the bytes of its shared header directly
        /// </summary>
        unsafe class HeaderView : BufferReadWrite
        {
            public HeaderView(string name)
                : base(name)
            {
            }

            public byte[] HeaderBytes()
            {
                var bytes = new byte[16];
                Marshal.Copy((IntPtr)Header, bytes, 0, bytes.Length);
                return bytes;
            }

            public byte[] SpareBytes()
            {
                var bytes = new byte[4];
                Array.Copy(HeaderBytes(), SpareBytesOffset, bytes, 0, bytes.Length);
                return bytes;
            }

            public void WriteSpareBytes(byte[] bytes)
            {
                Marshal.Copy(bytes, 0, (IntPtr)((byte*)Header + SpareBytesOffset), bytes.Length);
            }
        }

        [TestMethod]
        public void SharedHeader_Is16Bytes()
        {
            // The spare bytes are the header's last 4, and every opener expects the buffer to start at byte 16
            Assert.AreEqual(16, Marshal.SizeOf(typeof(SharedHeader)));
            Assert.AreEqual(16, FastStructure.SizeOf<SharedHeader>());
        }

        [TestMethod]
        public void Creator_WritesZeroToSpareBytes()
        {
            string name = Guid.NewGuid().ToString();
            using (var buffer = new BufferReadWrite(name, 1024))
                AssertHeaderAsCreated(name, buffer.SharedMemorySize, "BufferReadWrite");

            name = Guid.NewGuid().ToString();
            using (var array = new SharedArray<int>(name, 10))
                AssertHeaderAsCreated(name, array.SharedMemorySize, "SharedArray");

            name = Guid.NewGuid().ToString();
            using (var ring = new CircularBuffer(name, 3, 64))
                AssertHeaderAsCreated(name, ring.SharedMemorySize, "CircularBuffer");
        }

        static void AssertHeaderAsCreated(string name, long sharedMemorySize, string type)
        {
            using (var view = new HeaderView(name))
            {
                // Each field is read from the bytes the creator wrote, never through the structure
                byte[] header = view.HeaderBytes();
                Assert.AreEqual(sharedMemorySize, BitConverter.ToInt64(header, 0), type + ": bytes 0 to 7 hold SharedMemorySize");
                Assert.AreEqual(0, BitConverter.ToInt32(header, 8), type + ": bytes 8 to 11 hold Shutdown, 0 while the creator is open");
                CollectionAssert.AreEqual(new byte[4], view.SpareBytes(), type + ": the creator writes 0 to bytes 12 to 15");
            }
        }

        [TestMethod]
        public void Opener_IgnoresSpareBytes()
        {
            string name = Guid.NewGuid().ToString();
            using (var creator = new BufferReadWrite(name, 1024))
            using (var view = new HeaderView(name))
            {
                int value = 123;
                creator.Write(ref value);
                value = 456;
                creator.Write(ref value, 1000);
                view.WriteSpareBytes(Marker);

                using (var opener = new BufferReadWrite(name))
                {
                    Assert.AreEqual(1024L, opener.BufferSize);
                    Assert.IsFalse(opener.ShuttingDown);
                    int read;
                    opener.Read(out read);
                    Assert.AreEqual(123, read);
                    opener.Read(out read, 1000);
                    Assert.AreEqual(456, read);
                    value = 789;
                    opener.Write(ref value, 500);
                    creator.Read(out read, 500);
                    Assert.AreEqual(789, read);
                }
                CollectionAssert.AreEqual(Marker, view.SpareBytes(), "BufferReadWrite: the opener left bytes 12 to 15 as they were");
            }

            name = Guid.NewGuid().ToString();
            using (var creator = new SharedArray<int>(name, 10))
            using (var view = new HeaderView(name))
            {
                creator[0] = 123;
                creator[9] = 456;
                view.WriteSpareBytes(Marker);

                using (var opener = new SharedArray<int>(name))
                {
                    Assert.AreEqual(10, opener.Length);
                    Assert.IsFalse(opener.ShuttingDown);
                    Assert.AreEqual(123, opener[0]);
                    Assert.AreEqual(456, opener[9]);
                    opener[5] = 789;
                    Assert.AreEqual(789, creator[5]);
                }
                CollectionAssert.AreEqual(Marker, view.SpareBytes(), "SharedArray: the opener left bytes 12 to 15 as they were");
            }

            name = Guid.NewGuid().ToString();
            using (var creator = new CircularBuffer(name, 3, 64))
            using (var view = new HeaderView(name))
            {
                view.WriteSpareBytes(Marker);

                using (var opener = new CircularBuffer(name))
                {
                    Assert.AreEqual(3, opener.NodeCount);
                    Assert.AreEqual(64, opener.NodeBufferSize);
                    Assert.IsFalse(opener.ShuttingDown);
                    byte[] data = new byte[] { 1, 2, 3, 4 };
                    byte[] read = new byte[4];
                    Assert.AreEqual(4, creator.Write(data));
                    Assert.AreEqual(4, opener.Read(read));
                    CollectionAssert.AreEqual(data, read);
                    data = new byte[] { 5, 6, 7, 8 };
                    Assert.AreEqual(4, opener.Write(data));
                    Assert.AreEqual(4, creator.Read(read));
                    CollectionAssert.AreEqual(data, read);
                }
                CollectionAssert.AreEqual(Marker, view.SpareBytes(), "CircularBuffer: the opener left bytes 12 to 15 as they were");
            }
        }
    }
}
