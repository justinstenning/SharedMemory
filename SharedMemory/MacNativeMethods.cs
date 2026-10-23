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
    }
}
#endif
