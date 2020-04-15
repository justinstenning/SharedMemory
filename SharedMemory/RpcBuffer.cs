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
using System.Text;

namespace SharedMemory
{
    // Only supported in .NET 4.5+ and .NET Standard 2.0

    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    internal enum InstanceType
    {
        Master,
        Slave
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
    /// The RPC message type
    /// </summary>
    public enum MessageType: byte
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
        internal RpcRequest() { }
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
        public ManualResetEvent ResponseReady { get; } = new ManualResetEvent(false);
        /// <summary>
        /// Was the request successful
        /// </summary>
        public bool IsSuccess { get; internal set; }
        /// <summary>
        /// When the request was created
        /// </summary>
        public DateTime Created { get; } = DateTime.Now;
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
        public bool Success { get; set; }
        
        /// <summary>
        /// The returned result (if applicable)
        /// </summary>
        public byte[] Data { get; set; }
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
        public ulong BytesRead { get; private set; }
        /// <summary>
        /// Number of packets read from channel
        /// </summary>
        public ulong PacketsRead { get; private set; }
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
        public ulong RequestsReceived { get; private set; }

        /// <summary>
        /// The number of response messages received
        /// </summary>
        public ulong ResponsesReceived { get; private set; }

        /// <summary>
        /// The number of error message received
        /// </summary>
        public ulong ErrorsReceived { get; private set; }

        /// <summary>
        /// The number of bytes written to channel (excluding protocol overhead)
        /// </summary>
        public ulong BytesWritten { get; private set; }
        /// <summary>
        /// The number of packets written to channel
        /// </summary>
        public ulong PacketsWritten { get; private set; }
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
        public ulong DiscardedResponses { get; private set; }
        /// <summary>
        /// The response message Id that was last discarded
        /// </summary>
        public ulong LastDiscardedResponseId { get; private set; }

        /// <summary>
        /// The total number of messages sent
        /// </summary>
        public ulong MessagesSent { get { return RequestsSent + ResponsesSent + ErrorsSent; } }

        /// <summary>
        /// The number of request messages sent
        /// </summary>
        public ulong RequestsSent { get; private set; }

        /// <summary>
        /// The number of response messages sent
        /// </summary>
        public ulong ResponsesSent { get; private set; }

        /// <summary>
        /// The number of error messages sent
        /// </summary>
        public ulong ErrorsSent { get; private set; }

        /// <summary>
        /// Number of timeouts
        /// </summary>
        public ulong Timeouts { get; private set; }
        /// <summary>
        /// DateTime of last timeout
        /// </summary>
        public DateTime LastTimeout { get; private set; }
        DateTime StartWaitWriteTimestamp { get; set; }
        DateTime EndWaitWriteTimestamp { get; set; }
        /// <summary>
        /// Maximum Ticks waited for available write slot
        /// </summary>
        public long MaxWaitWriteTicks { get; private set; } = -1;
        DateTime StartWaitReadTimestamp { get; set; }
        DateTime EndWaitReadTimestamp { get; set; }
        /// <summary>
        /// Maximum Ticks waiting for read slot (cannot exceed 1sec)
        /// </summary>
        public long MaxWaitReadTicks { get; private set; } = -1;

        internal void StartWaitRead()
        {
            StartWaitReadTimestamp = DateTime.Now;
        }

        internal void ReadPacket(int bytes)
        {
            EndWaitReadTimestamp = DateTime.Now;

            var ticks = EndWaitReadTimestamp.Ticks - StartWaitReadTimestamp.Ticks;
            if (ticks > MaxWaitReadTicks)
            {
                MaxWaitReadTicks = ticks;
            }

            PacketsRead++;
            BytesRead += (ulong)bytes;
            ReadingLastPacketSize = bytes;
            if (bytes > ReadingMaxPacketSize)
            {
                ReadingMaxPacketSize = bytes;
            }
        }

        internal void StartWaitWrite()
        {
            StartWaitWriteTimestamp = DateTime.Now;
        }

        internal void WritePacket(int bytes)
        {
            EndWaitWriteTimestamp = DateTime.Now;

            var ticks = EndWaitWriteTimestamp.Ticks - StartWaitWriteTimestamp.Ticks;
            if (ticks > MaxWaitWriteTicks)
            {
                MaxWaitWriteTicks = ticks;
            }

            PacketsWritten++;
            BytesWritten += (ulong)bytes;
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
                    RequestsReceived++;
                    break;
                case MessageType.RpcResponse:
                    ResponsesReceived++;
                    break;
                case MessageType.ErrorInRpc:
                    ErrorsReceived++;
                    break;
            }
        }

