using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;
#if NET
using System.Runtime.Versioning;
#endif
using static SharedMemoryTests.CreateOrOpenTests;

namespace SharedMemoryTests
{
    /// <summary>
    /// <see cref="SharedBuffer.TryGetInfo"/>: a snapshot of a buffer's header, read without opening it for use, for each kind, for a
    /// buffer not ready, closed by its owner, from SharedMemory 2.3.x or from a later version, and that it changes nothing.
    /// </summary>
    [TestClass]
    public class BufferInfoTests
    {
        const string EventFree = "SharedBuffer subclass";
        const string RpcRing = "RpcBuffer ring";

        /// <summary>
        /// Nothing has the name: false, and no info.
        /// </summary>
        [TestMethod]
        public void TryGetInfo_MissingName_False()
        {
            SharedBufferInfo info;
            Assert.IsFalse(SharedBuffer.TryGetInfo(Guid.NewGuid().ToString(), out info));
            Assert.IsNull(info);
        }

        /// <summary>
        /// For each kind, the info matches the buffer's own instance: its kind, layout version 1, supported, ready, not shutting
        /// down, its BufferSize, and a ring's node count and node size. Another process reads the same.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
#if HAS_RPC_BUFFER
        [DataRow(RpcRing)]
#endif
        [DataRow(EventFree)]
        public void TryGetInfo_EachKind_MatchesTheInstance(string kind)
        {
            string channel = Guid.NewGuid().ToString(), name = channel;
            IDisposable owner;
            long bufferSize;
            int nodeCount = 0, nodeSize = 0;
            SharedBufferKind expected;
#if HAS_RPC_BUFFER
            if (kind == RpcRing)
            {
                var rpc = new RpcBuffer(channel, 1024);
                owner = rpc;
                name = channel + "_Opener_SharedMemory_MMF";
                using (var ring = new CircularBuffer(name))
                {
                    bufferSize = ring.BufferSize;
                    nodeCount = ring.NodeCount;
                    nodeSize = ring.NodeBufferSize;
                }
                expected = SharedBufferKind.RpcBufferRing;
            }
            else
#endif
            if (kind == EventFree)
            {
                var buffer = new EventFreeBuffer(name, Size);
                owner = buffer;
                bufferSize = buffer.BufferSize;
                expected = SharedBufferKind.Other;
            }
            else
            {
                var buffer = CreateToday(kind, name);
                owner = buffer;
                bufferSize = buffer.BufferSize;
                if (kind == Ring)
                {
                    nodeCount = ((CircularBuffer)buffer).NodeCount;
                    nodeSize = ((CircularBuffer)buffer).NodeBufferSize;
                }
                expected = kind == Ring ? SharedBufferKind.CircularBuffer : kind == IntArray ? SharedBufferKind.SharedArray : SharedBufferKind.BufferReadWrite;
            }

            using (owner)
            using (var peer = PeerProcess.Start())
            {
                SharedBufferInfo info;
                Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info), kind);
                Assert.AreEqual(name, info.Name, kind + ": Name");
                Assert.AreEqual(expected, info.Kind, kind + ": Kind");
                Assert.AreEqual(1, info.LayoutVersion, kind + ": LayoutVersion");
                Assert.IsTrue(info.IsSupported, kind + ": IsSupported");
                Assert.IsTrue(info.IsReady, kind + ": IsReady");
                Assert.IsFalse(info.ShuttingDown, kind + ": ShuttingDown");
                Assert.AreEqual(bufferSize, info.BufferSize, kind + ": BufferSize");
                Assert.AreEqual(nodeCount, info.NodeCount, kind + ": NodeCount");
                Assert.AreEqual(nodeSize, info.NodeBufferSize, kind + ": NodeBufferSize");

