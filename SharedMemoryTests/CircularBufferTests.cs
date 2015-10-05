// SharedMemory (File: SharedMemoryTests\CircularBufferTests.cs)
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
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Threading;
using SharedMemory;

namespace SharedMemoryTests
{
    [TestClass]
    public class CircularBufferTests
    {
        #region Constructor tests

        [TestMethod]
        public void Constructor_ProducerEmptyName_ExceptionThrown()
        {
            string name = String.Empty;
            try
            {
                using (var smr = new CircularBuffer(name, 2, 1))
                {
                    // Allowed String.Empty name
                    Assert.Fail();
                }
            }
            catch (ArgumentException ae)
            {
                Assert.AreEqual(ae.ParamName, "name");
                return;
            }
        }

        //[TestMethod]
        //public void Constructor_ConsumerNodeCount1_ValueIgnored()
        //{
        //    string name = Guid.NewGuid().ToString();
        //    using (var smr = new CircularBuffer(name, 1, 0, false))
        //    {
        //        Assert.AreEqual(0, smr.NodeCount);
        //        Assert.AreEqual(0, smr.BufferSize);
        //    }
        //}

        //[TestMethod]
        //public void Constructor_ConsumerBufferSize1_ValueIgnored()
        //{
        //    string name = Guid.NewGuid().ToString();
        //    using (var smr = new CircularBuffer(name, 0, 1, false))
        //    {
        //        Assert.AreEqual(0, smr.BufferSize);
        //    }
        //}

        [TestMethod]
        public void Constructor_ProducerNodeCount1_ExceptionThrown()
        {
            string name = Guid.NewGuid().ToString();
            try
            {
                using (var smr = new CircularBuffer(name, 1, 1))
                {
                    // Allowed single element circular buffer
                    Assert.Fail();
                }
            }
            catch (ArgumentOutOfRangeException aor)
            {
                Assert.AreEqual(aor.ParamName, "nodeCount");
                return;
            }
        }

        [TestMethod]
        public void Constructor_ProducerNodeCount0_ExceptionThrown()
        {
            string name = Guid.NewGuid().ToString();
            try
            {
                using (var smr = new CircularBuffer(name, 0, 1))
                {
                    // Allowed zero element circular buffer
                    Assert.Fail();
                }
            }
            catch (ArgumentOutOfRangeException aor)
            {
                Assert.AreEqual(aor.ParamName, "nodeCount");
                return;
            }
        }

        #endregion

        #region Open/Close tests

        [TestMethod]
        public void Constructor_Producer_True()
        {
            string name = Guid.NewGuid().ToString();
            using (var smr = new CircularBuffer(name, 2, 1))
            {
            }
        }

        [TestMethod]
        public void Constructor_ConsumerWithoutProducer_FileNotFoundException()
        {
            string name = Guid.NewGuid().ToString();
            try
            {
                using (var smr = new CircularBuffer(name))
                {
                }
            }
            catch (System.IO.FileNotFoundException)
            {
                return;
            }
            Assert.Fail("Trying to open non-existant MMF did not throw FileNotFoundException");
        }

        [TestMethod]
        public void Constructor_DuplicateProducer_IOException()
        {
            string name = Guid.NewGuid().ToString();
            try
            {
                using (var smr = new CircularBuffer(name, 2, 1))
                using (var smr2 = new CircularBuffer(name, 2, 1))
                {
                }
            }
            catch (System.IO.IOException)
            {
                return;
            }
            Assert.Fail("Trying to create duplicate MMF did not throw IOException");
        }

        [TestMethod]
        public void Constructor_ProducerAndConsumer_True()
        {
            string name = Guid.NewGuid().ToString();
            using (var producer = new CircularBuffer(name, 2, 1))
            using (var consumer = new CircularBuffer(name))
            {

            }
        }

        [TestMethod]
        public void Close_CheckShuttingDown_True()
        {
            string name = Guid.NewGuid().ToString();
            using (var producer = new CircularBuffer(name, 2, 1))
            using (var consumer = new CircularBuffer(name))
            {
                producer.Close();

                Assert.IsTrue(consumer.ShuttingDown);
            }
        }

