// SharedMemory (File: SharedMemory\SharedMemoryOptions.cs)
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
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// How a constructor that takes sizes treats a name that is already in use: <see cref="SharedMemoryOptions.OpenMode"/>.
    /// </summary>
    public enum SharedMemoryOpenMode
    {
        /// <summary>
        /// Create the buffer. If the name is already in use, the constructor throws <see cref="System.IO.IOException"/>, as the
        /// constructors without options do.
        /// </summary>
        CreateNew = 0,

        /// <summary>
        /// Create the buffer, or if the name is already in use, open the buffer that has it, provided it is of the same kind and
        /// sizes.
        /// </summary>
        CreateOrOpen = 1,
    }

    /// <summary>
    /// Options for creating and opening a <see cref="BufferReadWrite"/>, a <see cref="SharedArray{T}"/>, or another subclass of
    /// <see cref="SharedBuffer"/>, passed to their constructors that take options. A <see cref="CircularBuffer"/> takes
    /// <see cref="CircularBufferOptions"/>.
    /// </summary>
    /// <remarks>
    /// <para>A constructor reads the options once, when it runs, and checks them before it creates or opens anything. Changing an
    /// options object afterwards changes nothing about the instances already made with it, so one object can serve several
    /// constructors.</para>
    /// <para>The defaults give what the constructors without options do: <see cref="OpenMode"/> is
    /// <see cref="SharedMemoryOpenMode.CreateNew"/> and <see cref="OpenTimeout"/> is null.</para>
    /// </remarks>
    public class SharedMemoryOptions
    {
        /// <summary>
        /// How a constructor that takes sizes treats a name that is already in use. The default,
        /// <see cref="SharedMemoryOpenMode.CreateNew"/>, throws <see cref="System.IO.IOException"/> for it. The constructors that
        /// take only a name open an existing buffer whatever this says.
        /// </summary>
        /// <remarks>A value that <see cref="SharedMemoryOpenMode"/> does not define makes the constructor throw
        /// <see cref="ArgumentOutOfRangeException"/>.</remarks>
        public SharedMemoryOpenMode OpenMode
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        /// <summary>
        /// How long an open may wait, in all, for the buffer's name to appear and for its owner to finish creating it. The default,
        /// null, waits as the constructors without options do: a name that does not exist throws
        /// <see cref="System.IO.FileNotFoundException"/> at once, and a buffer whose owner is still creating it is waited for up to
        /// 1 s.
        /// </summary>
        /// <remarks>
        /// <para>With a value, the open waits until it runs out: <see cref="TimeSpan.Zero"/> waits for nothing, and
        /// <c>Timeout.InfiniteTimeSpan</c>, -1 ms, waits without limit. Nothing tells a process that a name has appeared, so the
        /// open looks for it again after 1 ms, then after twice as long each time, up to 16 ms between looks. On Linux and macOS
        /// a look that finds a buffer whose last holder died without closing it removes it, as any open does, and the wait goes
        /// on.</para>
        /// <para>When the time runs out, the open throws what an open without a wait throws at that point:
        /// <see cref="System.IO.FileNotFoundException"/> if the name never appeared, or <see cref="InvalidOperationException"/>
        /// saying that the buffer is not ready if its owner had not finished creating it. A buffer the open refuses, such as one
        /// whose owner closed it before it was ready, is refused at once.</para>
        /// <para>A value below zero other than <c>Timeout.InfiniteTimeSpan</c>, or above <see cref="int.MaxValue"/>
        /// milliseconds, makes the constructor throw <see cref="ArgumentOutOfRangeException"/>.</para>
        /// </remarks>
        public TimeSpan? OpenTimeout
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

#if NET40Plus
        /// <summary>
        /// Cancels the waits of an open: when it is cancelled, the open throws <see cref="OperationCanceledException"/> at its next
        /// look, within 16 ms, and releases what the constructor made. A token already cancelled when the constructor starts makes
        /// it throw before it creates or opens anything. Only the opens of the instance it was passed to observe the token, and the
        /// library's classes open only in their constructors. The default is <see cref="CancellationToken.None"/>. Not in the .NET
        /// Framework 3.5 build.
        /// </summary>
        public CancellationToken OpenCancellationToken
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }
#endif
    }

    /// <summary>
    /// Options for creating and opening a <see cref="CircularBuffer"/>, passed to its constructors that take options. It has the
    /// options of <see cref="SharedMemoryOptions"/>.
    /// </summary>
    public class CircularBufferOptions : SharedMemoryOptions
    {
    }
}
