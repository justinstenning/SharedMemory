// SharedMemory (File: SharedMemory\MemoryMappedViewAccessor.cs)
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
using System.Security;
using System.Security.Permissions;
using System.Text;

namespace System.IO.MemoryMappedFiles
{
#if !NET40Plus
    /// <summary>
    /// 
    /// </summary>
#if NETFULL
    [PermissionSet(SecurityAction.LinkDemand)]
#endif
    public sealed class MemoryMappedViewAccessor : IDisposable
    {
        MemoryMappedView _view;

        internal MemoryMappedViewAccessor(MemoryMappedView memoryMappedView)
        {
            this._view = memoryMappedView;
        }
        
        /// <summary>
        /// 
        /// </summary>
        public SafeMemoryMappedViewHandle SafeMemoryMappedViewHandle
        {
            [SecurityCritical]
            [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.UnmanagedCode)]
            get
            {
                return this._view.SafeMemoryMappedViewHandle;
            }
        }

        /// <summary>
        /// Dispose pattern
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposeManagedResources)
        {
            if (_view != null)
                _view.Dispose();
            _view = null;
        }

        internal static unsafe void PtrToStructure<T>(byte* ptr, out T structure)
            where T : struct
        {
            structure = FastStructure.PtrToStructure<T>((IntPtr)ptr);
            //var tr = __makeref(structure);
            //*(IntPtr*)&tr = (IntPtr)ptr;
            //structure = __refvalue( tr,T);
        }

        internal static unsafe void StructureToPtr<T>(ref T structure, byte* ptr)
            where T : struct
        {
            FastStructure.StructureToPtr<T>(ref structure, (IntPtr)ptr);
        }

        internal unsafe void Write<T>(long position, ref T structure)
            where T: struct
        {
            uint elementSize = (uint)Marshal.SizeOf(typeof(T));
            if (position > this._view.Size - elementSize)
                throw new ArgumentOutOfRangeException("position", "");

            try
            {
                byte* ptr = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                ptr += +_view.ViewStartOffset + position;
                StructureToPtr(ref structure, ptr);
            }
            finally
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        internal unsafe void WriteArray<T>(long position, T[] buffer, int index, int count)
            where T : struct
        {
            uint elementSize = (uint)Marshal.SizeOf(typeof(T));

            if (position > this._view.Size - (elementSize * count))
                throw new ArgumentOutOfRangeException("position");
            
            try
            {
                byte* ptr = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                ptr += _view.ViewStartOffset + position;

                FastStructure.WriteArray<T>((IntPtr)ptr, buffer, index, count);

                //for (var i = 0; i < count; i++)
                //{
                //    StructureToPtr(ref buffer[index + i], ptr + (i * elementSize));
                //}
            }
            finally
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        internal unsafe void Read<T>(long position, out T structure)
            where T: struct
        {
            uint size = (uint)Marshal.SizeOf(typeof(T));
            if (position > this._view.Size - size)
                throw new ArgumentOutOfRangeException("position", "");
            try
            {
                byte* ptr = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                ptr += _view.ViewStartOffset + position;
                PtrToStructure(ptr, out structure);
            }
            finally
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        internal unsafe void ReadArray<T>(long position, T[] buffer, int index, int count)
            where T : struct
        {
            uint elementSize = (uint)FastStructure.SizeOf<T>();

            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (position > this._view.Size - (elementSize * count))
                throw new ArgumentOutOfRangeException("position");
            try
            {
                byte* ptr = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                ptr += _view.ViewStartOffset + position;

                FastStructure.ReadArray<T>(buffer, (IntPtr)ptr, index, count);
                
                //for (var i = 0; i < count; i++)
                //{
                //    PtrToStructure(ptr + (i * elementSize), out buffer[index + i]);
                //}
            }
            finally
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }
#endif
}
