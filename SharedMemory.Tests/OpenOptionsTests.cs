using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;
#if NET
using System.Runtime.Versioning;
#endif

namespace SharedMemoryTests
{
    /// <summary>
    /// The options types and the constructors that take them: their defaults, their checks, what the defaults make the new
    /// constructors do (what today's constructors do), and an open that waits, within <see cref="SharedMemoryOptions.OpenTimeout"/>,
    /// for a name to appear and for its owner to finish, cancelled by <c>OpenCancellationToken</c>. The rows run for
    /// <see cref="BufferReadWrite"/>, <see cref="SharedArray{T}"/> and <see cref="CircularBuffer"/>, and through the protected
    /// constructors for subclasses of <see cref="SharedBuffer"/> and <see cref="BufferWithLocks"/>.
    /// </summary>
    [TestClass]
    public class OpenOptionsTests
    {
        const int Size = 1024;
        const int NodeCount = 4;
        const int NodeSize = 64;

        /// <summary>
        /// The bytes compared between two creates: the 16-byte shared header and what follows it, a ring's node header included.
        /// </summary>
        const int Snapshot = 64;

        const string ReadWrite = "BufferReadWrite";
        const string IntArray = "SharedArray<int>";
        const string Ring = "CircularBuffer";
        const string Plain = "SharedBuffer";
        const string Locked = "BufferWithLocks";

        // A layout marker of version 1 for each kind, without the ready flag, and the flag
        const int ReadWriteNotReady = 0x00000011;
        const int ArrayNotReady = 0x00000021;
        const int RingNotReady = 0x00000031;
        const int Ready = 0x00001000;

        #region Options and constructors

        /// <summary>
        /// A new options object of each type reads CreateNew, no timeout and no token.
        /// </summary>
        [TestMethod]
        public void Options_Defaults()
        {
            foreach (SharedMemoryOptions options in new[] { new SharedMemoryOptions(), new CircularBufferOptions() })
            {
                string type = options.GetType().Name;
                Assert.AreEqual(SharedMemoryOpenMode.CreateNew, options.OpenMode, type + ".OpenMode");
                Assert.IsNull(options.OpenTimeout, type + ".OpenTimeout");
#if !LIBRARY_NET35
                Assert.AreEqual(CancellationToken.None, options.OpenCancellationToken, type + ".OpenCancellationToken");
#endif
            }
            Assert.AreEqual(0, (int)SharedMemoryOpenMode.CreateNew);
            Assert.AreEqual(1, (int)SharedMemoryOpenMode.CreateOrOpen);
        }

        /// <summary>
        /// An instance keeps the options its constructor read: after the options object changes, an instance that opens again
        /// opens as it was told, while a new instance takes the new values. The creator's mode stays CreateNew, so its second open
        /// of a name another instance holds throws IOException; the opener's timeout stays 2 s, so its second open waits for a
        /// creator 300 ms later, where a new opener with a timeout of zero throws at once. A cancelled token put in the options
        /// after construction stops neither second open.
        /// </summary>
        [TestMethod]
        [DataRow(Plain)]
        [DataRow(Locked)]
        public void Options_ChangedAfterConstruction_ChangeNothing(string baseClass)
        {
            // Two names: the .NET Framework 3.5 build's failed create keeps the name until a collection, so the creator's name is
            // not the one the opener waits for
            string name = Guid.NewGuid().ToString();
            string waitedFor = Guid.NewGuid().ToString();
            var options = new SharedMemoryOptions { OpenTimeout = TimeSpan.FromSeconds(2) };
            using (var creator = Subclass.Make(baseClass, name, true, options))
            using (var holder = Subclass.Make(baseClass, name, false, options))
            using (var openerCreator = Subclass.Make(baseClass, waitedFor, true, options))
            using (var opener = Subclass.Make(baseClass, waitedFor, false, options))
            {
                options.OpenMode = SharedMemoryOpenMode.CreateOrOpen;
                options.OpenTimeout = TimeSpan.Zero;
#if !LIBRARY_NET35
                options.OpenCancellationToken = new CancellationToken(true);
#endif

                Assert.ThrowsExactly<IOException>(() => creator.Reopen(), baseClass + ": the creator's second open, with CreateNew");
                openerCreator.Dispose();

                Task reopening = Task.Factory.StartNew(() => opener.Reopen(), TaskCreationOptions.LongRunning);
                Thread.Sleep(300);
                Assert.IsFalse(reopening.IsCompleted, baseClass + ": the opener's second open should still be waiting for the name, but " + Describe(reopening));
                using (var later = Create(ReadWrite, waitedFor, new SharedMemoryOptions()))
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => reopening.IsCompleted, 5000), baseClass + ": the opener's second open did not return.");
                    Assert.AreEqual(TaskStatus.RanToCompletion, reopening.Status, baseClass + ": the opener's second open should have opened, but " + Describe(reopening));
                    Assert.IsFalse(opener.IsOwnerOfSharedMemory, baseClass + ": the reopened opener's IsOwnerOfSharedMemory");
                    Assert.AreEqual(later.BufferSize, opener.BufferSize, baseClass + ": the reopened opener's BufferSize");
                }
            }

#if !LIBRARY_NET35
            options.OpenCancellationToken = CancellationToken.None;
