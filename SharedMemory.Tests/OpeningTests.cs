using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;
#if NET
using System.Linq;
using System.Runtime.Versioning;
#endif

namespace SharedMemoryTests
{
    /// <summary>
    /// How each class creates and opens a buffer today: a create of a name in use, who is owner, what an opener writes to the
    /// shared header, a failed open of an abandoned buffer on Linux and macOS, and when a subclass's DoOpen runs. The tests run
    /// for each library class and, but for the DoOpen and abandoned-buffer tests, for a subclass of SharedBuffer with no events
    /// (EventFreeBuffer). They pin what later ways of opening must keep for today's constructors.
    /// </summary>
    [TestClass]
    public class OpeningTests
    {
        const int Size = 1024;
        const int NodeCount = 4;
        const int NodeSize = 64;

        /// <summary>
        /// The bytes compared before and after: the 16-byte shared header and what follows it, a ring's node header and first node
        /// included. <see cref="Seed"/> puts values there that creating the buffer does not write.
        /// </summary>
        const int Snapshot = 64;

        const string ReadWrite = "BufferReadWrite";
        const string IntArray = "SharedArray<int>";
        const string Ring = "CircularBuffer";
        const string EventFree = "EventFreeBuffer";

        /// <summary>
        /// A create of a name that a buffer of the same class already has throws IOException, and leaves that buffer's header and
        /// the bytes after it as they were. The failed create asks for other sizes, so that a create that rewrote the header would
        /// change it.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        [DataRow(EventFree)]
        public void Create_NameInUse_ThrowsIOExceptionAndLeavesTheBuffer(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var creator = Create(kind, name))
            {
                Seed(creator);
                int[] before = Words(name);

                Assert.ThrowsExactly<IOException>(() => Create(kind, name, true));

                CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes after the failed create");
            }
        }

