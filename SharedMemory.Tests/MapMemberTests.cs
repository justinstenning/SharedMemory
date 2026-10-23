using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SharedMemoryTests
{
    /// <summary>
    /// The protected map members of a buffer that makes no events, so that macOS's can be checked before it has named
    /// events: Mmf and View set everywhere but macOS, where the memory is mapped without a MemoryMappedFile, and the
    /// pointers set everywhere.
    /// </summary>
    [TestClass]
    public class MapMemberTests
    {
        static bool MapExpected
        {
            get { return !RuntimeInformation.IsOSPlatform(OSPlatform.OSX); }
        }

        [TestMethod]
        public void SharedBuffer_MapMembers_SetByPlatform()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new ExposedBuffer(name, 1024))
            using (var consumer = new ExposedBuffer(name))
            {
                foreach (var buffer in new[] { owner, consumer })
                {
                    if (MapExpected)
                    {
                        Assert.IsNotNull(buffer.ExposedMmf, "Mmf should be set on this platform.");
                        Assert.IsNotNull(buffer.ExposedView, "View should be set on this platform.");
                    }
                    else
                    {
                        Assert.IsNull(buffer.ExposedMmf, "Mmf should be null on this platform.");
                        Assert.IsNull(buffer.ExposedView, "View should be null on this platform.");
                    }
                    Assert.IsTrue(buffer.PointersSet, "ViewPtr, BufferStartPtr, and Header should be set.");
                    Assert.AreEqual(1024, buffer.BufferSize);
                }
            }
        }

#if NET
        /// <summary>
        /// A struct read or write that runs past the end of the buffer throws as a view's does, rather than reaching what lies
        /// beyond: on macOS the mapping runs on to the end of its page. After Close they throw NullReferenceException, as
        /// SharedBuffer.Close documents. The .NET Framework 3.5 build's views are its own, and are not checked here.
        /// </summary>
        [TestMethod]
        public void SharedBuffer_StructAccessPastTheEnd_ThrowsAsAViewDoes()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new ExposedBuffer(name, 1024))
            using (var consumer = new ExposedBuffer(name))
            {
                // The opener's mapping is a whole number of pages on macOS, the owner's exactly the buffer.
                foreach (var buffer in new[] { consumer, owner })
                {
                    long value = 42;
                    buffer.WriteStruct(ref value, 1016);
                    Assert.AreEqual(42L, buffer.ReadStruct(1016));

                    Assert.ThrowsExactly<ArgumentException>(() => { long v = 1; buffer.WriteStruct(ref v, 1020); });
                    Assert.ThrowsExactly<ArgumentException>(() => buffer.ReadStruct(1020));
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => { long v = 1; buffer.WriteStruct(ref v, 1024); });
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.ReadStruct(1024));
                    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.ReadStruct(-17));

                    buffer.Close();
                    Assert.ThrowsExactly<NullReferenceException>(() => { long v = 1; buffer.WriteStruct(ref v, 0); });
                    Assert.ThrowsExactly<NullReferenceException>(() => buffer.ReadStruct(0));
                }
            }
        }
#endif

        class ExposedBuffer : EventFreeBuffer
        {
            public ExposedBuffer(string name, long bufferSize) : base(name, bufferSize) { }
            public ExposedBuffer(string name) : base(name) { }

            // object, since the .NET Framework 3.5 build has its own MemoryMappedFile types
            public object ExposedMmf { get { return Mmf; } }
            public object ExposedView { get { return View; } }

            public void WriteStruct(ref long value, long bufferPosition)
            {
                Write(ref value, bufferPosition);
            }

            public long ReadStruct(long bufferPosition)
            {
                long value;
                Read(out value, bufferPosition);
                return value;
            }

            public unsafe bool PointersSet
            {
                get { return ViewPtr != null && BufferStartPtr != null && Header != null && BufferStartPtr == ViewPtr + 16 && (byte*)Header == ViewPtr; }
            }
        }
    }
}
