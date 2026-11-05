#if HAS_RPC_BUFFER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The named objects an RpcBuffer finds its other end by, from 2.4.0. A process on another package version looks for the
    /// same names, so these strings are a wire format: changing one stops this version pairing with the versions before it.
    /// They are checked through the operating system, character for character, whatever the code calls the two ends: on
    /// Windows the election mutex, the mappings and the events; on Linux the files in /dev/shm; on macOS the lock files
    /// under /var/tmp, whose names this class works out itself. On Linux and macOS the election is a file of its own, named
    /// as the Windows mutex is.
    /// </summary>
    [TestClass]
    public class RpcBufferObjectNamesTests
    {
        const string ElectionMutex = "SharedMemory_OwnerMutex";
        const string OwnerReads = "_Owner_SharedMemory_MMF";
        const string OwnerWrites = "_Opener_SharedMemory_MMF";

        sealed class Probe : RpcBuffer
        {
            public Probe(string name) : base(name) { }
            public bool OwnsBuffers { get { return WriteBuffer.IsOwnerOfSharedMemory && ReadBuffer.IsOwnerOfSharedMemory; } }
            public string WriteName { get { return WriteBuffer.Name; } }
            public string ReadName { get { return ReadBuffer.Name; } }
        }

        [DllImport("libc")]
        static extern uint getuid();

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getuid")]
        static extern uint MacUserId();

        static bool IsLinux { get { return RuntimeInformation.IsOSPlatform(OSPlatform.Linux); } }

        static bool IsMacOS { get { return RuntimeInformation.IsOSPlatform(OSPlatform.OSX); } }

#if NET
        [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
#endif
        static bool IsWindows { get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); } }

        /// <summary>
        /// The first RpcBuffer on a channel, per-user or Global\, creates its two buffers, their events and the election object,
        /// each found through the operating system under the name an older version looks for: on Windows the mappings, events
        /// and mutex; on Linux the files, the election's among them; on macOS the lock files, the election's among them. A
        /// Global\ mapping on Windows needs a privilege a test run need not have, so that row runs on Linux and macOS.
        /// </summary>
        [TestMethod]
        [DataRow("")]
        [DataRow("Global\\")]
        public void FirstOnChannel_CreatesTheObjectsOlderVersionsOpen(string prefix)
        {
            if (IsWindows && prefix.Length > 0)
                Assert.Inconclusive("Linux and macOS only: a Global\\ mapping on Windows needs SeCreateGlobalPrivilege.");
            string name = "RpcObjectNames" + Guid.NewGuid().ToString("N");
            using (var owner = new Probe(prefix + name))
            {
                Assert.IsTrue(owner.OwnsBuffers, "The first RpcBuffer on a channel creates its buffers.");
                Assert.AreEqual(prefix + name + OwnerReads, owner.ReadName);
                Assert.AreEqual(prefix + name + OwnerWrites, owner.WriteName);
                // Looked for through the operating system, so that nothing here depends on the library's names.
                foreach (string buffer in new[] { name + OwnerReads, name + OwnerWrites })
                {
                    foreach (string suffix in new[] { "", "_evt_dataexists", "_evt_nodeavail" })
                    {
                        if (IsWindows && suffix.Length == 0)
                            using (MemoryMappedFile.OpenExisting(buffer)) { }
                        else if (IsWindows)
                            using (EventWaitHandle.OpenExisting(buffer + suffix)) { }
                        else
                            Assert.IsTrue(File.Exists(UnixPath(prefix, buffer + suffix)), UnixPath(prefix, buffer + suffix));
                    }
                }
                if (IsWindows)
                    using (Mutex.OpenExisting(name + ElectionMutex)) { }
                else
                    Assert.IsTrue(File.Exists(UnixPath(prefix, name + ElectionMutex)), "The election file " + UnixPath(prefix, name + ElectionMutex));
            }
        }

        /// <summary>
        /// Stands in for an RpcBuffer of an older version that got there first: it holds the election object, made as that
        /// version makes it, and created both buffers. The next RpcBuffer on the channel opens them. On Linux and macOS the
        /// stand-in makes the election file with its own calls (<see cref="HeldElectionFile"/>) once the buffers exist.
        /// </summary>
        [TestMethod]
        public void SecondOnChannel_OpensTheObjectsAnOlderVersionCreated()
        {
            string name = "RpcObjectNames" + Guid.NewGuid().ToString("N");
            if (IsWindows)
            {
                bool createdNew;
                using (var election = new Mutex(true, name + ElectionMutex, out createdNew))
                {
                    Assert.IsTrue(createdNew);
                    try
                    {
                        using (var toOpener = new CircularBuffer(name + OwnerWrites, 10, 4096))
                        using (var toOwner = new CircularBuffer(name + OwnerReads, 10, 4096))
                            OpensTheBuffers(name);
                    }
                    finally
                    {
                        election.ReleaseMutex();
                    }
                }
            }
#if NET
            else
            {
                // The buffers first: on macOS their create makes the lock folder the election file goes in.
                using (var toOpener = new CircularBuffer(name + OwnerWrites, 10, 4096))
                using (var toOwner = new CircularBuffer(name + OwnerReads, 10, 4096))
                using (new HeldElectionFile(UnixPath("", name + ElectionMutex)))
                    OpensTheBuffers(name);
            }
#endif
        }

        /// <summary>
        /// The election file lives as long as the channel's owner, as the election mutex does on Windows, where an opener closes
        /// its handle at once: it is there once the owner is made, still the one file once an opener is made too, and gone once
        /// the owner is disposed, while the opener is still open.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void ElectionFile_HeldByTheOwnerAlone()
        {
            string name = "RpcObjectNames" + Guid.NewGuid().ToString("N");
            string election = UnixPath("", name + ElectionMutex);
            var owner = new Probe(name);
            try
            {
                CollectionAssert.AreEqual(new[] { election }, ElectionFiles(name), "While the owner lives: its election file.");
                using (var opener = new Probe(name))
                {
                    Assert.IsTrue(owner.OwnsBuffers, "The first RpcBuffer on a channel is its owner.");
                    Assert.IsFalse(opener.OwnsBuffers, "The second RpcBuffer on a channel is its opener.");
                    CollectionAssert.AreEqual(new[] { election }, ElectionFiles(name), "With an opener too: the owner's election file, and no other.");

                    owner.Dispose();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (!owner.DisposeFinished && watch.ElapsedMilliseconds < 5000)
                        Thread.Sleep(10);
                    Assert.IsTrue(owner.DisposeFinished, "The owner had not finished disposing after 5 s.");
                    CollectionAssert.AreEqual(new string[0], ElectionFiles(name), "Once the owner is disposed, with the opener still open.");
                }
            }
            finally
            {
                owner.Dispose();
            }
        }

        static void OpensTheBuffers(string name)
        {
            using (var opener = new Probe(name))
            {
                Assert.IsFalse(opener.OwnsBuffers, "A second RpcBuffer on a channel opens the buffers the first created.");
                Assert.AreEqual(name + OwnerReads, opener.WriteName);
                Assert.AreEqual(name + OwnerWrites, opener.ReadName);
            }
        }

        /// <summary>
        /// Where an object of an RpcBuffer named <paramref name="prefix"/> + <paramref name="objectName"/> is on Linux (its file
        /// in /dev/shm) or macOS (its lock file), for an object name that needs no escaping and is under 200 bytes, as these are.
        /// </summary>
        static string UnixPath(string prefix, string objectName)
        {
            bool global = prefix == "Global\\";
            if (IsMacOS)
                return "/var/tmp/.SharedMemory-" + (global ? "global" : "uid" + MacUserId()) + "/" + objectName + "~" + Hash(objectName);
            return "/dev/shm/SharedMemory." + (global ? "global." : "u" + getuid() + ".") + objectName;
        }

        /// <summary>
        /// The per-user election files of the channel <paramref name="name"/>: on Linux in /dev/shm, on macOS in the lock folder.
        /// </summary>
        static string[] ElectionFiles(string name)
        {
            string election = UnixPath("", name + ElectionMutex);
            string folder = Path.GetDirectoryName(election);
            string start = Path.GetFileName(IsMacOS ? election.Substring(0, election.LastIndexOf('~')) : election);
            return Directory.GetFiles(folder, start + "*").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// The end of a macOS lock file's name: the first 26 characters of the RFC 4648 base32 encoding of the SHA-256 of the
        /// name's UTF-8 bytes.
        /// </summary>
        static string Hash(string name)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(name));
            return Base32(hash, 26);
        }

        static string Base32(byte[] data, int characters)
        {
            const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var text = new StringBuilder();
            for (int bit = 0; text.Length < characters; bit += 5)
            {
                int index = bit / 8;
                int window = data[index] << 8 | (index + 1 < data.Length ? data[index + 1] : 0);
                text.Append(Alphabet[window >> (11 - bit % 8) & 31]);
            }
            return text.ToString();
        }

