// SharedMemory (File: SharedMemory\SharedBuffer.cs)
// Copyright (c) 2014 Justin Stenning
// http://spazzarama.com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// The SharedMemory library is inspired by the following Code Project article:
//   "Fast IPC Communication Using Shared Memory and InterlockedCompareExchange"
//   http://www.codeproject.com/Articles/14740/Fast-IPC-Communication-Using-Shared-Memory-and-Int

using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Text;
using System.Threading;

namespace SharedMemory
{
    /// <summary>
    /// Abstract base class that provides client/server support for reading/writing structures to a buffer within a <see cref="MemoryMappedFile" />.
    /// A header structure allows clients to open the buffer without knowing the size.
    /// </summary>
#if NETFULL
    [PermissionSet(SecurityAction.LinkDemand)]
    [PermissionSet(SecurityAction.InheritanceDemand)]
#endif
    public abstract unsafe class SharedBuffer : IDisposable
    {
        #region Public/Protected properties

        /// <summary>
        /// The name of the Shared Memory instance
        /// </summary>
        public string Name { get; private set; }
        
        /// <summary>
        /// The buffer size
        /// </summary>
        public long BufferSize { get; private set; }
        
        /// <summary>
        /// The total shared memory size, including header and buffer.
        /// </summary>
        public virtual long SharedMemorySize
        {
            get
            {
                return HeaderOffset + Marshal.SizeOf(typeof(SharedHeader)) + BufferSize;
            }
        }

        /// <summary>
        /// Indicates whether this instance owns the shared memory (i.e. creator of the shared memory)
        /// </summary>
        public bool IsOwnerOfSharedMemory { get; private set; }
        