#endif
            string missing = Guid.NewGuid().ToString();
            var watch = Stopwatch.StartNew();
            Assert.ThrowsExactly<FileNotFoundException>(() => Subclass.Make(baseClass, missing, false, options).Dispose());
            Assert.IsTrue(watch.ElapsedMilliseconds < 500, baseClass + ": a new opener with the changed timeout of zero gave up after " + watch.ElapsedMilliseconds + " ms");
        }

        /// <summary>
        /// Each constructor that takes options throws ArgumentNullException for null, naming the parameter, and makes nothing.
        /// </summary>
        [TestMethod]
        [DataRow("BufferReadWrite(name, bufferSize, options)")]
        [DataRow("BufferReadWrite(name, options)")]
        [DataRow("SharedArray<int>(name, length, options)")]
        [DataRow("SharedArray<int>(name, options)")]
        [DataRow("CircularBuffer(name, nodeCount, nodeBufferSize, options)")]
        [DataRow("CircularBuffer(name, options)")]
        [DataRow("SharedBuffer(name, bufferSize, true, options)")]
        [DataRow("SharedBuffer(name, 0, false, options)")]
        [DataRow("BufferWithLocks(name, bufferSize, true, options)")]
        [DataRow("BufferWithLocks(name, 0, false, options)")]
        public void Constructor_NullOptions_Throws(string constructor)
        {
            string name = Guid.NewGuid().ToString();

            var e = Assert.ThrowsExactly<ArgumentNullException>(() => Construct(constructor, name, null).Dispose());

            Assert.AreEqual("options", e.ParamName);
            CollectionAssert.AreEqual(new string[0], Left(name), constructor + ": what is left under the name");
        }

        /// <summary>
        /// An undefined open mode, and an open timeout below zero other than infinite or above Int32.MaxValue milliseconds, throw
        /// ArgumentOutOfRangeException naming the property, from each constructor that takes options, before it makes anything.
        /// </summary>
        [TestMethod]
        [DataRow("OpenMode 7")]
        [DataRow("OpenTimeout -2 ms")]
        [DataRow("OpenTimeout -1.5 ms")]
        [DataRow("OpenTimeout -0.5 ms")]
        [DataRow("OpenTimeout Int32.MaxValue + 1 ms")]
        [DataRow("OpenTimeout Int32.MaxValue ms + 1 tick")]
        public void Constructor_OptionsOutOfRange_Throw(string option)
        {
            foreach (string constructor in Constructors)
            {
                string name = Guid.NewGuid().ToString();
                SharedMemoryOptions options = OptionsFor(constructor);
                if (option == "OpenMode 7")
                    options.OpenMode = (SharedMemoryOpenMode)7;
                else if (option == "OpenTimeout -2 ms")
                    options.OpenTimeout = TimeSpan.FromTicks(-2 * TimeSpan.TicksPerMillisecond);
                else if (option == "OpenTimeout -1.5 ms")
                    options.OpenTimeout = TimeSpan.FromTicks(-3 * TimeSpan.TicksPerMillisecond / 2);
                else if (option == "OpenTimeout -0.5 ms")
                    options.OpenTimeout = TimeSpan.FromTicks(-TimeSpan.TicksPerMillisecond / 2);
                else if (option == "OpenTimeout Int32.MaxValue ms + 1 tick")
                    options.OpenTimeout = TimeSpan.FromTicks(int.MaxValue * TimeSpan.TicksPerMillisecond + 1);
                else
                    options.OpenTimeout = TimeSpan.FromTicks((int.MaxValue + 1L) * TimeSpan.TicksPerMillisecond);

                var e = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Construct(constructor, name, options).Dispose(), constructor);

                StringAssert.Contains(e.Message, option.Split(' ')[0], constructor);
                Assert.AreEqual("options", e.ParamName, constructor);
                CollectionAssert.AreEqual(new string[0], Left(name), constructor + ": what is left under the name");
            }
        }

        /// <summary>
        /// The open timeout's limits are accepted: infinite (-1 ms), zero and Int32.MaxValue milliseconds. A creator creates, and an
        /// opener of the buffer it made opens at once.
        /// </summary>
        [TestMethod]
        [DataRow(-1)]
        [DataRow(0)]
        [DataRow(int.MaxValue)]
        public void Constructor_OpenTimeoutLimits_Accepted(int milliseconds)
        {
            foreach (string kind in new[] { ReadWrite, IntArray, Ring })
            {
                string name = Guid.NewGuid().ToString();
                SharedMemoryOptions options = Options(kind);
                options.OpenTimeout = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
                using (var creator = Create(kind, name, options))
                {
                    var watch = Stopwatch.StartNew();
                    using (var opener = Open(kind, name, options))
                    {
                        Assert.IsTrue(creator.IsOwnerOfSharedMemory, kind + ": the creator");
                        Assert.IsFalse(opener.IsOwnerOfSharedMemory, kind + ": the opener");
                        Assert.IsTrue(watch.ElapsedMilliseconds < 1000, kind + ": the opener took " + watch.ElapsedMilliseconds + " ms");
                    }
                }
            }
        }

        /// <summary>
        /// The new constructors with default options do what today's do: a create writes the same header and node header; a create
        /// of a name in use throws IOException, whichever constructor holds the name; an opener is neither owner nor creator and
        /// has the creator's size; an open of a missing name throws FileNotFoundException at once; and an open of a buffer whose
        /// owner never finishes gives up after 1 s with "is not ready". CreatedSharedMemory is true for each creator only.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void NewConstructors_DefaultOptions_BehaveAsTodays(string kind)
        {
            string name = Guid.NewGuid().ToString();
            int[] today, withOptions;
            using (var creator = CreateToday(kind, name))
            {
                AssertCreator(creator, kind + ", today's constructor");
                today = Words(name);
            }
            using (var creator = Create(kind, name, Options(kind)))
            {
                AssertCreator(creator, kind + ", with default options");
                withOptions = Words(name);
            }
            CollectionAssert.AreEqual(today, withOptions, kind + ": the first " + Snapshot + " bytes after each create");

            // A new name after each failed create: the .NET Framework 3.5 build's keeps the name until a collection
            name = Guid.NewGuid().ToString();
            using (CreateToday(kind, name))
                Assert.ThrowsExactly<IOException>(() => Create(kind, name, Options(kind)), kind + ": a create with default options of a name in use");
            name = Guid.NewGuid().ToString();
            using (var creator = Create(kind, name, Options(kind)))
            {
                Assert.ThrowsExactly<IOException>(() => CreateToday(kind, name), kind + ": today's create of a name in use");

                using (var opener = OpenToday(kind, name))
                using (var openerWithOptions = Open(kind, name, Options(kind)))
                {
                    AssertOpener(opener, creator, kind + ", today's constructor");
                    AssertOpener(openerWithOptions, creator, kind + ", with default options");
                }
            }

            string missing = Guid.NewGuid().ToString();
            Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, missing, Options(kind)).Dispose(), "warming up");
            long ms = Elapsed(() => Assert.ThrowsExactly<FileNotFoundException>(() => OpenToday(kind, missing).Dispose()));
            long msWithOptions = Elapsed(() => Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, missing, Options(kind)).Dispose()));
            Assert.IsTrue(ms < 50 && msWithOptions < 50, kind + ": an open of a missing name took " + ms + " ms today, and " + msWithOptions + " ms with default options");

            name = Guid.NewGuid().ToString();
            using (CreateToday(kind, name))
            {
                using (var raw = new RawHeader(name))
                    raw.Marker = NotReady(kind);
                foreach (bool options in new[] { false, true })
                {
                    InvalidOperationException e = LayoutMarkerTests.Refused(() => options ? Open(kind, name, Options(kind)) : OpenToday(kind, name), out ms);
                    StringAssert.Contains(e.Message, name);
                    StringAssert.Contains(e.Message, "is not ready");
                    StringAssert.Contains(e.Message, "after waiting 1 s");
                    Assert.IsTrue(ms >= 1000 && ms <= 1500, kind + (options ? ", with default options" : ", today's constructor") + ": gave up after " + ms + " ms");
                }
            }
        }

        /// <summary>
        /// The characterised opener, through the name-only constructor with default options: it writes nothing shared, so the
        /// header and the bytes after it, a seeded pattern or a ring's moved cursors, are the same once it has opened and closed;
        /// and of a buffer whose owner closed, it sees ShuttingDown and Shutdown stays 1.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void NewConstructors_DefaultOptions_OpenerLeavesTheHeaderBytes(string kind)
        {
            string name = Guid.NewGuid().ToString();
            SharedBuffer creator = CreateToday(kind, name);
            using (creator)
            {
                var pattern = new int[(Snapshot - 16) / sizeof(int)];
                for (int i = 0; i < pattern.Length; i++)
                    pattern[i] = 0x5A000001 + i * 0x00010101;
                if (kind == Ring)
                {
                    var ring = (CircularBuffer)creator;
                    Assert.AreEqual(8, ring.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
                    Assert.AreEqual(8, ring.Write(new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 }));
                    Assert.AreEqual(8, ring.Read(new byte[NodeSize]));
                }
                else if (kind == IntArray)
                    ((SharedArray<int>)creator).Write(pattern);
                else
                    ((BufferReadWrite)creator).Write(pattern);

                int[] before = Words(name);
                using (Open(kind, name, Options(kind)))
                    CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes once the opener is open");
                CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes once the opener has closed");

                using (OpenToday(kind, name))
                {
                    creator.Dispose();
                    before = Words(name);
                    Assert.AreEqual(1, before[2], kind + ": Shutdown, bytes 8 to 11, once the owner has closed");
                    using (var opener = Open(kind, name, Options(kind)))
                    {
                        Assert.IsTrue(opener.ShuttingDown, kind + ": the opener's ShuttingDown");
                        CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes once the opener of the closed buffer is open");
                    }
                    CollectionAssert.AreEqual(before, Words(name), kind + ": the first " + Snapshot + " bytes once the opener of the closed buffer has closed");
                }
            }
        }

        /// <summary>
        /// The constructors that take only a name ignore the open mode: with CreateOrOpen, a missing name throws
        /// FileNotFoundException and nothing is made, and an existing buffer is opened, its opener neither owner nor creator.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void OpenConstructor_IgnoresOpenMode(string kind)
        {
            string name = Guid.NewGuid().ToString();
            SharedMemoryOptions options = Options(kind);
            options.OpenMode = SharedMemoryOpenMode.CreateOrOpen;

            Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name, options).Dispose());
            CollectionAssert.AreEqual(new string[0], Left(name), kind + ": what is left under the name");

            using (var creator = CreateToday(kind, name))
            using (var opener = Open(kind, name, options))
                AssertOpener(opener, creator, kind);
        }

        /// <summary>
        /// A subclass of SharedBuffer and of BufferWithLocks, through the protected constructors that take options, creates and
        /// opens as the library's classes do: the creator is owner and creator, inside DoOpen too; an opener with CreateOrOpen,
        /// which an opener ignores, is neither; a create of a name in use throws IOException; and an opener with a timeout waits
        /// for a missing name, then throws FileNotFoundException and leaves nothing.
        /// </summary>
        [TestMethod]
        [DataRow(Plain)]
        [DataRow(Locked)]
        public void ProtectedConstructors_WithOptions(string baseClass)
        {
            string name = Guid.NewGuid().ToString();
            var options = new SharedMemoryOptions { OpenTimeout = TimeSpan.FromMilliseconds(200) };
            var openOrCreate = new SharedMemoryOptions { OpenMode = SharedMemoryOpenMode.CreateOrOpen, OpenTimeout = TimeSpan.FromMilliseconds(200) };
            using (var creator = Subclass.Make(baseClass, name, true, options))
            using (var opener = Subclass.Make(baseClass, name, false, openOrCreate))
            {
                Assert.IsTrue(creator.IsOwnerOfSharedMemory && creator.CreatedSharedMemory, baseClass + ": the creator's IsOwnerOfSharedMemory and CreatedSharedMemory");
                Assert.AreEqual(1, creator.DoOpenCalls, baseClass + ": the creator's DoOpen calls");
                Assert.IsTrue(creator.OwnerInDoOpen && creator.CreatedInDoOpen, baseClass + ": the creator's IsOwnerOfSharedMemory and CreatedSharedMemory in DoOpen");
                Assert.IsFalse(opener.IsOwnerOfSharedMemory || opener.CreatedSharedMemory, baseClass + ": the opener's IsOwnerOfSharedMemory and CreatedSharedMemory");
                Assert.AreEqual(1, opener.DoOpenCalls, baseClass + ": the opener's DoOpen calls");
                Assert.IsFalse(opener.OwnerInDoOpen || opener.CreatedInDoOpen, baseClass + ": the opener's IsOwnerOfSharedMemory and CreatedSharedMemory in DoOpen");
                Assert.AreEqual(creator.BufferSize, opener.BufferSize, baseClass + ": the opener's BufferSize");

                Assert.ThrowsExactly<IOException>(() => Subclass.Make(baseClass, name, true, options).Dispose(), baseClass + ": a create of a name in use");
            }

            string missing = Guid.NewGuid().ToString();
            long ms = Elapsed(() => Assert.ThrowsExactly<FileNotFoundException>(() => Subclass.Make(baseClass, missing, false, options).Dispose()));
            Assert.IsTrue(ms >= 200 && ms <= 400, baseClass + ": an opener of a missing name with 200 ms gave up after " + ms + " ms");
            CollectionAssert.AreEqual(new string[0], Left(missing), baseClass + ": what is left under the missing name");
        }

        #endregion

        #region The open wait and its token

        /// <summary>
        /// An opener with a timeout of 2 s, of a name that another process creates 300 ms later, opens the buffer within 250 ms of
        /// its creator returning, which it does once the buffer is ready: soon after, not at the timeout. It looks every 16 ms at most;
        /// the bound allows for a loaded CI runner.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void Open_WithTimeout_NameCreatedLater_Opens(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                SharedMemoryOptions options = Options(kind);
                options.OpenTimeout = TimeSpan.FromSeconds(2);
                var clock = Stopwatch.StartNew();
                long openedAt = -1;
                Task<SharedBuffer> opening = Task.Factory.StartNew(() =>
                {
                    SharedBuffer opened = Open(kind, name, options);
                    openedAt = clock.ElapsedMilliseconds;
                    return opened;
                }, TaskCreationOptions.LongRunning);
                try
                {
                    Thread.Sleep(300);
                    Assert.IsFalse(opening.IsCompleted, kind + ": the opener should still be waiting for the name, but " + Describe(opening));
                    peer.Ok(PeerCreate(kind, name));
                    long createdAt = clock.ElapsedMilliseconds;

                    Assert.IsTrue(SpinWait.SpinUntil(() => opening.IsCompleted, 5000), kind + ": the opener did not return.");
                    Assert.AreEqual(TaskStatus.RanToCompletion, opening.Status, kind + ": the opener should have opened, but " + Describe(opening));
                    Assert.IsTrue(openedAt - createdAt <= 250, kind + ": the opener opened " + (openedAt - createdAt) + " ms after the creator returned, at " + createdAt + " ms");
                    Assert.AreEqual(KindSize(kind), opening.Result.BufferSize, kind + ": the opener's BufferSize");
                    Assert.IsFalse(opening.Result.IsOwnerOfSharedMemory || opening.Result.CreatedSharedMemory, kind + ": the opener's IsOwnerOfSharedMemory and CreatedSharedMemory");
                }
                finally
                {
                    DisposeOpened(opening);
                }
                peer.Ok("close");
            }
        }

        /// <summary>
        /// An opener with a timeout of 500 ms, of a name nobody creates, throws FileNotFoundException naming the buffer and the wait
        /// after 0.5 to 0.7 s, and leaves nothing under the name.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void Open_WithTimeout_NoCreator_ThrowsFileNotFound(string kind)
        {
            string name = Guid.NewGuid().ToString();
            SharedMemoryOptions options = Options(kind);
            options.OpenTimeout = TimeSpan.FromMilliseconds(500);

            FileNotFoundException e = null;
            long ms = Elapsed(() => e = Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name, options).Dispose()));

            Assert.IsTrue(ms >= 500 && ms <= 700, kind + ": gave up after " + ms + " ms");
            StringAssert.Contains(e.Message, "\"" + name + "\"");
            StringAssert.Contains(e.Message, "after waiting 500 ms");
            CollectionAssert.AreEqual(new string[0], Left(name), kind + ": what is left under the name");
        }

        /// <summary>
        /// An opener with a timeout of zero, of a missing name, throws FileNotFoundException within 50 ms.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void Open_WithZeroTimeout_MissingName_ThrowsAtOnce(string kind)
        {
            SharedMemoryOptions options = Options(kind);
            options.OpenTimeout = TimeSpan.Zero;
            Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, Guid.NewGuid().ToString(), options).Dispose(), "warming up");

            string name = Guid.NewGuid().ToString();
            FileNotFoundException e = null;
            long ms = Elapsed(() => e = Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name, options).Dispose()));

            Assert.IsTrue(ms < 50, kind + ": gave up after " + ms + " ms");
            StringAssert.Contains(e.Message, "\"" + name + "\"");
            StringAssert.Contains(e.Message, "after waiting 0 ms");
        }

        /// <summary>
        /// An opener with a timeout of 300 ms, of a buffer whose owner never sets the ready flag, throws InvalidOperationException
        /// "is not ready", naming the buffer and the wait, after 0.3 to 0.5 s.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void Open_WithTimeout_NeverReady_ThrowsNotReady(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (CreateToday(kind, name))
            {
                using (var raw = new RawHeader(name))
                    raw.Marker = NotReady(kind);
                SharedMemoryOptions options = Options(kind);
                options.OpenTimeout = TimeSpan.FromMilliseconds(300);

                long ms;
                InvalidOperationException e = LayoutMarkerTests.Refused(() => Open(kind, name, options), out ms);

                StringAssert.Contains(e.Message, "\"" + name + "\"");
                StringAssert.Contains(e.Message, "is not ready");
                StringAssert.Contains(e.Message, "after waiting 300 ms");
                Assert.IsTrue(ms >= 300 && ms <= 500, kind + ": gave up after " + ms + " ms");
            }
        }

        /// <summary>
        /// A buffer whose owner never sets the ready flag, with a timeout of zero or 300 ms: create-or-open's attach, and with zero
        /// the name-only constructor too, throw InvalidOperationException "is not ready" naming the buffer and the wait, at once or
        /// after 0.3 to 0.5 s, and leave the buffer as it was.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite, "CreateOrOpen", 0)]
        [DataRow(IntArray, "CreateOrOpen", 0)]
        [DataRow(Ring, "CreateOrOpen", 0)]
        [DataRow(ReadWrite, "CreateOrOpen", 300)]
        [DataRow(IntArray, "CreateOrOpen", 300)]
        [DataRow(Ring, "CreateOrOpen", 300)]
        [DataRow(ReadWrite, "open", 0)]
        [DataRow(IntArray, "open", 0)]
        [DataRow(Ring, "open", 0)]
        public void NeverReady_WithTimeout_ThrowsNotReady(string kind, string how, int timeout)
        {
            string name = Guid.NewGuid().ToString();
            using (CreateToday(kind, name))
            {
                using (var raw = new RawHeader(name))
                    raw.Marker = NotReady(kind);
                int[] before = Words(name);
                string[] left = Left(name);
                SharedMemoryOptions options = Options(kind);
                options.OpenTimeout = TimeSpan.FromMilliseconds(timeout);
                if (how == "CreateOrOpen")
                    options.OpenMode = SharedMemoryOpenMode.CreateOrOpen;
                Func<IDisposable> open = () => how == "open" ? Open(kind, name, options) : Create(kind, name, options);

                long ms;
                LayoutMarkerTests.Refused(open, out ms);
                InvalidOperationException e = LayoutMarkerTests.Refused(open, out ms);

                StringAssert.Contains(e.Message, "\"" + name + "\"");
                StringAssert.Contains(e.Message, "is not ready");
                StringAssert.Contains(e.Message, "after waiting " + timeout + " ms");
                Assert.IsTrue(timeout == 0 ? ms < 100 : ms >= 300 && ms <= 500, kind + ", " + how + ": gave up after " + ms + " ms");
                CollectionAssert.AreEqual(before, Words(name), kind + ", " + how + ": the buffer's header changed.");
                CollectionAssert.AreEquivalent(left, Left(name), kind + ", " + how + ": what is under the name changed.");
            }
        }

        /// <summary>
        /// A timeout shorter than 1 s is the whole wait for the owner, wherever the opener waits for it: for the ready flag, for the
        /// size of a buffer without a marker (SharedMemory 2.3.x), and for the node count of such a ring. With 200 ms, the opener
        /// gives up after 0.2 to 0.4 s, not after 1 s.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite + ", the ready flag")]
        [DataRow(Ring + ", the ready flag")]
        [DataRow(ReadWrite + " without a marker, its size")]
        [DataRow(Ring + " without a marker, its node count")]
        public void Open_WithTimeout_ShortBudgetForReady(string waitFor)
        {
            string kind = waitFor.StartsWith(Ring, StringComparison.Ordinal) ? Ring : ReadWrite;
            string name = Guid.NewGuid().ToString();
            using (CreateToday(kind, name))
            {
                // A ring's node count is the node header's fifth int, after the 16-byte shared header
                using (var raw = new RawHeader(name, 40))
                {
                    if (waitFor.EndsWith("the ready flag", StringComparison.Ordinal))
                        raw.Marker = NotReady(kind);
                    else
                    {
                        raw.Marker = 0;
                        if (kind == Ring)
                            raw.WriteInt32(32, 0);
                        else
                            raw.Size = 0;
                    }
                }
                SharedMemoryOptions options = Options(kind);
                options.OpenTimeout = TimeSpan.FromMilliseconds(200);

                long ms;
                InvalidOperationException e = LayoutMarkerTests.Refused(() => Open(kind, name, options), out ms);

                StringAssert.Contains(e.Message, "is not ready");
                StringAssert.Contains(e.Message, "after waiting 200 ms");
                Assert.IsTrue(ms >= 200 && ms <= 400, waitFor + ": gave up after " + ms + " ms: " + e.Message);
            }
        }

        /// <summary>
        /// The timeout is one budget for the whole open, counted from its start: an opener with 300 ms finds the name only after
        /// some 150 ms, created by an owner that then stays in DoOpen for 2 s, and gives up waiting for the ready flag from 300 ms
        /// after it started and before 300 ms after the owner started creating, where a budget restarted for the ready flag would
        /// end. Times count from the opener's and the owner's own threads; a run where the owner started outside 100 to 250 ms
        /// after the opener, as on a loaded machine, is tried again, up to three times.
        /// </summary>
        [TestMethod]
        public void Open_WithTimeout_OneBudgetForTheNameAndReady()
        {
            for (int attempt = 1; ; attempt++)
            {
                string late = OneBudgetForTheNameAndReady();
                if (late == null)
                    return;
                if (attempt == 3)
                    Assert.Fail(late);
                Console.WriteLine("attempt " + attempt + ": " + late + "; trying again");
            }
        }

        /// <summary>
        /// One run of <see cref="Open_WithTimeout_OneBudgetForTheNameAndReady"/>: null if it passed, or why the run could not tell.
        /// </summary>
        static string OneBudgetForTheNameAndReady()
        {
            string name = Guid.NewGuid().ToString();
            var options = new SharedMemoryOptions { OpenTimeout = TimeSpan.FromMilliseconds(300) };
            using (var release = new ManualResetEventSlim())
            {
                var clock = Stopwatch.StartNew();
                long startedAt = -1, failedAt = -1, creatingAt = -1;
                Task<ISubclass> opening = Task.Factory.StartNew(() =>
                {
                    Interlocked.Exchange(ref startedAt, clock.ElapsedMilliseconds);
                    try
                    {
                        return Subclass.Make(Plain, name, false, options);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref failedAt, clock.ElapsedMilliseconds);
                    }
                }, TaskCreationOptions.LongRunning);
                Task<HeldInDoOpen> creating = null;
                try
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => Interlocked.Read(ref startedAt) >= 0, 5000), "The opener's thread did not start.");
                    Thread.Sleep(150);
                    creating = Task.Factory.StartNew(() =>
                    {
                        Interlocked.Exchange(ref creatingAt, clock.ElapsedMilliseconds);
                        return new HeldInDoOpen(name, release);
                    }, TaskCreationOptions.LongRunning);

                    Assert.IsTrue(SpinWait.SpinUntil(() => opening.IsCompleted, 5000), "The opener did not return.");
                    Assert.AreEqual(TaskStatus.Faulted, opening.Status, "The opener should have thrown, but " + Describe(opening));
                    Assert.IsTrue(SpinWait.SpinUntil(() => Interlocked.Read(ref creatingAt) >= 0, 5000), "The owner's thread did not start.");
                    long creatingFrom = creatingAt - startedAt, gaveUp = failedAt - startedAt;
                    Exception thrown = opening.Exception.InnerException;
                    if (creatingFrom < 100 || creatingFrom > 250 || thrown is FileNotFoundException)
                        return "the owner started creating " + creatingFrom + " ms after the opener started, and the opener threw " + thrown.GetType().Name;

                    var e = thrown as InvalidOperationException;
                    Assert.IsNotNull(e, "The opener threw " + thrown);
                    StringAssert.Contains(e.Message, "is not ready");
                    StringAssert.Contains(e.Message, "after waiting 300 ms");
                    Assert.IsTrue(gaveUp >= 300 && gaveUp < creatingFrom + 300, "The opener gave up " + gaveUp + " ms after it started, and the owner started creating at " +
                        creatingFrom + " ms: a budget counted from the open's start ends at 300 ms, one restarted once the name appeared at " + (creatingFrom + 300) + " ms or later.");
                    return null;
                }
                finally
                {
                    release.Set();
                    DisposeOpened(opening);
                    if (creating != null)
                        DisposeOpened(creating);
                }
            }
        }

        /// <summary>
        /// An owner of a <see cref="SharedBuffer"/> held in DoOpen until it is released or 2 s pass: its buffer has a layout marker
        /// without the ready flag until then.
        /// </summary>
        sealed class HeldInDoOpen : SharedBuffer
        {
            readonly ManualResetEventSlim _release;

            public HeldInDoOpen(string name, ManualResetEventSlim release)
                : base(name, Size, true)
            {
                _release = release;
                Open();
            }

            protected override bool DoOpen()
            {
                _release.Wait(2000);
                return true;
            }
        }

        /// <summary>
        /// An opener with an infinite timeout is still waiting for a missing name after 1.2 s, and opens the buffer created 1.5 s
        /// after it started.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        public void Open_WithInfiniteTimeout_WaitsPastOneSecond(string kind)
        {
            string name = Guid.NewGuid().ToString();
            SharedMemoryOptions options = Options(kind);
            options.OpenTimeout = Timeout.InfiniteTimeSpan;
            var clock = Stopwatch.StartNew();
            Task<SharedBuffer> opening = Task.Factory.StartNew(() => Open(kind, name, options), TaskCreationOptions.LongRunning);
            try
            {
                Thread.Sleep(1200);
                Assert.IsFalse(opening.IsCompleted, kind + ": the opener should still be waiting after 1.2 s, but " + Describe(opening));
                Thread.Sleep((int)Math.Max(0, 1500 - clock.ElapsedMilliseconds));
                using (var creator = CreateToday(kind, name))
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => opening.IsCompleted, 5000), kind + ": the opener did not return.");
                    Assert.AreEqual(TaskStatus.RanToCompletion, opening.Status, kind + ": the opener should have opened, but " + Describe(opening));
                    AssertOpener(opening.Result, creator, kind);
                }
            }
            finally
            {
                DisposeOpened(opening);
            }
        }

