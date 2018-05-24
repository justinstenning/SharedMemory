// SharedMemory (File: SharedMemory\MemoryMappedView.cs)
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
using Microsoft.Win32.SafeHandles;
using SharedMemory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Text;

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
    public sealed class MemoryMappedView : IDisposable
    {
        SafeMemoryMappedViewHandle _handle;
        
        /// <summary>
        /// 
        /// </summary>
        public SafeMemoryMappedViewHandle SafeMemoryMappedViewHandle
        {
            get { return this._handle; }
        }

        long _size;
        long _offset;

        /// <summary>
        /// The size of the view (from offset to end)
        /// </summary>
        public long Size { get { return _size; } }
        
        /// <summary>
        /// The start of the view (the handle itself will be aligned based on the allocation granularity)
        /// http://msdn.microsoft.com/en-us/library/windows/desktop/aa366548(v=vs.85).aspx
        /// </summary>
        public long ViewStartOffset { get { return _offset; } }

        private MemoryMappedView(SafeMemoryMappedViewHandle handle, long offset, long size)
        {
            this._handle = handle;
            this._offset = offset;
            this._size = size;
        }

        /// <summary>
        /// 
        /// </summary>
        ~MemoryMappedView()
        {
            Dispose(false);
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
                this._handle.Dispose();
            this._handle = null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Interoperability", "CA1404:CallGetLastErrorImmediatelyAfterPInvoke")]
        internal static MemoryMappedView CreateView(SafeMemoryMappedFileHandle safeMemoryMappedFileHandle, MemoryMappedFileAccess access, long offset, long size)
        {
            // http://msdn.microsoft.com/en-us/library/windows/desktop/aa366548(v=vs.85).aspx
            UnsafeNativeMethods.SYSTEM_INFO info = new UnsafeNativeMethods.SYSTEM_INFO();
            UnsafeNativeMethods.GetSystemInfo(ref info);

            // To calculate where to start the file mapping, round down the
            // offset of the data into the memory-mapped file to the nearest multiple of the
            // system allocation granularity.
            long fileMapStart = (offset / info.dwAllocationGranularity) * info.dwAllocationGranularity;
            // How large will the file mapping object be?
            long mapViewSize = (offset % info.dwAllocationGranularity) + size;
            // The data of interest is not necessarily at the beginning of the
            // view, so determine how far into the view to set the pointer.
            long viewDelta = offset - fileMapStart;

            SafeMemoryMappedViewHandle safeHandle = UnsafeNativeMethods.MapViewOfFile(safeMemoryMappedFileHandle, access.ToMapViewFileAccess(), (ulong)fileMapStart, new UIntPtr((ulong)mapViewSize));
            var lastWin32Error = Marshal.GetLastWin32Error();
            if (safeHandle.IsInvalid)
            {
                if (lastWin32Error == UnsafeNativeMethods.ERROR_FILE_NOT_FOUND)
                    throw new FileNotFoundException();
                throw new System.IO.IOException(UnsafeNativeMethods.GetMessage(lastWin32Error));
            }

            return new MemoryMappedView(safeHandle, viewDelta, size);
        }
    }
#endif
}
