using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SharedMemoryTests
{
    [TestClass]
    public class BufferReadWriteTests
    {
        [TestMethod]
        public void Constructor_ProducerConsumer_Created()
        {
            var name = Guid.NewGuid().ToString();
            using (var buf = new SharedMemory.BufferReadWrite(name, 1024))
            using (var buf2 = new SharedMemory.BufferReadWrite(name))
            {

            }
        }

        [TestMethod]
        public void ReadWrite_Bytes_DataMatches()
        {
            var name = Guid.NewGuid().ToString();
            Random r = new Random();
            byte[] data = new byte[1024];
            byte[] readData = new byte[1024];
            r.NextBytes(data);

            using (var buf = new SharedMemory.BufferReadWrite(name, 1024))
            using (var buf2 = new SharedMemory.BufferReadWrite(name))
            {
                buf.Write(data);
                buf2.Read(readData);

                for (var i = 0; i < data.Length; i++)
                {
                    Assert.AreEqual(data[i], readData[i]);
                }
            }
        }

        [TestMethod]
        public void ReadWrite_TimeoutException()
        {
            bool timedout = false;
            var name = Guid.NewGuid().ToString();
            byte[] data = new byte[1024];
            byte[] readData = new byte[1024];


            using (var buf = new SharedMemory.BufferReadWrite(name, 1024))
            using (var buf2 = new SharedMemory.BufferReadWrite(name))
            {
                // Set a small timeout to speed up the test
                buf2.ReadWriteTimeout = 0;

                // Introduce possible deadlock by acquiring without releasing the write lock.
                buf.AcquireWriteLock();

                // We want the AcquireReadLock to fail
                if (!buf2.AcquireReadLock(1))
                {
                    try
                    {
                        // Read should timeout with TimeoutException because buf.ReleaseWriteLock has not been called
                        buf2.Read(readData);
                    }
                    catch (TimeoutException e)
                    {
                        timedout = true;
                    }
                }
                Assert.AreEqual(true, timedout, "The TimeoutException was not thrown.");

                // Remove the deadlock situation, by releasing the write lock...
                buf.ReleaseWriteLock();
                // ...and ensure that we can now read the data
                if (buf.AcquireReadLock(1))
                    buf2.Read(readData);
                else
                    Assert.Fail("Failed to acquire read lock after releasing write lock.");
            }
        }
    }
}
