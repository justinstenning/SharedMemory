// SharedMemory (File: SharedMemory\SharedHeader.cs)
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
using System.Text;

namespace SharedMemory
{
    /// <summary>
    /// A structure that is always located at the start of the shared memory in a <see cref="SharedBuffer"/> instance. 
    /// This allows the shared memory to be opened by other instances without knowing its size before hand.
    /// </summary>
    /// <remarks>This structure is the same size on 32-bit and 64-bit architectures.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct SharedHeader
    {
        /// <summary>
        /// The total size of the buffer including <see cref="SharedHeader"/>, i.e. <code>BufferSize + Marshal.SizeOf(typeof(SharedMemory.SharedHeader))</code>.
        /// </summary>
        public long SharedMemorySize;

        /// <summary>
        /// Flag indicating whether the owner of the buffer has closed its <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/> and <see cref="System.IO.MemoryMappedFiles.MemoryMappedViewAccessor"/>.
        /// </summary>
        public volatile int Shutdown;

        /// <summary>
        /// Pad to 16-bytes.
        /// </summary>
        int _padding0;
    }
}
