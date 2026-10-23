// SharedMemory (File: SharedMemory\LinuxEvent.cs)
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

#if NET10_0_OR_GREATER
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static SharedMemory.LinuxNativeMethods;

namespace SharedMemory
{
    /// <summary>
    /// A named event on Linux: 64 bytes in a file in /dev/shm under the event's Windows name, which every process with the event open maps. Waiters sleep
    /// on its state word with <c>futex</c>.
    /// </summary>
    /// <remarks>
    /// <para>The file holds a magic number, the format version, the reset mode (0 auto, 1 manual), the state, and the waiter count, then zeros. Bit 0 of the
    /// state is the signal; bits 1 to 31 count the Sets that signalled the event, wrapping. Its lifetime is a buffer's (<see cref="LinuxSharedFile"/>), with create-or-open as for a Windows named event: a file somebody holds is opened
    /// whatever reset mode and initial state the opener asked for, and anything else under the name gives way to a new event in its creator's state.</para>
    /// <para>A Set writes the state and then reads the waiter count, and a waiter writes the count and then reads the state, each with a full barrier, so a
    /// Set that finds no waiter can skip the wake call and still miss nobody on the way to sleep. <c>FUTEX_WAIT</c> sleeps only while the state is still the
    /// one the waiter last read.</para>
    /// <para>A manual-reset waiter returns true once the count of Sets differs from the one it read when it began, even if a Reset has cleared the signal
    /// since: Windows releases every thread waiting when a manual-reset event is set. A wake alone releases nobody, so a late wake call from a Set that lost
    /// the CPU leaves a thread that began waiting after a later Reset blocked, as on Windows. An auto-reset waiter takes the signal when it runs, so a second
    /// Set before then finds the event still signalled and adds nothing, where Windows would release a second waiter or leave the event signalled.</para>
    /// <para>The mapping and the file belong to a <see cref="SafeHandle"/> that every call holds a reference on. Disposing the event while another thread
    /// waits on it lets go of them, unmapping the region and running the close step, only when that wait returns: the event keeps its name meanwhile, as a
    /// Windows event does while a wait holds it. An event nobody disposes lets go of them when it is finalised.</para>
    /// </remarks>
    internal sealed unsafe class LinuxEvent : IDisposable
    {
        const int Size = 64;
        const uint Magic = 0x56454D53;   // "SMEV" in memory order
        const uint FormatVersion = 1;
        const int AutoReset = 0;
        const int ManualReset = 1;
        const int ModeOffset = 8;
        const int StateOffset = 12;
        const int WaitersOffset = 16;

        /// <summary>
        /// Bit 0 of the state: the signal.
        /// </summary>
        const int Signalled = 1;

        /// <summary>
        /// What one Set adds to the state's count of Sets, in bits 1 to 31.
        /// </summary>
        const int OneSet = 2;

        readonly string _name;
        readonly long _futex;
        readonly bool _manualReset;
        readonly Region _region;
        readonly int* _state;
        readonly int* _waiters;

        LinuxEvent(string name, long futex, bool manualReset, Region region)
        {
            _name = name;
            _futex = futex;
            _manualReset = manualReset;
            _region = region;
            byte* start = (byte*)region.DangerousGetHandle();
            _state = (int*)(start + StateOffset);
            _waiters = (int*)(start + WaitersOffset);
        }

