// SharedMemory (File: SharedMemory\MemoryMappedFileAccess.cs)
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
using System.Text;

namespace System.IO.MemoryMappedFiles
{
#if !NET40Plus
    /// <summary>
    /// Used when creating a memory mapped file
    /// </summary>
    public enum MemoryMappedFileAccess: uint
    {
        /// <summary>
        /// Read
        /// </summary>
        Read = 2,
        /// <summary>
        /// Read/Write
        /// </summary>
        ReadWrite = 4,
        /// <summary>
        /// CopyOnWrite
        /// </summary>
        CopyOnWrite = 8,
        /// <summary>
        /// Read Execute
        /// </summary>
        ReadExecute = 32,
        /// <summary>
        /// Read/Write Execute
        /// </summary>
        ReadWriteExecute = 64
    }

    internal static class MemoryMappedFileAccessExtensions
    {
        internal static UnsafeNativeMethods.FileMapAccess ToMapViewFileAccess(this MemoryMappedFileAccess access)
        {
            switch (access)
            {
                case MemoryMappedFileAccess.Read:
                    return UnsafeNativeMethods.FileMapAccess.FileMapRead;
                case MemoryMappedFileAccess.ReadWrite:
                    return UnsafeNativeMethods.FileMapAccess.FileMapRead | UnsafeNativeMethods.FileMapAccess.FileMapWrite;
                case MemoryMappedFileAccess.ReadExecute:
                    return UnsafeNativeMethods.FileMapAccess.FileMapRead | UnsafeNativeMethods.FileMapAccess.FileMapExecute;
                case MemoryMappedFileAccess.ReadWriteExecute:
                    return UnsafeNativeMethods.FileMapAccess.FileMapRead | UnsafeNativeMethods.FileMapAccess.FileMapWrite | UnsafeNativeMethods.FileMapAccess.FileMapExecute;
                default:
                    return UnsafeNativeMethods.FileMapAccess.FileMapAllAccess;
            }
        }
    }
#endif
}
