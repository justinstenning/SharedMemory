using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The protected members a subclass can use: the events, set on Windows only, and the map and its
    /// view, set everywhere but macOS, where the memory is mapped without a MemoryMappedFile. A buffer
    /// waits on, signals and disposes its own events, whatever a subclass assigns to the properties.
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
#if NET
        [System.Runtime.Versioning.SupportedOSPlatform("windows")] // EventWaitHandle.OpenExisting
#endif
        public void CircularBuffer_DataExistsAssignedBySubclass_IsNotSignalledOnWrite()
        {
            string name = Guid.NewGuid().ToString();
            using (var assigned = new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var buffer = new ExposedCircularBuffer(name, 2, 64))
            using (var dataExists = EventWaitHandle.OpenExisting(name + "_evt_dataexists"))
            {
                buffer.AssignDataExists(assigned);

                buffer.Write(new byte[] { 1 });

                Assert.IsFalse(assigned.WaitOne(0), "The write signalled the assigned handle.");
                Assert.IsTrue(dataExists.WaitOne(0), "The write did not signal the buffer's own event.");
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
#if NET
        [System.Runtime.Versioning.SupportedOSPlatform("windows")] // EventWaitHandle.OpenExisting
#endif
        public void CircularBuffer_NodeAvailableAssignedBySubclass_IsNotSignalledOnRead()
        {
            string name = Guid.NewGuid().ToString();
            using (var assigned = new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var buffer = new ExposedCircularBuffer(name, 2, 64))
            using (var nodeAvailable = EventWaitHandle.OpenExisting(name + "_evt_nodeavail"))
            {
                // Two nodes hold one node of data, so this write fills the buffer
                buffer.Write(new byte[] { 1 });
                buffer.AssignNodeAvailable(assigned);

                buffer.Read(new byte[64]);

                Assert.IsFalse(assigned.WaitOne(0), "The read signalled the assigned handle.");
                Assert.IsTrue(nodeAvailable.WaitOne(0), "The read did not signal the buffer's own event.");
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Windows)]
        public void CircularBuffer_Dispose_ClosesItsOwnEventsNotAssignedOnes()
        {
            string name = Guid.NewGuid().ToString();
            using (var assignedDataExists = new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var assignedNodeAvailable = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                EventWaitHandle ownDataExists, ownNodeAvailable;
                using (var buffer = new ExposedCircularBuffer(name, 2, 64))
                {
                    ownDataExists = buffer.ExposedDataExists;
                    ownNodeAvailable = buffer.ExposedNodeAvailable;
                    buffer.AssignDataExists(assignedDataExists);
                    buffer.AssignNodeAvailable(assignedNodeAvailable);
                }

                Assert.ThrowsExactly<ObjectDisposedException>(() => ownDataExists.WaitOne(0), "DataExists's own handle was left open.");
                Assert.ThrowsExactly<ObjectDisposedException>(() => ownNodeAvailable.WaitOne(0), "NodeAvailable's own handle was left open.");
                Assert.IsFalse(assignedDataExists.WaitOne(0), "The assigned DataExists handle was closed.");
                Assert.IsFalse(assignedNodeAvailable.WaitOne(0), "The assigned NodeAvailable handle was closed.");
            }
        }

        static void AssertSet(bool expected, object member, string memberName)
        {
            if (expected)
                Assert.IsNotNull(member, memberName + " should be set on this platform.");
            else
                Assert.IsNull(member, memberName + " should be null on this platform.");
        }

#pragma warning disable CS0618 // the wait-handle properties are obsolete
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

            public void AssignDataExists(EventWaitHandle handle)
            {
                DataExists = handle;
            }

            public void AssignNodeAvailable(EventWaitHandle handle)
            {
                NodeAvailable = handle;
            }
        }
#pragma warning restore CS0618
    }
}