#if NET
        /// <summary>
        /// On Linux and macOS, the name-only constructor with default options, as today's, finds a buffer whose holder was killed,
        /// removes it and throws FileNotFoundException at once, leaving no file with the name.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public void NewConstructors_DefaultOptions_AbandonedBufferRemoved(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create eventfree " + name + " " + Size);
                peer.Kill();
            }
            string path = PerUserPath(name);
            Assert.IsTrue(File.Exists(path), "The killed owner's file stays until the name is next used.");

            long ms = Elapsed(() => Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name, Options(kind)).Dispose()));

            Assert.IsTrue(ms < 500, kind + ": the open gave up after " + ms + " ms");
            Assert.IsFalse(File.Exists(path), kind + ": the abandoned file after the open");
            CollectionAssert.AreEqual(new string[0], Left(name), kind + ": what is left under the name");
        }

        /// <summary>
        /// On Linux and macOS, an opener with a timeout that finds a buffer whose holder was killed removes it, as any open does, and
        /// goes on waiting: the file is gone while the opener still waits, and it opens the buffer created 300 ms after it started.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [DataRow(ReadWrite)]
        [DataRow(IntArray)]
        [DataRow(Ring)]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public void Open_WithTimeout_AbandonedFile_RemovedAndWaitGoesOn(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create eventfree " + name + " " + Size);
                peer.Kill();
            }
            string path = PerUserPath(name);
            Assert.IsTrue(File.Exists(path), "The killed owner's file stays until the name is next used.");

            Task<SharedBuffer> opening = null;
            try
            {
                SharedMemoryOptions options = Options(kind);
                options.OpenTimeout = TimeSpan.FromSeconds(3);
                var clock = Stopwatch.StartNew();
                opening = Task.Factory.StartNew(() => Open(kind, name, options), TaskCreationOptions.LongRunning);
                Assert.IsTrue(SpinWait.SpinUntil(() => !File.Exists(path), 1000), kind + ": the abandoned file is still there after " + clock.ElapsedMilliseconds + " ms; the opener " + Describe(opening));
                Thread.Sleep((int)Math.Max(0, 300 - clock.ElapsedMilliseconds));
                Assert.IsFalse(opening.IsCompleted, kind + ": the opener should still be waiting, but " + Describe(opening));
                using (var creator = CreateToday(kind, name))
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => opening.IsCompleted, 5000), kind + ": the opener did not return.");
                    Assert.AreEqual(TaskStatus.RanToCompletion, opening.Status, kind + ": the opener should have opened, but " + Describe(opening));
                    AssertOpener(opening.Result, creator, kind);
                }
            }
            finally
            {
                if (opening != null)
                    DisposeOpened(opening);
                // An opener that failed, or never started, leaves the abandoned file: today's open removes it
                if (File.Exists(path))
                {
                    try
                    {
                        new EventFreeBuffer(name).Dispose();
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// The buffer's file on Linux, or its lock file on macOS, for a name without a scope.
        /// </summary>
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        static string PerUserPath(string name)
        {
            if (OperatingSystem.IsMacOS())
                return UnixBufferTests.Files.MacFolder + ".SharedMemory-uid" + UnixBufferTests.Files.EffectiveUserId() + "/" + UnixBufferTests.Files.LockFileName(name);
            return "/dev/shm/SharedMemory.u" + LinuxUserId() + "." + name;
        }

        static string LinuxUserId()
        {
            return File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal))
                .Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)[2];
        }
