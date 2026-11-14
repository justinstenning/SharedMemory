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
    /// create array {name} {length}  a SharedArray&lt;int&gt;
    /// open eventfree|readwrite|circular|array {name}
    /// createoropen readwrite {name} {size} [after {path}]
    /// createoropen circular {name} {node count} {node size} [after {path}]
    /// createoropen array {name} {length} [after {path}]
    ///                              with SharedMemoryOpenMode.CreateOrOpen; with after, it first writes {path}.waiting.{pid},
    ///                              then waits for the file {path};
    ///                              replies created={CreatedSharedMemory} owner={IsOwnerOfSharedMemory}
    /// info {name}                  SharedBuffer.TryGetInfo: replies False, or the info's properties as name=value
    /// write {hex}                  at offset 0 (an array's first elements, whole ints), or one circular node; replies with the
    ///                              bytes written
    /// read {count} [{timeout ms}]  at offset 0 (an array's first elements, whole ints), or one circular node; replies with the
    ///                              bytes as hex
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
                case "createoropen":
                    if (buffer != null)
                        throw new InvalidOperationException("The peer already holds a buffer.");
                    buffer = CreateOrOpen(args[1], args[2], args);
                    return "created=" + buffer.CreatedSharedMemory + " owner=" + buffer.IsOwnerOfSharedMemory;
                case "info":
                    return Info(args[1]);
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
                case "array": return new SharedArray<int>(name, Number(args[3]));
                default: throw new ArgumentException("Unknown buffer kind: " + kind);
            }
        }

        static SharedBuffer CreateOrOpen(string kind, string name, string[] args)
        {
            int sizes = kind == "circular" ? 2 : 1;
            if (args.Length == 3 + sizes + 2 && args[3 + sizes] == "after")
            {
                // The start barrier of a race: every peer says it is waiting, with a file of its own beside the start file, then
                // waits for the same start file, and creates or opens at once
                string path = args[4 + sizes];
                System.IO.File.WriteAllText(path + ".waiting." + System.Diagnostics.Process.GetCurrentProcess().Id, "");
                if (!System.Threading.SpinWait.SpinUntil(() => System.IO.File.Exists(path), 60000))
                    throw new TimeoutException("The start file did not appear within 60 s.");
            }
            else if (args.Length != 3 + sizes)
                throw new ArgumentException("Expected " + sizes + " sizes, and optionally after {path}.");

            var options = kind == "circular" ? new CircularBufferOptions() : new SharedMemoryOptions();
            options.OpenMode = SharedMemoryOpenMode.CreateOrOpen;
            switch (kind)
            {
                case "readwrite": return new BufferReadWrite(name, Number(args[3]), options);
                case "circular": return new CircularBuffer(name, Number(args[3]), Number(args[4]), (CircularBufferOptions)options);
                case "array": return new SharedArray<int>(name, Number(args[3]), options);
                default: throw new ArgumentException("Unknown buffer kind: " + kind);
            }
        }

        static string Info(string name)
        {
            SharedBufferInfo info;
            if (!SharedBuffer.TryGetInfo(name, out info))
                return "False";
            return "Kind=" + info.Kind + " LayoutVersion=" + info.LayoutVersion + " IsSupported=" + info.IsSupported + " IsReady=" + info.IsReady +
                " ShuttingDown=" + info.ShuttingDown + " BufferSize=" + info.BufferSize + " NodeCount=" + info.NodeCount + " NodeBufferSize=" + info.NodeBufferSize;
        }

        static SharedBuffer Open(string kind, string name)
        {
            switch (kind)
            {
                case "eventfree": return new EventFreeBuffer(name);
                case "readwrite": return new BufferReadWrite(name);
                case "circular": return new CircularBuffer(name);
                case "array": return new SharedArray<int>(name);
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
            else if (held is SharedArray<int>)
            {
                var array = (SharedArray<int>)held;
                for (int i = 0; i < data.Length / sizeof(int); i++)
                    array[i] = BitConverter.ToInt32(data, i * sizeof(int));
            }
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
            else if (held is SharedArray<int>)
            {
                var array = (SharedArray<int>)held;
                for (int i = 0; i < count / sizeof(int); i++)
                    BitConverter.GetBytes(array[i]).CopyTo(data, i * sizeof(int));
            }
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
