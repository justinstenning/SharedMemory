using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SharedMemoryTests
{
    /// <summary>
    /// Starts SharedMemory.Tests.Peer as a second process and sends it one command at a time. The
    /// commands are listed in the peer's Program.cs. Disposing kills the peer if it is still running.
    /// </summary>
    sealed class PeerProcess : IDisposable
    {
        const int StartTimeout = 60000;
        const int ReplyTimeout = 15000;

        readonly Process process;
        readonly BlockingCollection<string> replies = new BlockingCollection<string>();
        readonly StringBuilder errors = new StringBuilder();

        PeerProcess(Process process)
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
        /// Starts the peer and checks that it loaded the same SharedMemory.dll as the tests.
        /// </summary>
        public static PeerProcess Start()
        {
            string folder = Path.Combine(Path.GetDirectoryName(typeof(PeerProcess).Assembly.Location), "peer");
            var start = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            };
#if NETFRAMEWORK
            start.FileName = Path.Combine(folder, "SharedMemory.Tests.Peer.exe");
#else
            // The dotnet host of the runtime running the tests, so that the peer runs on it too
            string root = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            string host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            start.FileName = File.Exists(host) ? host : "dotnet";
            start.Arguments = "exec \"" + Path.Combine(folder, "SharedMemory.Tests.Peer.dll") + "\"";
#endif

            var peer = new PeerProcess(Process.Start(start));
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

        /// <summary>
        /// Sends a command and returns the peer's reply.
        /// </summary>
        public string Send(string command, int timeout = ReplyTimeout)
        {
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
            return Receive(command, timeout);
        }

        /// <summary>
        /// Sends a command that must succeed, and returns its result, or an empty string if it has none.
        /// </summary>
        public string Ok(string command, int timeout = ReplyTimeout)
        {
            string reply = Send(command, timeout);
            if (reply != "ok" && !reply.StartsWith("ok ", StringComparison.Ordinal))
                Assert.Fail("Peer command '" + command + "' failed: " + reply);
            return reply.Length > 3 ? reply.Substring(3) : "";
        }

        /// <summary>
        /// Kills the peer and waits until it has exited, so that the OS has closed its handles.
        /// </summary>
        public void Kill()
        {
            process.Kill();
            Assert.IsTrue(process.WaitForExit(StartTimeout), "The peer did not exit after being killed.");
        }

        /// <summary>
        /// Tells the peer to exit and waits until it has.
        /// </summary>
        public void Exit()
        {
            Ok("exit");
            Assert.IsTrue(process.WaitForExit(StartTimeout), "The peer did not exit.");
        }

        string Receive(string command, int timeout)
        {
            string reply;
            if (replies.TryTake(out reply, timeout))
                return reply;
            string state = process.HasExited ? "exited with code " + process.ExitCode : "sent no reply within " + timeout + " ms";
            lock (errors)
                throw new TimeoutException("Peer " + state + " after '" + command + "'. Its stderr: " + errors);
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