        [TestMethod]
        public void Constructor_BufferTooLarge_ArgumentOutOfRangeException()
        {
            string name = Guid.NewGuid().ToString();
            try
            {
                using (var smr = new CircularBuffer(name, 6, int.MaxValue))
                {
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // Success
                return;
            }
            Assert.Fail("Opening memory mapped file did not throw ArgumentOutOfRangeException for large memory buffer.");

        }

        #endregion

        #region Size assumption tests

        [TestMethod]
        public void StructSize_SharedMemoryHeader_Is16bytes()
        {
            Assert.AreEqual(16, Marshal.SizeOf(typeof(Header)));
        }

        [TestMethod]
        public void StructSize_Node_Is32bytes()
        {
            Assert.AreEqual(32, Marshal.SizeOf(typeof(CircularBuffer.Node)));
        }

        [TestMethod]
        public void StructSize_SharedMemoryNodeHeader_Is24bytes()
        {
            Assert.AreEqual(24, Marshal.SizeOf(typeof(CircularBuffer.NodeHeader)));
        }

        #endregion

        #region Read/Write tests

        [TestMethod]
        public void ReadWrite_SingleNode_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 1024;
            byte[] data = new byte[bufSize];
            byte[] readBuf = new byte[bufSize];

            // Fill with random data
            r.NextBytes(data);
            
            using (var smr = new CircularBuffer(name, 2, bufSize))
            {
                Assert.AreEqual(bufSize, smr.Write(data), String.Format("Failed to write {0} bytes", bufSize));
                Assert.AreEqual(bufSize, smr.Read(readBuf), String.Format("Failed to read {0} bytes", bufSize));

                for (var i = 0; i < data.Length; i++)
                    Assert.AreEqual(data[i], readBuf[i], String.Format("Data written does not match data read at index {0}", i));

                CircularBuffer.NodeHeader header = smr.ReadNodeHeader();
                Assert.AreEqual(1, header.WriteStart);
                Assert.AreEqual(1, header.WriteEnd);
                Assert.AreEqual(1, header.ReadStart);
                Assert.AreEqual(1, header.ReadEnd);
            }
        }

        /// <summary>
        /// Test that the Header is correct before, during and after a single read/write
        /// </summary>
        [TestMethod]
        public void ReadWrite_SingleNode_HeaderIndexesCorrect()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 1024;
            byte[] data = new byte[bufSize];
            byte[] readBuf = new byte[bufSize];
            CircularBuffer.NodeHeader header;

            // Fill with random data
            r.NextBytes(data);

            using (var smr = new CircularBuffer(name, 2, bufSize))
            {
                header = smr.ReadNodeHeader();
                Assert.AreEqual(0, header.WriteStart, "Initial WriteStart");
                Assert.AreEqual(0, header.WriteEnd, "Intial WriteEnd");
                Assert.AreEqual(0, header.ReadStart, "Initial ReadStart");
                Assert.AreEqual(0, header.ReadEnd, "Initial ReadEnd");

                Assert.AreEqual(bufSize, smr.Write((ptr) => {
                    header = smr.ReadNodeHeader();
                    Assert.AreEqual(1, header.WriteStart, "During single write WriteStart");
                    Assert.AreEqual(0, header.WriteEnd, "During single write WriteEnd");

                    Marshal.Copy(data, 0, ptr, bufSize);
                    return data.Length;
                }), String.Format("Failed to write {0} bytes", bufSize));

                header = smr.ReadNodeHeader();
                Assert.AreEqual(1, header.WriteStart, "After single write WriteStart");
                Assert.AreEqual(1, header.WriteEnd, "After single write WriteEnd");

                Assert.AreEqual(bufSize, smr.Read((ptr) =>
                    {
                        header = smr.ReadNodeHeader();
                        Assert.AreEqual(1, header.ReadStart, "During single read ReadStart");
                        Assert.AreEqual(0, header.ReadEnd, "During single read ReadEnd");

                        Marshal.Copy(ptr, readBuf, 0, smr.NodeBufferSize);
                        return smr.NodeBufferSize;
                    }), String.Format("Failed to read {0} bytes", bufSize));

                header = smr.ReadNodeHeader();
                Assert.AreEqual(1, header.ReadStart, "After single read ReadStart");
                Assert.AreEqual(1, header.ReadEnd, "After single read ReadEnd");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MyTestStruct
        {
            public int Prop1;
            public int Prop2;
            public int Prop3;
            public int Prop4;
        }

        [TestMethod]
        public void ReadWrite_MyTestStruct_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            int nodeSize = Marshal.SizeOf(typeof(MyTestStruct));

            using (var smr = new CircularBuffer(name, 2, nodeSize))
            using (var sm2 = new CircularBuffer(name))
            {
                MyTestStruct obj = new MyTestStruct
                {
                    Prop1 = 1,
                    Prop2 = 2,
                    Prop3 = 3,
                    Prop4 = 4
                };

                smr.Write(ref obj);

                MyTestStruct read;
                if (sm2.Read(out read) > 0)
                {
                    Assert.AreEqual(obj, read);
                }
                else
                {
                    Assert.Fail();
                }
            }
        }

