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
    }
}
