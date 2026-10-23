// SharedMemory (File: SharedMemoryTests\ArraySliceTests.cs)
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
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory.Utilities;

namespace SharedMemoryTests
{
    [TestClass]
    public class ArraySliceTests
    {
        [TestMethod]
        public void ArraySlice_WorksLikeArray()
        {
            var a = new[] {1.0, 2.71828, 3.14, 4, 4.99999, 42, 1024};
            var slicea = new ArraySlice<double>(a);
            var sliceaSame = new ArraySlice<double>(a);

            var b = new[] { 1.0, 2, 3, 4, 5, 99, 1024 };
            var sliceb = new ArraySlice<double>(b);

            Assert.AreEqual(a, slicea.List);
            Assert.AreEqual(0, slicea.Offset);
            Assert.AreEqual(7, slicea.Count);
            Assert.IsTrue(slicea.Equals(sliceaSame));
            Assert.IsTrue(slicea.Equals((object)sliceaSame));
            Assert.AreEqual(sliceaSame.GetHashCode(), sliceaSame.GetHashCode());
            Assert.IsTrue(slicea == sliceaSame);
            Assert.IsTrue(slicea != sliceb);

            Assert.IsTrue(ApproximatelyEqual(4, slicea[3]));
            Assert.AreEqual(6, slicea.IndexOf(1024));
            Assert.AreEqual(-1, slicea.IndexOf(1025));
            Assert.IsTrue(slicea.Contains(1024));
            Assert.IsFalse(slicea.Contains(1025));
            Assert.IsTrue(ApproximatelyEqual(1081.85827, slicea.Sum()));

            IList<double> asList = slicea;

            Assert.IsTrue(ApproximatelyEqual(4, asList[3]));
            Assert.AreEqual(6, asList.IndexOf(1024));
            Assert.AreEqual(-1, asList.IndexOf(1025));
            Assert.IsTrue(asList.Contains(1024));
            Assert.IsFalse(asList.Contains(1025));
            Assert.IsTrue(ApproximatelyEqual(1081.85827, asList.Sum()));
        }

        [TestMethod]
        public void ArraySlice_TestSlice()
        {
            var a = new[] { 1.0, 2.71828, 3.14, 4, 4.99999, 42, 1024 };
            var slicea = new ArraySlice<double>(a, 2, 3);
            var sliceaSame = new ArraySlice<double>(a, 2, 3);

            var b = new[] { 1.0, 2, 3, 4, 5, 99, 1024 };
            var sliceb = new ArraySlice<double>(b, 2, 3);

            Assert.AreEqual(a, slicea.List);
            Assert.AreEqual(2, slicea.Offset);
            Assert.AreEqual(3, slicea.Count);
            Assert.IsTrue(slicea.Equals(sliceaSame));
            Assert.IsTrue(slicea.Equals((object)sliceaSame));
            Assert.AreEqual(sliceaSame.GetHashCode(), sliceaSame.GetHashCode());
            Assert.IsTrue(slicea == sliceaSame);
            Assert.IsTrue(slicea != sliceb);

            Assert.IsTrue(ApproximatelyEqual(4.99999, slicea[2]));
            Assert.AreEqual(1, slicea.IndexOf(4));
            Assert.AreEqual(-1, slicea.IndexOf(1025));
            Assert.IsTrue(slicea.Contains(4));
            Assert.IsFalse(slicea.Contains(1025));
            Assert.IsTrue(ApproximatelyEqual(12.13999, slicea.Sum()));

            IList<double> asList = slicea;

            Assert.IsTrue(ApproximatelyEqual(4.99999, asList[2]));
            Assert.AreEqual(1, asList.IndexOf(4));
            Assert.AreEqual(-1, asList.IndexOf(1025));
            Assert.IsTrue(asList.Contains(4));
            Assert.IsFalse(asList.Contains(1025));
            Assert.IsTrue(ApproximatelyEqual(12.13999, asList.Sum()));
        }

        [TestMethod]
        public void ArraySlice_ToArrayAndToList_ReturnTheSlice()
        {
            var slice = new ArraySlice<int>(new[] { 1, 2, 3, 4, 5 }, 1, 3);

            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, slice.ToArray());
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, slice.ToList());
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, new List<int>(slice));
        }

        [TestMethod]
        public void ArraySlice_ICollectionCopyTo_CopiesAtArrayIndex()
        {
            ICollection<int> slice = new ArraySlice<int>(new[] { 1, 2, 3, 4, 5 }, 1, 3);
            var destination = new int[5];

            slice.CopyTo(destination, 1);

            CollectionAssert.AreEqual(new[] { 0, 2, 3, 4, 0 }, destination);
            Assert.ThrowsExactly<ArgumentException>(() => slice.CopyTo(new int[3], 1), "too small from arrayIndex");
            Assert.ThrowsExactly<ArgumentNullException>(() => slice.CopyTo(null, 0), "null");
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => slice.CopyTo(new int[5], -1), "negative arrayIndex");
        }

        [TestMethod]
        public void ArraySlice_ICollectionCopyTo_OntoItsOwnArray_CopiesLikeArrayCopy()
        {
            var array = new[] { 1, 2, 3, 4, 5 };
            ICollection<int> slice = new ArraySlice<int>(array, 0, 3);

            slice.CopyTo(array, 1);

            CollectionAssert.AreEqual(new[] { 1, 1, 2, 3, 5 }, array);
        }



        // http://stackoverflow.com/a/2411661/75129
        public static bool ApproximatelyEqual(double x, double y)
        {
            var epsilon = Math.Max(Math.Abs(x), Math.Abs(y)) * 1E-15;
            return Math.Abs(x - y) <= epsilon;
        }
    }
}
