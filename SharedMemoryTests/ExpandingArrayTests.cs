using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory.Utilities;

namespace SharedMemoryTests
{
    [TestClass]
    public class ExpandingArrayTests
    {
        [TestMethod]
        public void ExpandingArrayTests_GrownReport()
        {
            var sb = new StringBuilder();

            for (int i = 0; i < 32; i++)
            {
                sb.AppendFormat("{0}/{1}\r\n", i, ExpandingArray<int>.GetBucketIndex(i));
            }

            Assert.AreEqual(
@"0/0
1/0
2/0
3/1
4/1
5/1
6/1
7/2
8/2
9/2
10/2
11/2
12/2
13/2
14/2
15/3
16/3
17/3
18/3
19/3
20/3
21/3
22/3
23/3
24/3
25/3
26/3
27/3
28/3
29/3
30/3
31/4
", sb.ToString());
        }

        [TestMethod]
        public void ExpandingArrayTests_Basic()
        {
            var ea = new ExpandingArray<int>(size => new int[size]);
            TestEArray(ea);
            ea.Clear();
            TestEArray(ea);
        }

        private static void TestEArray(ExpandingArray<int> ea)
        {
            Assert.AreEqual(0, ea.Count);

            ea.Add(11);
            Assert.AreEqual(1, ea.Count);

            ea.Add(22);
            Assert.AreEqual(2, ea.Count);
            Assert.IsTrue(ea.Contains(11));
            Assert.IsFalse(ea.Contains(222));

            ea.Add(33);
            ea.Add(44);
            ea.Add(55);
            ea.Add(66);
            ea.Add(77);
            ea.Add(88);
            ea.Add(99);
            ea.Add(1010);
            ea.Add(111);
            ea.Add(1212);
            ea.Add(1313);
            ea.Add(1414);
            ea.Add(1515);
            ea.Add(1616);
            ea.Add(1717);

            Assert.AreEqual(10403, ea.Sum());

            Assert.AreEqual(6, ea.IndexOf(77));
            Assert.AreEqual(77, ea[6]);
            ea[6] = 777;
            Assert.AreEqual(777, ea[6]);

            Assert.AreEqual(11103, ea.Sum());

            
//            var a = new int[ea.Count + 1];
//            ea.CopyTo(a, 1);
//            Assert.AreEqual(
//                @"[0],
//[11],
//[22],
//[33],
//[44],
//[55],
//[66],
//[777],
//[88],
//[99],
//[1010],
//[111],
//[1212],
//[1313],
//[1414],
//[1515],
//[1616],
//[1717]
//", a.Dump());
        }
    }
}
