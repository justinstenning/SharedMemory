SharedMemory
============

C# shared memory classes for sharing data between processes (Array, Buffer and Circular Buffer)

[![Build status](https://ci.appveyor.com/api/projects/status/uc32kwm1281y4sie?svg=true)](https://ci.appveyor.com/project/spazzarama/sharedmemory)

About
-----

The SharedMemory class library provides a set of C# classes that utilise memory mapped files for fast low-level inter-process communication (IPC) - specifically for sharing data between processes.

The library uses the .NET 4 MemoryMappedFile class in .NET 4.0+, and implements its own wrapper class for .NET 3.5.

Classes
-------

 * **SharedMemory.SharedBuffer** - an abstract base class that wraps a memory mapped file, exposing the read/write operations and implementing a small header to allow clients to open the shared buffer without knowing the size beforehand.
 * **SharedMemory.BufferWithLocks** - an abstract class that extends SharedMemory.SharedBuffer to provide simple read/write locking support through the use of EventWaitHandles.
 * **SharedMemory.SharedArray** - a simple generic array implementation utilising a shared memory buffer. Inherits from SharedMemory.BufferWithLocks to provide support for thread synchronisation.
 * **SharedMemory.BufferReadWrite** - provides read/write access to a shared memory buffer, with various overloads to support reading and writing structures, copying to and from IntPtr and so on. Inherits from SharedMemory.BufferWithLocks to provide support for thread synchronisation.
 * **SharedMemory.CircularBuffer** - lock-free FIFO circular buffer implementation (aka ring buffer). Supporting 2 or more nodes, this implementation supports multiple readers and writers. The lock-free approach is implemented using Interlocked.Exchange and EventWaitHandles.

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
