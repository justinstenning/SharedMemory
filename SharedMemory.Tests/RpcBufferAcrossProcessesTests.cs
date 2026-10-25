#if NET && HAS_RPC_BUFFER
using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// RpcBuffer between two processes: one owner and one opener per channel, calls answered both ways, and a channel
    /// started again after its owner was killed. On Linux and macOS the other process runs in a POSIX session of its own,
    /// as a program started from another terminal does, and the tests also cover Global\ names and what a process that ends
    /// while it holds a channel leaves: files in /dev/shm on Linux, lock files and the objects they name on macOS.
    /// </summary>
    [TestClass]
    public class RpcBufferAcrossProcessesTests
    {
        /// <summary>
        /// What this process's RpcBuffers add to each byte of a payload they answer. The peer's add 1.
        /// </summary>
        const byte Increment = 2;

        static readonly byte[] Payload = { 10, 20, 30, 250 };

        [TestMethod]
        [DataRow("", "")]
        [DataRow("Local\\", "")]
        [DataRow("", "Local\\")]
        public void OwnerHere_OpenerInOtherProcess_CallsAnsweredBothWays(string herePrefix, string therePrefix)
        {
            string name = NewName();
            using (var owner = new RpcEndpoint(herePrefix + name, Increment))
            using (var peer = StartPeerInOwnSession())
            {
                Assert.IsTrue(owner.IsChannelOwner, "The first RpcBuffer on a channel is its owner.");
                Assert.AreEqual("opener", peer.Ok("open " + therePrefix + name));
                CallsAnsweredBothWays(owner, peer);
                peer.Ok("close");
                peer.Exit();
            }
        }

        [TestMethod]
        [DataRow("", "")]
        [DataRow("Local\\", "")]
        [DataRow("", "Local\\")]
        public void OwnerInOtherProcess_OpenerHere_CallsAnsweredBothWays(string therePrefix, string herePrefix)
        {
            string name = NewName();
            using (var peer = StartPeerInOwnSession())
            {
                Assert.AreEqual("owner", peer.Ok("open " + therePrefix + name));
                using (var opener = new RpcEndpoint(herePrefix + name, Increment))
                {
                    Assert.IsFalse(opener.IsChannelOwner, "A second RpcBuffer on a channel is its opener.");
                    CallsAnsweredBothWays(opener, peer);
                }
                peer.Ok("close");
                peer.Exit();
            }
        }

        /// <summary>
        /// On Linux a killed process's lock on the election mutex's file can outlast the process by some tens of milliseconds, until
        /// the kernel has torn down its mappings (about 25 ms when measured). An RpcBuffer made meanwhile becomes an opener and finds the
        /// dead owner's buffers abandoned (FileNotFoundException), so the test tries again for up to 5 s and prints how long it took.
        /// Windows closes the dead owner's handles before its process counts as ended.
        /// </summary>
        [TestMethod]
        public void OwnerKilled_NextRpcBufferBecomesOwnerAndIsAnswered()
        {
            string name = NewName();
            using (var peer = StartPeerInOwnSession())
            {
                Assert.AreEqual("owner", peer.Ok("open " + name));
                peer.Kill();
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int attempts = 0;
            RpcEndpoint created = null;
            while (created == null)
            {
                attempts++;
                try
                {
                    created = new RpcEndpoint(name, Increment);
                }
                catch (FileNotFoundException) when (watch.ElapsedMilliseconds < 5000)
                {
                    System.Threading.Thread.Sleep(10);
                }
            }
            Console.WriteLine("An RpcBuffer was made on the channel " + watch.ElapsedMilliseconds + " ms after the owner's process ended, at attempt " + attempts + ".");

            using (var owner = created)
            using (var peer = StartPeerInOwnSession())
            {
                Assert.IsTrue(owner.IsChannelOwner, "Once the owner's process has died, the next RpcBuffer on the channel is its owner.");
                Assert.AreEqual("opener", peer.Ok("open " + name));
                CallsAnsweredBothWays(owner, peer);
                peer.Ok("close");
                peer.Exit();
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void GlobalName_OtherSession_OneOwnerOneOpener()
        {
            string name = "Global\\" + NewName();
            using (var owner = new RpcEndpoint(name, Increment))
            using (var peer = StartPeerInOwnSession())
            {
                Assert.IsTrue(owner.IsChannelOwner, "The first RpcBuffer on a channel is its owner.");
                Assert.AreEqual("opener", peer.Ok("open " + name));
                CallsAnsweredBothWays(owner, peer);
                peer.Ok("close");
                peer.Exit();
            }
        }

        /// <summary>
        /// X and Global\X are different buffers on Linux and macOS (per user and machine-wide), so they are different channels with a
        /// distinct owner each. Not on Windows: the tests there run over SSH in session 0, where Local\ and Global\ are one namespace.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void PerUserAndGlobalNames_AreSeparateChannels()
        {
            string name = NewName();
            using (var perUser = new RpcEndpoint(name, Increment))
            using (var global = new RpcEndpoint("Global\\" + name, Increment))
            {
                Assert.IsTrue(perUser.IsChannelOwner, name + " has an owner of its own.");
                Assert.IsTrue(global.IsChannelOwner, "Global\\" + name + " has an owner of its own.");
            }
        }

        /// <summary>
        /// Local\Global\X names a per-user buffer whose name holds a backslash, not X's or Global\X's. With Local\ dropped its
        /// election would be Global\X's, which .NET folds into X's for one user, so it is refused instead, as Windows refuses a
        /// backslash after the prefix. X's channel goes on electing as before.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        public void LocalPrefixFollowedByGlobal_IsRefused()
        {
            string name = NewName();
            using (var owner = new RpcEndpoint(name, Increment))
            {
                Assert.ThrowsExactly<ArgumentException>(() => new RpcEndpoint("Local\\Global\\" + name, Increment));
                using (var opener = new RpcEndpoint(name, Increment))
                {
                    Assert.IsTrue(owner.IsChannelOwner);
                    Assert.IsFalse(opener.IsChannelOwner, name + " still has one owner.");
                }
            }
        }

        /// <summary>
        /// A process that holds both ends of a channel, so that nothing else holds its files, ends normally, by returning from
        /// Main: after disposing the RpcBuffers without waiting for them to finish, or without disposing them at all. Windows closes
        /// a process's handles when it exits; on Linux and macOS the library runs its close step at exit, and the files go: on
        /// macOS the lock files and the objects they name, which the test reads while the peer holds them.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [DataRow("dispose, then exit")]
        [DataRow("exit")]
        public void ProcessHoldingAChannelEnds_LeavesNoFiles(string ending)
        {
            string name = NewName();
            string[] objects = new string[0];
            using (var peer = RpcPeerProcess.Start(ownSession: false))
            {
                peer.Ok("pair " + name);
                Assert.AreEqual(6, FilesOf(name).Length, "While the peer holds the channel: its two buffers and their four event files. " + string.Join(", ", FilesOf(name)));
                if (OperatingSystem.IsMacOS())
                    objects = FilesOf(name).Select(UnixBufferTests.Files.ObjectNamedBy).ToArray();
                if (ending == "dispose, then exit")
                    peer.Ok("dispose");
                peer.Exit();
            }
            CollectionAssert.AreEqual(new string[0], FilesOf(name), "Files the peer left in /dev/shm");
            CollectionAssert.AreEqual(new string[0], objects.Where(UnixBufferTests.Files.ObjectExists).ToArray(), "Objects the peer left");
        }

        static void CallsAnsweredBothWays(RpcEndpoint here, RpcPeerProcess peer)
        {
            RpcResponse response = here.RemoteRequest(Payload, 5000);
            Assert.IsTrue(response.Success, "The other process answers a call from this one.");
            CollectionAssert.AreEqual(RpcEndpoint.Add(Payload, 1), response.Data, "The other process answered the call.");
            Assert.AreEqual(PeerProtocol.ToHex(RpcEndpoint.Add(Payload, Increment)), peer.Ok("call " + PeerProtocol.ToHex(Payload)),
                "This process answered the other process's call.");
        }

        static string NewName()
        {
            return "RpcAcross" + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Starts the peer, on Linux and macOS in a POSIX session of its own, checked.
        /// </summary>
        static RpcPeerProcess StartPeerInOwnSession()
        {
            var peer = RpcPeerProcess.Start(ownSession: true);
            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    Assert.AreEqual(peer.Id, SessionOf(peer.Id), "The peer leads a session of its own.");
                    Assert.AreNotEqual(SessionOf(Environment.ProcessId), SessionOf(peer.Id));
                }
                return peer;
            }
            catch
            {
                peer.Dispose();
                throw;
            }
        }

        static int SessionOf(int pid)
        {
            if (OperatingSystem.IsMacOS())
                return UnixBufferTests.Files.SessionOf(pid);
            string stat = File.ReadAllText("/proc/" + pid + "/stat");
            // pid (comm) state ppid pgrp session ...; comm may hold spaces and parentheses.
            return int.Parse(stat.Substring(stat.LastIndexOf(')') + 2).Split(' ')[3]);
        }

        /// <summary>
        /// The per-user files in /dev/shm of the channel <paramref name="name"/>, its buffers and their events, or on macOS their lock files.
        /// </summary>
        static string[] FilesOf(string name)
        {
            if (OperatingSystem.IsMacOS())
            {
                string folder = UnixBufferTests.Files.MacFolder + ".SharedMemory-uid" + UnixBufferTests.Files.EffectiveUserId() + "/";
                return Directory.Exists(folder) ? Directory.GetFiles(folder, name + "*").OrderBy(f => f, StringComparer.Ordinal).ToArray() : new string[0];
            }
            string line = File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal));
            string uid = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)[2];
            return Directory.GetFiles("/dev/shm/", "SharedMemory.u" + uid + "." + name + "*").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }
    }
}
#endif
