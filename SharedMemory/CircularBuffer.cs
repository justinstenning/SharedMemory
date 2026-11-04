// SharedMemory (File: SharedMemory\CircularBuffer.cs)
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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Text;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// A lock-free FIFO shared memory circular buffer (or ring buffer) utilising a <see cref="MemoryMappedFile"/>.
    /// </summary>
    /// <remarks>
    /// <para>Readers compete for items: each item goes to the one reader that claims its node first, not to every reader.</para>
    /// <para>A buffer of <see cref="NodeCount"/> nodes holds at most <see cref="NodeCount"/> - 1 unread items, because one node always stays free.</para>
    /// <para>A read that finds nothing to read, or a write that finds no free node, waits up to its timeout, which is for the whole call. While it
    /// waits it looks at the buffer again every 10 ms, or every tick of the system timer where that is longer (about 15.6 ms on Windows by default),
    /// and once more at the end of its timeout.</para>
    /// <para>Once the owner has shut down (<see cref="SharedBuffer.ShuttingDown"/>), a read still takes what is left in the buffer and a write still
    /// writes while there is room, but a read that finds nothing to read, or a write that finds no free node, returns 0 at once rather than wait. A
    /// loop that reads or writes until the owner goes should check <see cref="SharedBuffer.ShuttingDown"/>. A read or write that is waiting when
    /// this instance is closed returns 0; one that starts after <see cref="SharedBuffer.Close"/> throws <see cref="ObjectDisposedException"/>.</para>
    /// <para>A read or write checks its arguments before it takes a node. One whose copy or callback throws still gives its node back: a write
    /// posts it empty, and a read returns it for writing, so the ring goes on working for every process.</para>
    /// <para>A read skips an empty node, from a write that failed or wrote nothing, and goes on to the next item within its timeout, so it never
    /// returns 0 for one or runs a callback on one. Readers using a version before 2.4.0 do not skip empty nodes.</para>
    /// <para>What other processes can write in the shared memory is checked before it is used: a node's buffer is found from the node's index,
    /// an amount is limited to 0 to <see cref="NodeBufferSize"/>, and an index in the node header outside the node list, or a node header that
    /// does not fit the buffer when it is opened, throws <see cref="InvalidOperationException"/> naming the buffer.</para>
    /// </remarks>
#if NETFULL
    [PermissionSet(SecurityAction.LinkDemand)]
    [PermissionSet(SecurityAction.InheritanceDemand)]
