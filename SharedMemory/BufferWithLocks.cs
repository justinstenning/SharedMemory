// SharedMemory (File: SharedMemory\BufferWithLocks.cs)
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
using System.Security.Permissions;
using System.Text;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// <para>Extends <see cref="SharedBuffer"/> to support simple thread-synchronisation for read/write 
    /// to the buffer by allowing callers to acquire and release read/write locks.</para>
    /// <para>All buffer read/write operations have been overloaded to first perform a <see cref="System.Threading.WaitHandle.WaitOne()"/> 
    /// using the <see cref="ReadWaitEvent"/> and <see cref="WriteWaitEvent"/> respectively.</para>
    /// <para>By default all read/write operations will not block, it is necessary to first acquire locks 
    /// through calls to <see cref="AcquireReadLock"/> and <see cref="AcquireWriteLock"/> as appropriate, with corresponding 
    /// calls to <see cref="ReleaseReadLock"/> and <see cref="ReleaseWriteLock"/> to release the locks.</para>
    /// <para>The locks are gates, not exclusive locks. <see cref="AcquireWriteLock"/> closes the gate that reads wait at, and
    /// <see cref="AcquireReadLock"/> the gate that writes wait at, but neither stops a caller of its own kind: two writers can
    /// hold the write lock at once, and readers are not counted, so the first <see cref="ReleaseReadLock"/> lets writes in
    /// while other readers still read. A reader and a writer that acquire at the same moment can both get in. Any process can
    /// release a lock it does not hold. To exclude other writers, coordinate them outside this class, for example with a
    /// named <see cref="System.Threading.Mutex"/>.</para>
    /// </summary>
#if NETFULL
    [PermissionSet(SecurityAction.LinkDemand)]
    [PermissionSet(SecurityAction.InheritanceDemand)]
