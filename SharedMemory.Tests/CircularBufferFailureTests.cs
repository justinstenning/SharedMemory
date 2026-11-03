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
        /// A Write(Func) whose callback throws posts its node empty, as every 2.x release has: the next read returns 0 for it, at
        /// once, and the read after that gets the message written after it.
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
                Assert.AreEqual(0, ring.Read(destination, timeout: 1000), "The read after the failed write");
                Assert.IsTrue(timer.ElapsedMilliseconds < 500, String.Format("The read of the failed write's node took {0} ms", timer.ElapsedMilliseconds));

                Assert.AreEqual(message.Length, ring.Read(destination, timeout: 0), "The read after that");
                CollectionAssert.AreEqual(message, destination.Take(message.Length).ToArray());
            }
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
