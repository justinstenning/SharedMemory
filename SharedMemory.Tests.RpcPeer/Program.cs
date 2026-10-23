using System;
using System.Collections.Generic;
using System.Diagnostics;
using SharedMemory;

namespace SharedMemoryTests.RpcPeer
{
    /// <summary>
    /// The second process in the two-process RpcBuffer tests (RpcPeerProcess in SharedMemory.Tests). It reads one command
    /// per line on stdin and answers each with one line on stdout: "ok", "ok {result}", or "error {exception type}: {message}".
    /// Its RpcBuffers answer every call with the payload, 1 added to each byte.
    /// </summary>
    /// <remarks>
    /// <code>
    /// open {name}    an RpcBuffer on the channel; replies owner or opener
    /// pair {name}    an owner and an opener on the channel, both in this process, and one call from the owner to the opener
    /// call {hex}     a call from the RpcBuffer that open made, with a 5 s timeout; replies with the answer as hex
    /// close          disposes every RpcBuffer and waits up to 5 s for each to finish
    /// dispose        disposes every RpcBuffer, and returns at once
    /// exit           replies ok and returns from Main, leaving whatever it holds for the process exit
    /// </code>
    /// </remarks>
    static class Program
    {
        const byte Increment = 1;

        static readonly List<RpcEndpoint> endpoints = new List<RpcEndpoint>();
        static RpcEndpoint opened;

        static int Main()
        {
            Reply("ready " + PeerProtocol.LibraryHash());

            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                string[] args = line.Split(' ');
                if (args[0] == "exit")
                {
                    Reply("ok");
                    return 0;
                }

                try
                {
                    string result = Run(args);
                    Reply(result == null ? "ok" : "ok " + result);
                }
                catch (Exception e)
                {
                    Reply("error " + e.GetType().FullName + ": " + e.Message.Replace('\r', ' ').Replace('\n', ' '));
                }
            }
            return 0;
        }

        static void Reply(string line)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }

        static string Run(string[] args)
        {
            switch (args[0])
            {
                case "open":
                    if (opened != null)
                        throw new InvalidOperationException("The peer already has a channel open.");
                    opened = Add(new RpcEndpoint(args[1], Increment));
                    return opened.IsChannelOwner ? "owner" : "opener";
                case "pair":
                    RpcEndpoint owner = Add(new RpcEndpoint(args[1], Increment));
                    RpcEndpoint opener = Add(new RpcEndpoint(args[1], Increment));
                    if (!owner.IsChannelOwner || opener.IsChannelOwner)
                        throw new InvalidOperationException("The first RpcBuffer must be the owner and the second the opener.");
                    Check(owner.RemoteRequest(new byte[] { 1 }, 5000), new byte[] { 2 });
                    return null;
                case "call":
                    if (opened == null)
                        throw new InvalidOperationException("The peer has no channel open.");
                    byte[] payload = PeerProtocol.FromHex(args.Length > 1 ? args[1] : "");
                    RpcResponse response = opened.RemoteRequest(payload, 5000);
                    if (!response.Success)
                        throw new TimeoutException("The call was not answered within 5 s.");
                    return PeerProtocol.ToHex(response.Data ?? new byte[0]);
                case "close":
                    foreach (RpcEndpoint endpoint in endpoints)
                        endpoint.Dispose();
                    var watch = Stopwatch.StartNew();
                    foreach (RpcEndpoint endpoint in endpoints)
                        while (!endpoint.DisposeFinished)
                        {
                            if (watch.ElapsedMilliseconds > 5000)
                                throw new TimeoutException("An RpcBuffer had not finished disposing after 5 s.");
                            System.Threading.Thread.Sleep(10);
                        }
                    endpoints.Clear();
                    opened = null;
                    return null;
                case "dispose":
                    foreach (RpcEndpoint endpoint in endpoints)
                        endpoint.Dispose();
                    return null;
                default:
                    throw new ArgumentException("Unknown command: " + args[0]);
            }
        }

        static RpcEndpoint Add(RpcEndpoint endpoint)
        {
            endpoints.Add(endpoint);
            return endpoint;
        }

        static void Check(RpcResponse response, byte[] expected)
        {
            if (!response.Success || response.Data == null || PeerProtocol.ToHex(response.Data) != PeerProtocol.ToHex(expected))
                throw new InvalidOperationException("The call between the two RpcBuffers in this process was not answered as expected.");
        }
    }
}
