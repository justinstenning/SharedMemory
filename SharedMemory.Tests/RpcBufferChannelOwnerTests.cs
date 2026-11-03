#if HAS_RPC_BUFFER
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// RpcBuffer.IsChannelOwner in one process. RpcElectionNameTests, RpcBufferAcrossProcessesTests and the RPC peer read it
    /// too, across sessions and processes.
    /// </summary>
    [TestClass]
    public class RpcBufferChannelOwnerTests
    {
        sealed class Probe : RpcBuffer
        {
            public Probe(string name) : base(name) { }
            public string Buffers
            {
                get { return "created the write buffer " + WriteBuffer.IsOwnerOfSharedMemory + ", the read buffer " + ReadBuffer.IsOwnerOfSharedMemory; }
            }
        }

        static string NewName()
        {
            return "ChannelOwner" + Guid.NewGuid().ToString("N");
        }

        [TestMethod]
        public void IsChannelOwner_FirstInstanceOnChannelOnly()
        {
            string name = NewName();
            using (var owner = new RpcBuffer(name))
            using (var opener = new RpcBuffer(name))
            {
                Assert.IsTrue(owner.IsChannelOwner, "The first RpcBuffer on a channel is its owner.");
                Assert.IsFalse(opener.IsChannelOwner, "A second RpcBuffer on a channel is its opener.");
            }
        }

        [TestMethod]
        public void IsChannelOwner_TheInstanceThatCreatedBothBuffers()
        {
            string name = NewName();
            using (var owner = new Probe(name))
            using (var opener = new Probe(name))
            {
                Assert.AreEqual("owner True, created the write buffer True, the read buffer True", "owner " + owner.IsChannelOwner + ", " + owner.Buffers);
                Assert.AreEqual("owner False, created the write buffer False, the read buffer False", "owner " + opener.IsChannelOwner + ", " + opener.Buffers);
            }
        }

        [TestMethod]
        public void IsChannelOwner_UnchangedByDispose()
        {
            string name = NewName();
            var owner = new RpcBuffer(name);
            var opener = new RpcBuffer(name);
            opener.Dispose();
            owner.Dispose();
            Assert.IsTrue(owner.IsChannelOwner, "An owner still reports it after Dispose.");
            Assert.IsFalse(opener.IsChannelOwner, "An opener still reports it after Dispose.");
        }
    }
}
#endif
