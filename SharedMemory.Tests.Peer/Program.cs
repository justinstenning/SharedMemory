using System;
using System.Globalization;
using SharedMemory;

namespace SharedMemoryTests.Peer
{
    /// <summary>
    /// The second process in the two-process tests (PeerProcess in SharedMemory.Tests). It reads one
    /// command per line on stdin and answers each with one line on stdout: "ok", "ok {result}", or
    /// "error {exception type}: {message}". It holds at most one buffer at a time.
    /// </summary>
    /// <remarks>
    /// <code>
    /// create eventfree|readwrite {name} {size}
    /// create circular {name} {node count} {node size}
    /// open eventfree|readwrite|circular {name}
    /// write {hex}                  at offset 0, or one circular node; replies with the bytes written
    /// read {count} [{timeout ms}]  at offset 0, or one circular node; replies with the bytes as hex
    /// lock read|write              BufferWithLocks.AcquireReadLock or AcquireWriteLock
    /// unlock read|write            BufferWithLocks.ReleaseReadLock or ReleaseWriteLock
    /// shuttingdown                 replies True or False
    /// close                        disposes the buffer
    /// exit
    /// </code>
    /// </remarks>
    static class Program
    {
        static SharedBuffer buffer;

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
                case "create":
                    if (buffer != null)
                        throw new InvalidOperationException("The peer already holds a buffer.");
                    buffer = Create(args[1], args[2], args);
                    return null;
                case "open":
                    if (buffer != null)
                        throw new InvalidOperationException("The peer already holds a buffer.");
                    buffer = Open(args[1], args[2]);
                    return null;
                case "write":
                    return Write(PeerProtocol.FromHex(args.Length > 1 ? args[1] : "")).ToString(CultureInfo.InvariantCulture);
                case "read":
                    return PeerProtocol.ToHex(Read(Number(args[1]), args.Length > 2 ? Number(args[2]) : 1000));
                case "lock":
                    if (!(args[1] == "read" ? Locks().AcquireReadLock(5000) : Locks().AcquireWriteLock(5000)))
                        throw new TimeoutException("The lock was not acquired within 5 s.");
                    return null;
                case "unlock":
                    if (args[1] == "read")
                        Locks().ReleaseReadLock();
                    else
                        Locks().ReleaseWriteLock();
                    return null;
                case "shuttingdown":
                    return Held().ShuttingDown.ToString();
                case "close":
                    Held().Dispose();
                    buffer = null;
                    return null;
                default:
                    throw new ArgumentException("Unknown command: " + args[0]);
            }
        }

        static SharedBuffer Create(string kind, string name, string[] args)
        {
            switch (kind)
            {
                case "eventfree": return new EventFreeBuffer(name, Number(args[3]));
                case "readwrite": return new BufferReadWrite(name, Number(args[3]));
                case "circular": return new CircularBuffer(name, Number(args[3]), Number(args[4]));
                default: throw new ArgumentException("Unknown buffer kind: " + kind);
            }
        }

        static SharedBuffer Open(string kind, string name)
        {
            switch (kind)
            {
                case "eventfree": return new EventFreeBuffer(name);
                case "readwrite": return new BufferReadWrite(name);
                case "circular": return new CircularBuffer(name);
                default: throw new ArgumentException("Unknown buffer kind: " + kind);
            }
        }

        static int Write(byte[] data)
        {
            SharedBuffer held = Held();
            if (held is CircularBuffer)
                return ((CircularBuffer)held).Write(data);
            if (held is BufferReadWrite)
                ((BufferReadWrite)held).Write(data);
            else
                ((EventFreeBuffer)held).WriteBytes(data);
            return data.Length;
        }

        static byte[] Read(int count, int timeout)
        {
            SharedBuffer held = Held();
            var data = new byte[count];
            if (held is CircularBuffer)
            {
                int read = ((CircularBuffer)held).Read(data, 0, timeout);
                Array.Resize(ref data, read);
            }
            else if (held is BufferReadWrite)
                ((BufferReadWrite)held).Read(data);
            else
                data = ((EventFreeBuffer)held).ReadBytes(count);
            return data;
        }

        static BufferWithLocks Locks()
        {
            var locks = Held() as BufferWithLocks;
            if (locks == null)
                throw new InvalidOperationException("The buffer has no locks.");
            return locks;
        }

        static SharedBuffer Held()
        {
            if (buffer == null)
                throw new InvalidOperationException("The peer holds no buffer.");
            return buffer;
        }

        static int Number(string text)
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }
    }
}