#endif

        /// <summary>
        /// An opener waiting 5 s for a name that never appears uses under 50 ms of the process's CPU time, on Linux and Windows. On
        /// .NET the runtime goes on compiling hot methods in the background for a few seconds after they first run, so the test
        /// waits for that to stop before it measures, as NamedEventTests' CPU-time test does. Not on macOS, whose readings swing
        /// with the Mac's load.
        /// </summary>
        [TestMethod]
        [OSCondition(ConditionMode.Exclude, OperatingSystems.OSX)]
        public void Open_Waiting5s_UsesLittleCpu()
        {
            Assert.ThrowsExactly<FileNotFoundException>(() => new BufferReadWrite(Guid.NewGuid().ToString(), new SharedMemoryOptions { OpenTimeout = TimeSpan.FromMilliseconds(100) }).Dispose(), "warming up");
            ProcessorTime();
            string settled = WaitForBackgroundCompilation();

            string name = Guid.NewGuid().ToString();
            var options = new SharedMemoryOptions { OpenTimeout = TimeSpan.FromSeconds(5) };
            TimeSpan before = ProcessorTime();
            var watch = Stopwatch.StartNew();
            Assert.ThrowsExactly<FileNotFoundException>(() => new BufferReadWrite(name, options).Dispose());
            watch.Stop();
            TimeSpan used = ProcessorTime() - before;

            Console.WriteLine(settled + "; waited " + watch.ElapsedMilliseconds + " ms, process CPU time " + used.TotalMilliseconds.ToString("F1") + " ms");
            Assert.IsTrue(watch.ElapsedMilliseconds >= 5000, "The open gave up after " + watch.ElapsedMilliseconds + " ms.");
            Assert.IsTrue(used.TotalMilliseconds < 50, "The process used " + used.TotalMilliseconds + " ms of CPU time.");
        }

