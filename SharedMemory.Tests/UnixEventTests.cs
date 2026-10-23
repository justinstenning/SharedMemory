#if NET
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// Named events on Linux and macOS: on Linux each is a 64-byte file in /dev/shm under the event's Windows name, on macOS
    /// the first 64 bytes of an shm_open object named by a lock file under that name; private to its user and removed by its
    /// last holder; which files found under an event's name are opened, replaced, or refused; the waiter count in the region;
    /// and the futex call for this architecture, or on macOS the os_sync calls. The tests read and plant regions through
    /// Contents and Plant: on macOS a planted event is an object holding the bytes and a lock file naming it.
    /// </summary>
    [TestClass]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public class UnixEventTests
    {
        const string Folder = "/dev/shm/";
        const int EventSize = 64;
        const uint Magic = 0x56454D53;

        static readonly byte[] Data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        /// <summary>
        /// On macOS the paths are lock files, 0600 as Linux's event files are, and each names an object of one 16 KB page,
        /// 0600, gone with its lock file.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow("circular")]
        [DataRow("readwrite")]
        public void Events_PrivateToUser_RemovedWithTheLastHolder(string kind)
        {
            string name = Guid.NewGuid().ToString();
            bool circular = kind == "circular";
            string[] paths = (circular ? new[] { "_evt_dataexists", "_evt_nodeavail" } : new[] { "_evt_write", "_evt_read" })
                .Select(suffix => PerUserPath(name + suffix)).ToArray();
            var objects = new string[0];

            using (SharedBuffer owner = circular ? new CircularBuffer(name, 2, 64) : new BufferReadWrite(name, 1024))
            using (SharedBuffer consumer = circular ? new CircularBuffer(name) : new BufferReadWrite(name))
            {
                foreach (string path in paths)
                {
                    Assert.IsTrue(File.Exists(path), path);
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path), path);
                    // Auto reset and not signalled for CircularBuffer, manual reset and signalled for BufferWithLocks.
                    CollectionAssert.AreEqual(EventFile(1, circular ? 0 : 1, circular ? 0 : 1, 0), Contents(path), path);
                }
                if (OperatingSystem.IsMacOS())
                {
                    objects = paths.Select(UnixBufferTests.Files.ObjectNamedBy).ToArray();
                    foreach (string objectName in objects)
                    {
                        UnixBufferTests.Files.ObjectStatus status = UnixBufferTests.Files.StatusOf(objectName);
                        Assert.AreEqual(0x180, status.Mode & 0xFFF, objectName + " should be 0600.");
                        Assert.AreEqual(UnixBufferTests.Files.EffectiveUserId(), status.Uid, objectName);
                        Assert.AreEqual(16384L, status.Size, objectName + " should be one page.");
                    }
                }
            }

            foreach (string path in paths)
                Assert.IsFalse(File.Exists(path), path);
            CollectionAssert.AreEqual(new string[0], objects.Where(UnixBufferTests.Files.ObjectExists).ToArray(), "Objects left by the last holder");
        }

        /// <summary>
        /// Events of a Global\ name are machine-wide: on Linux SharedMemory.global. files, on macOS lock files in the global
        /// lock folder.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void GlobalName_EventsAreMachineWide()
        {
            string name = Guid.NewGuid().ToString();
            string[] paths = new[] { "_evt_dataexists", "_evt_nodeavail" }.Select(suffix => GlobalPath(name + suffix)).ToArray();
            var objects = new string[0];
            using (var owner = new CircularBuffer("Global\\" + name, 2, 64))
            using (var consumer = new CircularBuffer("Global\\" + name))
            {
                foreach (string path in paths)
                    CollectionAssert.AreEqual(EventFile(1, 0, 0, 0), Contents(path), path);
                if (OperatingSystem.IsMacOS())
                    objects = paths.Select(UnixBufferTests.Files.ObjectNamedBy).ToArray();
                Assert.AreEqual(Data.Length, owner.Write(Data));
                var data = new byte[64];
                Assert.AreEqual(Data.Length, consumer.Read(data, 0, 5000));
                CollectionAssert.AreEqual(Data, data.Take(Data.Length).ToArray());
            }
            foreach (string path in paths)
                Assert.IsFalse(File.Exists(path), path);
            CollectionAssert.AreEqual(new string[0], objects.Where(UnixBufferTests.Files.ObjectExists).ToArray(), "Objects left by the last holder");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
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
                CollectionAssert.AreEqual(planted, Contents(path));
            }
            finally
            {
                Remove(path);
            }
        }

        /// <summary>
        /// On macOS an object is whole pages, so "too short" is an object of 0 bytes, which cannot be mapped, and an object
        /// cannot be too long.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow("too short")]
        [DataRow("no magic")]
        [DataRow("unknown reset mode")]
        public void FileThatIsNoEvent_SomeoneHoldsIt_ThrowsWaitHandleCannotBeOpenedException_LeftAlone(string what)
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            byte[] planted;
            switch (what)
            {
                case "too short": planted = OperatingSystem.IsMacOS() ? new byte[0] : EventFile(1, 0, 1, 0).Take(4).ToArray(); break;
                case "no magic": planted = new byte[EventSize]; break;
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
                CollectionAssert.AreEqual(planted, Contents(path));
            }
            finally
            {
                Remove(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void EventFileTooLong_SomeoneHoldsIt_ThrowsWaitHandleCannotBeOpenedException_LeftAlone()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            byte[] planted = EventFile(1, 0, 1, 0).Concat(new byte[EventSize]).ToArray();
            Plant(path, planted);
            try
            {
                using (Hold(path))
                {
                    var e = Assert.ThrowsExactly<WaitHandleCannotBeOpenedException>(() => TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset));
                    StringAssert.Contains(e.Message, path);
                }
                CollectionAssert.AreEqual(planted, Contents(path));
            }
            finally
            {
                Remove(path);
            }
        }

        /// <summary>
        /// A file nobody holds was left by a killed process. It is replaced whatever it holds, never checked or opened.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow("set auto-reset event")]
        [DataRow("other format version")]
        [DataRow("no event")]
        public void FileNobodyHolds_IsReplacedByANewEvent(string what)
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            Plant(path, what == "set auto-reset event" ? EventFile(1, 0, 1, 0) : what == "other format version" ? EventFile(2, 0, 1, 0) : new byte[7]);
            string plantedObject = OperatingSystem.IsMacOS() ? UnixBufferTests.Files.ObjectNamedBy(path) : null;
            try
            {
                using (var replaced = TestEvent.CreateOrOpen(name, true, EventResetMode.ManualReset))
                {
                    CollectionAssert.AreEqual(EventFile(1, 1, 1, 0), Contents(path));
                    Assert.IsTrue(replaced.WaitOne(0));
                    Assert.IsTrue(replaced.WaitOne(0), "The new event should be manual reset.");
                    if (plantedObject != null)
                    {
                        Assert.IsFalse(UnixBufferTests.Files.ObjectExists(plantedObject), "The abandoned object should be gone with its lock file.");
                        Assert.AreNotEqual(plantedObject, UnixBufferTests.Files.ObjectNamedBy(path));
                    }
                }
                Assert.IsFalse(File.Exists(path));
            }
            finally
            {
                Remove(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
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
                CollectionAssert.AreEqual(planted, Contents(path));
            }
            finally
            {
                Remove(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
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
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
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
        /// The state word: bit 0 is the signal, and bits 1 to 31 count the Sets that signalled the event. A Set on a signalled
        /// event changes nothing, and a Reset or an auto-reset wait clears the signal and keeps the count.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow(EventResetMode.ManualReset)]
        [DataRow(EventResetMode.AutoReset)]
        public void StateWord_Bit0IsTheSignal_TheRestCountsSets(EventResetMode mode)
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            using (var e = TestEvent.CreateOrOpen(name, false, mode))
            {
                Assert.AreEqual(0, State(path));
                e.Set();
                Assert.AreEqual(1 << 1 | 1, State(path));
                e.Set();
                Assert.AreEqual(1 << 1 | 1, State(path), "A Set on a signalled event should change nothing.");
                if (mode == EventResetMode.ManualReset)
                    e.Reset();
                else
                    Assert.IsTrue(e.WaitOne(0));
                Assert.AreEqual(1 << 1, State(path));
                e.Set();
                Assert.AreEqual(2 << 1 | 1, State(path));
            }
        }

        /// <summary>
        /// A manual-reset waiter returns true only if a Set came while it waited, not for any wake. A Set that loses the CPU
        /// between reading the waiter count and its wake call wakes the word after whatever came meanwhile: here a Reset, then a
        /// thread that starts waiting. The test makes that late wake itself, with no Set, and the thread must stay blocked, as
        /// on Windows, where it began waiting after the Reset.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void ManualReset_WakeWithoutASet_DoesNotReleaseAThreadThatBeganAfterTheReset()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            using (var setter = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            using (var waiter = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            {
                setter.Set();
                setter.Reset();
                Task<bool> wait = Wait(waiter);
                AssertWaiterCount(path, 1);
                Thread.Sleep(100);

                Assert.AreEqual(1L, WakeStateWord(path), "The wake should have found the waiting thread.");
                Assert.IsFalse(wait.Wait(300), "A wake with no Set released a thread that began waiting after the Reset.");

                setter.Set();
                Assert.IsTrue(wait.Wait(5000), "The Set did not release the waiting thread.");
                Assert.IsTrue(wait.Result);
            }
        }

        /// <summary>
        /// A wait in progress holds its event, as on Windows: disposing the event while another thread waits on it keeps the
        /// file under its name, so another opener reaches the waiter, and the file goes when the wait returns.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
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
        /// The macOS counterpart of the test above: an event nobody disposes is unmapped, and its lock file and object removed,
        /// when it is collected. macOS has no /proc/self/maps; the test reads the region's address through reflection and asks
        /// mach_vm_region, which vmmap reads, whether a region still holds it. macOS's mincore succeeds for an unmapped address.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void NeverDisposed_EventUnmappedAndLockFileAndObjectRemovedWhenCollected()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);

            string objectName;
            IntPtr address = CreateMacEventAndDrop(name, path, out objectName);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.IsFalse(File.Exists(path), "The lock file should be gone.");
            Assert.IsFalse(UnixBufferTests.Files.ObjectExists(objectName), "The object should be gone.");
            Assert.IsFalse(MacObjects.IsMapped(address), "The event is still mapped.");
        }

        /// <returns>The address of the event's region.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static IntPtr CreateMacEventAndDrop(string name, string path, out string objectName)
        {
            TestEvent dropped = TestEvent.CreateOrOpen(name, true, EventResetMode.ManualReset);
            Assert.IsTrue(File.Exists(path));
            objectName = UnixBufferTests.Files.ObjectNamedBy(path);
            object namedEvent = typeof(TestEvent).GetField("namedEvent", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dropped);
            object macEvent = namedEvent.GetType().GetField("_macEvent", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(namedEvent);
            var region = (SafeHandle)macEvent.GetType().GetField("_region", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(macEvent);
            IntPtr address = region.DangerousGetHandle();
            Assert.IsTrue(MacObjects.IsMapped(address), "The event should be mapped.");
            return address;
        }

        /// <summary>
        /// macOS before 14.4 lacks the os_sync calls. This Mac has them; the test makes the library believe one is missing, and
        /// creating or opening an event then throws PlatformNotSupportedException naming it and macOS 14.4, before anything is
        /// made under the name. Read and set through reflection, since these tests also build against the netstandard2.1 library,
        /// which has no MacNativeMethods; there the event made first throws.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void OsSyncCallMissing_EventsNotSupported_NamingTheCallAndMacOS144()
        {
            using (var e = TestEvent.CreateOrOpen(Guid.NewGuid().ToString(), false, EventResetMode.AutoReset))
                Assert.IsTrue(e.Set());
            Type osSync = typeof(NamedEvent).Assembly.GetType("SharedMemory.MacNativeMethods+OsSync", true);
            PropertyInfo unsupported = osSync.GetProperty("Unsupported", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo missing = osSync.GetMethod("Missing", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNull(unsupported.GetValue(null), "This Mac has every os_sync call.");
            Assert.IsNull(missing.Invoke(null, new object[] { new nint[] { 1, 1, 1, 1 } }));
            var reason = (string)missing.Invoke(null, new object[] { new nint[] { 1, 0, 1, 1 } });
            Assert.AreEqual("libSystem lacks os_sync_wait_on_address_with_timeout, which macOS 14.4 added", reason);

            string name = Guid.NewGuid().ToString();
            unsupported.SetValue(null, reason);
            try
            {
                var e = Assert.ThrowsExactly<PlatformNotSupportedException>(() => TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset));
                StringAssert.StartsWith(e.Message, "SharedMemory does not support named events on macOS ");
                StringAssert.EndsWith(e.Message, " (" + RuntimeInformation.ProcessArchitecture + "): " + reason + ".");
            }
            finally
            {
                unsupported.SetValue(null, null);
            }
            Assert.IsFalse(File.Exists(PerUserPath(name)), "Nothing should be made under the name.");
        }

        /// <summary>
        /// On macOS the object a lock file names can go while the lock file is held (launchd removes a user's objects at
        /// logout). Opening the event then throws FileNotFoundException naming the object, and the lock file, which another
        /// holder still holds, is left alone.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        public void HeldLockFileNamingAGoneObject_ThrowsFileNotFoundException_LeftAlone()
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            Plant(path, EventFile(1, 0, 0, 0));
            string objectName = UnixBufferTests.Files.ObjectNamedBy(path);
            MacObjects.Unlink(objectName);
            byte[] lockFile = File.ReadAllBytes(path);
            try
            {
                using (Hold(path))
                {
                    var e = Assert.ThrowsExactly<FileNotFoundException>(() => TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset));
                    StringAssert.Contains(e.Message, objectName);
                }
                CollectionAssert.AreEqual(lockFile, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// Threads that create or open one new name at the same moment all end up with one event: a Set through any is taken by
        /// a wait through any other. Some lose the race to publish their own and must open the winner's, then remove what they
        /// made. Fifty rounds of eight threads.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void CreateOrOpen_ManyAtOnce_AllOpenOneEvent()
        {
            const int Threads = 8;
            for (int round = 0; round < 50; round++)
            {
                string name = Guid.NewGuid().ToString();
                var events = new TestEvent[Threads];
                var errors = new Exception[Threads];
                using (var start = new Barrier(Threads))
                {
                    Thread[] threads = Enumerable.Range(0, Threads).Select(i => new Thread(() =>
                    {
                        start.SignalAndWait();
                        try
                        {
                            events[i] = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset);
                        }
                        catch (Exception e)
                        {
                            errors[i] = e;
                        }
                    })).ToArray();
                    foreach (Thread thread in threads)
                        thread.Start();
                    foreach (Thread thread in threads)
                        thread.Join();
                }
                try
                {
                    Assert.AreEqual(0, errors.Count(e => e != null), "round " + round + ": " + string.Join("; ", errors.Where(e => e != null)));
                    for (int i = 0; i < Threads; i++)
                    {
                        Assert.IsTrue(events[i].Set());
                        Assert.IsTrue(events[(i + 1) % Threads].WaitOne(0), "round " + round + ": a Set through event " + i + " did not reach event " + (i + 1) % Threads + ".");
                    }
                }
                finally
                {
                    foreach (TestEvent e in events.Where(e => e != null))
                        e.Dispose();
                }
                Assert.IsFalse(File.Exists(PerUserPath(name)), "round " + round);
                CollectionAssert.AreEqual(new string[0], TemporaryFiles(), "Temporary files left by the losers");
            }
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
            return BitConverter.ToInt32(Contents(path), 16);
        }

        static int State(string path)
        {
            return BitConverter.ToInt32(Contents(path), 12);
        }

        /// <summary>
        /// <c>FUTEX_WAKE</c> on the event's state word, from this test's own mapping of the file, waking every waiter. On macOS
        /// <c>os_sync_wake_by_address_any</c>, shared, which wakes one.
        /// </summary>
        /// <returns>How many waiters it woke.</returns>
        static unsafe long WakeStateWord(string path)
        {
            if (OperatingSystem.IsMacOS())
                return MacObjects.WakeOne(UnixBufferTests.Files.ObjectNamedBy(path), 12);
            long futex = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 98 : 202;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (var map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true))
            using (var view = map.CreateViewAccessor(0, EventSize, MemoryMappedFileAccess.ReadWrite))
            {
                byte* region = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref region);
                try
                {
                    long woken = Syscall(futex, (IntPtr)(region + view.PointerOffset + 12), 1 /* FUTEX_WAKE */, int.MaxValue, IntPtr.Zero, IntPtr.Zero, 0);
                    Assert.IsTrue(woken >= 0, "futex wake failed: " + Marshal.GetLastPInvokeErrorMessage());
                    return woken;
                }
                finally
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        static extern long Syscall(long number, IntPtr address, int operation, int value, IntPtr timeout, IntPtr address2, int value3);

        static void AssertWaiterCount(string path, int expected)
        {
            for (int i = 0; i < 100 && WaiterCount(path) != expected; i++)
                Thread.Sleep(50);
            Assert.AreEqual(expected, WaiterCount(path), "waiter count in " + path);
        }

        /// <summary>
        /// The event's region as a test sees it: on Linux the file's bytes; on macOS the first 64 bytes of the object the lock
        /// file names, or all of it if it is shorter. Read only while the event or its lock file is held, or nobody uses it:
        /// reading a lock file takes a shared flock for a moment.
        /// </summary>
        static byte[] Contents(string path)
        {
            if (OperatingSystem.IsMacOS())
                return MacObjects.Read(UnixBufferTests.Files.ObjectNamedBy(path), EventSize);
            return File.ReadAllBytes(path);
        }

        /// <summary>
        /// Puts an event file under <paramref name="path"/>, 0600, as a process killed after creating it would leave it. On
        /// macOS an object holding <paramref name="contents"/> (none at all for no bytes) and a lock file naming it.
        /// </summary>
        static void Plant(string path, byte[] contents)
        {
            if (OperatingSystem.IsMacOS())
            {
                UnixBufferTests.Files.MakeFolder(Path.GetFileName(path));
                contents = UnixBufferTests.Files.LockFile(MacObjects.Create(contents));
            }
            File.WriteAllBytes(path, contents);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        /// <summary>
        /// Removes what is under <paramref name="path"/>, and on macOS the object its lock file names.
        /// </summary>
        static void Remove(string path)
        {
            if (OperatingSystem.IsMacOS() && File.Exists(path))
                MacObjects.Unlink(UnixBufferTests.Files.ObjectNamedBy(path));
            File.Delete(path);
        }

        /// <summary>
        /// Holds a shared flock on the file, as a process with the event open does: FileStream takes one for FileShare.ReadWrite.
        /// On macOS the file is the lock file.
        /// </summary>
        static FileStream Hold(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        static Task<bool> Wait(TestEvent waiter)
        {
            return Task.Factory.StartNew(() => waiter.WaitOne(20000), TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// The event's file on Linux, or its lock file on macOS, for a per-user name.
        /// </summary>
        static string PerUserPath(string name)
        {
            if (OperatingSystem.IsMacOS())
                return UnixBufferTests.Files.MacFolder + ".SharedMemory-uid" + EffectiveUserId() + "/" + UnixBufferTests.Files.LockFileName(name);
            return Folder + "SharedMemory.u" + EffectiveUserId() + "." + name;
        }

        /// <summary>
        /// The event's file on Linux, or its lock file on macOS, for <c>Global\</c> and <paramref name="name"/>.
        /// </summary>
        static string GlobalPath(string name)
        {
            if (OperatingSystem.IsMacOS())
                return UnixBufferTests.Files.MacFolder + ".SharedMemory-global/" + UnixBufferTests.Files.LockFileName(name);
            return Folder + "SharedMemory.global." + name;
        }

        static string[] TemporaryFiles()
        {
            return Directory.GetFiles(Path.GetDirectoryName(PerUserPath("x")), ".SharedMemory.tmp.*");
        }

        static uint EffectiveUserId()
        {
            if (OperatingSystem.IsMacOS())
                return UnixBufferTests.Files.EffectiveUserId();
            string line = File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal));
            return uint.Parse(line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)[2]);
        }

        /// <summary>
        /// Event objects on macOS as the tests make and read them: created 0600 as the library creates them, the mode where a
        /// variadic argument goes on arm64, and woken with os_sync in shared mode.
        /// </summary>
        static unsafe class MacObjects
        {
            const string Library = "/usr/lib/libSystem.B.dylib";
            const int O_RDONLY = 0;
            const int O_RDWR = 0x2;
            const int O_CREAT = 0x200;
            const int O_EXCL = 0x800;
            const int PROT_READ = 1;
            const int PROT_WRITE = 2;
            const int MAP_SHARED = 1;
            const int ENOENT = 2;
            const uint OsSyncShared = 1;

            /// <summary>
            /// A new object holding <paramref name="contents"/>, 0600; sized only if there is anything to hold, so that no
            /// bytes give an object of 0 bytes.
            /// </summary>
            public static string Create(byte[] contents)
            {
                string objectName = UnixBufferTests.Files.NewObjectName();
                int fd = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? shm_open_variadic(objectName, O_RDWR | O_CREAT | O_EXCL, 0, 0, 0, 0, 0, 0, 0x180)
                    : shm_open(objectName, O_RDWR | O_CREAT | O_EXCL, 0x180);
                Assert.IsTrue(fd >= 0, "shm_open " + objectName + ": " + Marshal.GetLastPInvokeError());
                try
                {
                    if (contents.Length > 0)
                    {
                        Assert.AreEqual(0, ftruncate(fd, contents.Length), "ftruncate " + objectName + ": " + Marshal.GetLastPInvokeError());
                        IntPtr address = Map(fd, contents.Length, PROT_READ | PROT_WRITE, objectName);
                        Marshal.Copy(contents, 0, address, contents.Length);
                        munmap(address, (nuint)contents.Length);
                    }
                }
                finally
                {
                    close(fd);
                }
                return objectName;
            }

            /// <summary>
            /// The first <paramref name="count"/> bytes of the object, or all of it if it is shorter.
            /// </summary>
            public static byte[] Read(string objectName, int count)
            {
                int length = (int)Math.Min(count, UnixBufferTests.Files.StatusOf(objectName).Size);
                if (length == 0)
                    return new byte[0];
                int fd = Open(objectName, O_RDONLY);
                try
                {
                    IntPtr address = Map(fd, length, PROT_READ, objectName);
                    var bytes = new byte[length];
                    Marshal.Copy(address, bytes, 0, length);
                    munmap(address, (nuint)length);
                    return bytes;
                }
                finally
                {
                    close(fd);
                }
            }

            /// <summary>
            /// <c>os_sync_wake_by_address_any</c>, shared, on the word at <paramref name="offset"/> in the object.
            /// </summary>
            /// <returns>How many waiters it woke: 1, or 0 if nobody waited there.</returns>
            public static long WakeOne(string objectName, int offset)
            {
                int fd = Open(objectName, O_RDWR);
                try
                {
                    IntPtr address = Map(fd, EventSize, PROT_READ | PROT_WRITE, objectName);
                    try
                    {
                        if (os_sync_wake_by_address_any((byte*)address + offset, sizeof(int), OsSyncShared) == 0)
                            return 1;
                        Assert.AreEqual(ENOENT, Marshal.GetLastPInvokeError(), "os_sync_wake_by_address_any");
                        return 0;
                    }
                    finally
                    {
                        munmap(address, (nuint)EventSize);
                    }
                }
                finally
                {
                    close(fd);
                }
            }

            /// <summary>
            /// Removes the object, if it is there.
            /// </summary>
            public static void Unlink(string objectName)
            {
                if (shm_unlink(objectName) != 0)
                    Assert.AreEqual(ENOENT, Marshal.GetLastPInvokeError(), "shm_unlink " + objectName);
            }

            /// <summary>
            /// Whether a region of this process's address space holds <paramref name="address"/>: mach_vm_region gives the first
            /// region at or after an address.
            /// </summary>
            public static bool IsMapped(IntPtr address)
            {
                const int BasicInfo64 = 9;     // VM_REGION_BASIC_INFO_64
                const int KernSuccess = 0;
                const int KernInvalidAddress = 1;
                uint task = *(uint*)NativeLibrary.GetExport(NativeLibrary.Load(Library), "mach_task_self_");
                ulong start = (ulong)address;
                ulong size;
                int* info = stackalloc int[9];
                uint count = 9;                // VM_REGION_BASIC_INFO_COUNT_64
                uint objectName;
                int result = mach_vm_region(task, &start, &size, BasicInfo64, info, &count, &objectName);
                if (result == KernInvalidAddress)
                    return false;
                Assert.AreEqual(KernSuccess, result, "mach_vm_region");
                return start <= (ulong)address && (ulong)address < start + size;
            }

            static int Open(string objectName, int flags)
            {
                // Without O_CREAT, shm_open reads no mode, so its variadic declaration does not matter here.
                int fd = shm_open(objectName, flags, 0);
                Assert.IsTrue(fd >= 0, "shm_open " + objectName + ": " + Marshal.GetLastPInvokeError());
                return fd;
            }

            static IntPtr Map(int fd, int length, int protection, string objectName)
            {
                IntPtr address = mmap(IntPtr.Zero, (nuint)length, protection, MAP_SHARED, fd, 0);
                Assert.AreNotEqual(new IntPtr(-1), address, "mmap " + objectName + ": " + Marshal.GetLastPInvokeError());
                return address;
            }

            [DllImport(Library, SetLastError = true)] static extern int shm_open(string name, int flags, int mode);
            [DllImport(Library, EntryPoint = "shm_open", SetLastError = true)] static extern int shm_open_variadic(string name, int flags, long x2, long x3, long x4, long x5, long x6, long x7, long mode);
            [DllImport(Library, SetLastError = true)] static extern int shm_unlink(string name);
            [DllImport(Library, SetLastError = true)] static extern int ftruncate(int fd, long length);
            [DllImport(Library, SetLastError = true)] static extern IntPtr mmap(IntPtr address, nuint length, int protection, int flags, int fd, long offset);
            [DllImport(Library, SetLastError = true)] static extern int munmap(IntPtr address, nuint length);
            [DllImport(Library, SetLastError = true)] static extern int close(int fd);
            [DllImport(Library)] static extern int mach_vm_region(uint task, ulong* address, ulong* size, int flavor, int* info, uint* count, uint* objectName);
            [DllImport(Library, SetLastError = true)] static extern int os_sync_wake_by_address_any(void* address, nuint size, uint flags);
        }
    }
}
#endif
