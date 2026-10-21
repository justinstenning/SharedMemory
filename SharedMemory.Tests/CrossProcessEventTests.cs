using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The named events signalling between two processes: CircularBuffer's auto-reset events, and
    /// the manual-reset events behind BufferWithLocks' read and write locks.
    /// </summary>
    [TestClass]
    public class CrossProcessEventTests
    {
        /// <summary>
        /// How long a wait must stay blocked before the test counts it as blocked.
        /// </summary>
        const int BlockedFor = 300;

        static readonly byte[] Data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        [TestMethod]
        public void CircularBuffer_ReaderBlockedInRead_WakesWhenOtherProcessWrites()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create circular " + name + " 4 64");
                using (var consumer = new CircularBuffer(name))
                {
                    var read = Task.Run(() =>
                    {
                        var data = new byte[64];
                        int count = consumer.Read(data, 0, 10000);
                        return data.Take(count).ToArray();
                    });
                    Assert.IsFalse(read.Wait(BlockedFor), "Nothing has been written yet.");

                    peer.Ok("write " + PeerProtocol.ToHex(Data));

                    Assert.IsTrue(read.Wait(5000), "The read did not wake.");
                    CollectionAssert.AreEqual(Data, read.Result);
                }
            }
        }

        [TestMethod]
        public void CircularBuffer_OtherProcessBlockedInRead_WakesWhenThisProcessWrites()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create circular " + name + " 4 64");
                using (var producer = new CircularBuffer(name))
                {
                    var read = Task.Run(() => peer.Ok("read 64 10000", 20000));
                    Assert.IsFalse(read.Wait(BlockedFor), "Nothing has been written yet.");

                    Assert.AreEqual(Data.Length, producer.Write(Data));

                    Assert.IsTrue(read.Wait(5000), "The peer's read did not wake.");
                    Assert.AreEqual(PeerProtocol.ToHex(Data), read.Result);
                }
            }
        }

        [TestMethod]
        public void CircularBuffer_WriterBlockedOnFullBuffer_WakesWhenOtherProcessReads()
        {
            string name = Guid.NewGuid().ToString();
            // Two nodes hold one node of data
            using (var producer = new CircularBuffer(name, 2, 64))
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("open circular " + name);
                Assert.AreEqual(Data.Length, producer.Write(Data, 0, 0));

                var write = Task.Run(() => producer.Write(Data, 0, 10000));
                Assert.IsFalse(write.Wait(BlockedFor), "The buffer is full.");

                Assert.AreEqual(PeerProtocol.ToHex(Data), peer.Ok("read 64"));

                Assert.IsTrue(write.Wait(5000), "The write did not wake.");
                Assert.AreEqual(Data.Length, write.Result);
            }
        }

        [TestMethod]
        public void BufferWithLocks_WriteLockHeldByOtherProcess_BlocksReadUntilReleased()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create readwrite " + name + " 1024");
                using (var consumer = new BufferReadWrite(name))
                {
                    var data = new byte[Data.Length];
                    peer.Ok("lock write");

                    Assert.ThrowsExactly<TimeoutException>(() => consumer.Read(data));

                    peer.Ok("write " + PeerProtocol.ToHex(Data));
                    peer.Ok("unlock write");
                    consumer.Read(data);
                    CollectionAssert.AreEqual(Data, data);
                }
            }
        }

        [TestMethod]
        public void BufferWithLocks_ReadLockHeldByOtherProcess_BlocksWriteUntilReleased()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create readwrite " + name + " 1024");
                using (var consumer = new BufferReadWrite(name))
                {
                    var data = Data.ToArray();
                    peer.Ok("lock read");

                    Assert.ThrowsExactly<TimeoutException>(() => consumer.Write(data));

                    peer.Ok("unlock read");
                    consumer.Write(data);
                    Assert.AreEqual(PeerProtocol.ToHex(Data), peer.Ok("read 16"));
                }
            }
        }
    }
}
