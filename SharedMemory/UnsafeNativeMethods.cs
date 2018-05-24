// SharedMemory (File: SharedMemory\UnsafeNativeMethods.cs)
// Copyright (c) 2014 Justin Stenning
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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SharedMemory
{
    [System.Security.SuppressUnmanagedCodeSecurity]
    internal class UnsafeNativeMethods
    {
        private UnsafeNativeMethods() { }

#if !NETCORE
        /// <summary>
        /// Allow copying memory from one IntPtr to another. Required as the <see cref="System.Runtime.InteropServices.Marshal.Copy(System.IntPtr, System.IntPtr[], int, int)"/> implementation does not provide an appropriate override.
        /// </summary>
        /// <param name="dest"></param>
        /// <param name="src"></param>
        /// <param name="count"></param>
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        [SecurityCritical]
        internal static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        [SecurityCritical]
        internal static extern unsafe void CopyMemoryPtr(void* dest, void* src, uint count);
#endif

#if !NET40Plus

        [DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Auto, ExactSpelling = false)]
        [SecurityCritical]
        internal static extern int FormatMessage(int dwFlags, IntPtr lpSource, int dwMessageId, int dwLanguageId, StringBuilder lpBuffer, int nSize, IntPtr va_list_arguments);

        [SecurityCritical]
        internal static string GetMessage(int errorCode)
        {
            StringBuilder stringBuilder = new StringBuilder(512);
            if (UnsafeNativeMethods.FormatMessage(12800, IntPtr.Zero, errorCode, 0, stringBuilder, stringBuilder.Capacity, IntPtr.Zero) != 0)
            {
                return stringBuilder.ToString();
            }
            return string.Concat("UnknownError_Num ", errorCode);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_INFO
        {
            internal _PROCESSOR_INFO_UNION uProcessorInfo;
            public uint dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public IntPtr dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public ushort dwProcessorLevel;
            public ushort dwProcessorRevision;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct _PROCESSOR_INFO_UNION
        {
            [FieldOffset(0)]
            internal uint dwOemId;
            [FieldOffset(0)]
            internal ushort wProcessorArchitecture;
            [FieldOffset(2)]
            internal ushort wReserved;
        }

        [Flags]
        public enum FileMapAccess : uint
        {
            FileMapCopy = 0x0001,
            FileMapWrite = 0x0002,
            FileMapRead = 0x0004,
            FileMapAllAccess = 0x001f,
            FileMapExecute = 0x0020,
        }

        [Flags]
        internal enum FileMapProtection : uint
        {
            PageReadonly = 0x02,
            PageReadWrite = 0x04,
            PageWriteCopy = 0x08,
            PageExecuteRead = 0x20,
            PageExecuteReadWrite = 0x40,
            SectionCommit = 0x8000000,
            SectionImage = 0x1000000,
            SectionNoCache = 0x10000000,
            SectionReserve = 0x4000000,
        }
        /// <summary>
        /// Cannot create a file when that file already exists.
        /// </summary>
        internal const int ERROR_ALREADY_EXISTS = 0xB7; // 183
        /// <summary>
        /// The system cannot open the file.
        /// </summary>
        internal const int ERROR_TOO_MANY_OPEN_FILES = 0x4; // 4
        /// <summary>
        /// Access is denied.
        /// </summary>
        internal const int ERROR_ACCESS_DENIED = 0x5; // 5
        /// <summary>
        /// The system cannot find the file specified.
        /// </summary>
        internal const int ERROR_FILE_NOT_FOUND = 0x2; // 2

        [DllImport("kernel32.dll", CharSet = CharSet.None, SetLastError = true)]
        [SecurityCritical]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Auto, SetLastError = true, ThrowOnUnmappableChar = true)]
        [SecurityCritical]
        internal static extern SafeMemoryMappedFileHandle CreateFileMapping(SafeFileHandle hFile, IntPtr lpAttributes, FileMapProtection fProtect, int dwMaxSizeHi, int dwMaxSizeLo, string lpName);
        internal static SafeMemoryMappedFileHandle CreateFileMapping(SafeFileHandle hFile, FileMapProtection flProtect, Int64 ddMaxSize, string lpName)
        {
            int hi = (Int32)(ddMaxSize / Int32.MaxValue);
            int lo = (Int32)(ddMaxSize % Int32.MaxValue);
            return CreateFileMapping(hFile, IntPtr.Zero, flProtect, hi, lo, lpName);
        }

        [DllImport("kernel32.dll")]
        internal static extern void GetSystemInfo([MarshalAs(UnmanagedType.Struct)] ref SYSTEM_INFO lpSystemInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeMemoryMappedViewHandle MapViewOfFile(
            SafeMemoryMappedFileHandle hFileMappingObject,
            FileMapAccess dwDesiredAccess,
            UInt32 dwFileOffsetHigh,
            UInt32 dwFileOffsetLow,
            UIntPtr dwNumberOfBytesToMap);
        internal static SafeMemoryMappedViewHandle MapViewOfFile(SafeMemoryMappedFileHandle hFileMappingObject, FileMapAccess dwDesiredAccess, ulong ddFileOffset, UIntPtr dwNumberofBytesToMap)
        {
            uint hi = (UInt32)(ddFileOffset / UInt32.MaxValue);
            uint lo = (UInt32)(ddFileOffset % UInt32.MaxValue);
            return MapViewOfFile(hFileMappingObject, dwDesiredAccess, hi, lo, dwNumberofBytesToMap);
        }

        [DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Auto, SetLastError = true, ThrowOnUnmappableChar = true)]
        internal static extern SafeMemoryMappedFileHandle OpenFileMapping(
             uint dwDesiredAccess,
             bool bInheritHandle,
             string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

#endif
    }
}
