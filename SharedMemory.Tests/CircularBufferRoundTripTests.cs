using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// What each pair of <see cref="CircularBuffer"/> Read and Write overloads copies, for a whole node and for part of one. The owner
    /// and an opener take turns to write and read, round the ring and into its first node again, so that every node is used from both
    /// instances.
    /// </summary>
    [TestClass]
    public class CircularBufferRoundTripTests
    {
        const int NodeCount = 3;
        const int NodeSize = 64;

        [StructLayout(LayoutKind.Sequential)]
        struct Pair
        {
            public int A;
            public int B;
        }

        // 16 bytes: part of a node
        [StructLayout(LayoutKind.Sequential)]
        struct Quarter
        {
            public long A, B;
        }

        // 64 bytes: a whole node
        [StructLayout(LayoutKind.Sequential)]
        struct Whole
        {
            public long A, B, C, D, E, F, G, H;
        }

        [TestMethod]
        [DataRow("byte[]", NodeSize)]
        [DataRow("byte[]", 20)]
        [DataRow("T[]", NodeSize)]
        [DataRow("T[]", 24)]
        [DataRow("ref T", NodeSize)]
        [DataRow("ref T", 16)]
        [DataRow("IntPtr", NodeSize)]
        [DataRow("IntPtr", 20)]
        [DataRow("Func", NodeSize)]
        [DataRow("Func", 20)]
        public void ReadAndWrite_WholeOrPartNode_RoundTripThroughEveryNode(string overload, int bytes)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new CircularBuffer(name, NodeCount, NodeSize))
            using (var opener = new CircularBuffer(name))
            {
                for (int round = 0; round <= NodeCount; round++)
                {
                    CircularBuffer writer = round % 2 == 0 ? owner : opener;
                    CircularBuffer reader = round % 2 == 0 ? opener : owner;
                    byte[] data = Pattern(bytes, round);
                    string where = String.Format("{0}, {1} bytes, round {2}", overload, bytes, round);

                    byte[] read = RoundTrip(overload, writer, reader, data, where);

                    CollectionAssert.AreEqual(data, read, "The bytes read differ from the bytes written: " + where);
                }
            }
        }

        static byte[] Pattern(int length, int round)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
                data[i] = (byte)(round * 31 + i + 1);
            return data;
        }

        /// <summary>
        /// Writes <paramref name="data"/> through <paramref name="writer"/> and reads it back through <paramref name="reader"/>, with
        /// the overload named, checking what each call returns.
        /// </summary>
        /// <returns>The bytes read.</returns>
        static unsafe byte[] RoundTrip(string overload, CircularBuffer writer, CircularBuffer reader, byte[] data, string where)
        {
            int bytes = data.Length;
            switch (overload)
            {
                case "byte[]":
                    {
                        Assert.AreEqual(bytes, writer.Write(data), "Write returned: " + where);
                        var destination = new byte[NodeSize];
                        Assert.AreEqual(bytes, reader.Read(destination), "Read returned: " + where);
                        return Take(destination, bytes);
                    }
                case "T[]":
                    {
                        var source = new Pair[bytes / 8];
                        fixed (byte* p = data)
                            for (int i = 0; i < source.Length; i++)
                                source[i] = ((Pair*)p)[i];
                        Assert.AreEqual(source.Length, writer.Write(source), "Write returned: " + where);
                        var destination = new Pair[NodeSize / 8];
                        Assert.AreEqual(source.Length, reader.Read(destination), "Read returned: " + where);
                        var read = new byte[bytes];
                        fixed (byte* p = read)
                            for (int i = 0; i < source.Length; i++)
                                ((Pair*)p)[i] = destination[i];
                        return read;
                    }
                case "ref T":
                    return bytes == NodeSize ? Structure<Whole>(writer, reader, data, where) : Structure<Quarter>(writer, reader, data, where);
                case "IntPtr":
                    {
                        var destination = new byte[NodeSize];
                        fixed (byte* source = data)
                            Assert.AreEqual(bytes, writer.Write((IntPtr)source, bytes), "Write returned: " + where);
                        fixed (byte* target = destination)
                            Assert.AreEqual(bytes, reader.Read((IntPtr)target, NodeSize), "Read returned: " + where);
                        return Take(destination, bytes);
                    }
                case "Func":
                    {
                        Assert.AreEqual(bytes, writer.Write(p => { Marshal.Copy(data, 0, p, bytes); return bytes; }), "Write returned: " + where);
                        var destination = new byte[NodeSize];
                        int calls = 0;
                        Assert.AreEqual(NodeSize, reader.Read(p => { calls++; Marshal.Copy(p, destination, 0, NodeSize); return NodeSize; }), "Read returned: " + where);
                        Assert.AreEqual(1, calls, "The read callback's calls: " + where);
                        return Take(destination, bytes);
                    }
                default:
                    throw new ArgumentException("Unknown overload: " + overload);
            }
        }

        static unsafe byte[] Structure<T>(CircularBuffer writer, CircularBuffer reader, byte[] data, string where)
            where T : struct
        {
            Assert.AreEqual(data.Length, Marshal.SizeOf(typeof(T)));
            T source;
            fixed (byte* p = data)
                source = (T)Marshal.PtrToStructure((IntPtr)p, typeof(T));
            Assert.AreEqual(data.Length, writer.Write(ref source), "Write returned: " + where);
            T destination;
            Assert.AreEqual(data.Length, reader.Read(out destination), "Read returned: " + where);
            var read = new byte[data.Length];
            fixed (byte* p = read)
                Marshal.StructureToPtr(destination, (IntPtr)p, false);
            return read;
        }

        static byte[] Take(byte[] data, int count)
        {
            var taken = new byte[count];
            Array.Copy(data, taken, count);
            return taken;
        }
    }
}