                Assert.AreEqual(Text(info), peer.Ok("info " + name), kind + ": the info another process reads");
            }
        }

        /// <summary>
        /// A buffer whose marker has no ready flag yet: IsReady false, and a ring's node count and size not read.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(Ring)]
        public void TryGetInfo_NotReady(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = CreateToday(kind, name))
            using (var raw = new RawHeader(name))
            {
                int marker = raw.Marker;
                raw.Marker = NotReady(kind);
                try
                {
                    SharedBufferInfo info;
                    Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info), kind);
                    Assert.IsFalse(info.IsReady, kind + ": IsReady");
                    Assert.AreEqual(kind == Ring ? SharedBufferKind.CircularBuffer : SharedBufferKind.BufferReadWrite, info.Kind, kind + ": Kind");
                    Assert.AreEqual(1, info.LayoutVersion, kind + ": LayoutVersion");
                    Assert.IsTrue(info.IsSupported, kind + ": IsSupported");
                    Assert.AreEqual(0, info.NodeCount, kind + ": NodeCount");
                    Assert.AreEqual(0, info.NodeBufferSize, kind + ": NodeBufferSize");
                }
                finally
                {
                    raw.Marker = marker;
                }
            }
        }

        /// <summary>
        /// A buffer whose owner closed while a reader holds it: ShuttingDown true, and still ready.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(Ring)]
        public void TryGetInfo_OwnerClosed(string kind)
        {
            string name = Guid.NewGuid().ToString();
            var owner = CreateToday(kind, name);
            using (var reader = OpenToday(kind, name))
            {
                owner.Dispose();
                SharedBufferInfo info;
                Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info), kind);
                Assert.IsTrue(info.ShuttingDown, kind + ": ShuttingDown");
                Assert.IsTrue(info.IsReady, kind + ": IsReady");
                Assert.AreEqual(reader.BufferSize, info.BufferSize, kind + ": BufferSize");
            }
        }

        /// <summary>
        /// A buffer without a layout marker, as SharedMemory 2.3.x creates it: kind Unknown, layout version 0, supported, ready
        /// once its size is written, its size, and no node count, which is read only for a kind the marker gives.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(Ring)]
        public void TryGetInfo_23xBuffer(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = CreateToday(kind, name))
            {
                using (var raw = new RawHeader(name))
                    raw.Marker = 0;
                SharedBufferInfo info;
                Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info), kind);
                Assert.AreEqual(SharedBufferKind.Unknown, info.Kind, kind + ": Kind");
                Assert.AreEqual(0, info.LayoutVersion, kind + ": LayoutVersion");
                Assert.IsTrue(info.IsSupported, kind + ": IsSupported");
                Assert.IsTrue(info.IsReady, kind + ": IsReady");
                Assert.AreEqual(owner.BufferSize, info.BufferSize, kind + ": BufferSize");
                Assert.AreEqual(0, info.NodeCount, kind + ": NodeCount");
                Assert.AreEqual(0, info.NodeBufferSize, kind + ": NodeBufferSize");
            }
        }

        /// <summary>
        /// A buffer without a layout marker whose size is not written yet, as a SharedMemory 2.3.x creator leaves it until it has
        /// made the buffer: kind Unknown, layout version 0, not ready, and size 0.
        /// </summary>
        [TestMethod]
        public void TryGetInfo_23xBufferBeforeItsSize()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = CreateToday(ReadWrite, name))
            using (var raw = new RawHeader(name))
            {
                int marker = raw.Marker;
                long size = raw.Size;
                raw.Marker = 0;
                raw.Size = 0;
                try
                {
                    SharedBufferInfo info;
                    Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info));
                    Assert.AreEqual(SharedBufferKind.Unknown, info.Kind, "Kind");
                    Assert.AreEqual(0, info.LayoutVersion, "LayoutVersion");
                    Assert.IsFalse(info.IsReady, "IsReady");
                    Assert.AreEqual(0L, info.BufferSize, "BufferSize");
                }
                finally
                {
                    raw.Size = size;
                    raw.Marker = marker;
                }
            }
        }

        /// <summary>
        /// A marker whose kind this version does not know, as a later version may write: kind Unknown, with its layout version 1
        /// telling it from a buffer without a marker, ready, and its size.
        /// </summary>
        [TestMethod]
        public void TryGetInfo_UnknownKind()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = CreateToday(ReadWrite, name))
            using (var raw = new RawHeader(name))
            {
                int marker = raw.Marker;
                // Layout version 1, kind 7, ready
                raw.Marker = 0x00001071;
                try
                {
                    SharedBufferInfo info;
                    Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info));
                    Assert.AreEqual(SharedBufferKind.Unknown, info.Kind, "Kind");
                    Assert.AreEqual(1, info.LayoutVersion, "LayoutVersion");
                    Assert.IsTrue(info.IsReady, "IsReady");
                    Assert.AreEqual(owner.BufferSize, info.BufferSize, "BufferSize");
                }
                finally
                {
                    raw.Marker = marker;
                }
            }
        }

        /// <summary>
        /// A marker from a later version, with layout version 2, or with lock mode 1: not supported, where an open would refuse it,
        /// and a ring's node header, whose layout this version cannot know, not read.
        /// </summary>
        [TestMethod]
        [DataRow("layout version 2", 0x00001012, 2)]
        [DataRow("lock mode 1", 0x00001111, 1)]
        [DataRow("a ring, layout version 2", 0x00001032, 2)]
        public void TryGetInfo_LaterVersion(string row, int marker, int version)
        {
            string name = Guid.NewGuid().ToString();
            bool ring = row.StartsWith("a ring", StringComparison.Ordinal);
            using (var owner = CreateToday(ring ? Ring : ReadWrite, name))
            using (var raw = new RawHeader(name))
            {
                int was = raw.Marker;
                raw.Marker = marker;
                try
                {
                    SharedBufferInfo info;
                    Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info), row);
                    Assert.IsFalse(info.IsSupported, row + ": IsSupported");
                    Assert.AreEqual(version, info.LayoutVersion, row + ": LayoutVersion");
                    Assert.AreEqual(ring ? SharedBufferKind.CircularBuffer : SharedBufferKind.BufferReadWrite, info.Kind, row + ": Kind");
                    Assert.AreEqual(0, info.NodeCount, row + ": NodeCount, not read from a layout this version cannot read");
                    Assert.AreEqual(0, info.NodeBufferSize, row + ": NodeBufferSize");
                    Assert.ThrowsExactly<InvalidOperationException>(() => new BufferReadWrite(name).Dispose(), row + ": an open refuses it");
                }
                finally
                {
                    raw.Marker = was;
                }
            }
        }

        /// <summary>
        /// TryGetInfo writes nothing: the header's bytes and a ring's node header are the same before and after, and nothing new
        /// is under the name, no event included.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(Ring)]
        [DataRow(EventFree)]
        public void TryGetInfo_ChangesNothing(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = kind == EventFree ? new EventFreeBuffer(name, Size) : CreateToday(kind, name))
            {
                int[] before = Words(name);
                string[] left = Left(name);
                SharedBufferInfo info;
                Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info), kind);
                CollectionAssert.AreEqual(before, Words(name), kind + ": the header bytes changed.");
                CollectionAssert.AreEquivalent(left, Left(name), kind + ": what is under the name changed.");
                if (kind == EventFree)
                    Assert.AreEqual(1, Left(name).Length, "an event-free buffer has its mapping alone under the name");
            }
        }

