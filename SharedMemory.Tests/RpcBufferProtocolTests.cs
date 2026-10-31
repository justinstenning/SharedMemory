#if HAS_RPC_BUFFER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;
using static SharedMemoryTests.RpcPackets;

namespace SharedMemoryTests
{
    /// <summary>
    /// The packet header: messages of any length, headers longer than this version's, headers that do not describe a packet;
    /// the protocol values a constructor takes; and messages a receiver does not understand, which it answers once with an
    /// error flagged "not understood", and replies, which it never answers.
    /// Some tests stand in for the peer at the ring level: the owner of channel "x" reads "x_Owner_SharedMemory_MMF" and
    /// writes "x_Opener_SharedMemory_MMF".
    /// </summary>
    [TestClass]
    public class RpcBufferProtocolTests
    {
        string name;
        readonly List<IDisposable> owned = new List<IDisposable>();

        [TestInitialize]
        public void Initialise()
        {
            name = "RpcProtocolTest" + Guid.NewGuid().ToString();
        }

        [TestCleanup]
        public void Cleanup()
        {
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                owned[i].Dispose();
            }
            owned.Clear();
        }

        [TestMethod]
        public void RPC_MessageOf65536Packets_ArrivesIntact()
        {
            // One packet more each way than a 16-bit count can number
            const int packets = 65536;
            var request = Pattern(packets * PacketPayload, 1);
            var reply = Pattern(packets * PacketPayload, 2);
            byte[] received = null;
            var owner = Own(new RpcBuffer(name, bufferCapacity: Capacity));
            Own(new RpcBuffer(name, (msgId, payload) =>
            {
                received = payload;
                return reply;
            }));

            var response = owner.RemoteRequest(request, timeoutMs: 60000);

            // The read thread counts the last packet just after it hands on the message
            WaitUntil(() => owner.Statistics.PacketsRead >= packets, 2000);
            Assert.AreEqual(
                $"success True, request intact True, response intact True, packets sent {packets}, packets read {packets}",
                $"success {response.Success}, request intact {received != null && received.SequenceEqual(request)}, response intact {response.Data != null && response.Data.SequenceEqual(reply)}, packets sent {owner.Statistics.PacketsWritten}, packets read {owner.Statistics.PacketsRead}");
        }

