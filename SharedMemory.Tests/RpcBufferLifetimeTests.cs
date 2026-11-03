// SharedMemory (File: SharedMemoryTests\RpcBufferLifetimeTests.cs)
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;
using static SharedMemoryTests.RpcPackets;

namespace SharedMemoryTests
{
    /// <summary>
    /// Where the code after an RPC await runs, a read thread that meets a bad packet, a client that restarts (#79),
    /// Dispose (#52), and a constructor that fails part-way. Some tests stand in for the peer at the ring level:
    /// the owner of channel "x" reads "x_Owner_SharedMemory_MMF" and writes "x_Opener_SharedMemory_MMF".
    /// </summary>
    [TestClass]
    public class RpcBufferLifetimeTests
    {
        string name;
        readonly List<IDisposable> owned = new List<IDisposable>();

        [TestInitialize]
        public void Initialise()
        {
            name = "RpcLifetimeTest" + Guid.NewGuid().ToString();
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
        public void RPC_CodeAfterAwait_RunsOffTheReadThread()
        {
            using (var reply = new ManualResetEventSlim())
            {
                var owner = Own(new RpcBuffer(name, bufferCapacity: Capacity));
                Own(new RpcBuffer(name, (msgId, payload) =>
                {
                    // Reply only once the caller awaits, as a peer in another process usually does
                    reply.Wait(5000);
                    return new byte[] { payload[0] };
                }));

                var outcome = Task.Run(() =>
                {
                    // Runs up to its await, which the held reply leaves waiting
                    var awaiting = AwaitThenRequestAgain(owner);
                    reply.Set();
                    return awaiting;
                });

                Assert.IsTrue(outcome.Wait(10000), "the requests did not finish");
                Assert.AreEqual("first True, on a pool thread True, second True, within 1 s True", outcome.Result);
            }
        }

        static async Task<string> AwaitThenRequestAgain(RpcBuffer owner)
        {
            var first = await owner.RemoteRequestAsync(new byte[] { 1 }, 5000);
            var onPool = Thread.CurrentThread.IsThreadPoolThread;

            // Only the read thread can take this request's response
            var watch = Stopwatch.StartNew();
            var second = owner.RemoteRequest(new byte[] { 2 }, 2000);
            return $"first {first.Success}, on a pool thread {onPool}, second {second.Success}, within 1 s {watch.ElapsedMilliseconds < 1000}";
        }

        [TestMethod]
        public void RPC_SendMessageOverride_ContinuesOffTheReadThread()
        {
            // A subclass's code after base.SendMessage, for a blocking RemoteRequest
            var owner = Own(new SendMessageProbe(name, Capacity));
            Own(new RpcBuffer(name, (msgId, payload) =>
            {
                // Reply only once the subclass's continuation is attached
                owner.Attached.Wait(5000);
                return new byte[] { 1 };
            }));

            var response = owner.RemoteRequest(new byte[] { 1 }, 5000);

            Assert.AreEqual(
                "success True, continued True, on a pool thread True",
                $"success {response.Success}, continued {owner.Continued}, on a pool thread {owner.OnPool}");
        }

        [TestMethod]
        public void RPC_MalformedPacket_DoesNotStopTheReadThread()
        {
            var handled = new ConcurrentQueue<ulong>();
            var owner = Own(new RpcBufferProbe(name, (msgId, payload) =>
            {
                handled.Enqueue(msgId);
                return new byte[] { 1 };
            }, Capacity, 10));
            var toOwner = Own(new CircularBuffer(name + "_Owner_SharedMemory_MMF"));
            var fromOwner = Own(new CircularBuffer(name + "_Opener_SharedMemory_MMF"));

            // The second packet of a request claims a larger payload than its first packet did
            var message = Pattern(PacketPayload + 1, 7);
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 7, 0, message, 1, 2));
            var malformed = Packet(MessageType.RpcRequest, 7, 0, message, 2, 2);
            SetPayloadSize(malformed, 3 * PacketPayload);
            WritePacket(toOwner, malformed);

            // A valid request after it
            WritePacket(toOwner, Packet(MessageType.RpcRequest, 8, 0, Pattern(10, 8), 1, 1));

            PacketHeader reply;
            var answered = TryReadPacket(fromOwner, 3000, out reply);
            Assert.AreEqual(
                "answered True, a response to 8, handled 8, partial messages 0",
                $"answered {answered}, a {reply.Type} to {reply.ResponseId}, handled {string.Join(",", handled)}, partial messages {owner.PartialRequests}"
                    .Replace("RpcResponse", "response"));
        }

