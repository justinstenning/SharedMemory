// SharedMemory (File: SharedMemory\LinuxNativeMethods.cs)
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
    /// The C library calls the Linux backend makes, on x64 and arm64. The constants have the same values on both.
    /// </summary>
    internal static unsafe partial class LinuxNativeMethods
    {
        internal const int O_RDWR = 0x2;
        internal const int O_CLOEXEC = 0x80000;

        internal const int LOCK_SH = 1;
        internal const int LOCK_EX = 2;
        internal const int LOCK_NB = 4;

        internal const int EPERM = 1;
        internal const int ENOENT = 2;
        internal const int EINTR = 4;
        internal const int EACCES = 13;
        internal const int EEXIST = 17;
        internal const int EWOULDBLOCK = 11;
        internal const int EAGAIN = 11;
        internal const int ETIMEDOUT = 110;

        /// <summary>
        /// <c>FUTEX_WAIT</c> and <c>FUTEX_WAKE</c>, without <c>FUTEX_PRIVATE_FLAG</c>: the word is in a file that other processes map.
        /// </summary>
        internal const int FUTEX_WAIT = 0;
        internal const int FUTEX_WAKE = 1;

        internal const int AT_FDCWD = -100;
        internal const int AT_EMPTY_PATH = 0x1000;

        /// <summary>
        /// STATX_TYPE | STATX_MODE | STATX_UID | STATX_INO | STATX_SIZE: the fields <see cref="Statx"/> reads.
        /// </summary>
        internal const uint StatxFields = 0x1 | 0x2 | 0x8 | 0x100 | 0x200;

        /// <summary>
        /// The fields of <c>struct statx</c> the backend reads. Its layout is the same on every architecture.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        internal struct Statx
        {
            [FieldOffset(0)] internal uint Mask;
            [FieldOffset(20)] internal uint Uid;
            [FieldOffset(28)] internal ushort Mode;
            [FieldOffset(32)] internal ulong Inode;
            [FieldOffset(40)] internal ulong Size;
            [FieldOffset(136)] internal uint DeviceMajor;
            [FieldOffset(140)] internal uint DeviceMinor;
        }

        /// <summary>
        /// <c>open(path, flags)</c>. C declares it variadic; the mode argument is read only with <c>O_CREAT</c> or <c>O_TMPFILE</c>, which these calls never pass.
        /// </summary>
        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, int flags);

        [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
        internal static partial int Flock(SafeFileHandle fd, int operation);

        [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Link(string oldPath, string newPath);

        [LibraryImport("libc", EntryPoint = "unlink", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Unlink(string path);

        /// <summary>
        /// <c>statx(dirfd, path, flags, mask, buffer)</c> on a path.
        /// </summary>
        [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int StatxPath(int dirfd, string path, int flags, uint mask, Statx* buffer);

        /// <summary>
        /// <c>statx(fd, "", AT_EMPTY_PATH, mask, buffer)</c> on an open descriptor, when called with an empty path and <see cref="AT_EMPTY_PATH"/>.
        /// </summary>
        [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int StatxDescriptor(SafeFileHandle fd, string path, int flags, uint mask, Statx* buffer);

        [LibraryImport("libc", EntryPoint = "geteuid")]
        internal static partial uint GetEffectiveUserId();

        /// <summary>
        /// <c>struct timespec</c> on 64-bit Linux: a relative timeout for <c>FUTEX_WAIT</c>, measured on the monotonic clock.
        /// </summary>
        internal struct Timespec
        {
            internal long Seconds;
            internal long Nanoseconds;
        }

        /// <summary>
        /// The number of the <c>futex</c> system call: 202 on x64, 98 on arm64.
        /// </summary>
        /// <exception cref="System.PlatformNotSupportedException">Any other architecture.</exception>
        internal static long FutexSyscall
        {
            get
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X64:
                        return 202;
                    case Architecture.Arm64:
                        return 98;
                    default:
                        throw PlatformSupport.NotSupported("named events");
                }
            }
        }

        /// <summary>
        /// <c>syscall(number, address, operation, value, timeout, address2, value3)</c>, with <paramref name="number"/> from <see cref="FutexSyscall"/>.
        /// C declares <c>syscall</c> variadic. On Linux x64 and arm64 variadic integer and pointer arguments go in the same registers, and the seventh
        /// on the stack, as fixed ones do, and glibc's <c>syscall</c> only moves them into place, so this fixed declaration makes the same call.
        /// </summary>
        [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
        internal static partial long Futex(long number, int* address, int operation, int value, Timespec* timeout, int* address2, int value3);
    }
}
#endif