#if NET
        /// <summary>
        /// Linux: TryGetInfo makes no file under the name at any moment, an event's included, not even one it removes again before
        /// it returns: /dev/shm is watched while it runs.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [DataRow(ReadWrite)]
        [DataRow(Ring)]
        [DataRow(EventFree)]
        [SupportedOSPlatform("linux")]
        public void TryGetInfo_MakesNoFile(string kind)
        {
            string name = Guid.NewGuid().ToString();
            string sentinel = "/dev/shm/.sentinel-" + name;
            var made = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using (var owner = kind == EventFree ? new EventFreeBuffer(name, Size) : CreateToday(kind, name))
            using (var watcher = new FileSystemWatcher("/dev/shm", "*" + name + "*"))
            using (var seen = new System.Threading.ManualResetEventSlim())
            {
                watcher.NotifyFilter = NotifyFilters.FileName;
                watcher.Created += (sender, e) =>
                {
                    if (e.FullPath == sentinel)
                        seen.Set();
                    else
                        made.Enqueue(e.Name);
                };
                watcher.Renamed += (sender, e) => made.Enqueue(e.Name);
                watcher.EnableRaisingEvents = true;

                SharedBufferInfo info;
                Assert.IsTrue(SharedBuffer.TryGetInfo(name, out info), kind);

                // The watcher reports in order: once it has seen the sentinel, it has seen any file TryGetInfo made
                File.WriteAllText(sentinel, "");
                File.Delete(sentinel);
                Assert.IsTrue(seen.Wait(5000), "The watcher did not report the sentinel file.");
                CollectionAssert.AreEqual(new string[0], made.ToArray(), kind + ": files made under the name while TryGetInfo ran");
            }
        }

        /// <summary>
        /// Linux and macOS: a buffer whose holder was killed is removed, as an open removes it, and TryGetInfo returns false.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public void TryGetInfo_AbandonedFile()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create eventfree " + name + " " + Size);
                peer.Kill();
            }
            try
            {
                Assert.AreEqual(1, Left(name).Length, "the killed owner's file stays until the name is next used");
                SharedBufferInfo info;
                Assert.IsFalse(SharedBuffer.TryGetInfo(name, out info));
                Assert.IsNull(info);
                CollectionAssert.AreEquivalent(new string[0], Left(name), "the abandoned file is removed");
            }
            finally
            {
                // A TryGetInfo that failed leaves the file: an open removes it
                if (Left(name).Length != 0)
                {
                    try
                    {
                        new EventFreeBuffer(name).Dispose();
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }
            }
        }
#endif

        /// <summary>
        /// A null or empty name throws ArgumentException, as the constructors do.
        /// </summary>
        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void TryGetInfo_BadName_Throws(string name)
        {
            SharedBufferInfo info;
            Assert.ThrowsExactly<ArgumentException>(() => SharedBuffer.TryGetInfo(name, out info));
        }

        /// <summary>
        /// The info as the peer's info command prints it.
        /// </summary>
        static string Text(SharedBufferInfo info)
        {
            return "Kind=" + info.Kind + " LayoutVersion=" + info.LayoutVersion + " IsSupported=" + info.IsSupported + " IsReady=" + info.IsReady +
                " ShuttingDown=" + info.ShuttingDown + " BufferSize=" + info.BufferSize + " NodeCount=" + info.NodeCount + " NodeBufferSize=" + info.NodeBufferSize;
        }
    }
}