        [TestMethod]
        public void RPC_RestartedClient_DoesNotGetItsPredecessorsLateReply()
        {
            using (var oldRequestArrived = new ManualResetEventSlim())
            using (var releaseOldReply = new ManualResetEventSlim())
            {
                RpcBuffer owner = null;
                owner = Own(new RpcBuffer(name, (msgId, payload) =>
                {
                    if (payload[0] == 0xAA)
                    {
                        oldRequestArrived.Set();
                        releaseOldReply.Wait(5000);
                        return new byte[] { 0xAA };
                    }

                    // The new client's request: reply only once the reply to the old client has been sent
                    releaseOldReply.Set();
                    WaitUntil(() => owner.Statistics.ResponsesSent >= 1, 5000);
                    return new byte[] { 0xBB };
                }, bufferCapacity: Capacity));

                // The old client sends a request and goes away while the owner works on it
                var oldClient = new RpcBuffer(name);
                oldClient.RemoteRequestAsync(new byte[] { 0xAA }, 10000);
                Assert.IsTrue(oldRequestArrived.Wait(5000), "the old client's request did not arrive");
                oldClient.Dispose();
                Assert.IsTrue(WaitUntil(() => oldClient.DisposeFinished, 5000), "the old client did not finish disposing");

                // A new client on the same name
                var newClient = Own(new RpcBuffer(name));
                var response = newClient.RemoteRequest(new byte[] { 0xBB }, 5000);

                Assert.AreEqual(
                    "success True, reply BB, discarded responses 1",
                    $"success {response.Success}, reply {(response.Data == null ? "none" : BitConverter.ToString(response.Data))}, discarded responses {newClient.Statistics.DiscardedResponses}");
            }
        }

        [TestMethod]
        public void RPC_Dispose_CompletesAPendingRequestAtOnce()
        {
            // No peer: the request waits for a response that does not come
            var owner = Own(new RpcBuffer(name, bufferCapacity: Capacity));
            var pending = owner.RemoteRequestAsync(new byte[] { 1 }, 10000);
            Assert.IsTrue(WaitUntil(() => owner.Statistics.RequestsSent == 1, 2000), "the request was not sent");

            var watch = new Stopwatch();
            var completedAfter = pending.ContinueWith(t => watch.ElapsedMilliseconds, TaskContinuationOptions.ExecuteSynchronously);
            watch.Start();
            owner.Dispose();
            var finished = owner.DisposeFinished;

            var completed = completedAfter.Wait(2000);
            Assert.AreEqual(
                "finished True, completed True, success False, within 100 ms True",
                $"finished {finished}, completed {completed}, success {(completed ? pending.Result.Success.ToString() : "unknown")}, within 100 ms {completed && completedAfter.Result < 100}",
                completed ? $"completed {completedAfter.Result} ms after Dispose began" : "");
        }

        [TestMethod]
        public void RPC_Dispose_EndsARequestStillWaitingForRoom()
        {
            // Two nodes leave room for one packet, and no peer reads it, so the second of three packets waits for room
            var owner = Own(new RpcBuffer(name, bufferCapacity: Capacity, bufferNodeCount: 2));
            var watch = new Stopwatch();
            var sending = Task.Run(() =>
            {
                var response = owner.RemoteRequest(Pattern(2 * PacketPayload + 1, 1), timeoutMs: 3000);
                return Tuple.Create(response.Success, watch.ElapsedMilliseconds);
            });
            Assert.IsTrue(WaitUntil(() => owner.Statistics.PacketsWritten >= 1, 2000), "the request did not start");
            Thread.Sleep(100);

            watch.Start();
            owner.Dispose();
            var finished = owner.DisposeFinished;

            var returned = sending.Wait(5000);
            Assert.AreEqual(
                "finished True, returned True, success False, within 500 ms True",
                $"finished {finished}, returned {returned}, success {(returned ? sending.Result.Item1.ToString() : "unknown")}, within 500 ms {returned && sending.Result.Item2 < 500}",
                returned ? $"returned {sending.Result.Item2} ms after Dispose began" : "");
        }

