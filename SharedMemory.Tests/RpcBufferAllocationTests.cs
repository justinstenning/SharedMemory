#if HAS_RPC_BUFFER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;
using static SharedMemoryTests.RpcPackets;

namespace SharedMemoryTests
{
    /// <summary>
    /// BufferAndRingSafety FR-011: what a receiver allocates for a message. A message's first packet allocates its size, or 1 MiB
    /// if it claims more, and the array doubles as packets arrive, never past that size (#98).
    /// The tests that count bytes stand in for the peer at the ring level: the owner of channel "x" reads
    /// "x_Owner_SharedMemory_MMF" and writes "x_Opener_SharedMemory_MMF". They count what the owner's read thread allocates
    /// with GC.GetAllocatedBytesForCurrentThread, read on that thread (<see cref="ReadThreadAllocated"/>).
    /// </summary>
    [TestClass]
    public class RpcBufferAllocationTests
    {
        const int OneMiB = 1 << 20;

        // What the read thread allocates between two counts besides a message's arrays: the message's request and the task
        // that starts its handler, the end of completing the first count's request and completing the second's. A few
        // hundred bytes on every build; an array of 64 KiB or more does not fit in it
        const int Slack = 64 * 1024;

        string name;
        ulong nextPeerId = 1000; // the ids of the messages the tests send as the peer
        readonly List<IDisposable> owned = new List<IDisposable>();

        [TestInitialize]
        public void Initialise()
        {
            name = "RpcAllocationTest" + Guid.NewGuid().ToString();
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
        [DataRow(int.MaxValue)]
        [DataRow(256 * OneMiB)]
        public void RPC_FirstPacketClaimingAHugeMessage_AllocatesUnder2MiBAndTheNextRequestIsAnswered(int claimed)
        {
            var handled = new ConcurrentQueue<ulong>();
            var owner = Own(new AllocationProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));

            // One packet of 8 bytes, the first of a request that claims to be much longer
            var before = ReadThreadAllocated(owner, toOwner, fromOwner);
            WritePacket(toOwner, Packet(new PacketHeader
            {
                HeaderLength = HeaderSize,
                Type = MessageType.RpcRequest,
                PayloadSize = claimed,
                MsgId = 7,
                PayloadOffset = 0,
                PacketLength = 8,
            }, Pattern(8, 7)));
            var after = ReadThreadAllocated(owner, toOwner, fromOwner);
            long allocated = after.Bytes - before.Bytes;
            Console.WriteLine($"A first packet claiming {claimed} bytes with 8: the read thread allocated {allocated} bytes");

            // The read thread reads on: the next request is answered
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 8, 0, Pattern(10, 8), 1, 1));
            PacketHeader reply;
            var answered = TryReadPacket(fromOwner, 3000, out reply);
            WaitUntil(() => handled.Contains(8UL), 2000);