#if !LIBRARY_NET35
        /// <summary>
        /// A token cancelled before the constructor runs makes it throw OperationCanceledException with that token, in every mode,
        /// before it makes anything: nothing is left under the name.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite, "CreateNew")]
        [DataRow(ReadWrite, "CreateOrOpen")]
        [DataRow(ReadWrite, "open")]
        [DataRow(IntArray, "CreateNew")]
        [DataRow(IntArray, "CreateOrOpen")]
        [DataRow(IntArray, "open")]
        [DataRow(Ring, "CreateNew")]
        [DataRow(Ring, "CreateOrOpen")]
        [DataRow(Ring, "open")]
        public void Open_TokenCancelledBefore_ThrowsAndLeavesNothing(string kind, string mode)
        {
            string name = Guid.NewGuid().ToString();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                SharedMemoryOptions options = Options(kind);
                options.OpenCancellationToken = cancelled.Token;
                if (mode == "CreateOrOpen")
                    options.OpenMode = SharedMemoryOpenMode.CreateOrOpen;

                var e = Assert.ThrowsExactly<OperationCanceledException>(() => (mode == "open" ? Open(kind, name, options) : Create(kind, name, options)).Dispose());

                Assert.AreEqual(cancelled.Token, e.CancellationToken, kind + ", " + mode + ": the exception's token");
                CollectionAssert.AreEqual(new string[0], Left(name), kind + ", " + mode + ": what is left under the name");
            }
        }

        /// <summary>
        /// A token cancelled once the constructors have returned plays no part: the creator and the opener write and read as
        /// before, and close.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite)]
        [DataRow(Ring)]
        public void Open_TokenCancelledAfterOpen_PlaysNoPart(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var cancel = new CancellationTokenSource())
            {
                SharedMemoryOptions options = Options(kind);
                options.OpenCancellationToken = cancel.Token;
                using (var creator = Create(kind, name, options))
                using (var opener = Open(kind, name, options))
                {
                    cancel.Cancel();
                    int value = 0x0A0B0C0D, read;
                    if (kind == Ring)
                    {
                        Assert.AreEqual(sizeof(int), ((CircularBuffer)creator).Write(ref value), "the creator's write");
                        Assert.AreEqual(sizeof(int), ((CircularBuffer)opener).Read(out read), "the opener's read");
                    }
                    else
                    {
                        ((BufferReadWrite)creator).Write(ref value);
                        ((BufferReadWrite)opener).Read(out read);
                    }
                    Assert.AreEqual(value, read, kind + ": what the opener read");
                    opener.Close();
                    creator.Close();
                }
            }
        }

        /// <summary>
        /// A token cancelled 200 ms into an open with an infinite timeout makes it throw OperationCanceledException with that token
        /// within 50 ms, whether it waits for the name to appear, for the owner to set the ready flag, or for the node count of a
        /// ring without a marker (SharedMemory 2.3.x), which it waits for after making the ring's events. Waiting for the name, it
        /// leaves nothing under it: the events it made are released.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite, "the name")]
        [DataRow(ReadWrite, "the ready flag")]
        [DataRow(IntArray, "the name")]
        [DataRow(IntArray, "the ready flag")]
        [DataRow(Ring, "the name")]
        [DataRow(Ring, "the ready flag")]
        [DataRow(Ring, "the node count")]
        public void Open_TokenCancelledDuringWait_ThrowsSoon(string kind, string waitFor)
        {
            CancelDuringWait(kind, waitFor, false);
        }

        /// <summary>
        /// As for an opener, a token cancelled 200 ms into create-or-open's attach with an infinite timeout, while it waits for the
        /// owner to set the ready flag or for the node count of a ring without a marker, makes it throw OperationCanceledException
        /// within 50 ms, and leaves the buffer as it was.
        /// </summary>
        [TestMethod]
        [DataRow(ReadWrite, "the ready flag")]
        [DataRow(IntArray, "the ready flag")]
        [DataRow(Ring, "the ready flag")]
        [DataRow(Ring, "the node count")]
        public void CreateOrOpen_TokenCancelledDuringWait_ThrowsSoon(string kind, string waitFor)
        {
            CancelDuringWait(kind, waitFor, true);
        }

        static void CancelDuringWait(string kind, string waitFor, bool createOrOpen)
        {
            string name = Guid.NewGuid().ToString();
            SharedBuffer creator = null;
            if (waitFor != "the name")
            {
                creator = CreateToday(kind, name);
                // A ring's node count is the node header's fifth int, after the 16-byte shared header
                using (var raw = new RawHeader(name, 40))
                {
                    raw.Marker = waitFor == "the ready flag" ? NotReady(kind) : 0;
                    if (waitFor == "the node count")
                        raw.WriteInt32(32, 0);
                }
            }
            int[] before = creator == null ? null : Words(name);
            string[] left = creator == null ? null : Left(name);
            using (creator)
            using (var cancel = new CancellationTokenSource())
            {
                SharedMemoryOptions options = Options(kind);
                options.OpenTimeout = Timeout.InfiniteTimeSpan;
                options.OpenCancellationToken = cancel.Token;
                if (createOrOpen)
                    options.OpenMode = SharedMemoryOpenMode.CreateOrOpen;
                var clock = Stopwatch.StartNew();
                long failedAt = -1;
                Task<SharedBuffer> opening = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        return createOrOpen ? Create(kind, name, options) : Open(kind, name, options);
                    }
                    finally
                    {
                        failedAt = clock.ElapsedMilliseconds;
                    }
                }, TaskCreationOptions.LongRunning);
                try
                {
                    Thread.Sleep(200);
                    Assert.IsFalse(opening.IsCompleted, kind + ": the opener should still be waiting for " + waitFor + ", but " + Describe(opening));
                    long cancelledAt = clock.ElapsedMilliseconds;
                    cancel.Cancel();

                    Assert.IsTrue(SpinWait.SpinUntil(() => opening.IsCompleted, 5000), kind + ": the opener did not return once cancelled.");
                    Assert.AreEqual(TaskStatus.Faulted, opening.Status, kind + ": the opener should have thrown, but " + Describe(opening));
                    var e = opening.Exception.InnerException as OperationCanceledException;
                    Assert.IsNotNull(e, kind + ": the opener threw " + opening.Exception.InnerException);
                    Assert.AreEqual(cancel.Token, e.CancellationToken, kind + ": the exception's token");
                    Assert.IsTrue(failedAt - cancelledAt <= 50, kind + ": the opener threw " + (failedAt - cancelledAt) + " ms after the cancel");
                    if (creator == null)
                        CollectionAssert.AreEqual(new string[0], Left(name), kind + ": what is left under the name");
                    else
                    {
                        CollectionAssert.AreEqual(before, Words(name), kind + ": the buffer's header changed.");
                        CollectionAssert.AreEquivalent(left, Left(name), kind + ": what is under the name changed.");
                    }
                }
                finally
                {
                    cancel.Cancel();
                    DisposeOpened(opening);
                }
            }
        }
