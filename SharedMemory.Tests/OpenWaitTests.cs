using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// How an opener of <see cref="BufferReadWrite"/> and <see cref="SharedArray{T}"/> sizes the buffer from the header its owner
    /// wrote, and how it fails on a name that does not exist. An owner makes the shared memory before it writes the header, so an
    /// opener can find the size still 0: it waits up to 1 s for the size, then gives up with InvalidOperationException.
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

        /// <summary>
        /// The owner writes the size 100 ms after an opener starts: the opener is still waiting then, and opens with the owner's size.
        /// </summary>
        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void Opener_OfAHeaderWrittenLate_WaitsAndOpensWithItsSize(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new UnwrittenHeader(name, Size))
            using (var started = new ManualResetEventSlim())
            {
                Task<SharedBuffer> opening = Task.Factory.StartNew(() => { started.Set(); return Open(kind, name); }, TaskCreationOptions.LongRunning);
                try
                {
                    Assert.IsTrue(started.Wait(5000), "The opener did not start.");
                    Thread.Sleep(100);
                    Assert.IsFalse(opening.IsCompleted, "The opener should still be waiting for the size.");
                    owner.WriteSize();

                    Assert.IsTrue(opening.Wait(5000), "The opener did not open once the size was written.");
                    Assert.AreEqual(Size, opening.Result.BufferSize);
                }
                finally
                {
                    // Whatever the opener opened is disposed, also after a failed assert
                    if (SpinWait.SpinUntil(() => opening.IsCompleted, 5000) && opening.Status == TaskStatus.RanToCompletion)
                        opening.Result.Dispose();
                }
            }
        }

        /// <summary>
        /// The owner never writes the size: the opener gives up after 1 s with InvalidOperationException naming the buffer.
        /// </summary>
        [TestMethod]
        [DataRow(BufferKind)]
        [DataRow(ArrayKind)]
        public void Opener_OfAHeaderNeverWritten_ThrowsInvalidOperationExceptionAfterOneSecond(string kind)
        {
            string name = Guid.NewGuid().ToString();
            using (new UnwrittenHeader(name, Size))
            {
                var watch = Stopwatch.StartNew();
                var e = Assert.ThrowsExactly<InvalidOperationException>(() => Open(kind, name).Dispose());
                watch.Stop();

                StringAssert.Contains(e.Message, name);
                Assert.IsTrue(watch.ElapsedMilliseconds >= 1000 && watch.ElapsedMilliseconds <= 1500, "gave up after " + watch.ElapsedMilliseconds + " ms");
            }
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

        /// <summary>
        /// An owner caught between making its shared memory and writing the header: it creates the buffer as every owner does, then
        /// sets the header's size back to 0, as an opener finds it in that window, until <see cref="WriteSize"/>.
        /// </summary>
        sealed unsafe class UnwrittenHeader : SharedBuffer
        {
            public UnwrittenHeader(string name, long bufferSize)
                : base(name, bufferSize, true)
            {
                Open();
                Header->SharedMemorySize = 0;
            }

            public void WriteSize()
            {
                Header->SharedMemorySize = SharedMemorySize;
            }
        }
    }
}
