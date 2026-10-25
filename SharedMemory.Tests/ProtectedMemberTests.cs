using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The protected members a subclass can use: the events, set on Windows only, and the map and its
    /// view, set everywhere but macOS, where the memory is mapped without a MemoryMappedFile.
    /// </summary>
    [TestClass]
    public class ProtectedMemberTests
    {
        static bool EventsExpected
        {
            get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); }
        }

        static bool MapExpected
        {
            get { return !RuntimeInformation.IsOSPlatform(OSPlatform.OSX); }
        }

        [TestMethod]
        public void BufferWithLocks_ProtectedMembers_SetByPlatform()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new ExposedBufferReadWrite(name, 1024))
            using (var consumer = new ExposedBufferReadWrite(name))
            {
                foreach (var buffer in new[] { owner, consumer })
                {
                    AssertSet(EventsExpected, buffer.ExposedWriteWaitEvent, "WriteWaitEvent");
                    AssertSet(EventsExpected, buffer.ExposedReadWaitEvent, "ReadWaitEvent");
                    AssertSet(MapExpected, buffer.ExposedMmf, "Mmf");
                    AssertSet(MapExpected, buffer.ExposedView, "View");
                }
            }
        }

        [TestMethod]
        public void CircularBuffer_ProtectedMembers_SetByPlatform()
        {
            string name = Guid.NewGuid().ToString();
            using (var owner = new ExposedCircularBuffer(name, 2, 64))
            using (var consumer = new ExposedCircularBuffer(name))
            {
                foreach (var buffer in new[] { owner, consumer })
                {
                    AssertSet(EventsExpected, buffer.ExposedDataExists, "DataExists");
                    AssertSet(EventsExpected, buffer.ExposedNodeAvailable, "NodeAvailable");
                    AssertSet(MapExpected, buffer.ExposedMmf, "Mmf");
                    AssertSet(MapExpected, buffer.ExposedView, "View");
                }
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void CircularBuffer_DataExistsReplacedBySubclass_IsSignalledOnWrite()
        {
            string name = Guid.NewGuid().ToString();
            using (var replacement = new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var buffer = new ExposedCircularBuffer(name, 2, 64))
            {
                buffer.ReplaceDataExists(replacement);

                buffer.Write(new byte[] { 1 });

                Assert.IsTrue(replacement.WaitOne(0));
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void CircularBuffer_NodeAvailableReplacedBySubclass_IsSignalledOnRead()
        {
            string name = Guid.NewGuid().ToString();
            using (var replacement = new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var buffer = new ExposedCircularBuffer(name, 2, 64))
            {
                // Two nodes hold one node of data, so this write fills the buffer
                buffer.Write(new byte[] { 1 });
                buffer.ReplaceNodeAvailable(replacement);

                buffer.Read(new byte[64]);

                Assert.IsTrue(replacement.WaitOne(0));
            }
        }

        static void AssertSet(bool expected, object member, string memberName)
        {
            if (expected)
                Assert.IsNotNull(member, memberName + " should be set on this platform.");
            else
                Assert.IsNull(member, memberName + " should be null on this platform.");
        }

        class ExposedBufferReadWrite : BufferReadWrite
        {
            public ExposedBufferReadWrite(string name, int bufferSize) : base(name, bufferSize) { }
            public ExposedBufferReadWrite(string name) : base(name) { }

            public EventWaitHandle ExposedWriteWaitEvent { get { return WriteWaitEvent; } }
            public EventWaitHandle ExposedReadWaitEvent { get { return ReadWaitEvent; } }
            public object ExposedMmf { get { return Mmf; } }
            public object ExposedView { get { return View; } }
        }

        class ExposedCircularBuffer : CircularBuffer
        {
            public ExposedCircularBuffer(string name, int nodeCount, int nodeBufferSize) : base(name, nodeCount, nodeBufferSize) { }
            public ExposedCircularBuffer(string name) : base(name) { }

            public EventWaitHandle ExposedDataExists { get { return DataExists; } }
            public EventWaitHandle ExposedNodeAvailable { get { return NodeAvailable; } }
            public object ExposedMmf { get { return Mmf; } }
            public object ExposedView { get { return View; } }

            public void ReplaceDataExists(EventWaitHandle replacement)
            {
                DataExists.Close();
                DataExists = replacement;
            }

            public void ReplaceNodeAvailable(EventWaitHandle replacement)
            {
                NodeAvailable.Close();
                NodeAvailable = replacement;
            }
        }
    }
}
