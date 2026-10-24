// SharedMemory (File: SharedMemoryTests\Net35MappingTests.cs)
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


#if LIBRARY_NET35
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The net35 build maps memory through its own CreateFileMapping and MapViewOfFile wrappers, which split a 64-bit
    /// size or offset into the high and low 32-bit halves that kernel32 takes (#85). The tests call those internal
    /// wrappers by reflection on a sparse file: Windows grows the file to the size the mapping asks for, so the file's
    /// length shows the halves that were passed, and a mapping of several GB takes neither memory nor disk space.
    /// </summary>
    [TestClass]
    public class Net35MappingTests
    {
        const uint FSCTL_SET_SPARSE = 0x000900C4;
        const uint PageReadWrite = 0x04;
        const uint FileMapRead = 0x04;
        const int ViewSize = 65536;

        static readonly Type NativeMethods = typeof(SharedBuffer).Assembly.GetType("SharedMemory.UnsafeNativeMethods", true);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        [TestMethod]
        public void CreateFileMapping_SizeOf2GBOrMore_AsksForExactlyThatSize()
        {
            var wrong = new List<string>();
            foreach (long size in new[] { int.MaxValue, 1L << 31, 3L << 30, (1L << 32) + 5 })
            {
                using (FileStream file = SparseFile())
                using (SafeHandle mapping = CreateFileMapping(file, size))
                {
                    long asked = file.Length;
                    if (asked != size)
                        wrong.Add($"size {size} ({Halves(size)}): the mapping asked for {asked} ({Halves(asked)})");
                }
            }
            Assert.AreEqual(0, wrong.Count, string.Join("; ", wrong.ToArray()));
        }

        [TestMethod]
        public void MapViewOfFile_OffsetOf2GBOrMore_MapsThatOffset()
        {
            // View offsets must be multiples of the 64 KB allocation granularity, so these stand in for the sizes above.
            long[] offsets = { 1L << 31, 3L << 30, 1L << 32, (1L << 32) + ViewSize };
            var wrong = new List<string>();
            using (FileStream file = SparseFile())
            {
                foreach (long offset in offsets)
                {
                    file.Position = offset;
                    file.Write(BitConverter.GetBytes(offset), 0, sizeof(long));
                }
                file.Flush();

                using (SafeHandle mapping = CreateFileMapping(file, (1L << 32) + 2 * ViewSize))
                {
                    foreach (long offset in offsets)
                    {
                        using (SafeHandle view = MapViewOfFile(mapping, offset))
                        {
                            if (view.IsInvalid)
                                wrong.Add($"offset {offset} ({Halves(offset)}): MapViewOfFile failed with error {Marshal.GetLastWin32Error()}");
                            else if (Marshal.ReadInt64(view.DangerousGetHandle()) != offset)
                                wrong.Add($"offset {offset} ({Halves(offset)}): the view starts at file offset {Marshal.ReadInt64(view.DangerousGetHandle())}");
                        }
                    }
                }
            }
            Assert.AreEqual(0, wrong.Count, string.Join("; ", wrong.ToArray()));
        }

        static string Halves(long value)
        {
            return $"high {(uint)(value >> 32)}, low {(uint)(value & 0xFFFFFFFF)}";
        }

        static FileStream SparseFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "Net35MappingTests_" + Guid.NewGuid().ToString("N"));
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            int returned;
            if (!DeviceIoControl(file.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                file.Dispose();
                Assert.Fail($"The temporary folder's file system cannot make a sparse file (error {error}), which these tests need.");
            }
            return file;
        }

        // UnsafeNativeMethods.CreateFileMapping(SafeFileHandle, FileMapProtection, long, string)
        static SafeHandle CreateFileMapping(FileStream file, long size)
        {
            MethodInfo create = NativeMethods.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(m => m.Name == "CreateFileMapping" && m.GetParameters().Length == 4);
            Type protection = create.GetParameters()[1].ParameterType;
            var mapping = (SafeHandle)create.Invoke(null, new object[] { file.SafeFileHandle, Enum.ToObject(protection, PageReadWrite), size, null });
            if (mapping.IsInvalid)
                Assert.Fail($"CreateFileMapping for {size} bytes failed with error {Marshal.GetLastWin32Error()}");
            return mapping;
        }

        // UnsafeNativeMethods.MapViewOfFile(SafeMemoryMappedFileHandle, FileMapAccess, ulong, UIntPtr)
        static SafeHandle MapViewOfFile(SafeHandle mapping, long offset)
        {
            MethodInfo map = NativeMethods.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(m => m.Name == "MapViewOfFile" && m.GetParameters()[2].ParameterType == typeof(ulong));
            Type access = map.GetParameters()[1].ParameterType;
            return (SafeHandle)map.Invoke(null, new object[] { mapping, Enum.ToObject(access, FileMapRead), (ulong)offset, new UIntPtr(ViewSize) });
        }
    }
}
#endif
