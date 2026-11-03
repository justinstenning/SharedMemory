using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// What a buffer leaves behind when nobody disposes it. An owner that is dropped and collected releases its memory, as the
    /// handles behind it are finalised, on every OS; the tests look for its view where each OS shows it, by the address it
    /// was mapped at.
    /// </summary>
    [TestClass]
    public class LeakTests
    {
        const string BufferKind = "BufferReadWrite";
        const string ArrayKind = "SharedArray";
        const string RingKind = "CircularBuffer";
        const int Size = 64 * 1024;

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

            IntPtr address = CreateAndDrop(kind, name);
            Assert.IsTrue(Windows.IsMappedView(address), "The view should be mapped before the owner is collected.");
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

            string mapping = Linux.MappingAt(CreateAndDrop(kind, name));
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

            IntPtr address = CreateAndDrop(kind, name);
            Assert.IsTrue(UnixEventTests.MacObjects.IsMapped(address), "The owner's memory should be mapped.");
            Collect();

            Assert.IsFalse(UnixEventTests.MacObjects.IsMapped(address), "The owner's memory is still mapped.");
        }
#endif

        static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// Creates an owner of <paramref name="kind"/> and drops it, in a method of its own so that nothing keeps it alive.
        /// </summary>
        /// <returns>The address its memory is mapped at.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static IntPtr CreateAndDrop(string kind, string name)
        {
            return AddressOf(Create(kind, name));
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

        /// <summary>
        /// Where the buffer's memory starts: its protected ViewPtr, read through reflection.
        /// </summary>
        static unsafe IntPtr AddressOf(SharedBuffer buffer)
        {
            object pointer = typeof(SharedBuffer).GetField("ViewPtr", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(buffer);
            return new IntPtr(Pointer.Unbox(pointer));
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
