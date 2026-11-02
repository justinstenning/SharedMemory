// SharedMemory (File: SharedMemory\NamedEvent.cs)
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
    /// A named event shared between processes: the library's own handle on the events behind <see cref="BufferWithLocks"/> and <see cref="CircularBuffer"/>.
    /// </summary>
    /// <remarks>
    /// <para>On Windows the event is a named <see cref="EventWaitHandle"/>, which this instance waits on, signals and disposes. The owning class's
    /// protected property returns it; a handle a subclass assigns there is not used.</para>
    /// <para>On Linux x64 and arm64 the net10.0 build keeps the event in a file in /dev/shm of its own (<c>LinuxEvent</c>), and waits on it with
    /// <c>futex</c>. On macOS x64 and arm64 it keeps the event in an <c>shm_open</c> object named by a lock file (<c>MacEvent</c>), and waits on it
    /// with <c>os_sync_wait_on_address</c>, which macOS 14.4 added. <see cref="WaitHandle"/> is null there.</para>
    /// <para>Elsewhere the net10.0 build throws <see cref="PlatformNotSupportedException"/>, naming the platform. The other builds leave it to
    /// <see cref="EventWaitHandle"/>, which throws its own.</para>
    /// </remarks>
    internal sealed class NamedEvent : IDisposable
    {
#if NET10_0_OR_GREATER
        private LinuxEvent _linuxEvent;
        private MacEvent _macEvent;
#endif

        private NamedEvent(EventWaitHandle waitHandle)
        {
            WaitHandle = waitHandle;
        }

        /// <summary>
        /// Creates the named event, or opens it if it already exists, as a named <see cref="EventWaitHandle"/> does: the creator's
        /// <paramref name="initialState"/> and <paramref name="mode"/> stand, and an opener's are ignored.
        /// </summary>
        /// <param name="name">The event's name.</param>
        /// <param name="initialState">Whether a newly created event starts signalled.</param>
        /// <param name="mode">Whether a newly created event resets itself after releasing one waiter.</param>
        internal static NamedEvent CreateOrOpen(string name, bool initialState, EventResetMode mode)
        {
#if NET10_0_OR_GREATER
            if (LinuxSharedFile.IsSupported)
                return new NamedEvent(null) { _linuxEvent = LinuxEvent.CreateOrOpen(name, initialState, mode) };
            if (MacSharedObject.IsSupported)
                return new NamedEvent(null) { _macEvent = MacEvent.CreateOrOpen(name, initialState, mode) };
            if (!OperatingSystem.IsWindows())
                throw PlatformSupport.NotSupported("named events");
#endif
            return new NamedEvent(new EventWaitHandle(initialState, mode, name));
        }

        /// <summary>
        /// The named <see cref="EventWaitHandle"/>, which the owning class's protected property returns. Null on Linux and macOS.
        /// </summary>
        internal EventWaitHandle WaitHandle { get; private set; }

        /// <summary>
        /// Blocks until the event is signalled or <paramref name="millisecondsTimeout"/> passes.
        /// </summary>
        /// <returns>true if the event was signalled.</returns>
        internal bool WaitOne(int millisecondsTimeout)
        {
#if NET10_0_OR_GREATER
            if (_linuxEvent != null)
                return _linuxEvent.WaitOne(millisecondsTimeout);
            if (_macEvent != null)
                return _macEvent.WaitOne(millisecondsTimeout);
#endif
            return WaitHandle.WaitOne(millisecondsTimeout);
        }

        /// <summary>
        /// Signals the event.
        /// </summary>
        internal bool Set()
        {
#if NET10_0_OR_GREATER
            if (_linuxEvent != null)
                return _linuxEvent.Set();
            if (_macEvent != null)
                return _macEvent.Set();
#endif
            return WaitHandle.Set();
        }

        /// <summary>
        /// Clears the event.
        /// </summary>
        internal bool Reset()
        {
#if NET10_0_OR_GREATER
            if (_linuxEvent != null)
                return _linuxEvent.Reset();
            if (_macEvent != null)
                return _macEvent.Reset();
#endif
            return WaitHandle.Reset();
        }

        /// <summary>
        /// Releases the event: on Windows <see cref="WaitHandle"/>; on Linux the event's file, and on macOS its object and lock file, which are
        /// removed if no other process holds them. Later waits and signals throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            if (WaitHandle != null)
                ((IDisposable)WaitHandle).Dispose(); // net35's WaitHandle has no public Dispose
#if NET10_0_OR_GREATER
            if (_linuxEvent != null)
                _linuxEvent.Dispose();
            if (_macEvent != null)
                _macEvent.Dispose();
#endif
        }
    }
}
