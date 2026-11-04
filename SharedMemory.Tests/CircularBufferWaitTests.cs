using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// How <see cref="CircularBuffer"/> reads and writes wait. A post or a return sets the buffer's event only for a waiter at the
    /// boundary, and one set wakes one waiter, so a waiter looks at the ring again after every 10 ms slice (a timer tick, about 15.6
    /// ms, on Windows), and once more at the end of its timeout, which is for the whole call.
    /// </summary>
    [TestClass]
    public class CircularBufferWaitTests
    {
        const int Tries = 20;
        const int NodeSize = 32;
        // How long the waiters in the two-waiter tests wait: far longer than they need, when they look at the ring while they wait
        const int WaitTimeout = 1000;
        // A 100 ms call's limit: its timeout, one slice (up to a 15.6 ms timer tick on Windows), and 60 ms for a slow machine's scheduling
        const int TimedCallLimit = 100 + 16 + 60;
        // How soon a call returns once the owner has shut down or its own instance has closed: one slice (up to a 15.6 ms timer tick
        // on Windows), and the rest for a slow machine's scheduling. The calls' timeouts are far longer.
        const int PromptLimit = 250;
        const int LongTimeout = 5000;

        /// <summary>
        /// An owner that makes two items readable, or two nodes free, in one walk of the ring. The walk sets the event once, which
        /// wakes one waiter, so a second waiter has to find its item or node without being woken.
        /// </summary>
        unsafe class TwoAtOnce : CircularBuffer
        {
            public TwoAtOnce(string name, int nodeCount)
                : base(name, nodeCount, NodeSize)
            {
            }

            public void WriteTwo(byte[] first, byte[] second)
            {
                Node* a = GetNodeForWriting(0);
                Node* b = GetNodeForWriting(0);
                Assert.IsTrue(a != null && b != null, "The ring should have two free nodes");
                Fill(a, first);
                Fill(b, second);
                // b waits for a, so nothing is readable until a is posted, and then both are
                PostNode(b);
                PostNode(a);
            }

            public void ReadTwo()
            {
                Node* a = GetNodeForReading(0);
                Node* b = GetNodeForReading(0);
                Assert.IsTrue(a != null && b != null, "The ring should hold two items");
                // b waits for a, so neither node is free until a is returned, and then both are
                ReturnNode(b);
                ReturnNode(a);
            }

            void Fill(Node* node, byte[] data)
            {
                Marshal.Copy(data, 0, new IntPtr(BufferStartPtr + node->Offset), data.Length);
                node->AmountWritten = data.Length;
            }
        }

        /// <summary>
        /// Two readers wait on an empty ring, and two items become readable at once: both readers get one, in every try.
        /// </summary>
        [TestMethod]
        public void Read_TwoReadersWaiting_BothGetTheTwoItemsPostedAtOnce()
        {
            var failures = new List<string>();
            var slower = new List<long>();
            for (int i = 0; i < Tries; i++)
            {
                string name = Guid.NewGuid().ToString();
                using (var owner = new TwoAtOnce(name, 4))
                using (var opener = new CircularBuffer(name))
                {
                    var readers = new Task<Outcome<byte[]>>[2];
                    for (int r = 0; r < readers.Length; r++)
                        readers[r] = StartTimed(() =>
                        {
                            var buffer = new byte[NodeSize];
                            return opener.Read(buffer, timeout: WaitTimeout) == NodeSize ? buffer : null;
                        });
                    LetThemWait(readers);

                    owner.WriteTwo(Item(1), Item(2));

                    Outcome<byte[]>[] got = Finish(readers);
                    slower.Add(got.Max(g => g.Milliseconds));
                    if (got.Any(g => g.Value == null))
                        failures.Add(String.Format("try {0}: a reader returned 0 after {1} ms with an item left in the ring", i + 1, got.First(g => g.Value == null).Milliseconds));
                    else if (!got.Select(g => g.Value[0]).OrderBy(b => b).SequenceEqual(new byte[] { 1, 2 }))
                        failures.Add(String.Format("try {0}: the readers got items {1}", i + 1, String.Join(" and ", got.Select(g => g.Value[0]))));
                }
            }

            Console.WriteLine("The slower reader returned after {0} to {1} ms over {2} tries", slower.Min(), slower.Max(), Tries);
            Assert.AreEqual(0, failures.Count, String.Format("{0} of {1} tries failed: {2}", failures.Count, Tries, String.Join("; ", failures)));
        }

        /// <summary>
        /// Two writers wait on a full ring, and two nodes become free at once: both writers write, in every try.
        /// </summary>
        [TestMethod]
        public void Write_TwoWritersWaiting_BothWriteWhenTwoNodesAreFreedAtOnce()
        {
            var failures = new List<string>();
            var slower = new List<long>();
            for (int i = 0; i < Tries; i++)
            {
                string name = Guid.NewGuid().ToString();
                using (var owner = new TwoAtOnce(name, 3))
                using (var opener = new CircularBuffer(name))
                {
                    Assert.AreEqual(NodeSize, owner.Write(Item(1), timeout: 0));
                    Assert.AreEqual(NodeSize, owner.Write(Item(2), timeout: 0));

                    var writers = new Task<Outcome<int>>[2];
                    for (int w = 0; w < writers.Length; w++)
                    {
                        byte item = (byte)(3 + w);
                        writers[w] = StartTimed(() => opener.Write(Item(item), timeout: WaitTimeout));
                    }
                    LetThemWait(writers);

                    owner.ReadTwo();

                    Outcome<int>[] wrote = Finish(writers);
                    slower.Add(wrote.Max(o => o.Milliseconds));
                    if (wrote.Any(o => o.Value != NodeSize))
                    {
                        failures.Add(String.Format("try {0}: a writer returned 0 after {1} ms with a node free", i + 1, wrote.First(o => o.Value != NodeSize).Milliseconds));
                        continue;
                    }
                    var buffer = new byte[NodeSize];
                    var read = new List<byte>();
                    while (owner.Read(buffer, timeout: 0) == NodeSize)
                        read.Add(buffer[0]);
                    if (!read.OrderBy(b => b).SequenceEqual(new byte[] { 3, 4 }))
                        failures.Add(String.Format("try {0}: the ring then held items {1}", i + 1, String.Join(" and ", read)));
                }
            }

            Console.WriteLine("The slower writer returned after {0} to {1} ms over {2} tries", slower.Min(), slower.Max(), Tries);
            Assert.AreEqual(0, failures.Count, String.Format("{0} of {1} tries failed: {2}", failures.Count, Tries, String.Join("; ", failures)));
        }

        /// <summary>
        /// A reader with a 100 ms timeout is woken three times for an item that a reader that does not wait takes first. It returns
        /// within its timeout and a slice, in every try.
        /// </summary>
        [TestMethod]
        public void Read_Timeout100WithACompetingReader_ReturnsWithinTheTimeoutAndASlice()
        {
            var failures = new List<string>();
            var took = new List<long>();
            for (int i = 0; i < Tries; i++)
            {
                string name = Guid.NewGuid().ToString();
                using (var owner = new CircularBuffer(name, 4, NodeSize))
                using (var opener = new CircularBuffer(name))
                {
                    Task<Outcome<int>> waiter = StartTimed(() => opener.Read(new byte[NodeSize], timeout: 100));
                    var sinceStart = Stopwatch.StartNew();
                    foreach (int at in new[] { 30, 60, 90 })
                    {
                        SleepUntil(sinceStart, at);
                        // An item that wakes the waiter, and that this thread then takes at once
                        owner.Write(Item(1), timeout: 0);
                        owner.Read(new byte[NodeSize], timeout: 0);
                    }

                    Outcome<int> outcome = Finish(waiter);
                    took.Add(outcome.Milliseconds);
                    if (outcome.Milliseconds > TimedCallLimit)
                        failures.Add(String.Format("try {0}: Read(timeout: 100) returned {1} after {2} ms", i + 1, outcome.Value, outcome.Milliseconds));
                }
            }

            Console.WriteLine("{0} tries took {1} to {2} ms", Tries, took.Min(), took.Max());
            Assert.AreEqual(0, failures.Count, String.Format("{0} of {1} tries failed: {2}", failures.Count, Tries, String.Join("; ", failures)));
        }

        /// <summary>
        /// A writer with a 100 ms timeout on a full ring is woken three times for a node that a writer that does not wait fills first.
        /// It returns within its timeout and a slice, in every try.
        /// </summary>
        [TestMethod]
        public void Write_Timeout100WithACompetingWriter_ReturnsWithinTheTimeoutAndASlice()
        {
            var failures = new List<string>();
            var took = new List<long>();
            for (int i = 0; i < Tries; i++)
            {
                string name = Guid.NewGuid().ToString();
                using (var owner = new CircularBuffer(name, 2, NodeSize))
                using (var opener = new CircularBuffer(name))
                {
                    Assert.AreEqual(NodeSize, owner.Write(Item(1), timeout: 0));

                    Task<Outcome<int>> waiter = StartTimed(() => opener.Write(Item(2), timeout: 100));
                    var sinceStart = Stopwatch.StartNew();
                    foreach (int at in new[] { 30, 60, 90 })
                    {
                        SleepUntil(sinceStart, at);
                        // A free node that wakes the waiter, and that this thread then fills at once
                        owner.Read(new byte[NodeSize], timeout: 0);
                        owner.Write(Item(1), timeout: 0);
                    }

                    Outcome<int> outcome = Finish(waiter);
                    took.Add(outcome.Milliseconds);
                    if (outcome.Milliseconds > TimedCallLimit)
                        failures.Add(String.Format("try {0}: Write(timeout: 100) returned {1} after {2} ms", i + 1, outcome.Value, outcome.Milliseconds));
                }
            }

            Console.WriteLine("{0} tries took {1} to {2} ms", Tries, took.Min(), took.Max());
            Assert.AreEqual(0, failures.Count, String.Format("{0} of {1} tries failed: {2}", failures.Count, Tries, String.Join("; ", failures)));
        }

        [TestMethod]
        public void Read_InfiniteTimeout_ReturnsAnItemWrittenTwoSecondsLater()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, 4, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                Task<Outcome<byte[]>> reader = StartTimed(() =>
                {
                    var buffer = new byte[NodeSize];
                    return opener.Read(buffer, timeout: Timeout.Infinite) == NodeSize ? buffer : null;
                });

                Thread.Sleep(2000);
                Assert.IsFalse(reader.IsCompleted, "The reader should still be waiting");
                Assert.AreEqual(NodeSize, owner.Write(Item(7), timeout: 0));

                Assert.IsTrue(reader.Wait(5000), "The reader should return the item");
                CollectionAssert.AreEqual(Item(7), reader.Result.Value);
            }
        }

        /// <summary>
        /// A timeout of 0 looks at the ring once and does not wait: 22 calls take far less than one slice each.
        /// </summary>
        [TestMethod]
        public void ReadAndWrite_Timeout0_LookOnceWithoutWaiting()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, 2, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                var buffer = new byte[NodeSize];
                var timer = Stopwatch.StartNew();

                for (int i = 0; i < 10; i++)
                    Assert.AreEqual(0, opener.Read(buffer, timeout: 0), "A read of an empty ring");
                Assert.AreEqual(NodeSize, owner.Write(Item(5), timeout: 0));
                for (int i = 0; i < 10; i++)
                    Assert.AreEqual(0, owner.Write(Item(6), timeout: 0), "A write to a full ring");
                Assert.AreEqual(NodeSize, opener.Read(buffer, timeout: 0));
                timer.Stop();

                CollectionAssert.AreEqual(Item(5), buffer);
                Assert.IsTrue(timer.ElapsedMilliseconds < 100, String.Format("22 calls with timeout 0 took {0} ms", timer.ElapsedMilliseconds));
            }
        }

        /// <summary>
        /// The owner shuts down with two items in the ring: an opener still reads both, and then a read returns 0 at once rather
        /// than wait out its timeout.
        /// </summary>
        [TestMethod]
        public void Read_OwnerShutDownWithTwoItemsLeft_ReadsBothThenReturns0AtOnce()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, 4, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                Assert.AreEqual(NodeSize, owner.Write(Item(1), timeout: 0));
                Assert.AreEqual(NodeSize, owner.Write(Item(2), timeout: 0));
                owner.Dispose();
                Assert.IsTrue(opener.ShuttingDown);

                var buffer = new byte[NodeSize];
                Assert.AreEqual(NodeSize, opener.Read(buffer));
                CollectionAssert.AreEqual(Item(1), buffer);
                Assert.AreEqual(NodeSize, opener.Read(buffer));
                CollectionAssert.AreEqual(Item(2), buffer);

                var timer = Stopwatch.StartNew();
                Assert.AreEqual(0, opener.Read(buffer, timeout: LongTimeout));
                Console.WriteLine("Read of the empty ring returned after {0} ms", timer.ElapsedMilliseconds);
                Assert.IsTrue(timer.ElapsedMilliseconds < PromptLimit, String.Format("Read(timeout: {0}) of an empty ring after the owner shut down took {1} ms", LongTimeout, timer.ElapsedMilliseconds));
            }
        }

        /// <summary>
        /// After the owner has shut down, an opener still writes while there is room, and then a write returns 0 at once; what it
        /// wrote can still be read.
        /// </summary>
        [TestMethod]
        public void Write_AfterTheOwnerShutDown_WritesWhileThereIsRoomThenReturns0AtOnce()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, 3, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                owner.Dispose();
                Assert.IsTrue(opener.ShuttingDown);

                Assert.AreEqual(NodeSize, opener.Write(Item(1)));
                Assert.AreEqual(NodeSize, opener.Write(Item(2)));
                var timer = Stopwatch.StartNew();
                Assert.AreEqual(0, opener.Write(Item(3), timeout: LongTimeout));
                Console.WriteLine("Write to the full ring returned after {0} ms", timer.ElapsedMilliseconds);
                Assert.IsTrue(timer.ElapsedMilliseconds < PromptLimit, String.Format("Write(timeout: {0}) to a full ring after the owner shut down took {1} ms", LongTimeout, timer.ElapsedMilliseconds));

                var buffer = new byte[NodeSize];
                Assert.AreEqual(NodeSize, opener.Read(buffer));
                CollectionAssert.AreEqual(Item(1), buffer);
                Assert.AreEqual(NodeSize, opener.Read(buffer));
                CollectionAssert.AreEqual(Item(2), buffer);
            }
        }

        /// <summary>
        /// A read waiting on an empty ring, or a write waiting on a full one, when the owner shuts down: it returns 0 promptly rather
        /// than wait out its timeout.
        /// </summary>
        [TestMethod]
        [DataRow("Read")]
        [DataRow("Write")]
        public void Call_WaitingWhenTheOwnerShutsDown_Returns0Promptly(string call)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, 2, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                if (call == "Write")
                    Assert.AreEqual(NodeSize, owner.Write(Item(1), timeout: 0));
                Task<Outcome<int>> waiter = StartTimed(call == "Read"
                    ? (Func<int>)(() => opener.Read(new byte[NodeSize], timeout: LongTimeout))
                    : () => opener.Write(Item(2), timeout: LongTimeout));
                LetThemWait(new Task[] { waiter });

                long shutDown = Stopwatch.GetTimestamp();
                owner.Dispose();

                Outcome<int> outcome = Finish(waiter);
                long afterShutdown = MillisecondsBetween(shutDown, outcome.Returned);
                Console.WriteLine("{0} returned {1} {2} ms after the owner shut down", call, outcome.Value, afterShutdown);
                Assert.AreEqual(0, outcome.Value);
                Assert.IsTrue(afterShutdown < PromptLimit, String.Format("{0}(timeout: {1}) returned {2} ms after the owner shut down", call, LongTimeout, afterShutdown));
                Assert.IsTrue(opener.ShuttingDown);
            }
        }

        /// <summary>
        /// A read or write waiting when its own instance is closed, on another thread, returns 0; a call after that throws
        /// ObjectDisposedException. The instance is the opener, or the owner with no timeout.
        /// </summary>
        [TestMethod]
        [DataRow("Read", false, LongTimeout)]
        [DataRow("Write", false, LongTimeout)]
        [DataRow("Read", true, Timeout.Infinite)]
        [DataRow("Write", true, Timeout.Infinite)]
        public void Call_WaitingWhenItsOwnInstanceCloses_Returns0ThenThrowsObjectDisposedException(string call, bool onTheOwner, int timeout)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, 2, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                CircularBuffer instance = onTheOwner ? owner : opener;
                if (call == "Write")
                    Assert.AreEqual(NodeSize, owner.Write(Item(1), timeout: 0));
                Func<int> waiting = call == "Read"
                    ? (Func<int>)(() => instance.Read(new byte[NodeSize], timeout: timeout))
                    : () => instance.Write(Item(2), timeout: timeout);
                Task<Outcome<int>> waiter = StartTimed(waiting);
                LetThemWait(new Task[] { waiter });

                long closed = Stopwatch.GetTimestamp();
                instance.Close();

                Outcome<int> outcome = Finish(waiter);
                long afterClose = MillisecondsBetween(closed, outcome.Returned);
                Console.WriteLine("{0} returned {1} {2} ms after its instance closed", call, outcome.Value, afterClose);
                Assert.AreEqual(0, outcome.Value);
                Assert.IsTrue(afterClose < PromptLimit, String.Format("{0} returned {1} ms after its instance closed", call, afterClose));
                var thrown = Assert.ThrowsExactly<ObjectDisposedException>(() => waiting());
                Assert.AreEqual(name, thrown.ObjectName);
            }
        }

        struct Outcome<T>
        {
            public T Value;
            public long Milliseconds;
            // When the call returned, as a Stopwatch timestamp
            public long Returned;
        }

        static byte[] Item(byte value)
        {
            return Enumerable.Repeat(value, NodeSize).ToArray();
        }

        // Runs the call on a thread of its own and times it, returning once that thread is about to make the call, so that a thread
        // slow to start cannot make its call after what the test does next
        static Task<Outcome<T>> StartTimed<T>(Func<T> call)
        {
            var started = new ManualResetEventSlim(false);
            Task<Outcome<T>> task = Task.Factory.StartNew(() =>
            {
                started.Set();
                var timer = Stopwatch.StartNew();
                T value = call();
                return new Outcome<T> { Value = value, Milliseconds = timer.ElapsedMilliseconds, Returned = Stopwatch.GetTimestamp() };
            }, TaskCreationOptions.LongRunning);
            Assert.IsTrue(started.Wait(LongTimeout), "The call's thread did not start");
            return task;
        }

        // Gives calls just started time to find nothing and wait. One that has not yet looked finds what it needs on its first look.
        static void LetThemWait(Task[] calls)
        {
            Thread.Sleep(100);
            Assert.IsFalse(calls.Any(c => c.IsCompleted), "The calls should be waiting");
        }

        static T Finish<T>(Task<T> call)
        {
            Assert.IsTrue(call.Wait(WaitTimeout + 5000), "The call should have returned");
            return call.Result;
        }

        static T[] Finish<T>(Task<T>[] calls)
        {
            return calls.Select(c => Finish(c)).ToArray();
        }

        static long MillisecondsBetween(long from, long to)
        {
            return (to - from) * 1000 / Stopwatch.Frequency;
        }

        static void SleepUntil(Stopwatch sinceStart, int milliseconds)
        {
            long left = milliseconds - sinceStart.ElapsedMilliseconds;
            if (left > 0)
                Thread.Sleep((int)left);
        }
    }
}
