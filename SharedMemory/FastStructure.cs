// SharedMemory (File: SharedMemory\FastStructure.cs)
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
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SharedMemory
{
    /// <summary>
    /// Provides fast reading and writing of generic structures to a memory location using IL emitted functions.
    /// </summary>
    /// <remarks>
    /// Shared memory holds each structure in one layout in every process: the layout 64-bit .NET 10 gives it. So processes on
    /// .NET Framework, .NET Core and .NET 5 or later can share structures holding a <see cref="DateTime"/> or a <see cref="decimal"/>.
    /// Where the running runtime lays a structure out that way, it is copied as a block of memory; where it does not, as .NET
    /// Framework does for those structures, it is copied field by field, which is slower. Structures holding an
    /// <see cref="IntPtr"/>, a <see cref="UIntPtr"/>, a pointer, a generic structure, or a .NET structure other than
    /// <see cref="DateTime"/>, <see cref="TimeSpan"/>, <see cref="decimal"/> and <see cref="Guid"/> keep the runtime's own
    /// layout, which is not portable across bitness or runtimes.
    /// </remarks>
    public static class FastStructure
    {
        /// <summary>
        /// Retrieve a pointer to the passed generic structure type. This is achieved by emitting a <see cref="DynamicMethod"/> to retrieve a pointer to the structure.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="structure"></param>
        /// <returns>A pointer to the provided structure in memory.</returns>
        /// <see cref="FastStructure{T}.GetPtr"/>
        public static unsafe void* GetPtr<T>(ref T structure)
            where T : struct
        {
            return FastStructure<T>.GetPtr(ref structure);
        }

        /// <summary>
        /// Loads the generic value type <typeparamref name="T"/> from a pointer. This is achieved by emitting a <see cref="DynamicMethod"/> that returns the value in the memory location as a <typeparamref name="T"/>.
        /// <para>The equivalent non-generic C# code:</para>
        /// <code>
        /// unsafe MyStruct ReadFromPointer(byte* pointer)
        /// {
        ///     return *(MyStruct*)pointer;
        /// }
        /// </code>
        /// </summary>
        /// <typeparam name="T">Any value/structure type</typeparam>
        /// <param name="pointer">Unsafe pointer to memory to load the value from</param>
        /// <returns>The newly loaded value</returns>
        public static unsafe T PtrToStructure<T>(IntPtr pointer)
            where T : struct
        {
            return FastStructure<T>.PtrToStructure(pointer);
        }

        /// <summary>
        /// Writes the generic value type <typeparamref name="T"/> to the location specified by a pointer. This is achieved by emitting a <see cref="DynamicMethod"/> that copies the value from the referenced structure into the specified memory location.
        /// <para>There is no exact equivalent possible in C#, the closest possible (generates the same IL) is the following code:</para>
        /// <code>
        /// unsafe void WriteToPointer(ref SharedHeader dest, ref SharedHeader src)
        /// {
        ///     dest = src;
        /// }
        /// </code>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="pointer"></param>
        /// <param name="structure"></param>
        public static unsafe void StructureToPtr<T>(ref T structure, IntPtr pointer)
            where T : struct
        {
            FastStructure<T>.StructureToPtr(ref structure, pointer);
        }

        /// <summary>
        /// Copy bytes of structure into the existing buffer at index
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="structure"></param>
        /// <param name="buffer"></param>
        /// <param name="startIndex"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">The structure does not fit in <paramref name="buffer"/> from <paramref name="startIndex"/>.</exception>
        public static unsafe void CopyTo<T>(ref T structure, byte[] buffer, int startIndex = 0)
            where T : struct
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (startIndex > buffer.Length || startIndex < 0)
                throw new ArgumentOutOfRangeException("startIndex");
            if (buffer.Length - startIndex < FastStructure<T>.Size)
                throw new ArgumentException("The structure does not fit in the buffer from startIndex.");

            fixed (byte* p = &buffer[startIndex])
            {
                StructureToPtr<T>(ref structure, new IntPtr(p));
            }
        }

        /// <summary>
        /// Return a byte[] for the provided structure
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="structure"></param>
        /// <returns></returns>
        public static unsafe byte[] ToBytes<T>(ref T structure)
            where T : struct
        {
            byte[] result = new byte[FastStructure<T>.Size];
            fixed (byte* p = &result[0])
            {
                StructureToPtr<T>(ref structure, new IntPtr(p));
                return result;
            }
        }

        /// <summary>
        /// Read structure from the provided byte array
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="buffer"></param>
        /// <param name="startIndex"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"><paramref name="buffer"/> holds less than one structure from <paramref name="startIndex"/>.</exception>
        public static unsafe T FromBytes<T>(byte[] buffer, int startIndex = 0)
            where T : struct
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (startIndex > buffer.Length || startIndex < 0)
                throw new ArgumentOutOfRangeException("startIndex");
            if (buffer.Length - startIndex < FastStructure<T>.Size)
                throw new ArgumentException("The buffer holds less than one structure from startIndex.");

            fixed (byte* p = &buffer[startIndex])
            {
                return PtrToStructure<T>(new IntPtr(p));
            }
        }

        /// <summary>
        /// Retrieve the cached size of a structure
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        /// <remarks>Caches the size by type</remarks>
        /// <see cref="FastStructure{T}.Size"/>
        public static int SizeOf<T>()
            where T : struct
        {
            return FastStructure<T>.Size;
        }

        /// <summary>
        /// Reads a number of elements from a memory location into the provided buffer starting at the specified index.
        /// </summary>
        /// <typeparam name="T">The structure type</typeparam>
        /// <param name="buffer">The destination buffer.</param>
        /// <param name="source">The source memory location.</param>
        /// <param name="index">The start index within <paramref name="buffer"/>.</param>
        /// <param name="count">The number of elements to read.</param>
        public static unsafe void ReadArray<T>(T[] buffer, IntPtr source, int index, int count)
            where T : struct
        {
            uint elementSize = (uint)SizeOf<T>();

            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            if (index < 0)
                throw new ArgumentOutOfRangeException("index");
            if (buffer.Length - index < count)
                throw new ArgumentException("Invalid offset into array specified by index and count");
            if (count == 0)
                return;

            FastStructure<T>.CopyFromMemory(buffer, index, source.ToPointer(), elementSize * count);
        }

        /// <summary>
        /// Reads a number of elements from a memory location into the provided buffer starting at the specified index.
        /// </summary>
        /// <param name="buffer">The destination buffer.</param>
        /// <param name="source">The source memory location.</param>
        /// <param name="index">The start index within <paramref name="buffer"/>.</param>
        /// <param name="count">The number of elements to read.</param>
        public static unsafe void ReadBytes(byte[] buffer, IntPtr source, int index, int count)
        {
            uint elementSize = sizeof(byte);

            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            if (index < 0)
                throw new ArgumentOutOfRangeException("index");
            if (buffer.Length - index < count)
                throw new ArgumentException("Invalid offset into array specified by index and count");
            if (count == 0)
                return;

            void* ptr = source.ToPointer();

            fixed (byte* p = &buffer[0])
            {
#if NETCORE
                Buffer.MemoryCopy(ptr, p + (index * elementSize), elementSize * count, elementSize * count);
#else
                UnsafeNativeMethods.CopyMemoryPtr(p + (index * elementSize), ptr, (uint)(elementSize * count));
#endif
            }
        }

        /// <summary>
        /// Writes a number of elements to a memory location from the provided buffer starting at the specified index.
        /// </summary>
        /// <typeparam name="T">The structure type</typeparam>
        /// <param name="destination">The destination memory location.</param>
        /// <param name="buffer">The source buffer.</param>
        /// <param name="index">The start index within <paramref name="buffer"/>.</param>
        /// <param name="count">The number of elements to write.</param>
        public static unsafe void WriteArray<T>(IntPtr destination, T[] buffer, int index, int count)
            where T : struct
        {
            uint elementSize = (uint)SizeOf<T>();

            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            if (index < 0)
                throw new ArgumentOutOfRangeException("index");
            if (buffer.Length - index < count)
                throw new ArgumentException("Invalid offset into array specified by index and count");
            if (count == 0)
                return;

            FastStructure<T>.CopyToMemory(buffer, index, destination.ToPointer(), elementSize * count);
        }

        /// <summary>
        /// Writes a number of elements to a memory location from the provided buffer starting at the specified index.
        /// </summary>
        /// <param name="destination">The destination memory location.</param>
        /// <param name="buffer">The source buffer.</param>
        /// <param name="index">The start index within <paramref name="buffer"/>.</param>
        /// <param name="count">The number of elements to write.</param>
        public static unsafe void WriteBytes(IntPtr destination, byte[] buffer, int index, int count)
        {
            uint elementSize = sizeof(byte);

            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");
            if (index < 0)
                throw new ArgumentOutOfRangeException("index");
            if (buffer.Length - index < count)
                throw new ArgumentException("Invalid offset into array specified by index and count");
            if (count == 0)
                return;

            void* ptr = destination.ToPointer();
            fixed (byte* p = &buffer[0])
            {
#if NETCORE
                Buffer.MemoryCopy(p + (index * elementSize), ptr, elementSize * count, elementSize * count);
#else
                UnsafeNativeMethods.CopyMemoryPtr(ptr, p + (index * elementSize), (uint)(elementSize * count));
#endif
            }
        }
    }

    /// <summary>
    /// Emits optimized IL for the reading and writing of structures to/from memory.
    /// <para>For a 32-byte structure with 1 million iterations:</para>
    /// <para>The <see cref="FastStructure{T}.PtrToStructure"/> method performs approx. 20x faster than
    /// <see cref="System.Runtime.InteropServices.Marshal.PtrToStructure(IntPtr, Type)"/> (8ms vs 160ms), and about 1.6x slower than the non-generic equivalent (8ms vs 5ms)</para>
    /// <para>The <see cref="FastStructure{T}.StructureToPtr"/> method performs approx. 8x faster than 
    /// <see cref="System.Runtime.InteropServices.Marshal.StructureToPtr(object, IntPtr, bool)"/> (4ms vs 34ms). </para>
    /// </summary>
    /// <remarks>
    /// The copies read and write T in one layout in every process, the layout 64-bit .NET 10 gives it, so that processes on
    /// different runtimes can share it. Where the running runtime lays T out another way, as .NET Framework does for
    /// structures holding a <see cref="DateTime"/> or a <see cref="decimal"/>, they copy field by field, which is slower.
    /// Structures holding an <see cref="IntPtr"/>, a <see cref="UIntPtr"/>, a pointer, a generic structure, or a .NET structure
    /// other than <see cref="DateTime"/>, <see cref="TimeSpan"/>, <see cref="decimal"/> and <see cref="Guid"/> keep the
    /// runtime's own layout.
    /// </remarks>
    /// <typeparam name="T"></typeparam>
    public static class FastStructure<T>
        where T : struct
    {
        /// <summary>
        /// Delegate that returns a pointer to the provided structure. Use with extreme caution.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public unsafe delegate void* GetPtrDelegate(ref T value);
        
        /// <summary>
        /// Delegate for loading a structure from the specified memory address
        /// </summary>
        /// <param name="pointer"></param>
        /// <returns></returns>
        public delegate T PtrToStructureDelegate(IntPtr pointer);
        
        /// <summary>
        /// Delegate for writing a structure to the specified memory address
        /// </summary>
        /// <param name="value"></param>
        /// <param name="pointer"></param>
        public delegate void StructureToPtrDelegate(ref T value, IntPtr pointer);
        
        /// <summary>
        /// The <see cref="GetPtrDelegate"/> delegate for the generated IL to retrieve a pointer to the structure. The pointer is to
        /// the structure in managed memory, in the running runtime's layout: where T is copied field by field, that layout is not
        /// the one in shared memory, and its size is not <see cref="Size"/>.
        /// </summary>
        public unsafe readonly static GetPtrDelegate GetPtr = BuildFunction();

        /// <summary>
        /// The <see cref="PtrToStructureDelegate"/> delegate for the generated IL to retrieve a structure from a specified memory address.
        /// </summary>
        public readonly static PtrToStructureDelegate PtrToStructure = BuildLoadFromPointerFunction();

        /// <summary>
        /// The <see cref="StructureToPtrDelegate"/> delegate for the generated IL to store a structure at the specified memory address.
        /// </summary>
        public readonly static StructureToPtrDelegate StructureToPtr = BuildWriteToPointerFunction();

        /// <summary>
        /// Cached size of T in bytes in shared memory: the size of the layout 64-bit .NET 10 gives T, the same in every process.
        /// This is the space an element of T takes in shared memory, and the number of bytes per element that copies move.
        /// Where the running runtime lays T out that way, it is also T's .NET size, as the IL <c>sizeof</c> instruction gives it;
        /// where it does not, such as on .NET Framework for structures holding a <see cref="DateTime"/> or a <see cref="decimal"/>,
        /// T is copied field by field and this is not the size of a T in managed memory, so do not copy <see cref="Size"/> bytes
        /// from <see cref="GetPtr"/>. Up to version 2.3.3 this was <see cref="System.Runtime.InteropServices.Marshal.SizeOf(Type)"/>,
        /// which agrees for blittable types but differs for types whose marshalled form has another size, such as
        /// <see cref="bool"/>, <see cref="char"/>, a <see cref="decimal"/> marked as Currency, and structures that hold them.
        /// </summary>
        public static readonly int Size = BuildSizeOf();
        
        private delegate int SizeOfDelegate();

        /// <summary>
        /// Copies <paramref name="byteCount"/> bytes between <paramref name="memory"/> and the elements of <paramref name="array"/>
        /// from <paramref name="index"/> on, with the array pinned for the whole copy.
        /// </summary>
        internal unsafe delegate void PinnedCopyDelegate(T[] array, int index, void* memory, long byteCount);

        /// <summary>
        /// Copies from memory into an array, which stays pinned for the whole copy.
        /// </summary>
        internal static readonly PinnedCopyDelegate CopyFromMemory = BuildPinnedCopy(true);

        /// <summary>
        /// Copies from an array into memory, with the array pinned for the whole copy.
        /// </summary>
        internal static readonly PinnedCopyDelegate CopyToMemory = BuildPinnedCopy(false);

        private static DynamicMethod method;
        private static DynamicMethod methodLoad;
        private static DynamicMethod methodWrite;

        /// <summary>
        /// T's layout in shared memory, the same in every process; null if T has none, such as a structure holding an
        /// <see cref="IntPtr"/>, which keeps the running runtime's own layout.
        /// </summary>
        internal static readonly ElementLayout Layout;

        /// <summary>
        /// Whether T is copied as a block of memory: true where the running runtime lays T out as <see cref="Layout"/>, or
        /// T has no <see cref="Layout"/>; false where T is copied field by field.
        /// </summary>
        internal static readonly bool UsesNativeCopy = true;

        /// <summary>
        /// Performs once of type compatibility check.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown if the type T is incompatible</exception>
        static FastStructure()
        {
            // Performs compatibility checks upon T
            CheckTypeCompatibility(typeof(T));

            // Shared memory holds T in one layout in every process. Where this runtime lays T out another way, the copies
            // built above give way to field-by-field ones. Anything that fails here keeps the copies above.
            try
            {
                ElementLayout layout = ElementLayout.For(typeof(T));
                if (layout == null)
                    return;
                int[] offsets = layout.RuntimeOffsets(typeof(T));
                if (!layout.Matches(offsets, Size))
                {
                    Delegate[] copies = layout.BuildCopies(typeof(T), offsets, Size,
                        typeof(PtrToStructureDelegate), typeof(StructureToPtrDelegate), typeof(PinnedCopyDelegate));
                    if (copies == null)
                        return;
                    PtrToStructure = (PtrToStructureDelegate)copies[0];
                    StructureToPtr = (StructureToPtrDelegate)copies[1];
                    CopyFromMemory = (PinnedCopyDelegate)copies[2];
                    CopyToMemory = (PinnedCopyDelegate)copies[3];
                    Size = layout.Size;
                    UsesNativeCopy = false;
                }
                Layout = layout;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Builds the field-by-field copies for T whether or not the running runtime needs them, for tests: the
        /// <see cref="PtrToStructureDelegate"/>, the <see cref="StructureToPtrDelegate"/>, and the array copies from and to
        /// memory. Null if T has no <see cref="Layout"/>.
        /// </summary>
        internal static Delegate[] BuildFieldByFieldCopies()
        {
            ElementLayout layout = ElementLayout.For(typeof(T));
            if (layout == null)
                return null;
            return layout.BuildCopies(typeof(T), layout.RuntimeOffsets(typeof(T)), BuildSizeOf(),
                typeof(PtrToStructureDelegate), typeof(StructureToPtrDelegate), typeof(PinnedCopyDelegate));
        }

        private static int BuildSizeOf()
        {
            // Marshal.SizeOf still runs so that the types it refuses, such as generic structures, are refused as before.
            System.Runtime.InteropServices.Marshal.SizeOf(typeof(T));

            DynamicMethod sizeOf = new DynamicMethod("SizeOf<" + typeof(T).FullName + ">",
                typeof(int), Type.EmptyTypes, typeof(FastStructure).Module, true);

            ILGenerator generator = sizeOf.GetILGenerator();
            generator.Emit(OpCodes.Sizeof, typeof(T));
            generator.Emit(OpCodes.Ret);
            return ((SizeOfDelegate)sizeOf.CreateDelegate(typeof(SizeOfDelegate)))();
        }

        /// <summary>
        /// Emits a copy between memory and an array that holds a reference to the array's first element in a pinned local, so
        /// that a garbage collection cannot move the array while the copy runs. A pinned <see cref="System.Runtime.InteropServices.GCHandle"/>
        /// cannot do this for every T: the .NET Framework refuses one for an array of structures with bool or char fields.
        /// </summary>
        private static unsafe PinnedCopyDelegate BuildPinnedCopy(bool intoArray)
        {
            DynamicMethod copy = new DynamicMethod((intoArray ? "CopyFromMemory<" : "CopyToMemory<") + typeof(T).FullName + ">",
                null, new Type[4] { typeof(T[]), typeof(int), typeof(void*), typeof(long) }, typeof(FastStructure).Module, true);

            ILGenerator generator = copy.GetILGenerator();
            generator.DeclareLocal(typeof(T).MakeByRefType(), true);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldelema, typeof(T));
            generator.Emit(OpCodes.Stloc_0);
#if NETCORE
            // Buffer.MemoryCopy(source, destination, destinationSizeInBytes, sourceBytesToCopy)
            if (intoArray)
            {
                generator.Emit(OpCodes.Ldarg_2);
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Conv_U);
            }
            else
            {
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Conv_U);
                generator.Emit(OpCodes.Ldarg_2);
            }
            generator.Emit(OpCodes.Ldarg_3);
            generator.Emit(OpCodes.Ldarg_3);
            generator.Emit(OpCodes.Call, typeof(Buffer).GetMethod("MemoryCopy", new Type[4] { typeof(void*), typeof(void*), typeof(long), typeof(long) }));
#else
            // UnsafeNativeMethods.CopyMemoryPtr(destination, source, count)
            if (intoArray)
            {
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Conv_U);
                generator.Emit(OpCodes.Ldarg_2);
            }
            else
            {
                generator.Emit(OpCodes.Ldarg_2);
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Conv_U);
            }
            generator.Emit(OpCodes.Ldarg_3);
            generator.Emit(OpCodes.Conv_U4);
            generator.Emit(OpCodes.Call, typeof(UnsafeNativeMethods).GetMethod("CopyMemoryPtr", BindingFlags.Static | BindingFlags.NonPublic));