#endif
    public abstract class BufferWithLocks : SharedBuffer
    {
        /// <summary>
        /// An event handle used for blocking write operations.
        /// </summary>
        /// <remarks>Null on Linux and macOS, where the named events are not <see cref="EventWaitHandle"/>s. Writes still block there.</remarks>
        protected EventWaitHandle WriteWaitEvent { get; private set; }
        
        /// <summary>
        /// An event handle used for blocking read operations.
        /// </summary>
        /// <remarks>Null on Linux and macOS, where the named events are not <see cref="EventWaitHandle"/>s. Reads still block there.</remarks>
        protected EventWaitHandle ReadWaitEvent { get; private set; }

        private readonly NamedEvent _writeEvent;
        private readonly NamedEvent _readEvent;

        #region Constructors

        /// <summary>
        /// Create a new <see cref="BufferWithLocks"/> instance with the specified name and buffer size.
        /// </summary>
        /// <param name="name">The name of the shared memory</param>
        /// <param name="bufferSize">The buffer size in bytes.</param>
        /// <param name="ownsSharedMemory">Whether or not the current instance owns the shared memory. If true a new shared memory will be created and initialised otherwise an existing one is opened.</param>
        protected BufferWithLocks(string name, long bufferSize, bool ownsSharedMemory)
            : base(name, bufferSize, ownsSharedMemory)
        {
            _writeEvent = NamedEvent.CreateOrOpen(Name + "_evt_write", true, EventResetMode.ManualReset, () => WriteWaitEvent);
            WriteWaitEvent = _writeEvent.WaitHandle;
            _readEvent = NamedEvent.CreateOrOpen(Name + "_evt_read", true, EventResetMode.ManualReset, () => ReadWaitEvent);
            ReadWaitEvent = _readEvent.WaitHandle;
        }

        #endregion

        #region Synchronisation

        private int _readWriteTimeout = 100;

        /// <summary>
        /// The Read/Write operation timeout in milliseconds (to prevent deadlocks). Defaults to 100ms and must be larger than -1.
        /// If a Read or Write operation's WaitEvent does not complete within this timeframe a <see cref="TimeoutException"/> will be thrown.
        /// If using AcquireReadLock/ReleaseReadLock and AcquireWriteLock/ReleaseWriteLock correctly this timeout will never occur.
        /// </summary>
        public virtual int ReadWriteTimeout
        {
            get { return _readWriteTimeout; }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException("ReadWriteTimeout", "Must be larger than -1.");
                _readWriteTimeout = value;
            }
        }

        /// <summary>
        /// Blocks the current thread until it is able to acquire a read lock. If successful all subsequent writes will be blocked until after a call to <see cref="ReleaseReadLock"/>.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="System.Threading.Timeout.Infinite" /> (-1) to wait indefinitely.</param>
        /// <returns>true if the read lock was able to be acquired, otherwise false.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number other than -1, which represents an infinite time-out.</exception>
        /// <remarks>If <paramref name="millisecondsTimeout"/> is <see cref="System.Threading.Timeout.Infinite" /> (-1), then attempting to acquire a read lock after acquiring a write lock on the same thread will result in a deadlock.</remarks>
        public bool AcquireReadLock(int millisecondsTimeout = System.Threading.Timeout.Infinite)
        {
            if (!_readEvent.WaitOne(millisecondsTimeout))
                return false;
            _writeEvent.Reset();
            return true;
        }

        /// <summary>
        /// Releases the current read lock, allowing all blocked writes to continue.
        /// </summary>
        public void ReleaseReadLock()
        {
            _writeEvent.Set();
        }

        /// <summary>
        /// Blocks the current thread until it is able to acquire a write lock. If successful all subsequent reads will be blocked until after a call to <see cref="ReleaseWriteLock"/>.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or System.Threading.Timeout.Infinite (-1) to wait indefinitely.</param>
        /// <returns>true if the write lock was able to be acquired, otherwise false.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number other than -1, which represents an infinite time-out.</exception>
        /// <remarks>If <paramref name="millisecondsTimeout"/> is <see cref="System.Threading.Timeout.Infinite" /> (-1), then attempting to acquire a write lock after acquiring a read lock on the same thread will result in a deadlock.</remarks>
        public bool AcquireWriteLock(int millisecondsTimeout = System.Threading.Timeout.Infinite)
        {
            if (!_writeEvent.WaitOne(millisecondsTimeout))
                return false;
            _readEvent.Reset();
            return true;
        }

        /// <summary>
        /// Releases the current write lock, allowing all blocked reads to continue.
        /// </summary>
        public void ReleaseWriteLock()
        {
            _readEvent.Set();
        }

        #endregion

        #region Writing

        /// <summary>
        /// Prevents write operations from deadlocking by throwing a TimeoutException if the WriteWaitEvent is not available within <see cref="ReadWriteTimeout"/> milliseconds
        /// </summary>
        private void WriteWait()
        {
            if (!_writeEvent.WaitOne(ReadWriteTimeout))
                throw new TimeoutException("The write operation timed out waiting for the write lock WaitEvent. Check your usage of AcquireWriteLock/ReleaseWriteLock and AcquireReadLock/ReleaseReadLock.");
        }

        /// <summary>
        /// Writes an instance of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="data">A reference to an instance of <typeparamref name="T"/> to be written</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected override void Write<T>(ref T data, long bufferPosition = 0)
        {
            WriteWait();
            base.Write<T>(ref data, bufferPosition);
        }

        /// <summary>
        /// Writes an array of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="buffer">An array of <typeparamref name="T"/> to be written. The length of this array controls the number of elements to be written.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected override void Write<T>(T[] buffer, long bufferPosition = 0)
        {
            WriteWait();
            base.Write<T>(buffer, bufferPosition);
        }

        /// <summary>
        /// Writes <paramref name="length"/> bytes from the <paramref name="ptr"/> into the shared memory buffer.
        /// </summary>
        /// <param name="ptr">A managed pointer to the memory location to be copied into the buffer</param>
        /// <param name="length">The number of bytes to be copied</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected override void Write(IntPtr ptr, int length, long bufferPosition = 0)
        {
            WriteWait();
            base.Write(ptr, length, bufferPosition);
        }

        /// <summary>
        /// Prepares an IntPtr to the buffer position and calls <paramref name="writeFunc"/> to perform the writing.
        /// </summary>
        /// <param name="writeFunc">A function used to write to the buffer. The IntPtr parameter is a pointer to the buffer offset by <paramref name="bufferPosition"/>.</param>
        /// <param name="bufferPosition">The offset within the buffer region to start writing from.</param>
        protected override void Write(Action<IntPtr> writeFunc, long bufferPosition = 0)
        {
            WriteWait();
            base.Write(writeFunc, bufferPosition);
        }

        #endregion

        #region Reading

        /// <summary>
        /// Prevents read operations from deadlocking by throwing a TimeoutException if the ReadWaitEvent is not available within <see cref="ReadWriteTimeout"/> milliseconds
        /// </summary>
        private void ReadWait()
        {
            if (!_readEvent.WaitOne(ReadWriteTimeout))
                throw new TimeoutException("The read operation timed out waiting for the read lock WaitEvent. Check your usage of AcquireWriteLock/ReleaseWriteLock and AcquireReadLock/ReleaseReadLock.");
        }

        /// <summary>
        /// Reads an instance of <typeparamref name="T"/> from the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="data">Output parameter that will contain the value read from the buffer</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected override void Read<T>(out T data, long bufferPosition = 0)
        {
            ReadWait();
            base.Read<T>(out data, bufferPosition);
        }

        /// <summary>
        /// Reads an array of <typeparamref name="T"/> from the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="buffer">Array that will contain the values read from the buffer. The length of this array controls the number of elements to read.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected override void Read<T>(T[] buffer, long bufferPosition = 0)
        {
            ReadWait();
            base.Read<T>(buffer, bufferPosition);
        }

        /// <summary>
        /// Reads <paramref name="length"/> bytes into the memory location <paramref name="destination"/> from the shared memory buffer.
        /// </summary>
        /// <param name="destination">A managed pointer to the memory location to copy data into from the buffer</param>
        /// <param name="length">The number of bytes to be copied</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected override void Read(IntPtr destination, int length, long bufferPosition = 0)
        {
            ReadWait();
            base.Read(destination, length, bufferPosition);
        }

        /// <summary>
        /// Prepares an IntPtr to the buffer position and calls <paramref name="readFunc"/> to perform the reading.
        /// </summary>
        /// <param name="readFunc">A function used to read from the buffer. The IntPtr parameter is a pointer to the buffer offset by <paramref name="bufferPosition"/>.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected override void Read(Action<IntPtr> readFunc, long bufferPosition = 0)
        {
            ReadWait();
            base.Read(readFunc, bufferPosition);
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// IDisposable pattern
        /// </summary>
        /// <param name="disposeManagedResources">true to release managed resources</param>
        protected override void Dispose(bool disposeManagedResources)
        {
            if (disposeManagedResources)
            {
                // Null on Linux and macOS
                if (WriteWaitEvent != null)
                    (WriteWaitEvent as IDisposable).Dispose();
                if (ReadWaitEvent != null)
                    (ReadWaitEvent as IDisposable).Dispose();
                _writeEvent.Dispose();
                _readEvent.Dispose();
            }
            base.Dispose(disposeManagedResources);
        }

        #endregion
    }
}
