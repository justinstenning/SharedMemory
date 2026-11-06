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
    /// <remarks>
    /// <para>This structure is the same size on 32-bit and 64-bit architectures: 16 bytes, after which every buffer's data starts.</para>
    /// <para>From SharedMemory 2.4.0, bytes 12 to 15 hold a layout marker, read as a little-endian <see cref="int"/>. A process on
    /// another version of SharedMemory can open the same buffer, so the marker's format is part of the layout. Version 1:</para>
    /// <list type="table">
    /// <listheader><term>Bits</term><description>Field</description></listheader>
    /// <item><term>0 to 3</term><description>The layout version, 1. A word of 0 is no marker: a buffer created by SharedMemory 2.3.x or
    /// earlier, which wrote 0 here, or one whose creator has not written its header yet.</description></item>
    /// <item><term>4 to 7</term><description>The buffer's kind, after the nearest library class it derives from: 0 another
    /// <see cref="SharedBuffer"/> or <see cref="BufferWithLocks"/> subclass, 1 <see cref="BufferReadWrite"/>, 2 <see cref="SharedArray{T}"/>,
    /// 3 <see cref="CircularBuffer"/>, 4 one of an <c>RpcBuffer</c> channel's two circular buffers.</description></item>
    /// <item><term>8 to 11</term><description>The lock mode, 0: the locks every version up to 2.4.0 uses.</description></item>
    /// <item><term>12</term><description>Ready, set by the creator.</description></item>
    /// <item><term>13 to 15</term><description>Spare flags, written 0 and ignored by 2.4.0.</description></item>
    /// <item><term>16 to 31</term><description>Reserved, written 0 and ignored by 2.4.0.</description></item>
    /// </list>
    /// <para>As bytes 12 to 15 of a ready buffer: <c>11 10 00 00</c> for a <see cref="BufferReadWrite"/>, <c>21 10 00 00</c> for a
    /// <see cref="SharedArray{T}"/>, <c>31 10 00 00</c> for a <see cref="CircularBuffer"/>, <c>41 10 00 00</c> for an <c>RpcBuffer</c>'s
    /// circular buffer and <c>01 10 00 00</c> for another subclass. An opener refuses a version other than 1 or a lock mode other than 0, and
    /// a <see cref="CircularBuffer"/> opener a kind other than 3 or 4. SharedMemory 2.3.x and earlier never read these bytes.</para>
    /// <para>Every later version keeps these rules: bits 0 to 3 are the version, and a word of 0 is no marker; a kind's value is never
    /// reused for another class; spare flags and reserved bits carry only what a 2.4.0 opener may ignore, and anything it must not miss
    /// comes with a new version or lock mode, which 2.4.0 refuses.</para>
    /// </remarks>
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
        /// The layout marker, bytes 12 to 15 (see the remarks). Only the creator writes it; an opener reads it and leaves it as it is.
        /// </summary>
        internal int LayoutMarker;
    }
}