        [TestMethod]
        public void RPC_HeaderLongerThan32Bytes_IsReadPastItsExtraBytes()
        {
            byte[] received = null;
            Own(new RpcBuffer(name, (msgId, payload) =>
            {
                received = payload;
                return new byte[] { 1 };
            }, bufferCapacity: Capacity));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));

            // A request from a later version, whose header has 8 bytes this version does not know, then a payload that fills the node
            const int headerLength = HeaderSize + 8;
            var message = Pattern(Capacity - headerLength, 5);
            var afterHeader = new byte[8 + message.Length];
            for (int i = 0; i < 8; i++)
            {
                afterHeader[i] = 0xEE;
            }
            Buffer.BlockCopy(message, 0, afterHeader, 8, message.Length);
            WritePacket(toOwner, Packet(new PacketHeader
            {
                HeaderLength = headerLength,
                Type = MessageType.RpcRequest,
                PayloadSize = message.Length,
                MsgId = 7,
                PayloadOffset = 0,
                PacketLength = message.Length,
            }, afterHeader));

            PacketHeader reply;
            var answered = TryReadPacket(fromOwner, 3000, out reply);
            Assert.AreEqual(
                $"the handler got {message.Length} bytes, intact True; answered True, a response to 7",
                $"the handler got {received?.Length ?? 0} bytes, intact {received != null && received.SequenceEqual(message)}; answered {answered}, a {reply.Type} to {reply.ResponseId}"
                    .Replace("RpcResponse", "response"));
        }

        [TestMethod]
        public void RPC_MalformedHeader_IsDroppedAndTheNextRequestAnswered()
        {
            var handled = new ConcurrentQueue<ulong>();
            var owner = Own(new RpcBufferProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));

            // Each is the one packet of a request. Believed, the first two headers would make complete requests; the third
            // would copy past the end of its message, which copying the bytes refuses as well
            var cases = new[]
            {
                Tuple.Create("HeaderLength 31", new PacketHeader
                    { HeaderLength = HeaderSize - 1, PayloadSize = 10, PacketLength = 10 }, Pattern(10, 1)),
                Tuple.Create("PacketLength past the node", new PacketHeader
                    { HeaderLength = HeaderSize, PayloadSize = PacketPayload + 1, PacketLength = PacketPayload + 1 }, Pattern(PacketPayload, 2)),
                Tuple.Create("PayloadOffset + PacketLength past PayloadSize", new PacketHeader
                    { HeaderLength = HeaderSize, PayloadSize = 10, PayloadOffset = 0, PacketLength = 20 }, Pattern(20, 3)),
            };

            var outcomes = new List<string>();
            var expected = new List<string>();
            for (int i = 0; i < cases.Length; i++)
            {
                ulong malformedId = 11UL + (ulong)i, nextId = 101UL + (ulong)i;
                var header = cases[i].Item2;
                header.Type = MessageType.RpcRequest;
                header.MsgId = malformedId;
                WritePacket(toOwner, Packet(header, cases[i].Item3));
                WritePacket(toOwner, Packet(MessageType.RpcRequest, nextId, 0, Pattern(10, i), 1, 1));

                // The replies until the next request's, and any that come soon after it
                var replies = new List<ulong>();
                PacketHeader reply;
                while (!replies.Contains(nextId) && TryReadPacket(fromOwner, 3000, out reply))
                {
                    replies.Add(reply.ResponseId);
                }
                Thread.Sleep(200); // time for any other handler to run
                while (TryReadPacket(fromOwner, 100, out reply))
                {
                    replies.Add(reply.ResponseId);
                }

                var handledNow = new List<ulong>();
                ulong id;
                while (handled.TryDequeue(out id))
                {
                    handledNow.Add(id);
                }
                outcomes.Add($"{cases[i].Item1}: handled {string.Join(",", handledNow)}, replies to {string.Join(",", replies)}");
                expected.Add($"{cases[i].Item1}: handled {nextId}, replies to {nextId}");
            }

            Assert.AreEqual(
                string.Join("; ", expected) + "; partial messages 0",
                string.Join("; ", outcomes) + $"; partial messages {owner.PartialRequests}");
        }

        [TestMethod]
        public void RPC_UndefinedProtocolValue_Throws()
        {
            var outcomes = new List<string>();
            foreach (var value in new[] { 0, 3 })
            {
                var channel = name + "-" + value;
                string threw;
                try
                {
                    Own(new RpcBuffer(channel, Capacity, (RpcProtocol)value));
                    threw = "nothing";
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    threw = ex.GetType().Name + " for " + ex.ParamName;
                }
                catch (Exception ex)
                {
                    threw = ex.GetType().Name;
                }

                // Straight away, the name is free for a new owner
                outcomes.Add($"{value}: threw {threw}, next {RoundTrip(channel)}");
            }

            Assert.AreEqual(
                "0: threw ArgumentOutOfRangeException for protocolVersion, next reply 2; 3: threw ArgumentOutOfRangeException for protocolVersion, next reply 2",
                string.Join("; ", outcomes));
        }

        [TestMethod]
        public void RPC_UnknownMessageType_GetsOneNotUnderstoodReply()
        {
            var handled = new ConcurrentQueue<ulong>();
            var owner = Own(new RpcBufferProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));

            // A message type that this version does not know, as a later version might send: in one packet, in three, and in
            // three whose first packet was lost. Then a request, whose response comes once the owner has read them all
            const MessageType unknown = (MessageType)9;
            var three = Pattern(2 * PacketPayload + 1, 2);
            WritePacket(toOwner, Packet(unknown, 21, 0, Pattern(10, 1), 1, 1));
            for (int packet = 1; packet <= 3; packet++)
            {
                WritePacket(toOwner, Packet(unknown, 22, 0, three, packet, 3));
            }
            for (int packet = 2; packet <= 3; packet++)
            {
                WritePacket(toOwner, Packet(unknown, 23, 0, three, packet, 3));
            }
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 24, 0, Pattern(10, 3), 1, 1));

            // The owner sends the not-understood replies from the thread pool, so they can follow the response
            WaitUntil(() => owner.Statistics.ErrorsSent >= 2, 10000);
            var replies = RepliesUntil(fromOwner, 24);
            Assert.AreEqual(
                "handled 24; replies: ErrorInRpc to 21, flags 1, 0 bytes; ErrorInRpc to 22, flags 1, 0 bytes; RpcResponse to 24, flags 0, 1 bytes; " +
                    "partial messages 0, requests received 1, errors sent 2",
                $"handled {string.Join(",", handled.OrderBy(id => id))}; replies: {string.Join("; ", replies)}; " +
                    $"partial messages {owner.PartialRequests}, requests received {owner.Statistics.RequestsReceived}, errors sent {owner.Statistics.ErrorsSent}");
        }

        [TestMethod]
        public void RPC_RequestWithUnknownFlag_GetsNotUnderstoodReply()
        {
            var handled = new ConcurrentQueue<ulong>();
            var owner = Own(new RpcBufferProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));

            // A request with flag bit 15, which this version does not know, as a later version might send: in one packet and in
            // three. A request with bit 0, which this version knows only on an error. Then a request without a flag, whose
            // response comes once the owner has read them all
            const ushort unknownFlag = 0x8000;
            var three = Pattern(2 * PacketPayload + 1, 2);
            WritePacket(toOwner, WithFlags(Packet(MessageType.RpcRequest, 30, 0, Pattern(10, 0), 1, 1), 1));
            WritePacket(toOwner, WithFlags(Packet(MessageType.RpcRequest, 31, 0, Pattern(10, 1), 1, 1), unknownFlag));
            for (int packet = 1; packet <= 3; packet++)
            {
                WritePacket(toOwner, WithFlags(Packet(MessageType.RpcRequest, 32, 0, three, packet, 3), unknownFlag));
            }
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 33, 0, Pattern(10, 3), 1, 1));

            // The owner sends the not-understood replies from the thread pool, so they can follow the response
            WaitUntil(() => owner.Statistics.ErrorsSent >= 3, 10000);
            var replies = RepliesUntil(fromOwner, 33);
            Assert.AreEqual(
                "handled 33; replies: ErrorInRpc to 30, flags 1, 0 bytes; ErrorInRpc to 31, flags 1, 0 bytes; ErrorInRpc to 32, flags 1, 0 bytes; " +
                    "RpcResponse to 33, flags 0, 1 bytes; partial messages 0, requests received 1, errors sent 3",
                $"handled {string.Join(",", handled.OrderBy(id => id))}; replies: {string.Join("; ", replies)}; " +
                    $"partial messages {owner.PartialRequests}, requests received {owner.Statistics.RequestsReceived}, errors sent {owner.Statistics.ErrorsSent}");
        }

        [TestMethod]
        public void RPC_ResponseWithUnknownFlag_FailsTheRequest()
        {
            var owner = Own(new RpcBufferProbe(name, Capacity, 10));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));

            var watch = Stopwatch.StartNew();
            var pending = owner.RemoteRequestAsync(new byte[] { 1 }, timeoutMs: 5000);
            PacketHeader request;
            Assert.IsTrue(TryReadPacket(fromOwner, 1000, out request), "the request did not arrive");

            // Its response, with flag bit 15, which this version does not know
            WritePacket(toOwner, WithFlags(Packet(MessageType.RpcResponse, 41, request.MsgId, Pattern(10, 4), 1, 1), 0x8000));

            var response = pending.Result;
            watch.Stop();
            PacketHeader reply;
            var answered = TryReadPacket(fromOwner, 500, out reply);
            Assert.AreEqual(
                "success False, before the timeout True, pending 0, answered False",
                $"success {response.Success}, before the timeout {watch.ElapsedMilliseconds < 4000}, pending {owner.PendingRequests}, answered {answered}",
                $"returned after {watch.ElapsedMilliseconds} ms");
        }

        [TestMethod]
        public void RPC_ErrorIsNeverAnswered()
        {
            var owner = Own(new RpcBufferProbe(name, (msgId, payload) => new byte[] { 1 }, Capacity, 10));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));

            var pending = owner.RemoteRequestAsync(new byte[] { 1 }, timeoutMs: 5000);
            PacketHeader request;
            Assert.IsTrue(TryReadPacket(fromOwner, 1000, out request), "the request did not arrive");

            // An error to the owner's request with flag bit 15, which this version does not know: it fails the request without
            // its payload, where the error would give it as the response's data. Errors to no request, with no flag, with the
            // not-understood flag and with bit 15. Then a request, whose response comes once the owner has read them
            WritePacket(toOwner, WithFlags(Packet(MessageType.ErrorInRpc, 51, request.MsgId, Pattern(10, 5), 1, 1), 0x8000));
            WritePacket(toOwner, Packet(MessageType.ErrorInRpc, 52, 12345, Pattern(10, 5), 1, 1));
            WritePacket(toOwner, WithFlags(Packet(MessageType.ErrorInRpc, 53, 12346, new byte[0], 1, 1), 1));
            WritePacket(toOwner, WithFlags(Packet(MessageType.ErrorInRpc, 54, 12347, new byte[0], 1, 1), 0x8000));
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 55, 0, Pattern(10, 6), 1, 1));

            var replies = RepliesUntil(fromOwner, 55);
            var response = pending.Result;
            Assert.AreEqual(
                "request failed True, data null True; replies: RpcResponse to 55, flags 0, 1 bytes",
                $"request failed {!response.Success}, data null {response.Data == null}; replies: {string.Join("; ", replies)}");
        }

        [TestMethod]
        public void RPC_NotUnderstoodReply_FailsTheCall()
        {
            var owner = Own(new RpcBufferProbe(name, Capacity, 10));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));

            var watch = Stopwatch.StartNew();
            var pending = owner.RemoteRequestAsync(new byte[] { 1 }, timeoutMs: 5000);
            PacketHeader request;
            Assert.IsTrue(TryReadPacket(fromOwner, 1000, out request), "the request did not arrive");

            // What a peer that does not understand the request answers: an error with the not-understood flag and no payload
            WritePacket(toOwner, WithFlags(Packet(MessageType.ErrorInRpc, 61, request.MsgId, new byte[0], 1, 1), 1));

            var response = pending.Result;
            watch.Stop();
            Assert.AreEqual(
                "success False, data null True, before the timeout True, pending 0",
                $"success {response.Success}, data null {response.Data == null}, before the timeout {watch.ElapsedMilliseconds < 4000}, pending {owner.PendingRequests}",
                $"returned after {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// The packets the owner sends until its reply to <paramref name="lastId"/>, and any in the 500 ms after it, each as
        /// "type to the id it answers, flags, payload bytes", in order of that id: the owner sends a handler's reply and the
        /// not-understood reply from the thread pool, so their order is not fixed.
        /// </summary>
        static List<string> RepliesUntil(CircularBuffer fromOwner, ulong lastId)
        {
            var replies = new List<PacketHeader>();
            PacketHeader reply;
            while (!replies.Any(r => r.ResponseId == lastId) && TryReadPacket(fromOwner, 3000, out reply))
            {
                replies.Add(reply);
            }
            while (TryReadPacket(fromOwner, 500, out reply))
            {
                replies.Add(reply);
            }
            return replies.OrderBy(r => r.ResponseId)
                .Select(r => $"{r.Type} to {r.ResponseId}, flags {r.Flags}, {r.PayloadSize} bytes")
                .ToList();
        }

        /// <summary>
        /// A new owner and opener on the channel, and one request from the opener.
        /// </summary>
        string RoundTrip(string channel)
        {
            try
            {
                Own(new RpcBuffer(channel, (msgId, payload) => new byte[] { 2 }, bufferCapacity: Capacity));
                var opener = Own(new RpcBuffer(channel));
                var response = opener.RemoteRequest(new byte[] { 1 }, 2000);
                return response.Success && response.Data != null ? "reply " + response.Data[0] : "no reply";
            }
            catch (Exception ex)
            {
                return "threw " + ex.GetType().Name;
            }
        }

        T Own<T>(T item) where T : IDisposable
        {
            owned.Add(item);
            return item;
        }

        static byte[] Pattern(int length, int seed)
        {
            // Never zero, so that a packet left unfilled shows
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = (byte)(1 + (i * 7 + seed) % 251);
            }
            return bytes;
        }

        static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > timeoutMs)
                {
                    return false;
                }
                Thread.Sleep(10);
            }
            return true;
        }

        /// <summary>
        /// Shows the tables of incoming requests still missing packets and of requests waiting for a response, which RpcBuffer
        /// keeps protected.
        /// </summary>
        class RpcBufferProbe : RpcBuffer
        {
            public RpcBufferProbe(string name, int bufferCapacity, int bufferNodeCount)
                : base(name, bufferCapacity, bufferNodeCount: bufferNodeCount)
            {
            }

            public RpcBufferProbe(string name, Func<ulong, byte[], byte[]> handler, int bufferCapacity, int bufferNodeCount)
                : base(name, handler, bufferCapacity, bufferNodeCount: bufferNodeCount)
            {
            }

            public int PartialRequests { get { return IncomingRequests.Count; } }

            public int PendingRequests { get { return Requests.Count; } }
        }
    }
}
#endif
