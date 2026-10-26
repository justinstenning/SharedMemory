// SharedMemory (File: SharedMemory\RpcBuffer.cs)
// Copyright (c) 2020 Justin Stenning
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SharedMemory
{
    // Only supported in .NET 4.5+ and .NET Standard 2.0

    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    internal enum InstanceType
    {
        Owner,
        Opener
    }

    /// <summary>
    /// The available RPC protocols
    /// </summary>
    public enum RpcProtocol
    {
        /// <summary>
        /// Version 1 - messages are split into packets that fit the buffer capacity and include a protocol header in each packet
        /// </summary>
        V1 = 1
    }

    /// <summary>
    /// Extension methods for adorning RPC tasks
    /// </summary>

    public static class ResponseTaskHelper
    {
        static readonly RpcResponse CancelledRpcResponse = new RpcResponse(false, null);
        static readonly Task<RpcResponse> CancelledRpcResponseTask = Task.FromResult(CancelledRpcResponse);

        /// <summary>
        /// Adds timeout and manual cancellation capabilities to an existing Task
        /// </summary>
        /// <param name="task"></param>
        /// <param name="millisecondsTimeout"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<RpcResponse> TimeoutOrCancel(this Task<RpcResponse> task, int millisecondsTimeout, CancellationToken cancellationToken = default)
        {
            if (task.IsCompleted)
            {
                // the task has already completed
                // No proxy necessary.
                return await task.ConfigureAwait(false);
            }

            // Short-circuit #2: zero timeout
            if (millisecondsTimeout == 0)
            {
                // We've already timed out.
                return new RpcResponse(false, null);
            }

            // WaitAsync is called as a static method, not as task.WaitAsync: in a build for .NET 6 or later, task.WaitAsync
            // binds to Task<T>.WaitAsync, which throws TaskCanceledException where this one returns an unsuccessful response.
            if (millisecondsTimeout == Timeout.Infinite)
            {
                return await WaitAsync(task, cancellationToken).ConfigureAwait(false);
            }

            using (var cts = new CancellationTokenSource(millisecondsTimeout))
            {
                var timeoutToken = cts.Token;
                return await WaitAsync(WaitAsync(task, cancellationToken), timeoutToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Asynchronously waits for the task to complete, or for the cancellation token to be canceled.
        /// </summary>
        /// <param name="this">The task to wait for. May not be <c>null</c>.</param>
        /// <param name="cancellationToken">The cancellation token that cancels the wait.</param>
        private static Task<RpcResponse> WaitAsync(this Task<RpcResponse> @this, CancellationToken cancellationToken)
        {
            if (@this == null)
                throw new ArgumentNullException(nameof(@this));

            if (!cancellationToken.CanBeCanceled)
                return @this;
            if (cancellationToken.IsCancellationRequested)
                return CancelledRpcResponseTask;
            return DoWaitAsync(@this, cancellationToken);
        }

        private static async Task<RpcResponse> DoWaitAsync(Task<RpcResponse> task, CancellationToken cancellationToken)
        {
            using (var cancelTaskSource = new RpcResponseCancellationTokenTaskSource(cancellationToken))
                return await (await Task.WhenAny(task, cancelTaskSource.Task).ConfigureAwait(false)).ConfigureAwait(false);
        }
        
        /// <summary>
        /// Holds the task for a cancellation token, as well as the token registration. The registration is disposed when this instance is disposed.
        /// </summary>
        public sealed class RpcResponseCancellationTokenTaskSource : IDisposable
        {
            /// <summary>
            /// The cancellation token registration, if any. This is <c>null</c> if the registration was not necessary.
            /// </summary>
            private readonly IDisposable _registration;

            /// <summary>
            /// Creates a task for the specified cancellation token, registering with the token if necessary.
            /// </summary>
            /// <param name="cancellationToken">The cancellation token to observe.</param>
            public RpcResponseCancellationTokenTaskSource(CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Task = CancelledRpcResponseTask;
                    return;
                }
                var tcs = new TaskCompletionSource<RpcResponse>();
                _registration = cancellationToken.Register(() => tcs.TrySetResult(CancelledRpcResponse), useSynchronizationContext: false);
                Task = tcs.Task;
            }

            /// <summary>
            /// Gets the task for the source cancellation token.
            /// </summary>
            public Task<RpcResponse> Task { get; private set; }

            /// <summary>
            /// Disposes the cancellation token registration, if any. Note that this may cause <see cref="Task"/> to never complete.
            /// </summary>
            public void Dispose()
            {
                _registration?.Dispose();
            }
        }
    }

    /// <summary>
    /// The RPC message type
    /// </summary>
    public enum MessageType : byte
    {
        /// <summary>
        /// A request message
        /// </summary>
        RpcRequest = 1,
        /// <summary>
        /// A response message
        /// </summary>
        RpcResponse = 2,
        /// <summary>
        /// An error message
        /// </summary>
        ErrorInRpc = 3,
    }

    /// <summary>
    /// The V1 protocol header
    /// </summary>
    public struct RpcProtocolHeaderV1
    {
        /// <summary>
        /// Message Type
        /// </summary>
        public MessageType MsgType;
        /// <summary>
        /// Message Id
        /// </summary>
        public ulong MsgId;
        /// <summary>
        /// Total message size
        /// </summary>
        public int PayloadSize;
        /// <summary>
        /// The current packet number
        /// </summary>
        public ushort CurrentPacket;
        /// <summary>
        /// The total number of packets in the message
        /// </summary>
        public ushort TotalPackets;
        /// <summary>
        /// If a response, the Id of the remote message this is a response to
        /// </summary>
        public ulong ResponseId;
    }

    /// <summary>
    /// Represents a request to be sent on the channel
    /// </summary>
    public class RpcRequest
    {
        internal RpcRequest() : this(true) { }

        internal RpcRequest(bool continuesOnThreadPool)
        {
            ContinuesOnThreadPool = continuesOnThreadPool;
#if NET46_OR_GREATER || NETSTANDARD || NETCOREAPP
            ResponseReady = continuesOnThreadPool
                ? new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
                : new TaskCompletionSource<RpcResponse>();
#else
            ResponseReady = new TaskCompletionSource<RpcResponse>();
#endif
        }

        /// <summary>
        /// The message Id
        /// </summary>
        public ulong MsgId { get; set; }
        /// <summary>
        /// The message type
        /// </summary>
        public MessageType MsgType { get; set; }
        /// <summary>
        /// The message payload (if any)
        /// </summary>
        public byte[] Data { get; set; }
        /// <summary>
        /// A wait event that is signaled when a response is ready
        /// </summary>
        public TaskCompletionSource<RpcResponse> ResponseReady { get; }
        /// <summary>
        /// Was the request successful
        /// </summary>
        public bool IsSuccess { get; internal set; }
        /// <summary>
        /// When the request was created
        /// </summary>
        public DateTime Created { get { return created.ToLocalTime(); } }
        readonly DateTime created = DateTime.UtcNow; // turned into local time when read, rather than for every request
        /// <summary>
        /// The packets of the message received so far
        /// </summary>
        internal int PacketsReceived { get; set; }
        /// <summary>
        /// Whether code awaiting the response must run on the thread pool, rather than on the read thread that completes it
        /// </summary>
        internal bool ContinuesOnThreadPool { get; }
    }

    /// <summary>
    /// Represents the result of a remote request.
    /// </summary>
    public class RpcResponse
    {
        /// <summary>
        /// Constructs an RpcResponse
        /// </summary>
        /// <param name="success">was it a success</param>
        /// <param name="data">the message data (if any)</param>
        public RpcResponse(bool success, byte[] data)
        {
            this.Success = success;
            this.Data = data;
        }

        /// <summary>
        /// If the request was successful
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// The returned result (if applicable)
        /// </summary>
        public byte[] Data { get; }
    }

    /// <summary>
    /// Represents the channel statistics of an <see cref="RpcBuffer"/> instance
    /// </summary>
    public class RpcStatistics
    {
        /// <summary>
        /// The protocol overhead per packet
        /// </summary>
        public int ProtocolOverheadPerPacket { get; internal set; }

        /// <summary>
        /// Bytes read from channel (excluding protocol overhead)
        /// </summary>
        public ulong BytesRead { get { return (ulong)Interlocked.Read(ref bytesRead); } }
        /// <summary>
        /// Number of packets read from channel
        /// </summary>
        public ulong PacketsRead { get { return (ulong)Interlocked.Read(ref packetsRead); } }
        /// <summary>
        /// The largest packet read from channel (excluding protocol overhead)
        /// </summary>
        public int ReadingMaxPacketSize { get; private set; }
        /// <summary>
        /// The size of last packet read from channel (excluding protocol overhead)
        /// </summary>
        public int ReadingLastPacketSize { get; private set; } = -1;
        /// <summary>
        /// The size of last message read from channel (excluding protocol overhead)
        /// </summary>
        public int ReadingLastMessageSize { get; private set; } = -1;

        /// <summary>
        /// The total number of messages received
        /// </summary>
        public ulong MessagesReceived { get { return RequestsReceived + ResponsesReceived + ErrorsReceived; } }

        /// <summary>
        /// The number of request messages received
        /// </summary>
        public ulong RequestsReceived { get { return (ulong)Interlocked.Read(ref requestsReceived); } }

        /// <summary>
        /// The number of response messages received
        /// </summary>
        public ulong ResponsesReceived { get { return (ulong)Interlocked.Read(ref responsesReceived); } }

        /// <summary>
        /// The number of error message received
        /// </summary>
        public ulong ErrorsReceived { get { return (ulong)Interlocked.Read(ref errorsReceived); } }

        /// <summary>
        /// The number of bytes written to channel (excluding protocol overhead)
        /// </summary>
        public ulong BytesWritten { get { return (ulong)Interlocked.Read(ref bytesWritten); } }
        /// <summary>
        /// The number of packets written to channel
        /// </summary>
        public ulong PacketsWritten { get { return (ulong)Interlocked.Read(ref packetsWritten); } }
        /// <summary>
        /// The largest packet written to channel (excluding protocol overhead)
        /// </summary>
        public int WritingMaxPacketSize { get; private set; }

        /// <summary>
        /// The size of last packet written to channel (excluding protocol overhead)
        /// </summary>
        public int WritingLastPacketSize { get; private set; } = -1;

        /// <summary>
        /// The size of last message written to channel (excluding protocol overhead)
        /// </summary>
        public int WritingLastMessageSize { get; private set; } = -1;

        /// <summary>
        /// Number of response messages received that were discarded (provided a non-existent message Id)
        /// </summary>
        public ulong DiscardedResponses { get { return (ulong)Interlocked.Read(ref discardedResponses); } }
        /// <summary>
        /// The response message Id that was last discarded
        /// </summary>
        public ulong LastDiscardedResponseId { get { return unchecked((ulong)Interlocked.Read(ref lastDiscardedResponseId)); } }

        /// <summary>
        /// The total number of messages sent
        /// </summary>
        public ulong MessagesSent { get { return RequestsSent + ResponsesSent + ErrorsSent; } }

        /// <summary>
        /// The number of request messages sent
        /// </summary>
        public ulong RequestsSent { get { return (ulong)Interlocked.Read(ref requestsSent); } }

        /// <summary>
        /// The number of response messages sent
        /// </summary>
        public ulong ResponsesSent { get { return (ulong)Interlocked.Read(ref responsesSent); } }

        /// <summary>
        /// The number of error messages sent
        /// </summary>
        public ulong ErrorsSent { get { return (ulong)Interlocked.Read(ref errorsSent); } }

        /// <summary>
        /// Number of timeouts
        /// </summary>
        public ulong Timeouts { get { return (ulong)Interlocked.Read(ref timeouts); } }
        /// <summary>
        /// DateTime of last timeout
        /// </summary>
        public DateTime LastTimeout { get { return DateTime.FromBinary(Interlocked.Read(ref lastTimeout)); } }
        DateTime StartWaitWriteTimestamp { get; set; }
        DateTime EndWaitWriteTimestamp { get; set; }
        /// <summary>
        /// Maximum Ticks waited for available write slot
        /// </summary>
        public long MaxWaitWriteTicks { get { return Interlocked.Read(ref maxWaitWriteTicks); } }
        DateTime StartWaitReadTimestamp { get; set; }
        DateTime EndWaitReadTimestamp { get; set; }
        /// <summary>
        /// Maximum Ticks waiting for read slot (cannot exceed 1sec)
        /// </summary>
        public long MaxWaitReadTicks { get { return Interlocked.Read(ref maxWaitReadTicks); } }

        // Messages are sent, and time out, on several threads at once, so the 64-bit values change only through Interlocked
        long bytesRead, packetsRead, requestsReceived, responsesReceived, errorsReceived;
        long bytesWritten, packetsWritten, discardedResponses, lastDiscardedResponseId;
        long requestsSent, responsesSent, errorsSent, timeouts, lastTimeout;
        long maxWaitWriteTicks = -1, maxWaitReadTicks = -1;

        // The wait timings keep only differences, so they read UtcNow, which skips Now's time zone lookup
        internal void StartWaitRead()
        {
            StartWaitReadTimestamp = DateTime.UtcNow;
        }

        internal void ReadPacket(int bytes)
        {
            EndWaitReadTimestamp = DateTime.UtcNow;

            var ticks = EndWaitReadTimestamp.Ticks - StartWaitReadTimestamp.Ticks;
            if (ticks > maxWaitReadTicks) // only the read thread sets it
            {
                Interlocked.Exchange(ref maxWaitReadTicks, ticks);
            }

            Interlocked.Increment(ref packetsRead);
            Interlocked.Add(ref bytesRead, bytes);
            ReadingLastPacketSize = bytes;
            if (bytes > ReadingMaxPacketSize)
            {
                ReadingMaxPacketSize = bytes;
            }
        }

        internal void StartWaitWrite()
        {
            StartWaitWriteTimestamp = DateTime.UtcNow;
        }

        internal void WritePacket(int bytes)
        {
            EndWaitWriteTimestamp = DateTime.UtcNow;

            var ticks = EndWaitWriteTimestamp.Ticks - StartWaitWriteTimestamp.Ticks;
            if (ticks > maxWaitWriteTicks) // set only under the send lock
            {
                Interlocked.Exchange(ref maxWaitWriteTicks, ticks);
            }

            Interlocked.Increment(ref packetsWritten);
            Interlocked.Add(ref bytesWritten, bytes);
            WritingLastPacketSize = bytes;
            if (bytes > WritingMaxPacketSize)
            {
                WritingMaxPacketSize = bytes;
            }
        }

        internal void MessageReceived(MessageType msgType, int size)
        {
            ReadingLastMessageSize = size;
            switch (msgType)
            {
                case MessageType.RpcRequest:
                    Interlocked.Increment(ref requestsReceived);
                    break;
                case MessageType.RpcResponse:
                    Interlocked.Increment(ref responsesReceived);
                    break;
                case MessageType.ErrorInRpc:
                    Interlocked.Increment(ref errorsReceived);
                    break;
            }
        }

        internal void MessageSent(MessageType msgType, int size)
        {
            WritingLastMessageSize = size;
            switch (msgType)
            {
                case MessageType.RpcRequest:
                    Interlocked.Increment(ref requestsSent);
                    break;
                case MessageType.RpcResponse:
                    Interlocked.Increment(ref responsesSent);
                    break;
                case MessageType.ErrorInRpc:
                    Interlocked.Increment(ref errorsSent);
                    break;
            }
        }

        internal void Timeout()
        {
            Interlocked.Increment(ref timeouts);
            Interlocked.Exchange(ref lastTimeout, DateTime.Now.ToBinary());
        }

        internal void DiscardResponse(ulong msgId)
        {
            Interlocked.Increment(ref discardedResponses);
            Interlocked.Exchange(ref lastDiscardedResponseId, unchecked((long)msgId));
        }

        /// <summary>
        /// Reset all statistics
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref timeouts, 0);
            Interlocked.Exchange(ref lastTimeout, DateTime.MinValue.ToBinary());
            Interlocked.Exchange(ref packetsRead, 0);
            Interlocked.Exchange(ref bytesRead, 0);
            Interlocked.Exchange(ref maxWaitReadTicks, -1);
            ReadingMaxPacketSize = 0;
            ReadingLastMessageSize = -1;
            ReadingLastPacketSize = -1;
            Interlocked.Exchange(ref requestsReceived, 0);
            Interlocked.Exchange(ref responsesReceived, 0);
            Interlocked.Exchange(ref errorsReceived, 0);
            Interlocked.Exchange(ref packetsWritten, 0);
            Interlocked.Exchange(ref bytesWritten, 0);
            Interlocked.Exchange(ref maxWaitWriteTicks, -1);
            WritingMaxPacketSize = 0;
            WritingLastMessageSize = -1;
            WritingLastPacketSize = -1;
            Interlocked.Exchange(ref requestsSent, 0);
            Interlocked.Exchange(ref responsesSent, 0);
            Interlocked.Exchange(ref errorsSent, 0);
            Interlocked.Exchange(ref discardedResponses, 0);
            Interlocked.Exchange(ref lastDiscardedResponseId, 0);
        }
    }

    /// <summary>
    /// A simple RPC channel with two ends: the owner, which creates it, and the opener, which joins it
    /// </summary>
    /// <remarks>A third instance on a channel joins as a second opener, and the two openers compete for each packet the owner sends:
    /// a request from the owner that fits in one packet is answered by one opener or the other, but any other call on the channel
    /// can fail.</remarks>
    public class RpcBuffer : IDisposable
    {
        // The objects an RpcBuffer finds its other end by. Changing any of them stops this version pairing with the one before it.
        const string ElectionMutexSuffix = "SharedMemory_OwnerMutex";
        const string OwnerReadsSuffix = "_Owner_SharedMemory_MMF";
        const string OwnerWritesSuffix = "_Opener_SharedMemory_MMF";
        // The name 2.3 and earlier elect with. On Windows the owner holds it, so that an older RpcBuffer fails instead of joining.
        const string PreviousElectionMutexSuffix = "SharedMemory_MasterMutex";

        private Mutex electionMutex;
        private Mutex previousElectionMutex;
        private long _disposed = 0;

        /// <summary>
        /// Whether the RpcBuffer has been disposed
        /// </summary>
        protected bool Disposed
        {
            get
            {
                return Interlocked.Read(ref _disposed) != 0;
            }
        }

        /// <summary>
        /// Whether <see cref="Dispose()"/> has closed the buffers. It usually has by the time it returns. If the thread that reads the
        /// buffer, or a send under way, has not let go of them within about 1 s, they close in the background once they are free,
        /// and this turns true then.
        /// </summary>
        public bool DisposeFinished
        {
            get
            {
                return Interlocked.Read(ref _disposed) == 2;
            }
        }

        /// <summary>
        /// Whether this instance was first on the channel: it created both buffers and set their capacity and node count.
        /// False when it opened the buffers another instance created, and took their capacity and node count.
        /// </summary>
        /// <remarks>Set by the constructor, and still readable after <see cref="Dispose()"/>.</remarks>
        public bool IsChannelOwner
        {
            get
            {
                return instanceType == InstanceType.Owner;
            }
        }

        private readonly InstanceType instanceType;
        private readonly RpcProtocol protocolVersion;
        private readonly int protocolLength;
        private readonly int bufferCapacity;
        private readonly int bufferNodeCount;
        private readonly int msgBufferLength; // The amount of room left in the node after protocol header
        private readonly bool sendMessageOverridden;

        // The send loop's packet, which writePacket writes into a node of the write buffer. Only the holder of the send lock
        // sets or reads them, and writePacket runs only inside the ring's Write, under that lock
        private readonly Func<IntPtr, int> writePacket;
        private RpcProtocolHeaderV1 sendHeader;
        private byte[] sendPayload;
        private int sendOffset;
        private int sendLength;

        /// <summary>
        /// The buffer used to send messages to remote channel endpoint
        /// </summary>
        protected CircularBuffer WriteBuffer { get; private set; }
        /// <summary>
        /// The buffer used to receive message from the remote channel endpoint
        /// </summary>
        protected CircularBuffer ReadBuffer { get; private set; }

        /// <summary>
        /// Channel endpoint statistics
        /// </summary>
        public RpcStatistics Statistics { get; private set; }

        const int defaultTimeoutMs = 30000;
        const int disposeWaitMs = 1000; // how long Dispose waits for the read thread and for a send under way
        const int readWaitMs = 100; // the read thread looks for Dispose this often
        const int roomWaitSliceMs = 50; // a send waiting for room looks for Dispose this often
        object lock_sendQ = new object();
        readonly ManualResetEventSlim readStopped = new ManualResetEventSlim();
        int buffersInUse; // calls in ThrowIfDisposedOrShutdown, which look at the buffers outside the send lock

        /// <summary>
        /// Outgoing requests waiting for responses
        /// </summary>
        protected ConcurrentDictionary<ulong, RpcRequest> Requests { get; } = new ConcurrentDictionary<ulong, RpcRequest>();
        /// <summary>
        /// Incoming requests waiting for more packets
        /// </summary>
        protected ConcurrentDictionary<ulong, RpcRequest> IncomingRequests { get; } = new ConcurrentDictionary<ulong, RpcRequest>();

        Action<ulong, byte[]> RemoteCallHandler = null;
        Func<ulong, byte[], Task> AsyncRemoteCallHandler = null;
        Func<ulong, byte[], byte[]> RemoteCallHandlerWithResult = null;
        Func<ulong, byte[], Task<byte[]>> AsyncRemoteCallHandlerWithResult = null;

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The channel name. This is the name the owner and the opener share. Each channel must have a unique value.</param>
        /// <param name="remoteCallHandler">Action to handle requests with no response.</param>
        /// <param name="bufferCapacity">Owner only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 32 bytes). The opener will use the same size as defined by the owner</param>
        /// <param name="protocolVersion">RpcProtocol.V1: a 32-byte header in each packet</param>
        /// <param name="bufferNodeCount">Owner only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        /// <remarks>The first instance on a channel is its owner and creates the two circular buffers, one after the other; the second
        /// is its opener and opens them. An opener waits up to about 1 s for each buffer its owner has created but not finished, and
        /// fails if its owner has not created a buffer yet.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> is less than 256 bytes or more than 1 MB.</exception>
        /// <exception cref="System.IO.FileNotFoundException">This instance is the opener, and its owner has not created a buffer yet.</exception>
        /// <exception cref="InvalidOperationException">This instance is the opener, and its owner had not finished creating a buffer
        /// after about 1 s. Or, on Windows, an RpcBuffer from SharedMemory 2.3 or earlier holds the channel, which this version
        /// cannot share.</exception>
        /// <exception cref="System.IO.IOException">This instance is the owner, and a buffer of the channel's name still exists, held
        /// open by an earlier opener.</exception>
        public RpcBuffer(string name, Action<ulong, byte[]> remoteCallHandler, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10) :
            this(name, bufferCapacity, protocolVersion, bufferNodeCount)
        {
            RemoteCallHandler = remoteCallHandler;
        }

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The unique channel name. This is the name the owner and the opener share. Each channel must have a unique value.</param>
        /// <param name="asyncRemoteCallHandler">Asynchronous action to handle requests with no response.</param>
        /// <param name="bufferCapacity">Owner only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 32 bytes). The opener will use the same size as defined by the owner</param>
        /// <param name="protocolVersion">RpcProtocol.V1: a 32-byte header in each packet</param>
        /// <param name="bufferNodeCount">Owner only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        /// <remarks>The first instance on a channel is its owner and creates the two circular buffers, one after the other; the second
        /// is its opener and opens them. An opener waits up to about 1 s for each buffer its owner has created but not finished, and
        /// fails if its owner has not created a buffer yet.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> is less than 256 bytes or more than 1 MB.</exception>
        /// <exception cref="System.IO.FileNotFoundException">This instance is the opener, and its owner has not created a buffer yet.</exception>
        /// <exception cref="InvalidOperationException">This instance is the opener, and its owner had not finished creating a buffer
        /// after about 1 s. Or, on Windows, an RpcBuffer from SharedMemory 2.3 or earlier holds the channel, which this version
        /// cannot share.</exception>
        /// <exception cref="System.IO.IOException">This instance is the owner, and a buffer of the channel's name still exists, held
        /// open by an earlier opener.</exception>
        public RpcBuffer(string name, Func<ulong, byte[], Task> asyncRemoteCallHandler, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10) :
            this(name, bufferCapacity, protocolVersion, bufferNodeCount)
        {
            AsyncRemoteCallHandler = asyncRemoteCallHandler;
        }

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The unique channel name. This is the name the owner and the opener share. Each channel must have a unique value.</param>
        /// <param name="remoteCallHandlerWithResult">Function to handle requests with a response.</param>
        /// <param name="bufferCapacity">Owner only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 32 bytes). The opener will use the same size as defined by the owner</param>
        /// <param name="protocolVersion">RpcProtocol.V1: a 32-byte header in each packet</param>
        /// <param name="bufferNodeCount">Owner only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        /// <remarks>The first instance on a channel is its owner and creates the two circular buffers, one after the other; the second
        /// is its opener and opens them. An opener waits up to about 1 s for each buffer its owner has created but not finished, and
        /// fails if its owner has not created a buffer yet.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> is less than 256 bytes or more than 1 MB.</exception>
        /// <exception cref="System.IO.FileNotFoundException">This instance is the opener, and its owner has not created a buffer yet.</exception>
        /// <exception cref="InvalidOperationException">This instance is the opener, and its owner had not finished creating a buffer
        /// after about 1 s. Or, on Windows, an RpcBuffer from SharedMemory 2.3 or earlier holds the channel, which this version
        /// cannot share.</exception>
        /// <exception cref="System.IO.IOException">This instance is the owner, and a buffer of the channel's name still exists, held
        /// open by an earlier opener.</exception>
        public RpcBuffer(string name, Func<ulong, byte[], byte[]> remoteCallHandlerWithResult, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10) :
            this(name, bufferCapacity, protocolVersion, bufferNodeCount)
        {
            RemoteCallHandlerWithResult = remoteCallHandlerWithResult;
        }

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The unique channel name. This is the name the owner and the opener share. Each channel must have a unique value.</param>
        /// <param name="asyncRemoteCallHandlerWithResult">Function to asynchronously handle requests with a response.</param>
        /// <param name="bufferCapacity">Owner only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 32 bytes). The opener will use the same size as defined by the owner</param>
        /// <param name="protocolVersion">RpcProtocol.V1: a 32-byte header in each packet</param>
        /// <param name="bufferNodeCount">Owner only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        /// <remarks>The first instance on a channel is its owner and creates the two circular buffers, one after the other; the second
        /// is its opener and opens them. An opener waits up to about 1 s for each buffer its owner has created but not finished, and
        /// fails if its owner has not created a buffer yet.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> is less than 256 bytes or more than 1 MB.</exception>
        /// <exception cref="System.IO.FileNotFoundException">This instance is the opener, and its owner has not created a buffer yet.</exception>
        /// <exception cref="InvalidOperationException">This instance is the opener, and its owner had not finished creating a buffer
        /// after about 1 s. Or, on Windows, an RpcBuffer from SharedMemory 2.3 or earlier holds the channel, which this version
        /// cannot share.</exception>
        /// <exception cref="System.IO.IOException">This instance is the owner, and a buffer of the channel's name still exists, held
        /// open by an earlier opener.</exception>
        public RpcBuffer(string name, Func<ulong, byte[], Task<byte[]>> asyncRemoteCallHandlerWithResult, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10) :
            this(name, bufferCapacity, protocolVersion, bufferNodeCount)
        {
            AsyncRemoteCallHandlerWithResult = asyncRemoteCallHandlerWithResult;
        }

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The unique channel name. This is the name the owner and the opener share. Each channel must have a unique value.</param>
        /// <param name="bufferCapacity">Owner only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 32 bytes). The opener will use the same size as defined by the owner</param>
        /// <param name="protocolVersion">RpcProtocol.V1: a 32-byte header in each packet</param>
        /// <param name="bufferNodeCount">Owner only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        /// <remarks>The first instance on a channel is its owner and creates the two circular buffers, one after the other; the second
        /// is its opener and opens them. An opener waits up to about 1 s for each buffer its owner has created but not finished, and
        /// fails if its owner has not created a buffer yet.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> is less than 256 bytes or more than 1 MB.</exception>
        /// <exception cref="System.IO.FileNotFoundException">This instance is the opener, and its owner has not created a buffer yet.</exception>
        /// <exception cref="InvalidOperationException">This instance is the opener, and its owner had not finished creating a buffer
        /// after about 1 s. Or, on Windows, an RpcBuffer from SharedMemory 2.3 or earlier holds the channel, which this version
        /// cannot share.</exception>
        /// <exception cref="System.IO.IOException">This instance is the owner, and a buffer of the channel's name still exists, held
        /// open by an earlier opener.</exception>
        public RpcBuffer(string name, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10)
        {
            if (bufferCapacity < 256) // min 256 bytes
            {
                throw new ArgumentOutOfRangeException(nameof(bufferCapacity), "cannot be less than 256 bytes");
            }

            if (bufferCapacity > 1024 * 1024) // max 1MB
            {
                throw new ArgumentOutOfRangeException(nameof(bufferCapacity), "cannot be larger than 1MB");
            }

            Statistics = new RpcStatistics();

            // A subclass's SendMessage may run code after awaiting the base one, so its blocking requests take the
            // thread pool hop as well
            sendMessageOverridden = GetType().GetMethod(nameof(SendMessage), BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(MessageType), typeof(RpcRequest), typeof(byte[]), typeof(ulong), typeof(int), typeof(CancellationToken) }, null)
                .DeclaringType != typeof(RpcBuffer);

#if NET10_0_OR_GREATER
            // On Unix a named mutex that a thread holds keeps its name until that thread releases it or ends, even once every handle has
            // been closed, and Dispose can close the owner's on its read thread. Which instance creates the mutex is all the election
            // needs, so there the owner creates it without taking it.
            bool takeMutex = OperatingSystem.IsWindows();
            bool createdNew;
            if (takeMutex)
                electionMutex = new Mutex(true, name + ElectionMutexSuffix, out createdNew);
            else
                electionMutex = CreateUnixElectionMutex(name + ElectionMutexSuffix, out createdNew);

            if (createdNew && (!takeMutex || electionMutex.WaitOne(500)))
#else
            electionMutex = new Mutex(true, name + ElectionMutexSuffix, out bool createdNew);

            if (createdNew && electionMutex.WaitOne(500))
#endif
            {
                instanceType = InstanceType.Owner;
            }
            else
            {
                instanceType = InstanceType.Opener;
                if (electionMutex != null)
                {
                    electionMutex.Close();
                    electionMutex.Dispose();
                    electionMutex = null;
                }
            }

#if NET10_0_OR_GREATER
            bool onWindows = OperatingSystem.IsWindows();
#else
            bool onWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
#endif
            // 2.3 and earlier run RpcBuffer only on Windows, under other names. An older RpcBuffer that arrives after this owner finds
            // the name it elects with taken, becomes an opener and fails to open its rings. One that is already here holds that name,
            // and this owner closes its election mutex and stops before creating its rings.
            if (instanceType == InstanceType.Owner && onWindows)
            {
                previousElectionMutex = new Mutex(false, name + PreviousElectionMutexSuffix, out bool createdPrevious);
                if (!createdPrevious)
                {
                    previousElectionMutex.Close();
                    previousElectionMutex = null;
                    electionMutex.Close();
                    electionMutex = null;
                    throw new InvalidOperationException("Channel \"" + name + "\" is in use by an RpcBuffer from SharedMemory 2.3 or earlier, which cannot share a channel with this version. Upgrade both ends.");
                }
            }

            switch (protocolVersion)
            {
                case RpcProtocol.V1:
                    this.protocolVersion = protocolVersion;
                    protocolLength = FastStructure.SizeOf<RpcProtocolHeaderV1>();
                    Statistics.ProtocolOverheadPerPacket = protocolLength;
                    writePacket = WritePacketV1;
                    break;
            }

            this.bufferCapacity = bufferCapacity;
            this.bufferNodeCount = bufferNodeCount;
            try
            {
                if (instanceType == InstanceType.Owner)
                {
                    WriteBuffer = new CircularBuffer(name + OwnerWritesSuffix, bufferNodeCount, this.bufferCapacity);
                    ReadBuffer = new CircularBuffer(name + OwnerReadsSuffix, bufferNodeCount, this.bufferCapacity);
                }
                else
                {
                    ReadBuffer = new CircularBuffer(name + OwnerWritesSuffix);
                    WriteBuffer = new CircularBuffer(name + OwnerReadsSuffix);
                    this.bufferCapacity = ReadBuffer.NodeBufferSize;
                    this.bufferNodeCount = ReadBuffer.NodeCount;
                }
            }
            catch
            {
                // Free the names now, rather than when the garbage collector finalises what was made
                WriteBuffer?.Dispose();
                ReadBuffer?.Dispose();
                // 2.3's name before the election mutex, as in Dispose
                previousElectionMutex?.Close();
                electionMutex?.Close();
                throw;
            }

            this.msgBufferLength = Convert.ToInt32(this.bufferCapacity) - protocolLength;


            Task readTask = new Task(() =>
            {
                switch (protocolVersion)
                {
                    case RpcProtocol.V1:
                        ReadThreadV1();
                        break;
                    default:
                        // No reader for a protocol it does not know, so Dispose has none to wait for
                        readStopped.Set();
                        break;
                }
            }, TaskCreationOptions.LongRunning);

            readTask.Start();
        }

        object mutex = new object();
        // A restarted peer does not reuse the ids of the instance before it, so a late reply to that instance matches no request
        ulong messageId = RandomMessageId();

        static ulong RandomMessageId()
        {
            var bytes = new byte[8];
            using (var random = RandomNumberGenerator.Create())
            {
                ulong id;
                do
                {
                    random.GetBytes(bytes);
                    id = BitConverter.ToUInt64(bytes, 0);
                }
                while (id == 0);
                return id;
            }
        }

        /// <summary>
        /// Constructs a new request message, giving it a new unique MsgId
        /// </summary>
        /// <returns></returns>
        protected RpcRequest CreateMessageRequest()
        {
            return CreateMessageRequest(true);
        }

        RpcRequest CreateMessageRequest(bool continuesOnThreadPool)
        {
            RpcRequest request = new RpcRequest(continuesOnThreadPool);
            lock (mutex)
            {
                request.MsgId = messageId++;
            }

            return request;
        }

        /// <summary>
        /// Send a remote request on the channel, blocking until a result is returned
        /// </summary>
        /// <param name="args">Arguments (if any) as a byte array to be sent to the remote endpoint</param>
        /// <param name="timeoutMs">How long the whole call may take, in milliseconds (defaults to 30 s): the waits to send the request,
        /// for room in the buffer, and for the response share it. <see cref="Timeout.Infinite"/> waits without limit. 0 sends the
        /// request and returns without its response, with <see cref="RpcResponse.Success"/> false; that send waits for the send lock
        /// without limit, and up to about 1 s a packet for room.</param>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns>The response. Its <see cref="RpcResponse.Success"/> is false if the request could not be sent, no response came
        /// within <paramref name="timeoutMs"/>, the call was cancelled, the remote handler threw, the response arrived damaged, or
        /// this instance was disposed while the request waited.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if this object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown if the underlying buffers have been closed by the channel owner</exception>
        public RpcResponse RemoteRequest(byte[] args = null, int timeoutMs = defaultTimeoutMs, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposedOrShutdown();

            // The caller blocks for the response and has no code to run where it completes, so the read thread
            // completes it without a hop through the thread pool, unless a subclass's SendMessage has code to run
            var request = CreateMessageRequest(sendMessageOverridden);
            Task<RpcResponse> sendMessage = SendMessage(request, args, timeoutMs, cancellationToken);
            return sendMessage.Result;
        }

        /// <summary>
        /// Send a remote request on the channel (awaitable)
        /// </summary>
        /// <param name="args">Arguments (if any) as a byte array to be sent to the remote endpoint</param>
        /// <param name="timeoutMs">How long the whole call may take, in milliseconds (defaults to 30 s): the waits to send the request,
        /// for room in the buffer, and for the response share it. <see cref="Timeout.Infinite"/> waits without limit. 0 sends the
        /// request and returns without its response, with <see cref="RpcResponse.Success"/> false; that send waits for the send lock
        /// without limit, and up to about 1 s a packet for room.</param>
        /// <param name="cancellationToken">A cancellation token</param>
        /// <returns>A task for the response. Its <see cref="RpcResponse.Success"/> is false if the request could not be sent, no response came
        /// within <paramref name="timeoutMs"/>, the call was cancelled, the remote handler threw, the response arrived damaged, or
        /// this instance was disposed while the request waited.
        /// The request is sent, waits for the send lock and for room included, before the task is returned. Code after an await
        /// of the task does not run on the thread that reads the buffer.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if this object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown if the underlying buffers have been closed by the channel owner</exception>
        public Task<RpcResponse> RemoteRequestAsync(byte[] args = null, int timeoutMs = defaultTimeoutMs, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposedOrShutdown();

            var request = CreateMessageRequest();
            return SendMessage(request, args, timeoutMs, cancellationToken);
        }

        async Task<RpcResponse> SendMessage(RpcRequest request, byte[] payload, int timeout = defaultTimeoutMs, CancellationToken cancellationToken = default)
        {
            return await SendMessage(MessageType.RpcRequest, request, payload, timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends a message to the remote endpoint
        /// </summary>
        /// <param name="msgType"></param>
        /// <param name="request"></param>
        /// <param name="payload"></param>
        /// <param name="responseMsgId"></param>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="ObjectDisposedException">Thrown if this object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown if the underlying buffers have been closed by the channel owner</exception>
        protected virtual Task<RpcResponse> SendMessage(MessageType msgType, RpcRequest request, byte[] payload, ulong responseMsgId = 0, int timeout = defaultTimeoutMs, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposedOrShutdown();

            var msgId = request.MsgId;
            var sending = Stopwatch.GetTimestamp();

            if (msgType == MessageType.RpcRequest)
            {
                Requests[request.MsgId] = request;
            }

            var success = false;
            try
            {
                switch (this.protocolVersion)
                {
                    case RpcProtocol.V1:
                        success = WriteProtocolV1(msgType, msgId, payload, responseMsgId, timeout, sending);
                        break;
                    default:
                        // Invalid protocol
                        return Task.FromResult(new RpcResponse(false, null));
                }
            }
            finally
            {
                if (!success && msgType == MessageType.RpcRequest)
                {
                    // A request that was not sent gets no response
                    Requests.TryRemove(msgId, out RpcRequest unsent);
                }
            }

            if (success)
            {
                Statistics.MessageSent(msgType, payload?.Length ?? 0);
            }

            if (success && msgType == MessageType.RpcRequest)
            {
                if (request != null)
                {
                    return WaitForResponse(request, RemainingTimeout(timeout, sending), timeout != 0, cancellationToken);
                }
                else
                {
                    return Task.FromResult(new RpcResponse(true, null));
                }

            }
            else
            {
                RpcResponse rpcResponse = new RpcResponse(success, null);

                if (request != null)
                {
                    request.IsSuccess = success;
                    request.ResponseReady.TrySetResult(rpcResponse); // Dispose may have failed it already
                }
                return Task.FromResult(rpcResponse);
            }
        }

        // Completes a request whose response did not come; only WaitForResponse uses it
        static readonly RpcResponse NoResponse = new RpcResponse(false, null);

        // Waits for the response, up to the request's remaining time or until the caller cancels. The timer and the
        // cancellation complete the request with NoResponse, so the caller waits on one task
        async Task<RpcResponse> WaitForResponse(RpcRequest request, int timeout, bool waitsForResponse, CancellationToken cancellationToken)
        {
            var ready = request.ResponseReady.Task;
            RpcResponse response;
            try
            {
                if (!ready.IsCompleted)
                {
                    Action stop = () => request.ResponseReady.TrySetResult(NoResponse);
                    if (timeout == 0 || (cancellationToken.IsCancellationRequested && timeout >= Timeout.Infinite))
                    {
                        stop();
                    }
                    else
                    {
                        Timer timer = null;
                        var cancellation = default(CancellationTokenRegistration);
                        try
                        {
                            if (timeout != Timeout.Infinite)
                            {
                                timer = new Timer(state => ((Action)state)(), stop, timeout, Timeout.Infinite);
                            }
                            cancellation = cancellationToken.Register(stop);
                            await ready.ConfigureAwait(false);
                        }
                        finally
                        {
                            timer?.Dispose();
                            cancellation.Dispose();
                        }
                    }
                }

                response = await ready.ConfigureAwait(false);
            }
            catch
            {
                Requests.TryRemove(request.MsgId, out RpcRequest failed);
                throw;
            }

            // NoResponse means that no response came: the request timed out, unless it was cancelled or did not wait
            // for its response (a timeout of 0). Removing it discards a late response. A response that won has
            // removed its request already. This runs before the caller resumes, so the caller sees both
            if (ReferenceEquals(response, NoResponse))
            {
                Requests.TryRemove(request.MsgId, out RpcRequest unanswered);
                if (waitsForResponse && !cancellationToken.IsCancellationRequested)
                {
                    Statistics.Timeout();
                }
            }

            return response;
        }

        bool WriteProtocolV1(MessageType msgType, ulong msgId, byte[] msg, ulong responseMsgId, int timeout, long sending)
        {
            // Dispose sets the property to null, and closes the buffer only under the send lock
            var writeBuffer = WriteBuffer;
            if (Disposed || writeBuffer == null)
            {
                return false;
            }

            // A request waits for the send lock and for room up to the caller's timeout. A reply, or a request
            // that does not wait for its response (a timeout of 0), waits for room up to 1 s a packet
            var waitsForCaller = msgType == MessageType.RpcRequest && (timeout > 0 || timeout == Timeout.Infinite);

            // Send the request packets
            var locked = false;
            // The send fields as this send found them. Code that runs while a send waits for room can send again on the same
            // thread, as a wait on an STA thread runs COM calls, and the outer send gets its packet back when the inner one ends
            var outerHeader = default(RpcProtocolHeaderV1);
            byte[] outerPayload = null;
            int outerOffset = 0, outerLength = 0;
            try
            {
                if (waitsForCaller)
                {
                    locked = Monitor.TryEnter(lock_sendQ) || Monitor.TryEnter(lock_sendQ, RemainingTimeout(timeout, sending));
                }
                else
                {
                    Monitor.Enter(lock_sendQ, ref locked);
                }

                if (!locked)
                {
                    if (!Disposed)
                    {
                        Statistics.Timeout();
                    }
                    return false;
                }

                outerHeader = sendHeader;
                outerPayload = sendPayload;
                outerOffset = sendOffset;
                outerLength = sendLength;

                // Nothing more is sent once Dispose has begun
                if (Disposed)
                {
                    return false;
                }

                // Split message into correct packet size
                int i = 0;
                int left = msg?.Length ?? 0;

                ushort totalPackets = ((msg?.Length ?? 0) == 0) ? (ushort)1 : Convert.ToUInt16(Math.Ceiling((double)msg.Length / (double)msgBufferLength));
                ushort currentPacket = 1;

                // The packet for writePacket: the header, and where its bytes are in msg
                sendHeader = new RpcProtocolHeaderV1
                {
                    MsgType = msgType,
                    MsgId = msgId,
                    TotalPackets = totalPackets,
                    PayloadSize = msg?.Length ?? 0,
                    ResponseId = responseMsgId
                };
                sendPayload = msg;

                while (true)
                {
                    if (writeBuffer.ShuttingDown || Disposed)
                    {
                        return false;
                    }

                    int packetLength = left > msgBufferLength ? msgBufferLength : left;
                    if (packetLength < 0)
                    {
                        // A node smaller than the header, in rings an opener found: fail before writing, as copying the
                        // header into a packet of the node's size did
                        throw new ArgumentException("The structure does not fit in the buffer from startIndex.");
                    }
                    sendHeader.CurrentPacket = currentPacket;
                    sendOffset = i;
                    sendLength = packetLength;

                    Statistics.StartWaitWrite();

                    // Write at once if there is room; otherwise wait for it in slices, so that a Dispose, or the channel's
                    // owner closing it, ends the wait
                    var bytes = writeBuffer.Write(writePacket, 0);
                    var waiting = bytes == 0 ? Stopwatch.GetTimestamp() : 0;
                    while (bytes == 0 && !Disposed && !writeBuffer.ShuttingDown)
                    {
                        var wait = waitsForCaller ? RemainingTimeout(timeout, sending) : RemainingTimeout(1000, waiting);
                        var lastSlice = wait >= 0 && wait <= roomWaitSliceMs;
                        bytes = writeBuffer.Write(writePacket, lastSlice ? wait : roomWaitSliceMs);
                        if (lastSlice)
                        {
                            break;
                        }
                    }

                    if (bytes == 0)
                    {
                        // No room in time: fail the message rather than send the rest of it with a packet missing
                        if (!Disposed && !writeBuffer.ShuttingDown)
                        {
                            Statistics.Timeout();
                        }
                        return false;
                    }

                    Statistics.WritePacket(bytes - protocolLength);

                    left -= packetLength;
                    i += packetLength;
                    if (left <= 0)
                    {
                        break;
                    }
                    currentPacket++;
                }
            }
            finally
            {
                if (locked)
                {
                    // An outer send on this thread gets its packet back. Otherwise the fields go back to holding no caller's
                    // array, so that none is kept once its send ends
                    sendHeader = outerHeader;
                    sendPayload = outerPayload;
                    sendOffset = outerOffset;
                    sendLength = outerLength;
                    Monitor.Exit(lock_sendQ);
                }
            }

            return true;
        }

        // Writes the send loop's packet into a node of the write buffer: protocol 1's header, then the packet's bytes straight
        // from the caller's array. Returns the bytes written. The ring calls it only once it has a node, under the send lock
        int WritePacketV1(IntPtr node)
        {
            FastStructure<RpcProtocolHeaderV1>.StructureToPtr(ref sendHeader, node);
            // Bytes 1 to 7 are the padding after MsgType, which copying the header need not write. Zero them, so that the
            // node does not carry an earlier packet's bytes
            Marshal.WriteInt64(node, 0, 0L);
            Marshal.WriteByte(node, 0, (byte)sendHeader.MsgType);

            if (sendLength > 0)
            {
                FastStructure.WriteBytes(node + protocolLength, sendPayload, sendOffset, sendLength);
            }

            return protocolLength + sendLength;
        }

        static int RemainingTimeout(int timeout, long started)
        {
            return timeout < 0 ? timeout : (int)Math.Max(0, timeout - (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency);
        }

        // Completes a request, from the read thread or Dispose, unless its timeout or cancellation came first. Code
        // awaiting the response runs on the thread pool: ResponseReady runs its continuations asynchronously, and on
        // .NET Framework 4.5, which cannot, the completion itself moves to the pool. A request from a blocking
        // RemoteRequest completes where it is, as its caller has no code to run there
        void CompleteRequest(RpcRequest request, RpcResponse response, bool countIfLate)
        {
            request.IsSuccess = response.Success;
#if !(NET46_OR_GREATER || NETSTANDARD || NETCOREAPP)
            if (request.ContinuesOnThreadPool)
            {
                Task.Run(() => TryCompleteRequest(request, response, countIfLate));
                return;
            }
#endif
            TryCompleteRequest(request, response, countIfLate);
        }

        void TryCompleteRequest(RpcRequest request, RpcResponse response, bool countIfLate)
        {
            if (!request.ResponseReady.TrySetResult(response) && countIfLate)
            {
                // Its request timed out or was cancelled meanwhile
                Statistics.DiscardResponse(request.MsgId);
            }
        }

        private RpcRequest partialRequest; // the incoming request still missing packets, used by the read thread only

        void ReadThreadV1()
        {
            try
            {
                // Work with Local Variable to prevent NPE after dispose  
                CircularBuffer l_TempReadBuffer = ReadBuffer;
                var header = new RpcProtocolHeaderV1(); // the packet being read, whose message is dropped if handling it fails
                var handling = false; // whether a fault came from handling a packet, rather than from the ring itself

                // Made once: the delegate that handles a packet, and the one the ring calls, which clears handling after it
                Func<IntPtr, int> readPacket = (ptr) =>
                {
                    int readLength = 0;
                    header = FastStructure<RpcProtocolHeaderV1>.PtrToStructure(ptr);
                    handling = true;
                    ptr = ptr + protocolLength;
                    readLength += protocolLength;

                    // The peer sends one message at a time, so the first packet of a message means
                    // that the request still missing packets will not get them
                    if (header.CurrentPacket == 1 && partialRequest != null)
                    {
                        IncomingRequests.TryRemove(partialRequest.MsgId, out RpcRequest abandoned);
                        partialRequest = null;
                    }

                    RpcRequest request = null;
                    if (header.MsgType == MessageType.RpcResponse || header.MsgType == MessageType.ErrorInRpc)
                    {
                        if (!Requests.TryGetValue(header.ResponseId, out request))
                        {
                            // The response received does not have a  matching message that was sent
                            Statistics.DiscardResponse(header.ResponseId);
                            return protocolLength;
                        }
                    }
                    else if (header.CurrentPacket == 1)
                    {
                        request = IncomingRequests.GetOrAdd(header.MsgId, new RpcRequest
                        {
                            MsgId = header.MsgId
                        });
                    }
                    else if (!IncomingRequests.TryGetValue(header.MsgId, out request))
                    {
                        // The start of this message was lost or discarded
                        return protocolLength;
                    }

                    int packetSize = header.PayloadSize < msgBufferLength ? header.PayloadSize :
                        (header.CurrentPacket < header.TotalPackets ? msgBufferLength : header.PayloadSize - (header.TotalPackets - 1) * msgBufferLength);

                    if (header.PayloadSize > 0)
                    {
                        if (request.Data == null)
                        {
                            request.Data = new byte[header.PayloadSize];
                        }

                        int index = msgBufferLength * (header.CurrentPacket - 1);
                        FastStructure.ReadBytes(request.Data, ptr, index, packetSize);
                        readLength += packetSize;
                    }

                    request.PacketsReceived++;
                    if (header.MsgType != MessageType.RpcResponse && header.MsgType != MessageType.ErrorInRpc)
                    {
                        partialRequest = header.CurrentPacket < header.TotalPackets ? request : null;
                    }

                    if (header.CurrentPacket == header.TotalPackets)
                    {
                        if (header.MsgType == MessageType.RpcResponse || header.MsgType == MessageType.ErrorInRpc)
                        {
                            Requests.TryRemove(request.MsgId, out RpcRequest removed);
                        }
                        else
                        {
                            IncomingRequests.TryRemove(request.MsgId, out RpcRequest removed);
                        }

                        if (request.PacketsReceived != header.TotalPackets)
                        {
                            // A packet of this message was lost: drop the message, and fail the request it answers
                            if (header.MsgType == MessageType.RpcResponse || header.MsgType == MessageType.ErrorInRpc)
                            {
                                CompleteRequest(request, new RpcResponse(false, null), false);
                            }

                            Statistics.ReadPacket(packetSize);
                            return protocolLength + packetSize;
                        }

                        // Full message is ready

                        Statistics.MessageReceived(header.MsgType, request.Data?.Length ?? 0);

                        if (header.MsgType == MessageType.RpcResponse)
                        {
                            CompleteRequest(request, new RpcResponse(true, request.Data), true);
                        }
                        else if (header.MsgType == MessageType.ErrorInRpc)
                        {
                            CompleteRequest(request, new RpcResponse(false, request.Data), true);
                        }
                        else if (header.MsgType == MessageType.RpcRequest)
                        {
                            StartHandling(request);
                        }
                    }

                    Statistics.ReadPacket(packetSize);

                    return protocolLength + packetSize;
                };
                Func<IntPtr, int> readNode = (ptr) =>
                {
                    try
                    {
                        return readPacket(ptr);
                    }
                    finally
                    {
                        // A fault after this, in giving the node back, is the ring's
                        handling = false;
                    }
                };

                while (!Disposed && !l_TempReadBuffer.ShuttingDown)
                {
                    try
                    {
                        handling = false;
                        Statistics.StartWaitRead();
                        l_TempReadBuffer.Read(readNode, readWaitMs);
                    }
                    catch (Exception) when (handling)
                    {
                        // Handling the packet failed, as on a malformed header: drop its message and read on. A fault
                        // in the ring itself still ends the read thread, which would otherwise meet it again at once
                        DropMessage(header);
                    }
                }
            }
            finally
            {
                // Dispose closes the buffers once the read thread has stopped
                readStopped.Set();
            }
        }

        // Runs a complete request's handler on the thread pool, since it can take some time. A method of its own, so that
        // reading a packet makes no closure for it
        void StartHandling(RpcRequest request)
        {
            Task.Run(async () =>
            {
                try
                {
                    await ProcessCallHandler(request).ConfigureAwait(false);
                }
                catch(Exception ex)
                {
                    // Ignore Object Disposed and Invalid Operation Exceptions
                    // because the other side of the rpc buffers may 
                    // not know if this Buffer is shutting down or disposed
                    if (!(ex is ObjectDisposedException || ex is InvalidOperationException))
                    {
                        throw;
                    }
                }
            });
        }

        // Drops the message of a packet that could not be handled, and fails the request that a dropped response answers
        void DropMessage(RpcProtocolHeaderV1 header)
        {
            if (partialRequest != null)
            {
                IncomingRequests.TryRemove(partialRequest.MsgId, out RpcRequest abandoned);
                partialRequest = null;
            }

            RpcRequest dropped;
            if (header.MsgType == MessageType.RpcResponse || header.MsgType == MessageType.ErrorInRpc)
            {
                if (Requests.TryRemove(header.ResponseId, out dropped))
                {
                    CompleteRequest(dropped, new RpcResponse(false, null), false);
                }
            }
            else
            {
                IncomingRequests.TryRemove(header.MsgId, out dropped);
            }
        }

        async Task ProcessCallHandler(RpcRequest request, CancellationToken cancellationToken = default)
        {
            try
            {

                if (RemoteCallHandler != null)
                {
                    RemoteCallHandler(request.MsgId, request.Data);
                    await SendMessage(MessageType.RpcResponse, CreateMessageRequest(), null, request.MsgId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (AsyncRemoteCallHandler != null)
                {
                    await AsyncRemoteCallHandler(request.MsgId, request.Data).ConfigureAwait(false);
                    await SendMessage(MessageType.RpcResponse, CreateMessageRequest(), null, request.MsgId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (RemoteCallHandlerWithResult != null)
                {
                    var result = RemoteCallHandlerWithResult(request.MsgId, request.Data);
                    await SendMessage(MessageType.RpcResponse, CreateMessageRequest(), result, request.MsgId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (AsyncRemoteCallHandlerWithResult != null)
                {
                    var result = await AsyncRemoteCallHandlerWithResult(request.MsgId, request.Data)
                        .ConfigureAwait(false);
                    await SendMessage(MessageType.RpcResponse, CreateMessageRequest(), result, request.MsgId, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                await SendMessage(MessageType.ErrorInRpc, CreateMessageRequest(), null, request.MsgId)
                    .ConfigureAwait(false);
            }
        }

        #region IDisposable

        /// <summary>
        /// Checks that the object has not been disposed, and that the underlying buffers are not shutting down.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if this object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown if the underlying buffers have been closed by the channel owner</exception>
        protected void ThrowIfDisposedOrShutdown()
        {
            // Dispose closes the buffers only once no call here is looking at them. The exception is thrown after
            // that, so that Dispose does not wait for the caller's handlers
            Exception failure = null;
            Interlocked.Increment(ref buffersInUse);
            try
            {
                if (Disposed)
                {
                    failure = new ObjectDisposedException("RpcBuffer");
                }
                else if (ReadBuffer.ShuttingDown || WriteBuffer.ShuttingDown)
                {
                    failure = new InvalidOperationException("Channel owner has closed buffers");
                }
            }
            finally
            {
                Interlocked.Decrement(ref buffersInUse);
            }

            if (failure != null)
            {
                throw failure;
            }
        }

        /// <summary>
        /// Closes the channel. Requests still waiting for a response complete at once, with <see cref="RpcResponse.Success"/> false.
        /// Waits up to about 1 s in all for the thread that reads the buffer to stop and for a send under way to end, then closes the
        /// buffers; past that, they close in the background once they are free (see <see cref="DisposeFinished"/>). It does not wait
        /// for request handlers still running, so their replies may not be sent.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// IDisposable pattern - dispose of managed/unmanaged resources
        /// </summary>
        /// <param name="disposeManagedResources">true to dispose of managed resources as well as unmanaged.</param>
        protected virtual void Dispose(bool disposeManagedResources)
        {
            if (!disposeManagedResources)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            {
                // Another call is disposing: wait for it, as for this one
                SpinWait.SpinUntil(() => DisposeFinished, disposeWaitMs);
                return;
            }

            // Requests still waiting for a response get none now
            foreach (var pending in Requests)
            {
                if (Requests.TryRemove(pending.Key, out RpcRequest failed))
                {
                    CompleteRequest(failed, new RpcResponse(false, null), false);
                }
            }

            // The read thread stops at its next wait for a packet, and a send at its next wait for room. Neither runs
            // user code, so this seldom takes long; if it does, the buffers close once both have let go
            if (!TryDisposeManagedResources(disposeWaitMs))
            {
                Task.Run(() => TryDisposeManagedResources(Timeout.Infinite));
            }
        }

        // Closes the buffers and the election mutexes, once the read thread has stopped, no send is under way and no caller
        // is checking them in ThrowIfDisposedOrShutdown. Disposed is set first, so no new caller looks at them
        private bool TryDisposeManagedResources(int timeout)
        {
            var started = Stopwatch.GetTimestamp();
            if (!readStopped.Wait(timeout))
            {
                return false;
            }

            var locked = false;
            try
            {
                Monitor.TryEnter(lock_sendQ, RemainingTimeout(timeout, started), ref locked);
                if (!locked || !SpinWait.SpinUntil(() => Volatile.Read(ref buffersInUse) == 0, RemainingTimeout(timeout, started)))
                {
                    return false;
                }

                // Disconnect handle to prevent processing
                RemoteCallHandler = null;
                AsyncRemoteCallHandler = null;
                RemoteCallHandlerWithResult = null;
                AsyncRemoteCallHandlerWithResult = null;

                if (WriteBuffer != null)
                {
                    WriteBuffer.Dispose();
                    WriteBuffer = null;
                }

                if (ReadBuffer != null)
                {
                    ReadBuffer.Dispose();
                    ReadBuffer = null;
                }

                // 2.3's name closes first: an instance elected once the election mutex is gone must find it free, or it takes
                // this channel for one that 2.3 holds
                if (previousElectionMutex != null)
                {
                    previousElectionMutex.Close();
                    previousElectionMutex = null;
                }

                if (electionMutex != null)
                {
                    electionMutex.Close();
                    electionMutex.Dispose();
                    electionMutex = null;
                }

                // Mark as DisposeFinished
                Interlocked.Exchange(ref _disposed, 2);
                return true;
            }
            finally
            {
                if (locked)
                {
                    Monitor.Exit(lock_sendQ);
                }
            }
        }

        #endregion

#if NET10_0_OR_GREATER
        /// <summary>
        /// Creates or opens the election mutex on Unix, unowned, with the scope of the channel's buffers: one user's processes in every POSIX
        /// session for a name without a prefix or with <c>Local\</c>, every process on the machine for a <c>Global\</c> name.
        /// </summary>
        /// <remarks>
        /// <para>Without options .NET gives a named mutex on Unix one POSIX session's scope, and every terminal is its own session, so two
        /// programs started from two terminals would both become owners. <c>Local\</c> is dropped because .NET refuses it beside
        /// <c>CurrentSessionOnly = false</c>; the buffers' files treat <c>Local\X</c> and <c>X</c> as one name too.</para>
        /// <para>A <c>Global\</c> name is not given <c>CurrentUserOnly</c>: .NET would then make <c>Global\X</c> the same mutex as <c>X</c>,
        /// whose buffers are other files, and an RpcBuffer on one would open the other's channel instead of owning its own.</para>
        /// <para>On macOS the name ends with a hash of itself (<see cref="UnixFiles.ElectionMutexName"/>): .NET's mutex files there are on a volume that
        /// folds case, where <c>TEST</c> and <c>test</c> would share one election, while their buffers are separate.</para>
        /// </remarks>
        static Mutex CreateUnixElectionMutex(string mutexName, out bool createdNew)
        {
            const string LocalScope = "Local\\";
            bool global = mutexName.StartsWith("Global\\", StringComparison.Ordinal);
            // Only Local\X with no other backslash: Local\Global\X would become X's mutex, while its buffers are other files. Kept whole, it is
            // refused by .NET, as a backslash after the prefix is on Windows.
            if (mutexName.StartsWith(LocalScope, StringComparison.Ordinal) && mutexName.IndexOf('\\', LocalScope.Length) < 0)
                mutexName = mutexName.Substring(LocalScope.Length);
            if (OperatingSystem.IsMacOS())
                mutexName = UnixFiles.ElectionMutexName(mutexName);
            var options = new NamedWaitHandleOptions { CurrentUserOnly = !global, CurrentSessionOnly = false };
            return new Mutex(false, mutexName, options, out createdNew);
        }
#endif
    }
}