            Assert.AreEqual(
                "allocated under 2 MiB True, counted on the read thread True; answered True, a response to 8, handled 8, partial messages 0",
                ($"allocated under 2 MiB {allocated < 2 * OneMiB}, counted on the read thread {before.OnReadThread(after)}; " +
                    $"answered {answered}, a {reply.Type} to {reply.ResponseId}, handled {string.Join(",", handled)}, partial messages {owner.PartialRequests}")
                    .Replace("RpcResponse", "response"),
                $"the read thread allocated {allocated} bytes");
        }

        [TestMethod]
        [DataRow(16 * OneMiB)]
        [DataRow(16 * OneMiB + 5)]
        public void RPC_MessageOf16MiB_ArrivesIntactEachWay(int size)
        {
            // 16 MiB is 1 MiB doubled four times; 5 bytes more is not, so its last growth is to its size alone
            var request = Pattern(size, 1);
            var reply = Pattern(size, 2);
            byte[] received = null;
            var owner = Own(new RpcBuffer(name));
            Own(new RpcBuffer(name, (msgId, payload) =>
            {
                received = payload;
                return reply;
            }));

            var response = owner.RemoteRequest(request, timeoutMs: 30000);

            Assert.AreEqual(
                "success True, request intact True, response intact True",
                $"success {response.Success}, request intact {received != null && received.SequenceEqual(request)}, response intact {response.Data != null && response.Data.SequenceEqual(reply)}");
        }

        [TestMethod]
        [DataRow(OneMiB)]
        [DataRow(OneMiB + 1)]
        public void RPC_MessageOf1MiBOrOneByteMore_AllocatesItOnceOrGrowsOnceToItsSize(int size)
        {
            byte[] received = null;
            var owner = Own(new AllocationProbe(name, (msgId, payload) =>
            {
                received = payload;
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));
            var message = Pattern(size, 3);
            int packets = (size + PacketPayload - 1) / PacketPayload;

            var before = ReadThreadAllocated(owner, toOwner, fromOwner);
            for (int p = 1; p <= packets; p++)
            {
                WritePacket(toOwner, Packet(MessageType.RpcRequest, 7, 0, message, p, packets));
            }
            PacketHeader reply;
            var answered = TryReadPacket(fromOwner, 5000, out reply);
            var after = ReadThreadAllocated(owner, toOwner, fromOwner);
            long allocated = after.Bytes - before.Bytes;
            Console.WriteLine($"A message of {size} bytes in {packets} packets: the read thread allocated {allocated} bytes");

            // 1 MiB is one array of its size; one byte more is 1 MiB, then an array of exactly its size
            long arrays = size <= OneMiB ? size : OneMiB + (long)size;
            Assert.AreEqual(
                $"answered True, a response to 7, handler got {size} bytes, intact True, allocated its arrays and under 64 KiB more True, counted on the read thread True",
                ($"answered {answered}, a {reply.Type} to {reply.ResponseId}, handler got {received?.Length ?? 0} bytes, intact {received != null && received.SequenceEqual(message)}, " +
                    $"allocated its arrays and under 64 KiB more {allocated >= arrays && allocated < arrays + Slack}, counted on the read thread {before.OnReadThread(after)}")
                    .Replace("RpcResponse", "response"),
                $"the read thread allocated {allocated} bytes; its arrays are {arrays}");
        }

        /// <summary>
        /// A request that claims 256 MiB and sends one packet past 1 MiB: its array doubles to 2 MiB, rather than grow to the size
        /// it claims, so the read thread holds at most twice what arrived.
        /// </summary>
        [TestMethod]
        public void RPC_MessageClaimingMoreThanItSends_GrowsByDoublingNotToItsClaim()
        {
            const int claimed = 256 * OneMiB;
            var owner = Own(new AllocationProbe(name, (msgId, payload) => new byte[] { 1 }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));
            // Enough packets to pass 1 MiB by less than one packet: the array grows once
            int packets = OneMiB / PacketPayload + 1;

            var before = ReadThreadAllocated(owner, toOwner, fromOwner);
            for (int p = 0; p < packets; p++)
            {
                WritePacket(toOwner, Packet(new PacketHeader
                {
                    HeaderLength = HeaderSize,
                    Type = MessageType.RpcRequest,
                    PayloadSize = claimed,
                    MsgId = 7,
                    PayloadOffset = p * PacketPayload,
                    PacketLength = PacketPayload,
                }, Pattern(PacketPayload, p)));
            }
            var after = ReadThreadAllocated(owner, toOwner, fromOwner);
            long allocated = after.Bytes - before.Bytes;
            Console.WriteLine($"{packets} packets of a message claiming {claimed} bytes: the read thread allocated {allocated} bytes");

            // 1 MiB for the first packet, then 2 MiB
            long arrays = 3L * OneMiB;
            Assert.AreEqual(
                "allocated its arrays and under 64 KiB more True, counted on the read thread True",
                $"allocated its arrays and under 64 KiB more {allocated >= arrays && allocated < arrays + Slack}, counted on the read thread {before.OnReadThread(after)}",
                $"the read thread allocated {allocated} bytes; its arrays are {arrays}");
        }

        [TestMethod]
        public void RPC_LaterPacketClaimingASmallerMessage_DropsIt()
        {
            var handled = new ConcurrentQueue<ulong>();
            var owner = Own(new AllocationProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));

            // A request of three packets whose second packet claims the message ends with it: believed, the request would be
            // complete, with the third packet's bytes never sent
            var message = Pattern(3 * PacketPayload, 11);
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 7, 0, message, 1, 3));
            var second = Packet(MessageType.RpcRequest, 7, 0, message, 2, 3);
            SetPayloadSize(second, 2 * PacketPayload);
            WritePacket(toOwner, second);
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 7, 0, message, 3, 3));
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 8, 0, Pattern(10, 8), 1, 1));
            Assert.IsTrue(WaitUntil(() => handled.Contains(8UL), 2000), "the next message was not handled");
            Thread.Sleep(200); // time for any other handler to run

            Assert.AreEqual(
                "requests received 1, handled 8, partial messages 0",
                $"requests received {owner.Statistics.RequestsReceived}, handled {string.Join(",", handled.OrderBy(id => id))}, partial messages {owner.PartialRequests}");
        }

        [TestMethod]
        public void RPC_ArrayASubclassReplacesMidMessage_DropsTheMessage()
        {
            var handled = new ConcurrentQueue<string>();
            var owner = Own(new AllocationProbe(name, (msgId, payload) =>
            {
                handled.Enqueue($"{msgId}: {payload?.Length ?? 0} bytes");
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));

            // A subclass gives a request still arriving an array longer than the message, once its first packet is read
            var message = Pattern(3 * PacketPayload, 12);
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 7, 0, message, 1, 3));
            Assert.IsTrue(WaitUntil(() => owner.Statistics.PacketsRead >= 1, 2000), "the first packet was not read");
            Assert.IsTrue(owner.ReplaceData(7, new byte[5000]), "the request is not arriving");
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 7, 0, message, 2, 3));
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 7, 0, message, 3, 3));
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 8, 0, Pattern(10, 8), 1, 1));
            Assert.IsTrue(WaitUntil(() => handled.Any(h => h.StartsWith("8:")), 2000), "the next message was not handled");
            Thread.Sleep(200); // time for any other handler to run

            Assert.AreEqual(
                "handled 8: 10 bytes, partial messages 0",
                $"handled {string.Join(", ", handled)}, partial messages {owner.PartialRequests}");
        }

        [TestMethod]
        public void RPC_RequestThatStopsAfterItsArrayGrew_IsNeverHandled()
        {
            var handled = new ConcurrentQueue<ulong>();
            var owner = Own(new AllocationProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));

            // A request of 3 MiB whose sender stops at 2.5 MiB, after its array has grown twice, then a complete request
            var message = Pattern(3 * OneMiB, 4);
            int packets = (message.Length + PacketPayload - 1) / PacketPayload;
            int sent = (5 * OneMiB / 2) / PacketPayload;
            for (int p = 1; p <= sent; p++)
            {
                WritePacket(toOwner, Packet(MessageType.RpcRequest, 9, 0, message, p, packets));
            }
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 10, 0, Pattern(10, 10), 1, 1));
            Assert.IsTrue(WaitUntil(() => handled.Contains(10UL), 5000), "the next message was not handled");
            Thread.Sleep(200); // time for any other handler to run

            Assert.AreEqual(
                "requests received 1, handled 10, partial messages 0",
                $"requests received {owner.Statistics.RequestsReceived}, handled {string.Join(",", handled.OrderBy(id => id))}, partial messages {owner.PartialRequests}");
        }

        [TestMethod]
        public void RPC_ResponseMissingAPacketAfterItsArrayGrew_FailsTheRequestWithNoData()
        {
            var owner = Own(new AllocationProbe(name, null, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));

            var watch = Stopwatch.StartNew();
            var pending = owner.RemoteRequestAsync(new byte[] { 1 }, timeoutMs: 10000);
            PacketHeader request;
            Assert.IsTrue(TryReadPacket(fromOwner, 2000, out request), "the request did not arrive");

            // A response of 3 MiB that loses a packet at 2.5 MiB, after its array has grown twice
            var payload = Pattern(3 * OneMiB, 6);
            int packets = (payload.Length + PacketPayload - 1) / PacketPayload;
            int lost = (5 * OneMiB / 2) / PacketPayload;
            for (int p = 1; p <= packets; p++)
            {
                if (p != lost)
                {
                    WritePacket(toOwner, Packet(MessageType.RpcResponse, 100, request.MsgId, payload, p, packets));
                }
            }

            var response = pending.Result;
            watch.Stop();
            Assert.AreEqual(
                "success False, data none, before the timeout True, pending 0",
                $"success {response.Success}, data {(response.Data == null ? "none" : response.Data.Length + " bytes")}, before the timeout {watch.ElapsedMilliseconds < 8000}, pending {owner.PendingRequests}",
                $"returned after {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// What the owner's read thread has allocated so far, read on that thread. The owner sends a request and waits for
        /// it, blocking a pool thread; this test answers it as the peer. The read thread completes the request where it is,
        /// as it does for a blocking RemoteRequest, and so runs a continuation attached to it before it reads another
        /// packet: the continuation reads GC.GetAllocatedBytesForCurrentThread.
        /// </summary>
        Count ReadThreadAllocated(AllocationProbe owner, CircularBuffer toOwner, CircularBuffer fromOwner)
        {
            var call = Task.Run(() => owner.RemoteRequest(new byte[] { 1 }, 10000));
            PacketHeader sent;
            Assert.IsTrue(TryReadPacket(fromOwner, 5000, out sent) && sent.Type == MessageType.RpcRequest, "the owner's request did not arrive");
            var request = owner.PendingRequest(sent.MsgId);
            Assert.IsNotNull(request, "the owner's request is not pending");

            var count = new Count();
            var counted = request.ResponseReady.Task.ContinueWith(t =>
            {
                count.Bytes = GC.GetAllocatedBytesForCurrentThread();
                count.Thread = Thread.CurrentThread.ManagedThreadId;
                count.OnPool = Thread.CurrentThread.IsThreadPoolThread;
            }, TaskContinuationOptions.ExecuteSynchronously);
            WritePacket(toOwner, Packet(MessageType.RpcResponse, nextPeerId++, sent.MsgId, new byte[0], 1, 1));

            Assert.IsTrue(call.Wait(10000) && call.Result.Success, "the owner's request failed");
            Assert.IsTrue(counted.Wait(10000), "the count was not taken");
            return count;
        }

        class Count
        {
            public long Bytes;
            public int Thread;
            public bool OnPool;

            /// <summary>
            /// Whether both counts were taken on one thread of the owner's own, which only its read thread is.
            /// </summary>
            public bool OnReadThread(Count later)
            {
                return Thread == later.Thread && !OnPool && !later.OnPool && Thread != System.Threading.Thread.CurrentThread.ManagedThreadId;
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
        /// Shows the tables that RpcBuffer keeps protected: requests waiting for a response, and requests still arriving, whose
        /// Data it can replace as a subclass can.
        /// </summary>
        class AllocationProbe : RpcBuffer
        {
            public AllocationProbe(string name, Func<ulong, byte[], byte[]> handler, int bufferCapacity, int bufferNodeCount)
                : base(name, handler, bufferCapacity, bufferNodeCount: bufferNodeCount)
            {
            }

            public int PendingRequests { get { return Requests.Count; } }

            public int PartialRequests { get { return IncomingRequests.Count; } }

            public RpcRequest PendingRequest(ulong msgId)
            {
                RpcRequest request;
                return Requests.TryGetValue(msgId, out request) ? request : null;
            }

            public bool ReplaceData(ulong msgId, byte[] data)
            {
                RpcRequest request;
                if (!IncomingRequests.TryGetValue(msgId, out request))
                {
                    return false;
                }
                request.Data = data;
                return true;
            }
        }
    }
}
#endif
