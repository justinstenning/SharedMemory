// SharedMemory (File: SharedMemory\MacNativeMethods.cs)
// Copyright (c) 2026 Justin Stenning
// http://spazzarama.com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// The SharedMemory library is inspired by the following Code Project article:
//   "Fast IPC Communication Using Shared Memory and InterlockedCompareExchange"
//   http://www.codeproject.com/Articles/14740/Fast-IPC-Communication-Using-Shared-Memory-and-Int

#if NET10_0_OR_GREATER
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharedMemory
{
    /// <summary>
    /// The C library calls the macOS backend makes, with Darwin's values, which differ from Linux's. Run on arm64; x64 takes the entry points and
    /// argument passing chosen for it below and has not been run.
    /// </summary>
    internal static unsafe partial class MacNativeMethods
    {
        const string Library = "/usr/lib/libSystem.B.dylib";

        internal const int O_RDWR = 0x2;
        internal const int O_CREAT = 0x200;
        internal const int O_EXCL = 0x800;
        internal const int O_CLOEXEC = 0x1000000;

        internal const int LOCK_SH = 1;
        internal const int LOCK_EX = 2;
        internal const int LOCK_NB = 4;
        internal const int LOCK_UN = 8;

        internal const int PROT_READ = 1;
        internal const int PROT_WRITE = 2;
        internal const int MAP_SHARED = 1;

        internal const int EPERM = 1;
        internal const int ENOENT = 2;
        internal const int EINTR = 4;
        internal const int EACCES = 13;
        internal const int EEXIST = 17;
        internal const int EWOULDBLOCK = 35;
        internal const int ETIMEDOUT = 60;

        /// <summary>
        /// The fields of Darwin's <c>struct stat</c> the backend reads, in the 64-bit inode layout (144 bytes) that arm64's <c>stat</c> functions and
        /// x64's <c>$INODE64</c> ones fill. An <c>shm_open</c> object's <c>st_mode</c> has no type bits, and its <c>st_ino</c> and <c>st_dev</c> are 0.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 144)]
        internal struct Stat
        {
            [FieldOffset(0)] internal int Device;
            [FieldOffset(4)] internal ushort Mode;
            [FieldOffset(8)] internal ulong Inode;
            [FieldOffset(16)] internal uint Uid;
            [FieldOffset(96)] internal long Size;
        }

        /// <summary>
        /// <c>shm_open(name, flags, mode)</c>. C declares it variadic, and on Apple arm64 a variadic argument goes on the stack: a mode passed in the third
        /// register never reaches it, and the object gets whatever the stack held. Six padding arguments fill the remaining argument registers, so the
        /// mode goes in the first stack slot, where the callee looks. On x64, variadic and fixed integer arguments travel alike, in registers.
        /// </summary>
        internal static int ShmOpen(string name, int flags, int mode)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                return ShmOpenArm64(name, flags, 0, 0, 0, 0, 0, 0, mode);
            return ShmOpenX64(name, flags, mode);
        }

        /// <summary>
        /// <c>open(path, flags, mode)</c>, passing the mode as <see cref="ShmOpen"/> does, for the same reason.
        /// </summary>
        internal static int Open(string path, int flags, int mode)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                return OpenArm64(path, flags, 0, 0, 0, 0, 0, 0, mode);
            return OpenX64(path, flags, mode);
        }

        [LibraryImport(Library, EntryPoint = "shm_open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int ShmOpenArm64(string name, int flags, long x2, long x3, long x4, long x5, long x6, long x7, long mode);

        [LibraryImport(Library, EntryPoint = "shm_open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int ShmOpenX64(string name, int flags, int mode);

        [LibraryImport(Library, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int OpenArm64(string path, int flags, long x2, long x3, long x4, long x5, long x6, long x7, long mode);

        [LibraryImport(Library, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int OpenX64(string path, int flags, int mode);

        [LibraryImport(Library, EntryPoint = "shm_unlink", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int ShmUnlink(string name);

        [LibraryImport(Library, EntryPoint = "ftruncate", SetLastError = true)]
        internal static partial int Ftruncate(int fd, long length);

        [LibraryImport(Library, EntryPoint = "mmap", SetLastError = true)]
        internal static partial nint Mmap(nint address, nuint length, int protection, int flags, int fd, long offset);

        [LibraryImport(Library, EntryPoint = "munmap", SetLastError = true)]
        internal static partial int Munmap(nint address, nuint length);

        [LibraryImport(Library, EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int fd);

        [LibraryImport(Library, EntryPoint = "flock", SetLastError = true)]
        internal static partial int Flock(SafeFileHandle fd, int operation);

        [LibraryImport(Library, EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Link(string oldPath, string newPath);

        [LibraryImport(Library, EntryPoint = "unlink", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Unlink(string path);

        [LibraryImport(Library, EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Mkdir(string path, ushort mode);

        [LibraryImport(Library, EntryPoint = "chmod", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Chmod(string path, ushort mode);

        [LibraryImport(Library, EntryPoint = "geteuid")]
        internal static partial uint GetEffectiveUserId();

        /// <summary>
        /// <c>fstat</c>, in the 64-bit inode layout: <c>fstat</c> on arm64, <c>fstat$INODE64</c> on x64, whose plain <c>fstat</c> has the old layout.
        /// </summary>
        internal static int FileStatus(int fd, Stat* status)
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? FstatArm64(fd, status) : FstatX64(fd, status);
        }

        /// <summary>
        /// <c>fstat</c> on a <see cref="SafeFileHandle"/>, as <see cref="FileStatus(int, Stat*)"/>.
        /// </summary>
        internal static int FileStatus(SafeFileHandle fd, Stat* status)
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? FstatArm64(fd, status) : FstatX64(fd, status);
        }

        /// <summary>
        /// <c>stat</c>, which follows a symbolic link, in the 64-bit inode layout.
        /// </summary>
        internal static int PathStatus(string path, Stat* status)
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? StatArm64(path, status) : StatX64(path, status);
        }

        /// <summary>
        /// <c>lstat</c>, which describes a symbolic link itself, in the 64-bit inode layout.
        /// </summary>
        internal static int LinkStatus(string path, Stat* status)
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? LstatArm64(path, status) : LstatX64(path, status);
        }

        [LibraryImport(Library, EntryPoint = "fstat", SetLastError = true)]
        private static partial int FstatArm64(int fd, Stat* status);

        [LibraryImport(Library, EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static partial int FstatX64(int fd, Stat* status);

        [LibraryImport(Library, EntryPoint = "fstat", SetLastError = true)]
        private static partial int FstatArm64(SafeFileHandle fd, Stat* status);

        [LibraryImport(Library, EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static partial int FstatX64(SafeFileHandle fd, Stat* status);

        [LibraryImport(Library, EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int StatArm64(string path, Stat* status);

        [LibraryImport(Library, EntryPoint = "stat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int StatX64(string path, Stat* status);

        [LibraryImport(Library, EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int LstatArm64(string path, Stat* status);

        [LibraryImport(Library, EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int LstatX64(string path, Stat* status);

        /// <summary>
        /// The <c>os_sync</c> calls that wait on a word in memory and wake its waiters, which macOS 14.4 added. Looked up once in libSystem, so that an
        /// older macOS gets a <see cref="System.PlatformNotSupportedException"/> naming them rather than an <see cref="System.EntryPointNotFoundException"/>
        /// partway through a wait. Every call passes the shared flag, without which no other process's wake reaches the waiter. A failed call returns -1,
        /// with errno, read straight after the call as <c>LibraryImport</c>'s generated code reads it.
        /// </summary>
        internal static class OsSync
        {
            /// <summary>
            /// <c>OS_SYNC_WAIT_ON_ADDRESS_SHARED</c> and <c>OS_SYNC_WAKE_BY_ADDRESS_SHARED</c>: the word is in memory other processes map.
            /// </summary>
            const uint Shared = 1;

            /// <summary>
            /// <c>OS_CLOCK_MACH_ABSOLUTE_TIME</c>, the only clock the timed wait takes. Its timeout is relative, in nanoseconds, and 0 is refused.
            /// </summary>
            const uint MachAbsoluteTime = 32;

            /// <summary>
            /// The functions, in the order <see cref="Missing"/> takes their addresses.
            /// </summary>
            internal static readonly string[] Names =
            {
                "os_sync_wait_on_address", "os_sync_wait_on_address_with_timeout", "os_sync_wake_by_address_any", "os_sync_wake_by_address_all",
            };

            static readonly delegate* unmanaged<void*, ulong, nuint, uint, int> WaitOnAddress;
            static readonly delegate* unmanaged<void*, ulong, nuint, uint, uint, ulong, int> WaitOnAddressWithTimeout;
            static readonly delegate* unmanaged<void*, nuint, uint, int> WakeAny;
            static readonly delegate* unmanaged<void*, nuint, uint, int> WakeAll;

            static OsSync()
            {
                var exports = new nint[Names.Length];
                nint library;
                if (NativeLibrary.TryLoad(Library, out library))
                {
                    for (int i = 0; i < Names.Length; i++)
                        NativeLibrary.TryGetExport(library, Names[i], out exports[i]);
                }
                Unsupported = Missing(exports);
                if (Unsupported != null)
                    return;
                WaitOnAddress = (delegate* unmanaged<void*, ulong, nuint, uint, int>)exports[0];
                WaitOnAddressWithTimeout = (delegate* unmanaged<void*, ulong, nuint, uint, uint, ulong, int>)exports[1];
                WakeAny = (delegate* unmanaged<void*, nuint, uint, int>)exports[2];
                WakeAll = (delegate* unmanaged<void*, nuint, uint, int>)exports[3];
            }

            /// <summary>
            /// Why named events cannot work here, or null if every function was found.
            /// </summary>
            internal static string Unsupported { get; private set; }

            /// <summary>
            /// Which of <see cref="Names"/> have no address in <paramref name="exports"/>, as the reason events are not supported, or null if none.
            /// </summary>
            internal static string Missing(nint[] exports)
            {
                var missing = new System.Collections.Generic.List<string>();
                for (int i = 0; i < Names.Length; i++)
                {
                    if (exports[i] == 0)
                        missing.Add(Names[i]);
                }
                return missing.Count == 0 ? null : "libSystem lacks " + string.Join(", ", missing) + ", which macOS 14.4 added";
            }

            /// <summary>
            /// Sleeps while the 4-byte word at <paramref name="address"/> holds <paramref name="value"/>, until a wake, a signal, or
            /// <paramref name="nanoseconds"/> pass; 0 nanoseconds waits without a timeout.
            /// </summary>
            /// <param name="error">errno when the call returns -1, else 0.</param>
            /// <returns>0 or more once woken or if the word already differs; -1 on a timeout (<see cref="ETIMEDOUT"/>), a signal (<see cref="EINTR"/>), or a failure.</returns>
            internal static int Wait(int* address, int value, ulong nanoseconds, out int error)
            {
                int result;
                if (nanoseconds == 0)
                    result = WaitOnAddress(address, (uint)value, sizeof(int), Shared);
                else
                    result = WaitOnAddressWithTimeout(address, (uint)value, sizeof(int), Shared, MachAbsoluteTime, nanoseconds);
                error = result < 0 ? Marshal.GetLastSystemError() : 0;
                return result;
            }

            /// <summary>
            /// Wakes one waiter on the word at <paramref name="address"/>, or all of them.
            /// </summary>
            /// <param name="error">errno when the call returns -1, else 0.</param>
            /// <returns>0 or more; -1 with <see cref="ENOENT"/> if nobody waited there, or on a failure.</returns>
            internal static int Wake(int* address, bool all, out int error)
            {
                int result = all ? WakeAll(address, sizeof(int), Shared) : WakeAny(address, sizeof(int), Shared);
                error = result < 0 ? Marshal.GetLastSystemError() : 0;
                return result;
            }
        }
    }
}
#endif
