using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// When a name can be created and opened, across two processes. Each test runs on a buffer with
    /// no events and on a <see cref="CircularBuffer"/>, which also has two named events.
    /// </summary>
    [TestClass]
    public class LifetimeTests
    {
        const string EventFree = "eventfree";
        const string Circular = "circular";

        static readonly byte[] Data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        [TestMethod]
        [DataRow(EventFree)]
        [DataRow(Circular)]
        public void Create_NameHeldByOtherProcess_ThrowsIOException(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok(LocalBuffer.CreateCommand(kind, name));

                Assert.ThrowsExactly<IOException>(() => LocalBuffer.Create(kind, name));
            }
        }

        [TestMethod]
        [DataRow(EventFree)]
        [DataRow(Circular)]
        public void Open_NameNobodyHolds_ThrowsFileNotFoundException(string kind)
        {
            string name = Guid.NewGuid().ToString();

            Assert.ThrowsExactly<FileNotFoundException>(() => LocalBuffer.Open(kind, name));
        }

        [TestMethod]
        [DataRow(EventFree)]
        [DataRow(Circular)]
        public void OwnerKilled_NothingElseHolds_NewOwnerCreatesNameAndStartsEmpty(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok(LocalBuffer.CreateCommand(kind, name));
                peer.Ok("write " + PeerProtocol.ToHex(Data));
                peer.Kill();
            }

            using (var owner = LocalBuffer.Create(kind, name))
            {
                CollectionAssert.AreEqual(LocalBuffer.Empty(kind), owner.Read(100));
            }
        }

        [TestMethod]
        [DataRow(EventFree)]
        [DataRow(Circular)]
        public void OwnerCloses_NothingElseHolds_NameIsGone(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok(LocalBuffer.CreateCommand(kind, name));
                peer.Ok("close");

                Assert.ThrowsExactly<FileNotFoundException>(() => LocalBuffer.Open(kind, name));
            }
        }

        [TestMethod]
        [DataRow(EventFree)]
        [DataRow(Circular)]
        public void LastConsumerCloses_NameIsGone(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok(LocalBuffer.CreateCommand(kind, name));
                var consumer = LocalBuffer.Open(kind, name);
                peer.Ok("close");
                peer.Exit();
                consumer.Dispose();

                Assert.ThrowsExactly<FileNotFoundException>(() => LocalBuffer.Open(kind, name));
            }
        }

        [TestMethod]
        [DataRow(EventFree)]
        [DataRow(Circular)]
        public void OwnerCloses_ConsumerKeepsReadingAndSeesShuttingDown(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok(LocalBuffer.CreateCommand(kind, name));
                peer.Ok("write " + PeerProtocol.ToHex(Data));
                using (var consumer = LocalBuffer.Open(kind, name))
                {
                    Assert.IsFalse(consumer.ShuttingDown);

                    peer.Ok("close");
                    peer.Exit();

                    Assert.IsTrue(consumer.ShuttingDown);
                    CollectionAssert.AreEqual(Data, consumer.Read());
                }
            }
        }

        [TestMethod]
        [DataRow(EventFree)]
        [DataRow(Circular)]
        public void OwnerClosed_ConsumerHolds_SecondConsumerOpensAndCreateThrowsIOException(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok(LocalBuffer.CreateCommand(kind, name));
                using (LocalBuffer.Open(kind, name))
                {
                    peer.Ok("close");
                    peer.Exit();

                    using (var second = LocalBuffer.Open(kind, name))
                    {
                        Assert.IsTrue(second.ShuttingDown);
                    }
                    Assert.ThrowsExactly<IOException>(() => LocalBuffer.Create(kind, name));
                }
            }
        }

        [TestMethod]
        [DataRow(EventFree)]
        [DataRow(Circular)]
        public void OwnerKilled_ConsumerKeepsReadingAndShuttingDownStaysFalse(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok(LocalBuffer.CreateCommand(kind, name));
                peer.Ok("write " + PeerProtocol.ToHex(Data));
                using (var consumer = LocalBuffer.Open(kind, name))
                {
                    peer.Kill();

                    Assert.IsFalse(consumer.ShuttingDown);
                    CollectionAssert.AreEqual(Data, consumer.Read());
                }
            }
        }

        /// <summary>
        /// The test process's side: an <see cref="EventFreeBuffer"/> or a <see cref="CircularBuffer"/>,
        /// the same shape as the peer's.
        /// </summary>
        sealed class LocalBuffer : IDisposable
        {
            const int Size = 1024;
            const int NodeCount = 4;
            const int NodeSize = 64;

            readonly SharedBuffer buffer;

            LocalBuffer(SharedBuffer buffer)
            {
                this.buffer = buffer;
            }

            public static string CreateCommand(string kind, string name)
            {
                return kind == Circular
                    ? "create circular " + name + " " + NodeCount + " " + NodeSize
                    : "create eventfree " + name + " " + Size;
            }

            public static LocalBuffer Create(string kind, string name)
            {
                return new LocalBuffer(kind == Circular ? (SharedBuffer)new CircularBuffer(name, NodeCount, NodeSize) : new EventFreeBuffer(name, Size));
            }

            public static LocalBuffer Open(string kind, string name)
            {
                return new LocalBuffer(kind == Circular ? (SharedBuffer)new CircularBuffer(name) : new EventFreeBuffer(name));
            }

            /// <summary>
            /// What <see cref="Read"/> returns from a new buffer.
            /// </summary>
            public static byte[] Empty(string kind)
            {
                return kind == Circular ? new byte[0] : new byte[Data.Length];
            }

            public bool ShuttingDown
            {
                get { return buffer.ShuttingDown; }
            }

            /// <summary>
            /// The first <see cref="Data"/>.Length bytes of an event-free buffer, or the next node of a
            /// circular buffer, which is empty if nothing arrives within the timeout.
            /// </summary>
            public byte[] Read(int timeout = 1000)
            {
                var circular = buffer as CircularBuffer;
                if (circular == null)
                    return ((EventFreeBuffer)buffer).ReadBytes(Data.Length);

                var data = new byte[NodeSize];
                int read = circular.Read(data, 0, timeout);
                return data.Take(read).ToArray();
            }

            public void Dispose()
            {
                buffer.Dispose();
            }
        }
    }
}
