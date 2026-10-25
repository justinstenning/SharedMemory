#if NET
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// Buffers on Linux and macOS: where their files are and who may use them, a /dev/shm without room, a buffer nobody
    /// disposed, processes in other sessions, and many processes creating, opening, closing, and dying at once. On Linux a
    /// buffer is a file in /dev/shm; on macOS a lock file under /var/tmp names the shm_open object that holds the buffer
    /// (<see cref="Files"/>). The tests that only one OS has are marked with it.
    /// </summary>
    [TestClass]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public class UnixBufferTests
    {
        const string Folder = "/dev/shm/";
        const int Size = 1024;
        const int HeaderSize = 16;

        static readonly byte[] Data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        static readonly byte[] Reply = Enumerable.Range(101, 16).Select(i => (byte)i).ToArray();

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow("")]
        [DataRow("Global\\")]
        public void Create_FilesPrivateToUser_RemovedOnClose(string scope)
        {
            string id = Guid.NewGuid().ToString();
            string path = scope == "" ? PerUserPath(id) : GlobalPath(id);
            string objectName = null;

            using (new EventFreeBuffer(scope + id, Size))
            {
                Assert.IsTrue(File.Exists(path), path);
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                if (OperatingSystem.IsMacOS())
                {
                    // The object is a whole number of pages, and its mode can only be read back through fstat.
                    objectName = Files.ObjectNamedBy(path);
                    Files.ObjectStatus status = Files.StatusOf(objectName);
                    Assert.AreEqual(0x180, status.Mode & 0xFFF, "mode " + Convert.ToString(status.Mode, 8));
                    Assert.AreEqual(Files.EffectiveUserId(), status.Uid);
                    Assert.IsTrue(status.Size >= Size + HeaderSize && status.Size % Environment.SystemPageSize == 0, "size " + status.Size);
                }
                else
                    Assert.AreEqual(Size + HeaderSize, new FileInfo(path).Length);
            }

            Assert.IsFalse(File.Exists(path));
            if (objectName != null)
                Assert.IsFalse(Files.ObjectExists(objectName), objectName);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite)]
        [DataRow(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite)]
        public void PerUserFileOthersCanWrite_RefusedAndLeftAlone(UnixFileMode mode)
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            Files.MakeFolder(name);
            byte[] planted = Enumerable.Repeat((byte)0x2A, Size + HeaderSize).ToArray();
            File.WriteAllBytes(path, planted);
            File.SetUnixFileMode(path, mode);
            try
            {
                var open = Assert.ThrowsExactly<UnauthorizedAccessException>(() => new EventFreeBuffer(name));
                StringAssert.Contains(open.Message, "group or others can write to it");
                var create = Assert.ThrowsExactly<UnauthorizedAccessException>(() => new EventFreeBuffer(name, Size));
                StringAssert.Contains(create.Message, "group or others can write to it");

                CollectionAssert.AreEqual(planted, File.ReadAllBytes(path));
                Assert.AreEqual(mode, File.GetUnixFileMode(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void GlobalFileOthersCanWrite_NotChecked_RemovedAsAbandoned()
        {
            string name = Guid.NewGuid().ToString();
            string path = GlobalPath(name);
            Files.MakeFolder("Global\\" + name);
            File.WriteAllBytes(path, OperatingSystem.IsMacOS() ? Files.LockFile(Files.NewObjectName()) : new byte[Size + HeaderSize]);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
            try
            {
                Assert.ThrowsExactly<FileNotFoundException>(() => new EventFreeBuffer("Global\\" + name));

                Assert.IsFalse(File.Exists(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void Create_LargerThanDevShm_ThrowsIOExceptionAtOnceAndLeavesNoFile()
        {
            string name = Guid.NewGuid().ToString();
            long size = new DriveInfo("/dev/shm").TotalSize + 4096;
            string[] before = SharedMemoryFiles();

            var watch = Stopwatch.StartNew();
            var e = Assert.ThrowsExactly<IOException>(() => new EventFreeBuffer(name, size));
            watch.Stop();

            StringAssert.Contains(e.Message, name);
            CollectionAssert.AreEquivalent(before, SharedMemoryFiles());
            Assert.IsTrue(watch.ElapsedMilliseconds < 5000, "took " + watch.ElapsedMilliseconds + " ms");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void NeverDisposed_FileRemovedWhenCollected()
        {
            string name = Guid.NewGuid().ToString();

            string objectName = CreateAndDrop(name);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.IsFalse(File.Exists(PerUserPath(name)));
            if (objectName != null)
                Assert.IsFalse(Files.ObjectExists(objectName), objectName);
        }

        /// <returns>On macOS, the name of the buffer's object; elsewhere null.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string CreateAndDrop(string name)
        {
            new EventFreeBuffer(name, Size);
            Assert.IsTrue(File.Exists(PerUserPath(name)));
            return OperatingSystem.IsMacOS() ? Files.ObjectNamedBy(PerUserPath(name)) : null;
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void OwnerKilled_OpenThrowsFileNotFoundAndRemovesFile()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create eventfree " + name + " " + Size);
                peer.Kill();
            }
            Assert.IsTrue(File.Exists(PerUserPath(name)), "The killed owner's file stays until the name is next used.");
            string objectName = OperatingSystem.IsMacOS() ? Files.ObjectNamedBy(PerUserPath(name)) : null;
            if (objectName != null)
                Assert.IsTrue(Files.ObjectExists(objectName), "The killed owner's object stays with its lock file.");

            Assert.ThrowsExactly<FileNotFoundException>(() => new EventFreeBuffer(name));

            Assert.IsFalse(File.Exists(PerUserPath(name)));
            if (objectName != null)
                Assert.IsFalse(Files.ObjectExists(objectName), objectName);
        }

        /// <summary>
        /// A killed process's lock must be gone by the time it has been reaped. When the library mapped the file through
        /// the descriptor it locked, the lock outlived a killed .NET process by milliseconds, and a create straight after
        /// found the name in use.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void OwnerKilled_CreateStraightAfter_Succeeds()
        {
            for (int i = 0; i < 20; i++)
            {
                string name = Guid.NewGuid().ToString();
                using (var peer = PeerProcess.Start())
                {
                    peer.Ok("create eventfree " + name + " " + Size);
                    peer.Ok("write " + PeerProtocol.ToHex(Data));
                    peer.Kill();
                }

                using (var owner = new EventFreeBuffer(name, Size))
                    CollectionAssert.AreEqual(new byte[Data.Length], owner.ReadBytes(Data.Length), "attempt " + i);
            }
        }

        /// <summary>
        /// An open that finds a file nobody holds, while another process holds its exclusive lock to remove it, waits for a
        /// shared lock, and must then check that the name still leads to that file: here the remover replaces it with a new
        /// buffer before letting go, and the open must reach the new one. The stress test meets this race now and then; this
        /// makes it every time.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void Open_FileReplacedWhileItWaitsForItsLock_OpensTheNewFile()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create eventfree " + name + " " + Size);
                peer.Ok("write " + PeerProtocol.ToHex(Data));
                peer.Kill();
            }
            string path = PerUserPath(name);
            string oldObject = OperatingSystem.IsMacOS() ? Files.ObjectNamedBy(path) : null;
            EventFreeBuffer replacement = null;
            try
            {
                Task<byte[]> opened;
                // FileShare.None takes the exclusive flock a process removing the abandoned file would hold.
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    opened = Task.Run(() =>
                    {
                        using (var opener = new EventFreeBuffer(name))
                            return opener.ReadBytes(Reply.Length);
                    });
                    Thread.Sleep(500);
                    Assert.IsFalse(opened.IsCompleted, "The open waits for the remover's lock.");

                    File.Delete(path);
                    replacement = new EventFreeBuffer(name, Size);
                    replacement.WriteBytes(Reply);
                }

                CollectionAssert.AreEqual(Reply, opened.Result, "The open reached the replaced file.");
            }
            finally
            {
                if (replacement != null)
                    replacement.Dispose();
                // The remover took the old lock file away by hand, so its object is the test's to remove.
                if (oldObject != null && Files.ObjectExists(oldObject))
                    Files.RemoveObject(oldObject);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void PerUserAndGlobalNames_AreSeparate()
        {
            string name = Guid.NewGuid().ToString();
            using (var perUser = new EventFreeBuffer(name, Size))
            using (var global = new EventFreeBuffer("Global\\" + name, Size))
            using (var local = new EventFreeBuffer("Local\\" + name))
            {
                perUser.WriteBytes(Data);
                global.WriteBytes(Reply);

                CollectionAssert.AreEqual(Data, local.ReadBytes(Data.Length));
                CollectionAssert.AreEqual(Reply, global.ReadBytes(Reply.Length));
            }
        }

        /// <summary>
        /// Every terminal is its own POSIX session. A process in another session, started with setsid, opens a buffer this
        /// process created, by a per-user name, a <c>Local\</c> one, or a <c>Global\</c> one.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow("", "")]
        [DataRow("", "Local\\")]
        [DataRow("Local\\", "")]
        [DataRow("Global\\", "Global\\")]
        public void OtherSession_OpensBufferThisProcessCreated(string createScope, string openScope)
        {
            string id = Guid.NewGuid().ToString();
            using (var buffer = new EventFreeBuffer(createScope + id, Size))
            using (var peer = SessionPeer.Start())
            {
                int session = SessionOf(Environment.ProcessId);
                Assert.AreEqual(peer.Id, SessionOf(peer.Id), "The peer leads a session of its own.");
                Assert.AreNotEqual(session, SessionOf(peer.Id));

                buffer.WriteBytes(Data);
                Assert.AreEqual("ok", peer.Send("open eventfree " + openScope + id));
                Assert.AreEqual("ok " + PeerProtocol.ToHex(Data), peer.Send("read " + Data.Length));
                Assert.AreEqual("ok " + Reply.Length, peer.Send("write " + PeerProtocol.ToHex(Reply)));

                CollectionAssert.AreEqual(Reply, buffer.ReadBytes(Reply.Length));
                Assert.AreEqual("ok", peer.Send("close"));
            }
        }

        /// <summary>
        /// Several processes create, open, read, close, and are killed, some in the middle of a command, on a few names for
        /// 60 s. The only failures are IOException and FileNotFoundException; the lifetime rules hold throughout; and at the
        /// end nothing is left in /dev/shm, or in the lock folder on macOS, but temporary files from kills between create
        /// and publish, which the library leaves rather than sweeping for them; the test removes and reports them, with the
        /// objects they name on macOS.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [TestCategory("Stress")]
        public void ManyProcesses_CreateOpenCloseAndDie_LifetimeHolds()
        {
            var history = new StressHistory("stress-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "-", 3);
            Files.MakeFolder(history.Names[0]);
            string[] temporaryBefore = TemporaryFiles();
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);

            var drivers = Enumerable.Range(0, 4).Select(i => new Thread(() => history.Drive(i, deadline))).ToArray();
            foreach (var driver in drivers)
                driver.Start();
            foreach (var driver in drivers)
                driver.Join();

            // Every peer has exited, so every name's file, if any, was left by a killed holder: opening it removes it.
            foreach (string name in history.Names)
            {
                try
                {
                    new EventFreeBuffer(name).Dispose();
                    history.Problem("opened " + name + " after every peer had exited");
                }
                catch (FileNotFoundException)
                {
                }
                Assert.IsFalse(File.Exists(PerUserPath(name)), name);
            }
            string[] leftovers = TemporaryFiles().Except(temporaryBefore).ToArray();
            foreach (string file in leftovers)
                Files.RemoveTemporary(file);

            Console.WriteLine(history.Summary());
            Console.WriteLine("temporary files left by kills between create and publish: " + leftovers.Length);
            Assert.AreEqual(0, history.Problems.Count, string.Join(Environment.NewLine, history.Problems.Take(20)));
            Assert.IsTrue(history.Count("create ok") > 0 && history.Count("open ok") > 0 && history.Count("kill") > 0, history.Summary());
        }

        /// <summary>
        /// A process that alone holds a buffer ends normally, by returning from Main, without disposing it. Windows closes a
        /// process's handles when it exits; the library runs its close step at exit, and the files go.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void ProcessHoldingABufferEnds_LeavesNothing()
        {
            string name = Guid.NewGuid().ToString();
            string objectName = null;
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create eventfree " + name + " " + Size);
                Assert.IsTrue(File.Exists(PerUserPath(name)));
                if (OperatingSystem.IsMacOS())
                    objectName = Files.ObjectNamedBy(PerUserPath(name));
                peer.Exit();
            }

            Assert.IsFalse(File.Exists(PerUserPath(name)));
            if (objectName != null)
                Assert.IsFalse(Files.ObjectExists(objectName), objectName);
        }

        /// <summary>
        /// A buffer whose open fails after its memory is mapped, as a CircularBuffer's does where named events are missing,
        /// leaves no file, and on macOS no object.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void OpenFailsAfterMapping_LeavesNothing()
        {
            string name = Guid.NewGuid().ToString();
            string objectName = null;

            Assert.ThrowsExactly<InvalidOperationException>(() => new FailingBuffer(name, objectNamed => objectName = objectNamed));

            Assert.IsFalse(File.Exists(PerUserPath(name)));
            if (OperatingSystem.IsMacOS())
            {
                Assert.IsNotNull(objectName);
                Assert.IsFalse(Files.ObjectExists(objectName), objectName);
            }
        }

        /// <summary>
        /// Lock files live under /var/tmp, which macOS's nightly cleaner leaves alone, not /tmp, where it deletes files whose
        /// three times are all over 3 days old, held or not. The per-user folder is private and the global one is shared like /tmp.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void LockFiles_InVarTmp()
        {
            string id = Guid.NewGuid().ToString();
            string perUser = "/var/tmp/.SharedMemory-uid" + Files.EffectiveUserId();
            const string Global = "/var/tmp/.SharedMemory-global";

            using (new EventFreeBuffer(id, Size))
            using (new EventFreeBuffer("Global\\" + id, Size))
            {
                Assert.IsTrue(File.Exists(perUser + "/" + Files.LockFileName(id)));
                Assert.IsTrue(File.Exists(Global + "/" + Files.LockFileName(id)));
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(perUser));
                Assert.AreEqual(Everyone | UnixFileMode.StickyBit, File.GetUnixFileMode(Global));
                Assert.IsNull(new DirectoryInfo(perUser).LinkTarget);
                Assert.IsNull(new DirectoryInfo(Global).LinkTarget);
            }
        }

        /// <summary>
        /// Each create gives the object a new random name, which only the lock file records, so a killed creator's object
        /// is never taken for a live one. The lock file is its header and that name (Files.ObjectNamedBy).
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void LockFile_NamesANewRandomObjectEachTime()
        {
            string name = Guid.NewGuid().ToString();
            string first;
            string second;

            using (new EventFreeBuffer(name, Size))
                first = Files.ObjectNamedBy(PerUserPath(name));
            using (new EventFreeBuffer(name, Size))
                second = Files.ObjectNamedBy(PerUserPath(name));

            StringAssert.Matches(first, new Regex("^/SM[A-Z2-7]{26}$"));
            StringAssert.Matches(second, new Regex("^/SM[A-Z2-7]{26}$"));
            Assert.AreNotEqual(first, second);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        [DataRow("")]
        [DataRow("Global\\")]
        public void LockFolder_Missing_IsMadeWithItsMode(string scope)
        {
            string name = scope + Guid.NewGuid();
            Files.MakeFolder(name);
            string folder = Path.GetDirectoryName(LockPath(name));
            using (Files.SetAside(folder))
            {
                using (new EventFreeBuffer(name, Size))
                {
                    // mkdir applies the umask, 022 here, which would leave the global folder 0755.
                    Assert.AreEqual(scope == "" ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : Everyone | UnixFileMode.StickyBit,
                        File.GetUnixFileMode(folder));
                }
            }
        }

        /// <summary>
        /// A lock folder that is a symbolic link, here to the folder that was there, is refused and left alone.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        [DataRow("")]
        [DataRow("Global\\")]
        public void LockFolder_SymbolicLink_RefusedAndLeftAlone(string scope)
        {
            string name = scope + Guid.NewGuid();
            Files.MakeFolder(name);
            string folder = Path.GetDirectoryName(LockPath(name));
            using (var setAside = Files.SetAside(folder))
            {
                string aside = setAside.Aside;
                File.CreateSymbolicLink(folder, aside);

                var create = Assert.ThrowsExactly<UnauthorizedAccessException>(() => new EventFreeBuffer(name, Size));
                StringAssert.Contains(create.Message, "it is a symbolic link");
                var open = Assert.ThrowsExactly<UnauthorizedAccessException>(() => new EventFreeBuffer(name));
                StringAssert.Contains(open.Message, "it is a symbolic link");

                Assert.AreEqual(aside, new FileInfo(folder).LinkTarget);
                Assert.IsFalse(File.Exists(Path.Combine(aside, Files.LockFileName(name))));
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void PerUserLockFolder_LooseMode_RefusedAndLeftAlone()
        {
            string name = Guid.NewGuid().ToString();
            Files.MakeFolder(name);
            string folder = Path.GetDirectoryName(PerUserPath(name));
            const UnixFileMode Loose = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(folder, Loose);
            try
            {
                var create = Assert.ThrowsExactly<UnauthorizedAccessException>(() => new EventFreeBuffer(name, Size));
                StringAssert.Contains(create.Message, "its mode is 0755, not 0700");
                var open = Assert.ThrowsExactly<UnauthorizedAccessException>(() => new EventFreeBuffer(name));
                StringAssert.Contains(open.Message, "its mode is 0755, not 0700");

                Assert.AreEqual(Loose, File.GetUnixFileMode(folder));
            }
            finally
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        /// <summary>
        /// A global folder of this user's left at another mode, as a process of this user's killed between its mkdir and its
        /// chmod leaves it, is given 1777 again by the next open or create, which goes ahead at once. Another user's would be
        /// looked at again for a second and then refused; the test Mac has one account, so that path does not run here.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void GlobalLockFolder_OfThisUserAtAnotherMode_IsGiven1777Again()
        {
            string name = "Global\\" + Guid.NewGuid();
            Files.MakeFolder(name);
            string folder = Path.GetDirectoryName(GlobalPath(name.Substring(7)));
            const UnixFileMode Loose = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            try
            {
                var watch = Stopwatch.StartNew();
                File.SetUnixFileMode(folder, Loose);
                Assert.ThrowsExactly<FileNotFoundException>(() => new EventFreeBuffer(name));
                Assert.AreEqual(Everyone | UnixFileMode.StickyBit, File.GetUnixFileMode(folder), "after an open");

                File.SetUnixFileMode(folder, Loose);
                using (new EventFreeBuffer(name, Size))
                    Assert.AreEqual(Everyone | UnixFileMode.StickyBit, File.GetUnixFileMode(folder), "after a create");
                Assert.IsTrue(watch.ElapsedMilliseconds < 1000, "took " + watch.ElapsedMilliseconds + " ms; this user's folder needs no wait");
            }
            finally
            {
                File.SetUnixFileMode(folder, Everyone | UnixFileMode.StickyBit);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void LockFileOfAnotherVersion_RefusedAndLeftAlone()
        {
            string name = Guid.NewGuid().ToString();
            Files.MakeFolder(name);
            string path = PerUserPath(name);
            byte[] planted = Files.LockFile(Files.NewObjectName(), 2);
            File.WriteAllBytes(path, planted);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try
            {
                var open = Assert.ThrowsExactly<IOException>(() => new EventFreeBuffer(name));
                StringAssert.Contains(open.Message, "has format version 2; this SharedMemory reads version 1");
                var create = Assert.ThrowsExactly<IOException>(() => new EventFreeBuffer(name, Size));
                StringAssert.Contains(create.Message, "has format version 2; this SharedMemory reads version 1");

                CollectionAssert.AreEqual(planted, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        [DataRow("")]           // empty
        [DataRow("SMLX")]       // another magic
        [DataRow("SMLK-long")]  // a name longer than its file
        [DataRow("SMLK-slash")] // a name without its leading slash
        public void FileThatIsNotALockFile_RefusedAndLeftAlone(string kind)
        {
            string name = Guid.NewGuid().ToString();
            Files.MakeFolder(name);
            string path = PerUserPath(name);
            byte[] planted = kind == "" ? new byte[0] : Files.LockFile(kind == "SMLK-slash" ? "SM" + new string('A', 26) : Files.NewObjectName());
            if (kind == "SMLX")
                planted[3] = (byte)'X';
            if (kind == "SMLK-long")
                planted[8]++;
            File.WriteAllBytes(path, planted);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try
            {
                var open = Assert.ThrowsExactly<IOException>(() => new EventFreeBuffer(name));
                StringAssert.Contains(open.Message, "it is not a SharedMemory lock file");
                var create = Assert.ThrowsExactly<IOException>(() => new EventFreeBuffer(name, Size));
                StringAssert.Contains(create.Message, "it is not a SharedMemory lock file");

                CollectionAssert.AreEqual(planted, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// launchd removes a user's objects when the user logs out, whether their lock files are held or not. An open then
        /// finds the lock file held and its object gone.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void ObjectGoneWhileItsLockFileIsHeld_OpenThrowsFileNotFound()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            using (new EventFreeBuffer(name, Size))
            {
                Files.RemoveObject(Files.ObjectNamedBy(path));

                var e = Assert.ThrowsExactly<FileNotFoundException>(() => new EventFreeBuffer(name));

                StringAssert.Contains(e.Message, "which is gone");
                Assert.IsTrue(File.Exists(path), "The owner still holds the lock file.");
            }
            Assert.IsFalse(File.Exists(path));
        }

        /// <summary>
        /// macOS sizes an object lazily, so a create of more than the machine can map fails at the mapping, and leaves no
        /// lock file, temporary file, or object behind.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void Create_TooLargeToMap_ThrowsIOExceptionAndLeavesNothing()
        {
            string name = Guid.NewGuid().ToString();
            Files.MakeFolder(name);
            string folder = Path.GetDirectoryName(PerUserPath(name));
            string[] before = Directory.GetFiles(folder);

            var e = Assert.ThrowsExactly<IOException>(() => new EventFreeBuffer(name, 1L << 50));

            StringAssert.Contains(e.Message, name);
            CollectionAssert.AreEquivalent(before, Directory.GetFiles(folder));
        }

        /// <summary>
        /// Two holders that close at the same moment: one of them must find it was the last, and remove the file (on macOS the
        /// lock file and its object). A failed LOCK_EX | LOCK_NB drops the shared lock it would have converted on Linux and keeps
        /// it on macOS, where two close steps that overlap could each see the other's lock and both leave. Two hundred rounds,
        /// the two disposes released together.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void TwoHoldersCloseAtOnce_OneRemovesTheFile()
        {
            var left = new List<int>();
            for (int round = 0; round < 200; round++)
            {
                string name = Guid.NewGuid().ToString();
                string path = PerUserPath(name);
                var owner = new EventFreeBuffer(name, Size);
                var opener = new EventFreeBuffer(name);
                string objectName = OperatingSystem.IsMacOS() ? Files.ObjectNamedBy(path) : null;
                using (var start = new Barrier(2))
                {
                    var other = new Thread(() =>
                    {
                        start.SignalAndWait();
                        opener.Dispose();
                    });
                    other.Start();
                    start.SignalAndWait();
                    owner.Dispose();
                    other.Join();
                }

                if (File.Exists(path))
                {
                    left.Add(round);
                    if (objectName != null)
                        Files.RemoveObject(objectName);
                    File.Delete(path);
                }
                else if (objectName != null)
                    Assert.IsFalse(Files.ObjectExists(objectName), "round " + round + ": the lock file went without its object");
            }
            Assert.AreEqual(0, left.Count, "Rounds whose file nobody removed: " + string.Join(", ", left));
        }

        /// <summary>
        /// The buffer's file on Linux, or its lock file on macOS, for a per-user name.
        /// </summary>
        static string PerUserPath(string name)
        {
            if (OperatingSystem.IsMacOS())
                return Files.MacFolder + ".SharedMemory-uid" + Files.EffectiveUserId() + "/" + Files.LockFileName(name);
            return Folder + "SharedMemory.u" + EffectiveUserId() + "." + name;
        }

        /// <summary>
        /// The buffer's file on Linux, or its lock file on macOS, for <c>Global\</c> and <paramref name="name"/>.
        /// </summary>
        static string GlobalPath(string name)
        {
            if (OperatingSystem.IsMacOS())
                return Files.MacFolder + ".SharedMemory-global/" + Files.LockFileName(name);
            return Folder + "SharedMemory.global." + name;
        }

        static string LockPath(string name)
        {
            return name.StartsWith("Global\\", StringComparison.Ordinal) ? GlobalPath(name.Substring(7)) : PerUserPath(name);
        }

        const UnixFileMode Everyone = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        static uint EffectiveUserId()
        {
            string line = File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal));
            return uint.Parse(line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)[2]);
        }

        static int SessionOf(int pid)
        {
            if (OperatingSystem.IsMacOS())
                return Files.SessionOf(pid);
            string stat = File.ReadAllText("/proc/" + pid + "/stat");
            // pid (comm) state ppid pgrp session ...; comm may hold spaces and parentheses.
            return int.Parse(stat.Substring(stat.LastIndexOf(')') + 2).Split(' ')[3]);
        }

        static string[] SharedMemoryFiles()
        {
            return Directory.GetFiles(Folder).Where(f => Path.GetFileName(f).StartsWith("SharedMemory.", StringComparison.Ordinal)
                || Path.GetFileName(f).StartsWith(".SharedMemory.tmp.", StringComparison.Ordinal)).ToArray();
        }

        static string[] TemporaryFiles()
        {
            if (OperatingSystem.IsMacOS())
                return Directory.GetFiles(Path.GetDirectoryName(PerUserPath("x")), ".SharedMemory.tmp.*");
            return Directory.GetFiles(Folder, ".SharedMemory.tmp.*");
        }

        /// <summary>
        /// A buffer whose open fails once its memory is mapped, after telling the test, on macOS, which object it mapped.
        /// </summary>
        sealed class FailingBuffer : SharedBuffer
        {
            readonly Action<string> mapped;

            public FailingBuffer(string name, Action<string> mapped)
                : base(name, Size, true)
            {
                this.mapped = mapped;
                Open();
            }

            protected override bool DoOpen()
            {
                mapped(OperatingSystem.IsMacOS() ? Files.ObjectNamedBy(PerUserPath(Name)) : null);
                throw new InvalidOperationException("The open fails after the mapping.");
            }
        }

        /// <summary>
        /// What the tests look at on macOS beyond the lock files: the folders, the object a lock file names, and the object's
        /// status, which only fstat on a descriptor from shm_open gives. Run on arm64, whose fstat has the 64-bit inode layout.
        /// The event and RPC tests use it too.
        /// </summary>
        internal static unsafe class Files
        {
            public const string MacFolder = "/var/tmp/";

            const string Library = "/usr/lib/libSystem.B.dylib";
            const int O_RDONLY = 0;
            const int ENOENT = 2;

            public struct ObjectStatus
            {
                public int Mode;
                public uint Uid;
                public long Size;
            }

            public static uint EffectiveUserId()
            {
                return geteuid();
            }

            public static int SessionOf(int pid)
            {
                int session = getsid(pid);
                Assert.IsTrue(session > 0, "getsid " + pid + ": " + Marshal.GetLastPInvokeError());
                return session;
            }

            /// <summary>
            /// Makes sure the lock folder for <paramref name="name"/> exists, as any create of its scope leaves it. Nothing on Linux.
            /// </summary>
            public static void MakeFolder(string name)
            {
                if (OperatingSystem.IsMacOS())
                    new EventFreeBuffer((name.StartsWith("Global\\", StringComparison.Ordinal) ? "Global\\" : "") + Guid.NewGuid(), 16).Dispose();
            }

            /// <summary>
            /// A lock file naming <paramref name="objectName"/>: the magic SMLK, the version, the name's length, each a
            /// little-endian uint32, then the name.
            /// </summary>
            public static byte[] LockFile(string objectName, uint version = 1)
            {
                var bytes = new byte[12 + objectName.Length];
                Encoding.ASCII.GetBytes("SMLK").CopyTo(bytes, 0);
                BitConverter.GetBytes(version).CopyTo(bytes, 4);
                BitConverter.GetBytes((uint)objectName.Length).CopyTo(bytes, 8);
                Encoding.ASCII.GetBytes(objectName).CopyTo(bytes, 12);
                return bytes;
            }

            /// <summary>
            /// The lock file name for a name that needs no escaping and is under 200 bytes, as the tests' names are: the name,
            /// <c>~</c>, and the first 26 base32 characters of its SHA-256. Worked out here rather than
            /// by the library, so that a library looking elsewhere fails the tests.
            /// </summary>
            public static string LockFileName(string name)
            {
                if (name.StartsWith("Global\\", StringComparison.Ordinal))
                    name = name.Substring(7);
                else if (name.StartsWith("Local\\", StringComparison.Ordinal))
                    name = name.Substring(6);
                Assert.IsTrue(name.All(c => c > 0x20 && c < 0x7F && c != '%' && c != '/' && c != '\\') && name.Length < 200, name);
                using (var sha = SHA256.Create())
                    return name + "~" + UnixFiles.Base32(sha.ComputeHash(Encoding.UTF8.GetBytes(name)), 26);
            }

            public static string NewObjectName()
            {
                return "/SM" + UnixFiles.Base32(RandomNumberGenerator.GetBytes(17), 26);
            }

            /// <summary>
            /// The name of the object a lock file names, checking the lock file's format: the magic SMLK, version 1, and the name's length.
            /// </summary>
            public static string ObjectNamedBy(string lockFile)
            {
                byte[] bytes = File.ReadAllBytes(lockFile);
                Assert.IsTrue(bytes.Length >= 12, lockFile + " holds " + bytes.Length + " bytes");
                Assert.AreEqual("SMLK", Encoding.ASCII.GetString(bytes, 0, 4));
                Assert.AreEqual(1u, BitConverter.ToUInt32(bytes, 4));
                Assert.AreEqual(bytes.Length - 12, (int)BitConverter.ToUInt32(bytes, 8));
                return Encoding.ASCII.GetString(bytes, 12, bytes.Length - 12);
            }

            public static bool ObjectExists(string objectName)
            {
                int fd = shm_open(objectName, O_RDONLY, 0);
                if (fd >= 0)
                {
                    close(fd);
                    return true;
                }
                Assert.AreEqual(ENOENT, Marshal.GetLastPInvokeError(), "shm_open " + objectName);
                return false;
            }

            public static ObjectStatus StatusOf(string objectName)
            {
                int fd = shm_open(objectName, O_RDONLY, 0);
                Assert.IsTrue(fd >= 0, "shm_open " + objectName + ": " + Marshal.GetLastPInvokeError());
                try
                {
                    Stat status;
                    Assert.AreEqual(0, fstat(fd, &status), "fstat " + objectName);
                    return new ObjectStatus { Mode = status.Mode, Uid = status.Uid, Size = status.Size };
                }
                finally
                {
                    close(fd);
                }
            }

            /// <summary>
            /// Moves a lock folder aside for a test that needs it missing or replaced, and puts it back when disposed, whatever
            /// the test left in its place: a symbolic link or an empty folder is removed, anything else moved to a name of its
            /// own beside it, where the rerun's check of /var/tmp finds it. Another process's buffers in the folder lose their
            /// lock files meanwhile, so these tests are for a machine whose lock folders nothing else is using.
            /// </summary>
            public static SetAsideFolder SetAside(string folder)
            {
                string aside = folder + ".aside-" + Guid.NewGuid().ToString("N");
                Directory.Move(folder, aside);
                return new SetAsideFolder(folder, aside);
            }

            public sealed class SetAsideFolder : IDisposable
            {
                readonly string folder;

                public SetAsideFolder(string folder, string aside)
                {
                    this.folder = folder;
                    Aside = aside;
                }

                public string Aside { get; private set; }

                public void Dispose()
                {
                    var left = new FileInfo(folder);
                    if (left.LinkTarget != null)
                        File.Delete(folder);
                    else if (Directory.Exists(folder))
                    {
                        if (Directory.EnumerateFileSystemEntries(folder).Any())
                            Directory.Move(folder, folder + ".left-" + Guid.NewGuid().ToString("N"));
                        else
                            Directory.Delete(folder);
                    }
                    Directory.Move(Aside, folder);
                }
            }

            public static void RemoveObject(string objectName)
            {
                Assert.AreEqual(0, shm_unlink(objectName), "shm_unlink " + objectName + ": " + Marshal.GetLastPInvokeError());
            }

            /// <summary>
            /// Removes a temporary file a kill left, and on macOS the object it names, if the create got that far.
            /// </summary>
            public static void RemoveTemporary(string file)
            {
                if (OperatingSystem.IsMacOS())
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    if (bytes.Length > 12)
                        shm_unlink(Encoding.ASCII.GetString(bytes, 12, bytes.Length - 12));
                }
                File.Delete(file);
            }

            [StructLayout(LayoutKind.Explicit, Size = 144)]
            struct Stat
            {
                [FieldOffset(4)] public ushort Mode;
                [FieldOffset(16)] public uint Uid;
                [FieldOffset(96)] public long Size;
            }

            // Without O_CREAT, shm_open reads no mode, so its variadic declaration does not matter here.
            [DllImport(Library, SetLastError = true)] static extern int shm_open(string name, int flags, int mode);
            [DllImport(Library, SetLastError = true)] static extern int shm_unlink(string name);
            [DllImport(Library, SetLastError = true)] static extern int fstat(int fd, Stat* status);
            [DllImport(Library, SetLastError = true)] static extern int close(int fd);
            [DllImport(Library, SetLastError = true)] static extern int getsid(int pid);
            [DllImport(Library)] static extern uint geteuid();
        }

        /// <summary>
        /// The peer (SharedMemory.Tests.Peer) started with setsid, so that it runs in a POSIX session of its own. macOS has
        /// no setsid command; perl, which it ships, makes the call. perl's setsid returns -1 on failure, which is true, hence
        /// the comparison.
        /// </summary>
        sealed class SessionPeer : IDisposable
        {
            readonly Process process;
            readonly BlockingCollection<string> replies = new BlockingCollection<string>();

            SessionPeer(Process process)
            {
                this.process = process;
                var reader = new Thread(() =>
                {
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                        replies.Add(line);
                    replies.CompleteAdding();
                });
                reader.IsBackground = true;
                reader.Start();
                process.ErrorDataReceived += (sender, e) => { };
                process.BeginErrorReadLine();
            }

            public static SessionPeer Start()
            {
                string folder = Path.Combine(Path.GetDirectoryName(typeof(SessionPeer).Assembly.Location), "peer");
                string root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
                string host = Path.Combine(root, "dotnet");
                var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/perl" : "setsid")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = folder,
                };
                if (OperatingSystem.IsMacOS())
                {
                    start.ArgumentList.Add("-MPOSIX");
                    start.ArgumentList.Add("-e");
                    start.ArgumentList.Add("POSIX::setsid() > 0 or die \"setsid: $!\\n\"; exec @ARGV or die \"exec: $!\\n\"");
                    start.ArgumentList.Add("--");
                }
                start.ArgumentList.Add(File.Exists(host) ? host : "dotnet");
                start.ArgumentList.Add("exec");
                start.ArgumentList.Add(Path.Combine(folder, "SharedMemory.Tests.Peer.dll"));

                var peer = new SessionPeer(Process.Start(start));
                try
                {
                    Assert.AreEqual("ready " + PeerProtocol.LibraryHash(), peer.Receive(60000));
                    return peer;
                }
                catch
                {
                    peer.Dispose();
                    throw;
                }
            }

            public int Id
            {
                get { return process.Id; }
            }

            public string Send(string command)
            {
                process.StandardInput.WriteLine(command);
                process.StandardInput.Flush();
                return Receive(15000);
            }

            string Receive(int timeout)
            {
                string reply;
                if (!replies.TryTake(out reply, timeout))
                    throw new TimeoutException("The peer sent no reply within " + timeout + " ms.");
                return reply;
            }

            public void Dispose()
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(60000);
                }
                process.Dispose();
            }
        }

        /// <summary>
        /// The stress test's record of who could hold which name when, and its checks of the lifetime rules against it.
        /// </summary>
        /// <remarks>
        /// <para>Each create or open a peer sends opens an interval: the peer may hold the name from when the command is sent
        /// until its close is answered or the peer has been killed, and certainly holds it from the success reply until the
        /// close or kill is sent. The checks, each on one name:</para>
        /// <list type="bullet">
        /// <item>A create that succeeds: no other peer certainly held the name from before the create was sent until after it was answered.</item>
        /// <item>An open that succeeds, or a create refused as in use: some other peer may have held the name meanwhile.</item>
        /// <item>A file outlives its creator while others hold it, but a create succeeds only when no file is held, so one file at most is live per
        /// name. What an opener reads is zeros (its creator was killed before writing, or has not written yet) or the token of the file it opened:
        /// the latest create answered before the open was sent, or a create sent before the open was answered and answered after it was sent.
        /// A creator reads its own token.</item>
        /// </list>
        /// </remarks>
        sealed class StressHistory
        {
            const int Size = 4096;
            const long Open = long.MaxValue;

            readonly object gate = new object();
            readonly List<Hold> holds = new List<Hold>();
            readonly ConcurrentDictionary<string, int> counts = new ConcurrentDictionary<string, int>();
            int nextPeer;

            public StressHistory(string prefix, int names)
            {
                Names = Enumerable.Range(0, names).Select(i => prefix + i).ToArray();
            }

            public string[] Names { get; private set; }

            public ConcurrentQueue<string> Problems { get; } = new ConcurrentQueue<string>();

            public void Problem(string problem)
            {
                Problems.Enqueue(problem);
            }

            public int Count(string what)
            {
                int n;
                return counts.TryGetValue(what, out n) ? n : 0;
            }

            public string Summary()
            {
                return string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + " " + c.Value));
            }

            void Counted(string what)
            {
                counts.AddOrUpdate(what, 1, (k, n) => n + 1);
            }

            sealed class Hold
            {
                public string Name;
                public int Peer;
                public bool Create;
                public long PossibleStart;
                public long CertainStart = Open;
                public long CertainEnd = Open;
                public long PossibleEnd = Open;
                public byte[] Token;
            }

            public void Drive(int driver, DateTime deadline)
            {
                var random = new Random(driver);
                PeerProcess peer = null;
                int peerId = 0;
                Hold held = null;
                try
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        if (peer == null)
                        {
                            peer = PeerProcess.Start();
                            peerId = Interlocked.Increment(ref nextPeer);
                        }

                        int roll = random.Next(100);
                        if (held != null)
                        {
                            if (roll < 45)
                                Read(peer, held);
                            else if (roll < 90)
                            {
                                End(held, certainOnly: true);
                                Reply(peer.Send("close"), "close");
                                End(held, certainOnly: false);
                                held = null;
                            }
                            else
                            {
                                End(held, certainOnly: true);
                                peer.Kill();
                                End(held, certainOnly: false);
                                Counted("kill");
                                peer.Dispose();
                                peer = null;
                                held = null;
                            }
                            continue;
                        }

                        string name = Names[random.Next(Names.Length)];
                        bool create = random.Next(2) == 0;
                        string command = create ? "create eventfree " + name + " " + Size : "open eventfree " + name;
                        if (roll < 10)
                        {
                            // Kill the peer partway through the command: it may or may not have completed it.
                            Hold uncertain = Begin(name, peerId, create);
                            var sent = Task.Run(() => { try { peer.Send(command); } catch (Exception e) when (e is TimeoutException || e is IOException) { } });
                            Thread.Sleep(random.Next(4));
                            peer.Kill();
                            sent.Wait();
                            End(uncertain, certainOnly: false);
                            Counted("kill during " + (create ? "create" : "open"));
                            peer.Dispose();
                            peer = null;
                            continue;
                        }

                        Hold hold = Begin(name, peerId, create);
                        string reply = peer.Send(command);
                        long answered = Stopwatch.GetTimestamp();
                        string what = create ? "create" : "open";
                        if (reply == "ok")
                        {
                            Counted(what + " ok");
                            Succeeded(hold, create, answered);
                            held = hold;
                            if (create)
                            {
                                var token = new byte[16];
                                random.NextBytes(token);
                                lock (gate)
                                    hold.Token = token;
                                Reply(peer.Send("write " + PeerProtocol.ToHex(token)), "write");
                            }
                            else
                                Read(peer, held);
                        }
                        else
                        {
                            Reply(reply, what);
                            Refused(hold, create, reply, answered);
                        }
                    }
                }
                catch (Exception e)
                {
                    Problem("driver " + driver + ": " + e.GetType().FullName + ": " + e.Message);
                }
                finally
                {
                    if (peer != null)
                    {
                        if (held != null)
                        {
                            End(held, certainOnly: true);
                            try { peer.Send("close"); } catch (Exception e) when (e is TimeoutException || e is IOException) { }
                            End(held, certainOnly: false);
                        }
                        peer.Dispose();
                    }
                }
            }

            Hold Begin(string name, int peer, bool create)
            {
                var hold = new Hold { Name = name, Peer = peer, Create = create, PossibleStart = Stopwatch.GetTimestamp() };
                lock (gate)
                    holds.Add(hold);
                return hold;
            }

            void End(Hold hold, bool certainOnly)
            {
                long now = Stopwatch.GetTimestamp();
                lock (gate)
                {
                    if (hold.CertainEnd == Open)
                        hold.CertainEnd = now;
                    if (!certainOnly)
                        hold.PossibleEnd = now;
                }
            }

            void Succeeded(Hold hold, bool create, long answered)
            {
                lock (gate)
                {
                    if (create)
                    {
                        Hold other = holds.FirstOrDefault(h => h != hold && h.Name == hold.Name
                            && h.CertainStart < hold.PossibleStart && h.CertainEnd > answered);
                        if (other != null)
                            Problem("peer " + hold.Peer + " created " + hold.Name + " while peer " + other.Peer + " held it");
                    }
                    else if (!PossiblyHeld(hold, answered))
                        Problem("peer " + hold.Peer + " opened " + hold.Name + " while no other peer could hold it");
                    hold.CertainStart = answered;
                }
            }

            void Refused(Hold hold, bool create, string reply, long answered)
            {
                lock (gate)
                {
                    hold.CertainEnd = hold.PossibleEnd = answered;
                    if (create && reply.StartsWith("error System.IO.IOException:", StringComparison.Ordinal) && !PossiblyHeld(hold, answered))
                        Problem("peer " + hold.Peer + " was refused " + hold.Name + " as in use while no other peer could hold it: " + reply);
                }
            }

            bool PossiblyHeld(Hold hold, long until)
            {
                return holds.Any(h => h != hold && h.Name == hold.Name && h.PossibleStart < until && h.PossibleEnd > hold.PossibleStart);
            }

            void Read(PeerProcess peer, Hold hold)
            {
                string reply = peer.Send("read 16");
                long answered = Stopwatch.GetTimestamp();
                Reply(reply, "read");
                if (!reply.StartsWith("ok ", StringComparison.Ordinal))
                    return;
                byte[] read = PeerProtocol.FromHex(reply.Substring(3));
                lock (gate)
                {
                    bool known;
                    if (read.All(b => b == 0))
                        known = true;
                    else if (hold.Create)
                        known = hold.Token != null && hold.Token.SequenceEqual(read);
                    else
                    {
                        long sent = hold.PossibleStart, opened = hold.CertainStart;
                        var created = holds.Where(h => h.Name == hold.Name && h.Create && h.CertainStart != Open).ToList();
                        Hold latest = created.Where(h => h.CertainStart < sent).OrderBy(h => h.CertainStart).LastOrDefault();
                        known = created.Where(h => h == latest || (h.PossibleStart < opened && h.CertainStart >= sent))
                            .Any(h => h.Token != null && h.Token.SequenceEqual(read));
                    }
                    if (!known)
                        Problem("peer " + hold.Peer + " read " + reply.Substring(3) + " from " + hold.Name + ", not the token of a file it could have opened");
                }
            }

            void Reply(string reply, string what)
            {
                if (reply == "ok" || reply.StartsWith("ok ", StringComparison.Ordinal))
                    return;
                string type = reply.StartsWith("error ", StringComparison.Ordinal) ? reply.Substring(6, Math.Max(0, reply.IndexOf(':') - 6)) : reply;
                Counted(what + " " + type);
                if (type != "System.IO.IOException" && type != "System.IO.FileNotFoundException")
                    Problem(what + ": " + reply);
                else if (reply.Contains("kept changing"))
                    Problem(what + ": " + reply);
            }
        }
    }
}
#endif