        internal void MessageSent(MessageType msgType, int size)
        {
            WritingLastMessageSize = size;
            switch (msgType)
            {
                case MessageType.RpcRequest:
                    RequestsSent++;
                    break;
                case MessageType.RpcResponse:
                    ResponsesSent++;
                    break;
                case MessageType.ErrorInRpc:
                    ErrorsSent++;
                    break;
            }
        }

        internal void Timeout()
        {
            Timeouts++;
            LastTimeout = DateTime.Now;
        }

        internal void DiscardResponse(ulong msgId)
        {
            DiscardedResponses++;
            LastDiscardedResponseId = msgId;
        }

        /// <summary>
        /// Reset all statistics
        /// </summary>
        public void Reset()
        {
            Timeouts = 0;
            LastTimeout = DateTime.MinValue;
            PacketsRead = 0;
            BytesRead = 0;
            MaxWaitReadTicks = -1;
            ReadingMaxPacketSize = 0;
            ReadingLastMessageSize = -1;
            ReadingLastPacketSize = -1;
            RequestsReceived = 0;
            ResponsesReceived = 0;
            ErrorsReceived = 0;
            PacketsWritten = 0;
            BytesWritten = 0;
            MaxWaitWriteTicks = -1;
            WritingMaxPacketSize = 0;
            WritingLastMessageSize = -1;
            WritingLastPacketSize = -1;
            RequestsSent = 0;
            ResponsesSent = 0;
            ErrorsSent = 0;
            DiscardedResponses = 0;
            LastDiscardedResponseId = 0;
        }
    }

    /// <summary>
    /// A simple RPC implementation designed for a single master/slave pair
    /// </summary>
    public class RpcBuffer : IDisposable
    {
        private Mutex masterMutex;
        private long _disposed = 0;
        
        /// <summary>
        /// Whether the RpcBuffer has been disposed
        /// </summary>
        protected bool Disposed
        {
            get
            {
                return Interlocked.Read(ref _disposed) == 1;
            }
            private set
            {
                Interlocked.Exchange(ref _disposed, value ? 1 : 0);
            }
        }

        private readonly InstanceType instanceType;
        private readonly RpcProtocol protocolVersion;
        private readonly int protocolLength;
        private readonly int bufferCapacity;
        private readonly int bufferNodeCount;
        private readonly int msgBufferLength; // The amount of room left in the node after protocol header

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
        object lock_sendQ = new object();

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
        /// <param name="name">The channel name. This is the name to be shared between the master/slave pair. Each pair must have a unique value.</param>
        /// <param name="remoteCallHandler">Action to handle requests with no response.</param>
        /// <param name="bufferCapacity">Master only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 64-bytes). The slave will use the same size as defined by the master</param>
        /// <param name="protocolVersion">ProtocolVersion.V1 = 64-byte header for each packet</param>
        /// <param name="bufferNodeCount">Master only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        public RpcBuffer(string name, Action<ulong, byte[]> remoteCallHandler, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10) :
            this (name, bufferCapacity, protocolVersion, bufferNodeCount)
        {
            RemoteCallHandler = remoteCallHandler;
        }

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The unique channel name. This is the name to be shared between the master/slave pair. Each pair must have a unique value.</param>
        /// <param name="asyncRemoteCallHandler">Asynchronous action to handle requests with no response.</param>
        /// <param name="bufferCapacity">Master only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 64-bytes). The slave will use the same size as defined by the master</param>
        /// <param name="protocolVersion">ProtocolVersion.V1 = 64-byte header for each packet</param>
        /// <param name="bufferNodeCount">Master only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        public RpcBuffer(string name, Func<ulong, byte[], Task> asyncRemoteCallHandler, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10) :
            this(name, bufferCapacity, protocolVersion, bufferNodeCount)
        {
            AsyncRemoteCallHandler = asyncRemoteCallHandler;
        }

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The unique channel name. This is the name to be shared between the master/slave pair. Each pair must have a unique value.</param>
        /// <param name="remoteCallHandlerWithResult">Function to handle requests with a response.</param>
        /// <param name="bufferCapacity">Master only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 64-bytes). The slave will use the same size as defined by the master</param>
        /// <param name="protocolVersion">ProtocolVersion.V1 = 64-byte header for each packet</param>
        /// <param name="bufferNodeCount">Master only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        public RpcBuffer(string name, Func<ulong, byte[], byte[]> remoteCallHandlerWithResult, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10) :
            this(name, bufferCapacity, protocolVersion, bufferNodeCount)
        {
            RemoteCallHandlerWithResult = remoteCallHandlerWithResult;
        }

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The unique channel name. This is the name to be shared between the master/slave pair. Each pair must have a unique value.</param>
        /// <param name="asyncRemoteCallHandlerWithResult">Function to asynchronously handle requests with a response.</param>
        /// <param name="bufferCapacity">Master only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 64-bytes). The slave will use the same size as defined by the master</param>
        /// <param name="protocolVersion">ProtocolVersion.V1 = 64-byte header for each packet</param>
        /// <param name="bufferNodeCount">Master only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
        public RpcBuffer(string name, Func<ulong, byte[], Task<byte[]>> asyncRemoteCallHandlerWithResult, int bufferCapacity = 50000, RpcProtocol protocolVersion = RpcProtocol.V1, int bufferNodeCount = 10) :
            this(name, bufferCapacity, protocolVersion, bufferNodeCount)
        {
            AsyncRemoteCallHandlerWithResult = asyncRemoteCallHandlerWithResult;
        }

        /// <summary>
        /// Construct a new RpcBuffer
        /// </summary>
        /// <param name="name">The unique channel name. This is the name to be shared between the master/slave pair. Each pair must have a unique value.</param>
        /// <param name="bufferCapacity">Master only: Maximum buffer capacity. Messages will be split into packets that fit this capacity (including a packet header of 64-bytes). The slave will use the same size as defined by the master</param>
        /// <param name="protocolVersion">ProtocolVersion.V1 = 64-byte header for each packet</param>
        /// <param name="bufferNodeCount">Master only: The number of nodes in the underlying circular buffers, each with a size of <paramref name="bufferCapacity"/></param>
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

            masterMutex = new Mutex(true, name + "SharedMemory_MasterMutex", out bool createdNew);

            if (createdNew && masterMutex.WaitOne(500))
            {
                instanceType = InstanceType.Master;
            }
            else
            {
                instanceType = InstanceType.Slave;
                if (masterMutex != null)
                {
                    masterMutex.Close();
                    masterMutex.Dispose();
                    masterMutex = null;
                }
            }

            switch (protocolVersion)
            {
                case RpcProtocol.V1:
                    this.protocolVersion = protocolVersion;
                    protocolLength = FastStructure.SizeOf<RpcProtocolHeaderV1>();
                    Statistics.ProtocolOverheadPerPacket = protocolLength;
                    break;
            }

            this.bufferCapacity = bufferCapacity;
            this.bufferNodeCount = bufferNodeCount;
            if (instanceType == InstanceType.Master)
            {
                WriteBuffer = new CircularBuffer(name + "_Slave_SharedMemory_MMF", bufferNodeCount, this.bufferCapacity);
                ReadBuffer = new CircularBuffer(name + "_Master_SharedMemory_MMF", bufferNodeCount, this.bufferCapacity);
            }
            else
            {
                ReadBuffer = new CircularBuffer(name + "_Slave_SharedMemory_MMF");
                WriteBuffer = new CircularBuffer(name + "_Master_SharedMemory_MMF");
                this.bufferCapacity = ReadBuffer.NodeBufferSize;
                this.bufferNodeCount = ReadBuffer.NodeCount;
            }

            this.msgBufferLength = Convert.ToInt32(this.bufferCapacity) - protocolLength;

            Task.Run(() =>
            {
                switch (protocolVersion)
                {
                    case RpcProtocol.V1:
                        ReadThreadV1();
                        break;
                }
            });
        }

        object mutex = new object();
        ulong messageId = 1;

        /// <summary>
        /// Constructs a new request message, giving it a new unique MsgId
        /// </summary>
        /// <returns></returns>
        protected RpcRequest CreateMessageRequest()
        {
            RpcRequest request = new RpcRequest();
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
        /// <param name="timeoutMs">Timeout in milliseconds (defaults to 30sec)</param>
        /// <returns>The returned response</returns>
        /// <exception cref="ObjectDisposedException">Thrown if this object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown if the underlying buffers have been closed by the channel owner</exception>
        public RpcResponse RemoteRequest(byte[] args = null, int timeoutMs = defaultTimeoutMs)
        {
            ThrowIfDisposedOrShutdown();

            var request = CreateMessageRequest();
            var t = new Task(async () =>
            {
                await SendMessage(request, args, timeoutMs).ConfigureAwait(false);
            });
            t.Start();
            
            if (!request.ResponseReady.WaitOne(timeoutMs))
            {
                // Timed out
                if (request.IsSuccess)
                {
                    return new RpcResponse(request.IsSuccess, request.Data);
                }
                else
                {
                    return new RpcResponse(false, null);
                }
            }
            else
            {
                return new RpcResponse(request.IsSuccess, request.Data);
            }
        }

        /// <summary>
        /// Send a remote request on the channel (awaitable)
        /// </summary>
        /// <param name="args">Arguments (if any) as a byte array to be sent to the remote endpoint</param>
        /// <param name="timeoutMs">Timeout in milliseconds (defaults to 30sec)</param>
        /// <returns></returns>
        /// <exception cref="ObjectDisposedException">Thrown if this object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown if the underlying buffers have been closed by the channel owner</exception>
        public Task<RpcResponse> RemoteRequestAsync(byte[] args = null, int timeoutMs = defaultTimeoutMs)
        {
            ThrowIfDisposedOrShutdown();

            var request = CreateMessageRequest();
            return SendMessage(request, args, timeoutMs);
        }

        async Task<RpcResponse> SendMessage(RpcRequest request, byte[] payload, int timeout = defaultTimeoutMs)
        {
            return await SendMessage(MessageType.RpcRequest, request, payload, timeout: timeout).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends a message to the remote endpoint
        /// </summary>
        /// <param name="msgType"></param>
        /// <param name="request"></param>
        /// <param name="payload"></param>
        /// <param name="responseMsgId"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        /// <exception cref="ObjectDisposedException">Thrown if this object has been disposed</exception>
        /// <exception cref="InvalidOperationException">Thrown if the underlying buffers have been closed by the channel owner</exception>
        protected virtual async Task<RpcResponse> SendMessage(MessageType msgType, RpcRequest request, byte[] payload, ulong responseMsgId = 0, int timeout = defaultTimeoutMs)
        {
            ThrowIfDisposedOrShutdown();

            var msgId = request.MsgId;
            
            if (msgType == MessageType.RpcRequest)
            {
                Requests[request.MsgId] = request;
            }

            var success = false;
            switch (this.protocolVersion)
            {
                case RpcProtocol.V1:
                    success = WriteProtocolV1(msgType, msgId, payload, responseMsgId, timeout);
                    break;
                default:
                    // Invalid protocol
                    return new RpcResponse(false, null);
            }

            if (success)
            {
                Statistics.MessageSent(msgType, payload?.Length ?? 0);
            }

            if (success && msgType == MessageType.RpcRequest)
            {
                RpcResponse result = new RpcResponse(true, null);

                if (request != null)
                {
                    await Task.Run(() =>
                    {
                        if (!request.ResponseReady.WaitOne(timeout))
                        {
                            result = new RpcResponse(false, null);
                        }
                        else
                        {
                            result = new RpcResponse(request.IsSuccess, request.Data);
                        }
                    }).ConfigureAwait(false);
                }

                return result;
            }
            else
            {
                if (request != null)
                {
                    request.IsSuccess = success;
                    request.ResponseReady.Set();
                }
                return new RpcResponse(success, null);
            }
        }

        bool WriteProtocolV1(MessageType msgType, ulong msgId, byte[] msg, ulong responseMsgId, int timeout)
        {
            if (Disposed)
            {
                return false;
            }

            if (WriteBuffer.ShuttingDown)
            {
                return false;
            }

            // Send the request packets
            lock (lock_sendQ)
            {
                // Split message into correct packet size
                int i = 0;
                int left = msg?.Length ?? 0;

                byte[] pMsg = null;

                ushort totalPackets = ((msg?.Length ?? 0) == 0) ? (ushort)1 : Convert.ToUInt16(Math.Ceiling((double)msg.Length / (double)msgBufferLength));
                ushort currentPacket = 1;

                while (true)
                {
                    if (WriteBuffer.ShuttingDown)
                    {
                        return false;
                    }

                    pMsg = new byte[left > msgBufferLength ? msgBufferLength + protocolLength : left + protocolLength];
                    
                    // Writing protocol header
                    var header = new RpcProtocolHeaderV1
                    {
                        MsgType = msgType,
                        MsgId = msgId,
                        CurrentPacket = currentPacket,
                        TotalPackets = totalPackets,
                        PayloadSize = msg?.Length ?? 0,
                        ResponseId = responseMsgId
                    };
                    FastStructure.CopyTo(ref header, pMsg, 0);

                    if (left > msgBufferLength)
                    {
                        // Writing payload
                        if (msg != null && msg.Length > 0)
                            Buffer.BlockCopy(msg, i, pMsg, protocolLength, msgBufferLength);

                        left -= msgBufferLength;
                        i += msgBufferLength;
                    }
                    else
                    {
                        // Writing last packet of payload
                        if (msg != null && msg.Length > 0)
                        {
                            Buffer.BlockCopy(msg, i, pMsg, protocolLength, left);
                        }

                        left = 0;
                    }

                    Statistics.StartWaitWrite();
                    var bytes = WriteBuffer.Write((ptr) =>
                    {
                        FastStructure.WriteBytes(ptr, pMsg, 0, pMsg.Length);
                        return pMsg.Length;
                    }, 1000);

                    Statistics.WritePacket(bytes - protocolLength);

                    if (left <= 0)
                    {
                        break;
                    }
                    currentPacket++;
                }
            }

            return true;
        }

        void ReadThreadV1()
        {
            while(true && !ReadBuffer.ShuttingDown)
            {
                if (Interlocked.Read(ref _disposed) == 1)
                    return;

                Statistics.StartWaitRead();

                ReadBuffer.Read((ptr) =>
                {
                    int readLength = 0;
                    var header = FastStructure<RpcProtocolHeaderV1>.PtrToStructure(ptr);
                    ptr = ptr + protocolLength;
                    readLength += protocolLength;

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
                    else
                    {
                        request = IncomingRequests.GetOrAdd(header.MsgId, new RpcRequest
                        {
                            MsgId = header.MsgId
                        });
                    }

                    int packetSize = header.PayloadSize < msgBufferLength ? header.PayloadSize :
                        (header.CurrentPacket < header.TotalPackets ? msgBufferLength : header.PayloadSize % msgBufferLength);

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

                        // Full message is ready
                        var watching = Stopwatch.StartNew();
                        Task.Run(async () =>
                        {
                            Statistics.MessageReceived(header.MsgType, request.Data?.Length ?? 0);

                            if (header.MsgType == MessageType.RpcResponse)
                            {
                                request.IsSuccess = true;
                                request.ResponseReady.Set();
                            }
                            else if (header.MsgType == MessageType.ErrorInRpc)
                            {
                                request.IsSuccess = false;
                                request.ResponseReady.Set();
                            }
                            else if (header.MsgType == MessageType.RpcRequest)
                            {
                                await ProcessCallHandler(request).ConfigureAwait(false);
                            }
                        });
                    }

                    Statistics.ReadPacket(packetSize);

                    return protocolLength + packetSize;
                }, 500);
            }
        }

        async Task ProcessCallHandler(RpcRequest request)
        {
            try
            {
                if (RemoteCallHandler != null)
                {
                    RemoteCallHandler(request.MsgId, request.Data);
                    await SendMessage(MessageType.RpcResponse, CreateMessageRequest(), null, request.MsgId).ConfigureAwait(false);
                }
                else if (AsyncRemoteCallHandler != null)
                {
                    await AsyncRemoteCallHandler(request.MsgId, request.Data).ConfigureAwait(false);
                    await SendMessage(MessageType.RpcResponse, CreateMessageRequest(), null, request.MsgId).ConfigureAwait(false);
                }
                else if (RemoteCallHandlerWithResult != null)
                {
                    var result = RemoteCallHandlerWithResult(request.MsgId, request.Data);
                    await SendMessage(MessageType.RpcResponse, CreateMessageRequest(), result, request.MsgId).ConfigureAwait(false);
                }
                else if (AsyncRemoteCallHandlerWithResult != null)
                {
                    var result = await AsyncRemoteCallHandlerWithResult(request.MsgId, request.Data).ConfigureAwait(false);
                    await SendMessage(MessageType.RpcResponse, CreateMessageRequest(), result, request.MsgId).ConfigureAwait(false);
                }
            }
            catch
            {
                await SendMessage(MessageType.ErrorInRpc, CreateMessageRequest(), null, request.MsgId).ConfigureAwait(false);
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
            if (Disposed)
            {
                throw new ObjectDisposedException("RpcBuffer");
            }

            if (ReadBuffer.ShuttingDown || WriteBuffer.ShuttingDown)
            {
                throw new InvalidOperationException("Channel owner has closed buffers");
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
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
            if (Disposed)
            {
                return;
            }

            if (disposeManagedResources)
            {
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

                if (masterMutex != null)
                {
                    masterMutex.Close();
                    masterMutex.Dispose();
                    masterMutex = null;
                }

                Disposed = true;
            }
        }

#endregion
    }
}
