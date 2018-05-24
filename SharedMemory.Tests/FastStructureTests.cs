// SharedMemory (File: SharedMemoryTests\FastStructureTests.cs)
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.InteropServices;
using SharedMemory;

namespace SharedMemoryTests
{
    [TestClass]
    public class FastStructureTests
    {
        #region Test Structures

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public unsafe struct CompatibleStructure
        {
            public int Integer1;

            public IntPtr Pointer1;

            public IntPtr Pointer2;

            public IntPtr Pointer3;

            public IntPtr Pointer4;

            public fixed byte Contents[8];

            public int Bookend;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IncompatibleNestedStructure
        {
            public int IncompatibleNestedStructure_One;

            public object IncompatibleNestedStructure_Two;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IncompatibleNestedStructure2
        {
            public int IncompatibleNestedStructure_One;

            public object IncompatibleNestedStructure_Two;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512, ArraySubType = UnmanagedType.I2)]
            public char[] Filename;
        }

        public struct HasIncompatibleStructure
        {
            public int HasIncompatibleStructure_One;

            public IncompatibleNestedStructure HasIncompatibleStructure_Two;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ComplexStructure
        {

            public int FirstElement;

            public CompatibleStructure Compatible;

            public int FinalElement;
        }

        #endregion

        [TestMethod]
        public void FastStructure_IncompabitibleNestedType()
        {
            try
            {
                var size = FastStructure<HasIncompatibleStructure>.Size;
            }
            catch (TypeInitializationException e)
            {
                return;
            }

            Assert.Fail("Did not throw TypeInitializationException for incompatible nested type: IncompatibleNestedStructure.");
        }

        [TestMethod]
        public void FastStructure_IncompatibleStructure()
        {
            try
            {
                var size = FastStructure<IncompatibleNestedStructure2>.Size;
            }
            catch (TypeInitializationException e)
            {
                return;
            }

            Assert.Fail("Did not throw TypeInitializationException for incompatible type: NestedStructure2.");
        }

        [TestMethod]
        public void FastStructure_CompatibleStructureSize()
        {
            Assert.AreEqual(IntPtr.Size * 4 + 8 + (sizeof(int) * 2), FastStructure<CompatibleStructure>.Size);
        }

        [TestMethod]
        public void FastStructure_ComplexStructureSize()
        {
            var sizeOfCompatibleStructure = IntPtr.Size * 4 + 8 + (sizeof(int) * 2);
            var sizeOfComplexStructure = (sizeof(int) * 2) + sizeOfCompatibleStructure;
            Assert.AreEqual(sizeOfComplexStructure, FastStructure<ComplexStructure>.Size);
        }

        [TestMethod]
        public void FastStructure_AllocHGlobalReadWrite()
        {
            IntPtr mem = Marshal.AllocHGlobal(FastStructure.SizeOf<ComplexStructure>());

            ComplexStructure n = new ComplexStructure();

            n.Compatible.Integer1 = 1;
            n.Compatible.Bookend = 2;

            n.FirstElement = 3;
            n.FinalElement = 9;
            unsafe
            {
                n.Compatible.Contents[0] = 4;
                n.Compatible.Contents[7] = 5;
            }

            FastStructure.StructureToPtr(ref n, mem);

            // Assert that the reading and writing result in same structure
            ComplexStructure m = FastStructure.PtrToStructure<ComplexStructure>(mem);
            Assert.AreEqual(n, m);
            Assert.AreEqual(n.Compatible.Integer1, m.Compatible.Integer1);
            Assert.AreEqual(n.Compatible.Bookend, m.Compatible.Bookend);
            unsafe
            {
                Assert.AreEqual(n.Compatible.Contents[0], m.Compatible.Contents[0]);
                Assert.AreEqual(n.Compatible.Contents[7], m.Compatible.Contents[7]);
            }

            // Assert that Marshal.PtrToStructure is compatible
            m = (ComplexStructure)Marshal.PtrToStructure(mem, typeof(ComplexStructure));
            Assert.AreEqual(n, m);
            Assert.AreEqual(n.Compatible.Integer1, m.Compatible.Integer1);
            Assert.AreEqual(n.Compatible.Bookend, m.Compatible.Bookend);
            unsafe
            {
                Assert.AreEqual(n.Compatible.Contents[0], m.Compatible.Contents[0]);
                Assert.AreEqual(n.Compatible.Contents[7], m.Compatible.Contents[7]);
            }

            Marshal.FreeHGlobal(mem);
        }
    }
}