        /// <summary>
        /// Creates the event, or opens it if somebody holds it, as a named <see cref="EventWaitHandle"/> does.
        /// </summary>
        /// <exception cref="IOException">The event's file has another format version, or /dev/shm has no room for a new one.</exception>
        /// <exception cref="WaitHandleCannotBeOpenedException">The file somebody holds under the name is not an event.</exception>
        /// <exception cref="UnauthorizedAccessException">The file under the name belongs to another user or is not private to this one.</exception>
        /// <exception cref="PlatformNotSupportedException">The process runs on neither x64 nor arm64.</exception>
        [SupportedOSPlatform("linux")]
        internal static LinuxEvent CreateOrOpen(string name, bool initialState, EventResetMode mode)
        {
            long futex = FutexSyscall;
            var contents = new byte[Size];
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(0), Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(4), FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(contents.AsSpan(ModeOffset), mode == EventResetMode.ManualReset ? ManualReset : AutoReset);
            BinaryPrimitives.WriteInt32LittleEndian(contents.AsSpan(StateOffset), initialState ? 1 : 0);

            LinuxSharedFile file = LinuxSharedFile.CreateOrOpen(name, contents);
            SafeFileHandle mapped = null;
            MemoryMappedFile map = null;
            MemoryMappedViewAccessor view = null;
            try
            {
                bool manualReset = ReadMode(file, name);
                mapped = file.OpenForMapping();
                map = MemoryMappedFile.CreateFromFile(mapped, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                mapped = null;
                view = map.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
                file.CloseAtExit();
                return new LinuxEvent(name, futex, manualReset, new Region(file, map, view));
            }
            catch
            {
                if (view != null)
                    view.Dispose();
                if (map != null)
                    map.Dispose();
                if (mapped != null)
                    mapped.Dispose();
                file.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Checks that the file is an event this version reads, through the locked descriptor before anything maps it, and returns whether it resets manually.
        /// </summary>
        static bool ReadMode(LinuxSharedFile file, string name)
        {
            Span<byte> header = stackalloc byte[StateOffset];
            int read = file.Read(header, 0);
            if (read < 8 || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
                throw NotAnEvent(file, name);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4));
            if (version != FormatVersion)
                throw new IOException("The event file " + file.FilePath + " for '" + name + "' has format version " + version + "; this SharedMemory reads version " + FormatVersion + ".");
            int mode = read == header.Length ? BinaryPrimitives.ReadInt32LittleEndian(header.Slice(ModeOffset)) : -1;
            if (file.Length != Size || (mode != AutoReset && mode != ManualReset))
                throw NotAnEvent(file, name);
            return mode == ManualReset;
        }

        /// <summary>
        /// Blocks until the event is signalled or <paramref name="millisecondsTimeout"/> passes, taking the signal if the event resets automatically.
        /// </summary>
        /// <param name="millisecondsTimeout">-1 to wait without a timeout; 0 to look without waiting.</param>
        /// <returns>true if the event was signalled.</returns>
        internal bool WaitOne(int millisecondsTimeout)
        {
            if (millisecondsTimeout < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout), millisecondsTimeout, "The timeout must be -1 (infinite) or more.");
            bool added = false;
            _region.DangerousAddRef(ref added);
            try
            {
                int state = Volatile.Read(ref *_state);
                return TryTake(ref state) || (millisecondsTimeout != 0 && Sleep(state, millisecondsTimeout));
            }
            finally
            {
                if (added)
                    _region.DangerousRelease();
            }
        }

        /// <summary>
        /// Signals the event and counts the Set, waking one waiter (auto reset) or every waiter (manual reset). A signalled event stays as it is.
        /// </summary>
        internal bool Set()
        {
            bool added = false;
            _region.DangerousAddRef(ref added);
            try
            {
                int state = Volatile.Read(ref *_state);
                for (; ; )
                {
                    if ((state & Signalled) != 0)
                        return true;
                    // The compare-exchange is a full barrier: the waiter count is read after the state is written.
                    int seen = Interlocked.CompareExchange(ref *_state, (state + OneSet) | Signalled, state);
                    if (seen == state)
                        break;
                    state = seen;
                }
                if (Volatile.Read(ref *_waiters) != 0)
                    Wake(_manualReset ? int.MaxValue : 1);
                return true;
            }
            finally
            {
                if (added)
                    _region.DangerousRelease();
            }
        }

        /// <summary>
        /// Clears the signal, keeping the count of Sets.
        /// </summary>
        internal bool Reset()
        {
            bool added = false;
            _region.DangerousAddRef(ref added);
            try
            {
                Interlocked.And(ref *_state, ~Signalled);
                return true;
            }
            finally
            {
                if (added)
                    _region.DangerousRelease();
            }
        }

        /// <summary>
        /// Unmaps the event and lets go of its file, removing it if this was its last holder, once every call in progress has returned. Later calls throw
        /// <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            _region.Dispose();
        }

