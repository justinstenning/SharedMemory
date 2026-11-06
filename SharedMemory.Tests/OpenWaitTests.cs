using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// How an opener of <see cref="BufferReadWrite"/> and <see cref="SharedArray{T}"/> sizes the buffer from the header its owner
    /// wrote, and how it fails on a name that does not exist. An owner makes the shared memory before it writes the header, so an
    /// opener can find the size still 0: it waits up to 1 s for the size, then gives up with InvalidOperationException. From 2.4.0 the
    /// owner also sets the ready flag of the layout marker in bytes 12 to 15 once DoOpen returns, and an opener of
    /// <see cref="BufferReadWrite"/>, <see cref="SharedArray{T}"/> and <see cref="CircularBuffer"/> waits up to 1 s for that flag.
    /// </summary>
    [TestClass]
    public class OpenWaitTests
    {
        const string BufferKind = "BufferReadWrite";
        const string ArrayKind = "SharedArray";
        const string RingKind = "CircularBuffer";
        const int Size = 1024;
        const int Ready = 0x00001000;

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void Opener_OfAnExistingBuffer_OpensWithItsSize(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (SharedBuffer owner = Create(kind, name))
            using (SharedBuffer opener = Open(kind, name))
            {
                Assert.IsFalse(opener.IsOwnerOfSharedMemory);
                Assert.AreEqual(Size, opener.BufferSize);
                Assert.AreEqual(owner.SharedMemorySize, opener.SharedMemorySize);
            }
        }

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void Opener_OfAMissingName_ThrowsFileNotFoundException(string kind)
        {
            string name = Guid.NewGuid().ToString();

            Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name).Dispose());
        }

        /// <summary>
        /// The owner writes the size 100 ms after an opener starts: the opener is still waiting then, and opens with the owner's size.
        /// </summary>
        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void Opener_OfAHeaderWrittenLate_WaitsAndOpensWithItsSize(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new UnwrittenHeader(name, Size))
            using (var started = new ManualResetEventSlim())
            {
                Task<SharedBuffer> opening = Task.Factory.StartNew(() => { started.Set(); return Open(kind, name); }, TaskCreationOptions.LongRunning);
                try
                {
                    Assert.IsTrue(started.Wait(5000), "The opener did not start.");
                    Thread.Sleep(100);
                    Assert.IsFalse(opening.IsCompleted, "The opener should still be waiting for the size.");
                    owner.WriteSize();

                    Assert.IsTrue(opening.Wait(5000), "The opener did not open once the size was written.");
                    Assert.AreEqual(Size, opening.Result.BufferSize);
                }
                finally
                {
                    // Whatever the opener opened is disposed, also after a failed assert
                    if (SpinWait.SpinUntil(() => opening.IsCompleted, 5000) && opening.Status == TaskStatus.RanToCompletion)
                        opening.Result.Dispose();
                }
            }
        }

        /// <summary>
        /// The owner never writes the size: the opener gives up after 1 s with InvalidOperationException naming the buffer.
        /// </summary>
        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void Opener_OfAHeaderNeverWritten_ThrowsInvalidOperationExceptionAfterOneSecond(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (new UnwrittenHeader(name, Size))
            {
                var watch = Stopwatch.StartNew();
                var e = Assert.ThrowsExactly<InvalidOperationException>(() => Open(kind, name).Dispose());
                watch.Stop();

                StringAssert.Contains(e.Message, name);
                Assert.IsTrue(watch.ElapsedMilliseconds >= 1000 && watch.ElapsedMilliseconds <= 1500, "gave up after " + watch.ElapsedMilliseconds + " ms");
            }
        }

        /// <summary>
        /// The marker without its ready flag and the whole size, as an opener finds a buffer whose owner is still in DoOpen. The ready
        /// flag is set 100 ms after the opener starts: the opener is still waiting then, and opens with the owner's size.
        /// </summary>
        [TestMethod]
        [DataRow(BufferKind, 0x00000011)]
        [DataRow(ArrayKind, 0x00000021)]
        [DataRow(RingKind, 0x00000031)]
        public void Opener_OfAMarkerNotYetReady_WaitsAndOpens(string kind, int notReady)
        {
            string name = Guid.NewGuid().ToString();
            using (SharedBuffer owner = Create(kind, name))
            using (var raw = new RawHeader(name))
            using (var started = new ManualResetEventSlim())
            {
                raw.Marker = notReady;
                Task<SharedBuffer> opening = Task.Factory.StartNew(() => { started.Set(); return Open(kind, name); }, TaskCreationOptions.LongRunning);
                try
                {
                    Assert.IsTrue(started.Wait(5000), "The opener did not start.");
                    Thread.Sleep(100);
                    Assert.IsFalse(opening.IsCompleted, "The opener should still be waiting for the ready flag, but " + Describe(opening));
                    raw.Marker = notReady | Ready;

                    Assert.IsTrue(SpinWait.SpinUntil(() => opening.IsCompleted, 5000), "The opener did not return once the ready flag was set.");
                    Assert.AreEqual(TaskStatus.RanToCompletion, opening.Status, "The opener should have opened, but " + Describe(opening));
                    Assert.AreEqual(owner.BufferSize, opening.Result.BufferSize);
                }
                finally
                {
                    DisposeOpened(opening);
                }
            }
        }

        /// <summary>
        /// The ready flag is never set: the opener gives up after 1 s with InvalidOperationException "is not ready", naming the buffer.
        /// </summary>
        [TestMethod]
        [DataRow(BufferKind, 0x00000011)]
        [DataRow(ArrayKind, 0x00000021)]
        [DataRow(RingKind, 0x00000031)]
        public void Opener_OfAMarkerNeverReady_ThrowsAfterOneSecond(string kind, int notReady)
        {
            string name = Guid.NewGuid().ToString();
            using (Create(kind, name))
            {
                using (var raw = new RawHeader(name))
                    raw.Marker = notReady;

                long ms;
                InvalidOperationException e = LayoutMarkerTests.Refused(() => Open(kind, name), out ms);
                StringAssert.Contains(e.Message, name);
                StringAssert.Contains(e.Message, "is not ready");
                Assert.IsTrue(ms >= 1000 && ms <= 1500, "gave up after " + ms + " ms");
            }
        }

        /// <summary>
        /// The owner is held inside DoOpen, after its header is written, until the opener returns or 200 ms pass, then writes a
        /// pattern there: an item in a ring, 64 bytes at the start of a SharedBuffer subclass. The opener waits for DoOpen to return,
        /// and reads the pattern at once.
        /// </summary>
        [TestMethod]
        [DataRow("SharedBuffer")]
        [DataRow("CircularBuffer")]
        public void Opener_OfAnOwnerStillInDoOpen_WaitsUntilItReturns(string baseClass)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new HeldOwner(name, baseClass == "CircularBuffer", false))
            {
                Assert.IsTrue(owner.InDoOpen.Wait(5000), "The owner did not reach DoOpen: " + owner.Failure);
                Assert.IsNull(owner.Failure, "The owner failed: " + owner.Failure);
                Task<byte[]> opening = Task.Factory.StartNew(() => OpenAndRead(baseClass, name), TaskCreationOptions.LongRunning);
                try
                {
                    SpinWait.SpinUntil(() => opening.IsCompleted, 200);
                    owner.Release();

                    Assert.IsTrue(SpinWait.SpinUntil(() => opening.IsCompleted, 5000), "The opener did not return.");
                    Assert.AreEqual(TaskStatus.RanToCompletion, opening.Status, "The opener should have opened, but " + Describe(opening));
                    CollectionAssert.AreEqual(HeldOwner.Pattern, opening.Result,
                        "The opener returned before the owner's DoOpen did: it read [" + BitConverter.ToString(opening.Result) + "]");
                    Assert.IsTrue(owner.Done.Wait(5000), "The owner did not finish.");
                    Assert.IsNull(owner.Failure, "The owner failed: " + owner.Failure);
                }
                finally
                {
                    owner.Release();
                    SpinWait.SpinUntil(() => opening.IsCompleted, 5000);
                }
            }
        }

        /// <summary>
        /// A ring whose marker is not ready and whose node count is 0: the opener gives up after the ready flag's 1 s, without a
        /// second wait for the node count.
        /// </summary>
        [TestMethod]
        public void RingOpener_OfARingNeverReady_GivesUpWithinOneAndAHalfSeconds()
        {
            string name = Guid.NewGuid().ToString();
            using (new CircularBuffer(name, 4, 64))
            {
                // NodeCount is the node header's fifth int, after the 16-byte shared header
                using (var raw = new RawHeader(name, 40))
                {
                    raw.Marker = 0x00000031;
                    raw.WriteInt32(32, 0);
                }

                long ms;
                InvalidOperationException e = LayoutMarkerTests.Refused(() => new CircularBuffer(name), out ms);
                StringAssert.Contains(e.Message, name);
                StringAssert.Contains(e.Message, "is not ready");
                Assert.IsFalse(e.Message.Contains("node count"), "The opener waited for the node count, as for a ring with no marker, and gave up after " + ms + " ms: " + e.Message);
                Assert.IsTrue(ms >= 1000 && ms <= 1500, "gave up after " + ms + " ms");
            }
        }

        /// <summary>
        /// The owner is held inside DoOpen while an opener waits, then throws, and its Close sets Shutdown: the opener throws
        /// "is not ready" at once, saying the owner closed it, rather than after 1 s.
        /// </summary>
        [TestMethod]
        public void Opener_OfAnOwnerClosedBeforeReady_ThrowsAtOnce()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new HeldOwner(name, false, true))
            {
                Assert.IsTrue(owner.InDoOpen.Wait(5000), "The owner did not reach DoOpen: " + owner.Failure);
                long failedAt = -1;
                Task<EventFreeBuffer> opening = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        return new EventFreeBuffer(name);
                    }
                    finally
                    {
                        failedAt = owner.Clock.ElapsedMilliseconds;
                    }
                }, TaskCreationOptions.LongRunning);
                try
                {
                    Thread.Sleep(100);
                    Assert.IsFalse(opening.IsCompleted, "The opener should still be waiting for its owner, but " + Describe(opening));
                    owner.Release();

                    Assert.IsTrue(owner.Done.Wait(5000), "The owner did not finish.");
                    Assert.IsTrue(owner.Failure is InvalidOperationException && owner.Failure.Message == HeldOwner.Planted, "The owner should have thrown the planted exception: " + owner.Failure);
                    Assert.IsTrue(SpinWait.SpinUntil(() => opening.IsCompleted, 5000), "The opener did not return once its owner closed.");
                    Assert.AreEqual(TaskStatus.Faulted, opening.Status, "The opener should have thrown, but " + Describe(opening));
                    var e = opening.Exception.InnerException as InvalidOperationException;
                    Assert.IsNotNull(e, "The opener threw " + opening.Exception.InnerException);
                    StringAssert.Contains(e.Message, name);
                    StringAssert.Contains(e.Message, "is not ready");
                    StringAssert.Contains(e.Message, "closed");
                    Assert.IsTrue(failedAt - owner.ClosedAt <= 100, "The opener threw " + (failedAt - owner.ClosedAt) + " ms after its owner closed: " + e.Message);
                }
                finally
                {
                    owner.Release();
                    DisposeOpened(opening);
                }
            }
        }

        static SharedBuffer Create(string kind, string name)
        {
            if (kind == ArrayKind)
                return new SharedArray<int>(name, Size / sizeof(int));
            if (kind == RingKind)
                return new CircularBuffer(name, 4, 64);
            return new BufferReadWrite(name, Size);
        }

        static SharedBuffer Open(string kind, string name)
        {
            if (kind == ArrayKind)
                return new SharedArray<int>(name);
            if (kind == RingKind)
                return new CircularBuffer(name);
            return new BufferReadWrite(name);
        }

        /// <summary>
        /// Opens the buffer, reads what the owner's DoOpen writes and returns it: the ring's first item, or the first 64 bytes.
        /// </summary>
        static byte[] OpenAndRead(string baseClass, string name)
        {
            if (baseClass == "CircularBuffer")
            {
                using (var ring = new CircularBuffer(name))
                {
                    byte[] item = new byte[HeldOwner.Pattern.Length];
                    int read = ring.Read(item, 0, 0);
                    Array.Resize(ref item, read);
                    return item;
                }
            }
            using (var buffer = new EventFreeBuffer(name))
                return buffer.ReadBytes(HeldOwner.Pattern.Length);
        }

        static string Describe(Task task)
        {
            if (task.Status == TaskStatus.RanToCompletion)
                return "it opened.";
            if (task.IsFaulted)
                return "it threw " + task.Exception.InnerException.GetType().Name + ": " + task.Exception.InnerException.Message;
            return "it is " + task.Status + ".";
        }

        static void DisposeOpened<T>(Task<T> opening) where T : IDisposable
        {
            // Whatever the opener opened is disposed, also after a failed assert
            if (SpinWait.SpinUntil(() => opening.IsCompleted, 5000) && opening.Status == TaskStatus.RanToCompletion)
                opening.Result.Dispose();
        }

        /// <summary>
        /// An owner caught between making its shared memory and writing the header: it creates the buffer as every owner does, then
        /// sets the header's size and layout marker back to 0, as an opener finds them in that window, until <see cref="WriteSize"/>.
        /// </summary>
        sealed unsafe class UnwrittenHeader : SharedBuffer
        {
            public UnwrittenHeader(string name, long bufferSize)
                : base(name, bufferSize, true)
            {
                Open();
                Header->SharedMemorySize = 0;
                // From 2.4.0 the owner has also set a ready layout marker in bytes 12 to 15 by now, and a ready marker with a size of
                // 0 reads as a damaged buffer, not as one whose header is not written yet: so the marker goes back to 0 as well
                *(int*)((byte*)Header + 12) = 0;
            }

            public void WriteSize()
            {
                Header->SharedMemorySize = SharedMemorySize;
            }
        }

        /// <summary>
        /// An owner made on a thread of its own and held inside DoOpen, after its header is written, until <see cref="Release"/> or
        /// 5 s. Then it throws the planted exception, or writes <see cref="Pattern"/> and returns: as a ring, its first item; as a
        /// SharedBuffer subclass, the start of its buffer.
        /// </summary>
        sealed class HeldOwner : IDisposable
        {
            public const string Planted = "DoOpen failed on purpose.";
            public static readonly byte[] Pattern = MakePattern();

            [ThreadStatic]
            static HeldOwner _making;

            readonly string _name;
            readonly bool _ring;
            readonly bool _throws;
            readonly ManualResetEventSlim _release = new ManualResetEventSlim();
            readonly Thread _thread;
            SharedBuffer _owner;

            public readonly ManualResetEventSlim InDoOpen = new ManualResetEventSlim();
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public Exception Failure;
            public long ClosedAt = -1;

            public HeldOwner(string name, bool ring, bool throws)
            {
                _name = name;
                _ring = ring;
                _throws = throws;
                _thread = new Thread(Run) { IsBackground = true };
                _thread.Start();
            }

            /// <summary>
            /// The owner being made on this thread, for a ring's DoOpen, which runs inside CircularBuffer's constructor.
            /// </summary>
            public static HeldOwner Making
            {
                get { return _making; }
            }

            static byte[] MakePattern()
            {
                var pattern = new byte[64];
                for (int i = 0; i < pattern.Length; i++)
                    pattern[i] = (byte)(i * 7 + 1);
                return pattern;
            }

            void Run()
            {
                _making = this;
                try
                {
                    _owner = _ring ? (SharedBuffer)new HeldRing(_name) : new HeldBuffer(_name, this);
                }
                catch (Exception e)
                {
                    // The constructor's Open has closed the owner by now
                    ClosedAt = Clock.ElapsedMilliseconds;
                    Failure = e;
                }
                finally
                {
                    _making = null;
                    InDoOpen.Set();
                    Done.Set();
                }
            }

            /// <summary>
            /// Called by the owner's DoOpen: holds it there until released.
            /// </summary>
            public void Hold()
            {
                InDoOpen.Set();
                _release.Wait(5000);
                if (_throws)
                    throw new InvalidOperationException(Planted);
            }

            public void Release()
            {
                _release.Set();
            }

            public void Dispose()
            {
                _release.Set();
                if (_thread.Join(10000) && _owner != null)
                    _owner.Dispose();
            }
        }

        sealed class HeldBuffer : SharedBuffer
        {
            readonly HeldOwner _hold;

            public HeldBuffer(string name, HeldOwner hold)
                : base(name, Size, true)
            {
                _hold = hold;
                Open();
            }

            protected override bool DoOpen()
            {
                _hold.Hold();
                Write(HeldOwner.Pattern);
                return true;
            }
        }

        sealed class HeldRing : CircularBuffer
        {
            public HeldRing(string name)
                : base(name, 4, HeldOwner.Pattern.Length)
            {
            }

            protected override bool DoOpen()
            {
                bool opened = base.DoOpen();
                HeldOwner hold = HeldOwner.Making;
                if (hold != null)
                {
                    hold.Hold();
                    if (Write(HeldOwner.Pattern, 0, 0) != HeldOwner.Pattern.Length)
                        throw new InvalidOperationException("The owner could not write its first item.");
                }
                return opened;
            }
        }
    }
}
