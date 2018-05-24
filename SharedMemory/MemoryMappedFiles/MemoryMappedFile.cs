// SharedMemory (File: SharedMemory\MemoryMappedFile.cs)
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
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Security.Permissions;
using System.Runtime;
using SharedMemory;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.IO.MemoryMappedFiles
{
#if !NET40Plus

    /// <summary>
    /// <para>Very limited .NET 3.5 implementation of a managed wrapper around memory-mapped files to reflect the .NET 4 API.</para>
    /// <para>Only those methods and features necessary for the SharedMemory library have been implemented.</para>
    /// </summary>
#if NETFULL
    [PermissionSet(SecurityAction.LinkDemand)]
#endif
    public sealed class MemoryMappedFile: IDisposable
    {
        SafeMemoryMappedFileHandle _handle;

        /// <summary>
        /// Gets the file handle of a memory-mapped file.
        /// </summary>
        /// <returns>The handle to the memory-mapped file.</returns>
        public SafeMemoryMappedFileHandle SafeMemoryMappedFileHandle
        {
            [SecurityCritical]
            [SecurityPermission(SecurityAction.Demand, Flags=SecurityPermissionFlag.UnmanagedCode)]
            get
            {
                return this._handle;
            }
        }

        private MemoryMappedFile(SafeMemoryMappedFileHandle handle)
        {
            this._handle = handle;
        }

        /// <summary>
        /// 
        /// </summary>
        ~MemoryMappedFile()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// Creates a new memory-mapped file, throwing an IOException if it already exists
        /// </summary>
        /// <param name="mapName"></param>
        /// <param name="capacity"></param>
        /// <returns></returns>
        public static MemoryMappedFile CreateNew(String mapName, long capacity)
        {
            if (String.IsNullOrEmpty(mapName))
                throw new ArgumentException("mapName cannot be null or empty.");
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException("capacity", "Value must be larger than 0.");
            if (IntPtr.Size == 4 && capacity > ((1024*1024*1024) * (long)4))
                throw new ArgumentOutOfRangeException("capacity", "The capacity cannot be greater than the size of the system's logical address space.");
            return new MemoryMappedFile(DoCreate(mapName, capacity));
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Interoperability", "CA1404:CallGetLastErrorImmediatelyAfterPInvoke"), SecurityCritical]
        [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        private static SafeMemoryMappedFileHandle DoCreate(string mapName, long capacity)
        {
            SafeFileHandle fileHandle = new SafeFileHandle(new IntPtr(-1), true);
            SafeMemoryMappedFileHandle safeHandle = null;

            safeHandle = UnsafeNativeMethods.CreateFileMapping(fileHandle, (UnsafeNativeMethods.FileMapProtection)MemoryMappedFileAccess.ReadWrite, capacity, mapName);
            var lastWin32Error = Marshal.GetLastWin32Error();
            if (!safeHandle.IsInvalid && (lastWin32Error == UnsafeNativeMethods.ERROR_ALREADY_EXISTS))
            {
                throw new System.IO.IOException(UnsafeNativeMethods.GetMessage(lastWin32Error));
            }
            else if (safeHandle.IsInvalid && lastWin32Error > 0)
            {
                throw new System.IO.IOException(UnsafeNativeMethods.GetMessage(lastWin32Error));
            }

            if (safeHandle == null || safeHandle.IsInvalid)
                throw new InvalidOperationException("Cannot create file mapping");

            return safeHandle;
        }

        /// <summary>
        /// Creates a new view accessor
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="size"></param>
        /// <param name="access"></param>
        /// <returns></returns>
        [SecurityCritical]
        [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        public MemoryMappedViewAccessor CreateViewAccessor(long offset, long size, MemoryMappedFileAccess access = MemoryMappedFileAccess.ReadWrite)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException("offset", "Value must be non-negative");
            if (size < 0)
                throw new ArgumentOutOfRangeException("size", "Value must be positive or zero for default size");
            if (IntPtr.Size == 4 && size > ((1024 * 1024 * 1024) * (long)4))
                throw new ArgumentOutOfRangeException("size", "The capacity cannot be greater than the size of the system's logical address space.");
            MemoryMappedView memoryMappedView = MemoryMappedView.CreateView(this._handle, access, offset, size);
            return new MemoryMappedViewAccessor(memoryMappedView);
        }

        /// <summary>
        /// Dispose pattern
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        void Dispose(bool disposeManagedResources)
        {
            if (this._handle != null && !this._handle.IsClosed)
            {
                this._handle.Dispose();
                this._handle = null;
            }
        }

        /// <summary>
        /// Opens an existing memory-mapped file. Throws FileNotFoundException if it doesn't exist.
        /// </summary>
        /// <param name="mapName"></param>
        /// <returns></returns>
        public static MemoryMappedFile OpenExisting(string mapName)
        {
            SafeMemoryMappedFileHandle safeMemoryMappedFileHandle = UnsafeNativeMethods.OpenFileMapping((uint)MemoryMappedFileRights.ReadWrite, false, mapName);
            int lastWin32Error = Marshal.GetLastWin32Error();
            if (safeMemoryMappedFileHandle.IsInvalid)
            {
                if (lastWin32Error == UnsafeNativeMethods.ERROR_FILE_NOT_FOUND)
                    throw new FileNotFoundException();
                throw new System.IO.IOException(UnsafeNativeMethods.GetMessage(lastWin32Error));
            }
            return new MemoryMappedFile(safeMemoryMappedFileHandle);
        }
    }

#endif
}
