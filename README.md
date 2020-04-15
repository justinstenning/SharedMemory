SharedMemory
============

C# shared memory classes for sharing data between processes (Array, Buffer, Circular Buffer and RPC)

[![Build status](https://ci.appveyor.com/api/projects/status/uc32kwm1281y4sie?svg=true)](https://ci.appveyor.com/project/spazzarama/sharedmemory)

About
-----

The SharedMemory class library provides a set of C# classes that utilise memory mapped files for fast low-level inter-process communication (IPC). Originally only for sharing data between processes, but now also with a simple RPC implementation.

The library uses the .NET MemoryMappedFile class in .NET 4.0+, and implements its own wrapper class for .NET 3.5.

Classes
-------

 * `SharedMemory.SharedBuffer` - an abstract base class that wraps a memory mapped file, exposing the read/write operations and implementing a small header to allow clients to open the shared buffer without knowing the size beforehand.
 * `SharedMemory.BufferWithLocks` - an abstract class that extends SharedMemory.SharedBuffer to provide simple read/write locking support through the use of EventWaitHandles.
 * `SharedMemory.SharedArray` - a simple generic array implementation utilising a shared memory buffer. Inherits from SharedMemory.BufferWithLocks to provide support for thread synchronisation.
 * `SharedMemory.BufferReadWrite` - provides read/write access to a shared memory buffer, with various overloads to support reading and writing structures, copying to and from IntPtr and so on. Inherits from SharedMemory.BufferWithLocks to provide support for thread synchronisation.
 * `SharedMemory.CircularBuffer` - lock-free FIFO circular buffer implementation (aka ring buffer). Supporting 2 or more nodes, this implementation supports multiple readers and writers. The lock-free approach is implemented using Interlocked.Exchange and EventWaitHandles.
 * `SharedMemory.RpcBuffer` - simple bi-directional RPC channel using `SharedMemory.CircularBuffer`. Supports a master+slave pair per channel. Only available in .NET 4.5+ / .NET Standard 2.0

Example Usage
-------------

The output from the of the following examples is:

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
    var rpcMaster = new RpcBuffer(rpcName);
    var rpcSlave = new RpcBuffer(rpcName, (msgId, payload) =>
    {
        // Add the two bytes together
        return BitConverter.GetBytes((payload[0] + payload[1]));
    });
    
    // Call the remote handler to add 123 and 10
    var result = rpcMaster.RemoteRequest(new byte[] { 123, 10 });
    Console.WriteLine(result); // outputs 133

Performance
-----------

### RPC Buffer

When an `RpcBuffer` is created, a buffer capacity can be specified along with the number of nodes to be created in the underlying `CircularBuffer` instances. A message is sent within one or more packets, where a single packet is made up of a packet header (64-bytes for `RpcProtocol.V1`) and the message payload. Ideally there should be enough room allocated within the underlying buffer to hold at least one message preferably a few (i.e. `bufferCapacity * numberOfNodes > maxMessageSize`).

If the message payload exceeds the `bufferCapacity - packetHeaderSize`, then the message is split into multiple packets. Therefore the `RpcBuffer` message throughput depends not only upon the message size, but the relationship between the buffer capacity and the message size (i.e. how many packets are required for a single message).

For example, a message size of 512KB that fits in a single packet (i.e. with a `bufferCapacity >= 1024 * 500 + 64`) might achieve a throughput of around 2k messages/sec whereas with a buffer capacity of only 1KB it will achieve around 500 messages/sec. Larger buffer capacities do not necessarily mean greater message throughput, for example with the 512KB message size example, using a smaller buffer capacity of 256KB actually slightly improves performance.

A 1KB message can be sent as a single packet approximately 10k times/sec.

### Circular Buffer

The maximum bandwidth achieved was approximately 20GB/s, using 20 nodes of 1MB each with 1 reader and 1 writer. The .NET 3.5 implementation is markedly slower (~14GB/s), probably due to framework level performance improvements.

The following chart shows the bandwidth achieved in MB/s using a variety of circular buffer configurations, ranging from 2 nodes to 50 nodes with a varying number of readers/writers, comparing a 1KB vs 1MB node buffer size on .NET 3.5 and .NET 4.

![Circular buffer bandwidth](http://spazzarama.com/wp-content/uploads/2015/12/SharedMemoryBandwidth.png)

All results are from a machine running Windows 10 64-bit, Intel Core i7-3770K @ 3.50GHz, 16GB DDR3@1200MHz on an ASUS P8Z77-V motherboard. The data transferred was selected randomly from an array of 256 buffers that had been populated with random bytes.
