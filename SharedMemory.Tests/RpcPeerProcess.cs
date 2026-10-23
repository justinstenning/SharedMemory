#if NET
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SharedMemoryTests
{
    /// <summary>
    /// Starts SharedMemory.Tests.RpcPeer as a second process and sends it one command at a time. The commands are listed
    /// in the peer's Program.cs. On Linux it can start the peer in a POSIX session of its own, with util-linux's setsid,
    /// which execs the peer in the same process. Disposing kills the peer if it is still running.
    /// </summary>
    sealed class RpcPeerProcess : IDisposable
    {
        const int StartTimeout = 60000;
        const int ReplyTimeout = 15000;

        readonly Process process;
        readonly BlockingCollection<string> replies = new BlockingCollection<string>();
        readonly StringBuilder errors = new StringBuilder();

        RpcPeerProcess(Process process)
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
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
            process.BeginErrorReadLine();
        }

        /// <summary>
        /// Starts the peer, in a session of its own if <paramref name="ownSession"/> is set and this is Linux, and checks
        /// that it loaded the same SharedMemory.dll as the tests.
        /// </summary>
        public static RpcPeerProcess Start(bool ownSession)
        {
            string folder = Path.Combine(Path.GetDirectoryName(typeof(RpcPeerProcess).Assembly.Location), "rpcpeer");
            // The dotnet host of the runtime running the tests, so that the peer runs on it too
            string root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            string host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (!File.Exists(host))
                host = "dotnet";
            bool setsid = ownSession && OperatingSystem.IsLinux();
            var start = new ProcessStartInfo(setsid ? "setsid" : host)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            };
            if (setsid)
                start.ArgumentList.Add(host);
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(Path.Combine(folder, "SharedMemory.Tests.RpcPeer.dll"));

            var peer = new RpcPeerProcess(Process.Start(start));
            try
            {
                string ready = peer.Receive("start", StartTimeout);
                Assert.AreEqual("ready " + PeerProtocol.LibraryHash(), ready, "The peer must load the same SharedMemory.dll as the tests.");
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

        /// <summary>
        /// Sends a command that must succeed, and returns its result, or an empty string if it has none.
        /// </summary>
        public string Ok(string command, int timeout = ReplyTimeout)
        {
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
            string reply = Receive(command, timeout);
            if (reply != "ok" && !reply.StartsWith("ok ", StringComparison.Ordinal))
                Assert.Fail("RPC peer command '" + command + "' failed: " + reply);
            return reply.Length > 3 ? reply.Substring(3) : "";
        }

        /// <summary>
        /// Tells the peer to exit and waits until it has.
        /// </summary>
        public void Exit()
        {
            Ok("exit");
            WaitForExit();
        }

        /// <summary>
        /// Kills the peer and waits until it has exited, so that the OS has closed its handles.
        /// </summary>
        public void Kill()
        {
            process.Kill();
            WaitForExit();
        }

        /// <summary>
        /// Waits until the peer has exited, and returns its exit code.
        /// </summary>
        public int WaitForExit()
        {
            Assert.IsTrue(process.WaitForExit(StartTimeout), "The RPC peer did not exit.");
            return process.ExitCode;
        }

        string Receive(string command, int timeout)
        {
            string reply;
            if (replies.TryTake(out reply, timeout))
                return reply;
            string state = process.HasExited ? "exited with code " + process.ExitCode : "sent no reply within " + timeout + " ms";
            lock (errors)
                throw new TimeoutException("RPC peer " + state + " after '" + command + "'. Its stderr: " + errors);
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(StartTimeout);
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
            {
                // It exited between the check and the kill.
            }
            process.Dispose();
        }
    }
}
#endif