#endif

        #endregion

        #region Helpers

        static void AssertCreator(SharedBuffer creator, string what)
        {
            Assert.IsTrue(creator.IsOwnerOfSharedMemory, what + ": the creator's IsOwnerOfSharedMemory");
            Assert.IsTrue(creator.CreatedSharedMemory, what + ": the creator's CreatedSharedMemory");
        }

        static void AssertOpener(SharedBuffer opener, SharedBuffer creator, string what)
        {
            Assert.IsFalse(opener.IsOwnerOfSharedMemory, what + ": the opener's IsOwnerOfSharedMemory");
            Assert.IsFalse(opener.CreatedSharedMemory, what + ": the opener's CreatedSharedMemory");
            Assert.AreEqual(creator.BufferSize, opener.BufferSize, what + ": the opener's BufferSize");
            Assert.AreEqual(creator.SharedMemorySize, opener.SharedMemorySize, what + ": the opener's SharedMemorySize");
        }

        static long Elapsed(Action action)
        {
            var watch = Stopwatch.StartNew();
            action();
            return watch.ElapsedMilliseconds;
        }

        static SharedMemoryOptions Options(string kind)
        {
            return kind == Ring ? new CircularBufferOptions() : new SharedMemoryOptions();
        }

        static int NotReady(string kind)
        {
            return kind == Ring ? RingNotReady : kind == IntArray ? ArrayNotReady : ReadWriteNotReady;
        }

        static long KindSize(string kind)
        {
            using (var probe = CreateToday(kind, Guid.NewGuid().ToString()))
                return probe.BufferSize;
        }

        static string PeerCreate(string kind, string name)
        {
            if (kind == Ring)
                return "create circular " + name + " " + NodeCount + " " + NodeSize;
            if (kind == IntArray)
                return "create array " + name + " " + Size / sizeof(int);
            return "create readwrite " + name + " " + Size;
        }

        /// <summary>
        /// Today's constructor with sizes.
        /// </summary>
        static SharedBuffer CreateToday(string kind, string name)
        {
            switch (kind)
            {
                case ReadWrite: return new BufferReadWrite(name, Size);
                case IntArray: return new SharedArray<int>(name, Size / sizeof(int));
                case Ring: return new CircularBuffer(name, NodeCount, NodeSize);
                default: throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        /// <summary>
        /// Today's constructor with the name only.
        /// </summary>
        static SharedBuffer OpenToday(string kind, string name)
        {
            switch (kind)
            {
                case ReadWrite: return new BufferReadWrite(name);
                case IntArray: return new SharedArray<int>(name);
                case Ring: return new CircularBuffer(name);
                default: throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        /// <summary>
        /// The constructor with sizes and options.
        /// </summary>
        static SharedBuffer Create(string kind, string name, SharedMemoryOptions options)
        {
            switch (kind)
            {
                case ReadWrite: return new BufferReadWrite(name, Size, options);
                case IntArray: return new SharedArray<int>(name, Size / sizeof(int), options);
                case Ring: return new CircularBuffer(name, NodeCount, NodeSize, (CircularBufferOptions)options);
                default: throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        /// <summary>
        /// The constructor with the name and options.
        /// </summary>
        static SharedBuffer Open(string kind, string name, SharedMemoryOptions options)
        {
            switch (kind)
            {
                case ReadWrite: return new BufferReadWrite(name, options);
                case IntArray: return new SharedArray<int>(name, options);
                case Ring: return new CircularBuffer(name, (CircularBufferOptions)options);
                default: throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        static readonly string[] Constructors =
        {
            "BufferReadWrite(name, bufferSize, options)",
            "BufferReadWrite(name, options)",
            "SharedArray<int>(name, length, options)",
            "SharedArray<int>(name, options)",
            "CircularBuffer(name, nodeCount, nodeBufferSize, options)",
            "CircularBuffer(name, options)",
            "SharedBuffer(name, bufferSize, true, options)",
            "SharedBuffer(name, 0, false, options)",
            "BufferWithLocks(name, bufferSize, true, options)",
            "BufferWithLocks(name, 0, false, options)",
        };

        static SharedMemoryOptions OptionsFor(string constructor)
        {
            return constructor.StartsWith(Ring, StringComparison.Ordinal) ? new CircularBufferOptions() : new SharedMemoryOptions();
        }

        /// <summary>
        /// One of <see cref="Constructors"/>, by its text.
        /// </summary>
        static IDisposable Construct(string constructor, string name, SharedMemoryOptions options)
        {
            switch (constructor)
            {
                case "BufferReadWrite(name, bufferSize, options)": return new BufferReadWrite(name, Size, options);
                case "BufferReadWrite(name, options)": return new BufferReadWrite(name, options);
                case "SharedArray<int>(name, length, options)": return new SharedArray<int>(name, Size / sizeof(int), options);
                case "SharedArray<int>(name, options)": return new SharedArray<int>(name, options);
                case "CircularBuffer(name, nodeCount, nodeBufferSize, options)": return new CircularBuffer(name, NodeCount, NodeSize, (CircularBufferOptions)options);
                case "CircularBuffer(name, options)": return new CircularBuffer(name, (CircularBufferOptions)options);
                case "SharedBuffer(name, bufferSize, true, options)": return Subclass.Make(Plain, name, true, options);
                case "SharedBuffer(name, 0, false, options)": return Subclass.Make(Plain, name, false, options);
                case "BufferWithLocks(name, bufferSize, true, options)": return Subclass.Make(Locked, name, true, options);
                case "BufferWithLocks(name, 0, false, options)": return Subclass.Make(Locked, name, false, options);
                default: throw new ArgumentException("Unknown constructor: " + constructor);
            }
        }

        /// <summary>
        /// The first <see cref="Snapshot"/> bytes of the buffer, as 32-bit words, read through a mapping of its own.
        /// </summary>
        static int[] Words(string name)
        {
            var words = new int[Snapshot / sizeof(int)];
            using (var raw = new RawHeader(name, Snapshot))
            {
                for (int i = 0; i < words.Length; i++)
                    words[i] = raw.ReadInt32(i * sizeof(int));
            }
            return words;
        }

        /// <summary>
        /// What is left under <paramref name="name"/>, and the names made from it for a buffer's events: on Linux the files in
        /// /dev/shm, on macOS the lock files, and on Windows the mapping and the events that open by name.
        /// </summary>
        static string[] Left(string name)
        {
#if NET
            if (OperatingSystem.IsLinux())
                return Directory.GetFiles("/dev/shm", "SharedMemory.*").Select(Path.GetFileName)
                    .Where(file => file.Contains("." + name, StringComparison.Ordinal)).ToArray();
            if (OperatingSystem.IsMacOS())
            {
                // The folder exists once this user has created a buffer on the machine
                string folder = UnixBufferTests.Files.MacFolder + ".SharedMemory-uid" + UnixBufferTests.Files.EffectiveUserId();
                if (!Directory.Exists(folder))
                    return new string[0];
                return Directory.GetFiles(folder).Select(Path.GetFileName).Where(file => file.StartsWith(name, StringComparison.Ordinal)).ToArray();
            }
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
#endif
            var left = new List<string>();
            try
            {
                NamedMapping mapping = NamedMapping.OpenExisting(name);
                if (mapping.File != null)
                    mapping.File.Dispose();
                mapping.Dispose();
                left.Add(name);
            }
            catch (FileNotFoundException)
            {
            }
            foreach (string suffix in new[] { "_evt_write", "_evt_read", "_evt_dataexists", "_evt_nodeavail" })
            {
                EventWaitHandle handle;
                if (EventWaitHandle.TryOpenExisting(name + suffix, out handle))
                {
                    handle.Dispose();
                    left.Add(name + suffix);
                }
            }
            return left.ToArray();
        }

        static string Describe(Task task)
        {
            if (task.Status == TaskStatus.RanToCompletion)
                return "it returned.";
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

        static TimeSpan ProcessorTime()
        {
            using (var self = Process.GetCurrentProcess())
                return self.TotalProcessorTime;
        }

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
            return "waited " + watch.ElapsedMilliseconds + " ms for background compilation to stop";
#else
            return "no background compilation";
#endif
        }

        /// <summary>
        /// A subclass of <see cref="SharedBuffer"/> or <see cref="BufferWithLocks"/> made through the protected constructor that
        /// takes options, which can open again, and records its DoOpen calls with IsOwnerOfSharedMemory and CreatedSharedMemory in
        /// the first.
        /// </summary>
        interface ISubclass : IDisposable
        {
            bool IsOwnerOfSharedMemory { get; }
            bool CreatedSharedMemory { get; }
            long BufferSize { get; }
            int DoOpenCalls { get; }
            bool OwnerInDoOpen { get; }
            bool CreatedInDoOpen { get; }
            void Reopen();
        }

        static class Subclass
        {
            public static ISubclass Make(string baseClass, string name, bool create, SharedMemoryOptions options)
            {
                if (baseClass == Plain)
                    return new PlainSubclass(name, create, options);
                if (baseClass == Locked)
                    return new LockedSubclass(name, create, options);
                throw new ArgumentException("Unknown base class: " + baseClass);
            }

            sealed class PlainSubclass : SharedBuffer, ISubclass
            {
                public PlainSubclass(string name, bool create, SharedMemoryOptions options)
                    : base(name, create ? Size : 0, create, options)
                {
                    Open();
                }

                public int DoOpenCalls { get; private set; }
                public bool OwnerInDoOpen { get; private set; }
                public bool CreatedInDoOpen { get; private set; }

                public void Reopen()
                {
                    Assert.IsTrue(Open(), "Open returned false.");
                }

                protected override bool DoOpen()
                {
                    if (DoOpenCalls++ == 0)
                    {
                        OwnerInDoOpen = IsOwnerOfSharedMemory;
                        CreatedInDoOpen = CreatedSharedMemory;
                    }
                    return true;
                }
            }

            sealed class LockedSubclass : BufferWithLocks, ISubclass
            {
                public LockedSubclass(string name, bool create, SharedMemoryOptions options)
                    : base(name, create ? Size : 0, create, options)
                {
                    try
                    {
                        Open();
                    }
                    catch
                    {
                        // As the library's classes do: nobody can dispose an instance whose constructor threw
                        DisposeEvents();
                        throw;
                    }
                }

                public int DoOpenCalls { get; private set; }
                public bool OwnerInDoOpen { get; private set; }
                public bool CreatedInDoOpen { get; private set; }

                public void Reopen()
                {
                    Assert.IsTrue(Open(), "Open returned false.");
                }

                protected override bool DoOpen()
                {
                    if (DoOpenCalls++ == 0)
                    {
                        OwnerInDoOpen = IsOwnerOfSharedMemory;
                        CreatedInDoOpen = CreatedSharedMemory;
                    }
                    return true;
                }
            }
        }

        #endregion
    }
}
