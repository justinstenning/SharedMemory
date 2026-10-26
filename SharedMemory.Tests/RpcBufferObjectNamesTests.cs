#if HAS_RPC_BUFFER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The named objects an RpcBuffer finds its other end by, from 2.4.0. A process on another package version looks for the
    /// same names, so these strings are a wire format: changing one stops this version pairing with the versions before it.
    /// They are checked through the operating system, character for character, whatever the code calls the two ends.
    /// </summary>
    [TestClass]
    public class RpcBufferObjectNamesTests
    {
        const string ElectionMutex = "SharedMemory_OwnerMutex";
        const string OwnerReads = "_Owner_SharedMemory_MMF";
        const string OwnerWrites = "_Opener_SharedMemory_MMF";

        sealed class Probe : RpcBuffer
        {
            public Probe(string name) : base(name) { }
            public bool OwnsBuffers { get { return WriteBuffer.IsOwnerOfSharedMemory && ReadBuffer.IsOwnerOfSharedMemory; } }
            public string WriteName { get { return WriteBuffer.Name; } }
            public string ReadName { get { return ReadBuffer.Name; } }
        }

        [DllImport("libc")]
        static extern uint getuid();

        static bool IsLinux { get { return RuntimeInformation.IsOSPlatform(OSPlatform.Linux); } }

#if NET
        [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
#endif
        static bool IsWindows { get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); } }

        [TestMethod]
        public void FirstOnChannel_CreatesTheObjectsOlderVersionsOpen()
        {
            if (!IsWindows && !IsLinux)
                Assert.Inconclusive("Windows and Linux only: macOS names its objects by hash.");
            string name = "RpcObjectNames" + Guid.NewGuid().ToString("N");
            using (var owner = new Probe(name))
            {
                Assert.IsTrue(owner.OwnsBuffers, "The first RpcBuffer on a channel creates its buffers.");
                Assert.AreEqual(name + OwnerReads, owner.ReadName);
                Assert.AreEqual(name + OwnerWrites, owner.WriteName);
                // Looked for through the operating system, so that nothing here depends on the library's names.
                foreach (string buffer in new[] { name + OwnerReads, name + OwnerWrites })
                {
                    foreach (string suffix in new[] { "", "_evt_dataexists", "_evt_nodeavail" })
                    {
                        if (IsWindows && suffix.Length == 0)
                            using (MemoryMappedFile.OpenExisting(buffer)) { }
                        else if (IsWindows)
                            using (EventWaitHandle.OpenExisting(buffer + suffix)) { }
                        else
                            Assert.IsTrue(File.Exists("/dev/shm/SharedMemory.u" + getuid() + "." + buffer + suffix), buffer + suffix);
                    }
                }
                if (IsWindows)
                    using (Mutex.OpenExisting(name + ElectionMutex)) { }
#if NET10_0_OR_GREATER
                else
                    // Through .NET, with the options RpcBuffer elects with on Linux. .NET picks the folder that holds the mutex, and
                    // moved it between 10.0.12 and 10.0.13.
                    using (Mutex.OpenExisting(name + ElectionMutex, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false })) { }
#endif
            }
        }

        [TestMethod]
        public void SecondOnChannel_OpensTheObjectsAnOlderVersionCreated()
        {
            if (!IsWindows && !IsLinux)
                Assert.Inconclusive("Windows and Linux only: macOS names its objects by hash.");
            // Stands in for an RpcBuffer of an older version that got there first: it holds the election mutex, made as
            // that version makes it, and created both buffers.
            string name = "RpcObjectNames" + Guid.NewGuid().ToString("N");
            bool createdNew;
#if NET10_0_OR_GREATER
            var election = IsWindows
                ? new Mutex(true, name + ElectionMutex, out createdNew)
                : new Mutex(false, name + ElectionMutex, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out createdNew);
#else
            var election = new Mutex(true, name + ElectionMutex, out createdNew);
#endif
            using (election)
            {
                Assert.IsTrue(createdNew);
                try
                {
                    using (var toOpener = new CircularBuffer(name + OwnerWrites, 10, 4096))
                    using (var toOwner = new CircularBuffer(name + OwnerReads, 10, 4096))
                    using (var opener = new Probe(name))
                    {
                        Assert.IsFalse(opener.OwnsBuffers, "A second RpcBuffer on a channel opens the buffers the first created.");
                        Assert.AreEqual(name + OwnerReads, opener.WriteName);
                        Assert.AreEqual(name + OwnerWrites, opener.ReadName);
                    }
                }
                finally
                {
                    if (IsWindows)
                        election.ReleaseMutex();
                }
            }
        }
    }
}
#endif
