// SharedMemory (File: SharedMemory\SharedArray.cs)
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
using System.Runtime.InteropServices;
using System.Security.Permissions;

namespace SharedMemory
{
    /// <summary>
    /// A generic fixed-length shared memory array of structures with support for simple inter-process read/write synchronisation.
    /// </summary>
    /// <typeparam name="T">The structure type that will be stored in the elements of this fixed array buffer.</typeparam>
#if NETFULL
    [PermissionSet(SecurityAction.LinkDemand)]
    [PermissionSet(SecurityAction.InheritanceDemand)]
#endif
    public class SharedArray<T> : BufferWithLocks, IList<T>
            where T : struct
    {
        /// <summary>
        /// Gets a 32-bit integer that represents the total number of elements in the <see cref="SharedArray{T}"/>
        /// </summary>
        public int Length { get; private set; }
        
        /// <summary>
        /// Gets or sets the element at the specified index
        /// </summary>
        /// <param name="index">The zero-based index of the element to get or set.</param>
        /// <returns>The element at the specified index.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is less than 0 -or- index is equal to or greater than <see cref="Length"/>.</exception>
        public T this[int index]
        {
            get
            {
                T item;
                Read(out item, index);
                return item;
            }
            set
            {
                Write(ref value, index);
            }
        }

        private int _elementSize;

        #region Constructors

        /// <summary>
        /// Creates the shared memory array with the name specified by <paramref name="name"/>.
        /// </summary>
        /// <param name="name">The name of the shared memory array to be created.</param>
        /// <param name="length">The number of elements to make room for within the shared memory array.</param>
        public SharedArray(string name, int length)
            : base(name, Marshal.SizeOf(typeof(T)) * length, true)
        {
            Length = length;
            _elementSize = Marshal.SizeOf(typeof(T));

            Open();
        }

        /// <summary>
        /// Opens an existing shared memory array with the name as specified by <paramref name="name"/>.
        /// </summary>
        /// <param name="name">The name of the shared memory array to open.</param>
        /// <exception cref="ArgumentOutOfRangeException">If the shared memory location specified by <paramref name="name"/> does not have a <see cref="SharedBuffer.BufferSize"/> that is evenly divisible by the size of <typeparamref name="T"/>.</exception>
        public SharedArray(string name)
            : base(name, 0, false)
        {
            _elementSize = Marshal.SizeOf(typeof(T));

            Open();
        }

        #endregion

        /// <summary>
        /// Perform any initialisation required when opening the shared memory array
        /// </summary>
        /// <returns>true if successful</returns>
        protected override bool DoOpen()
        {
            if (!IsOwnerOfSharedMemory)
            {
                if (BufferSize % _elementSize != 0)
                    throw new ArgumentOutOfRangeException("name", "BufferSize is not evenly divisible by the size of " + typeof(T).Name);

                Length = (int)(BufferSize / _elementSize);
            }
            return true;
        }

        #region Writing

        /// <summary>
        /// Copy <paramref name="data"/> to the shared memory array element at index <paramref name="index"/>.
        /// </summary>
        /// <param name="data">The data to be written.</param>
        /// <param name="index">The zero-based index of the element to set.</param>
        public void Write(ref T data, int index)
        {
            if (index > Length - 1 || index < 0)
                throw new ArgumentOutOfRangeException("index");

            base.Write(ref data, index * _elementSize);
        }

        /// <summary>
        /// Copy the elements of the array <paramref name="buffer"/> into the shared memory array starting at index <paramref name="startIndex"/>.
        /// </summary>
        /// <param name="buffer">The source array to copy elements from.</param>
        /// <param name="startIndex">The zero-based index of the shared memory array element to begin writing to.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is less than 0 -or- length of <paramref name="buffer"/> + <paramref name="startIndex"/> is greater than <see cref="Length"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> must not be null</exception>
        public void Write(T[] buffer, int startIndex = 0)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (buffer.Length + startIndex > Length || startIndex < 0)
                throw new ArgumentOutOfRangeException("startIndex");

            base.Write(buffer, startIndex * _elementSize);
        }

        #endregion

        #region Reading

        /// <summary>
        /// Reads a single element from the shared memory array into <paramref name="data"/> located at <paramref name="index"/>.
        /// </summary>
        /// <param name="data">The element at the specified index.</param>
        /// <param name="index">The zero-based index of the element to get.</param>
        /// <returns>The element at the specified index.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is less than 0 -or- index is equal to or greater than <see cref="Length"/>.</exception>
        public void Read(out T data, int index)
        {
            if (index > Length - 1 || index < 0)
                throw new ArgumentOutOfRangeException("index");

            base.Read(out data, index * _elementSize);
        }

        /// <summary>
        /// Reads buffer.Length elements from the shared memory array into <paramref name="buffer"/> starting at the shared memory array element located at <paramref name="startIndex"/>.
        /// </summary>
        /// <param name="buffer">The destination array to copy the elements into.</param>
        /// <param name="startIndex">The zero-based index of the shared memory array element to begin reading from.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startIndex"/> is less than 0 -or- length of <paramref name="buffer"/> + <paramref name="startIndex"/> is greater than <see cref="Length"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> must not be null</exception>
        public void CopyTo(T[] buffer, int startIndex = 0)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (buffer.Length + startIndex > Length || startIndex < 0)
                throw new ArgumentOutOfRangeException("startIndex");

            base.Read(buffer, startIndex * _elementSize);
        }

        #endregion

        #region IEnumerable<T>

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>An <see cref="System.Collections.Generic.IEnumerator{T}"/> instance that can be used to iterate through the collection</returns>
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Length; i++)
            {
                yield return this[i];
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>An <see cref="System.Collections.IEnumerator"/> object that can be used to iterate through the collection.</returns>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        #endregion

        #region IList<T>
        /// <summary>
        /// Operation not supported. Throws <see cref="System.NotImplementedException"/>
        /// </summary>
        /// <param name="item"></param>
        public void Add(T item)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Operation not supported. Throws <see cref="System.NotImplementedException"/>
        /// </summary>
        public void Clear()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Checks if the list contains the specified item.
        /// </summary>
        /// <param name="item"></param>
        /// <returns>True if found</returns>
        public bool Contains(T item)
        {
            return IndexOf(item) >= 0;
        }

        /// <summary>
        /// Operation not supported. Throws <see cref="System.NotImplementedException"/>
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool Remove(T item)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// The number of elements in the array
        /// </summary>
        public int Count
        {
            get { return Length; }
        }


        /// <summary>
        /// The elements are not read-only
        /// </summary>
        public bool IsReadOnly
        {
            get { return true; }
        }

        /// <summary>
        /// Return the index of the specified item.
        /// </summary>
        /// <param name="item"></param>
        /// <returns>The index of the item if found, otherwise -1.</returns>
        public int IndexOf(T item)
        {
            for (var i = 0; i < Count; i++)
            {
                if (this[i].Equals(item)) return i;
            }
            return -1;
        }

        /// <summary>
        /// Operation not supported. Throws <see cref="System.NotImplementedException"/>
        /// </summary>
        /// <param name="index"></param>
        /// <param name="item"></param>
        public void Insert(int index, T item)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Operation not supported. Throws <see cref="System.NotImplementedException"/>
        /// </summary>
        /// <param name="index"></param>
        public void RemoveAt(int index)
        {
            throw new NotImplementedException();
        }

        #endregion

    }
}
