// SharedMemory (File: SharedMemory\MacEvent.cs)
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
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using static SharedMemory.MacNativeMethods;

namespace SharedMemory
{
    /// <summary>
    /// A named event on macOS: the first 64 bytes of an <c>shm_open</c> object, named by a lock file under the event's Windows name, which every process
    /// with the event open maps. Waiters sleep on its state word with <c>os_sync_wait_on_address</c>.
    /// </summary>
    /// <remarks>
    /// <para>The region is <see cref="LinuxEvent"/>'s, and so are its rules: the magic, the format version, the reset mode, the state (bit 0 the signal,
    /// bits 1 to 31 a count of Sets), and the waiter count; the barriers that let a Set skip the wake call when nobody waits; a manual-reset waiter
    /// released by a Set it saw counted, not by a wake alone. Its lifetime is a buffer's (<see cref="MacSharedObject"/>), with create-or-open as for a
    /// Windows named event. The object is a whole page, so an opener checks that at least 64 bytes are mapped, not that 64 are.</para>
    /// <para>Both the waits and the wakes pass the shared flag, without which a wake from another process reaches nobody. <c>os_sync</c> refuses a
    /// timeout of 0, so a wait of 0 ms only looks at the state word.</para>
    /// <para>The object and its mapping belong to a <see cref="SafeHandle"/> that every call holds a reference on, as on Linux: disposing the event while
    /// another thread waits on it lets go of them only when that wait returns, and an event nobody disposes lets go of them when it is finalised.</para>
    /// </remarks>
    internal sealed unsafe class MacEvent : IDisposable
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
        readonly bool _manualReset;
        readonly Region _region;
        readonly int* _state;
        readonly int* _waiters;

        MacEvent(string name, bool manualReset, Region region)
        {
            _name = name;
            _manualReset = manualReset;
            _region = region;
            byte* start = (byte*)region.DangerousGetHandle();
            _state = (int*)(start + StateOffset);
            _waiters = (int*)(start + WaitersOffset);
        }

        /// <summary>
        /// Creates the event, or opens it if somebody holds it, as a named <see cref="EventWaitHandle"/> does.
        /// </summary>
        /// <exception cref="PlatformNotSupportedException">macOS lacks <c>os_sync_wait_on_address</c> and the calls beside it: it is older than 14.4.</exception>
        /// <exception cref="IOException">The event has another format version, there is no memory for a new one, or its lock file is not one this version reads.</exception>
        /// <exception cref="WaitHandleCannotBeOpenedException">What somebody holds under the name is not an event.</exception>
        /// <exception cref="FileNotFoundException">The object a held lock file names is gone: launchd removes a user's objects when the user logs out.</exception>
        /// <exception cref="UnauthorizedAccessException">The lock folder or the lock file under the name is not private to this user.</exception>
        [SupportedOSPlatform("macos")]
        internal static MacEvent CreateOrOpen(string name, bool initialState, EventResetMode mode)
        {
            if (OsSync.Unsupported != null)
                throw PlatformSupport.NotSupported("named events", OsSync.Unsupported);

            var contents = new byte[Size];
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(0), Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(4), FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(contents.AsSpan(ModeOffset), mode == EventResetMode.ManualReset ? ManualReset : AutoReset);
            BinaryPrimitives.WriteInt32LittleEndian(contents.AsSpan(StateOffset), initialState ? 1 : 0);

            MacSharedObject shared = MacSharedObject.CreateOrOpen(name, contents);
            try
            {
                bool manualReset = ReadMode(shared, name);
                var region = new Region(shared);
                shared.CloseAtExit();
                return new MacEvent(name, manualReset, region);
            }
            catch
            {
                shared.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Checks that the object is an event this version reads, through its mapping, and returns whether it resets manually.
        /// </summary>
        static bool ReadMode(MacSharedObject shared, string name)
        {
            MacSharedObject.Mapping memory = shared.Memory;
            if (memory.ByteLength < Size)
                throw NotAnEvent(shared, name);
            var header = new ReadOnlySpan<byte>((void*)memory.DangerousGetHandle(), StateOffset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
                throw NotAnEvent(shared, name);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4));
            if (version != FormatVersion)
                throw new IOException("The event " + shared.ObjectName + " named by " + shared.LockFilePath + " for '" + name + "' has format version " + version + "; this SharedMemory reads version " + FormatVersion + ".");
            int mode = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(ModeOffset));
            if (mode != AutoReset && mode != ManualReset)
                throw NotAnEvent(shared, name);
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
                    Wake();
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
        /// Unmaps the event and lets go of its object and lock file, removing them if this was their last holder, once every call in progress has
        /// returned. Later calls throw <see cref="ObjectDisposedException"/>.
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

                    // os_sync takes no timeout of 0: a wait whose time is up ends after the look above, and the rest is at least 1 ns.
                    ulong nanoseconds = 0;
                    if (millisecondsTimeout != Timeout.Infinite)
                    {
                        long left = deadline - Stopwatch.GetTimestamp();
                        if (left <= 0)
                            return false;
                        nanoseconds = (ulong)(left / Stopwatch.Frequency * 1000000000 + left % Stopwatch.Frequency * 1000000000 / Stopwatch.Frequency);
                        if (nanoseconds == 0)
                            nanoseconds = 1;
                    }

                    // Woken, the state had changed, a signal (EINTR), or the time ran out (ETIMEDOUT): look again; the deadline ends the wait.
                    int error;
                    if (OsSync.Wait(_state, state, nanoseconds, out error) < 0 && error != EINTR && error != ETIMEDOUT)
                        throw OsSyncFailure(nanoseconds == 0 ? "os_sync_wait_on_address" : "os_sync_wait_on_address_with_timeout", error);
                }
            }
            finally
            {
                Interlocked.Decrement(ref *_waiters);
            }
        }

        void Wake()
        {
            // ENOENT: nobody was asleep on the word yet. A waiter on its way reads the state before it sleeps, so it misses nothing.
            int error;
            if (OsSync.Wake(_state, _manualReset, out error) < 0 && error != ENOENT)
                throw OsSyncFailure(_manualReset ? "os_sync_wake_by_address_all" : "os_sync_wake_by_address_any", error);
        }

        IOException OsSyncFailure(string call, int error)
        {
            return new IOException(call + " failed on the event '" + _name + "': " + Marshal.GetPInvokeErrorMessage(error), error);
        }

        static WaitHandleCannotBeOpenedException NotAnEvent(MacSharedObject shared, string name)
        {
            return new WaitHandleCannotBeOpenedException("SharedMemory cannot open " + shared.ObjectName + ", named by " + shared.LockFilePath + ", as the event '" + name + "': it is not an event.");
        }

        /// <summary>
        /// The mapped region, its handle the address of its first byte. It holds a reference on the mapping, so the address stays mapped while it lives.
        /// Its release, once it has been disposed and the last call holding it has returned, or when it is finalised, lets go of the object and its lock
        /// file and unmaps the region.
        /// </summary>
        sealed class Region : SafeHandle
        {
            readonly MacSharedObject _shared;

            internal Region(MacSharedObject shared)
                : base(IntPtr.Zero, true)
            {
                _shared = shared;
                bool added = false;
                shared.Memory.DangerousAddRef(ref added);
                SetHandle(shared.Memory.DangerousGetHandle());
            }

            public override bool IsInvalid
            {
                get { return handle == IntPtr.Zero; }
            }

            protected override bool ReleaseHandle()
            {
                _shared.Memory.DangerousRelease();
                _shared.Dispose();
                return true;
            }
        }
    }
}
#endif
