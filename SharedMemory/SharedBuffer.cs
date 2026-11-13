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
        /// Indicates whether this instance owns the shared memory: true for the instance that created it, and for one whose
        /// <see cref="SharedMemoryOpenMode.CreateOrOpen"/> took over a buffer whose owner had closed it. The owner sets
        /// <see cref="ShuttingDown"/> for every instance when it closes.
        /// </summary>
        /// <remarks>Set by the constructor, from whether it creates the buffer, and by <see cref="Open"/>, and kept once the instance has
        /// closed. An instance that opened a buffer another instance created is not its owner, and its close writes nothing to the shared
        /// memory. With <see cref="SharedMemoryOpenMode.CreateOrOpen"/>, an instance that finds the buffer already there opens it as such
        /// an opener; if its owner had closed it, the instance then takes it over: it clears <see cref="ShuttingDown"/> for every
        /// instance and becomes owner, and its own close sets it again. Of several taking over at once, one becomes owner.
        /// <see cref="CreatedSharedMemory"/> tells whether this instance's open created the buffer.</remarks>
        public bool IsOwnerOfSharedMemory { get; private set; }

        /// <summary>
        /// Whether this instance's open created the shared memory: true only for the instance that created the buffer, whichever
        /// constructor it used, and false for one that opened a buffer that was already there.
        /// </summary>
        /// <remarks>Set by <see cref="Open"/> as soon as it has created the buffer, before <see cref="DoOpen"/> runs, and kept once the
        /// instance has closed. A creator that writes starting contents can check it to tell a new buffer from one that was already
        /// in use.</remarks>
        public bool CreatedSharedMemory { get; private set; }
        
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

        /// <summary>
        /// The kind a creator of this class records in the layout marker (<see cref="SharedHeader"/>): the nearest library class's.
        /// Internal, so that a subclass outside the library keeps its library base's kind.
        /// </summary>
        internal virtual int BufferKind
        {
            get { return LayoutMarker.KindSharedBuffer; }
        }

        /// <summary>
        /// Whether an opener of this class opens a buffer whose layout marker records <paramref name="kind"/>. Every class but
        /// <see cref="CircularBuffer"/> opens any kind as bytes.
        /// </summary>
        internal virtual bool AcceptsKind(int kind)
        {
            return true;
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
        // The handle of View that Open took ViewPtr from, for the finaliser: the .NET Framework 3.5 build's own view drops its handle when
        // it is finalised, which may be before this instance is
        private Microsoft.Win32.SafeHandles.SafeMemoryMappedViewHandle _viewHandle;
#if NET10_0_OR_GREATER
        // Where the memory is mapped without a MemoryMappedFile (macOS), this stands in for View: made with the size the header gives,
        // it checks that the size fits the mapping, as creating View does elsewhere.
        private System.IO.UnmanagedMemoryAccessor _mappedView;
#endif

        // The lease on the mapping. Bit 0 (Opened) is set from Open until Close, and each call that uses the mapping adds Call while it
        // runs. Whoever brings the count to 0 unmaps, and then sets it to Unmapped, the only state Open maps from: so Close never unmaps
        // under a running call, the last call out unmaps instead, and a call after Close finds bit 0 clear and throws.
        private const int Opened = 1;
        private const int Call = 2;
        private const int Unmapped = int.MinValue;
        private int _leases = Unmapped;

        /// <summary>
        /// For the tests alone: a creator calls it with its name between writing the layout marker and writing the size. Null outside the tests.
        /// </summary>
        internal static Action<string> LayoutMarkerWritten;

        /// <summary>
        /// For the tests alone: create-or-open calls it with its name when a create has found the name in use, before it opens the
        /// buffer there. Null outside the tests.
        /// </summary>
        internal static Action<string> CreateFoundNameInUse;

        // The options the constructor read, which every Open of this instance uses
        private OpenOptions _openOptions;
        // What the constructor was asked for: to create the buffer (a constructor with sizes), and of what size. Each Open starts from
        // these, since an Open that attached to the buffer already there takes the size it found and is not owner.
        private readonly bool _createsSharedMemory;
        private readonly long _requestedBufferSize;
        // The waits of the Open that is running, for a subclass's DoOpen in this library; null outside Open
        private OpenWait _openWait;

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
            : this(name, bufferSize, ownsSharedMemory, SharedMemoryOptions.Defaults)
        {
        }

        /// <summary>
        /// Create a new <see cref="SharedBuffer"/> instance with the specified name, buffer size and options for opening it.
        /// </summary>
        /// <param name="name">The name of the shared memory</param>
        /// <param name="bufferSize">The buffer size in bytes, when creating it. The total shared memory size will be <code>Marshal.SizeOf(SharedMemory.SharedHeader) + bufferSize</code></param>
        /// <param name="ownsSharedMemory">true to create the shared memory, which with <see cref="SharedMemoryOpenMode.CreateOrOpen"/>
        /// may open the buffer already there instead; false to open an existing one, which ignores <see cref="SharedMemoryOptions.OpenMode"/>.</param>
        /// <param name="options">How <see cref="Open"/> creates or opens the buffer: the open mode, how long it waits, and on .NET Framework 4
        /// and later the token that cancels the wait. Read once, here.</param>
        /// <remarks>
        /// <para>The constructor checks its arguments and the options, and creates and opens nothing; the subclass calls <see cref="Open"/>.</para>
        /// <para>The shared memory layout on 32-bit and 64-bit is:<br />
        /// <code>
        /// |       Header       |    Buffer    |<br />
        /// |      16-bytes      |  bufferSize  |
        /// </code>
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="ownsSharedMemory"/> is true and <paramref name="bufferSize"/> is
        /// not larger than zero. -or- <see cref="SharedMemoryOptions.OpenMode"/> is not a <see cref="SharedMemoryOpenMode"/> value, or
        /// <see cref="SharedMemoryOptions.OpenTimeout"/> is below zero other than <c>Timeout.InfiniteTimeSpan</c>, or above
        /// <see cref="int.MaxValue"/> milliseconds.</exception>
        /// <exception cref="OperationCanceledException">The options' <c>OpenCancellationToken</c>, in every build but .NET Framework 3.5, is already cancelled.</exception>
        protected SharedBuffer(string name, long bufferSize, bool ownsSharedMemory, SharedMemoryOptions options)
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
            // Read once and checked, the token included, before a subclass makes anything
            _openOptions = OpenOptions.Read(options);
            #endregion

            IsOwnerOfSharedMemory = ownsSharedMemory;
            _createsSharedMemory = ownsSharedMemory;
            Name = name;

            if (IsOwnerOfSharedMemory)
            {
                BufferSize = bufferSize;
                _requestedBufferSize = bufferSize;
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
        /// <remarks>
        /// <para>If <see cref="IsOwnerOfSharedMemory"/> is true then the shared memory buffer will be created, opening will fail in this case if the shared memory already exists. Otherwise if IsOwnerOfSharedMemory is false then the shared memory buffer will be opened, which will fail if it doesn't already exist.</para>
        /// <para>A creator records in the header the layout version, the buffer's kind and the lock mode, and once <see cref="DoOpen"/> has
        /// returned true, that the buffer is ready (<see cref="SharedHeader"/>). An opener waits up to 1 s for the ready flag, or for a buffer
        /// created by SharedMemory 2.3.x or earlier, which records none, for its size.</para>
        /// <para>The options the constructor took change the waits: with <see cref="SharedMemoryOptions.OpenTimeout"/>, an opener waits
        /// for the name to appear and for the ready flag within that time in all, counted from the start of this call, and on .NET Framework 4
        /// and later <c>OpenCancellationToken</c> cancels the waits. A creator sets
        /// <see cref="CreatedSharedMemory"/> once it has created the buffer.</para>
        /// <para>With <see cref="SharedMemoryOpenMode.CreateOrOpen"/>, a creator whose name is already in use opens the buffer there as an
        /// opener does, waiting for its owner to finish creating it. That buffer must be of this class's kind, with the same sizes, or the
        /// open closes it again and throws. <see cref="DoOpen"/> then runs as an opener's, and only after it has returned true, if the
        /// buffer's owner had closed it, does this instance take it over and become its owner. The loop that creates or opens the name
        /// runs within <see cref="SharedMemoryOptions.OpenTimeout"/>, or 1 s without one.</para>
        /// </remarks>
        /// <exception cref="System.IO.IOException">If trying to create a new shared memory buffer with a duplicate name as buffer owner,
        /// with <see cref="SharedMemoryOpenMode.CreateNew"/>.</exception>
        /// <exception cref="System.IO.FileNotFoundException">If trying to open a new shared memory buffer that does not exist as a consumer of existing buffer.
        /// With <see cref="SharedMemoryOptions.OpenTimeout"/>, the name did not appear within it.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">If trying to create a new shared memory buffer with a size larger than the logical addressable space.</exception>
        /// <exception cref="System.InvalidOperationException">A read or write on the earlier mapping of this instance is still running, for example on another thread or in the callback that calls Open.
        /// -or- Opening: the buffer is not ready, because its owner had not finished creating it after 1 s, or within
        /// <see cref="SharedMemoryOptions.OpenTimeout"/>, or closed it first; it was created by a later version of SharedMemory with a layout
        /// version or lock mode this version cannot read; it is not a circular buffer, and this is a <see cref="CircularBuffer"/>; or its header
        /// is damaged. -or- With <see cref="SharedMemoryOpenMode.CreateOrOpen"/>, the buffer already there is of another kind, or has
        /// other sizes.</exception>
        /// <exception cref="OperationCanceledException">The options' <c>OpenCancellationToken</c>, in every build but .NET Framework 3.5, was cancelled while the open
        /// waited.</exception>
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

            OpenOptions options = _openOptions ?? OpenOptions.Defaults;
            var wait = new OpenWait(options);
            CreatedSharedMemory = false;
            // Each open starts as the constructor asked: an earlier open may have attached as an opener, with the size it found
            IsOwnerOfSharedMemory = _createsSharedMemory;
            if (_createsSharedMemory)
                BufferSize = _requestedBufferSize;
            // Whether create-or-open found the buffer already there, and opens it as an opener does
            bool attached = false;
            try
            {
                // Attempts to create or open the shared memory with a name of this.Name
                if (IsOwnerOfSharedMemory && options.Mode == SharedMemoryOpenMode.CreateOrOpen)
                {
                    _mapping = CreateOrOpenExisting(wait);
                    if (!CreatedSharedMemory)
                    {
                        IsOwnerOfSharedMemory = false;
                        attached = true;
                    }
                }
                else if (IsOwnerOfSharedMemory)
                {
                    // Create a new shared memory mapping
                    _mapping = NamedMapping.CreateNew(Name, SharedMemorySize);
                    CreatedSharedMemory = true;
                }
                else
                {
                    // Open an existing shared memory mapping, waiting for its name within the open's timeout
                    _mapping = OpenExisting(wait);
                }

#if NET10_0_OR_GREATER
                if (NamedMapping.MapsWithoutFile)
                    MapWithoutFile(wait, attached);
                else
#endif
                if (IsOwnerOfSharedMemory)
                {
                    Mmf = _mapping.File;

                    // Create a view to the entire region of the shared memory
                    View = Mmf.CreateViewAccessor(0, SharedMemorySize, MemoryMappedFileAccess.ReadWrite);
                    View.SafeMemoryMappedViewHandle.AcquirePointer(ref ViewPtr);
                    _viewHandle = View.SafeMemoryMappedViewHandle;
                    Header = (SharedHeader*)(ViewPtr + HeaderOffset);
                    BufferStartPtr = ViewPtr + BufferOffset;
                    // Initialise the header
                    InitialiseHeader();
                }
                else
                {
                    Mmf = _mapping.File;

                    // Retrieve the header from the shared memory in order to initialise the correct size. The view is ReadWrite: the
                    // header is read with interlocked instructions, which write, and fault on a read-only page
                    using (var headerView = Mmf.CreateViewAccessor(0, HeaderOffset + Marshal.SizeOf(typeof(SharedHeader)), MemoryMappedFileAccess.ReadWrite))
                    {
                        byte* headerPtr = null;
                        headerView.SafeMemoryMappedViewHandle.AcquirePointer(ref headerPtr);
                        try
                        {
                            var header = (SharedHeader*)(headerPtr + HeaderOffset);
                            BufferSize = WaitForHeader(header, wait, attached) - Marshal.SizeOf(typeof(SharedHeader));
                        }
                        finally
                        {
                            headerView.SafeMemoryMappedViewHandle.ReleasePointer();
                        }
                    }

                    // Create a view to the entire region of the shared memory
                    View = Mmf.CreateViewAccessor(0, SharedMemorySize, MemoryMappedFileAccess.ReadWrite);
                    View.SafeMemoryMappedViewHandle.AcquirePointer(ref ViewPtr);
                    _viewHandle = View.SafeMemoryMappedViewHandle;
                    Header = (SharedHeader*)(ViewPtr + HeaderOffset);
                    BufferStartPtr = ViewPtr + HeaderOffset + Marshal.SizeOf(typeof(SharedHeader));
                }

                // Create-or-open: the buffer already there must have the size this instance asked for
                if (attached)
                    CheckAttachedSize(_requestedBufferSize);
            }
            catch
            {
                Close();
                throw;
            }

            // Complete any additional open logic
            _openWait = wait;
            try
            {
                if (!DoOpen())
                {
                    Close();
                    return false;
                }
                else
                {
                    if (attached)
                    {
                        // As an opener's DoOpen has read them, the subclass's own sizes; then the takeover, which comes after DoOpen so
                        // that a DoOpen that initialises the buffer when owner never does so for one already in use
                        CheckAttachedGeometry();
                        TakeOverIfOwnerClosed();
                    }
                    else if (IsOwnerOfSharedMemory)
                        SetReady();
                    return true;
                }
            }
            catch
            {
                Close();
                throw;
            }
            finally
            {
                _openWait = null;
            }
        }

        /// <summary>
        /// The waits of the <see cref="Open"/> that is running, for a subclass in this library that waits for its owner in
        /// <see cref="DoOpen"/>; outside Open, those of an open without a timeout.
        /// </summary>
        internal OpenWait CurrentOpenWait
        {
            get { return _openWait ?? new OpenWait(OpenOptions.Defaults); }
        }

        /// <summary>
        /// Create-or-open's create of the name, or else its open of the buffer already there: a create that finds the name in use opens
        /// it, and an open that finds it gone, because its holders closed it in between, creates again. The loop runs within the open's
        /// timeout, or 1 s without one, looking again after 1 ms, then twice as long each time up to 16 ms. Sets
        /// <see cref="CreatedSharedMemory"/> if it created the buffer.
        /// </summary>
        /// <exception cref="System.IO.IOException">The create failed other than for a name in use, or the name was in use at each create
        /// and gone at each open until the time ran out.</exception>
        private NamedMapping CreateOrOpenExisting(OpenWait wait)
        {
            for (int pause = 1; ; pause = Math.Min(pause * 2, 16))
            {
                wait.ThrowIfCancelled();
                System.IO.IOException createFailed;
                NamedMapping created = NamedMapping.TryCreateNew(Name, SharedMemorySize, out createFailed);
                if (created != null)
                {
                    CreatedSharedMemory = true;
                    return created;
                }

                Action<string> inUse = CreateFoundNameInUse;
                if (inUse != null)
                    inUse(Name);
                try
                {
                    return NamedMapping.OpenExisting(Name);
                }
                catch (System.IO.FileNotFoundException notFound)
                {
                    int left = wait.LeftOf(1000);
                    if (left == 0)
                    {
                        // Where the create cannot tell a name in use from another failure (Windows), an open that never finds the
                        // name means the create failed for that other reason
                        if (createFailed != null)
                            throw createFailed;
                        throw new System.IO.IOException(String.Format("The shared memory \"{0}\" could not be created or opened: each create found the name in use, and each open found it gone, for {1}.", Name, wait.Waited(1000)), notFound);
                    }
                    wait.Pause(left == Timeout.Infinite ? pause : Math.Min(pause, left));
                }
            }
        }

        /// <summary>
        /// An opener's open of the name. Without a timeout, a name that does not exist throws at once. With one, the name is looked
        /// for again after 1 ms, then twice as long each time up to 16 ms, until it appears or the timeout runs out; on Linux and
        /// macOS a look that finds an abandoned buffer removes it, and the wait goes on.
        /// </summary>
        /// <exception cref="System.IO.FileNotFoundException">The name does not exist, or did not appear within the timeout.</exception>
        private NamedMapping OpenExisting(OpenWait wait)
        {
            for (int pause = 1; ; pause = Math.Min(pause * 2, 16))
            {
                wait.ThrowIfCancelled();
                try
                {
                    return NamedMapping.OpenExisting(Name);
                }
                catch (System.IO.FileNotFoundException notFound)
                {
                    if (!wait.HasTimeout)
                        throw;
                    int left = wait.Left;
                    if (left == 0)
                        throw new System.IO.FileNotFoundException(String.Format("The shared memory \"{0}\" does not exist: it did not appear after waiting {1}.", Name, wait.Waited(0)), Name, notFound);
                    wait.Pause(left == Timeout.Infinite ? pause : Math.Min(pause, left));
                }
            }
        }

        /// <summary>
        /// Reads the header an opener found, and returns the total size once the owner has finished creating the buffer, looking every
        /// 1 ms for up to 1 s, or until the open's timeout runs out where it has one: once the layout marker's ready flag is set, or for
        /// a buffer without a marker, from SharedMemory 2.3.x or earlier, once the size is written. A marker this version cannot read is
        /// refused at once (<see cref="CheckLayoutMarker"/>), as is one of another kind than this class's when <paramref name="exactKind"/>
        /// (create-or-open).
        /// </summary>
        /// <exception cref="InvalidOperationException">The buffer was not ready after 1 s, or within the open's timeout, or its owner closed
        /// it before it was; the layout marker is refused; or the size of a ready buffer leaves no room after the header.</exception>
        /// <exception cref="OperationCanceledException">The open's token was cancelled.</exception>
        private long WaitForHeader(SharedHeader* header, OpenWait wait, bool exactKind)
        {
            System.Diagnostics.Stopwatch waited = null;
            while (true)
            {
                // The size first, then the marker. The owner writes the marker before the size, so with a size read first, the marker
                // read next is the owner's. Read the other way round, a word of 0 could be followed by a size, and a buffer still being
                // created would be opened as one from 2.3.x. No test tells the two orders apart: keep this one.
                long size = Interlocked.Read(ref header->SharedMemorySize);
                // Shutdown before the marker too: an owner that finished set the ready flag before it could set Shutdown, so a Shutdown
                // read here with no ready flag after it is an owner that closed before it finished
                bool shutdown = header->Shutdown != 0;
                int marker = Interlocked.CompareExchange(ref header->LayoutMarker, 0, 0);
                if (marker != 0)
                {
                    CheckLayoutMarker(marker, exactKind);
                    if (LayoutMarker.IsReady(marker))
                    {
                        // Everything the owner wrote before the ready flag is visible from here, the size included
                        size = Interlocked.Read(ref header->SharedMemorySize);
                        if (size <= HeaderOffset + Marshal.SizeOf(typeof(SharedHeader)))
                            throw new InvalidOperationException(String.Format("The shared memory \"{0}\" is damaged: its header gives a size of {1} bytes, which leaves no room after the 16-byte header.", Name, size));
                        return size;
                    }
                    if (shutdown)
                        throw new InvalidOperationException(String.Format("The shared memory \"{0}\" is not ready: its owner closed it before it finished creating it.", Name));
                }
                else if (size != 0)
                {
                    // No marker, from 2.3.x or earlier: opened as they open it
                    return size;
                }

                if (waited == null)
                    waited = System.Diagnostics.Stopwatch.StartNew();
                if (wait.IsOver(waited, 1000))
                    throw new InvalidOperationException(marker != 0
                        ? String.Format("The shared memory \"{0}\" is not ready: its owner had not finished creating it after waiting {1}.", Name, wait.Waited(1000))
                        : String.Format("The shared memory \"{0}\" is not ready: its size was still 0 after waiting {1} for its owner to write it.", Name, wait.Waited(1000)));
                wait.Pause(1);
            }
        }

        /// <summary>
        /// An opener's check of the layout marker it found, which is not 0: a layout version or lock mode this version cannot read, or a
        /// kind this class does not open (<see cref="AcceptsKind"/>), is refused at once. With <paramref name="exactKind"/>, for
        /// create-or-open, any kind but this class's own (<see cref="BufferKind"/>) is refused. The creator writes all three in one word,
        /// before anything else, so they hold whether the ready flag is set or not.
        /// </summary>
        /// <exception cref="InvalidOperationException">The marker is refused.</exception>
        private void CheckLayoutMarker(int marker, bool exactKind)
        {
            int version = LayoutMarker.Version(marker);
            int lockMode = LayoutMarker.LockMode(marker);
            if (version != LayoutMarker.CurrentVersion || lockMode != LayoutMarker.CurrentLockMode)
                throw new InvalidOperationException(String.Format("The shared memory \"{0}\" has layout version {1} and lock mode {2} (marker 0x{3:X8}), but this version of SharedMemory reads only layout version {4} with lock mode {5}. It was created by a later version of SharedMemory; use that version here too.",
                    Name, version, lockMode, marker, LayoutMarker.CurrentVersion, LayoutMarker.CurrentLockMode));

            int kind = LayoutMarker.Kind(marker);
            if (exactKind)
            {
                if (kind != BufferKind)
                    throw new InvalidOperationException(String.Format("The shared memory \"{0}\" already exists as {1}, not {2}.", Name, LayoutMarker.Describe(kind), LayoutMarker.Describe(BufferKind)));
                return;
            }

            // Only a CircularBuffer opener refuses a kind
            if (!AcceptsKind(kind))
                throw new InvalidOperationException(String.Format("The shared memory \"{0}\" was created as {1}, not a circular buffer, so it cannot be opened as a CircularBuffer.", Name, LayoutMarker.Describe(kind)));
        }

        /// <summary>
        /// Create-or-open's check of the size of the buffer it attached to, as soon as the header gives it, before <see cref="DoOpen"/>:
        /// <see cref="BufferSize"/>, as found, must be the size the constructor asked for.
        /// </summary>
        /// <exception cref="InvalidOperationException">The sizes differ.</exception>
        internal virtual void CheckAttachedSize(long requestedBufferSize)
        {
            if (BufferSize != requestedBufferSize)
                throw new InvalidOperationException(String.Format("The shared memory \"{0}\" already exists with {1} bytes, not {2}.", Name, BufferSize, requestedBufferSize));
        }

        /// <summary>
        /// Create-or-open's check, once <see cref="DoOpen"/> has run as an opener's, of the sizes a subclass in this library reads there.
        /// </summary>
        /// <exception cref="InvalidOperationException">The sizes differ.</exception>
        internal virtual void CheckAttachedGeometry()
        {
        }

        /// <summary>
        /// Create-or-open's last step, for an instance that attached to the buffer already there: if its owner had closed it, clears
        /// <see cref="ShuttingDown"/> and becomes owner. The compare-and-swap lets one instance of several do so; the flag 0 means an
        /// owner that is alive, or one that died without closing, and the instance stays an opener.
        /// </summary>
        private void TakeOverIfOwnerClosed()
        {
            // A DoOpen that closed this instance and still returned true has nothing to take over
            if (!TryEnter())
                return;
            try
            {
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
                if (Interlocked.CompareExchange(ref Header->Shutdown, 0, 1) == 1)
                    IsOwnerOfSharedMemory = true;
#pragma warning restore 0420
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// Allows any classes that inherit from <see cref="SharedBuffer"/> to perform additional open logic. There is no need to call base.DoOpen() from these implementations.
        /// </summary>
        /// <returns>True if successful, otherwise false.</returns>
        /// <remarks>
        /// <para>By throwing an exception or returning false, the call to <see cref="Open"/> will fail and <see cref="Close"/> will be called.</para>
        /// <para>Openers wait for the owner's DoOpen to return true. Write what an opener reads here, not in the subclass's constructor after
        /// <see cref="Open"/> returns, where an opener can find it half written.</para>
        /// <para><see cref="IsOwnerOfSharedMemory"/> is true here only in the instance that created the buffer, so initialise the buffer
        /// when it is true. With <see cref="SharedMemoryOpenMode.CreateOrOpen"/>, an instance that found the buffer already there runs
        /// DoOpen as an opener, with IsOwnerOfSharedMemory false, and if it then takes over a buffer whose owner had closed it, it
        /// becomes owner only after DoOpen has returned. After the constructor, <see cref="CreatedSharedMemory"/> tells the cases
        /// apart.</para>
        /// </remarks>
        protected virtual bool DoOpen()
        {
            return true;
        }

        /// <summary>
        /// Initialises the header within the shared memory. Only applicable if <see cref="IsOwnerOfSharedMemory"/> is true.
        /// </summary>
        /// <remarks>The layout marker is written only where there is none yet, so calling this again leaves it, and its ready flag, as they are.</remarks>
        protected void InitialiseHeader()
        {
            if (!IsOwnerOfSharedMemory)
                return;

            // Each field with Interlocked, a full fence on every build, in the order openers rely on: the layout marker without the ready
            // flag, then the size, so that an opener that reads the size finds the marker too. Open sets the ready flag after DoOpen.
            Interlocked.CompareExchange(ref Header->LayoutMarker, LayoutMarker.Make(BufferKind), 0);
            Action<string> markerWritten = LayoutMarkerWritten;
            if (markerWritten != null)
                markerWritten(Name);
            Interlocked.Exchange(ref Header->SharedMemorySize, SharedMemorySize);
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
            Interlocked.Exchange(ref Header->Shutdown, 0);
#pragma warning restore 0420
        }

        /// <summary>
        /// The creator's last step in <see cref="Open"/>, once <see cref="DoOpen"/> has returned true: sets the layout marker's ready flag,
        /// keeping its other bits, and openers waiting for it go on.
        /// </summary>
        private void SetReady()
        {
            // A DoOpen that closed this instance and still returned true has nothing left to mark
            if (!TryEnter())
                return;
            try
            {
                int marker = Interlocked.CompareExchange(ref Header->LayoutMarker, 0, 0);
                int seen;
                while ((seen = Interlocked.CompareExchange(ref Header->LayoutMarker, marker | LayoutMarker.Ready, marker)) != marker)
                    marker = seen;
            }
            finally
            {
                Exit();
            }
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
        /// Releases the mapping, once the count has come to 0 after <see cref="Close"/>, and lets <see cref="Open"/> map again. Never
        /// inlined, so that <see cref="Exit"/> stays an add and a compare wherever the JIT inlines it. Inlined into every Exit, it
        /// gave a loop over a ring's calls several times as many inlined methods, and on .NET 10 on Linux RpcBuffer's read loop
        /// then took about half a second to compile as it ran, which every call waited for.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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
            _viewHandle = null;
        }

        /// <summary>
        /// Any classes that inherit from <see cref="SharedBuffer"/> should implement any <see cref="Close"/> logic here, <see cref="Mmf"/> and <see cref="View"/> are still active at this point. There is no need to call base.DoClose() from these classes.
        /// </summary>
        /// <remarks>It is possible for <see cref="Close"/> to be called before <see cref="Open"/> has completed successfully, in this situation <see cref="DoClose"/> should fail gracefully.</remarks>
        protected virtual void DoClose()
        {
        }

        #endregion

        #region Information

        /// <summary>
        /// Reads the header of the buffer named <paramref name="name"/> without opening it for use: its kind, its layout, whether its
        /// owner has finished creating it or has closed it, and its sizes.
        /// </summary>
        /// <param name="name">The name of the buffer.</param>
        /// <param name="info">What the header said, or null if nothing has the name.</param>
        /// <returns>true if a buffer has the name, ready or not; false if nothing has it.</returns>
        /// <remarks>
        /// <para>A snapshot that does not wait: the buffer can change, or go, at any time after. It creates no event and writes
        /// nothing to the shared memory.</para>
        /// <para>On Linux and macOS, a buffer whose last holder died without closing it is removed, as any open removes it, and
        /// this returns false.</para>
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
        /// <exception cref="UnauthorizedAccessException">The buffer cannot be opened by this user.</exception>
        public static bool TryGetInfo(string name, out SharedBufferInfo info)
        {
            throw new NotImplementedException();
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
        internal static void CheckLength(int length)
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
        /// <param name="disposeManagedResources">true to dispose of managed resources as well as unmanaged, which calls <see cref="Close"/>.
        /// false from the finaliser of an instance nobody closed, which only lets the memory go once the garbage collector has finalised
        /// its handles: it writes nothing to the shared memory, so other processes see no change, and <see cref="ShuttingDown"/> stays as it was.</param>
        protected virtual void Dispose(bool disposeManagedResources)
        {
            if (disposeManagedResources)
            {
                this.Close();
            }
            else
            {
                ReleaseMappingPointer();
            }
        }

        /// <summary>
        /// The finaliser's part, for an instance nobody closed: gives up the pointer that <see cref="Open"/> took into the mapping, so
        /// that the mapping's own finaliser can release it. Nothing else is touched: no header write, no <see cref="ShuttingDown"/>, no
        /// <see cref="DoClose"/>, and the view, the file and any events are left to their own finalisers. Never throws.
        /// </summary>
        private void ReleaseMappingPointer()
        {
            // Only a mapped instance with no call running holds the pointer; taking it to Unmapped means nothing releases it again
            if (Interlocked.CompareExchange(ref _leases, Unmapped, Opened) != Opened)
                return;
            try
            {
                if (_viewHandle != null)
                    _viewHandle.ReleasePointer();
                else if (_mapping != null)
                    _mapping.ReleasePointer();
            }
            catch (Exception)
            {
                // A finaliser must not throw; at worst the mapping stays, as it did before
            }
            Header = null;
            ViewPtr = null;
            BufferStartPtr = null;
        }

        #endregion

#if NET10_0_OR_GREATER
        /// <summary>
        /// <see cref="Open"/>'s views of the mapping it created or opened, where the memory is mapped without a <see cref="MemoryMappedFile"/> (macOS): <see cref="Mmf"/> and <see cref="View"/>
        /// stay null, the pointers come from the mapping, and an accessor of the same size as <see cref="View"/> would have stands in for it, so that
        /// the size the header gives is checked against the mapping as it is elsewhere.
        /// </summary>
        private void MapWithoutFile(OpenWait wait, bool exactKind)
        {
            if (IsOwnerOfSharedMemory)
            {
                // The shared memory mapping this instance created
                ViewPtr = _mapping.Pointer;
                BufferStartPtr = ViewPtr + BufferOffset;
            }
            else
            {
                // The existing shared memory mapping, a whole number of pages: size the buffer from its header
                ViewPtr = _mapping.Pointer;
                BufferSize = WaitForHeader((SharedHeader*)(ViewPtr + HeaderOffset), wait, exactKind) - Marshal.SizeOf(typeof(SharedHeader));
                BufferStartPtr = ViewPtr + HeaderOffset + Marshal.SizeOf(typeof(SharedHeader));
            }
            _mappedView = new System.IO.UnmanagedMemoryAccessor(_mapping.Memory, 0, SharedMemorySize, System.IO.FileAccess.ReadWrite);
            Header = (SharedHeader*)(ViewPtr + HeaderOffset);
            InitialiseHeader();
        }
#endif
    }
}
