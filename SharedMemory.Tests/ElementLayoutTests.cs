// SharedMemory (File: SharedMemoryTests\ElementLayoutTests.cs)
// Copyright (c) 2026 Justin Stenning
// http://spazzarama.com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// The SharedMemory library is inspired by the following Code Project article:
//   "Fast IPC Communication Using Shared Memory and InterlockedCompareExchange"
//   http://www.codeproject.com/Articles/14740/Fast-IPC-Communication-Using-Shared-Memory-and-Int

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// One element layout in shared memory on every runtime (#81): the layout 64-bit .NET 10 gives a type. .NET Framework and
    /// .NET Core 2.1 lay out structures holding a DateTime or a decimal differently, so there the library copies them field
    /// by field. The layout and the path chosen are internal, and read here by reflection.
    /// </summary>
    [TestClass]
    public class ElementLayoutTests
    {
        #region The pinned shapes

        // Private on purpose: the library must handle element types it cannot see.
        [StructLayout(LayoutKind.Sequential)]
        private struct WithDateTime { public int A; public DateTime D; public int B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithDecimal { public int A; public decimal C; public int B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithCurrency
        {
            public int A;
#pragma warning disable CS0618 // Obsolete on .NET 5 and later, but existing callers' structures still carry it
            [MarshalAs(UnmanagedType.Currency)]
#pragma warning restore CS0618
            public decimal C;
            public int B;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithBoolChar { public bool F; public char L; public int N; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NestedDateTime { public byte X; public WithDateTime Inner; public short Y; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct Packed { public byte A; public DateTime D; public decimal C; public int B; }

        [StructLayout(LayoutKind.Explicit)]
        private struct Explicit
        {
            [FieldOffset(0)] public int A;
            [FieldOffset(8)] public DateTime D;
            [FieldOffset(16)] public int B;
            [FieldOffset(16)] public float F;
        }

        [StructLayout(LayoutKind.Sequential, Size = 40)]
        private struct Sized { public int A; public DateTime D; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blittable { public byte A; public long B; public short C; public double D; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithGuid { public byte A; public Guid G; public byte B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithTimeSpan { public byte A; public TimeSpan T; public int B; }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct FixedChars { public fixed char C[3]; public DateTime D; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithIntPtr { public byte A; public IntPtr P; public int B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithNullable { public int A; public int? N; public int B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithKeyValuePair { public int A; public KeyValuePair<int, int> P; public int B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WithDateTimeOffset { public int A; public DateTimeOffset O; public int B; }

        private static readonly DateTime UtcTime = new DateTime(0x0102030405060708, DateTimeKind.Utc);           // 08-07-06-05-04-03-02-41
        private static readonly DateTime UnspecifiedTime = new DateTime(0x0102030405060708, DateTimeKind.Unspecified); // 08-07-06-05-04-03-02-01
        private static readonly decimal Amount = new decimal(0x33333333, 0x44444444, 0x55555555, true, 7);      // 00-00-07-80-55-55-55-55-33-33-33-33-44-44-44-44
        private static readonly Guid Id = new Guid(0x11223344, 0x5566, 0x7788, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00);

        private static string Show(DateTime value)
        {
            return value.Ticks.ToString("X16") + " " + value.Kind;
        }

        private static string Show(decimal value)
        {
            int[] bits = decimal.GetBits(value);
            return bits[0].ToString("X8") + " " + bits[1].ToString("X8") + " " + bits[2].ToString("X8") + " " + bits[3].ToString("X8");
        }

        /// <summary>
        /// A pinned shape: its layout on 64-bit .NET 10 as the library describes it (each leaf's field path, offset and size),
        /// values that differ by element, and each value's bytes in that layout ("??" for padding).
        /// </summary>
        private sealed class Shape<T> where T : struct
        {
            public string Name;
            public string Net10Layout;
            public int Size;
            public Func<int, T> Make;
            public string Image;
            public int First;
            public Func<T, string> Show;

            public string ImageOf(int i)
            {
                return string.Format(Image, (First + i).ToString("X2"));
            }
        }

        private interface IShapeCheck
        {
            void Check<T>(Shape<T> shape) where T : struct;
        }

        private static unsafe void ForEachShape(IShapeCheck check)
        {
            check.Check(new Shape<WithDateTime>
            {
                Name = "WithDateTime", Net10Layout = "size 24; A 0+4; D 8+8; B 16+4", Size = 24, First = 0x11,
                Make = i => new WithDateTime { A = 0x11111111 + i, D = UtcTime, B = 0x22222222 },
                Image = "{0}-11-11-11-??-??-??-??-08-07-06-05-04-03-02-41-22-22-22-22-??-??-??-??",
                Show = v => v.A.ToString("X8") + "/" + Show(v.D) + "/" + v.B.ToString("X8"),
            });
            check.Check(new Shape<WithDecimal>
            {
                Name = "WithDecimal", Net10Layout = "size 32; A 0+4; C 8+16; B 24+4", Size = 32, First = 0x11,
                Make = i => new WithDecimal { A = 0x11111111 + i, C = Amount, B = 0x22222222 },
                Image = "{0}-11-11-11-??-??-??-??-00-00-07-80-55-55-55-55-33-33-33-33-44-44-44-44-22-22-22-22-??-??-??-??",
                Show = v => v.A.ToString("X8") + "/" + Show(v.C) + "/" + v.B.ToString("X8"),
            });
            check.Check(new Shape<WithCurrency>
            {
                Name = "WithCurrency", Net10Layout = "size 32; A 0+4; C 8+16; B 24+4", Size = 32, First = 0x11,
                Make = i => new WithCurrency { A = 0x11111111 + i, C = Amount, B = 0x22222222 },
                Image = "{0}-11-11-11-??-??-??-??-00-00-07-80-55-55-55-55-33-33-33-33-44-44-44-44-22-22-22-22-??-??-??-??",
                Show = v => v.A.ToString("X8") + "/" + Show(v.C) + "/" + v.B.ToString("X8"),
            });
            check.Check(new Shape<WithBoolChar>
            {
                Name = "WithBoolChar", Net10Layout = "size 8; F 0+1; L 2+2; N 4+4", Size = 8, First = 0x41,
                Make = i => new WithBoolChar { F = true, L = (char)('A' + i), N = 0x22222222 },
                Image = "01-??-{0}-00-22-22-22-22",
                Show = v => v.F + "/" + v.L + "/" + v.N.ToString("X8"),
            });
            check.Check(new Shape<NestedDateTime>
            {
                Name = "NestedDateTime", Net10Layout = "size 40; X 0+1; Inner.A 8+4; Inner.D 16+8; Inner.B 24+4; Y 32+2", Size = 40, First = 0x77,
                Make = i => new NestedDateTime { X = (byte)(0x77 + i), Inner = new WithDateTime { A = 0x33333333, D = UnspecifiedTime, B = 0x44444444 }, Y = 0x6666 },
                Image = "{0}-??-??-??-??-??-??-??-33-33-33-33-??-??-??-??-08-07-06-05-04-03-02-01-44-44-44-44-??-??-??-??-66-66-??-??-??-??-??-??",
                Show = v => v.X.ToString("X2") + "/" + v.Inner.A.ToString("X8") + "/" + Show(v.Inner.D) + "/" + v.Inner.B.ToString("X8") + "/" + v.Y.ToString("X4"),
            });
            check.Check(new Shape<Packed>
            {
                Name = "Packed", Net10Layout = "size 29; A 0+1; D 1+8; C 9+16; B 25+4", Size = 29, First = 0x77,
                Make = i => new Packed { A = (byte)(0x77 + i), D = UtcTime, C = Amount, B = 0x22222222 },
                Image = "{0}-08-07-06-05-04-03-02-41-00-00-07-80-55-55-55-55-33-33-33-33-44-44-44-44-22-22-22-22",
                Show = v => v.A.ToString("X2") + "/" + Show(v.D) + "/" + Show(v.C) + "/" + v.B.ToString("X8"),
            });
            check.Check(new Shape<Explicit>
            {
                Name = "Explicit", Net10Layout = "size 24; A 0+4; D 8+8; B 16+4; F 16+4", Size = 24, First = 0x11,
                Make = i => new Explicit { A = 0x11111111 + i, D = UtcTime, B = 0x22222222 },
                Image = "{0}-11-11-11-??-??-??-??-08-07-06-05-04-03-02-41-22-22-22-22-??-??-??-??",
                Show = v => v.A.ToString("X8") + "/" + Show(v.D) + "/" + v.B.ToString("X8"),
            });
            check.Check(new Shape<Sized>
            {
                Name = "Sized", Net10Layout = "size 40; A 0+4; D 8+8", Size = 40, First = 0x11,
                Make = i => new Sized { A = 0x11111111 + i, D = UtcTime },
                Image = "{0}-11-11-11-??-??-??-??-08-07-06-05-04-03-02-41" + Padding(24),
                Show = v => v.A.ToString("X8") + "/" + Show(v.D),
            });
            check.Check(new Shape<Blittable>
            {
                Name = "Blittable", Net10Layout = "size 32; A 0+1; B 8+8; C 16+2; D 24+8", Size = 32, First = 0x77,
                Make = i => new Blittable { A = (byte)(0x77 + i), B = 0x0102030405060708, C = 0x6666, D = 1.5 },
                Image = "{0}-??-??-??-??-??-??-??-08-07-06-05-04-03-02-01-66-66-??-??-??-??-??-??-00-00-00-00-00-00-F8-3F",
                Show = v => v.A.ToString("X2") + "/" + v.B.ToString("X16") + "/" + v.C.ToString("X4") + "/" + v.D.ToString("R"),
            });
            check.Check(new Shape<WithGuid>
            {
                Name = "WithGuid", Net10Layout = "size 24; A 0+1; G 4+16; B 20+1", Size = 24, First = 0x77,
                Make = i => new WithGuid { A = (byte)(0x77 + i), G = Id, B = 0x66 },
                Image = "{0}-??-??-??-44-33-22-11-66-55-88-77-99-AA-BB-CC-DD-EE-FF-00-66-??-??-??",
                Show = v => v.A.ToString("X2") + "/" + v.G + "/" + v.B.ToString("X2"),
            });
            check.Check(new Shape<WithTimeSpan>
            {
                Name = "WithTimeSpan", Net10Layout = "size 24; A 0+1; T 8+8; B 16+4", Size = 24, First = 0x77,
                Make = i => new WithTimeSpan { A = (byte)(0x77 + i), T = new TimeSpan(0x0102030405060708), B = 0x22222222 },
                Image = "{0}-??-??-??-??-??-??-??-08-07-06-05-04-03-02-01-22-22-22-22-??-??-??-??",
                Show = v => v.A.ToString("X2") + "/" + v.T.Ticks.ToString("X16") + "/" + v.B.ToString("X8"),
            });
            check.Check(new Shape<FixedChars>
            {
                Name = "FixedChars", Net10Layout = "size 16; C 0+6; D 8+8", Size = 16, First = 0x61,
                Make = MakeFixedChars,
                Image = "{0}-00-62-00-63-00-??-??-08-07-06-05-04-03-02-41",
                Show = ShowFixedChars,
            });
        }

        private static unsafe FixedChars MakeFixedChars(int i)
        {
            var value = new FixedChars { D = UtcTime };
            value.C[0] = (char)('a' + i);
            value.C[1] = 'b';
            value.C[2] = 'c';
            return value;
        }

        private static unsafe string ShowFixedChars(FixedChars value)
        {
            return new string(new[] { value.C[0], value.C[1], value.C[2] }) + "/" + Show(value.D);
        }

        private static string Padding(int count)
        {
            var text = new StringBuilder();
            for (int i = 0; i < count; i++)
                text.Append("-??");
            return text.ToString();
        }

        #endregion

        #region Reading the internals

        private static Type FastStructureOf(Type type)
        {
            return typeof(FastStructure<>).MakeGenericType(type);
        }

        private static object Internal(Type type, string field)
        {
            FieldInfo info = FastStructureOf(type).GetField(field, BindingFlags.NonPublic | BindingFlags.Static);
            if (info == null)
                Assert.Fail("FastStructure<" + type.Name + "> has no internal field " + field + ".");
            return info.GetValue(null);
        }

        /// <summary>The library's layout of T in shared memory, as text, or "none" if it has none.</summary>
        private static string LayoutOf<T>() where T : struct
        {
            object layout = Internal(typeof(T), "Layout");
            return layout == null ? "none" : layout.ToString();
        }

        private static bool UsesNativeCopy<T>() where T : struct
        {
            return (bool)Internal(typeof(T), "UsesNativeCopy");
        }

        /// <summary>
        /// The field-by-field copies for T, built whether or not this runtime needs them: load, store, array copy from memory,
        /// array copy to memory.
        /// </summary>
        private static Delegate[] FieldByFieldCopies<T>() where T : struct
        {
            MethodInfo build = FastStructureOf(typeof(T)).GetMethod("BuildFieldByFieldCopies", BindingFlags.NonPublic | BindingFlags.Static);
            if (build == null)
                Assert.Fail("FastStructure<" + typeof(T).Name + "> has no internal method BuildFieldByFieldCopies.");
            var copies = (Delegate[])build.Invoke(null, null);
            Assert.IsNotNull(copies, "no field-by-field copies for " + typeof(T).Name);
            return copies;
        }

        #endregion

        #region Byte helpers

        private static unsafe byte[] BytesAt(byte* memory, int count)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++)
                bytes[i] = memory[i];
            return bytes;
        }

        private static unsafe void Fill(byte* memory, int length, byte value)
        {
            for (int i = 0; i < length; i++)
                memory[i] = value;
        }

        /// <summary>Writes the image's bytes, padding as 0xEE.</summary>
        private static unsafe void WriteImage(byte* memory, string image)
        {
            string[] bytes = image.Split('-');
            for (int i = 0; i < bytes.Length; i++)
                memory[i] = bytes[i] == "??" ? (byte)0xEE : Convert.ToByte(bytes[i], 16);
        }

        private static string Images<T>(Shape<T> shape, params int[] elements) where T : struct
        {
            var parts = new string[elements.Length];
            for (int i = 0; i < elements.Length; i++)
                parts[i] = shape.ImageOf(elements[i]);
            return string.Join("-", parts);
        }

        private static string Bytes(byte[] bytes)
        {
            return BitConverter.ToString(bytes);
        }

        #endregion

        #region The pin test

        /// <summary>
        /// Pins the layout each shape has in shared memory to the layout 64-bit .NET 10 gives it, in every pairing. The values
        /// are hard-coded from .NET 10 on purpose: two processes on different runtimes or library versions share these bytes,
        /// so the layout must not drift. If a future .NET changes its layout rules, the .NET test below fails, not this one.
        /// </summary>
        [TestMethod]
        public void PinnedShapes_HaveTheNet10Layout()
        {
            var check = new PinCheck();
            ForEachShape(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches.ToArray()));
        }

        private class PinCheck : IShapeCheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public void Check<T>(Shape<T> shape) where T : struct
            {
                string layout = LayoutOf<T>();
                if (layout != shape.Net10Layout)
                    Mismatches.Add(shape.Name + ": " + layout + ", expected " + shape.Net10Layout);
                if (FastStructure.SizeOf<T>() != shape.Size || FastStructure<T>.Size != shape.Size)
                    Mismatches.Add(shape.Name + ": SizeOf " + FastStructure.SizeOf<T>() + ", Size " + FastStructure<T>.Size + ", expected " + shape.Size);
            }
        }

#if NET
        /// <summary>
        /// The other half of the pin, which catches a change in .NET's layout rules when the suite runs on that .NET: the
        /// runtime's own layout of each shape, measured with pointers into a real instance, is the pinned one, and the library
        /// copies the shapes as blocks of memory. Runs in the .NET test build only, which CI runs 64-bit.
        /// </summary>
        [TestMethod]
        public void PinnedShapes_AreHowDotNetLaysThemOut_AndCopyNatively()
        {
            Assert.AreEqual(8, IntPtr.Size, "the pinned layout is .NET's on 64-bit");
            var check = new RuntimeCheck();
            ForEachShape(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches));
        }

        private class RuntimeCheck : IShapeCheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public void Check<T>(Shape<T> shape) where T : struct
            {
                string runtime = RuntimeLayout(typeof(T));
                if (runtime != shape.Net10Layout)
                    Mismatches.Add(shape.Name + ": .NET lays it out as " + runtime + ", pinned " + shape.Net10Layout);
                if (!UsesNativeCopy<T>())
                    Mismatches.Add(shape.Name + ": copied field by field");
            }
        }

        private static string Leaves(int size, params string[] leaves)
        {
            return "size " + size + (leaves.Length == 0 ? "" : "; " + string.Join("; ", leaves));
        }

        private static unsafe string Leaf(string name, void* field, void* start, int size)
        {
            return name + " " + ((byte*)field - (byte*)start) + "+" + size;
        }

        private static unsafe string RuntimeLayout(Type type)
        {
            if (type == typeof(WithDateTime)) { var v = new WithDateTime(); return Leaves(sizeof(WithDateTime), Leaf("A", &v.A, &v, 4), Leaf("D", &v.D, &v, 8), Leaf("B", &v.B, &v, 4)); }
            if (type == typeof(WithDecimal)) { var v = new WithDecimal(); return Leaves(sizeof(WithDecimal), Leaf("A", &v.A, &v, 4), Leaf("C", &v.C, &v, 16), Leaf("B", &v.B, &v, 4)); }
            if (type == typeof(WithCurrency)) { var v = new WithCurrency(); return Leaves(sizeof(WithCurrency), Leaf("A", &v.A, &v, 4), Leaf("C", &v.C, &v, 16), Leaf("B", &v.B, &v, 4)); }
            if (type == typeof(WithBoolChar)) { var v = new WithBoolChar(); return Leaves(sizeof(WithBoolChar), Leaf("F", &v.F, &v, 1), Leaf("L", &v.L, &v, 2), Leaf("N", &v.N, &v, 4)); }
            if (type == typeof(NestedDateTime)) { var v = new NestedDateTime(); return Leaves(sizeof(NestedDateTime), Leaf("X", &v.X, &v, 1), Leaf("Inner.A", &v.Inner.A, &v, 4), Leaf("Inner.D", &v.Inner.D, &v, 8), Leaf("Inner.B", &v.Inner.B, &v, 4), Leaf("Y", &v.Y, &v, 2)); }
            if (type == typeof(Packed)) { var v = new Packed(); return Leaves(sizeof(Packed), Leaf("A", &v.A, &v, 1), Leaf("D", &v.D, &v, 8), Leaf("C", &v.C, &v, 16), Leaf("B", &v.B, &v, 4)); }
            if (type == typeof(Explicit)) { var v = new Explicit(); return Leaves(sizeof(Explicit), Leaf("A", &v.A, &v, 4), Leaf("D", &v.D, &v, 8), Leaf("B", &v.B, &v, 4), Leaf("F", &v.F, &v, 4)); }
            if (type == typeof(Sized)) { var v = new Sized(); return Leaves(sizeof(Sized), Leaf("A", &v.A, &v, 4), Leaf("D", &v.D, &v, 8)); }
            if (type == typeof(Blittable)) { var v = new Blittable(); return Leaves(sizeof(Blittable), Leaf("A", &v.A, &v, 1), Leaf("B", &v.B, &v, 8), Leaf("C", &v.C, &v, 2), Leaf("D", &v.D, &v, 8)); }
            if (type == typeof(WithGuid)) { var v = new WithGuid(); return Leaves(sizeof(WithGuid), Leaf("A", &v.A, &v, 1), Leaf("G", &v.G, &v, 16), Leaf("B", &v.B, &v, 1)); }
            if (type == typeof(WithTimeSpan)) { var v = new WithTimeSpan(); return Leaves(sizeof(WithTimeSpan), Leaf("A", &v.A, &v, 1), Leaf("T", &v.T, &v, 8), Leaf("B", &v.B, &v, 4)); }
            if (type == typeof(FixedChars)) { var v = new FixedChars(); return Leaves(sizeof(FixedChars), Leaf("C", v.C, &v, 6), Leaf("D", &v.D, &v, 8)); }
            throw new ArgumentException("no runtime layout for " + type.Name);
        }

        [TestMethod]
        public void CharacterisedTypesAndLibraryStructures_CopyNatively_OnDotNet()
        {
            var check = new NativeCheck();
            CharacterisedTypes.ForEach(check);
#pragma warning disable CS0618 // Obsolete, but public until 3.0, so its layout stays pinned
            check.Check<RpcProtocolHeaderV1>("RpcProtocolHeaderV1", 32);
#pragma warning restore CS0618
            Assert.AreEqual("", string.Join("\n", check.Mismatches));
        }
#endif

#if NETFRAMEWORK
        /// <summary>
        /// .NET Framework lays out structures holding a DateTime or a decimal differently from .NET 10, so they are copied
        /// field by field; blittable structures and those with bool or char are laid out the same, and copied as blocks.
        /// </summary>
        [TestMethod]
        public void NetFramework_CopiesDateTimeAndDecimalStructuresFieldByField_AndTheRestNatively()
        {
            var mismatches = new List<string>();
            Action<string, bool, bool> expect = (name, native, expected) =>
            {
                if (native != expected)
                    mismatches.Add(name + ": " + (native ? "native" : "field by field") + ", expected " + (expected ? "native" : "field by field"));
            };
            expect("WithDateTime", UsesNativeCopy<WithDateTime>(), false);
            expect("WithDecimal", UsesNativeCopy<WithDecimal>(), false);
            expect("WithCurrency", UsesNativeCopy<WithCurrency>(), false);
            expect("NestedDateTime", UsesNativeCopy<NestedDateTime>(), false);
            expect("Blittable", UsesNativeCopy<Blittable>(), true);
            expect("WithBoolChar", UsesNativeCopy<WithBoolChar>(), true);
            var check = new NativeCheck();
            CharacterisedTypes.ForEach(check);
            mismatches.AddRange(check.Mismatches);
            Assert.AreEqual("", string.Join("\n", mismatches.ToArray()));
        }
#endif

#if HAS_RPC_BUFFER
        /// <summary>
        /// The header at the start of every RpcBuffer packet, which the process at the other end of a channel reads: 32 bytes,
        /// every byte a field, laid out alike on every runtime and copied as one block.
        /// </summary>
        [TestMethod]
        public void RpcPacketHeader_HasTheProtocolsLayout_AndCopiesNatively()
        {
            Assert.AreEqual(
                "size 32; HeaderLength 0+1; MsgType 1+1; Flags 2+2; PayloadSize 4+4; MsgId 8+8; ResponseId 16+8; PayloadOffset 24+4; PacketLength 28+4; " +
                    "SizeOf 32; copied as one block True",
                LayoutOf<RpcPacketHeader>() + "; SizeOf " + FastStructure.SizeOf<RpcPacketHeader>() + "; copied as one block " + UsesNativeCopy<RpcPacketHeader>());
        }
#endif

        /// <summary>The characterised type table and the library's own structures: copied as blocks of memory.</summary>
        private class NativeCheck : CharacterisedTypes.ICheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public void Check<T>(string name, int expectedSize) where T : struct
            {
                if (!UsesNativeCopy<T>())
                    Mismatches.Add(name + ": copied field by field");
            }
        }

        /// <summary>
        /// The characterised type table, in the layout in shared memory: every type keeps 2.3.3's size, and types holding a pointer-sized
        /// field have no layout of their own, keeping the runtime's.
        /// </summary>
        [TestMethod]
        public void CharacterisedTypes_KeepTheirSizes()
        {
            var check = new SizeCheck();
            CharacterisedTypes.ForEach(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches.ToArray()));
        }

        private class SizeCheck : CharacterisedTypes.ICheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public void Check<T>(string name, int expectedSize) where T : struct
            {
                string layout = LayoutOf<T>();
                bool pointerSized = name == "IntPtr" || name == "UIntPtr" || name == "WithPointer" || name == "CompatibleStructure" || name == "ComplexStructure";
                if (pointerSized ? layout != "none" : !layout.StartsWith("size " + expectedSize + ";"))
                    Mismatches.Add(name + ": " + layout + (pointerSized ? ", expected none" : ", expected size " + expectedSize));
                if (FastStructure.SizeOf<T>() != expectedSize)
                    Mismatches.Add(name + ": SizeOf " + FastStructure.SizeOf<T>() + ", expected " + expectedSize);
            }
        }

        /// <summary>
        /// A structure with an IntPtr has no layout of its own: it keeps the runtime's, copied as a block, and is not
        /// portable across bitness or runtimes. No new exception.
        /// </summary>
        [TestMethod]
        public void StructureWithIntPtr_KeepsTheRuntimeLayout()
        {
            Assert.AreEqual("none", LayoutOf<WithIntPtr>());
            Assert.IsTrue(UsesNativeCopy<WithIntPtr>());
            Assert.AreEqual(IntPtr.Size == 8 ? 24 : 12, FastStructure.SizeOf<WithIntPtr>());
        }

        /// <summary>
        /// Fields of generic structures, and core-library structures other than DateTime, TimeSpan, decimal and Guid, give a
        /// structure no layout of its own: it keeps the runtime's, as before. Marshal.SizeOf accepts these two everywhere.
        /// </summary>
        [TestMethod]
        public void GenericStructureFields_KeepTheRuntimeLayout()
        {
            Assert.AreEqual("none", LayoutOf<WithNullable>(), "Nullable<int>");
            Assert.IsTrue(UsesNativeCopy<WithNullable>(), "Nullable<int>");
            Assert.AreEqual(16, FastStructure.SizeOf<WithNullable>(), "Nullable<int>");
            Assert.AreEqual("none", LayoutOf<WithKeyValuePair>(), "KeyValuePair<int, int>");
            Assert.IsTrue(UsesNativeCopy<WithKeyValuePair>(), "KeyValuePair<int, int>");
            Assert.AreEqual(16, FastStructure.SizeOf<WithKeyValuePair>(), "KeyValuePair<int, int>");
        }

        /// <summary>
        /// A DateTimeOffset field: Marshal.SizeOf refuses the structure on .NET 10, so FastStructure does too, as before. On
        /// .NET Framework it accepts it, and the structure keeps the runtime's layout, which differs from .NET's.
        /// </summary>
        [TestMethod]
        public void DateTimeOffsetField_RefusedOnDotNet_RuntimeLayoutOnNetFramework()
        {
#if NETFRAMEWORK
            Assert.AreEqual("none", LayoutOf<WithDateTimeOffset>());
            Assert.IsTrue(UsesNativeCopy<WithDateTimeOffset>());
            Assert.AreEqual(IntPtr.Size == 8 ? 24 : 20, FastStructure.SizeOf<WithDateTimeOffset>());
#else
            Assert.ThrowsExactly<TypeInitializationException>(() => FastStructure.SizeOf<WithDateTimeOffset>());
#endif
        }

        #endregion

        #region Round trips through every copy path

        [TestMethod]
        public void FastStructure_SingleCopies_UseTheNet10Layout()
        {
            var check = new SingleCopyCheck();
            ForEachShape(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches.ToArray()));
        }

        private class SingleCopyCheck : IShapeCheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public unsafe void Check<T>(Shape<T> shape) where T : struct
            {
                const int at = 3;    // not aligned
                byte* memory = stackalloc byte[at + shape.Size + 8];
                T value = shape.Make(0);
                string want = shape.Show(value);

                Fill(memory, at + shape.Size + 8, 0xCC);
                FastStructure.StructureToPtr(ref value, (IntPtr)(memory + at));
                Expect(Mismatches, shape.Name + " StructureToPtr", BytesAt(memory + at, shape.Size), shape.ImageOf(0));
                Untouched(Mismatches, shape.Name + " StructureToPtr", memory, at, shape.Size);
                Fill(memory, at + shape.Size + 8, 0xCC);
                FastStructure<T>.StructureToPtr(ref value, (IntPtr)(memory + at));
                Expect(Mismatches, shape.Name + " FastStructure<T>.StructureToPtr", BytesAt(memory + at, shape.Size), shape.ImageOf(0));

                WriteImage(memory + at, shape.ImageOf(0));
                Same(Mismatches, shape.Name + " PtrToStructure", shape.Show(FastStructure.PtrToStructure<T>((IntPtr)(memory + at))), want);
                Same(Mismatches, shape.Name + " FastStructure<T>.PtrToStructure", shape.Show(FastStructure<T>.PtrToStructure((IntPtr)(memory + at))), want);

                byte[] bytes = FastStructure.ToBytes(ref value);
                Expect(Mismatches, shape.Name + " ToBytes", bytes, shape.ImageOf(0));
                Same(Mismatches, shape.Name + " FromBytes", shape.Show(FastStructure.FromBytes<T>(bytes, 0)), want);

                var buffer = new byte[shape.Size + 5];
                FastStructure.CopyTo(ref value, buffer, 5);
                var copied = new byte[shape.Size];
                Array.Copy(buffer, 5, copied, 0, shape.Size);
                Expect(Mismatches, shape.Name + " CopyTo", copied, shape.ImageOf(0));
                Same(Mismatches, shape.Name + " FromBytes at 5", shape.Show(FastStructure.FromBytes<T>(buffer, 5)), want);
            }
        }

        [TestMethod]
        public void FastStructure_ArrayCopies_UseTheNet10Layout()
        {
            var check = new ArrayCopyCheck();
            ForEachShape(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches.ToArray()));
        }

        private class ArrayCopyCheck : IShapeCheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public unsafe void Check<T>(Shape<T> shape) where T : struct
            {
                const int at = 3;
                int length = at + 3 * shape.Size + 8;
                byte* memory = stackalloc byte[length];
                var values = new T[] { shape.Make(9), shape.Make(0), shape.Make(1), shape.Make(2), shape.Make(9) };

                Fill(memory, length, 0xCC);
                FastStructure.WriteArray((IntPtr)(memory + at), values, 1, 3);
                Expect(Mismatches, shape.Name + " WriteArray", BytesAt(memory + at, 3 * shape.Size), Images(shape, 0, 1, 2));
                Untouched(Mismatches, shape.Name + " WriteArray", memory, at, 3 * shape.Size);

                WriteImage(memory + at, Images(shape, 2, 1, 0));
                var read = new T[] { shape.Make(9), shape.Make(9), shape.Make(9), shape.Make(9), shape.Make(9) };
                FastStructure.ReadArray(read, (IntPtr)(memory + at), 1, 3);
                for (int i = 0; i < read.Length; i++)
                {
                    int expected = i == 0 || i == 4 ? 9 : 3 - i;
                    Same(Mismatches, shape.Name + " ReadArray element " + i, shape.Show(read[i]), shape.Show(shape.Make(expected)));
                }
            }
        }

        [TestMethod]
        public void BufferReadWrite_Copies_UseTheNet10Layout()
        {
            var check = new BufferCheck();
            ForEachShape(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches.ToArray()));
        }

        private class BufferCheck : IShapeCheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public unsafe void Check<T>(Shape<T> shape) where T : struct
            {
                const int at = 3;
                using (var buffer = new BufferReadWrite(Guid.NewGuid().ToString(), 512))
                {
                    T value = shape.Make(0);
                    buffer.Write(ref value, at);
                    Expect(Mismatches, shape.Name + " Write(ref T)", RawBytes(buffer, at, shape.Size), shape.ImageOf(0));
                    T read;
                    buffer.Read(out read, at);
                    Same(Mismatches, shape.Name + " Read(out T)", shape.Show(read), shape.Show(value));

                    WriteRaw(buffer, at, Images(shape, 1));
                    buffer.Read(out read, at);
                    Same(Mismatches, shape.Name + " Read(out T) of written bytes", shape.Show(read), shape.Show(shape.Make(1)));

                    var values = new T[] { shape.Make(0), shape.Make(1), shape.Make(2) };
                    buffer.Write(values, at);
                    Expect(Mismatches, shape.Name + " Write(T[])", RawBytes(buffer, at, 3 * shape.Size), Images(shape, 0, 1, 2));
                    WriteRaw(buffer, at, Images(shape, 2, 0, 1));
                    var back = new T[3];
                    buffer.Read(back, at);
                    for (int i = 0; i < 3; i++)
                        Same(Mismatches, shape.Name + " Read(T[]) element " + i, shape.Show(back[i]), shape.Show(shape.Make((i + 2) % 3)));
                }
            }
        }

        [TestMethod]
        public void SharedArray_Copies_UseTheNet10Layout()
        {
            var check = new SharedArrayCheck();
            ForEachShape(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches.ToArray()));
        }

        private class SharedArrayCheck : IShapeCheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public unsafe void Check<T>(Shape<T> shape) where T : struct
            {
                string name = Guid.NewGuid().ToString();
                using (var array = new SharedArray<T>(name, 3))
                using (var raw = new BufferReadWrite(name))
                {
                    if (array.BufferSize != 3 * shape.Size)
                        Mismatches.Add(shape.Name + ": BufferSize " + array.BufferSize + ", expected " + 3 * shape.Size);

                    for (int i = 0; i < 3; i++)
                        array[i] = shape.Make(i);
                    Expect(Mismatches, shape.Name + " indexer set", RawBytes(raw, 0, 3 * shape.Size), Images(shape, 0, 1, 2));
                    for (int i = 0; i < 3; i++)
                        Same(Mismatches, shape.Name + " indexer get " + i, shape.Show(array[i]), shape.Show(shape.Make(i)));
                    if (array.IndexOf(shape.Make(2)) != 2 || !array.Contains(shape.Make(1)))
                        Mismatches.Add(shape.Name + ": IndexOf or Contains");
                    int n = 0;
                    foreach (T item in array)
                    {
                        Same(Mismatches, shape.Name + " enumerator " + n, shape.Show(item), shape.Show(shape.Make(n)));
                        n++;
                    }

                    T value = shape.Make(5);
                    array.Write(ref value, 1);
                    Expect(Mismatches, shape.Name + " Write(ref T, 1)", RawBytes(raw, 0, 3 * shape.Size), Images(shape, 0, 5, 2));
                    T read;
                    array.Read(out read, 1);
                    Same(Mismatches, shape.Name + " Read(out T, 1)", shape.Show(read), shape.Show(value));

                    array.Write(new T[] { shape.Make(4), shape.Make(3) }, 1);
                    Expect(Mismatches, shape.Name + " Write(T[], 1)", RawBytes(raw, 0, 3 * shape.Size), Images(shape, 0, 4, 3));

                    WriteRaw(raw, 0, Images(shape, 2, 1, 0));
                    var copy = new T[2];
                    array.CopyTo(copy, 1);
                    Same(Mismatches, shape.Name + " CopyTo(T[], 1) element 0", shape.Show(copy[0]), shape.Show(shape.Make(1)));
                    Same(Mismatches, shape.Name + " CopyTo(T[], 1) element 1", shape.Show(copy[1]), shape.Show(shape.Make(0)));
                    var all = new T[4];
                    ((ICollection<T>)array).CopyTo(all, 1);
                    for (int i = 1; i < 4; i++)
                        Same(Mismatches, shape.Name + " ICollection.CopyTo element " + i, shape.Show(all[i]), shape.Show(shape.Make(3 - i)));
                }
            }
        }

        [TestMethod]
        public void CircularBuffer_Copies_UseTheNet10Layout()
        {
            var check = new CircularBufferCheck();
            ForEachShape(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches.ToArray()));
        }

        private class CircularBufferCheck : IShapeCheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public void Check<T>(Shape<T> shape) where T : struct
            {
                using (var ring = new CircularBuffer(Guid.NewGuid().ToString(), 4, 128))
                {
                    T value = shape.Make(0);
                    if (ring.Write(ref value) != shape.Size)
                        Mismatches.Add(shape.Name + ": Write(ref T) did not report " + shape.Size + " bytes");
                    var bytes = new byte[128];
                    int count = ring.Read(bytes);
                    var written = new byte[count];
                    Array.Copy(bytes, written, count);
                    Expect(Mismatches, shape.Name + " Write(ref T)", written, shape.ImageOf(0));

                    var image = new byte[shape.Size];
                    unsafe
                    {
                        fixed (byte* p = image)
                            WriteImage(p, shape.ImageOf(1));
                    }
                    ring.Write(image);
                    T read;
                    ring.Read(out read);
                    Same(Mismatches, shape.Name + " Read(out T)", shape.Show(read), shape.Show(shape.Make(1)));

                    var values = new T[] { shape.Make(0), shape.Make(1), shape.Make(2) };
                    if (ring.Write(values) != 3)
                        Mismatches.Add(shape.Name + ": Write(T[]) did not write 3 elements");
                    count = ring.Read(bytes);
                    written = new byte[count];
                    Array.Copy(bytes, written, count);
                    Expect(Mismatches, shape.Name + " Write(T[])", written, Images(shape, 0, 1, 2));

                    ring.Write(values);
                    var back = new T[3];
                    if (ring.Read(back) != 3)
                        Mismatches.Add(shape.Name + ": Read(T[]) did not read 3 elements");
                    for (int i = 0; i < 3; i++)
                        Same(Mismatches, shape.Name + " Read(T[]) element " + i, shape.Show(back[i]), shape.Show(shape.Make(i)));
                }
            }
        }

        /// <summary>
        /// The field-by-field copies, which this runtime may not need, built for every shape and run: they give the same bytes
        /// as the pinned layout, and the same values back.
        /// </summary>
        [TestMethod]
        public void FieldByFieldCopies_UseTheNet10Layout()
        {
            var check = new FieldByFieldCheck();
            ForEachShape(check);
            Assert.AreEqual("", string.Join("\n", check.Mismatches.ToArray()));
        }

        private class FieldByFieldCheck : IShapeCheck
        {
            public readonly List<string> Mismatches = new List<string>();

            public unsafe void Check<T>(Shape<T> shape) where T : struct
            {
                Delegate[] copies = FieldByFieldCopies<T>();
                var load = (FastStructure<T>.PtrToStructureDelegate)copies[0];
                var store = (FastStructure<T>.StructureToPtrDelegate)copies[1];
                const int at = 3;
                int length = at + 3 * shape.Size + 8;
                byte* memory = stackalloc byte[length];

                T value = shape.Make(0);
                Fill(memory, length, 0xCC);
                store(ref value, (IntPtr)(memory + at));
                Expect(Mismatches, shape.Name + " store", BytesAt(memory + at, shape.Size), shape.ImageOf(0));
                Untouched(Mismatches, shape.Name + " store", memory, at, shape.Size);
                WriteImage(memory + at, shape.ImageOf(1));
                Same(Mismatches, shape.Name + " load", shape.Show(load((IntPtr)(memory + at))), shape.Show(shape.Make(1)));

                var values = new T[] { shape.Make(9), shape.Make(0), shape.Make(1), shape.Make(2) };
                Fill(memory, length, 0xCC);
                copies[3].DynamicInvoke(values, 1, Pointer.Box(memory + at, typeof(void*)), (long)(3 * shape.Size));
                Expect(Mismatches, shape.Name + " array store", BytesAt(memory + at, 3 * shape.Size), Images(shape, 0, 1, 2));
                Untouched(Mismatches, shape.Name + " array store", memory, at, 3 * shape.Size);
                WriteImage(memory + at, Images(shape, 2, 1, 0));
                var read = new T[] { shape.Make(9), shape.Make(9), shape.Make(9), shape.Make(9) };
                copies[2].DynamicInvoke(read, 1, Pointer.Box(memory + at, typeof(void*)), (long)(3 * shape.Size));
                for (int i = 0; i < read.Length; i++)
                    Same(Mismatches, shape.Name + " array load element " + i, shape.Show(read[i]), shape.Show(shape.Make(i == 0 ? 9 : 3 - i)));
            }
        }

        private static unsafe byte[] RawBytes(BufferReadWrite buffer, long position, int count)
        {
            var bytes = new byte[count];
            fixed (byte* p = bytes)
                buffer.Read((IntPtr)p, count, position);
            return bytes;
        }

        private static unsafe void WriteRaw(BufferReadWrite buffer, long position, string image)
        {
            var bytes = new byte[image.Split('-').Length];
            fixed (byte* p = bytes)
            {
                WriteImage(p, image);
                buffer.Write((IntPtr)p, bytes.Length, position);
            }
        }

        private static void Expect(List<string> mismatches, string what, byte[] bytes, string image)
        {
            if (!CharacterisedTypes.MatchesLayout(bytes, image))
                mismatches.Add(what + ": " + Bytes(bytes) + ", expected " + image);
        }

        private static void Same(List<string> mismatches, string what, string actual, string expected)
        {
            if (actual != expected)
                mismatches.Add(what + ": " + actual + ", expected " + expected);
        }

        private static unsafe void Untouched(List<string> mismatches, string what, byte* memory, int at, int count)
        {
            for (int i = 0; i < at; i++)
            {
                if (memory[i] != 0xCC)
                {
                    mismatches.Add(what + ": wrote before the position");
                    return;
                }
            }
            for (int i = at + count; i < at + count + 8; i++)
            {
                if (memory[i] != 0xCC)
                {
                    mismatches.Add(what + ": wrote past the structure");
                    return;
                }
            }
        }

        #endregion

        #region Single-structure positions out of range

        /// <summary>
        /// Single-structure reads and writes no longer go through the view accessor, but throw what it threw for a position
        /// out of range (and, on the .NET Framework 3.5 build, what that build's own accessor threw).
        /// </summary>
        [TestMethod]
        public void SingleStructure_PositionOutOfRange_ThrowsAsBefore()
        {
            using (var buffer = new BufferReadWrite(Guid.NewGuid().ToString(), 64))
            {
                long value = 1;
                long read;
#if LIBRARY_NET35
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Write(ref value, 60)).ParamName, "write with too few bytes left");
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Read(out read, 60)).ParamName, "read with too few bytes left");
#else
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentException>(() => buffer.Write(ref value, 60)).ParamName, "write with too few bytes left");
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentException>(() => buffer.Read(out read, 60)).ParamName, "read with too few bytes left");
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Write(ref value, -100)).ParamName, "write before the view");
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Read(out read, -100)).ParamName, "read before the view");
#endif
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Write(ref value, 64)).ParamName, "write at the end");
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Read(out read, 64)).ParamName, "read at the end");
                Assert.AreEqual("position", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.Write(ref value, 1000)).ParamName, "write past the end");

                buffer.Write(ref value, 56);
                buffer.Read(out read, 56);
                Assert.AreEqual(1L, read, "the last whole long");
            }
        }

        #endregion
    }
}
