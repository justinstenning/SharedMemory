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
    /// The packet header: messages of any length, headers longer than this version's, headers that do not describe a packet.
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
        /// Shows the table of incoming requests still missing packets, which RpcBuffer keeps protected.
        /// </summary>
        class RpcBufferProbe : RpcBuffer
        {
            public RpcBufferProbe(string name, Func<ulong, byte[], byte[]> handler, int bufferCapacity, int bufferNodeCount)
                : base(name, handler, bufferCapacity, bufferNodeCount: bufferNodeCount)
            {
            }

            public int PartialRequests { get { return IncomingRequests.Count; } }
        }
    }
}
#endif
