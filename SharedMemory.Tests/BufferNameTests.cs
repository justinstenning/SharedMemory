using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SharedMemoryTests
{
    /// <summary>
    /// Names that differ only in case, Unicode normalisation, or ß against SS are different buffers: Windows compares
    /// object names case-sensitively and code unit for code unit, and Linux's /dev/shm compares bytes. The Mac's volume
    /// folds all three, so its lock file names carry a hash of the exact name.
    /// </summary>
    [TestClass]
    public class BufferNameTests
    {
        [TestMethod]
        [DataRow("TEST", "test")]
        [DataRow("café", "café")]
        [DataRow("Straße", "STRASSE")]
        public void NamesDifferingOnlyInCaseOrNormalisation_AreSeparateBuffers(string first, string second)
        {
            string prefix = "SharedMemoryTests-" + Guid.NewGuid().ToString("N") + "-";
            byte[] firstData = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            byte[] secondData = Enumerable.Range(101, 16).Select(i => (byte)i).ToArray();

            using (var firstOwner = new EventFreeBuffer(prefix + first, 64))
            using (var secondOwner = new EventFreeBuffer(prefix + second, 64))
            {
                firstOwner.WriteBytes(firstData);
                secondOwner.WriteBytes(secondData);

                using (var firstOpened = new EventFreeBuffer(prefix + first))
                using (var secondOpened = new EventFreeBuffer(prefix + second))
                {
                    CollectionAssert.AreEqual(firstData, firstOpened.ReadBytes(firstData.Length));
                    CollectionAssert.AreEqual(secondData, secondOpened.ReadBytes(secondData.Length));
                }
            }
        }
    }
}