        /// <summary>
        /// Takes the signal if <paramref name="state"/>, the state as last read, has it: a manual-reset event keeps it, and an auto-reset event clears it for
        /// this waiter alone. When it returns false, <paramref name="state"/> is the state as it then was, without the signal.
        /// </summary>
        bool TryTake(ref int state)
        {
            while ((state & Signalled) != 0)
            {
                if (_manualReset)
                    return true;
                int seen = Interlocked.CompareExchange(ref *_state, state & ~Signalled, state);
                if (seen == state)
                    return true;
                state = seen;
            }
            return false;
        }

        /// <summary>
        /// Waits for a signal as one of the event's counted waiters. <paramref name="start"/> is the state when the wait began.
        /// </summary>
        bool Sleep(int start, int millisecondsTimeout)
        {
            long deadline = millisecondsTimeout == Timeout.Infinite ? 0 : Stopwatch.GetTimestamp() + millisecondsTimeout * Stopwatch.Frequency / 1000;
            // The increment is a full barrier: the state is read after the count is written.
            Interlocked.Increment(ref *_waiters);
            try
            {
                for (; ; )
                {
                    int state = Volatile.Read(ref *_state);
                    if (TryTake(ref state))
                        return true;
                    // A Set since the wait began released this waiter, whether or not a Reset has cleared the signal since.
                    if (_manualReset && (state & ~Signalled) != (start & ~Signalled))
                        return true;

                    Timespec remaining;
                    Timespec* timeout = null;
                    if (millisecondsTimeout != Timeout.Infinite)
                    {
                        long left = deadline - Stopwatch.GetTimestamp();
                        if (left <= 0)
                            return false;
                        remaining.Seconds = left / Stopwatch.Frequency;
                        remaining.Nanoseconds = left % Stopwatch.Frequency * 1000000000 / Stopwatch.Frequency;
                        timeout = &remaining;
                    }

                    // Woken, or EAGAIN (the state had changed), EINTR (a signal), or ETIMEDOUT: look again; the deadline ends the wait.
                    if (Futex(_futex, _state, FUTEX_WAIT, state, timeout, null, 0) != 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (error != EAGAIN && error != EINTR && error != ETIMEDOUT)
                            throw FutexFailure("wait", error);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref *_waiters);
            }
        }

        void Wake(int waiters)
        {
            if (Futex(_futex, _state, FUTEX_WAKE, waiters, null, null, 0) < 0)
                throw FutexFailure("wake", Marshal.GetLastPInvokeError());
        }

        IOException FutexFailure(string operation, int error)
        {
            return new IOException("futex " + operation + " failed on the event '" + _name + "': " + Marshal.GetPInvokeErrorMessage(error), error);
        }

        static WaitHandleCannotBeOpenedException NotAnEvent(LinuxSharedFile file, string name)
        {
            return new WaitHandleCannotBeOpenedException("SharedMemory cannot open " + file.FilePath + " as the event '" + name + "': it is not an event file.");
        }

        /// <summary>
        /// The mapped region, its handle the address of its first byte. Its release, once it has been disposed and the last call holding it has returned,
        /// or when it is finalised, unmaps the region and lets go of the file.
        /// </summary>
        sealed class Region : SafeHandle
        {
            readonly LinuxSharedFile _file;
            readonly MemoryMappedFile _map;
            readonly MemoryMappedViewAccessor _view;

            internal Region(LinuxSharedFile file, MemoryMappedFile map, MemoryMappedViewAccessor view)
                : base(IntPtr.Zero, true)
            {
                _file = file;
                _map = map;
                _view = view;
                SetHandle(view.SafeMemoryMappedViewHandle.DangerousGetHandle() + (nint)view.PointerOffset);
            }

            public override bool IsInvalid
            {
                get { return handle == IntPtr.Zero; }
            }

            protected override bool ReleaseHandle()
            {
                _view.Dispose();
                _map.Dispose();
                _file.Dispose();
                return true;
            }
        }
    }
}
#endif