#endif
    public unsafe class CircularBuffer : SharedBuffer
    {
        #region Public/Protected properties
        
        /// <summary>
        /// The number of nodes within the circular linked-list
        /// </summary>
        public int NodeCount { get; private set; }
        
        /// <summary>
        /// The buffer size of each node
        /// </summary>
        public int NodeBufferSize { get; private set; }
        
        /// <summary>
        /// Event signaled when data has been written if the reading index has caught up to the writing index
        /// </summary>
        /// <remarks>Obsolete. Null on Linux and macOS, where the named events are not <see cref="EventWaitHandle"/>s. The buffer waits on and signals its own
        /// event, so a handle a subclass assigns here is not used. It becomes an error in SharedMemory 2.5.0 and is removed in 3.0.</remarks>
        [Obsolete("Set on Windows only, and null on Linux and macOS. Read and Write wait on and signal the buffer's own event, and a handle assigned here is not used. It becomes an error in SharedMemory 2.5.0 and is removed in 3.0.")]
        protected EventWaitHandle DataExists
        {
            get { return _dataExistsHandle; }
            set { _dataExistsHandle = value; }
        }

        /// <summary>
        /// Event signaled when a node becomes available after reading if the writing index has caught up to the reading index
        /// </summary>
        /// <remarks>Obsolete. Null on Linux and macOS, where the named events are not <see cref="EventWaitHandle"/>s. The buffer waits on and signals its own
        /// event, so a handle a subclass assigns here is not used. It becomes an error in SharedMemory 2.5.0 and is removed in 3.0.</remarks>
        [Obsolete("Set on Windows only, and null on Linux and macOS. Read and Write wait on and signal the buffer's own event, and a handle assigned here is not used. It becomes an error in SharedMemory 2.5.0 and is removed in 3.0.")]
        protected EventWaitHandle NodeAvailable
        {
            get { return _nodeAvailableHandle; }
            set { _nodeAvailableHandle = value; }
        }

        /// <summary>
        /// The offset relative to <see cref="SharedBuffer.BufferStartPtr"/> where the node header starts within the buffer region of the shared memory
        /// </summary>
        protected virtual long NodeHeaderOffset
        {
            get
            {
                return 0;
            }
        }
            
        /// <summary>
        /// Where the linked-list nodes are located within the buffer
        /// </summary>
        protected virtual long NodeOffset
        {
            get
            {
                return NodeHeaderOffset + Marshal.SizeOf(typeof(NodeHeader));
            }
        }

        /// <summary>
        /// Where the list of buffers are located within the shared memory
        /// </summary>
        protected virtual long NodeBufferOffset
        {
            get
            {
                return NodeOffset + (Marshal.SizeOf(typeof(Node)) * NodeCount);
            }
        }

        /// <summary>
        /// Provide direct access to the Node[] memory
        /// </summary>
        /// <param name="i"></param>
        /// <returns></returns>
        protected virtual Node* this[int i]
        {
            get
            {
                if (i < 0 || i >= NodeCount)
                    throw new ArgumentOutOfRangeException();

                return ((Node*)(BufferStartPtr + NodeOffset)) + i;
            }
        }

        #endregion

        #region Private field members

        private NodeHeader* _nodeHeader = null;

        // Where the node buffers start, read from NodeBufferOffset once each Open, when the node count is known. A node's buffer is
        // computed from it and the node's index, never read from the node's Offset (BufferAndRingSafety FR-010).
        private long _nodeBufferOffset;

        private NamedEvent _dataExists;
        private NamedEvent _nodeAvailable;
        // What DataExists and NodeAvailable return: the events' handles from DoOpen to DoClose, unless a subclass assigns others
        private EventWaitHandle _dataExistsHandle;
        private EventWaitHandle _nodeAvailableHandle;

        // Whether a subclass overrides GetNodeForWriting or GetNodeForReading. The overloads then reserve through the override, holding
        // a lease across it, as the override's node comes without one.
        private readonly bool _overridesGetNodeForWriting;
        private readonly bool _overridesGetNodeForReading;
        
        #endregion

        #region Structures

        /// <summary>
        /// Provides cursors for the circular buffer along with dimensions
        /// </summary>
        /// <remarks>This structure is the same size on 32-bit and 64-bit architectures.</remarks>
        [StructLayout(LayoutKind.Sequential)]
        public struct NodeHeader
        {
            /// <summary>
            /// The index of the first unreadable node
            /// </summary>
            public volatile int ReadEnd;
            /// <summary>
            /// The index of the next readable node
            /// </summary>
            public volatile int ReadStart;

            /// <summary>
            /// The index of the first unwritable node
            /// </summary>
            public volatile int WriteEnd;
            /// <summary>
            /// The index of the next writable node
            /// </summary>
            public volatile int WriteStart;

            /// <summary>
            /// The number of nodes within the buffer
            /// </summary>
            public int NodeCount;

            /// <summary>
            /// The size of the buffer for each node
            /// </summary>
            public int NodeBufferSize;
        }

        /// <summary>
        /// Represents a node within the buffer's circular linked list
        /// </summary>
        /// <remarks>This structure is the same size on 32-bit and 64-bit architectures.</remarks>
        [StructLayout(LayoutKind.Sequential)]
        public struct Node
        {
            /// <summary>
            /// The next node.
            /// </summary>
            public int Next;

            /// <summary>
            /// The previous node.
            /// </summary>
            public int Prev;

            /// <summary>
            /// A flag used while returning a node for writing after having been read.
            /// </summary>
            public volatile int DoneRead;

            /// <summary>
            /// A flag used while posting a node for reading after writing is completed.
            /// </summary>
            public volatile int DoneWrite;

            /// <summary>
            /// Represents the offset relative to <see cref="SharedBuffer.BufferStartPtr"/> where the data for this node can be found.
            /// </summary>
            public long Offset;
            
            /// <summary>
            /// Represents the index of the current node.
            /// </summary>
            public int Index;

            /// <summary>
            /// Holds the number of bytes written into this node.
            /// </summary>
            public int AmountWritten;
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates and opens a new <see cref="CircularBuffer"/> instance with the specified name, node count and buffer size per node.
        /// </summary>
        /// <param name="name">The name of the shared memory to be created</param>
        /// <param name="nodeCount">The number of nodes within the circular linked-list (minimum of 2). The buffer holds at most <paramref name="nodeCount"/> - 1 unread items.</param>
        /// <param name="nodeBufferSize">The buffer size per node in bytes. The total shared memory size will be <code>Marshal.SizeOf(SharedMemory.SharedHeader) + Marshal.SizeOf(CircularBuffer.NodeHeader) + (Marshal.SizeOf(CircularBuffer.Node) * nodeCount) + (bufferSize * nodeCount)</code></param>
        /// <remarks>
        /// <para>The maximum total shared memory size is dependent upon the system and current memory fragmentation.</para>
        /// <para>The shared memory layout on 32-bit and 64-bit architectures is:<br />
        /// <code>
        /// |       Header       |   NodeHeader  | Node[0] | ... | Node[N-1] | buffer[0] | ... | buffer[N-1] |<br />
        /// |      16-bytes      |    24-bytes   |       32-bytes * N        |     NodeBufferSize * N        |<br />
        ///                      |------------------------------BufferSize-----------------------------------|<br />
        /// |-----------------------------------------SharedMemorySize---------------------------------------|
        /// </code>
        /// </para>
        /// </remarks>
        public CircularBuffer(string name, int nodeCount, int nodeBufferSize)
            : this(name, nodeCount, nodeBufferSize, true)
        {
            Open();
        }

        /// <summary>
        /// Opens an existing <see cref="CircularBuffer"/> with the specified name.
        /// </summary>
        /// <param name="name">The name of an existing <see cref="CircularBuffer"/> previously created with <see cref="SharedBuffer.IsOwnerOfSharedMemory"/>=true</param>
        /// <remarks>If the owner is still initialising the buffer, this waits up to 1 s for it to finish.</remarks>
        /// <exception cref="InvalidOperationException">The owner had not finished initialising the buffer after 1 s, or its node header does
        /// not describe at least 2 nodes of at least 1 byte that fit in the buffer.</exception>
        public CircularBuffer(string name)
            : this(name, 0, 0, false)
        {
            Open();
        }

        private CircularBuffer(string name, int nodeCount, int nodeBufferSize, bool ownsSharedMemory)
            : base(name, Marshal.SizeOf(typeof(NodeHeader)) + (Marshal.SizeOf(typeof(Node)) * nodeCount) + (nodeCount * (long)nodeBufferSize), ownsSharedMemory)
        {
            #region Argument validation
            if (ownsSharedMemory && nodeCount < 2)
                throw new ArgumentOutOfRangeException("nodeCount", nodeCount, "The node count must be a minimum of 2.");
#if DEBUG
            else if (!ownsSharedMemory && (nodeCount != 0 || nodeBufferSize > 0))
                System.Diagnostics.Debug.Write("Node count and nodeBufferSize are ignored when opening an existing shared memory circular buffer.", "Warning");
#endif
            #endregion

            if (IsOwnerOfSharedMemory)
            {
                NodeCount = nodeCount;
                NodeBufferSize = nodeBufferSize;
            }

            _overridesGetNodeForWriting = Overrides("GetNodeForWriting");
            _overridesGetNodeForReading = Overrides("GetNodeForReading");
        }

        // Whether this instance's type overrides CircularBuffer's protected method of that name, which takes a timeout
        private bool Overrides(string methodName)
        {
            for (Type type = GetType(); type != typeof(CircularBuffer); type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, new[] { typeof(int) }, null);
                if (method != null && method.GetBaseDefinition().DeclaringType == typeof(CircularBuffer))
                    return true;
            }
            return false;
        }

        #endregion

        #region Open / Close

        /// <summary>
        /// Attempts to create the <see cref="EventWaitHandle"/> handles and initialise the node header and buffers.
        /// </summary>
        /// <returns>True if the events and nodes were initialised successfully.</returns>
        protected override bool DoOpen()
        {
            // Create signal events
            _dataExists = NamedEvent.CreateOrOpen(Name + "_evt_dataexists", false, EventResetMode.AutoReset);
            _dataExistsHandle = _dataExists.WaitHandle;
            _nodeAvailable = NamedEvent.CreateOrOpen(Name + "_evt_nodeavail", false, EventResetMode.AutoReset);
            _nodeAvailableHandle = _nodeAvailable.WaitHandle;

            if (IsOwnerOfSharedMemory)
            {
                // Retrieve pointer to node header
                _nodeHeader = (NodeHeader*)(BufferStartPtr + NodeHeaderOffset);
                _nodeBufferOffset = NodeBufferOffset;

                // Initialise nodes entries first: an opener takes a non-zero NodeCount to mean the buffer is ready
                InitialiseLinkedListNodes();

                // Initialise the node header, writing NodeCount last
                InitialiseNodeHeader();
            }
            else
            {
                // Load the NodeHeader, waiting for the owner to finish initialising it, and check it before using it
                _nodeHeader = (NodeHeader*)(BufferStartPtr + NodeHeaderOffset);
                NodeCount = WaitForNodeCount();
                NodeBufferSize = _nodeHeader->NodeBufferSize;
                _nodeBufferOffset = NodeBufferOffset;
                CheckNodeHeader();
            }

            return true;
        }

        /// <summary>
        /// An opener's check of the node count and node size it read from the node header: at least 2 nodes of at least 1 byte, with
        /// the node list and the node buffers inside the buffer (BufferAndRingSafety FR-010).
        /// </summary>
        /// <exception cref="InvalidOperationException">The node header does not describe a circular buffer that fits.</exception>
        private void CheckNodeHeader()
        {
            if (NodeCount < 2 || NodeBufferSize < 1)
                throw new InvalidOperationException(String.Format("The circular buffer \"{0}\" is damaged: its header gives {1} nodes of {2} bytes, where a circular buffer has at least 2 nodes of at least 1 byte.", Name, NodeCount, NodeBufferSize));

            long nodeListEnd = NodeOffset + (long)Marshal.SizeOf(typeof(Node)) * NodeCount;
            long nodeBuffersEnd = _nodeBufferOffset + (long)NodeBufferSize * NodeCount;
            if (NodeOffset < 0 || _nodeBufferOffset < 0 || nodeListEnd > BufferSize || nodeBuffersEnd > BufferSize)
                throw new InvalidOperationException(String.Format("The circular buffer \"{0}\" is damaged: its header gives {1} nodes of {2} bytes, which need {3} bytes, but it holds {4}.", Name, NodeCount, NodeBufferSize, Math.Max(nodeListEnd, nodeBuffersEnd), BufferSize));
        }

        /// <summary>
        /// Reads the node count from the node header, waiting up to 1 s while it is 0 because the owner is still initialising the buffer.
        /// </summary>
        /// <exception cref="InvalidOperationException">The node count was still 0 after 1 s.</exception>
        private int WaitForNodeCount()
        {
            // A fenced read, so that the node list and NodeBufferSize written before NodeCount are read after it
            int nodeCount = Interlocked.CompareExchange(ref _nodeHeader->NodeCount, 0, 0);
            if (nodeCount != 0)
                return nodeCount;

            var waited = System.Diagnostics.Stopwatch.StartNew();
            while ((nodeCount = Interlocked.CompareExchange(ref _nodeHeader->NodeCount, 0, 0)) == 0)
            {
                if (waited.ElapsedMilliseconds >= 1000)
                    throw new InvalidOperationException(String.Format("The circular buffer \"{0}\" is not ready: its node count was still 0 after waiting 1 s for its owner to initialise it.", Name));
                Thread.Sleep(1);
            }
            return nodeCount;
        }

        /// <summary>
        /// Initialises the node header within the shared memory. Only applicable if <see cref="SharedBuffer.IsOwnerOfSharedMemory"/> is true.
        /// </summary>
        private void InitialiseNodeHeader()
        {
            if (!IsOwnerOfSharedMemory)
                return;

            NodeHeader header = new NodeHeader();
            header.ReadStart = 0;
            header.ReadEnd = 0;
            header.WriteEnd = 0;
            header.WriteStart = 0;
            header.NodeBufferSize = NodeBufferSize;
            header.NodeCount = 0;
            base.Write<NodeHeader>(ref header, NodeHeaderOffset);

            // NodeCount goes last, with a full fence, so an opener that reads it non-zero also sees everything written before it
            Interlocked.Exchange(ref _nodeHeader->NodeCount, NodeCount);
        }

        /// <summary>
        /// Initialise the nodes of the circular linked-list. Only applicable if <see cref="SharedBuffer.IsOwnerOfSharedMemory"/> is true.
        /// </summary>
        private void InitialiseLinkedListNodes()
        {
            if (!IsOwnerOfSharedMemory)
                return;

            int N = 0;

            Node[] nodes = new Node[NodeCount];

            // First node
            nodes[N].Next = 1;
            nodes[N].Prev = NodeCount - 1;
            nodes[N].Offset = NodeBufferOffset;
            nodes[N].Index = N;
            // Middle nodes
            for (N = 1; N < NodeCount - 1; N++)
            {
                nodes[N].Next = N + 1;
                nodes[N].Prev = N - 1;
                nodes[N].Offset = NodeBufferOffset + (NodeBufferSize * N);
                nodes[N].Index = N;
            }
            // Last node
            nodes[N].Next = 0;
            nodes[N].Prev = NodeCount - 2;
            nodes[N].Offset = NodeBufferOffset + (NodeBufferSize * N);
            nodes[N].Index = N;

            // Write the nodes to the shared memory
            base.WriteArray<Node>(nodes, 0, nodes.Length, NodeOffset);
        }

        /// <summary>
        /// Stops the protected wait-handle properties returning the events. The events are closed with the mapping, once no read or
        /// write uses them. The shared memory could still be open within one or more other instances.
        /// </summary>
        protected override void DoClose()
        {
            _dataExistsHandle = null;
            _nodeAvailableHandle = null;
        }

        /// <summary>
        /// Closes the events and forgets the node header, once no read or write uses them, as the mapping is released.
        /// </summary>
        internal override void OnUnmap()
        {
            if (_dataExists != null)
            {
                _dataExists.Dispose();
                _dataExists = null;
            }
            if (_nodeAvailable != null)
            {
                _nodeAvailable.Dispose();
                _nodeAvailable = null;
            }

            _nodeHeader = null;
        }

        #endregion

        #region Node Writing

        /// <summary>
        /// Attempts to reserve a node from the linked-list for writing with the specified timeout.
        /// </summary>
        /// <param name="timeout">The number of milliseconds to wait if a node is not immediately available for writing.</param>
        /// <returns>An unsafe pointer to the node if successful, otherwise null: at the timeout, at once when the owner has shut down and no
        /// node is free, or when this instance is closed while the call waits.</returns>
        /// <exception cref="ObjectDisposedException">This instance was closed before the call.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        /// <remarks>The node is valid until the buffer is closed: a subclass that writes to it itself must post it before Close is called, on
        /// this thread or another.</remarks>
        protected virtual Node* GetNodeForWriting(int timeout)
        {
            // The caller holds no lease on the mapping, as before
            int index;
            Node* node = TakeNodeForWriting(timeout, out index);
            if (node != null)
                Exit();
            return node;
        }

        /// <summary>
        /// The overloads' reservation of a node for writing: the node and its index, with a lease on the mapping, which the caller
        /// releases once it has posted the node; or null at the timeout, at once when the owner has shut down and no node is free, or
        /// when this instance is closed while the call waits.
        /// </summary>
        private Node* ReserveNodeForWriting(int timeout, out int index)
        {
            if (!_overridesGetNodeForWriting)
                return TakeNodeForWriting(timeout, out index);

            // A subclass's own reservation holds no lease of its own, so this one keeps the node it returns mapped until it is posted
            Enter();
            Node* node = null;
            index = -1;
            try
            {
                node = GetNodeForWriting(timeout);
                if (node != null)
                    index = IndexOf(node);
            }
            finally
            {
                if (index < 0)
                    Exit();
            }
            return node;
        }

        /// <summary>
        /// The index of a node that a subclass's <see cref="GetNodeForWriting"/> or <see cref="GetNodeForReading"/> returned: where it
        /// sits in the node list, which must be where <see cref="this[int]"/> puts that index.
        /// </summary>
        /// <exception cref="InvalidOperationException">The node is not one of this buffer's nodes.</exception>
        private int IndexOf(Node* node)
        {
            long index = node - this[0];
            if (index < 0 || index >= NodeCount || this[(int)index] != node)
                throw new InvalidOperationException(String.Format("The node a subclass reserved is not one of the circular buffer \"{0}\"'s nodes.", Name));
            return (int)index;
        }

        /// <summary>
        /// Reserves a node for writing, and returns it with a lease on the mapping for the caller to release. Each look at the ring
        /// holds a lease of its own, and a wait holds none, so that a wait never keeps a closed instance's mapping.
        /// </summary>
        /// <returns>The node, and its index in <paramref name="index"/>; or null at the timeout, at once when the owner has shut down
        /// and no node is free, or when this instance is closed while the call waits.</returns>
        /// <exception cref="ObjectDisposedException">This instance was closed before the call.</exception>
        /// <exception cref="InvalidOperationException">An index in the node header is outside the node list.</exception>
        private Node* TakeNodeForWriting(int timeout, out int index)
        {
            index = -1;
            long deadline = Deadline(timeout);
            for (bool firstLook = true; ; firstLook = false)
            {
                if (!EnterForLook(firstLook))
                    return null;
                bool leased = false;
                NamedEvent nodeAvailable;
                try
                {
                    int blockIndex = HeaderIndex(_nodeHeader->WriteStart, "WriteStart");
                    Node* node = this[blockIndex];
                    int next = Next(blockIndex);
                    if (next != HeaderIndex(_nodeHeader->ReadEnd, "ReadEnd"))
                    {
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
                        if (Interlocked.CompareExchange(ref _nodeHeader->WriteStart, next, blockIndex) == blockIndex)
                        {
                            leased = true;
                            index = blockIndex;
                            return node;
                        }
#pragma warning restore 0420

                        // Another thread has already acquired this node for writing, try again.
                        continue;
                    }

                    // No room, and the owner has shut down (ShuttingDown): return now rather than wait
                    if (Header->Shutdown == 1)
                        return null;
                    nodeAvailable = _nodeAvailable;
                }
                finally
                {
                    if (!leased)
                        Exit();
                }

                // No room is available: wait for a reader to free a node, then look again
                if (!WaitBeforeNextLook(nodeAvailable, timeout, deadline))
                    return null;
            }
        }

        /// <summary>
        /// Waits on one of the buffer's events before a call's next look at the ring, asking for at most <see cref="WaitSlice"/> ms and
        /// no later than the call's deadline. A post or a return sets an event only for a waiter at the boundary, and one set wakes
        /// one waiter, so the call looks again after each slice whether or not the event was set. The look after the last slice is
        /// the call's last look, at its deadline.
        /// </summary>
        /// <param name="namedEvent">The event, which the call took while it held its lease. A Close since then may have released it
        /// with the mapping.</param>
        /// <param name="timeout">The call's timeout in milliseconds.</param>
        /// <param name="deadline">The call's deadline, from <see cref="Deadline"/>.</param>
        /// <returns>false, without waiting, once the deadline has passed or for a timeout of 0: the look just made was the last. Also
        /// false if this instance was closed while the call waited.</returns>
        private bool WaitBeforeNextLook(NamedEvent namedEvent, int timeout, long deadline)
        {
            int wait;
            if (timeout == Timeout.Infinite)
                wait = WaitSlice;
            else if (timeout < 0)
                wait = timeout; // refused by the wait with ArgumentOutOfRangeException, as before
            else if (timeout == 0)
                return false;
            else
            {
                long left = deadline - System.Diagnostics.Stopwatch.GetTimestamp();
                if (left <= 0)
                    return false;
                // Rounded up, so that the last look comes at the deadline, not before it
                long leftMs = (left * 1000 + System.Diagnostics.Stopwatch.Frequency - 1) / System.Diagnostics.Stopwatch.Frequency;
                wait = leftMs < WaitSlice ? (int)leftMs : WaitSlice;
            }
            try
            {
                namedEvent.WaitOne(wait);
            }
            catch (ObjectDisposedException)
            {
                // This instance was closed while the call waited, which released the event with the mapping
                return false;
            }
            return true;
        }

        // The longest wait a waiting call asks for before it looks at the ring again. Where the system timer is coarser, as on Windows
        // (about 15.6 ms by default), the wait lasts a tick.
        private const int WaitSlice = 10;

        /// <summary>
        /// Takes a lease on the mapping for a look at the ring. A call's first look throws if this instance is closed, as any call
        /// after Close does; a later look finds that it was closed while the call waited, and the call then returns as at its timeout.
        /// </summary>
        /// <returns>true with the lease taken; false, for a later look, if this instance is closed.</returns>
        /// <exception cref="ObjectDisposedException">This instance is closed, at the call's first look.</exception>
        private bool EnterForLook(bool firstLook)
        {
            if (!firstLook)
                return TryEnter();
            Enter();
            return true;
        }

        /// <summary>
        /// When a call with this timeout gives up, as a <see cref="System.Diagnostics.Stopwatch"/> timestamp: the timeout is for the
        /// whole call, however often it waits.
        /// </summary>
        /// <param name="timeout">The call's timeout in milliseconds.</param>
        /// <returns>The deadline, or 0 for a timeout of 0, <see cref="Timeout.Infinite"/> or below, which have none.</returns>
        private static long Deadline(int timeout)
        {
            if (timeout <= 0)
                return 0;
            return System.Diagnostics.Stopwatch.GetTimestamp() + timeout * System.Diagnostics.Stopwatch.Frequency / 1000;
        }

        /// <summary>
        /// Makes a node available for reading after writing is complete
        /// </summary>
        /// <param name="node">An unsafe pointer to the node to return</param>
        /// <exception cref="InvalidOperationException">An index in the node header is outside the node list.</exception>
        protected virtual void PostNode(Node* node)
        {
            // Set the write flag for this node (the node is reserved so no need for locks). The exchange is a full fence: the node's
            // data and AmountWritten are visible before the flag, and the flag before the loads of WriteEnd below.
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
            Interlocked.Exchange(ref node->DoneWrite, 1);
#pragma warning restore 0420

            // Move the write pointer as far forward as we can
            // always starting from WriteEnd to make all contiguous
            // completed nodes available for reading.
            for (; ; )
            {
                int blockIndex = HeaderIndex(_nodeHeader->WriteEnd, "WriteEnd");
                node = this[blockIndex];
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
                if (Interlocked.CompareExchange(ref node->DoneWrite, 0, 1) != 1)
                {
                    // If we get here then another thread either another thread
                    // has already moved the write index or we have moved forward 
                    // as far as we can
                    return;
                }

                // Move the pointer one forward
                Interlocked.CompareExchange(ref _nodeHeader->WriteEnd, Next(blockIndex), blockIndex);
#pragma warning restore 0420

                // Signal the "data exists" event if read threads are waiting
                if (blockIndex == HeaderIndex(_nodeHeader->ReadStart, "ReadStart"))
                    _dataExists.Set();
            }
        }

        /// <summary>
        /// The node after node <paramref name="index"/> in the ring, as every creator links them; the node's own Next is not read
        /// (BufferAndRingSafety FR-010).
        /// </summary>
        private int Next(int index)
        {
            return index + 1 == NodeCount ? 0 : index + 1;
        }

        /// <summary>
        /// The node before node <paramref name="index"/> in the ring, as every creator links them; the node's own Prev is not read.
        /// </summary>
        private int Prev(int index)
        {
            return index == 0 ? NodeCount - 1 : index - 1;
        }

        /// <summary>
        /// An index read from the node header, checked against the node list (BufferAndRingSafety FR-010).
        /// </summary>
        /// <exception cref="InvalidOperationException">The index is outside 0 to <see cref="NodeCount"/> - 1.</exception>
        private int HeaderIndex(int index, string field)
        {
            if (index < 0 || index >= NodeCount)
                throw new InvalidOperationException(String.Format("The circular buffer \"{0}\" is damaged: its {1} is {2}, outside 0 to {3}.", Name, field, index, NodeCount - 1));
            return index;
        }

        /// <summary>
        /// Where node <paramref name="index"/>'s buffer starts, from <see cref="SharedBuffer.BufferStartPtr"/>: computed from the index,
        /// as every creator lays the buffers out, not read from the node's Offset (BufferAndRingSafety FR-010).
        /// </summary>
        private long NodeBufferPosition(int index)
        {
            return _nodeBufferOffset + (long)NodeBufferSize * index;
        }

        /// <summary>
        /// An amount of a node's buffer, limited to 0 to <see cref="NodeBufferSize"/>: an AmountWritten read from shared memory, or
        /// one about to be written there (BufferAndRingSafety FR-010).
        /// </summary>
        private int Clamp(int amount)
        {
            if (amount > NodeBufferSize)
                amount = NodeBufferSize;
            return amount < 0 ? 0 : amount;
        }

        /// <summary>
        /// Writes the byte array buffer to the next available node for writing
        /// </summary>
        /// <param name="source">Reference to the buffer to write</param>
        /// <param name="startIndex">The index within the buffer to start writing from</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for writing (default 1000ms)</param>
        /// <returns>The number of bytes written</returns>
        /// <remarks>The maximum number of bytes that can be written is the minimum of the length of <paramref name="source"/> and <see cref="NodeBufferSize"/>.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is negative or greater than the length of <paramref name="source"/>.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Write(byte[] source, int startIndex = 0, int timeout = 1000)
        {
            CheckArray(source, startIndex, "source");

            // Grab a node for writing, and a lease on the mapping until it is posted
            int index;
            Node* node = ReserveNodeForWriting(timeout, out index);
            if (node == null) return 0;

            int amount = 0;
            try
            {
                // Copy the data
                int count = Math.Min(source.Length - startIndex, NodeBufferSize);
                Marshal.Copy(source, startIndex, new IntPtr(BufferStartPtr + NodeBufferPosition(index)), count);
                amount = count;
            }
            finally
            {
                // Writing is complete, make readable; a copy that failed posts the node empty
                PostAndExit(node, amount);
            }
            return amount;
        }

        /// <summary>
        /// Writes the structure array buffer to the next available node for writing
        /// </summary>
        /// <param name="source">Reference to the buffer to write</param>
        /// <param name="startIndex">The index within the buffer to start writing from</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for writing (default 1000ms)</param>
        /// <returns>The number of elements written</returns>
        /// <remarks>The maximum number of elements that can be written is the minimum of the length of <paramref name="source"/> subtracted by <paramref name="startIndex"/> and <see cref="NodeBufferSize"/> divided by <code>FastStructure.SizeOf&gt;T&lt;()</code>.</remarks>        
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is negative or greater than the length of <paramref name="source"/>.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Write<T>(T[] source, int startIndex = 0, int timeout = 1000)
            where T : struct
        {
            CheckArray(source, startIndex, "source");
            // Before the node too: FastStructure throws here for a type it cannot copy
            int elementSize = FastStructure.SizeOf<T>();

            // Grab a node for writing, and a lease on the mapping until it is posted
            int index;
            Node* node = ReserveNodeForWriting(timeout, out index);
            if (node == null) return 0;

            int count = 0;
            try
            {
                // Write the data using the FastStructure class (much faster than the MemoryMappedViewAccessor WriteArray<T> method)
                int toWrite = Math.Min(source.Length - startIndex, NodeBufferSize / elementSize);
                WriteArrayLeased<T>(source, startIndex, toWrite, NodeBufferPosition(index));
                count = toWrite;
            }
            finally
            {
                // Writing is complete, make node readable; a copy that failed posts the node empty
                PostAndExit(node, count * elementSize);
            }
            return count;
        }

        /// <summary>
        /// Writes the structure to the next available node for writing
        /// </summary>
        /// <typeparam name="T">The structure type to be written</typeparam>
        /// <param name="source">The structure to be written</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for writing (default 1000ms)</param>
        /// <returns>The number of bytes written - larger than 0 if successful</returns>
        /// <exception cref="ArgumentOutOfRangeException">If the size of the <typeparamref name="T"/> structure is larger than <see cref="NodeBufferSize"/>.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Write<T>(ref T source, int timeout = 1000)
            where T : struct
        {
            int structSize = FastStructure.SizeOf<T>();
            if (structSize > NodeBufferSize)
                throw new ArgumentOutOfRangeException("T", "The size of structure " + typeof(T).Name + " is larger than NodeBufferSize");

            // Attempt to retrieve a node for writing, and a lease on the mapping until it is posted
            int index;
            Node* node = ReserveNodeForWriting(timeout, out index);
            if (node == null) return 0;

            int amount = 0;
            try
            {
                // Copy the data using the MemoryMappedViewAccessor
                WriteStructureLeased<T>(ref source, NodeBufferPosition(index));
                amount = structSize;
            }
            finally
            {
                // Make the node readable; a copy that failed posts it empty
                PostAndExit(node, amount);
            }
            return amount;
        }

        /// <summary>
        /// Writes <paramref name="length"/> bytes from <paramref name="source"/> to the next available node for writing
        /// </summary>
        /// <param name="source">Pointer to the buffer to copy</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available (default 1000ms)</param>
        /// <param name="length">The number of bytes to attempt to write</param>
        /// <returns>The number of bytes written</returns>
        /// <remarks>The maximum number of bytes that can be written is the minimum of <paramref name="length"/> and <see cref="NodeBufferSize"/>.</remarks>        
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Write(IntPtr source, int length, int timeout = 1000)
        {
            CheckLength(length);

            // Grab a node for writing, and a lease on the mapping until it is posted
            int index;
            Node* node = ReserveNodeForWriting(timeout, out index);
            if (node == null) return 0;

            int amount = 0;
            try
            {
                // Copy the data
                int count = Math.Min(length, NodeBufferSize);
                WriteLeased(source, count, NodeBufferPosition(index));
                amount = count;
            }
            finally
            {
                // Writing is complete, make readable; a copy that failed posts the node empty
                PostAndExit(node, amount);
            }
            return amount;
        }

        /// <summary>
        /// Reserves a node for writing and then calls the provided <paramref name="writeFunc"/> to perform the write operation.
        /// </summary>
        /// <param name="writeFunc">A function to used to write to the node's buffer. The first parameter is a pointer to the node's buffer. 
        /// The provided function should return the number of bytes written.</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for writing (default 1000ms)</param>
        /// <returns>The number of bytes written: what <paramref name="writeFunc"/> returned, limited to 0 to <see cref="NodeBufferSize"/></returns>
        /// <remarks>The pointer stays valid until <paramref name="writeFunc"/> returns, even if the buffer is closed meanwhile.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="writeFunc"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Write(Func<IntPtr, int> writeFunc, int timeout = 1000)
        {
            if (writeFunc == null)
                throw new ArgumentNullException("writeFunc");

            // Grab a node for writing, and a lease on the mapping until it is posted
            int index;
            Node* node = ReserveNodeForWriting(timeout, out index);
            if (node == null) return 0;

            int amount = 0;
            try
            {
                // Pass destination IntPtr to custom write function, and hold what it says it wrote to the node's size
                amount = Clamp(writeFunc(new IntPtr(BufferStartPtr + NodeBufferPosition(index))));
            }
            finally
            {
                // Writing is complete, make readable; a callback that threw posts the node empty
                PostAndExit(node, amount);
            }
            return amount;
        }

        /// <summary>
        /// Posts a node an overload reserved, holding <paramref name="amount"/> bytes, 0 for a write that failed, and releases the
        /// overload's lease on the mapping.
        /// </summary>
        private void PostAndExit(Node* node, int amount)
        {
            try
            {
                node->AmountWritten = Clamp(amount);
                PostNode(node);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// The check of an array and a start index in it that each array overload makes before it reserves a node, so that a bad
        /// argument leaves the ring as it was. A start index equal to the array's length copies nothing.
        /// </summary>
        private static void CheckArray(Array array, int startIndex, string paramName)
        {
            if (array == null)
                throw new ArgumentNullException(paramName);
            if (startIndex < 0 || startIndex > array.Length)
                throw new ArgumentOutOfRangeException("startIndex", startIndex, "The start index must be from 0 to the array's length, " + array.Length + ".");
        }

        #endregion

        #region Node Reading

        /// <summary>
        /// Returns a copy of the shared memory header
        /// </summary>
        /// <exception cref="ObjectDisposedException">This instance is closed.</exception>
        public NodeHeader ReadNodeHeader()
        {
            Enter();
            try
            {
                return (NodeHeader)Marshal.PtrToStructure(new IntPtr(_nodeHeader), typeof(NodeHeader));
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// Attempts to reserve a node from the linked-list for reading with the specified timeout
        /// </summary>
        /// <param name="timeout">The number of milliseconds to wait if a node is not immediately available for reading.</param>
        /// <returns>An unsafe pointer to the node if successful, otherwise null: at the timeout, at once when the owner has shut down and
        /// nothing is left to read, or when this instance is closed while the call waits.</returns>
        /// <exception cref="ObjectDisposedException">This instance was closed before the call.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        /// <remarks>An empty node, from a write that failed or wrote nothing, is never returned: it is returned for writing unread, and the
        /// call goes on to the next node. The node is valid until the buffer is closed: a subclass that reads it itself must return it before
        /// Close is called, on this thread or another.</remarks>
        protected virtual Node* GetNodeForReading(int timeout)
        {
            // The caller holds no lease on the mapping, as before
            int index;
            Node* node = TakeNodeForReading(timeout, out index);
            if (node != null)
                Exit();
            return node;
        }

        /// <summary>
        /// The overloads' reservation of a node for reading: the node and its index, with a lease on the mapping, which the caller
        /// releases once it has returned the node; or null at the timeout, at once when the owner has shut down and the ring is empty,
        /// or when this instance is closed while the call waits.
        /// </summary>
        private Node* ReserveNodeForReading(int timeout, out int index)
        {
            if (!_overridesGetNodeForReading)
                return TakeNodeForReading(timeout, out index);

            // A subclass's own reservation holds no lease of its own, so this one keeps the node it returns mapped until it is returned
            Enter();
            Node* node = null;
            index = -1;
            try
            {
                node = GetNodeForReading(timeout);
                if (node != null)
                    index = IndexOf(node);
            }
            finally
            {
                if (index < 0)
                    Exit();
            }
            return node;
        }

        /// <summary>
        /// Reserves a node for reading, and returns it with a lease on the mapping for the caller to release. Each look at the ring
        /// holds a lease of its own, and a wait holds none, so that a wait never keeps a closed instance's mapping.
        /// </summary>
        /// <returns>The node, and its index in <paramref name="index"/>; or null at the timeout, at once when the owner has shut down
        /// and the ring is empty, or when this instance is closed while the call waits.</returns>
        /// <exception cref="ObjectDisposedException">This instance was closed before the call.</exception>
        /// <exception cref="InvalidOperationException">An index in the node header is outside the node list.</exception>
        private Node* TakeNodeForReading(int timeout, out int index)
        {
            index = -1;
            long deadline = Deadline(timeout);
            int skips = 0;
            for (bool firstLook = true; ; firstLook = false)
            {
                if (!EnterForLook(firstLook))
                    return null;
                bool leased = false;
                bool skipped = false;
                NamedEvent dataExists = null;
                try
                {
                    int blockIndex = HeaderIndex(_nodeHeader->ReadStart, "ReadStart");
                    Node* node = this[blockIndex];
                    if (blockIndex != HeaderIndex(_nodeHeader->WriteEnd, "WriteEnd"))
                    {
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
                        if (Interlocked.CompareExchange(ref _nodeHeader->ReadStart, Next(blockIndex), blockIndex) != blockIndex)
                        {
                            // Another thread has already acquired this node for reading, try again
                            continue;
                        }
#pragma warning restore 0420

                        if (Clamp(node->AmountWritten) != 0)
                        {
                            leased = true;
                            index = blockIndex;
                            return node;
                        }

                        // An empty node, from a write that failed or wrote nothing: give it back unread, and look again
                        ReturnNode(node);
                        skipped = true;
                    }
                    else
                    {
                        // Nothing to read, and the owner has shut down (ShuttingDown): return now rather than wait
                        if (Header->Shutdown == 1)
                            return null;
                        dataExists = _dataExists;
                    }
                }
                finally
                {
                    if (!leased)
                        Exit();
                }

                if (skipped)
                {
                    // Look again at once, without waiting, unless the call's time is up: a stream of empty writes must not hold it
                    // past its timeout
                    if (SkipsEnd(timeout, deadline, ++skips))
                        return null;
                    continue;
                }

                // No data is available: wait for a writer to post a node, then look again
                if (!WaitBeforeNextLook(dataExists, timeout, deadline))
                    return null;
            }
        }

        /// <summary>
        /// Whether a read that has just skipped an empty node gives up rather than look again: at its deadline, or for a timeout of 0,
        /// once it has skipped as many nodes as the ring has, more than were in the ring when it started.
        /// </summary>
        private bool SkipsEnd(int timeout, long deadline, int skips)
        {
            if (timeout == 0)
                return skips >= NodeCount;
            if (timeout < 0)
                return false; // Timeout.Infinite, or a timeout below it that the wait refuses
            return System.Diagnostics.Stopwatch.GetTimestamp() >= deadline;
        }

        /// <summary>
        /// Returns a node to the available list of nodes for writing.
        /// </summary>
        /// <param name="node">An unsafe pointer to the node to be returned</param>
        /// <exception cref="InvalidOperationException">An index in the node header is outside the node list.</exception>
        protected virtual void ReturnNode(Node* node)
        {
            // Keep it clean and reset AmountWritten to prepare it for next Write, while the node is still this reader's: once the flag
            // is set, another reader's walk can free it for a writer
            node->AmountWritten = 0;

            // Set the finished reading flag for this node (the node is reserved so no need for locks). The exchange is a full fence:
            // AmountWritten is visible before the flag, and the flag before the loads of ReadEnd below.
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
            Interlocked.Exchange(ref node->DoneRead, 1);
#pragma warning restore 0420

            // Move the read pointer forward as far as possible
            // always starting from ReadEnd to make all contiguous
            // read nodes available for writing.
            for (; ; )
            {
                int blockIndex = HeaderIndex(_nodeHeader->ReadEnd, "ReadEnd");
                node = this[blockIndex];
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
                if (Interlocked.CompareExchange(ref node->DoneRead, 0, 1) != 1)
                {
                    // If we get here then another read thread has already moved the pointer
                    // or we have moved ReadEnd as far forward as we can
                    return;
                }

                // Move the pointer forward one node
                Interlocked.CompareExchange(ref _nodeHeader->ReadEnd, Next(blockIndex), blockIndex);
#pragma warning restore 0420

               // If a writer thread is waiting on "node available" signal the event
                if (Prev(blockIndex) == HeaderIndex(_nodeHeader->WriteStart, "WriteStart"))
                        _nodeAvailable.Set();
            }
        }

        /// <summary>
        /// Reads the next available node for reading into the specified byte array
        /// </summary>
        /// <param name="destination">Reference to the buffer</param>
        /// <param name="startIndex">The index within the buffer to start writing from</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for reading (default 1000ms)</param>
        /// <returns>The number of bytes read</returns>
        /// <remarks>The maximum number of bytes that can be read is the minimum of the length of <paramref name="destination"/> subtracted by <paramref name="startIndex"/> and <see cref="NodeBufferSize"/>.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is negative or greater than the length of <paramref name="destination"/>.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Read(byte[] destination, int startIndex = 0, int timeout = 1000)
        {
            CheckArray(destination, startIndex, "destination");

            // A node to read, and a lease on the mapping until it is returned
            int index;
            Node* node = ReserveNodeForReading(timeout, out index);
            if (node == null) return 0;

            try
            {
                int amount = Math.Min(destination.Length - startIndex, Clamp(node->AmountWritten));

                // Copy the data
                Marshal.Copy(new IntPtr(BufferStartPtr + NodeBufferPosition(index)), destination, startIndex, amount);

                return amount;
            }
            finally
            {
                // Return the node for further writing, whether or not the copy succeeded
                ReturnAndExit(node);
            }
        }

        /// <summary>
        /// Reads the next available node for reading into the specified structure array
        /// </summary>
        /// <typeparam name="T">The structure type to be read</typeparam>
        /// <param name="destination">Reference to the buffer</param>
        /// <param name="startIndex">The index within the destination to start writing to.</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for reading (default 1000ms)</param>
        /// <returns>The number of elements read into destination</returns>
        /// <remarks>The maximum number of elements that can be read is the minimum of the length of <paramref name="destination"/> subtracted by <paramref name="startIndex"/> and <see cref="Node.AmountWritten"/> divided by <code>FastStructure.SizeOf&gt;T&lt;()</code>.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is negative or greater than the length of <paramref name="destination"/>.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Read<T>(T[] destination, int startIndex = 0, int timeout = 1000)
            where T : struct
        {
            CheckArray(destination, startIndex, "destination");
            // Before the node too: FastStructure throws here for a type it cannot copy
            int elementSize = FastStructure.SizeOf<T>();

            // A node to read, and a lease on the mapping until it is returned
            int index;
            Node* node = ReserveNodeForReading(timeout, out index);
            if (node == null) return 0;

            try
            {
                // Copy the data using the FastStructure class (much faster than the MemoryMappedViewAccessor ReadArray<T> method)
                int count = Math.Min(destination.Length - startIndex, Clamp(node->AmountWritten) / elementSize);
                ReadArrayLeased<T>(destination, startIndex, count, NodeBufferPosition(index));

                return count;
            }
            finally
            {
                // Return the node for further writing, whether or not the copy succeeded
                ReturnAndExit(node);
            }
        }

        /// <summary>
        /// Reads the next available node for reading into the a structure
        /// </summary>
        /// <typeparam name="T">The structure type to be read</typeparam>
        /// <param name="destination">The resulting structure if successful otherwise default(T)</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for reading (default 1000ms)</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ArgumentOutOfRangeException">If the size of <typeparamref name="T"/> is larger than <see cref="NodeBufferSize"/>.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Read<T>(out T destination, int timeout = 1000)
            where T: struct
        {
            int structSize = FastStructure.SizeOf<T>();
            if (structSize > NodeBufferSize)
                throw new ArgumentOutOfRangeException("T", "The size of structure " + typeof(T).Name + " is larger than NodeBufferSize");

            // Attempt to retrieve a node, and a lease on the mapping until it is returned
            int index;
            Node* node = ReserveNodeForReading(timeout, out index);
            if (node == null)
            {
                destination = default(T);
                return 0;
            }

            try
            {
                // Copy the data using the MemoryMappedViewAccessor
                ReadStructureLeased<T>(out destination, NodeBufferPosition(index));

                return structSize;
            }
            finally
            {
                // Return the node for further writing, whether or not the copy succeeded
                ReturnAndExit(node);
            }
        }

        /// <summary>
        /// Reads the next available node for reading into the specified memory location with the specified length
        /// </summary>
        /// <param name="destination">Pointer to the buffer</param>
        /// <param name="length">The maximum length of <paramref name="destination"/></param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for reading (default 1000ms)</param>
        /// <returns>The number of bytes read</returns>
        /// <remarks>The maximum number of bytes that can be read is the minimum of the <paramref name="length"/> and <see cref="Node.AmountWritten"/>.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Read(IntPtr destination, int length, int timeout = 1000)
        {
            CheckLength(length);

            // A node to read, and a lease on the mapping until it is returned
            int index;
            Node* node = ReserveNodeForReading(timeout, out index);
            if (node == null) return 0;

            try
            {
                int amount = Math.Min(length, Clamp(node->AmountWritten));

                // Copy the data
                ReadLeased(destination, amount, NodeBufferPosition(index));

                return amount;
            }
            finally
            {
                // Return the node for further writing, whether or not the copy succeeded
                ReturnAndExit(node);
            }
        }

        /// <summary>
        /// Reserves a node for reading and then calls the provided <paramref name="readFunc"/> to perform the read operation.
        /// </summary>
        /// <param name="readFunc">A function used to read from the node's buffer. The first parameter is a pointer to the node's buffer. 
        /// The provided function should return the number of bytes read.</param>
        /// <param name="timeout">The maximum number of milliseconds to wait for a node to become available for reading (default 1000ms)</param>
        /// <returns>The number of bytes read</returns>
        /// <remarks>The pointer stays valid until <paramref name="readFunc"/> returns, even if the buffer is closed meanwhile.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="readFunc"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The buffer is damaged: an index in its node header is outside the node list.</exception>
        public virtual int Read(Func<IntPtr, int> readFunc, int timeout = 1000)
        {
            if (readFunc == null)
                throw new ArgumentNullException("readFunc");

            // A node to read, and a lease on the mapping until it is returned
            int index;
            Node* node = ReserveNodeForReading(timeout, out index);
            if (node == null) return 0;

            try
            {
                // Pass pointer to buffer directly to custom read function
                return readFunc(new IntPtr(BufferStartPtr + NodeBufferPosition(index)));
            }
            finally
            {
                // Return the node for further writing, whether or not the callback threw
                ReturnAndExit(node);
            }
        }

        /// <summary>
        /// Returns a node an overload reserved for further writing, and releases the overload's lease on the mapping.
        /// </summary>
        private void ReturnAndExit(Node* node)
        {
            try
            {
                ReturnNode(node);
            }
            finally
            {
                Exit();
            }
        }

        #endregion
    }
}