#if NET
        /// <summary>
        /// An election file made with this class's own calls, as an RpcBuffer of any version that owns the channel makes it: on
        /// Linux a file of 0 bytes, 0600; on macOS a lock file, 0600, of format 1 (the magic 0x4B4C4D53, the version, the object
        /// name's length, each a little-endian uint32, then the name), naming an object of 1 byte it creates. It holds the file
        /// with a shared flock, as every holder does, and removes what it made when disposed.
        /// </summary>
        sealed class HeldElectionFile : IDisposable
        {
            readonly string path;
            readonly string objectName;
            readonly int fd;

            public HeldElectionFile(string path)
            {
                this.path = path;
                if (IsMacOS)
                {
                    objectName = "/SM" + Base32(Guid.NewGuid().ToByteArray(), 26);
                    int memory = Native.ShmOpen(objectName, Native.O_RDWR | Native.O_CREAT | Native.O_EXCL, 0x180);
                    Assert.IsTrue(memory >= 0, "shm_open " + objectName + ": " + Marshal.GetLastPInvokeError());
                    try
                    {
                        Assert.AreEqual(0, Native.Mac.ftruncate(memory, 1), "ftruncate " + objectName + ": " + Marshal.GetLastPInvokeError());
                    }
                    finally
                    {
                        Native.Close(memory);
                    }
                }
                fd = Native.Open(path, Native.O_RDWR | Native.O_CREAT | Native.O_EXCL | Native.O_CLOEXEC, 0x180);
                Assert.IsTrue(fd >= 0, "open " + path + ": " + Marshal.GetLastPInvokeError());
                if (objectName != null)
                {
                    var contents = new byte[12 + objectName.Length];
                    BitConverter.GetBytes(0x4B4C4D53u).CopyTo(contents, 0);
                    BitConverter.GetBytes(1u).CopyTo(contents, 4);
                    BitConverter.GetBytes((uint)objectName.Length).CopyTo(contents, 8);
                    Encoding.ASCII.GetBytes(objectName).CopyTo(contents, 12);
                    RandomAccess.Write(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, false), contents, 0);
                }
                Assert.AreEqual(0, Native.Flock(fd, Native.LOCK_SH), "flock " + path + ": " + Marshal.GetLastPInvokeError());
            }

            public void Dispose()
            {
                if (objectName != null)
                    Native.Mac.shm_unlink(objectName);
                Native.Unlink(path);
                Native.Close(fd);
            }
        }

        /// <summary>
        /// The calls <see cref="HeldElectionFile"/> makes, with each OS's values.
        /// </summary>
        static class Native
        {
            public const int O_RDWR = 2;
            public const int LOCK_SH = 1;
            public static readonly int O_CREAT = IsMacOS ? 0x200 : 0x40;
            public static readonly int O_EXCL = IsMacOS ? 0x800 : 0x80;
            public static readonly int O_CLOEXEC = IsMacOS ? 0x1000000 : 0x80000;

            static bool Arm64 { get { return RuntimeInformation.ProcessArchitecture == Architecture.Arm64; } }

            // open and shm_open are variadic. On Apple arm64 a variadic argument goes on the stack, so six padding arguments fill
            // the remaining argument registers and put the mode where the callee looks for it.
            public static int Open(string path, int flags, int mode)
            {
                if (!IsMacOS)
                    return Linux.open(path, flags, mode);
                return Arm64 ? Mac.open_arm64(path, flags, 0, 0, 0, 0, 0, 0, mode) : Mac.open(path, flags, mode);
            }

            public static int ShmOpen(string name, int flags, int mode)
            {
                return Arm64 ? Mac.shm_open_arm64(name, flags, 0, 0, 0, 0, 0, 0, mode) : Mac.shm_open(name, flags, mode);
            }

            public static int Flock(int fd, int operation) { return IsMacOS ? Mac.flock(fd, operation) : Linux.flock(fd, operation); }
            public static int Close(int fd) { return IsMacOS ? Mac.close(fd) : Linux.close(fd); }
            public static int Unlink(string path) { return IsMacOS ? Mac.unlink(path) : Linux.unlink(path); }

            public static class Linux
            {
                const string Library = "libc";
                [DllImport(Library, SetLastError = true)] public static extern int open(string path, int flags, int mode);
                [DllImport(Library, SetLastError = true)] public static extern int flock(int fd, int operation);
                [DllImport(Library, SetLastError = true)] public static extern int close(int fd);
                [DllImport(Library, SetLastError = true)] public static extern int unlink(string path);
            }

            public static class Mac
            {
                const string Library = "/usr/lib/libSystem.B.dylib";
                [DllImport(Library, EntryPoint = "open", SetLastError = true)] public static extern int open_arm64(string path, int flags, long x2, long x3, long x4, long x5, long x6, long x7, long mode);
                [DllImport(Library, SetLastError = true)] public static extern int open(string path, int flags, int mode);
                [DllImport(Library, EntryPoint = "shm_open", SetLastError = true)] public static extern int shm_open_arm64(string name, int flags, long x2, long x3, long x4, long x5, long x6, long x7, long mode);
                [DllImport(Library, SetLastError = true)] public static extern int shm_open(string name, int flags, int mode);
                [DllImport(Library, SetLastError = true)] public static extern int ftruncate(int fd, long length);
                [DllImport(Library, SetLastError = true)] public static extern int shm_unlink(string name);
                [DllImport(Library, SetLastError = true)] public static extern int flock(int fd, int operation);
                [DllImport(Library, SetLastError = true)] public static extern int close(int fd);
                [DllImport(Library, SetLastError = true)] public static extern int unlink(string path);
            }
        }
#endif
    }
}
#endif
