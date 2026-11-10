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
    /// Characterises the last 4 bytes of <see cref="SharedHeader"/>, bytes 12 to 15 of every buffer, which hold the layout
    /// marker from 2.4.0: the creator writes version 1's marker there, with its kind and the ready flag, and an opener
    /// ignores the marker's spare flags and reserved bits and never writes the bytes. A process on another version can open
    /// the same buffer, so these bytes are part of the layout. Up to 2.3.x the creator wrote 0 there and openers never read them.
    /// </summary>
    [TestClass]
    public class SharedHeaderTests
    {
        const int SpareBytesOffset = 12;

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
        public void Creator_WritesTheLayoutMarker()
        {
            // Version 1, the class's kind, lock mode 0 and the ready flag, as a little-endian int
            string name = Guid.NewGuid().ToString();
            using (var buffer = new BufferReadWrite(name, 1024))
                AssertHeaderAsCreated(name, buffer.SharedMemorySize, "BufferReadWrite", new byte[] { 0x11, 0x10, 0x00, 0x00 });

            name = Guid.NewGuid().ToString();
            using (var array = new SharedArray<int>(name, 10))
                AssertHeaderAsCreated(name, array.SharedMemorySize, "SharedArray", new byte[] { 0x21, 0x10, 0x00, 0x00 });

            name = Guid.NewGuid().ToString();
            using (var ring = new CircularBuffer(name, 3, 64))
                AssertHeaderAsCreated(name, ring.SharedMemorySize, "CircularBuffer", new byte[] { 0x31, 0x10, 0x00, 0x00 });
        }

        static void AssertHeaderAsCreated(string name, long sharedMemorySize, string type, byte[] marker)
        {
            using (var view = new HeaderView(name))
            {
                // Each field is read from the bytes the creator wrote, never through the structure
                byte[] header = view.HeaderBytes();
                Assert.AreEqual(sharedMemorySize, BitConverter.ToInt64(header, 0), type + ": bytes 0 to 7 hold SharedMemorySize");
                Assert.AreEqual(0, BitConverter.ToInt32(header, 8), type + ": bytes 8 to 11 hold Shutdown, 0 while the creator is open");
                CollectionAssert.AreEqual(marker, view.SpareBytes(), type + ": the creator writes version 1's marker to bytes 12 to 15, read ["
                    + BitConverter.ToString(view.SpareBytes()) + "]");
            }
        }

        [TestMethod]
        public void Opener_IgnoresSpareFlagsAndReservedBits()
        {
            // A ready version-1 marker of each class's kind, with the spare flags (bits 13 to 15) and the reserved bits (16 to 31) set
            byte[] marker = { 0x11, 0xF0, 0xFF, 0xFF };
            string name = Guid.NewGuid().ToString();
            using (var creator = new BufferReadWrite(name, 1024))
            using (var view = new HeaderView(name))
            {
                int value = 123;
                creator.Write(ref value);
                value = 456;
                creator.Write(ref value, 1000);
                view.WriteSpareBytes(marker);

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
                CollectionAssert.AreEqual(marker, view.SpareBytes(), "BufferReadWrite: the opener left bytes 12 to 15 as they were");
            }

            marker = new byte[] { 0x21, 0xF0, 0xFF, 0xFF };
            name = Guid.NewGuid().ToString();
            using (var creator = new SharedArray<int>(name, 10))
            using (var view = new HeaderView(name))
            {
                creator[0] = 123;
                creator[9] = 456;
                view.WriteSpareBytes(marker);

                using (var opener = new SharedArray<int>(name))
                {
                    Assert.AreEqual(10, opener.Length);
                    Assert.IsFalse(opener.ShuttingDown);
                    Assert.AreEqual(123, opener[0]);
                    Assert.AreEqual(456, opener[9]);
                    opener[5] = 789;
                    Assert.AreEqual(789, creator[5]);
                }
                CollectionAssert.AreEqual(marker, view.SpareBytes(), "SharedArray: the opener left bytes 12 to 15 as they were");
            }

            marker = new byte[] { 0x31, 0xF0, 0xFF, 0xFF };
            name = Guid.NewGuid().ToString();
            using (var creator = new CircularBuffer(name, 3, 64))
            using (var view = new HeaderView(name))
            {
                view.WriteSpareBytes(marker);

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
                CollectionAssert.AreEqual(marker, view.SpareBytes(), "CircularBuffer: the opener left bytes 12 to 15 as they were");
            }
        }
    }
}
