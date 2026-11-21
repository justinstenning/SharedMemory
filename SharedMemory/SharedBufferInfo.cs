// SharedMemory (File: SharedMemory\SharedBufferInfo.cs)
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

using System;

namespace SharedMemory
{
    /// <summary>
    /// The kind of buffer whose header <see cref="SharedBuffer.TryGetInfo"/> read: the library class that created it.
    /// </summary>
    public enum SharedBufferKind
    {
        /// <summary>
        /// The header records no kind, because SharedMemory 2.3.x or earlier created the buffer, or its owner has not written it yet;
        /// or it records a kind this version of SharedMemory does not know, from a later version.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// A subclass of <see cref="SharedBuffer"/> or <see cref="BufferWithLocks"/> that derives from no other library class.
        /// </summary>
        Other = 1,

        /// <summary>
        /// A <see cref="SharedMemory.BufferReadWrite"/>, or a subclass of it.
        /// </summary>
        BufferReadWrite = 2,

        /// <summary>
        /// A <see cref="SharedArray{T}"/>, or a subclass of it. The element type is not recorded.
        /// </summary>
        SharedArray = 3,

        /// <summary>
        /// A <see cref="SharedMemory.CircularBuffer"/>, or a subclass of it.
        /// </summary>
        CircularBuffer = 4,

        /// <summary>
        /// One of the two circular buffers of an <c>RpcBuffer</c> channel.
        /// </summary>
        RpcBufferRing = 5,
    }

    /// <summary>
    /// What a buffer's header said when <see cref="SharedBuffer.TryGetInfo"/> read it: its kind, its layout, whether its owner had
    /// finished creating it or has closed it, and its sizes.
    /// </summary>
    /// <remarks>A snapshot: the buffer can change, or go, at any time after it was read.</remarks>
    public sealed class SharedBufferInfo
    {
        internal SharedBufferInfo(string name, SharedBufferKind kind, int layoutVersion, bool isSupported, bool isReady, bool shuttingDown,
            long bufferSize, int nodeCount, int nodeBufferSize)
        {
            Name = name;
            Kind = kind;
            LayoutVersion = layoutVersion;
            IsSupported = isSupported;
            IsReady = isReady;
            ShuttingDown = shuttingDown;
            BufferSize = bufferSize;
            NodeCount = nodeCount;
            NodeBufferSize = nodeBufferSize;
        }

        /// <summary>
        /// The name the buffer was looked for by.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// The kind of buffer, from the layout marker in its header. <see cref="SharedBufferKind.Unknown"/> for a buffer without a
        /// marker, which <see cref="LayoutVersion"/> 0 shows, or with a kind from a later version of SharedMemory.
        /// </summary>
        public SharedBufferKind Kind { get; private set; }

        /// <summary>
        /// The layout version the header records, or 0 if it records none: a buffer created by SharedMemory 2.3.x or earlier, or
        /// one whose owner has not written its header yet.
        /// </summary>
        public int LayoutVersion { get; private set; }

        /// <summary>
        /// Whether this version of SharedMemory can open the buffer: true for a buffer without a layout marker, or with a layout
        /// version and lock mode this version reads. False for a buffer a later version of SharedMemory created with a layout this
        /// version cannot read, which an open refuses.
        /// </summary>
        public bool IsSupported { get; private set; }

        /// <summary>
        /// Whether the owner had finished creating the buffer: its layout marker's ready flag, or for a buffer without a marker, a
        /// size other than 0.
        /// </summary>
        public bool IsReady { get; private set; }

        /// <summary>
        /// Whether the buffer's owner had closed it, as <see cref="SharedBuffer.ShuttingDown"/> reads for an instance that has it
        /// open.
        /// </summary>
        public bool ShuttingDown { get; private set; }

        /// <summary>
        /// The buffer's size in bytes, after its header, as an instance's <see cref="SharedBuffer.BufferSize"/> would be; 0 until
        /// the owner has written it.
        /// </summary>
        public long BufferSize { get; private set; }

        /// <summary>
        /// For a <see cref="SharedMemory.CircularBuffer"/> or an <c>RpcBuffer</c> ring that is ready, its node count; otherwise 0.
        /// </summary>
        public int NodeCount { get; private set; }

        /// <summary>
        /// For a <see cref="SharedMemory.CircularBuffer"/> or an <c>RpcBuffer</c> ring that is ready, the size of each node's
        /// buffer in bytes; otherwise 0.
        /// </summary>
        public int NodeBufferSize { get; private set; }
    }
}
