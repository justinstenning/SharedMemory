using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// What a <see cref="CircularBuffer"/> read or write that fails leaves in the ring.
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
    }
}
