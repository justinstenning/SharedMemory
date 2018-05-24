// SharedMemory (File: SharedMemory\safememorymappedviewhandle.cs)
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
using SharedMemory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Permissions;
using System.Text;

namespace Microsoft.Win32.SafeHandles
{
#if !NET40Plus
    /// <summary>
    /// Provides a safe handle that represents a view of a block of unmanaged memory for random access.
    /// </summary>
#if NETFULL
    [PermissionSet(SecurityAction.LinkDemand)]
#endif
    public sealed class SafeMemoryMappedViewHandle: SafeHandleZeroOrMinusOneIsInvalid
    {
        [SecurityPermission(SecurityAction.LinkDemand, UnmanagedCode = true)]
        internal SafeMemoryMappedViewHandle()
            : base(true)
        {
        }

        [SecurityPermission(SecurityAction.LinkDemand, UnmanagedCode = true)]
        internal SafeMemoryMappedViewHandle(IntPtr handle, bool ownsHandle)
            : base(ownsHandle)
        {
            base.SetHandle(handle);
        }

        /// <summary>
        /// Unmap's the view of the file
        /// </summary>
        /// <returns></returns>
        protected override bool ReleaseHandle()
        {
            try
            {
                return UnsafeNativeMethods.UnmapViewOfFile(this.handle);
            }
            finally
            {
                this.handle = IntPtr.Zero;
            }
            
        }

        /// <summary>
        /// Acquires a reference to the pointer, incrementing the internal ref count. Should be followed by corresponding call to <see cref="ReleasePointer"/>
        /// </summary>
        /// <param name="pointer"></param>
        public unsafe void AcquirePointer(ref byte* pointer)
        {
            bool flag = false;
            base.DangerousAddRef(ref flag);
            pointer = (byte*)this.handle.ToPointer();
        }

        /// <summary>
        /// Release the pointer
        /// </summary>
        public void ReleasePointer()
        {
            base.DangerousRelease();
        }
    }
#endif
}
