using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// What a buffer leaves behind when nobody disposes it, and when its open fails. An owner that is dropped and collected
    /// releases its memory, as the handles behind it are finalised, on every OS; the tests look for its view where each OS shows
    /// it, by the address it was mapped at. A constructor whose open fails disposes the events it made.
    /// </summary>
    [TestClass]
    public class LeakTests
    {
        const string BufferKind = "BufferReadWrite";
        const string ArrayKind = "SharedArray";
        const string RingKind = "CircularBuffer";
        const int Size = 64 * 1024;
        const int FailedOpens = 1000;

        /// <summary>
        /// Windows: the owner's view is no longer mapped at its address, and its name no longer opens.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void NeverDisposed_OnWindows_ViewUnmappedAndNameGoneWhenCollected(string kind)
        {
            string name = Guid.NewGuid().ToString();

            IntPtr address = CreateAndDrop(kind, name, a => Assert.IsTrue(Windows.IsMappedView(a), "The view should be mapped before the owner is collected."));
            Collect();

            Assert.IsFalse(Windows.IsMappedView(address), "The view is still mapped.");
            Assert.ThrowsExactly<FileNotFoundException>(() => new EventFreeBuffer(name).Dispose());
        }

        /// <summary>
        /// The finaliser only lets the owner's memory go, and writes nothing to the shared memory: an opener in the same process
        /// sees no shutdown once the owner is collected, and goes on reading what the owner wrote.
        /// </summary>
        [TestMethod]
        public void NeverDisposed_OwnerCollected_OpenerSeesNoShutdownAndKeepsItsData()
        {
            string name = Guid.NewGuid().ToString();
            byte[] data = { 1, 2, 3, 4, 5, 6, 7, 8 };

            WeakReference owner;
            using (BufferReadWrite opener = CreateOpenAndDrop(name, data, out owner))
            {
                Collect();
                Assert.IsFalse(owner.IsAlive, "The owner should have been collected.");

                Assert.IsFalse(opener.ShuttingDown, "The collected owner's finaliser set Shutdown.");
                var read = new byte[data.Length];
                opener.Read(read);
                CollectionAssert.AreEqual(data, read);
            }
        }

#if NET
        /// <summary>
        /// Linux: /proc/self/maps no longer lists the owner's mapping of its /dev/shm file. The mapping is found by its address
        /// range, device and inode, since /proc/self/maps names a created file by the temporary name it was written under.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void NeverDisposed_OnLinux_UnmappedWhenCollected(string kind)
        {
            string name = Guid.NewGuid().ToString();

            string mapping = null;
            CreateAndDrop(kind, name, a => mapping = Linux.MappingAt(a));
            Assert.IsNotNull(mapping, "The owner's memory should be mapped from /dev/shm.");
            Collect();

            CollectionAssert.DoesNotContain(Linux.DevShmMappings(), mapping, "The owner's memory is still mapped.");
        }

        /// <summary>
        /// macOS: no region of the address space holds the owner's address any more, as mach_vm_region tells.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.OSX)]
        [System.Runtime.Versioning.SupportedOSPlatform("macos")] // mach_vm_region
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void NeverDisposed_OnMacOS_UnmappedWhenCollected(string kind)
        {
            string name = Guid.NewGuid().ToString();

            IntPtr address = CreateAndDrop(kind, name, a => Assert.IsTrue(UnixEventTests.MacObjects.IsMapped(a), "The owner's memory should be mapped."));
            Collect();

            Assert.IsFalse(UnixEventTests.MacObjects.IsMapped(address), "The owner's memory is still mapped.");
        }
#endif

        /// <summary>
        /// Opens of a name that does not exist fail with FileNotFoundException, and each disposes the two events its constructor
        /// made, so 1,000 of them leave none behind with no collection in between: on Windows neither event opens by name; on
        /// Linux neither has a file in /dev/shm; on macOS neither has a lock file.
        /// </summary>
        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void FailedOpens_LeaveNoEventBehind(string kind)
        {
            string name = Guid.NewGuid().ToString();

            for (int i = 0; i < FailedOpens; i++)
            {
                Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name));
                if (i == 0)
                    CollectionAssert.AreEqual(new string[0], EventsLeft(name), "Events left after the first failed open.");
            }

            CollectionAssert.AreEqual(new string[0], EventsLeft(name), "Events left after " + FailedOpens + " failed opens.");
        }

        static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// Creates an owner of <paramref name="kind"/> and drops it, in a method of its own so that nothing keeps it alive once it
        /// returns. <paramref name="whileAlive"/> sees the address while the owner is still held, so that a collection cannot
        /// release the owner before the test has looked at its mapping.
        /// </summary>
        /// <returns>The address its memory is mapped at.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static IntPtr CreateAndDrop(string kind, string name, Action<IntPtr> whileAlive)
        {
            SharedBuffer owner = Create(kind, name);
            IntPtr address = AddressOf(owner);
            whileAlive(address);
            GC.KeepAlive(owner);
            return address;
        }

        /// <summary>
        /// Creates an owner, writes <paramref name="data"/> through it, opens the buffer and drops the owner.
        /// </summary>
        /// <returns>The opener.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static BufferReadWrite CreateOpenAndDrop(string name, byte[] data, out WeakReference owner)
        {
            var created = new BufferReadWrite(name, Size);
            created.Write(data);
            owner = new WeakReference(created);
            return new BufferReadWrite(name);
        }

        static SharedBuffer Create(string kind, string name)
        {
            if (kind == ArrayKind)
                return new SharedArray<int>(name, Size / sizeof(int));
            if (kind == RingKind)
                return new CircularBuffer(name, 4, Size / 4);
            return new BufferReadWrite(name, Size);
        }

        static SharedBuffer Open(string kind, string name)
        {
            if (kind == ArrayKind)
                return new SharedArray<int>(name);
            return new BufferReadWrite(name);
        }

        /// <summary>
        /// The events of <paramref name="name"/>'s buffer that are still there: on Windows those that open by name, on Linux those
        /// with a file in /dev/shm, and on macOS those with a lock file.
        /// </summary>
        static string[] EventsLeft(string name)
        {
            string[] events = { name + "_evt_write", name + "_evt_read" };
#if NET
            if (OperatingSystem.IsLinux())
                return Directory.GetFiles("/dev/shm", "SharedMemory.*").Select(Path.GetFileName)
                    .Where(file => events.Any(e => file.EndsWith("." + e, StringComparison.Ordinal))).ToArray();
            if (OperatingSystem.IsMacOS())
                return Directory.GetFiles(UnixBufferTests.Files.MacFolder + ".SharedMemory-uid" + UnixBufferTests.Files.EffectiveUserId())
                    .Select(Path.GetFileName).Where(file => events.Any(e => file.StartsWith(e + "~", StringComparison.Ordinal))).ToArray();
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
#endif
            var left = new System.Collections.Generic.List<string>();
            foreach (string eventName in events)
            {
                EventWaitHandle handle;
                if (EventWaitHandle.TryOpenExisting(eventName, out handle))
                {
                    handle.Dispose();
                    left.Add(eventName);
                }
            }
            return left.ToArray();
        }

        /// <summary>
        /// Where the buffer's memory starts: its protected ViewPtr, read through reflection.
        /// </summary>
        internal static unsafe IntPtr AddressOf(SharedBuffer buffer)
        {
            object pointer = typeof(SharedBuffer).GetField("ViewPtr", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(buffer);
            return new IntPtr(Pointer.Unbox(pointer));
        }

        /// <summary>
        /// Whether a buffer's memory is still mapped at <paramref name="address"/>, from <see cref="AddressOf"/>, where each OS
        /// shows it: VirtualQuery on Windows, /proc/self/maps on Linux and mach_vm_region on macOS.
        /// </summary>
        internal static bool IsMapped(IntPtr address)
        {
#if NET
            if (OperatingSystem.IsLinux())
                return Linux.MappingAt(address) != null;
            if (OperatingSystem.IsMacOS())
                return UnixEventTests.MacObjects.IsMapped(address);
#endif
            return Windows.IsMappedView(address);
        }

        static class Windows
        {
            const uint MemMapped = 0x40000;

            /// <summary>
            /// Whether a view of a file mapping starts at <paramref name="address"/>.
            /// </summary>
            public static bool IsMappedView(IntPtr address)
            {
                MemoryBasicInformation info;
                Assert.AreNotEqual(UIntPtr.Zero, VirtualQuery(address, out info, new UIntPtr((uint)Marshal.SizeOf(typeof(MemoryBasicInformation)))), "VirtualQuery");
                return info.Type == MemMapped && info.AllocationBase == address;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct MemoryBasicInformation
            {
                public IntPtr BaseAddress;
                public IntPtr AllocationBase;
                public uint AllocationProtect;
                public UIntPtr RegionSize;
                public uint State;
                public uint Protect;
                public uint Type;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);
        }

#if NET
        static class Linux
        {
            /// <summary>
            /// The mapping of a /dev/shm file that starts at <paramref name="address"/>, as its address range, device and inode, or null.
            /// </summary>
            public static string MappingAt(IntPtr address)
            {
                string start = ((ulong)address).ToString("x") + "-";
                return DevShmMappings().FirstOrDefault(m => m.StartsWith(start, StringComparison.Ordinal));
            }

            /// <summary>
            /// This process's mappings of files in /dev/shm, each as its address range, device and inode.
            /// </summary>
            public static string[] DevShmMappings()
            {
                return File.ReadAllLines("/proc/self/maps").Where(l => l.Contains("/dev/shm/"))
                    .Select(l => l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    .Select(f => f[0] + " " + f[3] + " " + f[4]).ToArray();
            }
        }
#endif
    }
}
