// SharedMemory (File: SharedMemory\ElementLayout.cs)
// Copyright (c) 2014 Justin Stenning
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
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SharedMemory
{
    /// <summary>
    /// The layout an element type has in shared memory: the layout 64-bit .NET 10 gives the type, worked out from the type's
    /// metadata so that it is the same in every process, whatever its runtime or bitness. Runtimes lay some structures out
    /// differently, such as .NET Framework for structures holding a <see cref="DateTime"/> or a <see cref="decimal"/>; there
    /// <see cref="FastStructure{T}"/> copies field by field between the runtime's layout and this one.
    /// </summary>
    /// <remarks>
    /// The rules: fields in metadata order, each at the next multiple of its alignment, capped by
    /// <see cref="StructLayoutAttribute.Pack"/>; explicit layouts at their <see cref="FieldOffsetAttribute"/>; a structure is
    /// aligned to its largest field alignment and its size is rounded up to it, unless it sets
    /// <see cref="StructLayoutAttribute.Size"/>: then it is that size, or the end of its last field if that is further, unrounded. Primitives take their size as their alignment; <see cref="DateTime"/> and
    /// <see cref="TimeSpan"/> are 8 bytes aligned to 8, <see cref="decimal"/> 16 aligned to 8, <see cref="Guid"/> 16 aligned
    /// to 4. Types it cannot describe (pointers, <see cref="IntPtr"/>, <see cref="UIntPtr"/>, other structures from the core
    /// library, generic and auto-layout structures) have no canonical layout and keep the runtime's own.
    /// </remarks>
    internal sealed class ElementLayout
    {
        /// <summary>
        /// A field copied as a whole: a primitive, an enum, a <see cref="DateTime"/>, <see cref="TimeSpan"/>, <see cref="decimal"/>
        /// or <see cref="Guid"/>, or a fixed buffer.
        /// </summary>
        internal struct Leaf
        {
            /// <summary>The offset in the canonical layout.</summary>
            internal int Offset;
            /// <summary>The size in bytes.</summary>
            internal int Size;
            /// <summary>The fields from the element type down to the leaf; empty when the element type is itself a leaf.</summary>
            internal FieldInfo[] Path;
        }

        /// <summary>
        /// A run of bytes that is contiguous in both layouts.
        /// </summary>
        internal struct Run
        {
            internal int Canonical;
            internal int Runtime;
            internal int Length;
        }

        /// <summary>The size in bytes in the canonical layout.</summary>
        internal readonly int Size;

        /// <summary>The alignment in the canonical layout.</summary>
        internal readonly int Alignment;

        /// <summary>The leaves, in metadata order.</summary>
        internal readonly Leaf[] Leaves;

        private ElementLayout(int size, int alignment, Leaf[] leaves)
        {
            Size = size;
            Alignment = alignment;
            Leaves = leaves;
        }

        /// <summary>
        /// The canonical layout of <paramref name="type"/>, or null if it cannot be described. Never throws.
        /// </summary>
        internal static ElementLayout For(Type type)
        {
            try
            {
                var leaves = new List<Leaf>();
                int size, alignment;
                if (!TryDescribe(type, 0, out size, out alignment, leaves))
                    return null;
                return new ElementLayout(size, alignment, leaves.ToArray());
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryLeaf(Type type, out int size, out int alignment)
        {
            if (type.IsEnum)
                type = Enum.GetUnderlyingType(type);

            if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte))
                size = alignment = 1;
            else if (type == typeof(char) || type == typeof(short) || type == typeof(ushort))
                size = alignment = 2;
            else if (type == typeof(int) || type == typeof(uint) || type == typeof(float))
                size = alignment = 4;
            else if (type == typeof(long) || type == typeof(ulong) || type == typeof(double) || type == typeof(DateTime) || type == typeof(TimeSpan))
                size = alignment = 8;
            else if (type == typeof(decimal))
            {
                size = 16;
                alignment = 8;
            }
            else if (type == typeof(Guid))
            {
                size = 16;
                alignment = 4;
            }
            else
            {
                size = alignment = 0;
                return false;
            }
            return true;
        }

        private static readonly FieldInfo[] NoFields = new FieldInfo[0];

        private static bool TryDescribe(Type type, int depth, out int size, out int alignment, List<Leaf> leaves)
        {
            size = 0;
            alignment = 1;

            if (TryLeaf(type, out size, out alignment))
            {
                leaves.Add(new Leaf { Offset = 0, Size = size, Path = NoFields });
                return true;
            }

            // Pointers, IntPtr and UIntPtr change size with bitness. Other structures from the core library have private
            // fields that differ between runtimes, and generic and auto-layout structures have no fixed field order.
            if (depth > 32 || !type.IsValueType || type.IsPointer || type.IsPrimitive || type.IsGenericType)
                return false;
            if (type.Assembly == typeof(object).Assembly || (type.Namespace != null && (type.Namespace == "System" || type.Namespace.StartsWith("System.", StringComparison.Ordinal))))
                return false;
            StructLayoutAttribute layout = type.StructLayoutAttribute;
            if (layout == null || layout.Value == LayoutKind.Auto)
                return false;
            bool isExplicit = layout.Value == LayoutKind.Explicit;
            int pack = layout.Pack == 0 ? int.MaxValue : layout.Pack;

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // GetFields promises no order; the runtime lays sequential fields out in metadata order.
            Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));

            int end = 0;
            int maxAlignment = 1;
            foreach (FieldInfo field in fields)
            {
                int fieldSize, fieldAlignment;
                var fieldLeaves = new List<Leaf>();
                object[] fixedBuffer = field.GetCustomAttributes(typeof(FixedBufferAttribute), false);
                if (fixedBuffer.Length > 0)
                {
                    var attribute = (FixedBufferAttribute)fixedBuffer[0];
                    int elementSize;
                    if (!TryLeaf(attribute.ElementType, out elementSize, out fieldAlignment) || attribute.Length <= 0)
                        return false;
                    fieldSize = checked(elementSize * attribute.Length);
                    fieldLeaves.Add(new Leaf { Offset = 0, Size = fieldSize, Path = NoFields });
                }
                else if (!TryDescribe(field.FieldType, depth + 1, out fieldSize, out fieldAlignment, fieldLeaves))
                    return false;

                fieldAlignment = Math.Min(fieldAlignment, pack);
                int offset;
                if (isExplicit)
                {
                    object[] fieldOffset = field.GetCustomAttributes(typeof(FieldOffsetAttribute), false);
                    if (fieldOffset.Length == 0)
                        return false;
                    offset = ((FieldOffsetAttribute)fieldOffset[0]).Value;
                    if (offset < 0)
                        return false;
                }
                else
                {
                    offset = AlignUp(end, fieldAlignment);
                }

                foreach (Leaf leaf in fieldLeaves)
                {
                    var path = new FieldInfo[leaf.Path.Length + 1];
                    path[0] = field;
                    Array.Copy(leaf.Path, 0, path, 1, leaf.Path.Length);
                    leaves.Add(new Leaf { Offset = checked(offset + leaf.Offset), Size = leaf.Size, Path = path });
                }
                end = Math.Max(end, checked(offset + fieldSize));
                maxAlignment = Math.Max(maxAlignment, fieldAlignment);
            }

            // A structure that sets StructLayout.Size is at least that size and is not rounded up to its alignment
            alignment = maxAlignment;
            size = layout.Size > 0 ? Math.Max(layout.Size, end) : Math.Max(1, AlignUp(end, alignment));
            return true;
        }

        private static int AlignUp(int value, int alignment)
        {
            return checked((value + alignment - 1) / alignment * alignment);
        }

        /// <summary>
        /// The layout as text, for tests: the size, then each leaf's field path, offset and size.
        /// </summary>
        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append("size ").Append(Size);
            foreach (Leaf leaf in Leaves)
            {
                text.Append("; ");
                for (int i = 0; i < leaf.Path.Length; i++)
                {
                    if (i > 0)
                        text.Append('.');
                    text.Append(leaf.Path[i].Name);
                }
                if (leaf.Path.Length == 0)
                    text.Append("value");
                text.Append(' ').Append(leaf.Offset).Append('+').Append(leaf.Size);
            }
            return text.ToString();
        }

        /// <summary>
        /// Measures where the running runtime puts each leaf of <paramref name="type"/>, with <c>ldflda</c> on a local.
        /// </summary>
        internal int[] RuntimeOffsets(Type type)
        {
            var method = new DynamicMethod("LeafOffsets<" + type.FullName + ">", null, new Type[1] { typeof(int[]) }, typeof(ElementLayout).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.DeclareLocal(type);
            for (int i = 0; i < Leaves.Length; i++)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                foreach (FieldInfo field in Leaves[i].Path)
                    il.Emit(OpCodes.Ldflda, field);
                il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Ldloca_S, (byte)0);
                il.Emit(OpCodes.Conv_U);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Stelem_I4);
            }
            il.Emit(OpCodes.Ret);

            var offsets = new int[Leaves.Length];
            ((Action<int[]>)method.CreateDelegate(typeof(Action<int[]>)))(offsets);
            return offsets;
        }

        /// <summary>
        /// Whether the runtime's layout, given by its leaf offsets and size, is the canonical layout.
        /// </summary>
        internal bool Matches(int[] runtimeOffsets, int runtimeSize)
        {
            if (runtimeSize != Size)
                return false;
            for (int i = 0; i < Leaves.Length; i++)
            {
                if (runtimeOffsets[i] != Leaves[i].Offset)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The runs of bytes that a copy between the two layouts moves, or null if overlapping leaves would need a byte of
        /// one layout in two places of the other.
        /// </summary>
        internal Run[] Runs(int[] runtimeOffsets, int runtimeSize)
        {
            // For each canonical byte, the runtime byte it holds, or -1 for padding
            var map = new int[Size];
            for (int i = 0; i < map.Length; i++)
                map[i] = -1;
            for (int i = 0; i < Leaves.Length; i++)
            {
                Leaf leaf = Leaves[i];
                if (leaf.Offset + leaf.Size > Size || runtimeOffsets[i] < 0 || runtimeOffsets[i] + leaf.Size > runtimeSize)
                    return null;
                for (int b = 0; b < leaf.Size; b++)
                {
                    int runtime = runtimeOffsets[i] + b;
                    if (map[leaf.Offset + b] == -1)
                        map[leaf.Offset + b] = runtime;
                    else if (map[leaf.Offset + b] != runtime)
                        return null;
                }
            }

            var runs = new List<Run>();
            for (int c = 0; c < map.Length; c++)
            {
                if (map[c] == -1)
                    continue;
                int start = c;
                while (c + 1 < map.Length && map[c + 1] != -1 && map[c + 1] == map[c] + 1)
                    c++;
                runs.Add(new Run { Canonical = start, Runtime = map[start], Length = c - start + 1 });
            }
            return runs.ToArray();
        }

        /// <summary>
        /// Builds the field-by-field copies of <paramref name="type"/>: a load from memory in the canonical layout
        /// (<paramref name="loadType"/>: <c>T (IntPtr)</c>), a store into it (<paramref name="storeType"/>: <c>void (ref T, IntPtr)</c>),
        /// and array copies from and to it (<paramref name="arrayCopyType"/>: <c>void (T[] array, int index, void* memory, long byteCount)</c>,
        /// the byte count in the canonical layout), which keep the array pinned for the whole copy. Null if the layouts
        /// cannot be mapped onto each other.
        /// </summary>
        internal Delegate[] BuildCopies(Type type, int[] runtimeOffsets, int runtimeSize, Type loadType, Type storeType, Type arrayCopyType)
        {
            Run[] runs = Runs(runtimeOffsets, runtimeSize);
            if (runs == null)
                return null;

            return new Delegate[4]
            {
                BuildLoad(type, runs, loadType),
                BuildStore(type, runs, storeType),
                BuildArrayCopy(type, runs, runtimeSize, true, arrayCopyType),
                BuildArrayCopy(type, runs, runtimeSize, false, arrayCopyType),
            };
        }

        private Delegate BuildLoad(Type type, Run[] runs, Type delegateType)
        {
            var method = new DynamicMethod("LoadFieldByField<" + type.FullName + ">", type, new Type[1] { typeof(IntPtr) }, typeof(ElementLayout).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.DeclareLocal(type);
            il.Emit(OpCodes.Ldloca_S, (byte)0);
            il.Emit(OpCodes.Initobj, type);
            foreach (Run run in runs)
            {
                EmitCopy(il, run.Length,
                    delegate (int offset) { il.Emit(OpCodes.Ldloca_S, (byte)0); EmitAdd(il, run.Runtime + offset); },
                    delegate (int offset) { il.Emit(OpCodes.Ldarg_0); EmitAdd(il, run.Canonical + offset); });
            }
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate(delegateType);
        }

        private Delegate BuildStore(Type type, Run[] runs, Type delegateType)
        {
            var method = new DynamicMethod("StoreFieldByField<" + type.FullName + ">", null, new Type[2] { type.MakeByRefType(), typeof(IntPtr) }, typeof(ElementLayout).Module, true);
            ILGenerator il = method.GetILGenerator();
            foreach (Run run in runs)
            {
                EmitCopy(il, run.Length,
                    delegate (int offset) { il.Emit(OpCodes.Ldarg_1); EmitAdd(il, run.Canonical + offset); },
                    delegate (int offset) { il.Emit(OpCodes.Ldarg_0); EmitAdd(il, run.Runtime + offset); });
            }
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate(delegateType);
        }

        private unsafe Delegate BuildArrayCopy(Type type, Run[] runs, int runtimeSize, bool intoArray, Type delegateType)
        {
            var method = new DynamicMethod((intoArray ? "CopyFromMemoryFieldByField<" : "CopyToMemoryFieldByField<") + type.FullName + ">",
                null, new Type[4] { type.MakeArrayType(), typeof(int), typeof(void*), typeof(long) }, typeof(ElementLayout).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.DeclareLocal(type.MakeByRefType(), true);    // 0: the first element copied, pinned for the whole copy
            il.DeclareLocal(typeof(byte*));                 // 1: the current element in the array
            il.DeclareLocal(typeof(byte*));                 // 2: the current element in memory
            il.DeclareLocal(typeof(long));                  // 3: elements left to copy
            Label loop = il.DefineLabel();
            Label test = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldelema, type);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Stloc_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stloc_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldc_I8, (long)Size);
            il.Emit(OpCodes.Div);
            il.Emit(OpCodes.Stloc_3);
            il.Emit(OpCodes.Br, test);

            il.MarkLabel(loop);
            foreach (Run run in runs)
            {
                if (intoArray)
                {
                    EmitCopy(il, run.Length,
                        delegate (int offset) { il.Emit(OpCodes.Ldloc_1); EmitAdd(il, run.Runtime + offset); },
                        delegate (int offset) { il.Emit(OpCodes.Ldloc_2); EmitAdd(il, run.Canonical + offset); });
                }
                else
                {
                    EmitCopy(il, run.Length,
                        delegate (int offset) { il.Emit(OpCodes.Ldloc_2); EmitAdd(il, run.Canonical + offset); },
                        delegate (int offset) { il.Emit(OpCodes.Ldloc_1); EmitAdd(il, run.Runtime + offset); });
                }
            }
            il.Emit(OpCodes.Ldloc_1);
            EmitAdd(il, runtimeSize);
            il.Emit(OpCodes.Stloc_1);
            il.Emit(OpCodes.Ldloc_2);
            EmitAdd(il, Size);
            il.Emit(OpCodes.Stloc_2);
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Conv_I8);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Stloc_3);

            il.MarkLabel(test);
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Conv_I8);
            il.Emit(OpCodes.Bgt, loop);
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate(delegateType);
        }

        private delegate void EmitAddress(int offset);

        private static void EmitAdd(ILGenerator il, int value)
        {
            if (value == 0)
                return;
            il.Emit(OpCodes.Ldc_I4, value);
            il.Emit(OpCodes.Add);
        }

        /// <summary>
        /// Emits a copy of <paramref name="length"/> bytes, in loads and stores of up to 8 bytes, or one block copy for
        /// long runs. Neither address need be aligned: the caller's position in shared memory can be anything.
        /// </summary>
        private static void EmitCopy(ILGenerator il, int length, EmitAddress destination, EmitAddress source)
        {
            if (length > 64)
            {
                destination(0);
                source(0);
                il.Emit(OpCodes.Ldc_I4, length);
                il.Emit(OpCodes.Unaligned, (byte)1);
                il.Emit(OpCodes.Cpblk);
                return;
            }

            int offset = 0;
            while (offset < length)
            {
                int left = length - offset;
                int chunk = left >= 8 ? 8 : left >= 4 ? 4 : left >= 2 ? 2 : 1;
                destination(offset);
                source(offset);
                il.Emit(OpCodes.Unaligned, (byte)1);
                il.Emit(chunk == 8 ? OpCodes.Ldind_I8 : chunk == 4 ? OpCodes.Ldind_I4 : chunk == 2 ? OpCodes.Ldind_I2 : OpCodes.Ldind_I1);
                il.Emit(OpCodes.Unaligned, (byte)1);
                il.Emit(chunk == 8 ? OpCodes.Stind_I8 : chunk == 4 ? OpCodes.Stind_I4 : chunk == 2 ? OpCodes.Stind_I2 : OpCodes.Stind_I1);
                offset += chunk;
            }
        }
    }
}
