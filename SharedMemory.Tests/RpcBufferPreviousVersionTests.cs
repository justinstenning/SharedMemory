#if HAS_RPC_BUFFER
using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// An RpcBuffer from SharedMemory 2.3 or earlier on the same channel, on Windows, the only OS where those versions run
    /// RpcBuffer. They elect with the mutex "{name}SharedMemory_MasterMutex", whose creator is the owner, and the owner creates
    /// the rings "{name}_Slave_SharedMemory_MMF" and "{name}_Master_SharedMemory_MMF". This version's owner also holds that
    /// mutex, so that a mixed pair fails at construction rather than both ends becoming owners of different objects. The older
    /// version is played here by what its constructor does with those names.
    /// </summary>
    [TestClass]
    [OSCondition(OperatingSystems.Windows)]
    public class RpcBufferPreviousVersionTests
    {
        const string PreviousElectionMutex = "SharedMemory_MasterMutex";
        const string PreviousOwnerWrites = "_Slave_SharedMemory_MMF";
        const string PreviousOwnerReads = "_Master_SharedMemory_MMF";

        static string NewName()
        {
            return "PreviousVersion" + Guid.NewGuid().ToString("N");
        }

        [TestMethod]
        public void PreviousVersionFirst_ThisVersionThrowsAndLeavesTheNameFree()
        {
            string name = NewName();
            bool createdNew;
            using (var previous = new Mutex(true, name + PreviousElectionMutex, out createdNew))
            using (new CircularBuffer(name + PreviousOwnerWrites, 10, 4096))
            using (new CircularBuffer(name + PreviousOwnerReads, 10, 4096))
            {
                Assert.IsTrue(createdNew);
                try
                {
                    var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => new RpcBuffer(name));
                    Assert.AreEqual("Channel \"" + name + "\" is in use by an RpcBuffer from SharedMemory 2.3 or earlier, which cannot share a channel with this version. Upgrade both ends.",
                        thrown.Message);
                }
                finally
                {
                    previous.ReleaseMutex();
                }
            }

            // The constructor that threw let go of what it made, so once the older version has gone the next RpcBuffer owns the channel
            using (var owner = new RpcBuffer(name))
            {
                Assert.IsTrue(owner.IsChannelOwner);
            }
        }

        [TestMethod]
        public void ThisVersionFirst_PreviousVersionFindsNoRing()
        {
            string name = NewName();
            using (var owner = new RpcBuffer(name))
            {
                Assert.IsTrue(owner.IsChannelOwner);
                bool createdNew;
                using (new Mutex(true, name + PreviousElectionMutex, out createdNew))
                {
                    Assert.IsFalse(createdNew, "This version's owner holds the name older versions elect with, so an older one becomes the opener.");
                    // An older opener opens the ring its owner writes first, and this version never creates it
                    Assert.ThrowsExactly<FileNotFoundException>(() => new CircularBuffer(name + PreviousOwnerWrites));
                }
            }
        }

        [TestMethod]
        public void ThisVersionOnly_TheNextOwnerAfterDisposeIsNotRefused()
        {
            string name = NewName();
            using (var owner = new RpcBuffer(name))
            using (var opener = new RpcBuffer(name, (Func<ulong, byte[], byte[]>)((id, payload) => payload)))
            {
                Assert.IsFalse(opener.IsChannelOwner);
                Assert.IsTrue(owner.RemoteRequest(new byte[] { 1 }, 5000).Success);
            }

            // Dispose let go of the name older versions elect with; otherwise the next owner would take it for an older version's
            using (var next = new RpcBuffer(name))
            {
                Assert.IsTrue(next.IsChannelOwner);
            }
        }
    }
}
#endif
