using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The named events behind BufferWithLocks and CircularBuffer, on every OS: how a Set reaches the waiters of an
    /// auto-reset and a manual-reset event, timeouts, create-or-open, what an event's last close and a killed holder
    /// leave, a name another kind of object holds, and the CPU a blocked reader uses. Within one process each side opens
    /// the event by name, as another process would; across two, the peer's buffers set and wait on theirs.
    /// </summary>
    [TestClass]
    public class NamedEventTests
    {
        /// <summary>
        /// How long a wait must stay blocked before the test counts it as blocked.
        /// </summary>
        const int BlockedFor = 300;

        /// <summary>
        /// How long a released wait may take to return.
        /// </summary>
        const int ReleasedWithin = 5000;

        static readonly byte[] Data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        [TestMethod]
        public void AutoReset_SetWithNoWaiter_IsKeptUntilOneWaitTakesIt()
        {
            string name = Guid.NewGuid().ToString();
            using (var setter = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            using (var waiter = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            {
                Assert.IsTrue(setter.Set());
                Thread.Sleep(BlockedFor);

                Assert.IsTrue(waiter.WaitOne(0), "The Set was not kept.");
                Assert.IsFalse(waiter.WaitOne(0), "The first wait should have taken the Set.");
                Assert.IsFalse(setter.WaitOne(0), "The first wait should have taken the Set.");
            }
        }

        [TestMethod]
        public void AutoReset_Set_ReleasesOneOfTwoWaiters()
        {
            string name = Guid.NewGuid().ToString();
            using (var setter = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            using (var first = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            using (var second = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            {
                Task<bool>[] waits = { Wait(first, 20000), Wait(second, 20000) };
                AssertBlocked(waits, "Nothing has been set yet.");

                setter.Set();
                int released = Task.WaitAny(waits, ReleasedWithin);
                Assert.AreNotEqual(-1, released, "The Set released neither waiter.");
                Assert.IsTrue(waits[released].Result);
                Task<bool> other = waits[1 - released];
                Assert.IsFalse(other.Wait(BlockedFor), "One Set released both waiters.");

                setter.Set();
                Assert.IsTrue(other.Wait(ReleasedWithin), "The second Set did not release the other waiter.");
                Assert.IsTrue(other.Result);
                Assert.IsFalse(setter.WaitOne(0), "Each Set should have been taken by its waiter.");
            }
        }

        [TestMethod]
        public void ManualReset_Set_ReleasesEveryWaiterAndStaysSetUntilReset()
        {
            string name = Guid.NewGuid().ToString();
            using (var setter = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            using (var first = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            using (var second = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            using (var third = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            {
                Task<bool>[] waits = { Wait(first, 20000), Wait(second, 20000), Wait(third, 20000) };
                AssertBlocked(waits, "Nothing has been set yet.");

                setter.Set();
                Assert.IsTrue(Task.WaitAll(waits, ReleasedWithin), "The Set did not release every waiter.");
                Assert.IsTrue(waits.All(w => w.Result));
                Assert.IsTrue(first.WaitOne(0) && second.WaitOne(0) && setter.WaitOne(0), "The event should stay set.");

                setter.Reset();
                Assert.IsFalse(first.WaitOne(0), "The event should be reset.");
                Assert.IsFalse(setter.WaitOne(0), "The event should be reset.");
            }
        }

        /// <summary>
        /// Windows releases every thread waiting on a manual-reset event when it is set, even if it is reset before they
        /// run. A BufferWithLocks writer that releases its lock and takes it again at once relies on that to let a
        /// waiting reader through. A woken thread usually runs before the Reset, so the test repeats the Set and Reset.
        /// </summary>
        [TestMethod]
        public void ManualReset_SetThenResetAtOnce_ReleasesTheThreadsWaitingAtTheSet()
        {
            const int Rounds = 40;
            string name = Guid.NewGuid().ToString();
            using (var setter = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            using (var first = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            using (var second = TestEvent.CreateOrOpen(name, false, EventResetMode.ManualReset))
            {
                for (int round = 0; round < Rounds; round++)
                {
                    Task<bool>[] waits = { Wait(first, 20000), Wait(second, 20000) };
                    Thread.Sleep(round == 0 ? BlockedFor : 50);
                    Assert.IsFalse(waits.Any(w => w.IsCompleted), "Nothing has been set yet, round " + round + ".");

                    setter.Set();
                    setter.Reset();

                    Assert.IsTrue(Task.WaitAll(waits, ReleasedWithin), "A thread waiting at the Set was not released, round " + round + ".");
                    Assert.IsTrue(waits.All(w => w.Result));
                    Assert.IsFalse(setter.WaitOne(0), "The event should be reset.");
                }
            }
        }

        /// <summary>
        /// Windows timer ticks are 15.6 ms by default, so its waits may return up to a tick early (96.7 ms for 100 on
        /// Windows Server 2016, before this feature's change) or late. Linux and macOS have no tick.
        /// </summary>
        [TestMethod]
        [DataRow(EventResetMode.AutoReset)]
        [DataRow(EventResetMode.ManualReset)]
        public void WaitOne_NoSignal_ReturnsFalseAfterTheTimeout(EventResetMode mode)
        {
            const int Timeout = 100;
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            int early = windows ? 16 : 1;
            int late = windows ? 50 : 20;
            string name = Guid.NewGuid().ToString();
            using (var waiter = TestEvent.CreateOrOpen(name, false, mode))
            {
                var elapsed = new double[5];
                for (int i = 0; i < elapsed.Length; i++)
                {
                    var watch = Stopwatch.StartNew();
                    Assert.IsFalse(waiter.WaitOne(Timeout));
                    elapsed[i] = watch.Elapsed.TotalMilliseconds;
                }

                string all = string.Join(", ", elapsed.Select(e => e.ToString("F1")));
                Console.WriteLine("WaitOne(" + Timeout + ") returned after " + all + " ms");
                Assert.IsTrue(elapsed.All(e => e >= Timeout - early), "A wait returned before its timeout: " + all);
                Assert.IsTrue(elapsed.OrderBy(e => e).ElementAt(elapsed.Length / 2) <= Timeout + late, "The waits returned late: " + all);
            }
        }

        [TestMethod]
        public void CreateOrOpen_EventSomeoneHolds_KeepsItsCreatorsModeAndState()
        {
            string name = Guid.NewGuid().ToString();
            using (var creator = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            using (var opener = TestEvent.CreateOrOpen(name, true, EventResetMode.ManualReset))
            {
                Assert.IsFalse(opener.WaitOne(0), "The opener's initial state was used.");

                creator.Set();
                Assert.IsTrue(opener.WaitOne(0));
                Assert.IsFalse(opener.WaitOne(0), "The opener's reset mode was used.");
            }
        }

        [TestMethod]
        public void CreateOrOpen_AfterTheLastHolderCloses_StartsInTheNewCreatorsState()
        {
            string name = Guid.NewGuid().ToString();
            using (var first = TestEvent.CreateOrOpen(name, true, EventResetMode.ManualReset))
                first.Reset();
            using (var second = TestEvent.CreateOrOpen(name, true, EventResetMode.ManualReset))
                Assert.IsTrue(second.WaitOne(0), "The new event should start set.");

            using (var third = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            {
                Assert.IsFalse(third.WaitOne(0), "The new event should start reset.");
                third.Set();
            }
            using (var fourth = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            {
                Assert.IsFalse(fourth.WaitOne(0), "The new event should start reset.");
                fourth.Set();
                Assert.IsTrue(fourth.WaitOne(0));
                Assert.IsFalse(fourth.WaitOne(0), "The new event should reset itself.");
            }
        }

        [TestMethod]
        public void Disposed_WaitSetAndReset_ThrowObjectDisposedException()
        {
            string name = Guid.NewGuid().ToString();
            var disposed = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset);
            disposed.Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(() => disposed.WaitOne(0));
            Assert.ThrowsExactly<ObjectDisposedException>(() => disposed.Set());
            Assert.ThrowsExactly<ObjectDisposedException>(() => disposed.Reset());
        }

        /// <summary>
        /// A wait in progress keeps its event open, so disposing it from another thread neither breaks the wait nor
        /// stops a Set through another instance from releasing it.
        /// </summary>
        [TestMethod]
        public void DisposedWhileAThreadWaits_TheWaitGoesOnAndASetReleasesIt()
        {
            string name = Guid.NewGuid().ToString();
            using (var setter = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset))
            {
                var waiter = TestEvent.CreateOrOpen(name, false, EventResetMode.AutoReset);
                var wait = Wait(waiter, 20000);
                AssertBlocked(new[] { wait }, "Nothing has been set yet.");

                waiter.Dispose();
                Assert.IsFalse(wait.Wait(BlockedFor), "Disposing the event ended the wait.");

                setter.Set();
                Assert.IsTrue(wait.Wait(ReleasedWithin), "The Set did not release the wait.");
                Assert.IsTrue(wait.Result);
            }
        }

        /// <summary>
        /// Two threads hand a signal back and forth through two auto-reset events, each thread with its own instances.
        /// A Set that misses a waiter on its way to sleep leaves that waiter blocked until its timeout.
        /// </summary>
        [TestMethod]
        public void PingPong_ManyRoundTrips_NoWakeIsLost()
        {
            const int RoundTrips = 50000;
            string name = Guid.NewGuid().ToString();
            using (var ping = TestEvent.CreateOrOpen(name + "_ping", false, EventResetMode.AutoReset))
            using (var pong = TestEvent.CreateOrOpen(name + "_pong", false, EventResetMode.AutoReset))
            using (var otherPing = TestEvent.CreateOrOpen(name + "_ping", false, EventResetMode.AutoReset))
            using (var otherPong = TestEvent.CreateOrOpen(name + "_pong", false, EventResetMode.AutoReset))
            {
                var other = Task.Factory.StartNew(() =>
                {
                    for (int i = 0; i < RoundTrips; i++)
                    {
                        if (!otherPing.WaitOne(5000))
                            return i;
                        otherPong.Set();
                    }
                    return RoundTrips;
                }, TaskCreationOptions.LongRunning);

                var watch = Stopwatch.StartNew();
                int done = 0;
                for (; done < RoundTrips; done++)
                {
                    ping.Set();
                    if (!pong.WaitOne(5000))
                        break;
                }
                watch.Stop();

                Assert.IsTrue(other.Wait(10000), "The other thread did not finish.");
                Console.WriteLine(done + " round trips, " + (watch.Elapsed.TotalMilliseconds * 1000 / Math.Max(done, 1)).ToString("F2") + " us each");
                Assert.AreEqual(RoundTrips, done, "A wake was lost at round trip " + done + ".");
                Assert.AreEqual(RoundTrips, other.Result, "A wake was lost at round trip " + other.Result + " on the other thread.");
            }
        }

        [TestMethod]
        public void AutoReset_SetInOtherProcessWithNoWaiter_IsKeptUntilAWaitHereTakesIt()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create circular " + name + " 4 64");
                using (var dataExists = TestEvent.CreateOrOpen(name + "_evt_dataexists", false, EventResetMode.AutoReset))
                {
                    // A write that catches up with the reader sets DataExists.
                    peer.Ok("write " + PeerProtocol.ToHex(Data));
                    Thread.Sleep(BlockedFor);

                    Assert.IsTrue(dataExists.WaitOne(0), "The peer's Set was not kept.");
                    Assert.IsFalse(dataExists.WaitOne(0), "The first wait should have taken the Set.");
                }
                ClosePeersBuffer(peer);
            }
        }

        [TestMethod]
        public void AutoReset_SetInOtherProcess_ReleasesOneOfTwoWaitersHere()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create circular " + name + " 4 64");
                using (var first = TestEvent.CreateOrOpen(name + "_evt_dataexists", false, EventResetMode.AutoReset))
                using (var second = TestEvent.CreateOrOpen(name + "_evt_dataexists", false, EventResetMode.AutoReset))
                {
                    Task<bool>[] waits = { Wait(first, 20000), Wait(second, 20000) };
                    AssertBlocked(waits, "Nothing has been written yet.");

                    peer.Ok("write " + PeerProtocol.ToHex(Data));
                    int released = Task.WaitAny(waits, ReleasedWithin);
                    Assert.AreNotEqual(-1, released, "The peer's Set released neither waiter.");
                    Task<bool> other = waits[1 - released];
                    Assert.IsFalse(other.Wait(BlockedFor), "One Set released both waiters.");

                    first.Set();
                    Assert.IsTrue(other.Wait(ReleasedWithin), "The second Set did not release the other waiter.");
                    Assert.IsTrue(waits.All(w => w.Result));
                }
                ClosePeersBuffer(peer);
            }
        }

        [TestMethod]
        public void ManualReset_SetInOtherProcess_ReleasesEveryWaiterHereAndStaysSetUntilReset()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create readwrite " + name + " 1024");
                using (var first = TestEvent.CreateOrOpen(name + "_evt_read", true, EventResetMode.ManualReset))
                using (var second = TestEvent.CreateOrOpen(name + "_evt_read", true, EventResetMode.ManualReset))
                using (var third = TestEvent.CreateOrOpen(name + "_evt_read", true, EventResetMode.ManualReset))
                {
                    // Taking the write lock resets the read event; releasing it sets it.
                    peer.Ok("lock write");
                    Task<bool>[] waits = { Wait(first, 20000), Wait(second, 20000), Wait(third, 20000) };
                    AssertBlocked(waits, "The peer holds the write lock.");

                    peer.Ok("unlock write");
                    Assert.IsTrue(Task.WaitAll(waits, ReleasedWithin), "The peer's Set did not release every waiter.");
                    Assert.IsTrue(waits.All(w => w.Result));
                    Assert.IsTrue(first.WaitOne(0) && second.WaitOne(0), "The event should stay set.");

                    peer.Ok("lock write");
                    Assert.IsFalse(first.WaitOne(0), "The peer's Reset did not reach this process.");
                }
                ClosePeersBuffer(peer);
            }
        }

        [TestMethod]
        public void Waiter_InOtherProcess_IsReleasedBySetHere()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create readwrite " + name + " 1024");
                using (var consumer = new BufferReadWrite(name))
                {
                    // Taking the read lock resets the write event, so the peer's write lock waits for it.
                    Assert.IsTrue(consumer.AcquireReadLock(0));
                    var locked = Task.Factory.StartNew(() => peer.Send("lock write", 20000), TaskCreationOptions.LongRunning);
                    Assert.IsFalse(locked.Wait(BlockedFor), "The peer took the write lock while this process held the read lock.");

                    consumer.ReleaseReadLock();
                    Assert.IsTrue(locked.Wait(ReleasedWithin), "The peer's wait was not released.");
                    Assert.AreEqual("ok", locked.Result);
                }
                ClosePeersBuffer(peer);
            }
        }

        /// <summary>
        /// When every holder of an event has gone, Windows destroys it, so a new buffer under the name starts with new
        /// events in their creator's state, whatever state a killed holder left them in.
        /// </summary>
        [TestMethod]
        public void BufferWithLocks_EventsOfKilledHolder_NewOwnerStartsWithNewEvents()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create readwrite " + name + " 1024");
                // Taking the write lock resets the read event.
                peer.Ok("lock write");
                peer.Kill();
            }

            using (var owner = new BufferReadWrite(name, 1024))
            {
                var data = new byte[Data.Length];
                owner.Read(data);
                owner.Write(Data);
                owner.Read(data);
                CollectionAssert.AreEqual(Data, data);
            }
        }

        [TestMethod]
        public void CircularBuffer_EventsOfKilledHolder_NewOwnerStartsWithNewEvents()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create circular " + name + " 4 64");
                // A write that catches up with the reader sets DataExists, and nobody takes it.
                peer.Ok("write " + PeerProtocol.ToHex(Data));
                peer.Kill();
            }

            using (var owner = new CircularBuffer(name, 4, 64))
            using (var dataExists = TestEvent.CreateOrOpen(name + "_evt_dataexists", false, EventResetMode.AutoReset))
            {
                Assert.IsFalse(dataExists.WaitOne(0), "DataExists kept the killed holder's Set.");
            }
        }

        [TestMethod]
        public void BufferWithLocks_EventNameHeldByABuffer_ThrowsWaitHandleCannotBeOpenedException()
        {
            string name = Guid.NewGuid().ToString();
            using (var buffer = new EventFreeBuffer(name + "_evt_write", 1024))
            {
                buffer.WriteBytes(Data);

                Assert.ThrowsExactly<WaitHandleCannotBeOpenedException>(() => new BufferReadWrite(name, 1024));

                CollectionAssert.AreEqual(Data, buffer.ReadBytes(Data.Length));
            }
        }

        [TestMethod]
        public void Buffer_NameHeldByAnEvent_ThrowsIOException()
        {
            string name = Guid.NewGuid().ToString();
            using (var locks = new BufferReadWrite(name, 1024))
            {
                Assert.ThrowsExactly<IOException>(() => new EventFreeBuffer(name + "_evt_write", 1024));

                Assert.IsTrue(locks.AcquireWriteLock(0), "The write event was changed.");
                locks.ReleaseWriteLock();
            }
        }

        /// <summary>
        /// A waiting thread uses no CPU: a reader blocked for 5 s adds under 50 ms of process CPU time. On .NET the runtime
        /// compiles hot methods again in the background for a few seconds after they first run (80 ms of CPU time during
        /// the 5 s, on that thread alone, when this test ran first), so the test waits for that to stop before it measures.
        /// </summary>
        [TestMethod]
        public void CircularBuffer_ReaderBlockedFor5s_UsesUnder50msOfCpu()
        {
            string name = Guid.NewGuid().ToString();
            using (var buffer = new CircularBuffer(name, 4, 64))
            {
                var data = new byte[64];
                Assert.AreEqual(0, buffer.Read(data, 0, 10), "The buffer should be empty.");
                // Its first call compiles what it calls; do that before the measurement.
                ProcessorTime();
                string settled = WaitForBackgroundCompilation();

                TimeSpan before = ProcessorTime();
                var watch = Stopwatch.StartNew();
                Assert.AreEqual(0, buffer.Read(data, 0, 5000));
                watch.Stop();
                TimeSpan used = ProcessorTime() - before;

                Console.WriteLine(settled + "; blocked " + watch.ElapsedMilliseconds + " ms, process CPU time " + used.TotalMilliseconds.ToString("F1") + " ms" + CompiledSince());
                Assert.IsTrue(watch.ElapsedMilliseconds >= 4990, "The read returned after " + watch.ElapsedMilliseconds + " ms.");
                Assert.IsTrue(used.TotalMilliseconds < 50, "The process used " + used.TotalMilliseconds + " ms of CPU time.");
            }
        }

#if NET
        static long compiledBefore;
#endif

        /// <summary>
        /// Waits, up to 30 s, until the runtime has compiled no method for 1 s. .NET Framework has no background compilation.
        /// </summary>
        static string WaitForBackgroundCompilation()
        {
#if NET
            var watch = Stopwatch.StartNew();
            long count = System.Runtime.JitInfo.GetCompiledMethodCount();
            for (var quiet = Stopwatch.StartNew(); quiet.ElapsedMilliseconds < 1000 && watch.ElapsedMilliseconds < 30000; )
            {
                Thread.Sleep(100);
                long now = System.Runtime.JitInfo.GetCompiledMethodCount();
                if (now != count)
                {
                    count = now;
                    quiet.Restart();
                }
            }
            compiledBefore = System.Runtime.JitInfo.GetCompiledMethodCount();
            return "waited " + watch.ElapsedMilliseconds + " ms for background compilation to stop";
#else
            return "no background compilation";
#endif
        }

        static string CompiledSince()
        {
#if NET
            return ", " + (System.Runtime.JitInfo.GetCompiledMethodCount() - compiledBefore) + " methods compiled meanwhile";
#else
            return "";
#endif
        }

        /// <summary>
        /// Disposing a peer kills it, and on Linux a killed holder's files stay in /dev/shm until the name is used again.
        /// </summary>
        static void ClosePeersBuffer(PeerProcess peer)
        {
            peer.Ok("close");
        }

        static TimeSpan ProcessorTime()
        {
            using (var self = Process.GetCurrentProcess())
                return self.TotalProcessorTime;
        }

        static Task<bool> Wait(TestEvent waiter, int timeout)
        {
            return Task.Factory.StartNew(() => waiter.WaitOne(timeout), TaskCreationOptions.LongRunning);
        }

        static void AssertBlocked(Task<bool>[] waits, string because)
        {
            Thread.Sleep(BlockedFor);
            Assert.IsFalse(waits.Any(w => w.IsCompleted), because);
        }
    }
}
