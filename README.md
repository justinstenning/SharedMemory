SharedMemory
============

C# shared memory classes for sharing data between processes (Array, Buffer and Circular Buffer)

About
-----

The SharedMemory class library provides a set of C# classes that utilise a .NET 4 MemoryMappedFile class for fast low-level inter-process communication (IPC) - specifically for sharing data between processes.

Classes
-------

 * **SharedMemory.Buffer** - an abstract base class that wraps the .NET 4 MemoryMappedFile class, exposing the read/write operations and implementing a small header to allow clients to open the shared buffer without knowing the size beforehand.
 * **SharedMemory.BufferWithLocks** - an abstract class that extends SharedMemory.Buffer to provide simple read/write locking support through the use of EventWaitHandles.
 * **SharedMemory.Array** - a simple generic array implementation utilising a shared memory buffer. Inherits from SharedMemory.BufferWithLocks to provide support for thread synchronisation.
 * **SharedMemory.BufferReadWrite** - provides read/write access to a shared memory buffer, with various overloads to support reading and writing structures, copying to and from IntPtr and so on. Inherits from SharedMemory.BufferWithLocks to provide support for thread synchronisation.
 * **SharedMemory.CircularBuffer** - lock-free FIFO circular buffer implementation (aka ring buffer). Supporting 2 or more nodes, this implementation supports multiple readers and writers. The lock-free approach is implemented using Interlocked.Exchange and EventWaitHandles.
 
 