#endif
            generator.Emit(OpCodes.Ret);
            return (PinnedCopyDelegate)copy.CreateDelegate(typeof(PinnedCopyDelegate));
        }

        private unsafe static GetPtrDelegate BuildFunction()
        {
            method = new DynamicMethod("GetStructurePtr<" + typeof(T).FullName + ">",
                typeof(void*), new Type[1] { typeof(T).MakeByRefType() }, typeof(FastStructure).Module);

            ILGenerator generator = method.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Conv_U);
            generator.Emit(OpCodes.Ret);
            return (GetPtrDelegate)method.CreateDelegate(typeof(GetPtrDelegate));
        }

        private static unsafe PtrToStructureDelegate BuildLoadFromPointerFunction()
        {
            methodLoad = new DynamicMethod("PtrToStructure<" + typeof(T).FullName + ">",
                typeof(T), new Type[1] { typeof(IntPtr) }, typeof(FastStructure).Module);

            ILGenerator generator = methodLoad.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldobj, typeof(T));
            generator.Emit(OpCodes.Ret);

            return (PtrToStructureDelegate)methodLoad.CreateDelegate(typeof(PtrToStructureDelegate));
        }

        private static unsafe StructureToPtrDelegate BuildWriteToPointerFunction()
        {
            methodWrite = new DynamicMethod("StructureToPtr<" + typeof(T).FullName + ">",
                null, new Type[2] { typeof(T).MakeByRefType(), typeof(IntPtr) }, typeof(FastStructure).Module);

            ILGenerator generator = methodWrite.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldobj, typeof(T));
            generator.Emit(OpCodes.Stobj, typeof(T));
            generator.Emit(OpCodes.Ret);
            return (StructureToPtrDelegate)methodWrite.CreateDelegate(typeof(StructureToPtrDelegate));
        }

        private static void CheckTypeCompatibility(Type t, System.Collections.Generic.HashSet<Type> checkedItems = null)
        {
            if (checkedItems == null)
            {
                checkedItems = new System.Collections.Generic.HashSet<Type>();
                checkedItems.Add(typeof(char));
                checkedItems.Add(typeof(byte));
                checkedItems.Add(typeof(sbyte));
                checkedItems.Add(typeof(bool));
                checkedItems.Add(typeof(double));
                checkedItems.Add(typeof(float));
                checkedItems.Add(typeof(decimal));
                checkedItems.Add(typeof(int));
                checkedItems.Add(typeof(short));
                checkedItems.Add(typeof(long));
                checkedItems.Add(typeof(uint));
                checkedItems.Add(typeof(ushort));
                checkedItems.Add(typeof(ulong));
                checkedItems.Add(typeof(IntPtr));
                checkedItems.Add(typeof(void*));
            }

            if (checkedItems.Contains(t))
                return;
            else
                checkedItems.Add(t);

            FieldInfo[] fi = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (FieldInfo info in fi)
            {
                if (!info.FieldType.IsPrimitive && !info.FieldType.IsValueType && !info.FieldType.IsPointer)
                {
                    throw new ArgumentException(String.Format("Non-value types are not supported: field {0} is of type {1} in structure {2}", info.Name, info.FieldType.Name, info.DeclaringType.Name));
                }

                // Example for adding future marshal attributes as incompatible
                //System.Runtime.InteropServices.MarshalAsAttribute attr;
                //if (TryGetAttribute<System.Runtime.InteropServices.MarshalAsAttribute>(info, out attr))
                //{
                //    if (attr.Value == System.Runtime.InteropServices.UnmanagedType.ByValArray)
                //    {
                //        throw new ArgumentException(String.Format("UnmanagedType.ByValArray is not supported on field {0} in type [{1}].", info.Name, typeof(T).FullName));
                //    }
                //}

                CheckTypeCompatibility(info.FieldType, checkedItems);
            }
        }

        //private static bool TryGetAttribute<T1>(MemberInfo memberInfo, out T1 customAttribute) where T1 : Attribute
        //{
        //    var attributes = memberInfo.GetCustomAttributes(typeof(T1), false).FirstOrDefault();
        //    if (attributes == null)
        //    {
        //        customAttribute = null;
        //        return false;
        //    }
        //    customAttribute = (T1)attributes;
        //    return true;
        //}
    }
}