        [TestMethod]
        public void RPC_Dispose_HasFinishedWhenItReturns()
        {
            var owner = Own(new RpcBuffer(name, (msgId, payload) => new byte[] { 1 }, bufferCapacity: Capacity));
            var opener = Own(new RpcBuffer(name));
            Assert.IsTrue(opener.RemoteRequest(new byte[] { 1 }, 2000).Success, "the channel did not work");

            opener.Dispose();
            owner.Dispose();
            var finished = $"{owner.DisposeFinished}, {opener.DisposeFinished}";

            // Straight away, the name is free for a new owner
            Assert.AreEqual("finished True, True, next reply 2", $"finished {finished}, next {RoundTrip(name)}");
        }

        [TestMethod]
        public void RPC_OwnerConstructorThatFails_LeavesTheNameFree()
        {
            var outcomes = new List<string>();
            foreach (var ring in new[] { "_Opener_SharedMemory_MMF", "_Owner_SharedMemory_MMF" })
            {
                var channel = name + ring.Substring(0, 7);
                string failed;

                // Another holder of one of the ring names makes creating that ring fail
                using (new CircularBuffer(channel + ring, 2, Capacity))
                {
                    failed = Throws(() => new RpcBuffer(channel, bufferCapacity: Capacity));
                }

                // Straight away, with no garbage collection, a new instance becomes the owner
                outcomes.Add($"{ring}: threw {failed}, next {RoundTrip(channel)}");
            }

            Assert.AreEqual(
                "_Opener_SharedMemory_MMF: threw IOException, next reply 2; _Owner_SharedMemory_MMF: threw IOException, next reply 2",
                string.Join("; ", outcomes));
        }

        [TestMethod]
        public void RPC_OpenerConstructorThatFails_LeavesNoRingOpen()
        {
            string failed;

            // An owner that holds the role and has created only the first ring an opener opens
            bool createdNew;
            using (var role = CreateOwnerMutex(name + "SharedMemory_OwnerMutex", out createdNew))
            {
                Assert.IsTrue(createdNew, "the owner mutex already existed");
                using (new CircularBuffer(name + "_Opener_SharedMemory_MMF", 2, Capacity))
                {
                    failed = Throws(() => new RpcBuffer(name));
                }
                role.ReleaseMutex();
            }

            // An owner started afterwards, with no garbage collection, can create the rings
            Assert.AreEqual("threw FileNotFoundException, next reply 2", $"threw {failed}, next {RoundTrip(name)}");
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

        /// <summary>
        /// The owner's mutex, taken: on Windows as RpcBuffer takes it, and on Linux and macOS with the name and options of the
        /// net10.0 build's election mutex there (RpcBuffer.CreateUnixElectionMutex), so that an RpcBuffer finds the role held.
        /// </summary>
        static Mutex CreateOwnerMutex(string mutexName, out bool createdNew)
        {
#if NET
            if (!OperatingSystem.IsWindows())
                return new Mutex(true, OperatingSystem.IsMacOS() ? UnixFiles.ElectionMutexName(mutexName) : mutexName,
                    new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out createdNew);
#endif
            return new Mutex(true, mutexName, out createdNew);
        }

        static string Throws(Action action)
        {
            try
            {
                action();
                return "nothing";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        T Own<T>(T item) where T : IDisposable
        {
            owned.Add(item);
            return item;
        }

        static byte[] Pattern(int length, int seed)
        {
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
        /// Overrides SendMessage, and records where its code after the base call runs for a request.
        /// </summary>
        class SendMessageProbe : RpcBuffer
        {
            public SendMessageProbe(string name, int bufferCapacity)
                : base(name, bufferCapacity)
            {
            }

            public ManualResetEventSlim Attached { get; } = new ManualResetEventSlim();

            public bool Continued { get; private set; }

            public bool OnPool { get; private set; }

            protected override Task<RpcResponse> SendMessage(MessageType msgType, RpcRequest request, byte[] payload, ulong responseMsgId = 0, int timeout = 30000, CancellationToken cancellationToken = default(CancellationToken))
            {
                var sent = base.SendMessage(msgType, request, payload, responseMsgId, timeout, cancellationToken);
                if (msgType != MessageType.RpcRequest)
                {
                    return sent;
                }

                // As the code after an await does when the response comes later
                var continued = sent.ContinueWith(t =>
                {
                    OnPool = Thread.CurrentThread.IsThreadPoolThread;
                    Continued = true;
                    return t.Result;
                }, TaskContinuationOptions.ExecuteSynchronously);
                Attached.Set();
                return continued;
            }
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
