// SharedMemory (File: SharedMemory\MemoryMappedFileRights.cs)
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
using System.Text;

namespace System.IO.MemoryMappedFiles
{
#if !NET40Plus
    /// <summary>
    /// Used for opening a memory-mapped file
    /// </summary>
    [Flags]
    public enum MemoryMappedFileRights: uint
    {
        /// <summary>The right to add data to a file or remove data from a file.</summary>
        Write = 0x02,
        /// <summary>The right to open and copy a file as read-only.</summary>
        Read = 0x04,
        /// <summary>The right to open and copy a file, and the right to add data to a file or remove data from a file.</summary>
        ReadWrite = MemoryMappedFileRights.Write | MemoryMappedFileRights.Read,
    }
#endif
}
