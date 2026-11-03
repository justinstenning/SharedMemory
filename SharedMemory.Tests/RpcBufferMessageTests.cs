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
    /// What a message keeps whatever the packet format carries it in: long messages, the statistics, the protocol value
    /// that code built against 2.3 passes, several openers on one channel, and empty payloads.
    /// </summary>
    [TestClass]
    public class RpcBufferMessageTests
    {
        string name;
        readonly List<IDisposable> owned = new List<IDisposable>();

        [TestInitialize]
        public void Initialise()
        {
            name = "RpcMessageTest" + Guid.NewGuid().ToString();
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
        public void RPC_MessageOfManyPackets_ArrivesIntact()
        {
            // More packets each way than one byte can count
            const int packets = 1000;
            var request = Pattern(packets * PacketPayload, 1);
            var reply = Pattern(packets * PacketPayload, 2);
            byte[] received = null;
            var owner = Own(new RpcBuffer(name, bufferCapacity: Capacity));
            Own(new RpcBuffer(name, (msgId, payload) =>
            {
                received = payload;
                return reply;
            }));

            var response = owner.RemoteRequest(request, timeoutMs: 30000);

            // The read thread counts the last packet just after it hands on the message
            WaitUntil(() => owner.Statistics.PacketsRead >= packets, 2000);
            Assert.AreEqual(
                $"success True, request intact True, response intact True, packets sent {packets}, packets read {packets}",
                $"success {response.Success}, request intact {received != null && received.SequenceEqual(request)}, response intact {response.Data != null && response.Data.SequenceEqual(reply)}, packets sent {owner.Statistics.PacketsWritten}, packets read {owner.Statistics.PacketsRead}");
        }

        [TestMethod]
        public void RPC_Statistics_CountPacketsAndPayloadBytes()
        {
            // A request of three packets and a response of two
            const int requestSize = 2 * PacketPayload + 1;
            const int replySize = PacketPayload + 1;
            var owner = Own(new RpcBuffer(name, bufferCapacity: Capacity));
            var opener = Own(new RpcBuffer(name, (msgId, payload) =>
            {
                return Pattern(replySize, 4);
            }));

            var response = owner.RemoteRequest(Pattern(requestSize, 3), timeoutMs: 5000);

            // Each end counts the last packet it reads, and the opener the last packet it writes, just after the other end can
            // have the message. A packet's bytes are counted after the packet, so once the bytes add up, so do the packets
            WaitUntil(() => owner.Statistics.BytesRead >= replySize && opener.Statistics.BytesRead >= requestSize && opener.Statistics.BytesWritten >= replySize, 2000);
            Assert.AreEqual(
                $"success True; owner: overhead 32, wrote 3 packets of {requestSize} bytes, read 2 packets of {replySize} bytes; " +
                    $"opener: overhead 32, wrote 2 packets of {replySize} bytes, read 3 packets of {requestSize} bytes",
                $"success {response.Success}; owner: {Counts(owner.Statistics)}; opener: {Counts(opener.Statistics)}");
        }

        [TestMethod]
        public void RPC_ProtocolValue1_PairsWithTheDefault()
        {
            // 1 is the value that code built against 2.3 passes, whether it names the protocol or takes the default
            var outcomes = new List<string>();
            foreach (var ownerGetsValue1 in new[] { true, false })
            {
                var channel = name + (ownerGetsValue1 ? "a" : "b");
                Func<ulong, byte[], byte[]> ownerAnswers = (msgId, payload) => RpcEndpoint.Add(payload, 1);
                Func<ulong, byte[], byte[]> openerAnswers = (msgId, payload) => RpcEndpoint.Add(payload, 2);
                var owner = Own(ownerGetsValue1
                    ? new RpcBuffer(channel, ownerAnswers, protocolVersion: (RpcProtocol)1)
                    : new RpcBuffer(channel, ownerAnswers));
                var opener = Own(ownerGetsValue1
                    ? new RpcBuffer(channel, openerAnswers)
                    : new RpcBuffer(channel, openerAnswers, protocolVersion: (RpcProtocol)1));

                var ownersCall = owner.RemoteRequest(new byte[] { 10 }, timeoutMs: 5000);
                var openersCall = opener.RemoteRequest(new byte[] { 20 }, timeoutMs: 5000);
                outcomes.Add($"{(ownerGetsValue1 ? "owner" : "opener")} given 1: the owner's call {Reply(ownersCall)}, the opener's call {Reply(openersCall)}");
            }

            Assert.AreEqual(
                "owner given 1: the owner's call 12, the opener's call 21; opener given 1: the owner's call 12, the opener's call 21",
                string.Join("; ", outcomes));
        }

        [TestMethod]
        public void RPC_TwoOpeners_AnswerEveryOnePacketRequestFromTheOwner()
        {
            // A third instance on a channel joins as a second opener. Whichever opener reads a request answers it
            const int requests = 100;
            var owner = Own(new RpcBuffer(name));
            var first = Own(new RpcEndpoint(name, 1));
            var second = Own(new RpcEndpoint(name, 2));

            var answeredBy = new int[3];
            int answered = 0;
            while (answered < requests)
            {
                var response = owner.RemoteRequest(new byte[] { (byte)answered }, timeoutMs: 5000);
                int increment = response.Success && response.Data != null && response.Data.Length == 1 ? response.Data[0] - answered : 0;
                if (increment != 1 && increment != 2)
                {
                    break;
                }
                answeredBy[increment]++;
                answered++;
            }

            Assert.AreEqual(
                $"channel owner True, False, False; answered {requests} of {requests}",
                $"channel owner {owner.IsChannelOwner}, {first.IsChannelOwner}, {second.IsChannelOwner}; answered {answered} of {requests}",
                $"the first opener answered {answeredBy[1]}, the second {answeredBy[2]}");
        }

        [TestMethod]
        public void RPC_NullAndEmptyPayloads_ArriveAsNull()
        {
            byte[] reply = null;
            var arrived = new ConcurrentQueue<string>();
            var owner = Own(new RpcBuffer(name, bufferCapacity: Capacity));
            Own(new RpcBuffer(name, (msgId, payload) =>
            {
                arrived.Enqueue(Describe(payload));
                return reply;
            }));

            var outcomes = new List<string>();
            foreach (var payload in new[] { null, new byte[0] })
            {
                reply = payload;
                var response = owner.RemoteRequest(payload, timeoutMs: 5000);
                string request;
                arrived.TryDequeue(out request);
                outcomes.Add($"{Describe(payload)}: the request arrived as {request ?? "nothing"}, the response as {(response.Success ? Describe(response.Data) : "a failure")}");
            }

            Assert.AreEqual(
                "null: the request arrived as null, the response as null; empty: the request arrived as null, the response as null",
                string.Join("; ", outcomes));
        }

        T Own<T>(T item) where T : IDisposable
        {
            owned.Add(item);
            return item;
        }

        static string Counts(RpcStatistics statistics)
        {
            return $"overhead {statistics.ProtocolOverheadPerPacket}, wrote {statistics.PacketsWritten} packets of {statistics.BytesWritten} bytes, " +
                $"read {statistics.PacketsRead} packets of {statistics.BytesRead} bytes";
        }

        static string Reply(RpcResponse response)
        {
            return response.Success && response.Data != null && response.Data.Length == 1 ? response.Data[0].ToString() : "failed";
        }

        static string Describe(byte[] payload)
        {
            return payload == null ? "null" : payload.Length == 0 ? "empty" : payload.Length + " bytes";
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
    }
}
#endif
