using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The layout marker in bytes 12 to 15 of every buffer's shared header, version 1, as a little-endian int: bits 0 to 3 the layout
    /// version (1), 4 to 7 the buffer's kind (0 another SharedBuffer or BufferWithLocks subclass, 1 BufferReadWrite, 2 SharedArray,
    /// 3 CircularBuffer, 4 an RpcBuffer ring), 8 to 11 the lock mode (0), bit 12 the ready flag, 13 to 15 spare flags and 16 to 31
    /// reserved. A process on another version can open the same buffer, so every marker here is spelled as a literal, never through
    /// the library's constants: once 2.4.0 ships, version 1's format is fixed for good. A word of 0 is a buffer from 2.3.x or earlier.
    /// </summary>
    [TestClass]
    public class LayoutMarkerTests
    {
        internal const int Size = 1024;
        internal const string OwnerWritesRing = "_Opener_SharedMemory_MMF";
        internal const string OwnerReadsRing = "_Owner_SharedMemory_MMF";

        /// <summary>
        /// Each library class writes version 1's marker with its own kind, lock mode 0 and, once open, the ready flag: bytes 12 to 15
        /// read 11 10 00 00, 21 10 00 00, 31 10 00 00 and 01 10 00 00.
        /// </summary>
        [TestMethod]
        [DataRow("BufferReadWrite", 0x00001011)]
        [DataRow("SharedArray", 0x00001021)]
        [DataRow("CircularBuffer", 0x00001031)]
        [DataRow("SharedBuffer", 0x00001001)]
        public void Creator_WritesVersionOneMarker(string kind, int marker)
        {
            string name = Guid.NewGuid().ToString();
            using (Create(kind, name))
            using (var raw = new RawHeader(name))
                AssertMarker(marker, raw.Marker, kind + ": bytes 12 to 15 once open");
        }

#if HAS_RPC_BUFFER
        /// <summary>
        /// Both of an RpcBuffer owner's rings are kind 4, read as bytes through a BufferReadWrite opener.
        /// </summary>
        [TestMethod]
        public void RpcBuffer_Rings_AreKindFour()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new RpcBuffer(name, 1024))
            {
                Assert.IsTrue(owner.IsChannelOwner);
                foreach (string ring in new[] { OwnerWritesRing, OwnerReadsRing })
                {
                    using (var view = new HeaderView(name + ring))
                        AssertMarker(0x00001041, view.Marker, "the ring " + name + ring + ", read through a BufferReadWrite");
                }
            }
        }
#endif

        /// <summary>
        /// A class outside the library takes its kind from the nearest library class it derives from; one deriving straight from
        /// SharedBuffer or BufferWithLocks is kind 0.
        /// </summary>
        [TestMethod]
        [DataRow("BufferReadWrite", 0x00001011)]
        [DataRow("SharedArray", 0x00001021)]
        [DataRow("CircularBuffer", 0x00001031)]
        [DataRow("BufferWithLocks", 0x00001001)]
        [DataRow("SharedBuffer", 0x00001001)]
#if HAS_RPC_BUFFER
        [DataRow("RpcBuffer", 0x00001041)]
