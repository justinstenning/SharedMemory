#if NET
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// Buffers on Linux: where their files are and who may use them, a /dev/shm without room, a buffer nobody disposed,
    /// processes in other sessions, and many processes creating, opening, closing, and dying at once.
    /// </summary>
    [TestClass]
    [SupportedOSPlatform("linux")]
    public class LinuxBufferTests
    {
        const string Folder = "/dev/shm/";
        const int Size = 1024;
        const int HeaderSize = 16;

        static readonly byte[] Data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        static readonly byte[] Reply = Enumerable.Range(101, 16).Select(i => (byte)i).ToArray();

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [DataRow("")]
        [DataRow("Global\\")]
        public void Create_FileInDevShm_PrivateToUser_RemovedOnClose(string scope)
        {
            string id = Guid.NewGuid().ToString();
            string path = scope == "" ? PerUserPath(id) : Folder + "SharedMemory.global." + id;

            using (new EventFreeBuffer(scope + id, Size))
            {
                Assert.IsTrue(File.Exists(path), path);
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.AreEqual(Size + HeaderSize, new FileInfo(path).Length);
            }

            Assert.IsFalse(File.Exists(path));
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [DataRow(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite)]
        [DataRow(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite)]
        public void PerUserFileOthersCanWrite_RefusedAndLeftAlone(UnixFileMode mode)
        {
            string name = Guid.NewGuid().ToString();
            string path = PerUserPath(name);
            byte[] planted = Enumerable.Repeat((byte)0x2A, Size + HeaderSize).ToArray();
            File.WriteAllBytes(path, planted);
            File.SetUnixFileMode(path, mode);
            try
            {
                var open = Assert.ThrowsExactly<UnauthorizedAccessException>(() => new EventFreeBuffer(name));
                StringAssert.Contains(open.Message, "group or others can write to it");
                var create = Assert.ThrowsExactly<UnauthorizedAccessException>(() => new EventFreeBuffer(name, Size));
                StringAssert.Contains(create.Message, "group or others can write to it");

                CollectionAssert.AreEqual(planted, File.ReadAllBytes(path));
                Assert.AreEqual(mode, File.GetUnixFileMode(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void GlobalFileOthersCanWrite_NotChecked_RemovedAsAbandoned()
        {
            string name = Guid.NewGuid().ToString();
            string path = Folder + "SharedMemory.global." + name;
            File.WriteAllBytes(path, new byte[Size + HeaderSize]);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
            try
            {
                Assert.ThrowsExactly<FileNotFoundException>(() => new EventFreeBuffer("Global\\" + name));

                Assert.IsFalse(File.Exists(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void Create_LargerThanDevShm_ThrowsIOExceptionAtOnceAndLeavesNoFile()
        {
            string name = Guid.NewGuid().ToString();
            long size = new DriveInfo("/dev/shm").TotalSize + 4096;
            string[] before = SharedMemoryFiles();

            var watch = Stopwatch.StartNew();
            var e = Assert.ThrowsExactly<IOException>(() => new EventFreeBuffer(name, size));
            watch.Stop();

            StringAssert.Contains(e.Message, name);
            CollectionAssert.AreEquivalent(before, SharedMemoryFiles());
            Assert.IsTrue(watch.ElapsedMilliseconds < 5000, "took " + watch.ElapsedMilliseconds + " ms");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void NeverDisposed_FileRemovedWhenCollected()
        {
            string name = Guid.NewGuid().ToString();

            CreateAndDrop(name);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.IsFalse(File.Exists(PerUserPath(name)));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CreateAndDrop(string name)
        {
            new EventFreeBuffer(name, Size);
            Assert.IsTrue(File.Exists(PerUserPath(name)));
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void OwnerKilled_OpenThrowsFileNotFoundAndRemovesFile()
        {
            string name = Guid.NewGuid().ToString();
            using (var peer = PeerProcess.Start())
            {
                peer.Ok("create eventfree " + name + " " + Size);
                peer.Kill();
            }
            Assert.IsTrue(File.Exists(PerUserPath(name)), "The killed owner's file stays until the name is next used.");

            Assert.ThrowsExactly<FileNotFoundException>(() => new EventFreeBuffer(name));

            Assert.IsFalse(File.Exists(PerUserPath(name)));
        }

        /// <summary>
        /// A killed process's lock must be gone by the time it has been reaped. When the library mapped the file through
        /// the descriptor it locked, the lock outlived a killed .NET process by milliseconds, and a create straight after
        /// found the name in use.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void OwnerKilled_CreateStraightAfter_Succeeds()
        {
            for (int i = 0; i < 20; i++)
            {
                string name = Guid.NewGuid().ToString();
                using (var peer = PeerProcess.Start())
                {
                    peer.Ok("create eventfree " + name + " " + Size);
                    peer.Ok("write " + PeerProtocol.ToHex(Data));
                    peer.Kill();
                }

                using (var owner = new EventFreeBuffer(name, Size))
                    CollectionAssert.AreEqual(new byte[Data.Length], owner.ReadBytes(Data.Length), "attempt " + i);
            }
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public void PerUserAndGlobalNames_AreSeparate()
        {
            string name = Guid.NewGuid().ToString();
            using (var perUser = new EventFreeBuffer(name, Size))
            using (var global = new EventFreeBuffer("Global\\" + name, Size))
            using (var local = new EventFreeBuffer("Local\\" + name))
            {
                perUser.WriteBytes(Data);
                global.WriteBytes(Reply);

                CollectionAssert.AreEqual(Data, local.ReadBytes(Data.Length));
                CollectionAssert.AreEqual(Reply, global.ReadBytes(Reply.Length));
            }
        }

        /// <summary>
        /// Every terminal is its own POSIX session. A process in another session, started with setsid, opens a buffer this
        /// process created, by a per-user name, a <c>Local\</c> one, or a <c>Global\</c> one.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [DataRow("", "")]
        [DataRow("", "Local\\")]
        [DataRow("Local\\", "")]
        [DataRow("Global\\", "Global\\")]
        public void OtherSession_OpensBufferThisProcessCreated(string createScope, string openScope)
        {
            string id = Guid.NewGuid().ToString();
            using (var buffer = new EventFreeBuffer(createScope + id, Size))
            using (var peer = SessionPeer.Start())
            {
                int session = SessionOf(Environment.ProcessId);
                Assert.AreEqual(peer.Id, SessionOf(peer.Id), "The peer leads a session of its own.");
                Assert.AreNotEqual(session, SessionOf(peer.Id));

                buffer.WriteBytes(Data);
                Assert.AreEqual("ok", peer.Send("open eventfree " + openScope + id));
                Assert.AreEqual("ok " + PeerProtocol.ToHex(Data), peer.Send("read " + Data.Length));
                Assert.AreEqual("ok " + Reply.Length, peer.Send("write " + PeerProtocol.ToHex(Reply)));

                CollectionAssert.AreEqual(Reply, buffer.ReadBytes(Reply.Length));
                Assert.AreEqual("ok", peer.Send("close"));
            }
        }

        /// <summary>
        /// Several processes create, open, read, close, and are killed, some in the middle of a command, on a few names for
        /// 60 s. The only failures are IOException and FileNotFoundException; the lifetime rules hold throughout; and at the
        /// end nothing is left in /dev/shm but temporary files from kills between create and publish, which the library
        /// leaves until logout or reboot rather than sweeping for them; the test removes and reports them.
        /// </summary>
        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        [TestCategory("Stress")]
        public void ManyProcesses_CreateOpenCloseAndDie_LifetimeHolds()
        {
            var history = new StressHistory("stress-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "-", 3);
            string[] temporaryBefore = TemporaryFiles();
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);

            var drivers = Enumerable.Range(0, 4).Select(i => new Thread(() => history.Drive(i, deadline))).ToArray();
            foreach (var driver in drivers)
                driver.Start();
            foreach (var driver in drivers)
                driver.Join();

            // Every peer has exited, so every name's file, if any, was left by a killed holder: opening it removes it.
            foreach (string name in history.Names)
            {
                try
                {
                    new EventFreeBuffer(name).Dispose();
                    history.Problem("opened " + name + " after every peer had exited");
                }
                catch (FileNotFoundException)
                {
                }
                Assert.IsFalse(File.Exists(PerUserPath(name)), name);
            }
            string[] leftovers = TemporaryFiles().Except(temporaryBefore).ToArray();
            foreach (string file in leftovers)
                File.Delete(file);

            Console.WriteLine(history.Summary());
            Console.WriteLine("temporary files left by kills between create and publish: " + leftovers.Length);
            Assert.AreEqual(0, history.Problems.Count, string.Join(Environment.NewLine, history.Problems.Take(20)));
            Assert.IsTrue(history.Count("create ok") > 0 && history.Count("open ok") > 0 && history.Count("kill") > 0, history.Summary());
        }

        static string PerUserPath(string name)
        {
            return Folder + "SharedMemory.u" + EffectiveUserId() + "." + name;
        }

        static uint EffectiveUserId()
        {
            string line = File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal));
            return uint.Parse(line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)[2]);
        }

        static int SessionOf(int pid)
        {
            string stat = File.ReadAllText("/proc/" + pid + "/stat");
            // pid (comm) state ppid pgrp session ...; comm may hold spaces and parentheses.
            return int.Parse(stat.Substring(stat.LastIndexOf(')') + 2).Split(' ')[3]);
        }

        static string[] SharedMemoryFiles()
        {
            return Directory.GetFiles(Folder).Where(f => Path.GetFileName(f).StartsWith("SharedMemory.", StringComparison.Ordinal)
                || Path.GetFileName(f).StartsWith(".SharedMemory.tmp.", StringComparison.Ordinal)).ToArray();
        }

        static string[] TemporaryFiles()
        {
            return Directory.GetFiles(Folder, ".SharedMemory.tmp.*");
        }

        /// <summary>
        /// The peer (SharedMemory.Tests.Peer) started with setsid, so that it runs in a POSIX session of its own.
        /// </summary>
        sealed class SessionPeer : IDisposable
        {
            readonly Process process;
            readonly BlockingCollection<string> replies = new BlockingCollection<string>();

            SessionPeer(Process process)
            {
                this.process = process;
                var reader = new Thread(() =>
                {
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                        replies.Add(line);
                    replies.CompleteAdding();
                });
                reader.IsBackground = true;
                reader.Start();
                process.ErrorDataReceived += (sender, e) => { };
                process.BeginErrorReadLine();
            }

            public static SessionPeer Start()
            {
                string folder = Path.Combine(Path.GetDirectoryName(typeof(SessionPeer).Assembly.Location), "peer");
                string root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
                string host = Path.Combine(root, "dotnet");
                var start = new ProcessStartInfo("setsid")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = folder,
                };
                start.ArgumentList.Add(File.Exists(host) ? host : "dotnet");
                start.ArgumentList.Add("exec");
                start.ArgumentList.Add(Path.Combine(folder, "SharedMemory.Tests.Peer.dll"));

                var peer = new SessionPeer(Process.Start(start));
                try
                {
                    Assert.AreEqual("ready " + PeerProtocol.LibraryHash(), peer.Receive(60000));
                    return peer;
                }
                catch
                {
                    peer.Dispose();
                    throw;
                }
            }

            public int Id
            {
                get { return process.Id; }
            }

            public string Send(string command)
            {
                process.StandardInput.WriteLine(command);
                process.StandardInput.Flush();
                return Receive(15000);
            }

            string Receive(int timeout)
            {
                string reply;
                if (!replies.TryTake(out reply, timeout))
                    throw new TimeoutException("The peer sent no reply within " + timeout + " ms.");
                return reply;
            }

            public void Dispose()
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(60000);
                }
                process.Dispose();
            }
        }

        /// <summary>
        /// The stress test's record of who could hold which name when, and its checks of the lifetime rules against it.
        /// </summary>
        /// <remarks>
        /// <para>Each create or open a peer sends opens an interval: the peer may hold the name from when the command is sent
        /// until its close is answered or the peer has been killed, and certainly holds it from the success reply until the
        /// close or kill is sent. The checks, each on one name:</para>
        /// <list type="bullet">
        /// <item>A create that succeeds: no other peer certainly held the name from before the create was sent until after it was answered.</item>
        /// <item>An open that succeeds, or a create refused as in use: some other peer may have held the name meanwhile.</item>
        /// <item>A file outlives its creator while others hold it, but a create succeeds only when no file is held, so one file at most is live per
        /// name. What an opener reads is zeros (its creator was killed before writing, or has not written yet) or the token of the file it opened:
        /// the latest create answered before the open was sent, or a create sent before the open was answered and answered after it was sent.
        /// A creator reads its own token.</item>
        /// </list>
        /// </remarks>
        sealed class StressHistory
        {
            const int Size = 4096;
            const long Open = long.MaxValue;

            readonly object gate = new object();
            readonly List<Hold> holds = new List<Hold>();
            readonly ConcurrentDictionary<string, int> counts = new ConcurrentDictionary<string, int>();
            int nextPeer;

            public StressHistory(string prefix, int names)
            {
                Names = Enumerable.Range(0, names).Select(i => prefix + i).ToArray();
            }

            public string[] Names { get; private set; }

            public ConcurrentQueue<string> Problems { get; } = new ConcurrentQueue<string>();

            public void Problem(string problem)
            {
                Problems.Enqueue(problem);
            }

            public int Count(string what)
            {
                int n;
                return counts.TryGetValue(what, out n) ? n : 0;
            }

            public string Summary()
            {
                return string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + " " + c.Value));
            }

            void Counted(string what)
            {
                counts.AddOrUpdate(what, 1, (k, n) => n + 1);
            }

            sealed class Hold
            {
                public string Name;
                public int Peer;
                public bool Create;
                public long PossibleStart;
                public long CertainStart = Open;
                public long CertainEnd = Open;
                public long PossibleEnd = Open;
                public byte[] Token;
            }

            public void Drive(int driver, DateTime deadline)
            {
                var random = new Random(driver);
                PeerProcess peer = null;
                int peerId = 0;
                Hold held = null;
                try
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        if (peer == null)
                        {
                            peer = PeerProcess.Start();
                            peerId = Interlocked.Increment(ref nextPeer);
                        }

                        int roll = random.Next(100);
                        if (held != null)
                        {
                            if (roll < 45)
                                Read(peer, held);
                            else if (roll < 90)
                            {
                                End(held, certainOnly: true);
                                Reply(peer.Send("close"), "close");
                                End(held, certainOnly: false);
                                held = null;
                            }
                            else
                            {
                                End(held, certainOnly: true);
                                peer.Kill();
                                End(held, certainOnly: false);
                                Counted("kill");
                                peer.Dispose();
                                peer = null;
                                held = null;
                            }
                            continue;
                        }

                        string name = Names[random.Next(Names.Length)];
                        bool create = random.Next(2) == 0;
                        string command = create ? "create eventfree " + name + " " + Size : "open eventfree " + name;
                        if (roll < 10)
                        {
                            // Kill the peer partway through the command: it may or may not have completed it.
                            Hold uncertain = Begin(name, peerId, create);
                            var sent = Task.Run(() => { try { peer.Send(command); } catch (Exception e) when (e is TimeoutException || e is IOException) { } });
                            Thread.Sleep(random.Next(4));
                            peer.Kill();
                            sent.Wait();
                            End(uncertain, certainOnly: false);
                            Counted("kill during " + (create ? "create" : "open"));
                            peer.Dispose();
                            peer = null;
                            continue;
                        }

                        Hold hold = Begin(name, peerId, create);
                        string reply = peer.Send(command);
                        long answered = Stopwatch.GetTimestamp();
                        string what = create ? "create" : "open";
                        if (reply == "ok")
                        {
                            Counted(what + " ok");
                            Succeeded(hold, create, answered);
                            held = hold;
                            if (create)
                            {
                                var token = new byte[16];
                                random.NextBytes(token);
                                lock (gate)
                                    hold.Token = token;
                                Reply(peer.Send("write " + PeerProtocol.ToHex(token)), "write");
                            }
                            else
                                Read(peer, held);
                        }
                        else
                        {
                            Reply(reply, what);
                            Refused(hold, create, reply, answered);
                        }
                    }
                }
                catch (Exception e)
                {
                    Problem("driver " + driver + ": " + e.GetType().FullName + ": " + e.Message);
                }
                finally
                {
                    if (peer != null)
                    {
                        if (held != null)
                        {
                            End(held, certainOnly: true);
                            try { peer.Send("close"); } catch (Exception e) when (e is TimeoutException || e is IOException) { }
                            End(held, certainOnly: false);
                        }
                        peer.Dispose();
                    }
                }
            }

            Hold Begin(string name, int peer, bool create)
            {
                var hold = new Hold { Name = name, Peer = peer, Create = create, PossibleStart = Stopwatch.GetTimestamp() };
                lock (gate)
                    holds.Add(hold);
                return hold;
            }

            void End(Hold hold, bool certainOnly)
            {
                long now = Stopwatch.GetTimestamp();
                lock (gate)
                {
                    if (hold.CertainEnd == Open)
                        hold.CertainEnd = now;
                    if (!certainOnly)
                        hold.PossibleEnd = now;
                }
            }

            void Succeeded(Hold hold, bool create, long answered)
            {
                lock (gate)
                {
                    if (create)
                    {
                        Hold other = holds.FirstOrDefault(h => h != hold && h.Name == hold.Name
                            && h.CertainStart < hold.PossibleStart && h.CertainEnd > answered);
                        if (other != null)
                            Problem("peer " + hold.Peer + " created " + hold.Name + " while peer " + other.Peer + " held it");
                    }
                    else if (!PossiblyHeld(hold, answered))
                        Problem("peer " + hold.Peer + " opened " + hold.Name + " while no other peer could hold it");
                    hold.CertainStart = answered;
                }
            }

            void Refused(Hold hold, bool create, string reply, long answered)
            {
                lock (gate)
                {
                    hold.CertainEnd = hold.PossibleEnd = answered;
                    if (create && reply.StartsWith("error System.IO.IOException:", StringComparison.Ordinal) && !PossiblyHeld(hold, answered))
                        Problem("peer " + hold.Peer + " was refused " + hold.Name + " as in use while no other peer could hold it: " + reply);
                }
            }

            bool PossiblyHeld(Hold hold, long until)
            {
                return holds.Any(h => h != hold && h.Name == hold.Name && h.PossibleStart < until && h.PossibleEnd > hold.PossibleStart);
            }

            void Read(PeerProcess peer, Hold hold)
            {
                string reply = peer.Send("read 16");
                long answered = Stopwatch.GetTimestamp();
                Reply(reply, "read");
                if (!reply.StartsWith("ok ", StringComparison.Ordinal))
                    return;
                byte[] read = PeerProtocol.FromHex(reply.Substring(3));
                lock (gate)
                {
                    bool known;
                    if (read.All(b => b == 0))
                        known = true;
                    else if (hold.Create)
                        known = hold.Token != null && hold.Token.SequenceEqual(read);
                    else
                    {
                        long sent = hold.PossibleStart, opened = hold.CertainStart;
                        var created = holds.Where(h => h.Name == hold.Name && h.Create && h.CertainStart != Open).ToList();
                        Hold latest = created.Where(h => h.CertainStart < sent).OrderBy(h => h.CertainStart).LastOrDefault();
                        known = created.Where(h => h == latest || (h.PossibleStart < opened && h.CertainStart >= sent))
                            .Any(h => h.Token != null && h.Token.SequenceEqual(read));
                    }
                    if (!known)
                        Problem("peer " + hold.Peer + " read " + reply.Substring(3) + " from " + hold.Name + ", not the token of a file it could have opened");
                }
            }

            void Reply(string reply, string what)
            {
                if (reply == "ok" || reply.StartsWith("ok ", StringComparison.Ordinal))
                    return;
                string type = reply.StartsWith("error ", StringComparison.Ordinal) ? reply.Substring(6, Math.Max(0, reply.IndexOf(':') - 6)) : reply;
                Counted(what + " " + type);
                if (type != "System.IO.IOException" && type != "System.IO.FileNotFoundException")
                    Problem(what + ": " + reply);
                else if (reply.Contains("kept changing"))
                    Problem(what + ": " + reply);
            }
        }
    }
}
#endif