        /// <summary>
        /// Returns true if the SharedMemory owner has/is shutting down
        /// </summary>
        public bool ShuttingDown
        {
            get
            {
                if (Header == null || Header->Shutdown == 1)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Where the header starts within the shared memory
        /// </summary>
        protected virtual long HeaderOffset
        {
            get
            {
                return 0;
            }
        }

        /// <summary>
        /// Where the buffer is located within the shared memory
        /// </summary>
        protected virtual long BufferOffset
        {
            get
            {
                return HeaderOffset + Marshal.SizeOf(typeof(SharedHeader));
            }
        }

        #endregion

        #region Protected field members

        /// <summary>
        /// Memory mapped file
        /// </summary>
        protected MemoryMappedFile Mmf;
        /// <summary>
        /// Memory mapped view
        /// </summary>
        protected MemoryMappedViewAccessor View;
        /// <summary>
        /// Pointer to the memory mapped view
        /// </summary>
        protected byte* ViewPtr = null;
        /// <summary>
        /// Pointer to the start of the buffer region of the memory mapped view
        /// </summary>
        protected byte* BufferStartPtr = null;
        /// <summary>
        /// Pointer to the header within shared memory
        /// </summary>
        protected SharedHeader* Header = null;

        #endregion

        #region Constructor / destructor

        /// <summary>
        /// Create a new <see cref="SharedBuffer"/> instance with the specified name and buffer size
        /// </summary>
        /// <param name="name">The name of the shared memory</param>
        /// <param name="bufferSize">The buffer size in bytes. The total shared memory size will be <code>Marshal.SizeOf(SharedMemory.SharedHeader) + bufferSize</code></param>
        /// <param name="ownsSharedMemory">Whether or not the current instance owns the shared memory. If true a new shared memory will be created and initialised otherwise an existing one is opened.</param>
        /// <remarks>
        /// <para>The maximum total shared memory size is dependent upon the system and current memory fragmentation.</para>
        /// <para>The shared memory layout on 32-bit and 64-bit is:<br />
        /// <code>
        /// |       Header       |    Buffer    |<br />
        /// |      16-bytes      |  bufferSize  |
        /// </code>
        /// </para>
        /// </remarks>
        protected SharedBuffer(string name, long bufferSize, bool ownsSharedMemory)
        {
            #region Argument validation
            if (name == String.Empty || name == null)
                throw new ArgumentException("Cannot be String.Empty or null", "name");
            if (ownsSharedMemory && bufferSize <= 0)
                throw new ArgumentOutOfRangeException("bufferSize", bufferSize, "Buffer size must be larger than zero when creating a new shared memory buffer.");
#if DEBUG
            else if (!ownsSharedMemory && bufferSize > 0)
                System.Diagnostics.Debug.Write("Buffer size is ignored when opening an existing shared memory buffer.", "Warning");
#endif
            #endregion

            IsOwnerOfSharedMemory = ownsSharedMemory;
            Name = name;

            if (IsOwnerOfSharedMemory)
            {
                BufferSize = bufferSize;
            }
        }

        /// <summary>
        /// Destructor - for Dispose(false)
        /// </summary>
        ~SharedBuffer()
        {
            Dispose(false);
        }

        #endregion

        #region Open / Close

        /// <summary>
        /// Creates a new or opens an existing shared memory buffer with the name of <see cref="Name"/> depending on the value of <see cref="IsOwnerOfSharedMemory"/>. 
        /// </summary>
        /// <returns>True if the memory was successfully mapped</returns>
        /// <remarks>If <see cref="IsOwnerOfSharedMemory"/> is true then the shared memory buffer will be created, opening will fail in this case if the shared memory already exists. Otherwise if IsOwnerOfSharedMemory is false then the shared memory buffer will be opened, which will fail if it doesn't already exist.</remarks>
        /// <exception cref="System.IO.IOException">If trying to create a new shared memory buffer with a duplicate name as buffer owner.</exception>
        /// <exception cref="System.IO.FileNotFoundException">If trying to open a new shared memory buffer that does not exist as a consumer of existing buffer.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">If trying to create a new shared memory buffer with a size larger than the logical addressable space.</exception>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        protected bool Open()
        {
            Close();

            try
            {
                // Attempts to create or open the shared memory with a name of this.Name
                if (IsOwnerOfSharedMemory)
                {
                    // Create a new shared memory mapping
                    Mmf = MemoryMappedFile.CreateNew(Name, SharedMemorySize);

                    // Create a view to the entire region of the shared memory
                    View = Mmf.CreateViewAccessor(0, SharedMemorySize, MemoryMappedFileAccess.ReadWrite);
                    View.SafeMemoryMappedViewHandle.AcquirePointer(ref ViewPtr);
                    Header = (SharedHeader*)(ViewPtr + HeaderOffset);
                    BufferStartPtr = ViewPtr + BufferOffset;
                    // Initialise the header
                    InitialiseHeader();
                }
                else
                {
                    // Open an existing shared memory mapping
                    Mmf = MemoryMappedFile.OpenExisting(Name);

                    // Retrieve the header from the shared memory in order to initialise the correct size
                    using (var headerView = Mmf.CreateViewAccessor(0, HeaderOffset + Marshal.SizeOf(typeof(SharedHeader)), MemoryMappedFileAccess.Read))
                    {
                        byte* headerPtr = null;
                        headerView.SafeMemoryMappedViewHandle.AcquirePointer(ref headerPtr);
                        var header = (SharedHeader*)(headerPtr + HeaderOffset);
                        BufferSize = header->SharedMemorySize - Marshal.SizeOf(typeof(SharedHeader));
                        headerView.SafeMemoryMappedViewHandle.ReleasePointer();
                    }

                    // Create a view to the entire region of the shared memory
                    View = Mmf.CreateViewAccessor(0, SharedMemorySize, MemoryMappedFileAccess.ReadWrite);
                    View.SafeMemoryMappedViewHandle.AcquirePointer(ref ViewPtr);
                    Header = (SharedHeader*)(ViewPtr + HeaderOffset);
                    BufferStartPtr = ViewPtr + HeaderOffset + Marshal.SizeOf(typeof(SharedHeader));
                }
            }
            catch
            {
                Close();
                throw;
            }

            // Complete any additional open logic
            try
            {
                if (!DoOpen())
                {
                    Close();
                    return false;
                }
                else
                {
                    return true;
                }
            }
            catch
            {
                Close();
                throw;
            }
        }

        /// <summary>
        /// Allows any classes that inherit from <see cref="SharedBuffer"/> to perform additional open logic. There is no need to call base.DoOpen() from these implementations.
        /// </summary>
        /// <returns>True if successful, otherwise false.</returns>
        /// <remarks>By throwing an exception or returning false, the call to <see cref="Open"/> will fail and <see cref="Close"/> will be called.</remarks>
        protected virtual bool DoOpen()
        {
            return true;
        }

        /// <summary>
        /// Initialises the header within the shared memory. Only applicable if <see cref="IsOwnerOfSharedMemory"/> is true.
        /// </summary>
        protected void InitialiseHeader()
        {
            if (!IsOwnerOfSharedMemory)
                return;

            SharedHeader header = new SharedHeader();
            header.SharedMemorySize = SharedMemorySize;
            header.Shutdown = 0;
            View.Write<SharedHeader>(HeaderOffset, ref header);
        }

        /// <summary>
        /// Sets the <see cref="ShuttingDown"/> flag, and disposes of the MemoryMappedFile and MemoryMappedViewAccessor.<br />
        /// Attempting to read/write to the buffer after closing will result in a <see cref="System.NullReferenceException"/>.
        /// </summary>
        public virtual void Close()
        {
            if (IsOwnerOfSharedMemory && View != null)
            {
                // Indicates to any open instances that the owner is no longer open
#pragma warning disable 0420 // ignore ref to volatile warning - Interlocked API
                Interlocked.Exchange(ref Header->Shutdown, 1);
#pragma warning restore 0420
            }

            // Allow additional close logic
            DoClose();

            // Close the MemoryMappedFile and MemoryMappedViewAccessor
            if (View != null)
            {
                View.SafeMemoryMappedViewHandle.ReleasePointer();
                View.Dispose();
            }
            if (Mmf != null)
            {
                Mmf.Dispose();
            }
            Header = null;
            ViewPtr = null;
            BufferStartPtr = null;
            View = null;
            Mmf = null;
        }

        /// <summary>
        /// Any classes that inherit from <see cref="SharedBuffer"/> should implement any <see cref="Close"/> logic here, <see cref="Mmf"/> and <see cref="View"/> are still active at this point. There is no need to call base.DoClose() from these classes.
        /// </summary>
        /// <remarks>It is possible for <see cref="Close"/> to be called before <see cref="Open"/> has completed successfully, in this situation <see cref="DoClose"/> should fail gracefully.</remarks>
        protected virtual void DoClose()
        {
        }

        #endregion

        #region Writing

        /// <summary>
        /// Writes an instance of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="source">A reference to an instance of <typeparamref name="T"/> to be written into the buffer</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void Write<T>(ref T source, long bufferPosition = 0)
            where T : struct
        {
            View.Write<T>(BufferOffset + bufferPosition, ref source);
        }

        /// <summary>
        /// Writes an array of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="source">An array of <typeparamref name="T"/> to be written. The length of this array controls the number of elements to be written.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void Write<T>(T[] source, long bufferPosition = 0)
            where T : struct
        {
            Write<T>(source, 0, bufferPosition);
        }

        /// <summary>
        /// Writes an array of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="source">An array of <typeparamref name="T"/> to be written. The length of this array controls the number of elements to be written.</param>
        /// <param name="index">The index within the array to start writing from.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void Write<T>(T[] source, int index, long bufferPosition = 0)
            where T : struct
        {
            FastStructure.WriteArray<T>((IntPtr)(BufferStartPtr + bufferPosition), source, index, source.Length - index);
        }

        /// <summary>
        /// Writes an array of <typeparamref name="T"/> into the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="source">The source data to be written to the buffer</param>
        /// <param name="index">The start index within <paramref name="source"/>.</param>
        /// <param name="count">The number of elements to write.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void WriteArray<T>(T[] source, int index, int count, long bufferPosition = 0)
            where T : struct
        {
            FastStructure.WriteArray<T>((IntPtr)(BufferStartPtr + bufferPosition), source, index, count);
        }

        /// <summary>
        /// Writes <paramref name="length"/> bytes from the <paramref name="source"/> into the shared memory buffer.
        /// </summary>
        /// <param name="source">A managed pointer to the memory location to be copied into the buffer</param>
        /// <param name="length">The number of bytes to be copied</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to write to.</param>
        protected virtual void Write(IntPtr source, int length, long bufferPosition = 0)
        {
#if NETCORE
            Buffer.MemoryCopy((void*)source, BufferStartPtr + bufferPosition, BufferSize - bufferPosition, length);
#else
            UnsafeNativeMethods.CopyMemory(new IntPtr(BufferStartPtr + bufferPosition), source, (uint)length);
#endif
        }

        /// <summary>
        /// Prepares an IntPtr to the buffer position and calls <paramref name="writeFunc"/> to perform the writing.
        /// </summary>
        /// <param name="writeFunc">A function used to write to the buffer. The IntPtr parameter is a pointer to the buffer location offset by <paramref name="bufferPosition"/>.</param>
        /// <param name="bufferPosition">The offset within the buffer region to start writing to.</param>
        protected virtual void Write(Action<IntPtr> writeFunc, long bufferPosition = 0)
        {
            writeFunc(new IntPtr(BufferStartPtr + bufferPosition));
        }

        #endregion

        #region Reading

        /// <summary>
        /// Reads an instance of <typeparamref name="T"/> from the buffer
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="data">Output parameter that will contain the value read from the buffer</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected virtual void Read<T>(out T data, long bufferPosition = 0)
            where T : struct
        {
            View.Read<T>(BufferOffset + bufferPosition, out data);
        }

        /// <summary>
        /// Reads an array of <typeparamref name="T"/> from the buffer.
        /// </summary>
        /// <typeparam name="T">A structure type</typeparam>
        /// <param name="destination">Array that will contain the values read from the buffer. The length of this array controls the number of elements to read.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected virtual void Read<T>(T[] destination, long bufferPosition = 0)
            where T : struct
        {
            FastStructure.ReadArray<T>(destination, (IntPtr)(BufferStartPtr + bufferPosition), 0, destination.Length);
        }

        /// <summary>
        /// Reads a number of elements from a memory location into the provided buffer starting at the specified index.
        /// </summary>
        /// <typeparam name="T">The structure type</typeparam>
        /// <param name="destination">The destination buffer.</param>
        /// <param name="index">The start index within <paramref name="destination"/>.</param>
        /// <param name="count">The number of elements to read.</param>
        /// <param name="bufferPosition">The source offset within the buffer region of the shared memory.</param>
        protected virtual void ReadArray<T>(T[] destination, int index, int count, long bufferPosition)
            where T : struct
        {
            FastStructure.ReadArray<T>(destination, (IntPtr)(BufferStartPtr + bufferPosition), index, count);
        }

        /// <summary>
        /// Reads <paramref name="length"/> bytes into the memory location <paramref name="destination"/> from the buffer region of the shared memory.
        /// </summary>
        /// <param name="destination">A managed pointer to the memory location to copy data into from the buffer</param>
        /// <param name="length">The number of bytes to be copied</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected virtual void Read(IntPtr destination, int length, long bufferPosition = 0)
        {
#if NETCORE
            Buffer.MemoryCopy(BufferStartPtr + bufferPosition, (void*)destination, length, length);
#else
            UnsafeNativeMethods.CopyMemory(destination, new IntPtr(BufferStartPtr + bufferPosition), (uint)length);
#endif
        }

        /// <summary>
        /// Prepares an IntPtr to the buffer position and calls <paramref name="readFunc"/> to perform the reading.
        /// </summary>
        /// <param name="readFunc">A function used to read from the buffer. The IntPtr parameter is a pointer to the buffer offset by <paramref name="bufferPosition"/>.</param>
        /// <param name="bufferPosition">The offset within the buffer region of the shared memory to read from.</param>
        protected virtual void Read(Action<IntPtr> readFunc, long bufferPosition = 0)
        {
            readFunc(new IntPtr(BufferStartPtr + bufferPosition));
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// IDisposable pattern - dispose of managed/unmanaged resources
        /// </summary>
        /// <param name="disposeManagedResources">true to dispose of managed resources as well as unmanaged.</param>
        protected virtual void Dispose(bool disposeManagedResources)
        {
            if (disposeManagedResources)
            {
                this.Close();
            }
        }

        #endregion
    }
}
