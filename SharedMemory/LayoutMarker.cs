// SharedMemory (File: SharedMemory\LayoutMarker.cs)
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

namespace SharedMemory
{
    /// <summary>
    /// Version 1 of the layout marker in <see cref="SharedHeader"/>'s bytes 12 to 15, read as a little-endian <see cref="int"/>: bits
    /// 0 to 3 the layout version (1), 4 to 7 the buffer's kind, 8 to 11 the lock mode (0), bit 12 the ready flag, 13 to 15 spare flags
    /// and 16 to 31 reserved. Spare flags and reserved bits are written 0 and ignored. A word of 0 is no marker: a buffer from
    /// SharedMemory 2.3.x or earlier, or one whose creator has not written its header yet. Once 2.4.0 ships this format is fixed:
    /// a process on another version can open the same buffer (<see cref="SharedHeader"/>'s remarks).
    /// </summary>
    internal static class LayoutMarker
    {
        /// <summary>The layout version this version of SharedMemory writes, and the only one it reads.</summary>
        internal const int CurrentVersion = 1;
        /// <summary>The lock mode this version of SharedMemory writes, and the only one it reads: today's locks.</summary>
        internal const int CurrentLockMode = 0;

        internal const int VersionMask = 0x0000000F;
        internal const int KindMask = 0x000000F0;
        internal const int KindShift = 4;
        internal const int LockModeMask = 0x00000F00;
        internal const int LockModeShift = 8;
        /// <summary>Set by the creator once <c>DoOpen</c> has returned true, and never cleared.</summary>
        internal const int Ready = 0x00001000;

        /// <summary>A class outside the library that derives from <see cref="SharedBuffer"/> or <see cref="BufferWithLocks"/>.</summary>
        internal const int KindSharedBuffer = 0;
        /// <summary><see cref="BufferReadWrite"/> and its subclasses.</summary>
        internal const int KindBufferReadWrite = 1;
        /// <summary><see cref="SharedArray{T}"/> and its subclasses.</summary>
        internal const int KindSharedArray = 2;
        /// <summary><see cref="CircularBuffer"/> and its subclasses.</summary>
        internal const int KindCircularBuffer = 3;
        /// <summary>One of the two circular buffers of an <c>RpcBuffer</c> channel.</summary>
        internal const int KindRpcBufferRing = 4;

        /// <summary>
        /// The marker a creator of <paramref name="kind"/> writes before its size: this version, the kind and this lock mode, without
        /// the ready flag.
        /// </summary>
        internal static int Make(int kind)
        {
            return CurrentVersion | ((kind << KindShift) & KindMask) | (CurrentLockMode << LockModeShift);
        }

        internal static int Version(int marker)
        {
            return marker & VersionMask;
        }

        internal static int Kind(int marker)
        {
            return (marker & KindMask) >> KindShift;
        }

        internal static int LockMode(int marker)
        {
            return (marker & LockModeMask) >> LockModeShift;
        }

        internal static bool IsReady(int marker)
        {
            return (marker & Ready) != 0;
        }

        /// <summary>
        /// The kind as an error message names it: "a BufferReadWrite", or for a kind this version has no class for, "kind 5, by a later
        /// version of SharedMemory".
        /// </summary>
        internal static string Describe(int kind)
        {
            switch (kind)
            {
                case KindSharedBuffer:
                    return "a SharedBuffer subclass";
                case KindBufferReadWrite:
                    return "a BufferReadWrite";
                case KindSharedArray:
                    return "a SharedArray";
                case KindCircularBuffer:
                    return "a CircularBuffer";
                case KindRpcBufferRing:
                    return "an RpcBuffer ring";
                default:
                    return "kind " + kind + ", by a later version of SharedMemory";
            }
        }
    }
}
