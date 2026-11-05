using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// Close and Dispose while a call runs, and calls after them, for <see cref="BufferReadWrite"/>, <see cref="SharedArray{T}"/>
    /// and <see cref="CircularBuffer"/>. Close returns at once and the memory stays mapped until the last call returns, so a
    /// callback can go on using its pointer; a read or write that starts after Close throws <see cref="ObjectDisposedException"/>
    /// naming the buffer, at offsets below and above 64 KB; and a subclass can open the buffer again once no call runs.
    /// </summary>
    [TestClass]
    public class DisposeTests
    {
        const string BufferKind = "BufferReadWrite";
        const string ArrayKind = "SharedArray";
        const string RingKind = "CircularBuffer";

        // Above 64 KB a copy through the pointer of a closed buffer used to end the process rather than throw
        const int Size = 128 * 1024;
        static readonly long[] Positions = { 16, 65536 };
        const int Chunk = 4096;
        const int NodeCount = 4;
        const int Repeats = 1000;
        const int WaitMs = 10000;
        const int RepeatsWithinMs = 120000;

        #region Calls after Close or Dispose

        /// <summary>
        /// Every protected read and write of SharedBuffer itself, through a subclass with no events of its own to throw first.
        /// </summary>
        [TestMethod]
        [DataRow("Close")]
        [DataRow("Dispose")]
        public void SharedBuffer_EveryCallAfterClose_ThrowsObjectDisposedExceptionNamingTheBuffer(string how)
        {
            string name = Guid.NewGuid().ToString();
            IntPtr memory = Marshal.AllocHGlobal(Chunk);
            try
            {
                using (var buffer = new EveryCallBuffer(name, Size))
                {
                    End(buffer, how);
                    Assert.IsTrue(buffer.ShuttingDown, "ShuttingDown after " + how);
                    foreach (long position in Positions)
                    {
                        string at = " at " + position + " after " + how;
                        AssertDisposed(name, () => buffer.WriteStruct(position), "Write(ref T)" + at);
                        AssertDisposed(name, () => buffer.WriteElements(position), "Write(T[])" + at);
                        AssertDisposed(name, () => buffer.WriteElementsFrom(position), "Write(T[], index)" + at);
                        AssertDisposed(name, () => buffer.WriteArrayPart(position), "WriteArray" + at);
                        AssertDisposed(name, () => buffer.WritePointer(memory, position), "Write(IntPtr)" + at);
                        AssertDisposed(name, () => buffer.WriteWith(position), "Write(Action)" + at);
                        AssertDisposed(name, () => buffer.ReadStruct(position), "Read(out T)" + at);
                        AssertDisposed(name, () => buffer.ReadElements(position), "Read(T[])" + at);
                        AssertDisposed(name, () => buffer.ReadArrayPart(position), "ReadArray" + at);
                        AssertDisposed(name, () => buffer.ReadPointer(memory, position), "Read(IntPtr)" + at);
                        AssertDisposed(name, () => buffer.ReadWith(position), "Read(Action)" + at);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        [TestMethod]
        [DataRow("Close")]
        [DataRow("Dispose")]
        public void BufferReadWrite_EveryCallAfterClose_ThrowsObjectDisposedExceptionNamingTheBuffer(string how)
        {
            string name = Guid.NewGuid().ToString();
            IntPtr memory = Marshal.AllocHGlobal(Chunk);
            try
            {
                using (var buffer = new BufferReadWrite(name, Size))
                {
                    End(buffer, how);
                    foreach (long position in Positions)
                    {
                        string at = " at " + position + " after " + how;
                        long value = 1;
                        AssertDisposed(name, () => buffer.Write(ref value, position), "Write(ref T)" + at);
                        AssertDisposed(name, () => buffer.Write(new byte[16], position), "Write(T[])" + at);
                        AssertDisposed(name, () => buffer.Write(memory, 16, position), "Write(IntPtr)" + at);
                        AssertDisposed(name, () => buffer.Write(p => Marshal.WriteByte(p, 1), position), "Write(Action)" + at);
                        AssertDisposed(name, () => { long read; buffer.Read(out read, position); }, "Read(out T)" + at);
                        AssertDisposed(name, () => buffer.Read(new byte[16], position), "Read(T[])" + at);
                        AssertDisposed(name, () => buffer.Read(memory, 16, position), "Read(IntPtr)" + at);
                        AssertDisposed(name, () => buffer.Read(p => Marshal.ReadByte(p), position), "Read(Action)" + at);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        [TestMethod]
        [DataRow("Close")]
        [DataRow("Dispose")]
        public void SharedArray_EveryCallAfterClose_ThrowsObjectDisposedExceptionNamingTheBuffer(string how)
        {
            string name = Guid.NewGuid().ToString();
            using (var array = new SharedArray<int>(name, Size / sizeof(int)))
            {
                End(array, how);
                foreach (long position in Positions)
                {
                    int index = (int)(position / sizeof(int));
                    string at = " at " + position + " after " + how;
                    int value = 1;
                    AssertDisposed(name, () => { int read = array[index]; }, "the indexer's get" + at);
                    AssertDisposed(name, () => array[index] = 1, "the indexer's set" + at);
                    AssertDisposed(name, () => array.Write(ref value, index), "Write(ref T, index)" + at);
                    AssertDisposed(name, () => array.Write(new int[4], index), "Write(T[], startIndex)" + at);
                    AssertDisposed(name, () => { int read; array.Read(out read, index); }, "Read(out T, index)" + at);
                    AssertDisposed(name, () => array.CopyTo(new int[4], index), "CopyTo(T[], startIndex)" + at);
                }
                AssertDisposed(name, () => ((ICollection<int>)array).CopyTo(new int[array.Length], 0), "ICollection<T>.CopyTo after " + how);
                AssertDisposed(name, () => array.GetEnumerator().MoveNext(), "enumeration after " + how);
            }
        }

        [TestMethod]
        [DataRow("Close")]
        [DataRow("Dispose")]
        public void CircularBuffer_EveryCallAfterClose_ThrowsObjectDisposedExceptionNamingTheBuffer(string how)
        {
            string name = Guid.NewGuid().ToString();
            IntPtr memory = Marshal.AllocHGlobal(Chunk);
            try
            {
                using (var ring = new CircularBuffer(name, NodeCount, Chunk))
                {
                    // A node to read, so that a read after Close finds something
                    Assert.AreEqual(16, ring.Write(new byte[16]));
                    End(ring, how);
                    string after = " after " + how;
                    int value = 1;
                    AssertDisposed(name, () => ring.Write(new byte[16], 0, 0), "Write(byte[])" + after);
                    AssertDisposed(name, () => ring.Write(new int[4], 0, 0), "Write(T[])" + after);
                    AssertDisposed(name, () => ring.Write(ref value, 0), "Write(ref T)" + after);
                    AssertDisposed(name, () => ring.Write(memory, 16, 0), "Write(IntPtr)" + after);
                    AssertDisposed(name, () => ring.Write(p => 0, 0), "Write(Func)" + after);
                    AssertDisposed(name, () => ring.Read(new byte[16], 0, 0), "Read(byte[])" + after);
                    AssertDisposed(name, () => ring.Read(new int[4], 0, 0), "Read(T[])" + after);
                    AssertDisposed(name, () => { int read; ring.Read(out read, 0); }, "Read(out T)" + after);
                    AssertDisposed(name, () => ring.Read(memory, 16, 0), "Read(IntPtr)" + after);
                    AssertDisposed(name, () => ring.Read(p => 0, 0), "Read(Func)" + after);
                    AssertDisposed(name, () => ring.ReadNodeHeader(), "ReadNodeHeader" + after);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        #endregion

        #region Close during a call

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void Dispose_OnAnotherThreadDuringAReadCallback_ReturnsFirstAndTheCallbackReadsItAll(string kind)
        {
            WithinTime(RepeatsWithinMs, () =>
            {
                for (int round = 0; round < Repeats; round++)
                {
                    string name = Guid.NewGuid().ToString();
                    byte[] data = Pattern(Chunk, round);
                    var seen = new byte[Chunk];
                    bool disposedFirst = false, called = false;
                    var target = Create(kind, name);
                    using (var entered = new ManualResetEvent(false))
                    using (var disposed = new ManualResetEvent(false))
                    {
                        target.Put(data);
                        var disposer = new Thread(() =>
                        {
                            entered.WaitOne();
                            target.Dispose();
                            disposed.Set();
                        });
                        disposer.IsBackground = true;
                        disposer.Start();

                        target.ReadWith(pointer =>
                        {
                            called = true;
                            entered.Set();
                            disposedFirst = disposed.WaitOne(WaitMs);
                            Marshal.Copy(pointer, seen, 0, Chunk);
                        });
                        Assert.IsTrue(disposer.Join(WaitMs), "Dispose did not return, round " + round);
                    }

                    Assert.IsTrue(called, "The callback was not called, round " + round);
                    Assert.IsTrue(disposedFirst, "Dispose did not return while the callback ran, round " + round);
                    CollectionAssert.AreEqual(data, seen, "What the callback read after Dispose, round " + round);
                    AssertDisposed(name, () => target.ReadWith(pointer => { }), "A read after Dispose, round " + round);
                }
            });
        }

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void Dispose_InsideAWriteCallback_ReturnsAndTheWriteLands(string kind)
        {
            WithinTime(RepeatsWithinMs, () =>
            {
                for (int round = 0; round < Repeats; round++)
                {
                    string name = Guid.NewGuid().ToString();
                    byte[] data = Pattern(Chunk, round);
                    bool returned = false, shutdownSeen = false;
                    var owner = Create(kind, name);
                    using (var opener = Open(kind, name))
                    {
                        owner.WriteWith(pointer =>
                        {
                            owner.Dispose();
                            returned = true;
                            // The owner's Close sets the flag at once, not when the call returns
                            shutdownSeen = opener.Buffer.ShuttingDown;
                            Marshal.Copy(data, 0, pointer, Chunk);
                        });

                        Assert.IsTrue(returned, "Dispose did not return in the callback, round " + round);
                        Assert.IsTrue(shutdownSeen, "The opener did not see ShuttingDown while the callback ran, round " + round);
                        CollectionAssert.AreEqual(data, opener.Take(), "What the callback wrote after Dispose, round " + round);
                    }
                    AssertDisposed(name, () => owner.WriteWith(pointer => { }), "A write after Dispose, round " + round);
                }
            });
        }

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void Dispose_DuringACallback_ReleasesTheMemoryWhenTheCallbackReturns(string kind)
        {
            string name = Guid.NewGuid().ToString();
            byte[] data = Pattern(Chunk, 7);
            byte[] seenByOpener = null;
            bool mappedInCallback = false;
            var owner = Create(kind, name);
            LeakTests.Mapping mapping = LeakTests.Mapping.Of(owner.Buffer);
            // Twice, so that a ring holds a node for the opener besides the one the owner reads
            owner.Put(data);
            owner.Put(data);

            owner.ReadWith(pointer =>
            {
                owner.Dispose();
                mappedInCallback = mapping.IsMapped;
                // Still mapped: another instance opens it, and finds what the owner wrote
                using (var opener = Open(kind, name))
                    seenByOpener = opener.Take();
            });

            Assert.IsTrue(mappedInCallback, "The owner's view was unmapped while its callback ran.");
            CollectionAssert.AreEqual(data, seenByOpener, "What an opener read while the owner's callback ran");
            Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name).Dispose(), "The memory was still mapped after the callback returned.");
            // The name goes with the last handle, even while a view stays mapped (Windows), so the view is looked for too
            Assert.IsFalse(mapping.IsMapped, "The owner's view was still mapped after the callback returned.");
        }

        /// <summary>
        /// Two callbacks running when the ring is disposed: its memory stays mapped until the second one returns, not the first, so
        /// the second still writes through its pointer, and the memory goes once the second returns.
        /// </summary>
        [TestMethod]
        public void Dispose_DuringTwoCallbacks_KeepsTheMemoryUntilTheLastReturns()
        {
            string name = Guid.NewGuid().ToString();
            byte[] first = Pattern(Chunk, 1), second = Pattern(Chunk, 2);
            var failures = new List<Exception>();
            var owner = Create(RingKind, name);
            LeakTests.Mapping mapping = LeakTests.Mapping.Of(owner.Buffer);
            // Never disposed: if the memory went with the first callback, the second must not go on to write through its pointer
            var releaseSecond = new ManualResetEvent(false);
            using (var opener = Open(RingKind, name))
            using (var firstIn = new ManualResetEvent(false))
            using (var secondIn = new ManualResetEvent(false))
            using (var releaseFirst = new ManualResetEvent(false))
            {
                Thread one = WriteOnThread(owner, first, firstIn, releaseFirst, failures);
                Assert.IsTrue(firstIn.WaitOne(WaitMs), "The first callback did not start.");
                Thread two = WriteOnThread(owner, second, secondIn, releaseSecond, failures);
                Assert.IsTrue(secondIn.WaitOne(WaitMs), "The second callback did not start.");

                owner.Dispose();
                releaseFirst.Set();
                Assert.IsTrue(one.Join(WaitMs), "The first callback did not return.");
                Assert.IsTrue(mapping.IsMapped, "The owner's memory was unmapped when the first of two callbacks returned.");

                releaseSecond.Set();
                Assert.IsTrue(two.Join(WaitMs), "The second callback did not return.");
                lock (failures)
                    Assert.AreEqual(0, failures.Count, "A callback's call threw: " + String.Join("; ", failures));
                CollectionAssert.AreEqual(first, opener.Take(), "What the first callback wrote");
                CollectionAssert.AreEqual(second, opener.Take(), "What the second callback wrote after the first returned");
            }
            Assert.IsFalse(mapping.IsMapped, "The owner's memory was still mapped after the last callback returned.");
        }

        // A write whose callback signals that it has started, waits for its release, then writes the data through its pointer
        static Thread WriteOnThread(Target target, byte[] data, ManualResetEvent entered, ManualResetEvent release, List<Exception> failures)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    target.WriteWith(pointer =>
                    {
                        entered.Set();
                        release.WaitOne();
                        Marshal.Copy(data, 0, pointer, data.Length);
                    });
                }
                catch (Exception e)
                {
                    lock (failures)
                        failures.Add(e);
                }
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        #endregion

        #region Opening again

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void Open_AgainAfterClose_ReadsAndWritesAgain(string kind)
        {
            string name = Guid.NewGuid().ToString();
            byte[] first = Pattern(Chunk, 1), second = Pattern(Chunk, 2), third = Pattern(Chunk, 3);
            using (var owner = Create(kind, name))
            {
                owner.Put(first);
                owner.Close();
                Assert.IsTrue(owner.Reopen(), "The owner's Open after Close");
                owner.Put(second);
                using (var opener = Open(kind, name))
                {
                    CollectionAssert.AreEqual(second, opener.Take(), "After the owner opened again");
                    opener.Close();
                    Assert.IsTrue(opener.Reopen(), "The opener's Open after Close");
                    owner.Put(third);
                    CollectionAssert.AreEqual(third, opener.Take(), "After the opener opened again");
                }
            }
        }

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void Open_InACallback_ThrowsInvalidOperationExceptionAndTheCallFinishes(string kind)
        {
            WithinTime(WaitMs, () =>
            {
                string name = Guid.NewGuid().ToString();
                byte[] first = Pattern(Chunk, 1), second = Pattern(Chunk, 2), third = Pattern(Chunk, 3);
                var owner = Create(kind, name);
                var opener = Open(kind, name);
                try
                {
                    // Open while a callback runs, the instance still open: refused, and the instance stays open
                    Exception refused = null;
                    owner.WriteWith(pointer =>
                    {
                        refused = Catch(() => owner.Reopen());
                        Marshal.Copy(first, 0, pointer, Chunk);
                    });
                    AssertRefused(refused, "Open in a callback");
                    CollectionAssert.AreEqual(first, opener.Take(), "The callback's write after the refused Open");
                    owner.Put(second);
                    CollectionAssert.AreEqual(second, opener.Take(), "A write after the refused Open");

                    // Close and then Open while a callback runs: refused, and the callback's pointer still holds
                    refused = null;
                    owner.WriteWith(pointer =>
                    {
                        owner.Close();
                        refused = Catch(() => owner.Reopen());
                        Marshal.Copy(third, 0, pointer, Chunk);
                    });
                    AssertRefused(refused, "Open in a callback after Close");
                    CollectionAssert.AreEqual(third, opener.Take(), "The callback's write after Close");

                    // Once the call has returned, Open maps again
                    opener.Dispose();
                    Assert.IsTrue(owner.Reopen(), "Open after the call returned");
                    opener = Open(kind, name);
                    owner.Put(second);
                    CollectionAssert.AreEqual(second, opener.Take(), "After opening again");
                }
                finally
                {
                    opener.Dispose();
                    owner.Dispose();
                }
            });
        }

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        [DataRow(RingKind)]
        public void Close_TwiceAndDisposeAfterClose_ReleaseTheMemoryOnce(string kind)
        {
            string name = Guid.NewGuid().ToString();
            byte[] data = Pattern(Chunk, 5);
            var owner = Create(kind, name);
            try
            {
                owner.Close();
                owner.Close();
                Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name).Dispose(), "The memory was still mapped after Close twice.");

                // The lease came back to where Open starts it, once: Open maps again, and the next Close releases it
                Assert.IsTrue(owner.Reopen(), "Open after Close twice");
                using (var opener = Open(kind, name))
                {
                    owner.Put(data);
                    CollectionAssert.AreEqual(data, opener.Take(), "After opening again");
                }
                owner.Close();
                owner.Dispose();
                owner.Dispose();
                Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name).Dispose(), "The memory was still mapped after Dispose.");
            }
            finally
            {
                owner.Dispose();
            }
        }

        #endregion

        #region Subclasses of CircularBuffer

        /// <summary>
        /// A subclass that reserves nodes through the protected methods, overriding them or calling them itself, still has its memory
        /// released at Close: no lease is left behind.
        /// </summary>
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void CircularBuffer_SubclassReservingThroughTheProtectedMethods_IsReleasedAtClose(bool overrides)
        {
            string name = Guid.NewGuid().ToString();
            byte[] data = Pattern(16, 9);
            var owner = overrides ? new OverridingRing(name, NodeCount, Chunk) : new OwnWritesRing(name, NodeCount, Chunk);
            using (var opener = new CircularBuffer(name))
            {
                // The overloads, through the override if there is one
                Assert.AreEqual(data.Length, owner.Write(data));
                var read = new byte[data.Length];
                Assert.AreEqual(data.Length, opener.Read(read));
                CollectionAssert.AreEqual(data, read, "Through Write");
                Assert.AreEqual(data.Length, opener.Write(data));
                Assert.AreEqual(data.Length, owner.Read(read));

                // The subclass's own write, through GetNodeForWriting and PostNode
                Assert.IsTrue(owner.WriteOwn(42), "The subclass's own write");
                Assert.AreEqual(1, opener.Read(read));
                Assert.AreEqual(42, read[0], "Through the subclass's own write");

                if (overrides)
                    Assert.AreEqual(3, ((OverridingRing)owner).Reservations, "The overrides were not called");
            }

            owner.Dispose();
            Assert.ThrowsExactly<FileNotFoundException>(() => new CircularBuffer(name).Dispose(), "The subclass's memory was still mapped after Dispose.");
        }

        /// <summary>
        /// A subclass's own reservation that closes the buffer before it returns the node: the overload still copies into or out of
        /// the node and posts or returns it, so the ring goes on for other instances, and the memory is released after.
        /// </summary>
        [TestMethod]
        [DataRow("write")]
        [DataRow("read")]
        public void CircularBuffer_OverrideThatClosesTheBuffer_StillFinishesTheCall(string call)
        {
            string name = Guid.NewGuid().ToString();
            byte[] data = Pattern(16, 11);
            var read = new byte[data.Length];
            var owner = new ClosingRing(name, NodeCount, Chunk);
            using (var opener = new CircularBuffer(name))
            {
                if (call == "write")
                {
                    owner.CloseOnReserve = true;
                    Assert.AreEqual(data.Length, owner.Write(data), "The write whose reservation closed the buffer");
                    Assert.AreEqual(data.Length, opener.Read(read), "The opener's read of that write");
                }
                else
                {
                    Assert.AreEqual(data.Length, opener.Write(data));
                    owner.CloseOnReserve = true;
                    Assert.AreEqual(data.Length, owner.Read(read), "The read whose reservation closed the buffer");
                }
                CollectionAssert.AreEqual(data, read);

                // The node was posted or returned: the ring goes on for the opener
                Assert.AreEqual(data.Length, opener.Write(data), "A write after it");
                Assert.AreEqual(data.Length, opener.Read(read), "A read after it");
                AssertDisposed(name, () => owner.Write(data), "The owner's write after its reservation closed it");
            }
            Assert.ThrowsExactly<FileNotFoundException>(() => new CircularBuffer(name).Dispose(), "The owner's memory was still mapped.");
        }

        class ClosingRing : CircularBuffer
        {
            public bool CloseOnReserve;

            public ClosingRing(string name, int nodeCount, int nodeBufferSize) : base(name, nodeCount, nodeBufferSize) { }

            protected override unsafe Node* GetNodeForWriting(int timeout)
            {
                Node* node = base.GetNodeForWriting(timeout);
                if (CloseOnReserve)
                    Close();
                return node;
            }

            protected override unsafe Node* GetNodeForReading(int timeout)
            {
                Node* node = base.GetNodeForReading(timeout);
                if (CloseOnReserve)
                    Close();
                return node;
            }
        }

        class OwnWritesRing : CircularBuffer
        {
            public OwnWritesRing(string name, int nodeCount, int nodeBufferSize) : base(name, nodeCount, nodeBufferSize) { }

            public unsafe bool WriteOwn(byte value)
            {
                Node* node = GetNodeForWriting(1000);
                if (node == null)
                    return false;
                BufferStartPtr[node->Offset] = value;
                node->AmountWritten = 1;
                PostNode(node);
                return true;
            }
        }

        class OverridingRing : OwnWritesRing
        {
            public int Reservations;

            public OverridingRing(string name, int nodeCount, int nodeBufferSize) : base(name, nodeCount, nodeBufferSize) { }

            protected override unsafe Node* GetNodeForWriting(int timeout)
            {
                Reservations++;
                return base.GetNodeForWriting(timeout);
            }

            protected override unsafe Node* GetNodeForReading(int timeout)
            {
                Reservations++;
                return base.GetNodeForReading(timeout);
            }
        }

        #endregion

        #region How the JIT sees the lease

        /// <summary>
        /// The last call out's release of the mapping is never inlined, so that each call's Exit stays small where the JIT inlines
        /// it. Inlined into every Exit, it made .NET 10 compile RpcBuffer's read loop for about half a second on Linux as it ran,
        /// while every call waited.
        /// </summary>
        [TestMethod]
        public void Unmap_NeverInlined()
        {
            var unmap = typeof(SharedBuffer).GetMethod("Unmap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(unmap);
            Assert.AreEqual(System.Reflection.MethodImplAttributes.NoInlining, unmap.MethodImplementationFlags & System.Reflection.MethodImplAttributes.NoInlining);
        }

        #endregion

        #region Helpers

        static void End(SharedBuffer buffer, string how)
        {
            if (how == "Close")
                buffer.Close();
            else
                buffer.Dispose();
        }

        static void AssertDisposed(string name, Action call, string what)
        {
            var e = Assert.ThrowsExactly<ObjectDisposedException>(call, what);
            Assert.AreEqual(name, e.ObjectName, what + ": the exception does not name the buffer");
        }

        static void AssertRefused(Exception refused, string what)
        {
            Assert.IsTrue(refused is InvalidOperationException, what + ": " + (refused == null ? "no exception" : refused.ToString()));
        }

        static Exception Catch(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        // Runs the action on a thread of its own, failing if it takes longer than the time given, as it would if Close waited for a call
        static void WithinTime(int milliseconds, Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            thread.IsBackground = true;
            thread.Start();
            Assert.IsTrue(thread.Join(milliseconds), "The test did not finish within " + milliseconds + " ms: Close or Dispose may be waiting for a call.");
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        static byte[] Pattern(int count, int seed)
        {
            var data = new byte[count];
            for (int i = 0; i < count; i++)
                data[i] = (byte)(i * 7 + seed * 13 + 1);
            return data;
        }

        static Target Create(string kind, string name)
        {
            switch (kind)
            {
                case BufferKind: return new BufferTarget(new ReopenableBuffer(name, Size));
                case ArrayKind: return new ArrayTarget(new ReopenableArray(name, Size));
                case RingKind: return new RingTarget(new ReopenableRing(name, NodeCount, Chunk));
                default: throw new ArgumentException("Unknown kind " + kind);
            }
        }

        static Target Open(string kind, string name)
        {
            switch (kind)
            {
                case BufferKind: return new BufferTarget(new ReopenableBuffer(name));
                case ArrayKind: return new ArrayTarget(new ReopenableArray(name));
                case RingKind: return new RingTarget(new ReopenableRing(name));
                default: throw new ArgumentException("Unknown kind " + kind);
            }
        }

        /// <summary>
        /// One of the three classes, seen the same way: <see cref="Put"/> writes Chunk bytes where <see cref="ReadWith"/> and
        /// <see cref="Take"/> read them (the start of the buffer, or the ring's next node), and <see cref="WriteWith"/> writes there.
        /// </summary>
        abstract class Target : IDisposable
        {
            public abstract SharedBuffer Buffer { get; }
            public abstract void Put(byte[] data);
            public abstract byte[] Take();
            public abstract void ReadWith(Action<IntPtr> callback);
            public abstract void WriteWith(Action<IntPtr> callback);
            public abstract bool Reopen();

            public void Close()
            {
                Buffer.Close();
            }

            public void Dispose()
            {
                Buffer.Dispose();
            }
        }

        sealed class BufferTarget : Target
        {
            readonly ReopenableBuffer buffer;

            public BufferTarget(ReopenableBuffer buffer)
            {
                this.buffer = buffer;
            }

            public override SharedBuffer Buffer { get { return buffer; } }

            public override void Put(byte[] data)
            {
                buffer.Write(data, 0);
            }

            public override byte[] Take()
            {
                var data = new byte[Chunk];
                buffer.Read(data, 0);
                return data;
            }

            public override void ReadWith(Action<IntPtr> callback)
            {
                buffer.Read(callback, 0);
            }

            public override void WriteWith(Action<IntPtr> callback)
            {
                buffer.Write(callback, 0);
            }

            public override bool Reopen()
            {
                return buffer.Reopen();
            }
        }

        sealed class ArrayTarget : Target
        {
            readonly ReopenableArray array;

            public ArrayTarget(ReopenableArray array)
            {
                this.array = array;
            }

            public override SharedBuffer Buffer { get { return array; } }

            public override void Put(byte[] data)
            {
                array.Write(data, 0);
            }

            public override byte[] Take()
            {
                var data = new byte[Chunk];
                array.CopyTo(data, 0);
                return data;
            }

            public override void ReadWith(Action<IntPtr> callback)
            {
                array.ReadWith(callback);
            }

            public override void WriteWith(Action<IntPtr> callback)
            {
                array.WriteWith(callback);
            }

            public override bool Reopen()
            {
                return array.Reopen();
            }
        }

        sealed class RingTarget : Target
        {
            readonly ReopenableRing ring;

            public RingTarget(ReopenableRing ring)
            {
                this.ring = ring;
            }

            public override SharedBuffer Buffer { get { return ring; } }

            public override void Put(byte[] data)
            {
                Assert.AreEqual(data.Length, ring.Write(data), "Put");
            }

            public override byte[] Take()
            {
                var data = new byte[Chunk];
                Assert.AreEqual(Chunk, ring.Read(data), "Take");
                return data;
            }

            public override void ReadWith(Action<IntPtr> callback)
            {
                ring.Read(pointer => { callback(pointer); return Chunk; });
            }

            public override void WriteWith(Action<IntPtr> callback)
            {
                ring.Write(pointer => { callback(pointer); return Chunk; });
            }

            public override bool Reopen()
            {
                return ring.Reopen();
            }
        }

        class EveryCallBuffer : EventFreeBuffer
        {
            public EveryCallBuffer(string name, long bufferSize) : base(name, bufferSize) { }

            public void WriteStruct(long position) { long value = 1; Write(ref value, position); }
            public void WriteElements(long position) { Write(new int[4], position); }
            public void WriteElementsFrom(long position) { Write(new int[4], 1, position); }
            public void WriteArrayPart(long position) { WriteArray(new int[4], 0, 4, position); }
            public void WritePointer(IntPtr source, long position) { Write(source, 16, position); }
            public void WriteWith(long position) { Write(p => Marshal.WriteByte(p, 1), position); }
            public void ReadStruct(long position) { long value; Read(out value, position); }
            public void ReadElements(long position) { Read(new int[4], position); }
            public void ReadArrayPart(long position) { ReadArray(new int[4], 0, 4, position); }
            public void ReadPointer(IntPtr destination, long position) { Read(destination, 16, position); }
            public void ReadWith(long position) { Read(p => Marshal.ReadByte(p), position); }
        }

        class ReopenableBuffer : BufferReadWrite
        {
            public ReopenableBuffer(string name, int bufferSize) : base(name, bufferSize) { }
            public ReopenableBuffer(string name) : base(name) { }

            public bool Reopen()
            {
                return Open();
            }
        }

        class ReopenableArray : SharedArray<byte>
        {
            public ReopenableArray(string name, int length) : base(name, length) { }
            public ReopenableArray(string name) : base(name) { }

            public bool Reopen()
            {
                return Open();
            }

            public void ReadWith(Action<IntPtr> readFunc)
            {
                Read(readFunc, 0);
            }

            public void WriteWith(Action<IntPtr> writeFunc)
            {
                Write(writeFunc, 0);
            }
        }

        class ReopenableRing : CircularBuffer
        {
            public ReopenableRing(string name, int nodeCount, int nodeBufferSize) : base(name, nodeCount, nodeBufferSize) { }
            public ReopenableRing(string name) : base(name) { }

            public bool Reopen()
            {
                return Open();
            }
        }

        #endregion
    }
}
