using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// What a <see cref="CircularBuffer"/> read or write that fails leaves in the ring. A node left reserved would stop every process
    /// that uses the ring, so after each failure this process and a second one write and read round the ring.
    /// </summary>
    [TestClass]
    public class CircularBufferFailureTests
    {
        const int NodeCount = 4;
        const int NodeSize = 32;
        const int LongTimeout = 5000;
        // How soon a read gets an item already in the ring, with room for a slow machine's scheduling
        const int PromptLimit = 250;
        // A 100 ms call's limit: its timeout, one slice (up to a 15.6 ms timer tick on Windows), and 60 ms for a slow machine's scheduling
        const int TimedCallLimit = 100 + 16 + 60;

        class CallbackException : Exception
        {
            public CallbackException() : base("The callback failed.") { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LargerThanANode
        {
            public long A, B, C, D, E;
        }

        // Memory for the pointer overloads, which a bad length keeps them from touching
        static readonly IntPtr Somewhere = Marshal.AllocHGlobal(NodeSize);

        // What a bad argument to each overload throws, and the call
        static readonly Dictionary<string, KeyValuePair<Type, Action<CircularBuffer>>> BadCalls = new Dictionary<string, KeyValuePair<Type, Action<CircularBuffer>>>
        {
            { "Write byte[] null", Bad<ArgumentNullException>(ring => ring.Write((byte[])null)) },
            { "Write byte[] startIndex past the end", Bad<ArgumentOutOfRangeException>(ring => ring.Write(new byte[10], startIndex: 20)) },
            { "Write byte[] negative startIndex", Bad<ArgumentOutOfRangeException>(ring => ring.Write(new byte[10], startIndex: -1)) },
            { "Write T[] null", Bad<ArgumentNullException>(ring => ring.Write((int[])null)) },
            { "Write T[] startIndex past the end", Bad<ArgumentOutOfRangeException>(ring => ring.Write(new int[4], startIndex: 5)) },
            { "Write T[] negative startIndex", Bad<ArgumentOutOfRangeException>(ring => ring.Write(new int[4], startIndex: -1)) },
            { "Write ref T larger than a node", Bad<ArgumentOutOfRangeException>(ring => { var value = new LargerThanANode(); ring.Write(ref value); }) },
            { "Write IntPtr negative length", Bad<ArgumentOutOfRangeException>(ring => ring.Write(Somewhere, -1)) },
            { "Write Func null", Bad<ArgumentNullException>(ring => ring.Write((Func<IntPtr, int>)null)) },
            { "Read byte[] null", Bad<ArgumentNullException>(ring => ring.Read((byte[])null)) },
            { "Read byte[] startIndex past the end", Bad<ArgumentOutOfRangeException>(ring => ring.Read(new byte[10], startIndex: 20)) },
            { "Read byte[] negative startIndex", Bad<ArgumentOutOfRangeException>(ring => ring.Read(new byte[10], startIndex: -1)) },
            { "Read T[] null", Bad<ArgumentNullException>(ring => ring.Read((int[])null)) },
            { "Read T[] startIndex past the end", Bad<ArgumentOutOfRangeException>(ring => ring.Read(new int[4], startIndex: 5)) },
            { "Read T[] negative startIndex", Bad<ArgumentOutOfRangeException>(ring => ring.Read(new int[4], startIndex: -1)) },
            { "Read out T larger than a node", Bad<ArgumentOutOfRangeException>(ring => { LargerThanANode value; ring.Read(out value); }) },
            { "Read IntPtr negative length", Bad<ArgumentOutOfRangeException>(ring => ring.Read(Somewhere, -1)) },
            { "Read Func null", Bad<ArgumentNullException>(ring => ring.Read((Func<IntPtr, int>)null)) },
        };

        static KeyValuePair<Type, Action<CircularBuffer>> Bad<TException>(Action<CircularBuffer> call)
            where TException : Exception
        {
            return new KeyValuePair<Type, Action<CircularBuffer>>(typeof(TException), call);
        }

        /// <summary>
        /// A bad argument to any overload throws before the call takes a node, so the ring is as it was: an item already in it is
        /// still there to read, and this process and a second one then write and read round the ring. The two structure overloads
        /// checked their size first before too.
        /// </summary>
        [TestMethod]
        [DataRow("Write byte[] null")]
        [DataRow("Write byte[] startIndex past the end")]
        [DataRow("Write byte[] negative startIndex")]
        [DataRow("Write T[] null")]
        [DataRow("Write T[] startIndex past the end")]
        [DataRow("Write T[] negative startIndex")]
        [DataRow("Write ref T larger than a node")]
        [DataRow("Write IntPtr negative length")]
        [DataRow("Write Func null")]
        [DataRow("Read byte[] null")]
        [DataRow("Read byte[] startIndex past the end")]
        [DataRow("Read byte[] negative startIndex")]
        [DataRow("Read T[] null")]
        [DataRow("Read T[] startIndex past the end")]
        [DataRow("Read T[] negative startIndex")]
        [DataRow("Read out T larger than a node")]
        [DataRow("Read IntPtr negative length")]
        [DataRow("Read Func null")]
        public void Call_BadArgument_ThrowsAndLeavesTheRingWorkingForTwoProcesses(string call)
        {
            string name = Guid.NewGuid().ToString();
            byte[] queued = { 9, 8, 7, 6 };
            var problems = new List<string>();
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                // An item to read, so that a read would take a node
                Assert.AreEqual(queued.Length, ring.Write(queued));
                string before = Describe(ring.ReadNodeHeader());

                Part(problems, "the exception", () => AssertThrows(BadCalls[call].Key, () => BadCalls[call].Value(ring), call));
                Part(problems, "the indices", () => Assert.AreEqual(before, Describe(ring.ReadNodeHeader())));
                Part(problems, "the item written before", () => AssertReads(ring, queued, "the item written before"));
                AssertRingWorks(ring, name, problems);
            }
            AssertNoProblems(problems, "After " + call);
        }

        /// <summary>
        /// A Write(Func) whose callback throws: the callback's exception reaches the caller, the node is posted, with no data a reader
        /// gets, and this process and a second one then write and read round the ring.
        /// </summary>
        [TestMethod]
        public void Write_CallbackThrows_ExceptionReachesTheCallerAndTheRingWorksForTwoProcesses()
        {
            string name = Guid.NewGuid().ToString();
            var problems = new List<string>();
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.ThrowsExactly<CallbackException>(() => ring.Write(p => { Marshal.WriteInt32(p, 5); throw new CallbackException(); }));

                CircularBuffer.NodeHeader header = ring.ReadNodeHeader();
                Assert.AreEqual(header.WriteStart, header.WriteEnd, "The failed write's node is still reserved: " + Describe(header));
                Assert.AreEqual(0, ring.Read(new byte[NodeSize], timeout: 100), "A read got data from the failed write");
                AssertRingWorks(ring, name, problems);
            }
            AssertNoProblems(problems, "After the failed write");
        }

        /// <summary>
        /// A Read(Func) whose callback throws: the callback's exception reaches the caller, the node is returned for writing, and this
        /// process and a second one then write and read round the ring.
        /// </summary>
        [TestMethod]
        public void Read_CallbackThrows_ExceptionReachesTheCallerAndTheRingWorksForTwoProcesses()
        {
            string name = Guid.NewGuid().ToString();
            var problems = new List<string>();
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.AreEqual(4, ring.Write(new byte[] { 1, 2, 3, 4 }));

                Assert.ThrowsExactly<CallbackException>(() => ring.Read(p => { throw new CallbackException(); }));

                CircularBuffer.NodeHeader header = ring.ReadNodeHeader();
                Assert.AreEqual(header.ReadStart, header.ReadEnd, "The failed read's node is still reserved: " + Describe(header));
                AssertRingWorks(ring, name, problems);
            }
            AssertNoProblems(problems, "After the failed read");
        }

        /// <summary>
        /// A Write(Func) whose callback throws posts its node empty, and a reader skips it: the next read gets the message written
        /// after it, within its timeout, and nothing is left after that. A Read(byte[]) before 2.4.0 returns 0 for the failed write's node.
        /// </summary>
        [TestMethod]
        public void Read_AfterAWriteFuncCallbackThrew_ReturnsNoDataFromTheFailedWrite()
        {
            string name = Guid.NewGuid().ToString();
            byte[] message = { 1, 2, 3, 4 };
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.ThrowsExactly<CallbackException>(() => ring.Write(p => { throw new CallbackException(); }));
                Assert.AreEqual(message.Length, ring.Write(message));

                var destination = new byte[NodeSize];
                var timer = Stopwatch.StartNew();
                Assert.AreEqual(message.Length, ring.Read(destination, timeout: 1000), "The read after the failed write");
                Assert.IsTrue(timer.ElapsedMilliseconds < 500, String.Format("The read past the failed write's node took {0} ms", timer.ElapsedMilliseconds));
                CollectionAssert.AreEqual(message, destination.Take(message.Length).ToArray());

                Assert.AreEqual(0, ring.Read(destination, timeout: 0), "The read after that");
            }
        }

        /// <summary>
        /// BufferAndRingSafety FR-009: after a write whose callback threw and a good one, each read overload gets the good one, at once,
        /// and a Read(Func) callback runs once, for it.
        /// </summary>
        [TestMethod]
        [DataRow("byte[]")]
        [DataRow("T[]")]
        [DataRow("out T")]
        [DataRow("IntPtr")]
        [DataRow("Func")]
        public void Read_AfterAFailedWriteAndAGoodOne_GetsTheGoodOneAtOnce(string overload)
        {
            string name = Guid.NewGuid().ToString();
            byte[] message = { 1, 2, 3, 4, 5, 6, 7, 8 };
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.ThrowsExactly<CallbackException>(() => ring.Write(p => { throw new CallbackException(); }));
                Assert.AreEqual(message.Length, ring.Write(message));

                var timer = Stopwatch.StartNew();
                byte[] read = ReadWith(overload, ring, message.Length, LongTimeout);
                timer.Stop();

                CollectionAssert.AreEqual(message, read, overload + " did not get the good write");
                Assert.IsTrue(timer.ElapsedMilliseconds < PromptLimit, String.Format("{0} took {1} ms", overload, timer.ElapsedMilliseconds));
                Assert.AreEqual(0, ring.Read(new byte[NodeSize], timeout: 0), "Something was left in the ring");
            }
        }

        /// <summary>
        /// A write of nothing still posts a node, and a reader skips it and gets the message after it.
        /// </summary>
        [TestMethod]
        [DataRow("byte[]")]
        [DataRow("T[]")]
        [DataRow("IntPtr")]
        [DataRow("Func")]
        public void Read_AfterAnEmptyWrite_SkipsItAndGetsTheNextMessage(string overload)
        {
            string name = Guid.NewGuid().ToString();
            byte[] message = { 1, 2, 3, 4 };
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.AreEqual(0, WriteNothing(overload, ring), "The empty write");
                Assert.AreEqual(1, Queued(ring), "The empty write did not post a node");
                Assert.AreEqual(message.Length, ring.Write(message));

                var destination = new byte[NodeSize];
                Assert.AreEqual(message.Length, ring.Read(destination, timeout: LongTimeout), "The read after the empty write");
                CollectionAssert.AreEqual(message, destination.Take(message.Length).ToArray());
            }
        }

        /// <summary>
        /// A read with a timeout of 0 skips an empty node and takes the item behind it, without waiting.
        /// </summary>
        [TestMethod]
        public void Read_Timeout0AfterAnEmptyWrite_GetsTheItemBehindIt()
        {
            string name = Guid.NewGuid().ToString();
            byte[] message = { 1, 2, 3, 4 };
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.AreEqual(0, ring.Write(new byte[0]));
                Assert.AreEqual(message.Length, ring.Write(message));

                var destination = new byte[NodeSize];
                Assert.AreEqual(message.Length, ring.Read(destination, timeout: 0));
                CollectionAssert.AreEqual(message, destination.Take(message.Length).ToArray());
            }
        }

        /// <summary>
        /// While a writer posts nothing but empty writes, a read skips each one and returns 0 at its timeout: the skips neither end
        /// the read early nor hold it past its timeout.
        /// </summary>
        [TestMethod]
        public void Read_WhileOnlyEmptyWritesArrive_SkipsThemUntilItsTimeout()
        {
            string name = Guid.NewGuid().ToString();
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                bool stop = false;
                var writer = new System.Threading.Thread(() =>
                {
                    while (!System.Threading.Volatile.Read(ref stop))
                        opener.Write(new byte[0], timeout: 0);
                });
                writer.IsBackground = true;
                writer.Start();
                try
                {
                    // Let the writer fill the ring with empty writes
                    var filling = Stopwatch.StartNew();
                    while (Queued(ring) == 0 && filling.ElapsedMilliseconds < LongTimeout)
                        System.Threading.Thread.Sleep(1);
                    Assert.AreNotEqual(0, Queued(ring), "The writer posted no empty write");

                    var timer = Stopwatch.StartNew();
                    int read = ring.Read(new byte[NodeSize], timeout: 100);
                    timer.Stop();

                    Console.WriteLine("Read(timeout: 100) returned {0} after {1} ms", read, timer.ElapsedMilliseconds);
                    Assert.AreEqual(0, read);
                    Assert.IsTrue(timer.ElapsedMilliseconds >= 90, String.Format("The read returned after {0} ms, before its timeout", timer.ElapsedMilliseconds));
                    Assert.IsTrue(timer.ElapsedMilliseconds < TimedCallLimit, String.Format("The read returned after {0} ms", timer.ElapsedMilliseconds));
                }
                finally
                {
                    System.Threading.Volatile.Write(ref stop, true);
                    Assert.IsTrue(writer.Join(LongTimeout), "The writer did not stop");
                }
            }
        }

        /// <summary>
        /// A reader in a second process skips the node of a write here whose callback threw, and gets the message after it.
        /// </summary>
        [TestMethod]
        public void Read_InASecondProcessAfterAFailedWrite_GetsTheGoodOne()
        {
            string name = Guid.NewGuid().ToString();
            byte[] message = { 1, 2, 3, 4 };
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("open circular " + name);
                Assert.ThrowsExactly<CallbackException>(() => ring.Write(p => { throw new CallbackException(); }));
                Assert.AreEqual(message.Length, ring.Write(message));

                Assert.AreEqual(PeerProtocol.ToHex(message), peer.Ok("read " + NodeSize + " 1000"));
                peer.Exit();
            }
        }

        // How many posted items the ring holds
        static int Queued(CircularBuffer ring)
        {
            CircularBuffer.NodeHeader header = ring.ReadNodeHeader();
            return (header.WriteEnd - header.ReadStart + NodeCount) % NodeCount;
        }

        static unsafe int WriteNothing(string overload, CircularBuffer ring)
        {
            switch (overload)
            {
                case "byte[]": return ring.Write(new byte[0]);
                case "T[]": return ring.Write(new int[0]);
                case "IntPtr": return ring.Write(Somewhere, 0);
                case "Func": return ring.Write(p => 0);
                default: throw new ArgumentException("Unknown overload: " + overload);
            }
        }

        /// <summary>
        /// Reads one item with the overload named, into a destination of a whole node.
        /// </summary>
        /// <returns>The bytes read, <paramref name="length"/> of them for the structure and callback overloads.</returns>
        static unsafe byte[] ReadWith(string overload, CircularBuffer ring, int length, int timeout)
        {
            var destination = new byte[NodeSize];
            int bytes;
            switch (overload)
            {
                case "byte[]":
                    bytes = ring.Read(destination, timeout: timeout);
                    break;
                case "T[]":
                    {
                        var longs = new long[NodeSize / 8];
                        bytes = ring.Read(longs, timeout: timeout) * 8;
                        Buffer.BlockCopy(longs, 0, destination, 0, NodeSize);
                        break;
                    }
                case "out T":
                    {
                        long value;
                        ring.Read(out value, timeout);
                        bytes = length;
                        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, destination, 0, 8);
                        break;
                    }
                case "IntPtr":
                    fixed (byte* p = destination)
                        bytes = ring.Read((IntPtr)p, NodeSize, timeout);
                    break;
                case "Func":
                    {
                        int calls = 0;
                        ring.Read(p => { calls++; Marshal.Copy(p, destination, 0, NodeSize); return NodeSize; }, timeout);
                        Assert.AreEqual(1, calls, "The read callback's calls");
                        bytes = length;
                        break;
                    }
                default:
                    throw new ArgumentException("Unknown overload: " + overload);
            }
            return destination.Take(bytes).ToArray();
        }

        static void AssertThrows(Type expected, Action call, string what)
        {
            try
            {
                call();
            }
            catch (Exception e)
            {
                if (e.GetType() != expected)
                    Assert.Fail(String.Format("{0} threw {1} ({2}), not {3}", what, e.GetType().Name, e.Message, expected.Name));
                return;
            }
            Assert.Fail(what + " did not throw");
        }

        static string Describe(CircularBuffer.NodeHeader header)
        {
            return String.Format("ReadStart {0}, ReadEnd {1}, WriteStart {2}, WriteEnd {3}", header.ReadStart, header.ReadEnd, header.WriteStart, header.WriteEnd);
        }

        static void AssertReads(CircularBuffer ring, byte[] expected, string what)
        {
            var destination = new byte[NodeSize];
            Assert.AreEqual(expected.Length, ring.Read(destination, timeout: 1000), "The read of " + what);
            CollectionAssert.AreEqual(expected, destination.Take(expected.Length).ToArray(), "The bytes of " + what);
        }

        /// <summary>
        /// Runs one part of a test's checks, recording what fails rather than stopping, so that a test reports every part that fails.
        /// </summary>
        static void Part(List<string> problems, string what, Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                string message = String.Join(" ", e.Message.Split('\r', '\n').Select(line => line.Trim()).Where(line => line.Length > 0));
                problems.Add(what + ": " + message);
            }
        }

        static void AssertNoProblems(List<string> problems, string what)
        {
            foreach (string problem in problems)
                Console.WriteLine(problem);
            if (problems.Count > 0)
                Assert.Fail(String.Format("{0}, {1} parts failed: {2}", what, problems.Count, String.Join("; ", problems)));
        }

        /// <summary>
        /// Round the ring and into its first node again: this process writes and reads each item; then a second process opens the
        /// ring, and it and this process take turns, one writing and the other reading. Each part stops at its first failure.
        /// </summary>
        static void AssertRingWorks(CircularBuffer ring, string name, List<string> problems)
        {
            Part(problems, "this process round the ring", () =>
            {
                for (int i = 0; i <= NodeCount; i++)
                {
                    byte[] item = { (byte)(i + 1), 0x10, 0x20, 0x30 };
                    Assert.AreEqual(item.Length, ring.Write(item, timeout: 1000), "write " + (i + 1));
                    AssertReads(ring, item, "item " + (i + 1));
                }
            });

            Part(problems, "a second process round the ring", () =>
            {
                using (var peer = PeerProcess.Start())
                {
                    peer.Ok("open circular " + name);
                    for (int i = 0; i <= NodeCount; i++)
                    {
                        byte[] item = { (byte)(i + 1), 0x40, 0x50, 0x60 };
                        if (i % 2 == 0)
                        {
                            Assert.AreEqual("4", peer.Ok("write " + PeerProtocol.ToHex(item)), "the second process's write " + (i + 1));
                            AssertReads(ring, item, "the second process's item " + (i + 1));
                        }
                        else
                        {
                            Assert.AreEqual(item.Length, ring.Write(item, timeout: 1000), "write " + (i + 1) + " for the second process");
                            Assert.AreEqual(PeerProtocol.ToHex(item), peer.Ok("read " + NodeSize + " 1000"), "the second process's read " + (i + 1));
                        }
                    }
                    peer.Exit();
                }
            });
        }
    }
}