        /// <summary>
        /// The failed create's exception is exactly IOException on every build, and NamedMapping.OpenExisting of the same name then
        /// opens the buffer that holds it. The output records the exception's HResult, read with Marshal.GetHRForException since
        /// HResult's getter is protected before .NET Framework 4.5, and where it was thrown, which differs by build.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        [DataRow(EventFree)]
        public void Create_NameInUse_IsAnIOExceptionThatOpenExistingResolves(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var creator = Create(kind, name))
            {
                var e = Assert.ThrowsExactly<IOException>(() => Create(kind, name));
                Console.WriteLine("{0}: {1}, HResult 0x{2:X8}, thrown in {3}: {4}", kind, e.GetType().FullName, Marshal.GetHRForException(e),
                    e.TargetSite == null ? "(unknown)" : e.TargetSite.DeclaringType.FullName + "." + e.TargetSite.Name, e.Message);

                // Throws if it cannot open the name
                NamedMapping mapping = NamedMapping.OpenExisting(name);
                if (mapping.File != null)
                    mapping.File.Dispose();
                mapping.Dispose();

                using (var raw = new RawHeader(name))
                    Assert.AreEqual(creator.SharedMemorySize, raw.Size, kind + ": the size in the header OpenExisting found");
            }
        }

        /// <summary>
        /// IsOwnerOfSharedMemory is true for the instance that created the buffer and false for one that opened it, and stays so
        /// once the instance has closed.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        [DataRow(EventFree)]
        public void IsOwner_CreatorTrue_OpenerFalse(string kind)
        {
            string name = Guid.NewGuid().ToString();
            SharedBuffer creator = Create(kind, name);
            SharedBuffer opener;
            using (creator)
            using (opener = Open(kind, name))
            {
                Assert.IsTrue(creator.IsOwnerOfSharedMemory, kind + ": the creator");
                Assert.IsFalse(opener.IsOwnerOfSharedMemory, kind + ": the opener");
            }
            Assert.IsTrue(creator.IsOwnerOfSharedMemory, kind + ": the creator, closed");
            Assert.IsFalse(opener.IsOwnerOfSharedMemory, kind + ": the opener, closed");
        }

        /// <summary>
        /// An opener writes nothing shared: the header and the bytes after it, a ring's node header included, are the same after
        /// its open and after its close as before it opened.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        [DataRow(EventFree)]
        public void Opener_OpenAndClose_LeaveTheHeaderBytes(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var creator = Create(kind, name))
            {
                Seed(creator);
                int[] before = Words(name);

                using (Open(kind, name))
                    CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes once the opener is open");

                CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes once the opener has closed");
            }
        }

        /// <summary>
        /// An opener of a buffer whose owner has closed, while another opener holds it, opens it, sees ShuttingDown, and writes
        /// nothing shared either: Shutdown stays 1 through its open and its close.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        [DataRow(EventFree)]
        public void Opener_OfABufferWhoseOwnerClosed_LeavesTheHeaderBytes(string kind)
        {
            string name = Guid.NewGuid().ToString();
            SharedBuffer creator = Create(kind, name);
            using (creator)
            using (Open(kind, name))
            {
                Seed(creator);
                creator.Dispose();
                int[] before = Words(name);
                Assert.AreEqual(1, before[2], kind + ": Shutdown, bytes 8 to 11, once the owner has closed");

                using (var opener = Open(kind, name))
                {
                    Assert.IsTrue(opener.ShuttingDown, kind + ": the opener's ShuttingDown");
                    CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes once the opener is open");
                }

                CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes once the opener has closed");
            }
        }

        /// <summary>
        /// A subclass's DoOpen runs once in each instance, with IsOwnerOfSharedMemory true in the instance that creates the buffer
        /// and false in one that opens it: a subclass that sets up shared state in DoOpen when it is owner does so only in a new
        /// buffer.
        /// </summary>
        [TestMethod]
        [DataRow("SharedBuffer")]
        [DataRow("BufferWithLocks")]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void DoOpen_SeesOwnerOnlyInTheCreator(string baseClass)
        {
            string name = Guid.NewGuid().ToString();
            using (var creator = Counted.Create(baseClass, name, true))
            using (var opener = Counted.Create(baseClass, name, false))
            {
                Assert.AreEqual(1, creator.DoOpenCalls, baseClass + ": DoOpen calls in the creator");
                Assert.IsTrue(creator.OwnerInDoOpen, baseClass + ": IsOwnerOfSharedMemory in the creator's DoOpen");
                Assert.AreEqual(1, opener.DoOpenCalls, baseClass + ": DoOpen calls in the opener");
                Assert.IsFalse(opener.OwnerInDoOpen, baseClass + ": IsOwnerOfSharedMemory in the opener's DoOpen");
            }
        }

#if NET
        /// <summary>
        /// On Linux and macOS, an open that finds a buffer whose holder was killed removes its file (on macOS, its lock file and
        /// object) and throws FileNotFoundException, whichever class opens it, and leaves no file with the name: the events a
        /// BufferReadWrite or SharedArray opener makes before it opens are gone too. A peer with no events leaves the abandoned
        /// buffer, so that nothing but the buffer is left for the open to find. UnixBufferTests.OwnerKilled_OpenThrowsFileNotFoundAndRemovesFile
        /// covers the event-free opener.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public void Open_OfAnAbandonedBuffer_RemovesItAndThrowsFileNotFoundException(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create eventfree " + name + " " + Size);
                peer.Kill();
            }
            string path = PerUserPath(name);
            Assert.IsTrue(File.Exists(path), "The killed owner's file stays until the name is next used.");
            string objectName = OperatingSystem.IsMacOS() ? UnixBufferTests.Files.ObjectNamedBy(path) : null;
            if (objectName != null)
                Assert.IsTrue(UnixBufferTests.Files.ObjectExists(objectName), "The killed owner's object stays with its lock file.");

            Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name).Dispose());

            Assert.IsFalse(File.Exists(path), kind + ": the abandoned file after the open");
            if (objectName != null)
                Assert.IsFalse(UnixBufferTests.Files.ObjectExists(objectName), kind + ": the abandoned object " + objectName);
            CollectionAssert.AreEqual(new string[0], FilesNamed(name), kind + ": files with the name after the open");
        }

        /// <summary>
        /// The buffer's file on Linux, or its lock file on macOS, for a name without a scope.
        /// </summary>
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        static string PerUserPath(string name)
        {
            return Path.Combine(PerUserFolder(), OperatingSystem.IsMacOS() ? UnixBufferTests.Files.LockFileName(name) : "SharedMemory.u" + LinuxUserId() + "." + name);
        }

        /// <summary>
        /// The files for a name without a scope, and for the names made from it, such as its events': on Linux the files in
        /// /dev/shm, on macOS the lock files.
        /// </summary>
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        static string[] FilesNamed(string name)
        {
            string prefix = OperatingSystem.IsMacOS() ? name : "SharedMemory.u" + LinuxUserId() + "." + name;
            return Directory.GetFiles(PerUserFolder()).Select(Path.GetFileName).Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        }

        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        static string PerUserFolder()
        {
            return OperatingSystem.IsMacOS() ? UnixBufferTests.Files.MacFolder + ".SharedMemory-uid" + UnixBufferTests.Files.EffectiveUserId() : "/dev/shm";
        }

        static string LinuxUserId()
        {
            return File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal))
                .Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)[2];
        }
