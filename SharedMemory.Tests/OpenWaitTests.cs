using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// How an opener of <see cref="BufferReadWrite"/> and <see cref="SharedArray{T}"/> sizes the buffer from the header its owner
    /// wrote, and how it fails on a name that does not exist.
    /// </summary>
    [TestClass]
    public class OpenWaitTests
    {
        const string BufferKind = "BufferReadWrite";
        const string ArrayKind = "SharedArray";
        const int Size = 1024;

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void Opener_OfAnExistingBuffer_OpensWithItsSize(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (SharedBuffer owner = Create(kind, name))
            using (SharedBuffer opener = Open(kind, name))
            {
                Assert.IsFalse(opener.IsOwnerOfSharedMemory);
                Assert.AreEqual(Size, opener.BufferSize);
                Assert.AreEqual(owner.SharedMemorySize, opener.SharedMemorySize);
            }
        }

        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void Opener_OfAMissingName_ThrowsFileNotFoundException(string kind)
        {
            string name = Guid.NewGuid().ToString();

            Assert.ThrowsExactly<FileNotFoundException>(() => Open(kind, name).Dispose());
        }

        static SharedBuffer Create(string kind, string name)
        {
            if (kind == ArrayKind)
                return new SharedArray<int>(name, Size / sizeof(int));
            return new BufferReadWrite(name, Size);
        }

        static SharedBuffer Open(string kind, string name)
        {
            if (kind == ArrayKind)
                return new SharedArray<int>(name);
            return new BufferReadWrite(name);
        }
    }
}
