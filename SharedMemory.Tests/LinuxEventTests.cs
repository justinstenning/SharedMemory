#if NET
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// Named events on Linux: each is a 64-byte file in /dev/shm under the event's Windows name, private to its user and
    /// removed by its last holder; which files found under an event's name are opened, replaced, or refused; the waiter
    /// count in the file; and the futex call for this architecture.
    /// </summary>
    [TestClass]
    [SupportedOSPlatform("linux")]
    public class LinuxEventTests
    {
        const string Folder = "/dev/shm/";
        const int EventSize = 64;
        const uint Magic = 0x56454D53;

        static readonly byte[] Data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [DataRow("circular")]
        [DataRow("readwrite")]
        public void Events_FilesInDevShm_PrivateToUser_RemovedWithTheLastHolder(string kind)
        {
            string name = Guid.NewGuid().ToString();
            bool circular = kind == "circular";
            string[] paths = (circular ? new[] { "_evt_dataexists", "_evt_nodeavail" } : new[] { "_evt_write", "_evt_read" })
                .Select(suffix => PerUserPath(name + suffix)).ToArray();

            using (SharedBuffer owner = circular ? new CircularBuffer(name, 2, 64) : new BufferReadWrite(name, 1024))
            using (SharedBuffer consumer = circular ? new CircularBuffer(name) : new BufferReadWrite(name))
            {
                foreach (string path in paths)
                {
                    Assert.IsTrue(File.Exists(path), path);
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path), path);
                    // Auto reset and not signalled for CircularBuffer, manual reset and signalled for BufferWithLocks.
                    CollectionAssert.AreEqual(EventFile(1, circular ? 0 : 1, circular ? 0 : 1, 0), File.ReadAllBytes(path), path);
                }
            }

            foreach (string path in paths)
                Assert.IsFalse(File.Exists(path), path);
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void EventFileOfAnotherFormatVersion_SomeoneHoldsIt_ThrowsIOExceptionNamingTheVersion_LeftAlone()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            byte[] planted = EventFile(2, 0, 1, 0);
            Plant(path, planted);
            try
            {
                using (Hold(path))
                {
                    var e = Assert.ThrowsExactly<IOException>(() => TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset));
                    StringAssert.Contains(e.Message, "format version 2");
                    StringAssert.Contains(e.Message, path);
                }
                CollectionAssert.AreEqual(planted, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [DataRow("too short")]
        [DataRow("no magic")]
        [DataRow("too long")]
        [DataRow("unknown reset mode")]
        public void FileThatIsNoEvent_SomeoneHoldsIt_ThrowsWaitHandleCannotBeOpenedException_LeftAlone(string what)
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            byte[] planted;
            switch (what)
            {
                case "too short": planted = EventFile(1, 0, 1, 0).Take(4).ToArray(); break;
                case "no magic": planted = new byte[EventSize]; break;
                case "too long": planted = EventFile(1, 0, 1, 0).Concat(new byte[EventSize]).ToArray(); break;
                default: planted = EventFile(1, 2, 1, 0); break;
            }
            Plant(path, planted);
            try
            {
                using (Hold(path))
                {
                    var e = Assert.ThrowsExactly<WaitHandleCannotBeOpenedException>(() => TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset));
                    StringAssert.Contains(e.Message, path);
                }
                CollectionAssert.AreEqual(planted, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// A file nobody holds was left by a killed process. It is replaced whatever it holds, never checked or opened.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [DataRow("set auto-reset event")]
        [DataRow("other format version")]
        [DataRow("no event")]
        public void FileNobodyHolds_IsReplacedByANewEvent(string what)
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            Plant(path, what == "set auto-reset event" ? EventFile(1, 0, 1, 0) : what == "other format version" ? EventFile(2, 0, 1, 0) : new byte[7]);
            try
            {
                using (var replaced = TestEvent.CreateOrOpen(name, true, EventResetMode.ManualReset))
                {
                    CollectionAssert.AreEqual(EventFile(1, 1, 1, 0), File.ReadAllBytes(path));
                    Assert.IsTrue(replaced.WaitOne(0));
                    Assert.IsTrue(replaced.WaitOne(0), "The new event should be manual reset.");
                }
                Assert.IsFalse(File.Exists(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void PerUserEventFileOthersCanWrite_RefusedAndLeftAlone()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            byte[] planted = EventFile(1, 0, 1, 0);
            Plant(path, planted);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
            try
            {
                var e = Assert.ThrowsExactly<UnauthorizedAccessException>(() => TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset));
                StringAssert.Contains(e.Message, "group or others can write to it");
                CollectionAssert.AreEqual(planted, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void WaiterCount_InTheFile_CountsTheThreadsWaiting()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            using (var setter = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            using (var first = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            using (var second = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            {
                Assert.AreEqual(0, WaiterCount(path));
                Task<bool>[] waits = { Wait(first), Wait(second) };
                AssertWaiterCount(path, 2);

                setter.Set();
                int released = Task.WaitAny(waits, 5000);
                Assert.AreNotEqual(-1, released, "The Set released neither waiter.");
                AssertWaiterCount(path, 1);

                setter.Set();
                Assert.IsTrue(Task.WaitAll(waits, 5000), "The second Set did not release the other waiter.");
                Assert.AreEqual(0, WaiterCount(path));
            }
        }

        /// <summary>
        /// A process killed while it waits cannot take itself off the waiter count. The count stays one too high, which
        /// costs later Sets a wake call and nothing else.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void WaiterKilled_CountStaysOneTooHigh_EventStillWorks()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name + "_evt_dataexists");
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create circular " + name + " 4 64");
                using (var consumer = new CircularBuffer(name))
                {
                    var peerRead = Task.Factory.StartNew(() => peer.Send("read 64 20000", 30000), TaskCreationOptions.LongRunning);
                    AssertWaiterCount(path, 1);
                    peer.Kill();
                    Assert.ThrowsExactly<AggregateException>(() => peerRead.Wait(30000), "The killed peer's read should end without a reply.");
                    Assert.AreEqual(1, WaiterCount(path));

                    var data = new byte[64];
                    var read = Task.Factory.StartNew(() => consumer.Read(data, 0, 20000), TaskCreationOptions.LongRunning);
                    AssertWaiterCount(path, 2);
                    Assert.AreEqual(Data.Length, consumer.Write(Data));
                    Assert.IsTrue(read.Wait(5000), "The read did not wake.");
                    Assert.AreEqual(Data.Length, read.Result);
                    CollectionAssert.AreEqual(Data, data.Take(Data.Length).ToArray());
                    Assert.AreEqual(1, WaiterCount(path));
                }
            }
            Assert.IsFalse(File.Exists(path), "The last holder should have removed the event file.");
        }

        /// <summary>
        /// A wait in progress holds its event, as on Windows: disposing the event while another thread waits on it keeps the
        /// file under its name, so another opener reaches the waiter, and the file goes when the wait returns.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void DisposedWhileAThreadWaits_FileStaysUntilTheWaitReturns()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            var waiter = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset);
            Task<bool> wait = Wait(waiter);
            AssertWaiterCount(path, 1);

            waiter.Dispose();
            Thread.Sleep(100);
            Assert.IsTrue(File.Exists(path), "The event lost its name while a wait held it.");

            using (var opener = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
                opener.Set();
            Assert.IsTrue(wait.Wait(5000), "The Set through the other opener did not release the wait.");
            Assert.IsTrue(wait.Result);
            Assert.IsFalse(File.Exists(path), "The last holder's file should be gone once the wait returned.");
        }

        /// <summary>
        /// An event nobody disposes is unmapped and its file removed when it is collected, as a Windows event's handle is
        /// closed when it is finalised. /proc/self/maps names a created event's mapping by the temporary file it was
        /// written under, so the test finds the mapping by its address range, device, and inode.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void NeverDisposed_EventUnmappedAndFileRemovedWhenCollected()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);

            string mapping = CreateEventAndDrop(name, path);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.IsFalse(File.Exists(path), "The file should be gone.");
            CollectionAssert.DoesNotContain(DevShmMappings(), mapping, "The event is still mapped.");
        }

        /// <returns>The event's mapping: address range, device, and inode.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string CreateEventAndDrop(string name, string path)
        {
            string[] before = DevShmMappings();
            TestEvent.CreateOrOpen(name, true, EventResetMode.ManualReset);
            Assert.IsTrue(File.Exists(path));
            string[] added = DevShmMappings().Except(before).ToArray();
            Assert.AreEqual(1, added.Length, "The event should be mapped once: " + string.Join(", ", added));
            return added[0];
        }

        /// <summary>
        /// This process's mappings of files in /dev/shm, each as its address range, device, and inode.
        /// </summary>
        static string[] DevShmMappings()
        {
            return File.ReadAllLines("/proc/self/maps").Where(l => l.Contains("/dev/shm/"))
                .Select(l => l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(f => f[0] + " " + f[3] + " " + f[4]).ToArray();
        }

        /// <summary>
        /// Which system call the events make here. Under QEMU it shows that the arm64 path ran. The number is read by
        /// reflection, since these tests also build against the netstandard2.1 library, which has no LinuxNativeMethods;
        /// there the event throws first.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void Futex_SyscallNumberForThisArchitecture()
        {
            using (var e = TestEvent.CreateOrOpen(Guid.NewGuid().ToString(), false, EventResetMode.AutoReset))
            {
                e.Set();
                Assert.IsTrue(e.WaitOne(0));
            }
            var futex = (long)typeof(NamedEvent).Assembly.GetType("SharedMemory.LinuxNativeMethods", true)
                .GetProperty("FutexSyscall", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

            Console.WriteLine("process architecture " + RuntimeInformation.ProcessArchitecture + ", futex syscall " + futex);
            Assert.AreEqual(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 98L : 202L, futex);
        }

        /// <summary>
        /// The 64 bytes of an event file: magic, format version, reset mode (0 auto, 1 manual), state, waiter count.
        /// </summary>
        static byte[] EventFile(uint version, int mode, int state, int waiters)
        {
            var file = new byte[EventSize];
            BitConverter.GetBytes(Magic).CopyTo(file, 0);
            BitConverter.GetBytes(version).CopyTo(file, 4);
            BitConverter.GetBytes(mode).CopyTo(file, 8);
            BitConverter.GetBytes(state).CopyTo(file, 12);
            BitConverter.GetBytes(waiters).CopyTo(file, 16);
            return file;
        }

        static int WaiterCount(string path)
        {
            return BitConverter.ToInt32(File.ReadAllBytes(path), 16);
        }

        static void AssertWaiterCount(string path, int expected)
        {
            for (int i = 0; i < 100 && WaiterCount(path) != expected; i++)
                Thread.Sleep(50);
            Assert.AreEqual(expected, WaiterCount(path), "waiter count in " + path);
        }

        static void Plant(string path, byte[] contents)
        {
            File.WriteAllBytes(path, contents);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        /// <summary>
        /// Holds a shared flock on the file, as a process with the event open does: FileStream takes one for FileShare.ReadWrite.
        /// </summary>
        static FileStream Hold(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        static Task<bool> Wait(TestEvent waiter)
        {
            return Task.Factory.StartNew(() => waiter.WaitOne(20000), TaskCreationOptions.LongRunning);
        }

        static string PerUserPath(string name)
        {
            return Folder + "SharedMemory.u" + EffectiveUserId() + "." + name;
        }

        static uint EffectiveUserId()
        {
            string line = File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal));
            return uint.Parse(line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)[2]);
        }
    }
}
#endif