#endif

        /// <summary>
        /// A creator of <paramref name="kind"/>; with <paramref name="otherSizes"/>, twice the size, or for a ring twice the nodes
        /// at half the size.
        /// </summary>
        static SharedBuffer Create(string kind, string name, bool otherSizes = false)
        {
            int scale = otherSizes ? 2 : 1;
            switch (kind)
            {
                case ReadWrite:
                    return new BufferReadWrite(name, Size * scale);
                case IntArray:
                    return new SharedArray<int>(name, Size * scale / sizeof(int));
                case Ring:
                    return new CircularBuffer(name, NodeCount * scale, NodeSize / scale);
                case EventFree:
                    return new EventFreeBuffer(name, Size * scale);
                default:
                    throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        /// <summary>
        /// Puts values in the snapshot that creating the buffer does not write: a pattern in its first bytes, or for a ring, two
        /// nodes written and one read, which moves its node header's cursors and fills its first node.
        /// </summary>
        static void Seed(SharedBuffer buffer)
        {
            var pattern = new int[(Snapshot - 16) / sizeof(int)];
            for (int i = 0; i < pattern.Length; i++)
                pattern[i] = 0x5A000001 + i * 0x00010101;

            var ring = buffer as CircularBuffer;
            if (ring != null)
            {
                Assert.AreEqual(8, ring.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
                Assert.AreEqual(8, ring.Write(new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 }));
                Assert.AreEqual(8, ring.Read(new byte[NodeSize]));
            }
            else if (buffer is BufferReadWrite)
                ((BufferReadWrite)buffer).Write(pattern);
            else if (buffer is SharedArray<int>)
                ((SharedArray<int>)buffer).Write(pattern);
            else
            {
                var bytes = new byte[pattern.Length * sizeof(int)];
                Buffer.BlockCopy(pattern, 0, bytes, 0, bytes.Length);
                ((EventFreeBuffer)buffer).WriteBytes(bytes);
            }
        }

        static SharedBuffer Open(string kind, string name)
        {
            switch (kind)
            {
                case ReadWrite:
                    return new BufferReadWrite(name);
                case IntArray:
                    return new SharedArray<int>(name);
                case Ring:
                    return new CircularBuffer(name);
                case EventFree:
                    return new EventFreeBuffer(name);
                default:
                    throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        /// <summary>
        /// The first <see cref="Snapshot"/> bytes of the buffer, as 32-bit words, read through a mapping of its own.
        /// </summary>
        static int[] Words(string name)
        {
            var words = new int[Snapshot / sizeof(int)];
            using (var raw = new RawHeader(name, Snapshot))
            {
                for (int i = 0; i < words.Length; i++)
                    words[i] = raw.ReadInt32(i * sizeof(int));
            }
            return words;
        }

        /// <summary>
        /// A subclass of a library class that counts its DoOpen calls and records IsOwnerOfSharedMemory in the first.
        /// </summary>
        interface ICounted : IDisposable
        {
            int DoOpenCalls { get; }
            bool OwnerInDoOpen { get; }
        }

        static class Counted
        {
            public static ICounted Create(string baseClass, string name, bool create)
            {
                switch (baseClass)
                {
                    case "SharedBuffer":
                        return new CountedBuffer(name, create);
                    case "BufferWithLocks":
                        return new CountedLocks(name, create);
                    case ReadWrite:
                        return create ? new CountedReadWrite(name, Size) : new CountedReadWrite(name);
                    case IntArray:
                        return create ? new CountedArray(name, Size / sizeof(int)) : new CountedArray(name);
                    case Ring:
                        return create ? new CountedRing(name, NodeCount, NodeSize) : new CountedRing(name);
                    default:
                        throw new ArgumentException("Unknown base class: " + baseClass);
                }
            }

            // DoOpen runs inside the base class's constructor for the last three, before a subclass constructor's body, so the
            // counts are fields with no initialiser and no constructor sets them

            sealed class CountedBuffer : SharedBuffer, ICounted
            {
                public CountedBuffer(string name, bool create)
                    : base(name, create ? Size : 0, create)
                {
                    Open();
                }

                public int DoOpenCalls { get; private set; }
                public bool OwnerInDoOpen { get; private set; }

                protected override bool DoOpen()
                {
                    if (DoOpenCalls++ == 0)
                        OwnerInDoOpen = IsOwnerOfSharedMemory;
                    return base.DoOpen();
                }
            }

            sealed class CountedLocks : BufferWithLocks, ICounted
            {
                public CountedLocks(string name, bool create)
                    : base(name, create ? Size : 0, create)
                {
                    Open();
                }

                public int DoOpenCalls { get; private set; }
                public bool OwnerInDoOpen { get; private set; }

                protected override bool DoOpen()
                {
                    if (DoOpenCalls++ == 0)
                        OwnerInDoOpen = IsOwnerOfSharedMemory;
                    return base.DoOpen();
                }
            }

            sealed class CountedReadWrite : BufferReadWrite, ICounted
            {
                public CountedReadWrite(string name, int bufferSize)
                    : base(name, bufferSize)
                {
                }

                public CountedReadWrite(string name)
                    : base(name)
                {
                }

                public int DoOpenCalls { get; private set; }
                public bool OwnerInDoOpen { get; private set; }

                protected override bool DoOpen()
                {
                    if (DoOpenCalls++ == 0)
                        OwnerInDoOpen = IsOwnerOfSharedMemory;
                    return base.DoOpen();
                }
            }

            sealed class CountedArray : SharedArray<int>, ICounted
            {
                public CountedArray(string name, int length)
                    : base(name, length)
                {
                }

                public CountedArray(string name)
                    : base(name)
                {
                }

                public int DoOpenCalls { get; private set; }
                public bool OwnerInDoOpen { get; private set; }

                protected override bool DoOpen()
                {
                    if (DoOpenCalls++ == 0)
                        OwnerInDoOpen = IsOwnerOfSharedMemory;
                    return base.DoOpen();
                }
            }

            sealed class CountedRing : CircularBuffer, ICounted
            {
                public CountedRing(string name, int nodeCount, int nodeBufferSize)
                    : base(name, nodeCount, nodeBufferSize)
                {
                }

                public CountedRing(string name)
                    : base(name)
                {
                }

                public int DoOpenCalls { get; private set; }
                public bool OwnerInDoOpen { get; private set; }

                protected override bool DoOpen()
                {
                    if (DoOpenCalls++ == 0)
                        OwnerInDoOpen = IsOwnerOfSharedMemory;
                    return base.DoOpen();
                }
            }
        }
    }
}
