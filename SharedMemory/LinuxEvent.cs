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
    /// <para>The file holds a magic number, the format version, the reset mode (0 auto, 1 manual), the state (0 not signalled), and the waiter count, then
    /// zeros. Its lifetime is a buffer's (<see cref="LinuxSharedFile"/>), with create-or-open as for a Windows named event: a file somebody holds is opened
    /// whatever reset mode and initial state the opener asked for, and anything else under the name gives way to a new event in its creator's state.</para>
    /// <para>A Set writes the state and then reads the waiter count, and a waiter writes the count and then reads the state, each with a full barrier, so a
    /// Set that finds no waiter can skip the wake call and still miss nobody on the way to sleep. <c>FUTEX_WAIT</c> sleeps only while the state is still 0.</para>
    /// <para>A manual-reset waiter that <c>FUTEX_WAKE</c> woke returns true even if a Reset came first: Windows releases every thread waiting when a
    /// manual-reset event is set, and only a Set wakes the word. An auto-reset waiter takes the signal when it runs, so a second Set before then finds the
    /// event still signalled and adds nothing, where Windows would release a second waiter or leave the event signalled.</para>
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
                return TryTake() || (millisecondsTimeout != 0 && Sleep(millisecondsTimeout));
            }
            finally
            {
                if (added)
                    _region.DangerousRelease();
            }
        }

        /// <summary>
        /// Signals the event, waking one waiter (auto reset) or every waiter (manual reset).
        /// </summary>
        internal bool Set()
        {
            bool added = false;
            _region.DangerousAddRef(ref added);
            try
            {
                // The exchange is a full barrier: the waiter count is read after the state is written.
                if (Interlocked.Exchange(ref *_state, 1) == 0 && Volatile.Read(ref *_waiters) != 0)
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
        /// Clears the event.
        /// </summary>
        internal bool Reset()
        {
            bool added = false;
            _region.DangerousAddRef(ref added);
            try
            {
                Interlocked.Exchange(ref *_state, 0);
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
        /// Takes the signal if there is one: any state but 0 is a signal, and an auto-reset event clears it for this waiter alone.
        /// </summary>
        bool TryTake()
        {
            int state = Volatile.Read(ref *_state);
            if (state == 0)
                return false;
            return _manualReset || Interlocked.CompareExchange(ref *_state, 0, state) == state;
        }

        /// <summary>
        /// Waits for a signal as one of the event's counted waiters.
        /// </summary>
        bool Sleep(int millisecondsTimeout)
        {
            long deadline = millisecondsTimeout == Timeout.Infinite ? 0 : Stopwatch.GetTimestamp() + millisecondsTimeout * Stopwatch.Frequency / 1000;
            // The increment is a full barrier: the state is read after the count is written.
            Interlocked.Increment(ref *_waiters);
            try
            {
                for (; ; )
                {
                    if (TryTake())
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

                    if (Futex(_futex, _state, FUTEX_WAIT, 0, timeout, null, 0) == 0)
                    {
                        // Only a Set wakes the word. For a manual-reset event that releases this waiter whatever has happened since.
                        if (_manualReset)
                            return true;
                        continue;
                    }
                    // EAGAIN: the state was no longer 0. EINTR: a signal. ETIMEDOUT: look once more, then the deadline ends the wait.
                    int error = Marshal.GetLastPInvokeError();
                    if (error != EAGAIN && error != EINTR && error != ETIMEDOUT)
                        throw FutexFailure("wait", error);
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
