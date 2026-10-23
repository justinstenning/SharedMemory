SharedMemory
============

C# shared memory classes for sharing data between processes (Array, Buffer, Circular Buffer and RPC)

[![CI](https://github.com/justinstenning/SharedMemory/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/justinstenning/SharedMemory/actions/workflows/ci.yml)

About
-----

The SharedMemory class library provides a set of C# classes that utilise memory mapped files for fast low-level inter-process communication (IPC). Originally only for sharing data between processes, but now also with a simple RPC implementation.

The library uses the .NET MemoryMappedFile class in .NET 4.0+, and implements its own wrapper class for .NET 3.5.

Classes
-------

 * `SharedMemory.SharedBuffer` - an abstract base class that wraps a memory mapped file, exposing the read/write operations and implementing a small header to allow clients to open the shared buffer without knowing the size beforehand.
 * `SharedMemory.BufferWithLocks` - an abstract class that extends SharedMemory.SharedBuffer with a read gate and a write gate, two named EventWaitHandles that `AcquireReadLock`, `AcquireWriteLock` and their `Release` methods close and open. They are gates, not exclusive locks: see [Read and write locks](#read-and-write-locks).
 * `SharedMemory.SharedArray` - a simple generic array implementation utilising a shared memory buffer. Inherits from SharedMemory.BufferWithLocks, so its reads and writes wait at its gates.
 * `SharedMemory.BufferReadWrite` - provides read/write access to a shared memory buffer, with various overloads to support reading and writing structures, copying to and from IntPtr and so on. Inherits from SharedMemory.BufferWithLocks, so its reads and writes wait at its gates.
 * `SharedMemory.CircularBuffer` - lock-free FIFO circular buffer implementation (aka ring buffer). Supporting 2 or more nodes, this implementation supports multiple readers and writers, which compete for items: see [Circular buffer readers and capacity](#circular-buffer-readers-and-capacity). The lock-free approach is implemented using Interlocked.CompareExchange and EventWaitHandles.
 * `SharedMemory.RpcBuffer` - simple bi-directional RPC channel using `SharedMemory.CircularBuffer`. Supports a master+slave pair per channel. Only available in .NET 4.5+ / .NET Standard 2.0

Behaviour to know
-----------------

### Circular buffer readers and capacity

Readers of one `CircularBuffer` compete for items: each item goes to the one reader that claims its node first, not to every reader. For every reader to see every item, give each reader a buffer of its own.

A `CircularBuffer` created with `nodeCount` nodes holds at most `nodeCount - 1` unread items, because one node always stays free. A writer that finds no free node waits for a reader, up to its timeout, and then returns 0.

### Read and write locks

The read and write locks of `BufferWithLocks`, and so of `SharedArray<T>` and `BufferReadWrite`, are gates, not exclusive locks:

 * `AcquireWriteLock` waits for the write gate to open and then closes the read gate. `AcquireReadLock` waits for the read gate to open and then closes the write gate. Neither stops a caller of its own kind: a second writer's `AcquireWriteLock` succeeds while the first still holds the lock.
 * Readers are not counted, so the first `ReleaseReadLock` opens the write gate while other readers are still reading.
 * Acquiring is a wait followed by a close, so a reader and a writer that acquire at the same moment can both get in.
 * Any process can release a lock, whether it holds it or not. A process that ends while holding one leaves the gate closed until every process has closed the buffer: meanwhile the others' reads and writes time out, and an `AcquireReadLock` or `AcquireWriteLock` with no timeout waits for ever.
 * Ordinary reads and writes take no lock. They wait at the gate for up to `ReadWriteTimeout` (100 ms by default), then throw `TimeoutException`.

Give each buffer a single writer, or coordinate writers outside the library, for example with a named `Mutex`.

### Element sizes from 2.3.4

From 2.3.4, shared memory holds each element type in one layout, the same in every process whatever its runtime or bitness: the layout 64-bit .NET 10 gives the type. `SharedArray<T>`, `CircularBuffer`, `BufferReadWrite` and `FastStructure` read and write that layout, and `FastStructure.SizeOf<T>()` returns its size. So a .NET Framework process and a .NET 10 process can now share structures that hold a `DateTime` or a `decimal`, which the two runtimes lay out differently.

Where a process lays a structure out the same way, the library copies it as one block of memory, as before. Where it does not, the library copies it field by field, which is slower. The library checks each type when a process first uses it. Layouts measured on .NET Framework 3.5 and 4.8, .NET Core 2.1 and .NET 6, 7, 9 and 10, in 64-bit and 32-bit processes, differ from .NET 10's for:

 * most structures that hold a `DateTime`, on .NET Framework, .NET Core 2.1 and .NET 6, and in 32-bit .NET 7 and .NET 10 processes;
 * most structures that hold a `decimal`, with or without `[MarshalAs(UnmanagedType.Currency)]`, on .NET Framework and .NET Core 2.1;
 * some structures that hold a `bool` or a `char` and use an explicit layout, directly or in a nested structure, on .NET Framework;
 * some structures that hold another structure whose `Pack` is smaller than its fields' alignment, in 32-bit .NET Framework processes.

The library copies other blittable structures, and other structures that hold `bool` or `char`, as blocks. Where speed matters, store a `long` in place of a `DateTime` or `decimal` field, such as `DateTime.Ticks`, or `DateTime.ToBinary()` to keep the `Kind`.

On the field-by-field path, `FastStructure<T>.Size` is the size in shared memory, not the size of a `T` in the process's own memory. Code that copies `Size` bytes from `FastStructure.GetPtr`, or works out `BufferReadWrite` positions with `sizeof(T)`, does not match shared memory there, and `Marshal.SizeOf` does not always match either. Use `FastStructure.SizeOf<T>()` for positions.

Structures that hold an `IntPtr`, a `UIntPtr` or a pointer keep the process's own layout and are copied as before, so they are not portable across bitness or runtimes. The same goes for structures with a field of a generic structure type, such as `int?`, or of a structure from .NET itself other than `DateTime`, `decimal`, `Guid` and `TimeSpan`.

Up to 2.3.3, the library sized elements with `Marshal.SizeOf` and laid them out as each process did. These types change from 2.3.3:

 * `bool` (4 bytes before, 1 now), `char` (1 byte before, 2 now), and structures that hold them, unless declared as the first workaround below says;
 * structures that hold a Currency `decimal`, on every runtime: an `int`, a Currency `decimal` and an `int` took 24 bytes and now take 32;
 * the structures listed above, in the processes that copy them field by field: their fields move to the offsets .NET 10 gives them. Elsewhere they keep 2.3.3's layout;
 * structures the library keeps in the process's own layout, as described above, whose .NET size differs from their `Marshal.SizeOf` size: an `int`, a `DateTimeOffset` and an `int` took 24 bytes in a 32-bit .NET Framework process and now take 20.

2.3.3 copied many of these wrongly, but not all. On .NET Framework, for example, it read and wrote a structure of an `int`, a `DateTime` and an `int` correctly one at a time, as the `SharedArray<T>` indexer does, and arrays of the same structure with a Currency `decimal` in place of the `DateTime`. Even so, a process on 2.3.3 or earlier and one on 2.3.4 or later must not share a buffer of these types: upgrade every process that shares them together.

To share `bool` and `char` data with 2.3.3 or earlier:

 * declare structures with `bool` or `char` fields `[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]`, with the `bool` fields marked `[MarshalAs(UnmanagedType.I1)]`, which gives them the same size under both rules;
 * for plain `bool` or `char` elements, use `byte` or `ushort` and convert.

The library's own structures and blittable types are unchanged, in 32-bit and 64-bit processes, apart from the 32-bit .NET Framework case listed above.

Example Usage
-------------

The output of the following examples is:

    SharedMemory.SharedArray:
    123
    456
    SharedMemory.CircularBuffer:
    123
    456
    SharedMemory.BufferReadWrite:
    123
    456
    SharedMemory.RpcBuffer:
    133

**SharedMemory.SharedArray**

    Console.WriteLine("SharedMemory.SharedArray:");
    using (var producer = new SharedMemory.SharedArray<int>("MySharedArray", 10))
    using (var consumer = new SharedMemory.SharedArray<int>("MySharedArray"))
    {
        producer[0] = 123;
        producer[producer.Length - 1] = 456;
        
        Console.WriteLine(consumer[0]);
        Console.WriteLine(consumer[consumer.Length - 1]);
    }

**SharedMemory.CircularBuffer**

    Console.WriteLine("SharedMemory.CircularBuffer:");
    using (var producer = new SharedMemory.CircularBuffer(name: "MySharedMemory", nodeCount: 3, nodeBufferSize: 4))
    using (var consumer = new SharedMemory.CircularBuffer(name: "MySharedMemory"))
    {
        // nodeCount must be one larger than the number
        // of writes that must fit in the buffer at any one time
        producer.Write<int>(new int[] { 123 });
        producer.Write<int>(new int[] { 456 });
       
        int[] data = new int[1];
        consumer.Read<int>(data);
        Console.WriteLine(data[0]);
        consumer.Read<int>(data);
        Console.WriteLine(data[0]);
    }

**SharedMemory.BufferReadWrite**

    Console.WriteLine("SharedMemory.BufferReadWrite:");
    using (var producer = new SharedMemory.BufferReadWrite(name: "MySharedBuffer", bufferSize: 1024))
    using (var consumer = new SharedMemory.BufferReadWrite(name: "MySharedBuffer"))
    {
        int data = 123;
        producer.Write<int>(ref data);
        data = 456;
        producer.Write<int>(ref data, 1000);
        
        int readData;
        consumer.Read<int>(out readData);
        Console.WriteLine(readData);
        consumer.Read<int>(out readData, 1000);
        Console.WriteLine(readData);
    }

**SharedMemory.RpcBuffer**

    Console.WriteLine("SharedMemory.RpcBuffer:");
    // Ensure a unique channel name
    var rpcName = "RpcTest" + Guid.NewGuid().ToString();
    using (var rpcMaster = new RpcBuffer(rpcName))
    using (var rpcSlave = new RpcBuffer(rpcName, (msgId, payload) =>
    {
        // Add the two bytes together
        return BitConverter.GetBytes((payload[0] + payload[1]));
    }))
    {
        // Call the remote handler to add 123 and 10
        var result = rpcMaster.RemoteRequest(new byte[] { 123, 10 });
        // Success is false if no response came within the timeout (30 s by default)
        if (result.Success)
            Console.WriteLine(BitConverter.ToInt32(result.Data, 0)); // outputs 133
    }

Performance
-----------

### RPC Buffer

When an `RpcBuffer` is created, a buffer capacity can be specified along with the number of nodes to be created in the underlying `CircularBuffer` instances. A message is sent within one or more packets, where a single packet is made up of a packet header (32 bytes for `RpcProtocol.V1`) and the message payload. Ideally there should be enough room allocated within the underlying buffer to hold at least one message preferably a few (i.e. `(numberOfNodes - 1) * (bufferCapacity - 32) >= maxMessageSize`, since one node always stays free and each packet carries the header).

If the message payload exceeds the `bufferCapacity - packetHeaderSize`, then the message is split into multiple packets. Therefore the `RpcBuffer` message throughput depends not only upon the message size, but the relationship between the buffer capacity and the message size (i.e. how many packets are required for a single message).

For example, a message size of 512KB that fits in a single packet (i.e. with a `bufferCapacity >= 1024 * 512 + 32`) might achieve a throughput of around 2k messages/sec whereas with a buffer capacity of only 1KB it will achieve around 500 messages/sec. Larger buffer capacities do not necessarily mean greater message throughput, for example with the 512KB message size example, using a smaller buffer capacity of 256KB actually slightly improves performance.

A 1KB message can be sent as a single packet approximately 10k times/sec.

### Circular Buffer

The maximum bandwidth achieved was approximately 20GB/s, using 20 nodes of 1MB each with 1 reader and 1 writer. The .NET 3.5 implementation is markedly slower (~14GB/s), probably due to framework level performance improvements.

The following chart shows the bandwidth achieved in MB/s using a variety of circular buffer configurations, ranging from 2 nodes to 50 nodes with a varying number of readers/writers, comparing a 1KB vs 1MB node buffer size on .NET 3.5 and .NET 4.

![Circular buffer bandwidth](https://raw.githubusercontent.com/justinstenning/SharedMemory/main/docs/SharedMemoryBandwidth.png)

All results are from a machine running Windows 10 64-bit, Intel Core i7-3770K @ 3.50GHz, 16GB DDR3@1200MHz on an ASUS P8Z77-V motherboard. The data transferred was selected randomly from an array of 256 buffers that had been populated with random bytes.