#endif
        public void Creator_OfASubclass_WritesItsLibraryBasesKind(string baseClass, int marker)
        {
            string name = Guid.NewGuid().ToString();
#if HAS_RPC_BUFFER
            if (baseClass == "RpcBuffer")
            {
                using (new RpcSubclass(name))
                {
                    foreach (string ring in new[] { OwnerWritesRing, OwnerReadsRing })
                    {
                        using (var raw = new RawHeader(name + ring))
                            AssertMarker(marker, raw.Marker, "a subclass of RpcBuffer: the ring " + name + ring);
                    }
                }
                return;
            }
#endif
            using (IProbe probe = Probe.Create(baseClass, name))
                AssertMarker(marker, probe.Marker, "a subclass of " + baseClass);
        }

        /// <summary>
        /// Inside DoOpen the creator has written the marker, without the ready flag, and the whole size.
        /// </summary>
        [TestMethod]
        [DataRow("BufferReadWrite", 0x00000011)]
        [DataRow("SharedArray", 0x00000021)]
        [DataRow("CircularBuffer", 0x00000031)]
        [DataRow("BufferWithLocks", 0x00000001)]
        [DataRow("SharedBuffer", 0x00000001)]
        public void Creator_InsideDoOpen_HasWrittenMarkerAndSizeButNotReady(string baseClass, int marker)
        {
            string name = Guid.NewGuid().ToString();
            using (IProbe probe = Probe.Create(baseClass, name))
            {
                Assert.AreEqual(probe.SharedMemorySize, probe.SizeInDoOpen, baseClass + ": the size in the header, inside DoOpen");
                AssertMarker(marker, probe.MarkerInDoOpen, baseClass + ": the marker inside DoOpen, without the ready flag");
            }
        }

        /// <summary>
        /// A creator whose DoOpen throws or returns false never sets the ready flag, and its Close sets Shutdown: read through a raw
        /// mapping that DoOpen made, which keeps the memory after the creator lets go of it.
        /// </summary>
        [TestMethod]
        [DataRow("throws")]
        [DataRow("returns false")]
        public void Creator_WhoseDoOpenThrows_NeverSetsReady(string failure)
        {
            string name = Guid.NewGuid().ToString();
            var failing = new FailingOpen(failure == "throws");
            try
            {
                try
                {
                    new FailingBuffer(name, failing).Dispose();
                    Assert.IsFalse(failing.Throws, "The constructor should have thrown the planted exception.");
                }
                catch (InvalidOperationException e) when (e.Message == FailingOpen.Planted)
                {
                }

                Assert.IsNotNull(failing.Raw, "DoOpen did not run.");
                Assert.AreEqual(1, failing.Raw.Shutdown, failure + ": Shutdown, once the failed open closed");
                AssertMarker(0x00000001, failing.Raw.Marker, failure + ": the marker after a DoOpen that " + failure);
            }
            finally
            {
                if (failing.Raw != null)
                    failing.Raw.Dispose();
            }
        }

        /// <summary>
        /// A word of 0 with a size is a buffer from 2.3.x or earlier: each class opens it as before, with the creator's size and
        /// data, and leaves the word at 0.
        /// </summary>
        [TestMethod]
        [DataRow("BufferReadWrite")]
        [DataRow("SharedArray")]
        [DataRow("CircularBuffer")]
        public void Opener_OfMarkerZeroWithASize_OpensAsBefore(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (SharedBuffer creator = Create(kind, name))
            using (var raw = new RawHeader(name))
            {
                raw.Marker = 0;
                AssertOpensWithCreatorsData(kind, creator, name);
                AssertMarker(0, raw.Marker, kind + ": the opener left the word at 0");
            }
        }

        /// <summary>
        /// A marker whose layout version is not 1 is refused at once, ready or not, naming the buffer, the version and the lock mode.
        /// </summary>
        [TestMethod]
        [DataRow(0x00001010, 0, 0)]
        [DataRow(0x00000010, 0, 0)]
        [DataRow(0x00001012, 2, 0)]
        [DataRow(0x00000012, 2, 0)]
        [DataRow(0x0000101F, 15, 0)]
        [DataRow(0x0000001F, 15, 0)]
        public void Opener_OfAnUnknownVersion_IsRefusedAtOnce(int marker, int version, int lockMode)
        {
            AssertLayoutRefused(marker, version, lockMode);
        }

        /// <summary>
        /// A marker whose lock mode is not 0 is refused at once, ready or not, naming the buffer, the version and the lock mode.
        /// </summary>
        [TestMethod]
        [DataRow(0x00001111, 1, 1)]
        [DataRow(0x00000111, 1, 1)]
        [DataRow(0x00001F11, 1, 15)]
        [DataRow(0x00000F11, 1, 15)]
        [DataRow(0x00001312, 2, 3)]
        public void Opener_OfAnUnknownLockMode_IsRefusedAtOnce(int marker, int version, int lockMode)
        {
            AssertLayoutRefused(marker, version, lockMode);
        }

        /// <summary>
        /// A ring opener refuses at once a buffer marked as another kind, naming the buffer and the kind it found.
        /// </summary>
        [TestMethod]
        [DataRow("BufferReadWrite", 0x00001011, "BufferReadWrite")]
        [DataRow("SharedArray", 0x00001021, "SharedArray")]
        [DataRow("SharedBuffer", 0x00001001, "SharedBuffer")]
        [DataRow("BufferReadWrite", 0x00001051, "kind 5")]
        public void RingOpener_OfAnotherKind_IsRefusedAtOnce(string kind, int marker, string found)
        {
            string name = Guid.NewGuid().ToString();
            using (Create(kind, name))
            {
                using (var raw = new RawHeader(name))
                    raw.Marker = marker;

                long ms;
                InvalidOperationException e = Refused(() => new CircularBuffer(name), out ms);
                AssertAtOnce(ms, e);
                StringAssert.Contains(e.Message, name);
                StringAssert.Contains(e.Message, found);
            }
        }

        /// <summary>
        /// A BufferReadWrite or SharedArray opener opens any kind as bytes, known or not, and leaves the marker as it was.
        /// </summary>
        [TestMethod]
        [DataRow("BufferReadWrite", 0x00001001)]
        [DataRow("BufferReadWrite", 0x00001011)]
        [DataRow("BufferReadWrite", 0x00001021)]
        [DataRow("BufferReadWrite", 0x00001031)]
        [DataRow("BufferReadWrite", 0x00001041)]
        [DataRow("BufferReadWrite", 0x00001051)]
        [DataRow("SharedArray", 0x00001001)]
        [DataRow("SharedArray", 0x00001011)]
        [DataRow("SharedArray", 0x00001021)]
        [DataRow("SharedArray", 0x00001031)]
        [DataRow("SharedArray", 0x00001041)]
        [DataRow("SharedArray", 0x00001051)]
        public void ByteOpener_OfAnyKind_Opens(string opener, int marker)
        {
            string name = Guid.NewGuid().ToString();
            using (var creator = new BufferReadWrite(name, Size))
            using (var raw = new RawHeader(name))
            {
                raw.Marker = marker;
                int value = 123;
                creator.Write(ref value, 100);

                if (opener == "SharedArray")
                {
                    using (var array = new SharedArray<byte>(name))
                    {
                        Assert.AreEqual(Size, array.Length);
                        Assert.AreEqual((byte)123, array[100]);
                    }
                }
                else
                {
                    using (var buffer = new BufferReadWrite(name))
                    {
                        Assert.AreEqual((long)Size, buffer.BufferSize);
                        buffer.Read(out value, 100);
                        Assert.AreEqual(123, value);
                    }
                }
                AssertMarker(marker, raw.Marker, opener + ": the opener left the marker as it was");
            }
        }

        /// <summary>
        /// A ready marker whose size leaves no room past the 16-byte header is refused at once as damaged, naming the size.
        /// </summary>
        [TestMethod]
        [DataRow(0L)]
        [DataRow(1L)]
        [DataRow(16L)]
        public void Opener_OfAReadyMarkerWithNoRoom_IsRefusedAsDamaged(long size)
        {
            string name = Guid.NewGuid().ToString();
            using (new BufferReadWrite(name, Size))
            {
                using (var raw = new RawHeader(name))
                {
                    raw.Marker = 0x00001011;
                    raw.Size = size;
                }

                long ms;
                InvalidOperationException e = Refused(() => new BufferReadWrite(name), out ms);
                AssertAtOnce(ms, e);
                StringAssert.Contains(e.Message, name);
                StringAssert.Contains(e.Message, "is damaged");
                StringAssert.Contains(e.Message, size + " bytes");
            }
        }

        internal static SharedBuffer Create(string kind, string name)
        {
            switch (kind)
            {
                case "BufferReadWrite":
                    return new BufferReadWrite(name, Size);
                case "SharedArray":
                    return new SharedArray<int>(name, Size / sizeof(int));
                case "CircularBuffer":
                    return new CircularBuffer(name, 4, 64);
                case "SharedBuffer":
                    return new EventFreeBuffer(name, Size);
                default:
                    throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        static void AssertOpensWithCreatorsData(string kind, SharedBuffer creator, string name)
        {
            switch (kind)
            {
                case "BufferReadWrite":
                    {
                        int value = 123;
                        ((BufferReadWrite)creator).Write(ref value, 100);
                        using (var opener = new BufferReadWrite(name))
                        {
                            Assert.AreEqual(creator.BufferSize, opener.BufferSize, kind);
                            opener.Read(out value, 100);
                            Assert.AreEqual(123, value, kind);
                        }
                        break;
                    }
                case "SharedArray":
                    {
                        ((SharedArray<int>)creator)[3] = 123;
                        using (var opener = new SharedArray<int>(name))
                        {
                            Assert.AreEqual(creator.BufferSize, opener.BufferSize, kind);
                            Assert.AreEqual(123, opener[3], kind);
                        }
                        break;
                    }
                case "CircularBuffer":
                    {
                        var ring = (CircularBuffer)creator;
                        byte[] data = { 1, 2, 3, 4 };
                        Assert.AreEqual(4, ring.Write(data));
                        using (var opener = new CircularBuffer(name))
                        {
                            Assert.AreEqual(ring.NodeCount, opener.NodeCount, kind);
                            Assert.AreEqual(ring.NodeBufferSize, opener.NodeBufferSize, kind);
                            byte[] read = new byte[4];
                            Assert.AreEqual(4, opener.Read(read, 0, 0), kind);
                            CollectionAssert.AreEqual(data, read, kind);
                        }
                        break;
                    }
                default:
                    throw new ArgumentException("Unknown kind: " + kind);
            }
        }

        static void AssertLayoutRefused(int marker, int version, int lockMode)
        {
            string name = Guid.NewGuid().ToString();
            using (new BufferReadWrite(name, Size))
            {
                using (var raw = new RawHeader(name))
                    raw.Marker = marker;

                long ms;
                InvalidOperationException e = Refused(() => new BufferReadWrite(name), out ms);
                AssertAtOnce(ms, e);
                StringAssert.Contains(e.Message, name);
                StringAssert.Contains(e.Message, "version " + version);
                StringAssert.Contains(e.Message, "lock mode " + lockMode);
            }
        }

        /// <summary>
        /// Runs an opener that should be refused: returns its InvalidOperationException and how long it took. If it opens instead,
        /// it is disposed and the test fails.
        /// </summary>
        internal static InvalidOperationException Refused(Func<IDisposable> open, out long elapsedMs)
        {
            var watch = Stopwatch.StartNew();
            IDisposable opened;
            try
            {
                opened = open();
            }
            catch (InvalidOperationException e)
            {
                elapsedMs = watch.ElapsedMilliseconds;
                return e;
            }
            elapsedMs = watch.ElapsedMilliseconds;
            opened.Dispose();
            Assert.Fail("The opener opened, after " + elapsedMs + " ms, where it should have been refused.");
            return null;
        }

        static void AssertAtOnce(long ms, InvalidOperationException e)
        {
            Assert.IsTrue(ms <= 100, "Refused after " + ms + " ms, not at once: " + e.Message);
        }

        internal static void AssertMarker(int expected, int actual, string what)
        {
            Assert.AreEqual(Hex(expected), Hex(actual), what + ": read " + Hex(actual) + ", expected " + Hex(expected));
        }

        internal static string Hex(int word)
        {
            return "0x" + word.ToString("X8");
        }

        internal static unsafe int MarkerOf(SharedHeader* header)
        {
            return Interlocked.CompareExchange(ref *(int*)((byte*)header + RawHeader.MarkerOffset), 0, 0);
        }

        internal static unsafe long SizeOf(SharedHeader* header)
        {
            return Interlocked.Read(ref header->SharedMemorySize);
        }

        /// <summary>
        /// A BufferReadWrite opener of any buffer, reading the marker through its own header.
        /// </summary>
        sealed unsafe class HeaderView : BufferReadWrite
        {
            public HeaderView(string name)
                : base(name)
            {
            }

            public int Marker
            {
                get { return MarkerOf(Header); }
            }
        }

        sealed class FailingOpen
        {
            public const string Planted = "DoOpen failed on purpose.";

            public FailingOpen(bool throws)
            {
                Throws = throws;
            }

            public bool Throws { get; private set; }
            public RawHeader Raw { get; set; }
        }

        /// <summary>
        /// A creator whose DoOpen maps the header raw, then throws or returns false.
        /// </summary>
        sealed class FailingBuffer : SharedBuffer
        {
            readonly FailingOpen _failing;

            public FailingBuffer(string name, FailingOpen failing)
                : base(name, Size, true)
            {
                _failing = failing;
                Open();
            }

            protected override bool DoOpen()
            {
                _failing.Raw = new RawHeader(Name);
                if (_failing.Throws)
                    throw new InvalidOperationException(FailingOpen.Planted);
                return false;
            }
        }

#if HAS_RPC_BUFFER
        sealed class RpcSubclass : RpcBuffer
        {
            public RpcSubclass(string name)
                : base(name, 1024)
            {
            }
        }
#endif
    }

    /// <summary>
    /// The first bytes of a buffer's shared memory, mapped by name apart from any <see cref="SharedBuffer"/>, so that a test can read
    /// and write the header of a buffer that no opener would open, and keep the memory after its creator lets go of it. On macOS,
    /// where the memory is mapped without a MemoryMappedFile, it uses the mapping's own pointer.
    /// </summary>
    internal sealed unsafe class RawHeader : IDisposable
    {
        internal const int MarkerOffset = 12;

        readonly NamedMapping _mapping;
        readonly IDisposable _file;
        readonly IDisposable _view;
        readonly Action _release;
        readonly byte* _pointer;

        public RawHeader(string name, int length = 16)
        {
            _mapping = NamedMapping.OpenExisting(name);
            try
            {
                if (_mapping.File != null)
                {
                    _file = _mapping.File;
                    var view = _mapping.File.CreateViewAccessor(0, length);
                    _view = view;
                    byte* pointer = null;
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    _release = () => view.SafeMemoryMappedViewHandle.ReleasePointer();
                    _pointer = pointer;
                }
                else
                {
                    object pointer = typeof(NamedMapping).GetProperty("Pointer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_mapping, null);
                    _pointer = (byte*)System.Reflection.Pointer.Unbox(pointer);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Bytes 12 to 15, as a little-endian int.
        /// </summary>
        public int Marker
        {
            get { return Interlocked.CompareExchange(ref *(int*)(_pointer + MarkerOffset), 0, 0); }
            set { Interlocked.Exchange(ref *(int*)(_pointer + MarkerOffset), value); }
        }

        /// <summary>
        /// Bytes 0 to 7, the total size.
        /// </summary>
        public long Size
        {
            get { return Interlocked.Read(ref *(long*)_pointer); }
            set { Interlocked.Exchange(ref *(long*)_pointer, value); }
        }

        /// <summary>
        /// Bytes 8 to 11, 1 once the owner has closed.
        /// </summary>
        public int Shutdown
        {
            get { return ReadInt32(8); }
        }

        public int ReadInt32(int offset)
        {
            return Interlocked.CompareExchange(ref *(int*)(_pointer + offset), 0, 0);
        }

        public void WriteInt32(int offset, int value)
        {
            Interlocked.Exchange(ref *(int*)(_pointer + offset), value);
        }

        public void Dispose()
        {
            if (_release != null)
                _release();
            if (_view != null)
                _view.Dispose();
            if (_file != null)
                _file.Dispose();
            _mapping.Dispose();
        }
    }

    /// <summary>
    /// A test subclass of a library class, which records the marker and the size in its own header inside DoOpen.
    /// </summary>
    internal interface IProbe : IDisposable
    {
        long SharedMemorySize { get; }
        int Marker { get; }
        int MarkerInDoOpen { get; }
        long SizeInDoOpen { get; }
    }

    internal static class Probe
    {
        public static IProbe Create(string baseClass, string name)
        {
            switch (baseClass)
            {
                case "BufferReadWrite":
                    return new ReadWriteProbe(name);
                case "SharedArray":
                    return new ArrayProbe(name);
                case "CircularBuffer":
                    return new RingProbe(name);
                case "BufferWithLocks":
                    return new LocksProbe(name);
                case "SharedBuffer":
                    return new BufferProbe(name);
                default:
                    throw new ArgumentException("Unknown base class: " + baseClass);
            }
        }

        sealed unsafe class ReadWriteProbe : BufferReadWrite, IProbe
        {
            public ReadWriteProbe(string name)
                : base(name, LayoutMarkerTests.Size)
            {
            }

            public int Marker { get { return LayoutMarkerTests.MarkerOf(Header); } }
            public int MarkerInDoOpen { get; private set; }
            public long SizeInDoOpen { get; private set; }

            protected override bool DoOpen()
            {
                MarkerInDoOpen = LayoutMarkerTests.MarkerOf(Header);
                SizeInDoOpen = LayoutMarkerTests.SizeOf(Header);
                return base.DoOpen();
            }
        }

        sealed unsafe class ArrayProbe : SharedArray<int>, IProbe
        {
            public ArrayProbe(string name)
                : base(name, LayoutMarkerTests.Size / sizeof(int))
            {
            }

            public int Marker { get { return LayoutMarkerTests.MarkerOf(Header); } }
            public int MarkerInDoOpen { get; private set; }
            public long SizeInDoOpen { get; private set; }

            protected override bool DoOpen()
            {
                MarkerInDoOpen = LayoutMarkerTests.MarkerOf(Header);
                SizeInDoOpen = LayoutMarkerTests.SizeOf(Header);
                return base.DoOpen();
            }
        }

        sealed unsafe class RingProbe : CircularBuffer, IProbe
        {
            public RingProbe(string name)
                : base(name, 4, 64)
            {
            }

            public int Marker { get { return LayoutMarkerTests.MarkerOf(Header); } }
            public int MarkerInDoOpen { get; private set; }
            public long SizeInDoOpen { get; private set; }

            protected override bool DoOpen()
            {
                MarkerInDoOpen = LayoutMarkerTests.MarkerOf(Header);
                SizeInDoOpen = LayoutMarkerTests.SizeOf(Header);
                return base.DoOpen();
            }
        }

        sealed unsafe class LocksProbe : BufferWithLocks, IProbe
        {
            public LocksProbe(string name)
                : base(name, LayoutMarkerTests.Size, true)
            {
                Open();
            }

            public int Marker { get { return LayoutMarkerTests.MarkerOf(Header); } }
            public int MarkerInDoOpen { get; private set; }
            public long SizeInDoOpen { get; private set; }

            protected override bool DoOpen()
            {
                MarkerInDoOpen = LayoutMarkerTests.MarkerOf(Header);
                SizeInDoOpen = LayoutMarkerTests.SizeOf(Header);
                return base.DoOpen();
            }
        }

        sealed unsafe class BufferProbe : SharedBuffer, IProbe
        {
            public BufferProbe(string name)
                : base(name, LayoutMarkerTests.Size, true)
            {
                Open();
            }

            public int Marker { get { return LayoutMarkerTests.MarkerOf(Header); } }
            public int MarkerInDoOpen { get; private set; }
            public long SizeInDoOpen { get; private set; }

            protected override bool DoOpen()
            {
                MarkerInDoOpen = LayoutMarkerTests.MarkerOf(Header);
                SizeInDoOpen = LayoutMarkerTests.SizeOf(Header);
                return base.DoOpen();
            }
        }
    }
}
