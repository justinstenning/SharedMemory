// SharedMemory (File: SharedMemoryTests\RpcBufferPacketTests.cs)
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

#if HAS_RPC_BUFFER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// RPC packet sizes (#78), sends that cannot finish, and the request bookkeeping (#52).
    /// Several tests stand in for the peer at the ring level: the owner of channel "x" reads
    /// "x_Master_SharedMemory_MMF" and writes "x_Slave_SharedMemory_MMF".
    /// </summary>
    [TestClass]
    public class RpcBufferPacketTests
    {
        const int Capacity = 256;
        const int HeaderSize = 32;
        const int PacketPayload = Capacity - HeaderSize;

        static readonly int[] BoundarySizes =
        {
            PacketPayload - 1, PacketPayload, PacketPayload + 1, 2 * PacketPayload, 2 * PacketPayload + 1
        };

        string name;
        readonly List<IDisposable> owned = new List<IDisposable>();

        [TestInitialize]
        public void Initialise()
        {
            name = "RpcPacketTest" + Guid.NewGuid().ToString();
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
        public void RPC_RequestOfBoundarySize_ArrivesIntact()
        {
            byte[] received = null;
            var master = Own(new RpcBuffer(name, bufferCapacity: Capacity));
            Own(new RpcBuffer(name, (msgId, payload) =>
            {
                received = payload;
                return new byte[] { 1 };
            }));

            var corrupted = new List<int>();
            foreach (var size in BoundarySizes)
            {
                var request = Pattern(size, size);
                received = null;
                var response = master.RemoteRequest(request, 5000);
                Assert.IsTrue(response.Success, "the request of " + size + " bytes failed");
                if (received == null || !received.SequenceEqual(request))
                {
                    corrupted.Add(size);
                }
            }

            Assert.AreEqual("", string.Join(", ", corrupted), "requests that arrived corrupted, in bytes, with " + PacketPayload + " payload bytes a packet");
        }

        [TestMethod]
        public void RPC_ResponseOfBoundarySize_ArrivesIntact()
        {
            var master = Own(new RpcBuffer(name, bufferCapacity: Capacity));
            Own(new RpcBuffer(name, (msgId, payload) =>
            {
                return Pattern(BitConverter.ToInt32(payload, 0), 3);
            }));

            var corrupted = new List<int>();
            foreach (var size in BoundarySizes)
            {
                var response = master.RemoteRequest(BitConverter.GetBytes(size), 5000);
                Assert.IsTrue(response.Success, "the request for " + size + " bytes failed");
                if (response.Data == null || !response.Data.SequenceEqual(Pattern(size, 3)))
                {
                    corrupted.Add(size);
                }
            }

            Assert.AreEqual("", string.Join(", ", corrupted), "responses that arrived corrupted, in bytes, with " + PacketPayload + " payload bytes a packet");
        }

        [TestMethod]
        public void RPC_RequestToStalledPeer_FailsWithinItsTimeout()
        {
            // Two nodes leave room for one packet, and no peer reads it, so the second of three packets gets no room
            var master = Own(new RpcBufferProbe(name, Capacity, 2));

            var watch = Stopwatch.StartNew();
            var response = master.RemoteRequest(Pattern(2 * PacketPayload + 1, 1), timeoutMs: 2000);
            watch.Stop();

            var statistics = master.Statistics;
            Assert.AreEqual(
                "success False, within 2.5 s True, pending 0, packets written 1, bytes written " + PacketPayload + ", requests sent 0, timeouts 1",
                $"success {response.Success}, within 2.5 s {watch.ElapsedMilliseconds < 2500}, pending {master.PendingRequests}, packets written {statistics.PacketsWritten}, bytes written {statistics.BytesWritten}, requests sent {statistics.RequestsSent}, timeouts {statistics.Timeouts}",
                $"the request returned after {watch.ElapsedMilliseconds} ms");

            // The peer resumes and finds the first packet, and nothing sent after the one that failed
            var toPeer = Own(new CircularBuffer(name + "_Slave_SharedMemory_MMF"));
            PacketHeader header;
            Assert.IsTrue(TryReadPacket(toPeer, 500, out header), "the first packet is missing");
            Assert.AreEqual("packet 1 of 3", $"packet {header.Packet} of {header.TotalPackets}");
            Assert.IsFalse(TryReadPacket(toPeer, 200, out header), "a packet was sent after the one that failed");
        }

        [TestMethod]
        public void RPC_RequestQueuedBehindStalledSend_FailsWithinItsTimeout()
        {
            // Two nodes leave room for one packet, and no peer reads it
            var master = Own(new RpcBufferProbe(name, Capacity, 2));

            // The first request's second packet waits for room, holding up every other send
            var first = Task.Run(() => master.RemoteRequest(Pattern(2 * PacketPayload + 1, 1), timeoutMs: 3000));
            Assert.IsTrue(WaitUntil(() => master.Statistics.PacketsWritten > 0, 2000), "the first request did not start");

            var watch = Stopwatch.StartNew();
            var second = master.RemoteRequest(new byte[] { 2 }, timeoutMs: 500);
            watch.Stop();
            first.Wait();

            Assert.AreEqual(
                "success False, within 1 s True",
                $"success {second.Success}, within 1 s {watch.ElapsedMilliseconds < 1000}",
                $"the second request returned after {watch.ElapsedMilliseconds} ms");
        }

        [TestMethod]
        public void RPC_RequestThatDoesNotWaitForItsResponse_StillWaitsForRoom()
        {
            // Two nodes leave room for one packet; the peer reads at its own pace
            var master = Own(new RpcBufferProbe(name, Capacity, 2));
            var toPeer = Own(new CircularBuffer(name + "_Slave_SharedMemory_MMF"));

            // A timeout of 0 sends without waiting for the response
            Assert.IsFalse(master.RemoteRequest(new byte[] { 1 }, timeoutMs: 0).Success);
            var reader = Task.Run(() =>
            {
                Thread.Sleep(300);
                var ids = new List<ulong>();
                PacketHeader header;
                while (ids.Count < 2 && TryReadPacket(toPeer, 2000, out header))
                {
                    ids.Add(header.MsgId);
                }
                return ids.Count;
            });

            // The ring is full until the peer reads, so this one has to wait for room, as a reply does
            Assert.IsFalse(master.RemoteRequest(new byte[] { 2 }, timeoutMs: 0).Success);

            Assert.AreEqual(
                "requests the peer read 2, requests sent 2, timeouts 0",
                $"requests the peer read {reader.Result}, requests sent {master.Statistics.RequestsSent}, timeouts {master.Statistics.Timeouts}");
        }

        [TestMethod]
        public void RPC_AbandonedRequest_IsDiscardedByTheReceiver()
        {
            var handled = new ConcurrentQueue<ulong>();
            var master = Own(new RpcBufferProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toMaster = Own(new CircularBuffer(name + "_Master_SharedMemory_MMF"));

            // The peer's send stops after the first of three packets, as a send to a stalled reader does
            WritePacket(toMaster, Packet(MessageType.RpcRequest, 7, 0, Pattern(2 * PacketPayload + 1, 7), 1, 3));
            Assert.IsTrue(WaitUntil(() => master.HasPartialRequest(7), 2000), "the first packet was not read");

            // Its next message is complete
            WritePacket(toMaster, Packet(MessageType.RpcRequest, 8, 0, Pattern(10, 8), 1, 1));
            Assert.IsTrue(WaitUntil(() => handled.Contains(8UL), 2000), "the next message was not handled");

            Assert.AreEqual("partial messages 0, handled 8", $"partial messages {master.PartialRequests}, handled {string.Join(",", handled)}");
        }

        [TestMethod]
        public void RPC_RequestWithMissingPacket_IsDiscarded()
        {
            var handled = new ConcurrentQueue<ulong>();
            var master = Own(new RpcBufferProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toMaster = Own(new CircularBuffer(name + "_Master_SharedMemory_MMF"));

            // An older sender drops a packet that gets no room and goes on with the next one
            var message = Pattern(2 * PacketPayload + 1, 9);
            WritePacket(toMaster, Packet(MessageType.RpcRequest, 9, 0, message, 1, 3));
            WritePacket(toMaster, Packet(MessageType.RpcRequest, 9, 0, message, 3, 3));
            WritePacket(toMaster, Packet(MessageType.RpcRequest, 10, 0, Pattern(10, 10), 1, 1));
            Assert.IsTrue(WaitUntil(() => master.Statistics.PacketsRead >= 3 && handled.Contains(10UL), 2000), "the next message was not handled");
            Thread.Sleep(200); // time for any other handler to run

            Assert.AreEqual(
                "requests received 1, handled 10, partial messages 0",
                $"requests received {master.Statistics.RequestsReceived}, handled {string.Join(",", handled.OrderBy(id => id))}, partial messages {master.PartialRequests}");
        }

        [TestMethod]
        public void RPC_ResponseWithMissingPacket_FailsTheRequest()
        {
            var master = Own(new RpcBufferProbe(name, Capacity, 10));
            var toPeer = Own(new CircularBuffer(name + "_Slave_SharedMemory_MMF"));
            var toMaster = Own(new CircularBuffer(name + "_Master_SharedMemory_MMF"));

            var watch = Stopwatch.StartNew();
            var pending = master.RemoteRequestAsync(new byte[] { 1 }, timeoutMs: 5000);
            PacketHeader request;
            Assert.IsTrue(TryReadPacket(toPeer, 1000, out request), "the request did not arrive");

            // An older sender drops the middle packet of its response
            var payload = Pattern(2 * PacketPayload + 1, 6);
            WritePacket(toMaster, Packet(MessageType.RpcResponse, 100, request.MsgId, payload, 1, 3));
            WritePacket(toMaster, Packet(MessageType.RpcResponse, 100, request.MsgId, payload, 3, 3));

            var response = pending.Result;
            watch.Stop();
            var intact = response.Data != null && response.Data.SequenceEqual(payload);
            Assert.AreEqual(
                "success False, before the timeout True, pending 0",
                $"success {response.Success}, before the timeout {watch.ElapsedMilliseconds < 4000}, pending {master.PendingRequests}",
                $"returned after {watch.ElapsedMilliseconds} ms; data intact {intact}");
        }

        [TestMethod]
        public void RPC_ReplyWithNoRoom_FailsAfterOneSecond()
        {
            // The reply needs three packets, two nodes leave room for one, and the peer never reads it
            var master = Own(new RpcBufferProbe(name, (msgId, payload) =>
            {
                return Pattern(2 * PacketPayload + 1, 5);
            }, Capacity, 2));
            var toMaster = Own(new CircularBuffer(name + "_Master_SharedMemory_MMF"));

            var watch = Stopwatch.StartNew();
            WritePacket(toMaster, Packet(MessageType.RpcRequest, 5, 0, new byte[] { 1 }, 1, 1));
            var failed = WaitUntil(() => master.Statistics.Timeouts > 0, 4000);
            var elapsed = watch.ElapsedMilliseconds;

            Assert.AreEqual(
                "failed True, after about 1 s True, responses sent 0, packets written 1",
                $"failed {failed}, after about 1 s {elapsed >= 900 && elapsed < 2500}, responses sent {master.Statistics.ResponsesSent}, packets written {master.Statistics.PacketsWritten}",
                $"checked after {elapsed} ms");
        }

        [TestMethod]
        public void RPC_TimedOutRequests_LeaveThePendingTableAndAreCounted()
        {
            var gate = new TaskCompletionSource<bool>();
            var master = Own(new RpcBufferProbe(name, Capacity, 10));
            Own(new RpcBuffer(name, async (msgId, payload) =>
            {
                await gate.Task.ConfigureAwait(false);
                return new byte[] { 1 };
            }));

            try
            {
                for (int i = 0; i < 20; i++)
                {
                    Assert.IsFalse(master.RemoteRequest(new byte[] { 1 }, timeoutMs: 50).Success, "request " + i + " did not time out");
                }

                Assert.AreEqual("pending 0, timeouts 20", $"pending {master.PendingRequests}, timeouts {master.Statistics.Timeouts}");
            }
            finally
            {
                gate.TrySetResult(true);
            }
        }

        [TestMethod]
        public void RPC_CancelledRequest_LeavesThePendingTable()
        {
            var gate = new TaskCompletionSource<bool>();
            var master = Own(new RpcBufferProbe(name, Capacity, 10));
            Own(new RpcBuffer(name, async (msgId, payload) =>
            {
                await gate.Task.ConfigureAwait(false);
                return new byte[] { 1 };
            }));

            try
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    var pending = master.RemoteRequestAsync(new byte[] { 1 }, 30000, cancellation.Token);
                    cancellation.Cancel();
                    Assert.IsTrue(pending.Wait(2000), "the cancelled request did not finish");

                    Assert.AreEqual(
                        "success False, pending 0, timeouts 0",
                        $"success {pending.Result.Success}, pending {master.PendingRequests}, timeouts {master.Statistics.Timeouts}");
                }
            }
            finally
            {
                gate.TrySetResult(true);
            }
        }

        [TestMethod]
        public void RPC_Statistics_AddUpWhenUpdatedFromSeveralThreads()
        {
            // RpcBuffer counts sent messages and timeouts on whichever thread sends or times out, outside any lock
            var statistics = new RpcStatistics();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var messageSent = (Action<MessageType, int>)Delegate.CreateDelegate(
                typeof(Action<MessageType, int>), statistics, typeof(RpcStatistics).GetMethod("MessageSent", flags));
            var timedOut = (Action)Delegate.CreateDelegate(
                typeof(Action), statistics, typeof(RpcStatistics).GetMethod("Timeout", flags));

            const int threadCount = 8;
            const int each = 100000;
            using (var start = new ManualResetEventSlim())
            {
                var threads = Enumerable.Range(0, threadCount).Select(_ => new Thread(() =>
                {
                    start.Wait();
                    for (int i = 0; i < each; i++)
                    {
                        messageSent(MessageType.RpcRequest, 1);
                        timedOut();
                    }
                })).ToList();
                threads.ForEach(t => t.Start());
                start.Set();
                threads.ForEach(t => t.Join());
            }

            Assert.AreEqual(
                $"requests sent {threadCount * each}, timeouts {threadCount * each}",
                $"requests sent {statistics.RequestsSent}, timeouts {statistics.Timeouts}");
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
        /// One packet as RpcProtocolHeaderV1 lays it out: MsgType at byte 0, bytes 1 to 7 zero, MsgId at 8,
        /// PayloadSize at 16, CurrentPacket at 20, TotalPackets at 22, ResponseId at 24, then the packet's part of the payload.
        /// </summary>
        static byte[] Packet(MessageType type, ulong msgId, ulong responseId, byte[] payload, int packet, int totalPackets)
        {
            int offset = (packet - 1) * PacketPayload;
            int length = Math.Min(PacketPayload, payload.Length - offset);
            var bytes = new byte[HeaderSize + length];
            bytes[0] = (byte)type;
            BitConverter.GetBytes(msgId).CopyTo(bytes, 8);
            BitConverter.GetBytes(payload.Length).CopyTo(bytes, 16);
            BitConverter.GetBytes((ushort)packet).CopyTo(bytes, 20);
            BitConverter.GetBytes((ushort)totalPackets).CopyTo(bytes, 22);
            BitConverter.GetBytes(responseId).CopyTo(bytes, 24);
            Buffer.BlockCopy(payload, offset, bytes, HeaderSize, length);
            return bytes;
        }

        static void WritePacket(CircularBuffer ring, byte[] packet)
        {
            Assert.AreEqual(packet.Length, ring.Write(packet, 0, 1000), "the packet did not fit in the ring");
        }

        struct PacketHeader
        {
            public MessageType Type;
            public ulong MsgId;
            public int Packet;
            public int TotalPackets;
        }

        static bool TryReadPacket(CircularBuffer ring, int timeoutMs, out PacketHeader header)
        {
            var bytes = new byte[Capacity];
            header = new PacketHeader();
            if (ring.Read(bytes, 0, timeoutMs) < HeaderSize)
            {
                return false;
            }
            header.Type = (MessageType)bytes[0];
            header.MsgId = BitConverter.ToUInt64(bytes, 8);
            header.Packet = BitConverter.ToUInt16(bytes, 20);
            header.TotalPackets = BitConverter.ToUInt16(bytes, 22);
            return true;
        }

        /// <summary>
        /// Shows the tables that RpcBuffer keeps protected: requests waiting for a response, and requests still arriving.
        /// </summary>
        class RpcBufferProbe : RpcBuffer
        {
            public RpcBufferProbe(string name, int bufferCapacity, int bufferNodeCount)
                : base(name, bufferCapacity, RpcProtocol.V1, bufferNodeCount)
            {
            }

            public RpcBufferProbe(string name, Func<ulong, byte[], byte[]> handler, int bufferCapacity, int bufferNodeCount)
                : base(name, handler, bufferCapacity, RpcProtocol.V1, bufferNodeCount)
            {
            }

            public int PendingRequests { get { return Requests.Count; } }

            public int PartialRequests { get { return IncomingRequests.Count; } }

            public bool HasPartialRequest(ulong msgId)
            {
                return IncomingRequests.ContainsKey(msgId);
            }
        }
    }
}
#endif
