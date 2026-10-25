#if HAS_RPC_BUFFER
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// RpcBuffer channels whose names differ only in case, and channels with long names, each elect one owner of their own,
    /// on every OS. On macOS .NET keeps its named mutexes on a volume that folds case, where the channels TEST and test would
    /// otherwise share one election while their buffers are separate. An owner disposed on another thread leaves the
    /// channel to the next RpcBuffer.
    /// </summary>
    [TestClass]
    public class RpcElectionNameTests
    {
        static readonly byte[] Payload = { 10, 20, 30, 250 };

        [TestMethod]
        public void ChannelsDifferingOnlyInCase_ElectSeparately()
        {
            string lower = "rpccase" + Guid.NewGuid().ToString("N");
            string upper = lower.ToUpperInvariant();
            using (var lowerOwner = new RpcEndpoint(lower, 1))
            using (var upperOwner = new RpcEndpoint(upper, 2))
            {
                Assert.IsTrue(lowerOwner.IsChannelOwner, lower + " has an owner of its own.");
                Assert.IsTrue(upperOwner.IsChannelOwner, upper + " has an owner of its own.");
                using (var lowerOpener = new RpcEndpoint(lower, 3))
                using (var upperOpener = new RpcEndpoint(upper, 4))
                {
                    Assert.IsFalse(lowerOpener.IsChannelOwner, "A second RpcBuffer on " + lower + " is its opener.");
                    Assert.IsFalse(upperOpener.IsChannelOwner, "A second RpcBuffer on " + upper + " is its opener.");
                    AssertAnsweredBy(lowerOpener, 1, lower);
                    AssertAnsweredBy(upperOpener, 2, upper);
                }
            }
        }

        /// <summary>
        /// 210 characters: within Windows' limit for the channel's longest object name (its events', 39 characters more), past
        /// what fits before the macOS election mutex's suffix in .NET's 255 bytes.
        /// </summary>
        [TestMethod]
        public void LongChannelName_OneOwnerOneOpener()
        {
            string name = ("rpclong" + Guid.NewGuid().ToString("N")).PadRight(210, 'x');
            using (var owner = new RpcEndpoint(name, 1))
            using (var opener = new RpcEndpoint(name, 2))
            {
                Assert.IsTrue(owner.IsChannelOwner, "The first RpcBuffer on a channel is its owner.");
                Assert.IsFalse(opener.IsChannelOwner, "A second RpcBuffer on a channel is its opener.");
                AssertAnsweredBy(opener, 1, "the owner");
            }
        }

        /// <summary>
        /// An owner disposed on a thread other than the one that made it leaves the channel to the next RpcBuffer. On Linux
        /// and macOS a named mutex its thread holds keeps its name until that thread lets it go, even once another thread has
        /// closed it, so there the election mutex is made without being taken.
        /// </summary>
        [TestMethod]
        public void OwnerDisposedOnAnotherThread_NextIsOwner()
        {
            string name = "rpcthread" + Guid.NewGuid().ToString("N");
            var first = new RpcEndpoint(name, 1);
            Assert.IsTrue(first.IsChannelOwner, "The first RpcBuffer on a channel is its owner.");
            Assert.IsTrue(Task.Run(() => first.Dispose()).Wait(5000), "Dispose on another thread should return.");
            using (var owner = new RpcEndpoint(name, 2))
            using (var opener = new RpcEndpoint(name, 3))
            {
                Assert.IsTrue(owner.IsChannelOwner, "Once the owner is disposed on another thread, the next RpcBuffer on the channel is its owner.");
                AssertAnsweredBy(opener, 2, "the new owner");
            }
        }

        static void AssertAnsweredBy(RpcEndpoint caller, byte increment, string answerer)
        {
            RpcResponse response = caller.RemoteRequest(Payload, 5000);
            Assert.IsTrue(response.Success, "The call should be answered by " + answerer + ".");
            CollectionAssert.AreEqual(RpcEndpoint.Add(Payload, increment), response.Data, "The call should be answered by " + answerer + ".");
        }
    }
}
#endif
