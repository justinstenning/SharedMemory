// SharedMemory (File: SharedMemory\SharedBuffer.cs)
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
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Text;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// Abstract base class that provides client/server support for reading/writing structures to a buffer within a <see cref="MemoryMappedFile" />.
    /// A header structure allows clients to open the buffer without knowing the size.
    /// </summary>
#if NETFULL
    [PermissionSet(SecurityAction.LinkDemand)]
    [PermissionSet(SecurityAction.InheritanceDemand)]
#endif
    public abstract unsafe class SharedBuffer : IDisposable
    {
        #region Public/Protected properties

        /// <summary>
        /// The name of the Shared Memory instance
        /// </summary>
        public string Name { get; private set; }
        
        /// <summary>
        /// The buffer size
        /// </summary>
        public long BufferSize { get; private set; }
        
        /// <summary>
        /// The total shared memory size, including header and buffer.
        /// </summary>
        public virtual long SharedMemorySize
        {
            get
            {
                return HeaderOffset + Marshal.SizeOf(typeof(SharedHeader)) + BufferSize;
            }
        }

        /// <summary>
        /// Indicates whether this instance owns the shared memory (i.e. creator of the shared memory)
        /// </summary>
        public bool IsOwnerOfSharedMemory { get; private set; }
        
        /// <summary>
        /// Returns true if the SharedMemory owner has/is shutting down, or if this instance is closed
        /// </summary>
        public bool ShuttingDown
        {
            get
            {
                if (!TryEnter())
                    return true;
                try
                {
                    return Header == null || Header->Shutdown == 1;
                }
                finally
                {
                    Exit();
                }
            }
        }

        /// <summary>
        /// Where the header starts within the shared memory
        /// </summary>
        protected virtual long HeaderOffset
        {
            get
            {
                return 0;
            }
        }

        /// <summary>
        /// Where the buffer is located within the shared memory
        /// </summary>
        protected virtual long BufferOffset
        {
            get
            {
                return HeaderOffset + Marshal.SizeOf(typeof(SharedHeader));
            }
        }

        #endregion

        #region Protected field members

        /// <summary>
        /// Memory mapped file. Null on macOS, where the net10.0 build maps the memory without a <see cref="MemoryMappedFile"/>.
        /// </summary>
        protected MemoryMappedFile Mmf;
        /// <summary>
        /// Memory mapped view. Null on macOS, where the net10.0 build maps the memory without a <see cref="MemoryMappedFile"/>;
        /// <see cref="ViewPtr"/>, <see cref="BufferStartPtr"/>, and <see cref="Header"/> are set there as everywhere else.
        /// </summary>
        protected MemoryMappedViewAccessor View;
        /// <summary>
        /// Pointer to the memory mapped view
        /// </summary>
        protected byte* ViewPtr = null;
        /// <summary>
        /// Pointer to the start of the buffer region of the memory mapped view
        /// </summary>
        protected byte* BufferStartPtr = null;
        /// <summary>
        /// Pointer to the header within shared memory
        /// </summary>
        protected SharedHeader* Header = null;

        #endregion

        #region Private field members

        private NamedMapping _mapping;
#if NET10_0_OR_GREATER
        // Where the memory is mapped without a MemoryMappedFile (macOS), this stands in for View: the header is written through it, and the
        // struct reads and writes check their bounds against its capacity.
        private System.IO.UnmanagedMemoryAccessor _mappedView;
#endif

        // The lease on the mapping. Bit 0 (Opened) is set from Open until Close, and each call that uses the mapping adds Call while it
        // runs. Whoever brings the count to 0 unmaps, and then sets it to Unmapped, the only state Open maps from: so Close never unmaps
        // under a running call, the last call out unmaps instead, and a call after Close finds bit 0 clear and throws.
        private const int Opened = 1;
        private const int Call = 2;
        private const int Unmapped = int.MinValue;
        private int _leases = Unmapped;

        #endregion

        #region Constructor / destructor

        /// <summary>
        /// Create a new <see cref="SharedBuffer"/> instance with the specified name and buffer size
        /// </summary>
        /// <param name="name">The name of the shared memory</param>
        /// <param name="bufferSize">The buffer size in bytes. The total shared memory size will be <code>Marshal.SizeOf(SharedMemory.SharedHeader) + bufferSize</code></param>
        /// <param name="ownsSharedMemory">Whether or not the current instance owns the shared memory. If true a new shared memory will be created and initialised otherwise an existing one is opened.</param>
        /// <remarks>
        /// <para>The maximum total shared memory size is dependent upon the system and current memory fragmentation.</para>
        /// <para>The shared memory layout on 32-bit and 64-bit is:<br />
        /// <code>
        /// |       Header       |    Buffer    |<br />
        /// |      16-bytes      |  bufferSize  |
        /// </code>
        /// </para>
        /// </remarks>
        protected SharedBuffer(string name, long bufferSize, bool ownsSharedMemory)
        {
            #region Argument validation
            if (name == String.Empty || name == null)
                throw new ArgumentException("Cannot be String.Empty or null", "name");
            if (ownsSharedMemory && bufferSize <= 0)
                throw new ArgumentOutOfRangeException("bufferSize", bufferSize, "Buffer size must be larger than zero when creating a new shared memory buffer.");
#if DEBUG
            else if (!ownsSharedMemory && bufferSize > 0)
                System.Diagnostics.Debug.Write("Buffer size is ignored when opening an existing shared memory buffer.", "Warning");
#endif
            #endregion

            IsOwnerOfSharedMemory = ownsSharedMemory;
            Name = name;

            if (IsOwnerOfSharedMemory)
            {
                BufferSize = bufferSize;
            }
        }

        /// <summary>
        /// Destructor - for Dispose(false)
        /// </summary>
        ~SharedBuffer()
        {
            Dispose(false);
        }

        #endregion

        #region Open / Close

        /// <summary>
        /// Creates a new or opens an existing shared memory buffer with the name of <see cref="Name"/> depending on the value of <see cref="IsOwnerOfSharedMemory"/>. 
        /// </summary>
        /// <returns>True if the memory was successfully mapped</returns>
        /// <remarks>If <see cref="IsOwnerOfSharedMemory"/> is true then the shared memory buffer will be created, opening will fail in this case if the shared memory already exists. Otherwise if IsOwnerOfSharedMemory is false then the shared memory buffer will be opened, which will fail if it doesn't already exist.</remarks>
        /// <exception cref="System.IO.IOException">If trying to create a new shared memory buffer with a duplicate name as buffer owner.</exception>
        /// <exception cref="System.IO.FileNotFoundException">If trying to open a new shared memory buffer that does not exist as a consumer of existing buffer.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">If trying to create a new shared memory buffer with a size larger than the logical addressable space.</exception>
        /// <exception cref="System.InvalidOperationException">A read or write on the earlier mapping of this instance is still running, for example on another thread or in the callback that calls Open.</exception>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        protected bool Open()
        {
            // A call still running keeps the earlier mapping until it returns; one count cannot hold two mappings
            int leases = _leases;
            if (leases != Unmapped && leases != Opened)
                throw StillInUse();

            Close();

            // Opened from here on, so that Close unmaps whatever the steps below map before one of them fails
            if (Interlocked.CompareExchange(ref _leases, Opened, Unmapped) != Unmapped)
                throw StillInUse();

            try
            {
                // Attempts to create or open the shared memory with a name of this.Name
#if NET10_0_OR_GREATER
                if (NamedMapping.MapsWithoutFile)
                    OpenWithoutFile();
                else
#endif
                if (IsOwnerOfSharedMemory)
                {
                    // Create a new shared memory mapping
                    _mapping = NamedMapping.CreateNew(Name, SharedMemorySize);
                    Mmf = _mapping.File;

                    // Create a view to the entire region of the shared memory
                    View = Mmf.CreateViewAccessor(0, SharedMemorySize, MemoryMappedFileAccess.ReadWrite);
                    View.SafeMemoryMappedViewHandle.AcquirePointer(ref ViewPtr);
                    Header = (SharedHeader*)(ViewPtr + HeaderOffset);
                    BufferStartPtr = ViewPtr + BufferOffset;
                    // Initialise the header
                    InitialiseHeader();
                }
                else
                {
                    // Open an existing shared memory mapping
                    _mapping = NamedMapping.OpenExisting(Name);
                    Mmf = _mapping.File;

                    // Retrieve the header from the shared memory in order to initialise the correct size
                    using (var headerView = Mmf.CreateViewAccessor(0, HeaderOffset + Marshal.SizeOf(typeof(SharedHeader)), MemoryMappedFileAccess.Read))
                    {
                        byte* headerPtr = null;
                        headerView.SafeMemoryMappedViewHandle.AcquirePointer(ref headerPtr);
                        var header = (SharedHeader*)(headerPtr + HeaderOffset);
                        BufferSize = header->SharedMemorySize - Marshal.SizeOf(typeof(SharedHeader));
                        headerView.SafeMemoryMappedViewHandle.ReleasePointer();
                    }

                    // Create a view to the entire region of the shared memory
                    View = Mmf.CreateViewAccessor(0, SharedMemorySize, MemoryMappedFileAccess.ReadWrite);
                    View.SafeMemoryMappedViewHandle.AcquirePointer(ref ViewPtr);
                    Header = (SharedHeader*)(ViewPtr + HeaderOffset);
                    BufferStartPtr = ViewPtr + HeaderOffset + Marshal.SizeOf(typeof(SharedHeader));
                }
            }
            catch
            {
                Close();
                throw;
            }

            // Complete any additional open logic
            try
            {
                if (!DoOpen())
                {
                    Close();
                    return false;
                }
                else
                {
                    return true;
                }
            }
            catch
            {
                Close();
                throw;
            }
        }

        /// <summary>
        /// Allows any classes that inherit from <see cref="SharedBuffer"/> to perform additional open logic. There is no need to call base.DoOpen() from these implementations.
        /// </summary>
        /// <returns>True if successful, otherwise false.</returns>
        /// <remarks>By throwing an exception or returning false, the call to <see cref="Open"/> will fail and <see cref="Close"/> will be called.</remarks>
        protected virtual bool DoOpen()
        {
            return true;
        }

        /// <summary>
        /// Initialises the header within the shared memory. Only applicable if <see cref="IsOwnerOfSharedMemory"/> is true.
        /// </summary>
        protected void InitialiseHeader()
        {
            if (!IsOwnerOfSharedMemory)
                return;

            SharedHeader header = new SharedHeader();
            header.SharedMemorySize = SharedMemorySize;
            header.Shutdown = 0;
#if NET10_0_OR_GREATER
            (View ?? _mappedView).Write<SharedHeader>(HeaderOffset, ref header);
#else
            View.Write<SharedHeader>(HeaderOffset, ref header);
#endif
        }

        /// <summary>
        /// Sets the <see cref="ShuttingDown"/> flag if this instance owns the shared memory, calls <see cref="DoClose"/>, and releases the
        /// MemoryMappedFile and MemoryMappedViewAccessor.<br />
        /// Close returns at once. A read or write still running on another thread, or a callback that called Close, keeps the memory
        /// mapped until it returns, and the memory is released then. Reads and writes that start after Close throw
        /// <see cref="ObjectDisposedException"/>. Close may be called again, and <see cref="Dispose()"/> after it.
        /// </summary>
        public virtual void Close()
        {
            // Hold the mapping while closing, so that a call that returns meanwhile cannot release it under DoClose
            bool held = TryHold();
            if (!held)
                WaitWhileUnmapping();
            try
            {
                if (held && IsOwnerOfSharedMemory && Header != null)
                {
                    // Indicates to any open instances that the owner is no longer open
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
                    Interlocked.Exchange(ref Header->Shutdown, 1);
#pragma warning restore 0420
                }

                // Allow additional close logic
                DoClose();
            }
            catch
            {
                // Open as before, as when DoClose threw before
                if (held)
                    Exit();
                throw;
            }

            if (held)
            {
                // Later calls throw, and the last call out, which may be this one, releases the memory
                ClearOpened();
                Exit();
            }
        }

        /// <summary>
        /// A call starts using the mapping: adds one to the lease, unless this instance is not open.
        /// </summary>
        /// <returns>true if the mapping stays until the matching <see cref="Exit"/>; false if this instance is closed or not yet opened.</returns>
        internal bool TryEnter()
        {
            // A stale read only costs a retry
            int leases = _leases;
            while ((leases & Opened) != 0)
            {
                int seen = Interlocked.CompareExchange(ref _leases, leases + Call, leases);
                if (seen == leases)
                    return true;
                leases = seen;
            }
            return false;
        }

        /// <summary>
        /// <see cref="TryEnter"/>, throwing <see cref="ObjectDisposedException"/> with the buffer's name if this instance is closed.
        /// </summary>
        internal void Enter()
        {
            if (!TryEnter())
                throw new ObjectDisposedException(Name);
        }

        /// <summary>
        /// A call has finished with the mapping. The last one out after <see cref="Close"/> releases it, on its own thread.
        /// </summary>
        internal void Exit()
        {
            if (Interlocked.Add(ref _leases, -Call) == 0)
                Unmap();
        }

        /// <summary>
        /// Whether this instance is open: from <see cref="Open"/> until <see cref="Close"/>.
        /// </summary>
        internal bool IsOpen
        {
            get { return (_leases & Opened) != 0; }
        }

        // Close's hold on the mapping: like TryEnter, but also while a call keeps a closed instance's mapping, so that Close can
        // call DoClose with the mapping in place however often it is called.
        private bool TryHold()
        {
            int leases = _leases;
            while (leases > 0)
            {
                int seen = Interlocked.CompareExchange(ref _leases, leases + Call, leases);
                if (seen == leases)
                    return true;
                leases = seen;
            }
            return false;
        }

        // Waits while the last call out of another thread releases the mapping, so that DoClose runs before or after that, not during it.
        // Releasing takes as long as disposing the view, the file and a ring's events.
        private void WaitWhileUnmapping()
        {
            for (int spins = 0; Interlocked.CompareExchange(ref _leases, 0, 0) == 0; spins++)
                Thread.Sleep(spins < 20 ? 0 : 1);
        }

        // Clears bit 0. The caller holds a lease, so this never brings the count to 0.
        private void ClearOpened()
        {
            int leases = _leases;
            while ((leases & Opened) != 0)
            {
                int seen = Interlocked.CompareExchange(ref _leases, leases - Opened, leases);
                if (seen == leases)
                    return;
                leases = seen;
            }
        }

        private InvalidOperationException StillInUse()
        {
            return new InvalidOperationException("The shared memory \"" + Name + "\" cannot be opened again while a read or write on its earlier mapping is still running.");
        }

        /// <summary>
        /// Called once no call uses the mapping any more, just before it is released, on the thread of the last call out. A
        /// subclass in this library releases here what its calls use until they return.
        /// </summary>
        internal virtual void OnUnmap()
        {
        }

        /// <summary>
        /// Releases the mapping, once the count has come to 0 after <see cref="Close"/>, and lets <see cref="Open"/> map again.
        /// </summary>
        private void Unmap()
        {
            try
            {
                try
                {
                    OnUnmap();
                }
                finally
                {
                    ReleaseMapping();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _leases, Unmapped);
            }
        }

        private void ReleaseMapping()
        {
            // Close the MemoryMappedFile and MemoryMappedViewAccessor
            if (View != null)
            {
                View.SafeMemoryMappedViewHandle.ReleasePointer();
                View.Dispose();
            }
            if (Mmf != null)
            {
                Mmf.Dispose();
            }
#if NET10_0_OR_GREATER
            if (_mappedView != null)
            {
                _mappedView.Dispose();
                _mappedView = null;
            }
#endif
            if (_mapping != null)
            {
                _mapping.Dispose();
            }
            Header = null;
            ViewPtr = null;
            BufferStartPtr = null;
            View = null;
            Mmf = null;
            _mapping = null;
        }

        /// <summary>
        /// Any classes that inherit from <see cref="SharedBuffer"/> should implement any <see cref="Close"/> logic here, <see cref="Mmf"/> and <see cref="View"/> are still active at this point. There is no need to call base.DoClose() from these classes.
        /// </summary>
        /// <remarks>It is possible for <see cref="Close"/> to be called before <see cref="Open"/> has completed successfully, in this situation <see cref="DoClose"/> should fail gracefully.</remarks>
        protected virtual void DoClose()
        {
        }

        #endregion

        #region Writing

        /// <summary>
        /// Writes an instance of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="source">A reference to an instance of <typeparamref name="T"/> to be written into the buffer</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void Write<T>(ref T source, long bufferPosition = 0)
            where T : struct
        {
            Enter();
            try
            {
                WriteStructureLeased(ref source, bufferPosition);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// <see cref="Write{T}(ref T, long)"/> for a caller that already holds a lease on the mapping (<see cref="Enter"/>).
        /// </summary>
        internal void WriteStructureLeased<T>(ref T source, long bufferPosition)
            where T : struct
        {
#if NET40Plus
            FastStructure<T>.StructureToPtr(ref source, StructureAt<T>(bufferPosition));
#else
            // This build's own view accessor checks the end, as it always has
            CheckRange(bufferPosition, 0, "position");
            View.Write<T>(BufferOffset + bufferPosition, ref source);
#endif
        }

#if NET40Plus
        /// <summary>
        /// The address of a <typeparamref name="T"/> at <paramref name="bufferPosition"/>, checked under the name, and with the
        /// exceptions, that the view accessor which copied single structures up to version 2.3.3 used.
        /// </summary>
        private IntPtr StructureAt<T>(long bufferPosition)
            where T : struct
        {
            CheckRange(bufferPosition, FastStructure<T>.Size, "position");
            return new IntPtr(BufferStartPtr + bufferPosition);
        }
#endif

        /// <summary>
        /// The one bounds check of every read and write: <paramref name="length"/> bytes from <paramref name="bufferPosition"/>
        /// must lie within the buffer, the first <see cref="Limit"/> bytes from <see cref="BufferStartPtr"/>. A start outside the
        /// buffer throws <see cref="ArgumentOutOfRangeException"/>; a start inside it with more bytes than remain throws
        /// <see cref="ArgumentException"/>. Both name <paramref name="paramName"/>. A length of 0 checks the start alone.
        /// </summary>
        private void CheckRange(long bufferPosition, long length, string paramName)
        {
            long limit = Limit;
            if (bufferPosition < 0 || bufferPosition >= limit)
                throw new ArgumentOutOfRangeException(paramName, bufferPosition, "The position is outside the buffer, which holds " + limit + " bytes.");
            // With the start inside the buffer, this subtraction cannot overflow
            if (length > limit - bufferPosition)
                throw new ArgumentException("There are not enough bytes remaining in the buffer for " + length + " bytes at position " + bufferPosition + ": the buffer holds " + limit + " bytes.", paramName);
        }

        /// <summary>
        /// How many bytes from <see cref="BufferStartPtr"/> reads and writes may reach: <see cref="BufferSize"/>. An opener that read
        /// the header before its owner had written the size there (#95) has no size, and mapped all of the shared memory instead:
        /// it may reach the end of that mapping, as it always could. Where this build cannot tell how long that mapping is (the
        /// .NET Framework 3.5 build's own views), it may reach nothing.
        /// </summary>
        private long Limit
        {
            get
            {
                if (BufferSize > 0)
                    return BufferSize;
                long mapped = 0;
#if NET10_0_OR_GREATER
                if (_mapping != null && _mapping.Memory != null)
                    mapped = (long)_mapping.Memory.ByteLength;
                else
#endif
#if NET40Plus
                if (View != null)
                    mapped = View.Capacity;
#endif
                return mapped - (BufferStartPtr - ViewPtr);
            }
        }

        /// <summary>
        /// The bytes that <paramref name="count"/> elements of <typeparamref name="T"/> take in the buffer, in a <see cref="long"/>,
        /// where the product cannot overflow. It first makes the checks that FastStructure's array copies make on the array, in
        /// the same order, so that a bad array is reported as it was before the buffer's own check.
        /// </summary>
        private static long ArrayLength<T>(T[] array, int index, int count)
            where T : struct
        {
            if (array == null)
                throw new ArgumentNullException("buffer");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            if (index < 0)
                throw new ArgumentOutOfRangeException("index");
            if (array.Length - index < count)
                throw new ArgumentException("Invalid offset into array specified by index and count");
            return (long)count * FastStructure<T>.Size;
        }

        /// <summary>
        /// Refuses a negative length for a pointer copy, which the buffer's check would let through to the copy.
        /// </summary>
        private static void CheckLength(int length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException("length", length, "The number of bytes to copy cannot be negative.");
        }

        /// <summary>
        /// Writes an array of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="source">An array of <typeparamref name="T"/> to be written. The length of this array controls the number of elements to be written.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void Write<T>(T[] source, long bufferPosition = 0)
            where T : struct
        {
            Write<T>(source, 0, bufferPosition);
        }

        /// <summary>
        /// Writes an array of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="source">An array of <typeparamref name="T"/> to be written. The length of this array controls the number of elements to be written.</param>
        /// <param name="index">The index within the array to start writing from.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void Write<T>(T[] source, int index, long bufferPosition = 0)
            where T : struct
        {
            Enter();
            try
            {
                WriteArrayLeased(source, index, source.Length - index, bufferPosition);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// Writes an array of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="source">The source data to be written to the buffer</param>
        /// <param name="index">The start index within <paramref name="source"/>.</param>
        /// <param name="count">The number of elements to write.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void WriteArray<T>(T[] source, int index, int count, long bufferPosition = 0)
            where T : struct
        {
            Enter();
            try
            {
                WriteArrayLeased(source, index, count, bufferPosition);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// <see cref="WriteArray{T}(T[], int, int, long)"/> for a caller that already holds a lease on the mapping (<see cref="Enter"/>).
        /// </summary>
        internal void WriteArrayLeased<T>(T[] source, int index, int count, long bufferPosition)
            where T : struct
        {
            CheckRange(bufferPosition, ArrayLength(source, index, count), "bufferPosition");
            FastStructure.WriteArray<T>((IntPtr)(BufferStartPtr + bufferPosition), source, index, count);
        }

        /// <summary>
        /// Writes <paramref name="length"/> bytes from the <paramref name="source"/> into the shared memory buffer.
        /// </summary>
        /// <param name="source">A managed pointer to the memory location to be copied into the buffer</param>
        /// <param name="length">The number of bytes to be copied</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void Write(IntPtr source, int length, long bufferPosition = 0)
        {
            Enter();
            try
            {
                WriteLeased(source, length, bufferPosition);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// <see cref="Write(IntPtr, int, long)"/> for a caller that already holds a lease on the mapping (<see cref="Enter"/>).
        /// </summary>
        internal void WriteLeased(IntPtr source, int length, long bufferPosition)
        {
            CheckLength(length);
            CheckRange(bufferPosition, length, "bufferPosition");
#if NETCORE
            Buffer.MemoryCopy((void*)source, BufferStartPtr + bufferPosition, BufferSize - bufferPosition, length);
#else
            UnsafeNativeMethods.CopyMemory(new IntPtr(BufferStartPtr + bufferPosition), source, (uint)length);
#endif
        }

        /// <summary>
        /// Prepares an IntPtr to the buffer position and calls <paramref name="writeFunc"/> to perform the writing.
        /// </summary>
        /// <param name="writeFunc">A function used to write to the buffer. The IntPtr parameter is a pointer to the buffer location offset by <paramref name="bufferPosition"/>.</param>
        /// <param name="bufferPosition">The offset within the buffer region to start writing to.</param>
        /// <remarks>The pointer stays valid until <paramref name="writeFunc"/> returns, even if <see cref="Close"/> is called meanwhile.</remarks>
        protected virtual void Write(Action<IntPtr> writeFunc, long bufferPosition = 0)
        {
            Enter();
            try
            {
                CheckRange(bufferPosition, 0, "bufferPosition");
                writeFunc(new IntPtr(BufferStartPtr + bufferPosition));
            }
            finally
            {
                Exit();
            }
        }

        #endregion

        #region Reading

        /// <summary>
        /// Reads an instance of <typeparamref name="T"/> from the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="data">Output parameter that will contain the value read from the buffer</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected virtual void Read<T>(out T data, long bufferPosition = 0)
            where T : struct
        {
            Enter();
            try
            {
                ReadStructureLeased(out data, bufferPosition);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// <see cref="Read{T}(out T, long)"/> for a caller that already holds a lease on the mapping (<see cref="Enter"/>).
        /// </summary>
        internal void ReadStructureLeased<T>(out T data, long bufferPosition)
            where T : struct
        {
#if NET40Plus
            data = FastStructure<T>.PtrToStructure(StructureAt<T>(bufferPosition));
#else
            // This build's own view accessor checks the end, as it always has
            CheckRange(bufferPosition, 0, "position");
            View.Read<T>(BufferOffset + bufferPosition, out data);
#endif
        }

        /// <summary>
        /// Reads an array of <typeparamref name="T"/> from the buffer.
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="destination">Array that will contain the values read from the buffer. The length of this array controls the number of elements to read.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected virtual void Read<T>(T[] destination, long bufferPosition = 0)
            where T : struct
        {
            Enter();
            try
            {
                ReadArrayLeased(destination, 0, destination.Length, bufferPosition);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// Reads a number of elements from a memory location into the provided buffer starting at the specified index.
        /// </summary>
        /// <typeparam name="T">The structure type</typeparam>
        /// <param name="destination">The destination buffer.</param>
        /// <param name="index">The start index within <paramref name="destination"/>.</param>
        /// <param name="count">The number of elements to read.</param>
        /// <param name="bufferPosition">The source offset within the buffer region of the shared memory.</param>
        protected virtual void ReadArray<T>(T[] destination, int index, int count, long bufferPosition)
            where T : struct
        {
            Enter();
            try
            {
                ReadArrayLeased(destination, index, count, bufferPosition);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// <see cref="ReadArray{T}(T[], int, int, long)"/> for a caller that already holds a lease on the mapping (<see cref="Enter"/>).
        /// </summary>
        internal void ReadArrayLeased<T>(T[] destination, int index, int count, long bufferPosition)
            where T : struct
        {
            CheckRange(bufferPosition, ArrayLength(destination, index, count), "bufferPosition");
            FastStructure.ReadArray<T>(destination, (IntPtr)(BufferStartPtr + bufferPosition), index, count);
        }

        /// <summary>
        /// Reads <paramref name="length"/> bytes into the memory location <paramref name="destination"/> from the buffer region of the shared memory.
        /// </summary>
        /// <param name="destination">A managed pointer to the memory location to copy data into from the buffer</param>
        /// <param name="length">The number of bytes to be copied</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected virtual void Read(IntPtr destination, int length, long bufferPosition = 0)
        {
            Enter();
            try
            {
                ReadLeased(destination, length, bufferPosition);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// <see cref="Read(IntPtr, int, long)"/> for a caller that already holds a lease on the mapping (<see cref="Enter"/>).
        /// </summary>
        internal void ReadLeased(IntPtr destination, int length, long bufferPosition)
        {
            CheckLength(length);
            CheckRange(bufferPosition, length, "bufferPosition");
#if NETCORE
            Buffer.MemoryCopy(BufferStartPtr + bufferPosition, (void*)destination, length, length);
#else
            UnsafeNativeMethods.CopyMemory(destination, new IntPtr(BufferStartPtr + bufferPosition), (uint)length);
#endif
        }

        /// <summary>
        /// Prepares an IntPtr to the buffer position and calls <paramref name="readFunc"/> to perform the reading.
        /// </summary>
        /// <param name="readFunc">A function used to read from the buffer. The IntPtr parameter is a pointer to the buffer offset by <paramref name="bufferPosition"/>.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        /// <remarks>The pointer stays valid until <paramref name="readFunc"/> returns, even if <see cref="Close"/> is called meanwhile.</remarks>
        protected virtual void Read(Action<IntPtr> readFunc, long bufferPosition = 0)
        {
            Enter();
            try
            {
                CheckRange(bufferPosition, 0, "bufferPosition");
                readFunc(new IntPtr(BufferStartPtr + bufferPosition));
            }
            finally
            {
                Exit();
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// IDisposable pattern - dispose of managed/unmanaged resources
        /// </summary>
        /// <param name="disposeManagedResources">true to dispose of managed resources as well as unmanaged.</param>
        protected virtual void Dispose(bool disposeManagedResources)
        {
            if (disposeManagedResources)
            {
                this.Close();
            }
        }

        #endregion

#if NET10_0_OR_GREATER
        /// <summary>
        /// <see cref="Open"/>'s mapping where the memory is mapped without a <see cref="MemoryMappedFile"/> (macOS): <see cref="Mmf"/> and <see cref="View"/>
        /// stay null, the pointers come from the mapping, and an accessor of the same size as <see cref="View"/> would have stands in for it, so that
        /// the header write and the struct reads and writes check their bounds as they do elsewhere.
        /// </summary>
        private void OpenWithoutFile()
        {
            if (IsOwnerOfSharedMemory)
            {
                // Create a new shared memory mapping
                _mapping = NamedMapping.CreateNew(Name, SharedMemorySize);
                ViewPtr = _mapping.Pointer;
                BufferStartPtr = ViewPtr + BufferOffset;
            }
            else
            {
                // Open an existing shared memory mapping, a whole number of pages, and size the buffer from its header
                _mapping = NamedMapping.OpenExisting(Name);
                ViewPtr = _mapping.Pointer;
                BufferSize = ((SharedHeader*)(ViewPtr + HeaderOffset))->SharedMemorySize - Marshal.SizeOf(typeof(SharedHeader));
                BufferStartPtr = ViewPtr + HeaderOffset + Marshal.SizeOf(typeof(SharedHeader));
            }
            _mappedView = new System.IO.UnmanagedMemoryAccessor(_mapping.Memory, 0, SharedMemorySize, System.IO.FileAccess.ReadWrite);
            Header = (SharedHeader*)(ViewPtr + HeaderOffset);
            InitialiseHeader();
        }
#endif
    }
}