        [TestMethod]
        public void ReadWrite_1000NodesIn2NodeRing_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 32;
            int iterations = 1000;
            byte[][] data = new byte[iterations][];
            byte[] readBuf = new byte[bufSize];
            byte[] writeBuf = null;

            // Fill with random data
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = new byte[bufSize];
                r.NextBytes(data[i]);
            }

            using (var smr = new CircularBuffer(name, 2, bufSize))
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    writeBuf = data[iteration];
                    Assert.AreEqual(bufSize, smr.Write(writeBuf), String.Format("Failed to write {0} bytes", bufSize));
                    Assert.AreEqual(bufSize, smr.Read(readBuf), String.Format("Failed to read {0} bytes", bufSize));

                    for (var i = 0; i < writeBuf.Length; i++)
                        Assert.AreEqual(writeBuf[i], readBuf[i], String.Format("Data written does not match data read at index {0}", i));
                }
            }
        }

        private long WriteMultiple<T>(CircularBuffer smr, T[][] data, out int timeouts, int delay = 0, int timeout = 1000)
            where T: struct
        {
            T[] writeBuf = null;
            long totalBytesWritten = 0;
            int iteration = 0;
            timeouts = 0;
            while (iteration < data.Length)
            {
                if (delay > 0)
                    Thread.Sleep(delay);

                writeBuf = data[iteration];
                int written = smr.Write(writeBuf, timeout);
                totalBytesWritten += written;
                if (written == 0)
                    timeouts++;
                else
                    iteration++;
            }

            return totalBytesWritten;
        }

        private long ReadMultiple<T>(CircularBuffer smr, T[][] writtenData, out int timeouts, int delay = 0, int timeout = 1000)
            where T : struct
        {
            T[] readBuf = new T[writtenData[0].Length];
            long totalBytesRead = 0;
            int iteration = 0;
            timeouts = 0;
            while (iteration < writtenData.Length)
            {
                if (delay > 0)
                    Thread.Sleep(delay);

                int read = smr.Read(readBuf, timeout);
                totalBytesRead += read;
                if (read == 0)
                    timeouts++;
                else
                {
                    iteration++;
                }
            }

            return totalBytesRead;
        }

        private long ReadMultipleWithCheck<T>(CircularBuffer smr, T[][] writtenData, out int timeouts, int delay = 0, int timeout = 1000)
            where T: struct
        {
            T[] readBuf = new T[writtenData[0].Length];
            long totalBytesRead = 0;
            int iteration = 0;
            timeouts = 0;
            while (iteration < writtenData.Length)
            {
                int read = smr.Read(readBuf, timeout);
                totalBytesRead += read;
                if (read == 0)
                    timeouts++;
                else
                {
                    for (var i = 0; i < readBuf.Length; i++)
                        Assert.AreEqual(writtenData[iteration][i], readBuf[i], String.Format("Data written does not match data read for iteration {0} at index {1}", iteration, i));
                    iteration++;
                }

                if (delay > 0)
                    Thread.Sleep(delay);
            }

            return totalBytesRead;
        }

        [TestMethod]
        public void ReadWriteAsync_1000NodesIn2NodeRing_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 32;
            int iterations = 1000;
            byte[][] data = new byte[iterations][];
            int timeouts = 0;
            // Fill with random data
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = new byte[bufSize];
                r.NextBytes(data[i]);
            }

            using (var producer = new CircularBuffer(name, 2, bufSize))
            using (var consumer = new CircularBuffer(name))
            {
                Action writer = () =>
                {
                    long totalBytesWritten = WriteMultiple(producer, data, out timeouts);
                    Assert.AreEqual(totalBytesWritten, iterations * bufSize, "Failed to write all bytes");
                };

                Action reader = () =>
                {
                    long totalBytesRead = ReadMultipleWithCheck(consumer, data, out timeouts);
                    Assert.AreEqual(totalBytesRead, iterations * bufSize, "Failed to read all bytes");
                };

                Task tWriter = Task.Factory.StartNew(writer);
                Task tReader = Task.Factory.StartNew(reader);

                if (!Task.WaitAll(new Task[] { tWriter, tReader }, 5000))
                {
                    Assert.Fail("Reader or writer took too long");
                }
            }
        }

        /// <summary>
        /// Test the write node available event signal by making the writer thread wait for the reader thread. Done by simulating a slightly slower read vs write, with a low write timeout of 1ms.
        /// </summary>
        [TestMethod]
        public void ReadWriteAsync_SlowReaderSmallWriterTimeout_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 32;
            int iterations = 10;
            byte[][] data = new byte[iterations][];

            // Fill with random data
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = new byte[bufSize];
                r.NextBytes(data[i]);
            }

            using (var producer = new CircularBuffer(name, 2, bufSize))
            using (var consumer = new CircularBuffer(name))
            {
                Action writer = () =>
                {
                    int writeTimeouts = 0;
                    long totalBytesWritten = WriteMultiple(producer, data, out writeTimeouts, 0, 1);
                    Assert.IsTrue(writeTimeouts > 0);
                };

                Action reader = () =>
                {
                    int readTimeouts = 0;
                    long totalBytesRead = ReadMultipleWithCheck(consumer, data, out readTimeouts, 1);
                };

                Task tWriter = Task.Factory.StartNew(writer);
                Task tReader = Task.Factory.StartNew(reader);

                if (!Task.WaitAll(new Task[] { tWriter, tReader }, 5000))
                {
                    Assert.Fail("Reader or writer took too long");
                }
            }
        }

        /// <summary>
        /// Test the read data exists event signal by making the reader thread wait for the writer thread. Done by simulating a slightly slower write vs read, with a low read timeout of 1ms.
        /// </summary>
        [TestMethod]
        public void ReadWriteAsync_SlowWriterSmallReaderTimeout_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 32;
            int iterations = 10;
            byte[][] data = new byte[iterations][];

            // Fill with random data
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = new byte[bufSize];
                r.NextBytes(data[i]);
            }

            using (var producer = new CircularBuffer(name, 2, bufSize))
            using (var consumer = new CircularBuffer(name))
            {
                Action writer = () =>
                {
                    int writeTimeouts = 0;
                    long totalBytesWritten = WriteMultiple(producer, data, out writeTimeouts, 1);
                };

                Action reader = () =>
                {
                    int readTimeouts = 0;
                    long totalBytesRead = ReadMultiple(consumer, data, out readTimeouts, 0, 1);
                    Assert.IsTrue(readTimeouts > 0);
                };

                Task tWriter = Task.Factory.StartNew(writer);
                Task tReader = Task.Factory.StartNew(reader);

                //Task.WaitAll(tReader, tWriter);

                if (!Task.WaitAll(new Task[] { tWriter, tReader }, 10000))
                {
                    Assert.Fail("Reader or writer took too long");
                }
            }
        }


        /// <summary>
        /// <para>Tests the integrity of the protected <see cref="SharedMemoryRing.PostNode"/> and <see cref="SharedMemoryRing.ReturnNode"/> functions to ensure that
        /// nodes are made available in the same order that they were reserved regardless of the order Post/ReturnNode is called.</para>
        /// <para>E.g. if nodes 1, 2 & 3 are reserved for writing in sequence, but they are ready in reverse order (i.e. PostNode is called for node 3, 2 and then 1), 
        /// the call to PostNode for node 3 and 2 will simply mark <see cref="SharedMemoryRing.Node.DoneWrite"/> as 1 and then return, while the call to PostNode 
        /// for node 1 will move the WriteEnd pointer for node 1, 2 and then 3 also clearing the DoneWrite flag. This ensures that the nodes are ready for reading in 
        /// the correct order and the read/write indexes maintain their integrity. The same applies to reading and ReturnNode.</para>
        /// </summary>
        [TestMethod]
        public void ReadWrite_NonSequentialReadWrite_HeaderIndexesCorrect()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 1024;
            byte[] data = new byte[bufSize];
            byte[] readBuf = new byte[bufSize];
            CircularBuffer.NodeHeader header;

            // Fill with random data
            r.NextBytes(data);

            using (var smr = new CircularBuffer(name, 5, bufSize))
            {
                header = smr.ReadNodeHeader();
                Assert.AreEqual(0, header.WriteStart, "Initial WriteStart");
                Assert.AreEqual(0, header.WriteEnd, "Intial WriteEnd");
                Assert.AreEqual(0, header.ReadStart, "Initial ReadStart");
                Assert.AreEqual(0, header.ReadEnd, "Initial ReadEnd");

                Assert.AreEqual(bufSize, smr.Write((ptr) =>
                {
                    header = smr.ReadNodeHeader();
                    Assert.AreEqual(1, header.WriteStart, "During nested out of order write (1) WriteStart");
                    Assert.AreEqual(0, header.WriteEnd, "During nested out of order write (1) WriteEnd");

                    smr.Write((ptr2) =>
                    {
                        header = smr.ReadNodeHeader();
                        Assert.AreEqual(2, header.WriteStart, "During nested out of order write (2) WriteStart");
                        Assert.AreEqual(0, header.WriteEnd, "During nested out of order write (2) WriteEnd");

                        smr.Write((ptr3) =>
                        {
                            header = smr.ReadNodeHeader();
                            Assert.AreEqual(3, header.WriteStart, "During nested out of order write (3) WriteStart");
                            Assert.AreEqual(0, header.WriteEnd, "During nested out of order write (3) WriteEnd");

                            Marshal.Copy(data, 0, ptr3, bufSize);
                            return bufSize;
                        });
                        header = smr.ReadNodeHeader();
                        Assert.AreEqual(0, header.WriteEnd, "After nested out of order write (3) WriteEnd");

                        Marshal.Copy(data, 0, ptr2, bufSize);
                        return bufSize;
                    });
                    header = smr.ReadNodeHeader();
                    Assert.AreEqual(0, header.WriteEnd, "After nested out of order write (2) WriteEnd");

                    Marshal.Copy(data, 0, ptr, bufSize);
                    return data.Length;
                }), String.Format("Failed to write {0} bytes", bufSize));

                header = smr.ReadNodeHeader();
                Assert.AreEqual(3, header.WriteStart, "After nested out of order writes (1,2,3) WriteStart");
                Assert.AreEqual(3, header.WriteEnd, "After nested out of order writes (1,2,3) WriteEnd");

                Assert.AreEqual(bufSize, smr.Read((ptr) =>
                {
                    header = smr.ReadNodeHeader();
                    Assert.AreEqual(1, header.ReadStart, "During nested out of order read (1) ReadStart");
                    Assert.AreEqual(0, header.ReadEnd, "During nested out of order read (1) ReadEnd");

                    smr.Read((ptr2) =>
                    {
                        header = smr.ReadNodeHeader();
                        Assert.AreEqual(2, header.ReadStart, "During nested out of order read (2) ReadStart");
                        Assert.AreEqual(0, header.ReadEnd, "During nested out of order read (2) ReadEnd");

                        smr.Read((ptr3) =>
                        {
                            header = smr.ReadNodeHeader();
                            Assert.AreEqual(3, header.ReadStart, "During nested out of order read (3) ReadStart");
                            Assert.AreEqual(0, header.ReadEnd, "During nested out of order read (3) ReadEnd");

                            Marshal.Copy(ptr3, readBuf, 0, smr.NodeBufferSize);
                            return smr.NodeBufferSize;
                        });
                        header = smr.ReadNodeHeader();
                        Assert.AreEqual(0, header.ReadEnd, "After nested out of order read (3) ReadEnd");

                        Marshal.Copy(ptr2, readBuf, 0, smr.NodeBufferSize);
                        return smr.NodeBufferSize;
                    });
                    header = smr.ReadNodeHeader();
                    Assert.AreEqual(0, header.ReadEnd, "After nested out of order read (2) ReadEnd");

                    Marshal.Copy(ptr, readBuf, 0, smr.NodeBufferSize);
                    return smr.NodeBufferSize;
                }), String.Format("Failed to read {0} bytes", bufSize));

                header = smr.ReadNodeHeader();
                Assert.AreEqual(3, header.ReadStart, "After nested out of order read (1,2,3) ReadStart");
                Assert.AreEqual(3, header.ReadEnd, "After nested out of order read (1,2,3) ReadEnd");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TestStruct
        {
            public int Value1;
            public int Value2;
        }

        [TestMethod]
        public void ReadWrite_StructuredData_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            TestStruct[] data = new TestStruct[100];
            TestStruct[] readBuff = new TestStruct[100];
            int bufSize = Marshal.SizeOf(typeof(TestStruct)) * 100;

            // Fill with random data
            for (var i = 0; i < data.Length; i++)
            {
                data[i].Value1 = r.Next();
                data[i].Value2 = r.Next();
            }

            using (var smr = new CircularBuffer(name, 2, bufSize))
            {
                smr.Write(data);
                smr.Read(readBuff);

                for (var i = 0; i < data.Length; i++)
                    Assert.AreEqual(data[i], readBuff[i], String.Format("Data written does not match data read at index {0}", i));
            }
        }

        [TestMethod]
        public unsafe void ReadWrite_IntPtr_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 32;
            int iterations = 1;
            byte[][] data = new byte[iterations][];
            byte[] readBuf = new byte[bufSize];
            byte[] writeBuf = null;

            // Fill with random data
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = new byte[bufSize];
                r.NextBytes(data[i]);
            }

            using (var smr = new CircularBuffer(name, 2, bufSize))
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    writeBuf = data[iteration];
                    fixed (byte* wPtr = &writeBuf[0])
                    {
                        Assert.AreEqual(bufSize, smr.Write((IntPtr)wPtr, bufSize), String.Format("Failed to write {0} bytes", bufSize));
                    }
                    fixed (byte* rPtr = &readBuf[0])
                    {
                        Assert.AreEqual(bufSize, smr.Read((IntPtr)rPtr, bufSize), String.Format("Failed to write {0} bytes", bufSize));
                    }

                    for (var i = 0; i < writeBuf.Length; i++)
                        Assert.AreEqual(writeBuf[i], readBuf[i], String.Format("Data written does not match data read at index {0}", i));
                }
            }
        }

        [TestMethod]
        public unsafe void ReadWrite_DelegateIntPtr_DataMatches()
        {
            string name = Guid.NewGuid().ToString();
            Random r = new Random();
            int bufSize = 32;
            int iterations = 1;
            byte[][] data = new byte[iterations][];
            byte[] readBuf = new byte[bufSize];
            byte[] writeBuf = null;

            // Fill with random data
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = new byte[bufSize];
                r.NextBytes(data[i]);
            }

            // create write delegate
            Func<IntPtr,int> writeFunc = (addr) =>
            {
                int indx = 0;
                int count = writeBuf.Length;
                while (count > 0)
                {
                    var b = writeBuf[indx++];
                    Marshal.WriteByte(addr, indx, b);
                    count--;
                }
                return writeBuf.Length;
            };

            // create read delegate
            Func<IntPtr, int> readFunc = (addr) =>
            {
                int indx = 0;
                int count = readBuf.Length;
                while (count > 0)
                {
                    readBuf[indx++] = Marshal.ReadByte(addr, indx);
                    count--;
                }
                return readBuf.Length;
            };

            using (var smr = new CircularBuffer(name, 2, bufSize))
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    writeBuf = data[iteration];
                    Assert.AreEqual(bufSize, smr.Write(writeFunc), String.Format("Failed to write {0} bytes", bufSize));
                    Assert.AreEqual(bufSize, smr.Read(readFunc), String.Format("Failed to write {0} bytes", bufSize));

                    for (var i = 0; i < writeBuf.Length; i++)
                        Assert.AreEqual(writeBuf[i], readBuf[i], String.Format("Data written does not match data read at index {0}", i));
                }
            }
        }

        #endregion

    }
}
