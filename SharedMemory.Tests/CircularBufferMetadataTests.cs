using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// BufferAndRingSafety FR-010: what a <see cref="CircularBuffer"/> does with the node header and nodes another process can write.
    /// Each test damages a field by opening the buffer's shared memory by name, as another process would, and writing to it.
    /// </summary>
    [TestClass]
    public class CircularBufferMetadataTests
    {
        const int NodeCount = 3;
        const int NodeSize = 64;
        static readonly int NodeHeaderSize = Marshal.SizeOf(typeof(CircularBuffer.NodeHeader));
        static readonly int NodeStructSize = Marshal.SizeOf(typeof(CircularBuffer.Node));

        // 64 bytes: a whole node
        [StructLayout(LayoutKind.Sequential)]
        struct Whole
        {
            public long A, B, C, D, E, F, G, H;
        }

        /// <summary>
        /// Every node's Offset points past the end of the buffer: the owner writes, and an opener reads, the first node's own buffer.
        /// The overloads that write through a raw pointer (byte[] and Func) are left out here, as the library before this check wrote
        /// outside the mapping with them; the next test damages Offset with them inside the buffer.
        /// </summary>
        [TestMethod]
        [DataRow("T[]")]
        [DataRow("ref T")]
        [DataRow("IntPtr")]
        public void ReadAndWrite_NodeOffsetOutsideTheBuffer_UseTheNodesOwnBuffer(string overload)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, NodeCount, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                for (int node = 0; node < NodeCount; node++)
                    Poke(name, NodeField(node, "Offset"), BitConverter.GetBytes(owner.BufferSize + 1000000L));

                AssertUsesOwnBuffer(name, overload, owner, opener);
            }
        }

        /// <summary>
        /// The first node's Offset points at the second node's buffer: the owner writes, and an opener reads, the first node's own
        /// buffer, and the second node's stays as it was.
        /// </summary>
        [TestMethod]
        [DataRow("byte[]")]
        [DataRow("T[]")]
        [DataRow("ref T")]
        [DataRow("IntPtr")]
        [DataRow("Func")]
        public void ReadAndWrite_NodeOffsetAtAnotherNodesBuffer_UseTheNodesOwnBuffer(string overload)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, NodeCount, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                Poke(name, NodeField(0, "Offset"), BitConverter.GetBytes(NodeBuffer(1)));

                AssertUsesOwnBuffer(name, overload, owner, opener);
                CollectionAssert.AreEqual(new byte[NodeSize], Peek(name, NodeBuffer(1), NodeSize), "The second node's buffer was written");
            }
        }

        /// <summary>
        /// Every node's Next and Prev point outside the node list: the owner and an opener still write and read round the ring twice.
        /// </summary>
        [TestMethod]
        public void ReadAndWrite_NodeNeighboursOutsideTheNodeList_RingStillWorks()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, NodeCount, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                for (int node = 0; node < NodeCount; node++)
                {
                    Poke(name, NodeField(node, "Next"), BitConverter.GetBytes(NodeCount + 5));
                    Poke(name, NodeField(node, "Prev"), BitConverter.GetBytes(-3));
                }

                for (int i = 0; i < 2 * NodeCount; i++)
                {
                    byte[] item = Pattern(16, i);
                    Assert.AreEqual(item.Length, owner.Write(item, timeout: 1000), "Write " + (i + 1));
                    var destination = new byte[NodeSize];
                    Assert.AreEqual(item.Length, opener.Read(destination, timeout: 1000), "Read " + (i + 1));
                    CollectionAssert.AreEqual(item, destination.Take(item.Length).ToArray(), "Item " + (i + 1));
                }
            }
        }

        /// <summary>
        /// A node whose AmountWritten is below 0 is skipped as an empty one: each read overload gets the item after it.
        /// </summary>
        [TestMethod]
        [DataRow("byte[]")]
        [DataRow("T[]")]
        [DataRow("out T")]
        [DataRow("IntPtr")]
        [DataRow("Func")]
        public void Read_AmountWrittenBelowZero_SkipsTheNode(string overload)
        {
            string name = Guid.NewGuid().ToString();
            byte[] first = Pattern(16, 1), second = Pattern(16, 2);
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.AreEqual(first.Length, ring.Write(first));
                Poke(name, NodeField(0, "AmountWritten"), BitConverter.GetBytes(-5));
                Assert.AreEqual(second.Length, ring.Write(second));

                byte[] read = ReadWith(overload, ring, second.Length, 1000);

                CollectionAssert.AreEqual(second.Take(read.Length).ToArray(), read, overload + " did not get the item after the damaged node");
                Assert.AreNotEqual(0, read.Length, overload + " read nothing");
            }
        }

        /// <summary>
        /// A node whose AmountWritten is past the node's size reads as a whole node, into a destination with room for more.
        /// </summary>
        [TestMethod]
        [DataRow("byte[]")]
        [DataRow("T[]")]
        [DataRow("IntPtr")]
        public void Read_AmountWrittenPastTheNodeSize_ReadsTheNodeSize(string overload)
        {
            string name = Guid.NewGuid().ToString();
            byte[] item = Pattern(NodeSize, 1);
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.AreEqual(item.Length, ring.Write(item));
                Poke(name, NodeField(0, "AmountWritten"), BitConverter.GetBytes(NodeSize + 100));

                byte[] read = ReadWith(overload, ring, NodeSize + 200, 1000);

                Assert.AreEqual(NodeSize, read.Length, overload + " read past the node");
                CollectionAssert.AreEqual(item, read);
            }
        }

        /// <summary>
        /// A Write(Func) callback that returns more than the node's size, or less than 0: the node holds the node's size, or nothing,
        /// and Write returns that.
        /// </summary>
        [TestMethod]
        [DataRow(NodeSize * 2, NodeSize)]
        [DataRow(-5, 0)]
        public void Write_FuncReturnsOutsideTheNodeSize_NodeHoldsWhatFits(int returned, int held)
        {
            string name = Guid.NewGuid().ToString();
            byte[] item = Pattern(NodeSize, 1);
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.AreEqual(held, ring.Write(p => { Marshal.Copy(item, 0, p, NodeSize); return returned; }), "Write returned");
                Assert.AreEqual(held, BitConverter.ToInt32(Peek(name, NodeField(0, "AmountWritten"), 4), 0), "The node's AmountWritten");

                var destination = new byte[NodeSize * 3];
                Assert.AreEqual(held, ring.Read(destination, timeout: 0), "Read returned");
                CollectionAssert.AreEqual(item.Take(held).ToArray(), destination.Take(held).ToArray());
            }
        }

        /// <summary>
        /// An index in the node header outside the node list: the call that reads it throws InvalidOperationException, naming the
        /// buffer, the field and its value. The index is damaged before the call, which meets it as it reserves a node, or while the
        /// call holds its node, which it then meets as it posts or returns the node.
        /// </summary>
        [TestMethod]
        [DataRow("ReadStart", NodeCount + 3, "Read")]
        [DataRow("ReadStart", -1, "Read")]
        [DataRow("WriteEnd", NodeCount + 3, "Read")]
        [DataRow("WriteStart", NodeCount + 3, "Write")]
        [DataRow("WriteStart", -1, "Write")]
        [DataRow("ReadEnd", NodeCount + 3, "Write")]
        [DataRow("WriteEnd", NodeCount + 3, "Write(Func), damaged as it writes")]
        [DataRow("ReadStart", NodeCount + 3, "Write(Func), damaged as it writes")]
        [DataRow("ReadEnd", NodeCount + 3, "Read(Func), damaged as it reads")]
        [DataRow("WriteStart", NodeCount + 3, "Read(Func), damaged as it reads")]
        public void Call_HeaderIndexOutsideTheNodeList_ThrowsInvalidOperationException(string field, int value, string call)
        {
            string name = Guid.NewGuid().ToString();
            byte[] damage = BitConverter.GetBytes(value);
            using (var ring = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Assert.AreEqual(4, ring.Write(new byte[] { 1, 2, 3, 4 }));
                if (call == "Read" || call == "Write")
                    Poke(name, HeaderField(field), damage);

                InvalidOperationException e = Assert.ThrowsExactly<InvalidOperationException>(() =>
                {
                    if (call == "Read")
                        ring.Read(new byte[NodeSize], timeout: 100);
                    else if (call == "Write")
                        ring.Write(new byte[] { 5, 6, 7, 8 }, timeout: 100);
                    else if (call.StartsWith("Write(Func)", StringComparison.Ordinal))
                        ring.Write(p => { Poke(name, HeaderField(field), damage); return 4; }, timeout: 100);
                    else
                        ring.Read(p => { Poke(name, HeaderField(field), damage); return 4; }, timeout: 100);
                });

                Console.WriteLine(e.Message);
                Assert.Contains("\"" + name + "\"", e.Message);
                Assert.Contains(String.Format("its {0} is {1},", field, value), e.Message);
            }
        }

        /// <summary>
        /// An opener of a node header that does not describe at least 2 nodes of at least 1 byte that fit in the buffer throws
        /// InvalidOperationException naming the buffer, and releases the mapping and the events it opened: once the owner has gone,
        /// no event is left and the name can be created again.
        /// </summary>
        [TestMethod]
        [DataRow("NodeCount", 1000000)]
        [DataRow("NodeCount", 1)]
        [DataRow("NodeCount", -4)]
        [DataRow("NodeBufferSize", 1000000)]
        [DataRow("NodeBufferSize", 1073741824)]
        [DataRow("NodeBufferSize", 0)]
        [DataRow("NodeBufferSize", -64)]
        public void Open_NodeHeaderThatDoesNotFit_ThrowsAndReleasesWhatItOpened(string field, int value)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, NodeCount, NodeSize))
            {
                Poke(name, HeaderField(field), BitConverter.GetBytes(value));

                InvalidOperationException e = Assert.ThrowsExactly<InvalidOperationException>(() =>
                {
                    using (new CircularBuffer(name))
                    {
                    }
                });

                Console.WriteLine(e.Message);
                Assert.Contains("\"" + name + "\"", e.Message);
                Assert.Contains(field == "NodeCount" ? String.Format("gives {0} nodes of {1} bytes", value, NodeSize)
                    : String.Format("gives {0} nodes of {1} bytes", NodeCount, value), e.Message);
            }

            CollectionAssert.AreEqual(new string[0], EventsLeft(name), "Events left once the owner had gone");
            using (new CircularBuffer(name, NodeCount, NodeSize))
            {
            }
        }

        /// <summary>
        /// Writes a whole node with the overload named through the owner, checks that it is in the first node's own buffer, and
        /// reads it back with the same overload through the opener.
        /// </summary>
        static void AssertUsesOwnBuffer(string name, string overload, CircularBuffer owner, CircularBuffer opener)
        {
            byte[] item = Pattern(NodeSize, 1);

            Assert.AreEqual(NodeSize, WriteWith(overload, owner, item), overload + " wrote");
            CollectionAssert.AreEqual(item, Peek(name, NodeBuffer(0), NodeSize), overload + " did not write to the node's own buffer");
            CollectionAssert.AreEqual(item, ReadWith(overload, opener, NodeSize, 1000), overload + " did not read the node's own buffer");
        }

        static byte[] Pattern(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
                data[i] = (byte)(seed * 37 + i + 1);
            return data;
        }

        /// <summary>
        /// Writes <paramref name="data"/>, a multiple of 8 bytes and 64 bytes for "ref T", with the overload named.
        /// </summary>
        /// <returns>The bytes written.</returns>
        static unsafe int WriteWith(string overload, CircularBuffer ring, byte[] data)
        {
            switch (overload)
            {
                case "byte[]":
                    return ring.Write(data);
                case "T[]":
                    {
                        var longs = new long[data.Length / 8];
                        Buffer.BlockCopy(data, 0, longs, 0, data.Length);
                        return ring.Write(longs) * 8;
                    }
                case "ref T":
                    {
                        Whole value;
                        fixed (byte* p = data)
                            value = *(Whole*)p;
                        return ring.Write(ref value);
                    }
                case "IntPtr":
                    fixed (byte* p = data)
                        return ring.Write((IntPtr)p, data.Length);
                case "Func":
                    return ring.Write(p => { Marshal.Copy(data, 0, p, data.Length); return data.Length; });
                default:
                    throw new ArgumentException("Unknown overload: " + overload);
            }
        }

        /// <summary>
        /// Reads one item with the overload named, into a destination of <paramref name="room"/> bytes.
        /// </summary>
        /// <returns>The bytes read: for "ref T" a whole node, for "out T" 8 bytes, for "Func" what the callback saw of <paramref name="room"/>.</returns>
        static unsafe byte[] ReadWith(string overload, CircularBuffer ring, int room, int timeout)
        {
            var destination = new byte[Math.Max(room, NodeSize)];
            int bytes;
            switch (overload)
            {
                case "byte[]":
                    bytes = ring.Read(destination, timeout: timeout);
                    break;
                case "T[]":
                    {
                        var longs = new long[(room + 7) / 8];
                        bytes = ring.Read(longs, timeout: timeout) * 8;
                        Buffer.BlockCopy(longs, 0, destination, 0, bytes);
                        break;
                    }
                case "ref T":
                    {
                        Whole value;
                        bytes = ring.Read(out value, timeout);
                        fixed (byte* p = destination)
                            *(Whole*)p = value;
                        break;
                    }
                case "out T":
                    {
                        long value;
                        bytes = ring.Read(out value, timeout);
                        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, destination, 0, 8);
                        break;
                    }
                case "IntPtr":
                    fixed (byte* p = destination)
                        bytes = ring.Read((IntPtr)p, room, timeout);
                    break;
                case "Func":
                    {
                        int calls = 0;
                        int want = Math.Min(room, NodeSize);
                        bytes = ring.Read(p => { calls++; Marshal.Copy(p, destination, 0, want); return want; }, timeout);
                        Assert.IsTrue(calls <= 1, "The read callback ran " + calls + " times");
                        break;
                    }
                default:
                    throw new ArgumentException("Unknown overload: " + overload);
            }
            return destination.Take(bytes).ToArray();
        }

        /// <summary>
        /// Writes <paramref name="bytes"/> at <paramref name="position"/> in the buffer of <paramref name="name"/>, as another process
        /// that opens its shared memory can.
        /// </summary>
        static void Poke(string name, long position, byte[] bytes)
        {
            using (var mapping = new EventFreeBuffer(name))
                mapping.WriteBytes(bytes, position);
        }

        static byte[] Peek(string name, long position, int count)
        {
            using (var mapping = new EventFreeBuffer(name))
                return mapping.ReadBytes(count, position);
        }

        // Where things are in a ring of NodeCount nodes of NodeSize bytes, from the start of its buffer
        static long HeaderField(string field)
        {
            return Marshal.OffsetOf(typeof(CircularBuffer.NodeHeader), field).ToInt64();
        }

        static long NodeField(int node, string field)
        {
            return NodeHeaderSize + (long)NodeStructSize * node + Marshal.OffsetOf(typeof(CircularBuffer.Node), field).ToInt64();
        }

        static long NodeBuffer(int node)
        {
            return NodeHeaderSize + (long)NodeStructSize * NodeCount + (long)NodeSize * node;
        }

        /// <summary>
        /// The ring events of <paramref name="name"/> that are still there: on Windows those that open by name, on Linux those with
        /// a file in /dev/shm, and on macOS those with a lock file.
        /// </summary>
        static string[] EventsLeft(string name)
        {
            string[] events = { name + "_evt_dataexists", name + "_evt_nodeavail" };
#if NET
            if (OperatingSystem.IsLinux())
                return Directory.GetFiles("/dev/shm", "SharedMemory.*").Select(Path.GetFileName)
                    .Where(file => events.Any(e => file.EndsWith("." + e, StringComparison.Ordinal))).ToArray();
            if (OperatingSystem.IsMacOS())
                return Directory.GetFiles(UnixBufferTests.Files.MacFolder + ".SharedMemory-uid" + UnixBufferTests.Files.EffectiveUserId())
                    .Select(Path.GetFileName).Where(file => events.Any(e => file.StartsWith(e + "~", StringComparison.Ordinal))).ToArray();
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
#endif
            var left = new System.Collections.Generic.List<string>();
            foreach (string eventName in events)
            {
                EventWaitHandle handle;
                if (EventWaitHandle.TryOpenExisting(eventName, out handle))
                {
                    handle.Dispose();
                    left.Add(eventName);
                }
            }
            return left.ToArray();
        }
    }
}
