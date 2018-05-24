using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SharedMemory.Utilities
{
    /// <summary>
    /// This is a dynamic array like .NET's List'T, except for the following:
    /// - Should be used only when growing the list incrementally.  No inserts or deletes in the middle of the list.
    /// - You can specify a custom allocator, so the data can be stored in any IList'T, including a memory-mapped file.
    ///       for struct types.
    /// - Unlike a typical dynamic array, such as List'T, this class does not copy data and fragment the memory by
    ///       leaving holes that have to be freed/reallocated.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class ExpandingArray<T> : IList<T>
    {
        /// <summary>
        /// Passed in allocator.  We use to this get "memory" where the actual data is stored.  Can be any IList'T
        /// </summary>
        private readonly Func<int, IList<T>> _allocator;

        /// <summary>
        /// Backing field for Count.  Contains the number of elements in the list from the user's perspective.
        /// </summary>
        private int _count;

        /// <summary>
        /// This is where all the allocations are stored.  The 1st bucket is a special case 
        /// and represents the first 3 elements of the list.
        /// The 2nd bucket contains the next 4 element of the list.
        /// The 3rd bucket contains the next 8 element of the list.
        /// The 4th bucket contains the next 16 element of the list.
        /// The 5th bucket contains the next 32 element of the list.
        /// etc.
        /// This way, given the index into the list, we can calculate which bucket it belongs to using log(i)/log(2)
        /// This array is initially allocated to contain only one bucket.   But a hint can be passed in to the constructor
        /// to pre-allocate more pubckets.  This will reduce reallocating the array as it grows.
        /// </summary>
        private IList<T>[] _buckets;

        /// <summary>
        /// The allocation is used to specify a customer allocator.
        ///    It's a function that returns an IList of the indicated size.
        ///    It can be as simple as something like: size => new int[size]
        /// The finalCapacityHint allows us to preallocate the buckets based on what the
        ///    the final capacity might be.   This does not allocate the entire list.
        ///    For instance, if finalCapacityHint one-million, an array of 18 objects
        ///    are allocated.
        ///    Guessing a smaller number simply means that the buckets are reallocated
        ///    as the array grows, causing GC churn which could otherwise be avoided.
        /// </summary>
        /// <param name="allocator"></param>
        /// <param name="finalCapacityHint"></param>
        public ExpandingArray(Func<int, IList<T>> allocator = null, int finalCapacityHint = 1)
        {
            _allocator = allocator ?? (size => new T[size]);
            _buckets = new IList<T>[Math.Max(GetBucketIndex(finalCapacityHint), 1)];
        }

        /// <summary>
        /// Given the bucket number, returns the bucket entry, which may be null if it hasn't
        /// been allocated yet.
        /// </summary>
        /// <param name="bucketIndex"></param>
        /// <returns></returns>
        private IList<T> GetBucket(int bucketIndex)
        {
            if (bucketIndex >= _buckets.Length)
            {
                var newBuckets = new IList<T>[_buckets.Length + 1];
                _buckets.CopyTo(newBuckets, 0);
                _buckets = newBuckets;
            }
            return _buckets[bucketIndex];
        }

        /// <summary>
        /// Given the index into the list, determines which bucket the element resides in.
        /// Made pubic for testing.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public static int GetBucketIndex(int index)
        {
            return Math.Max((int) (Math.Log(index + 1)/Math.Log(2)) - 1, 0);
        }

        /// <summary>
        /// Given the index into the list, returns the index into the bucket where the actual element resides.
        /// along with the bucket itself.
        /// </summary>
        /// <param name="globalIndex"></param>
        /// <param name="bucket"></param>
        /// <returns></returns>
        private int GetLocalIndex(int globalIndex, out IList<T> bucket)
        {
            var bucketIndex = GetBucketIndex(globalIndex);
            bucket = GetBucket(bucketIndex) ?? (_buckets[bucketIndex] = _allocator(Math.Max(3, globalIndex + 1)));
            return globalIndex - (bucketIndex > 0 ? (int)Math.Pow(2, bucketIndex + 1) - 1 : 0);
        }

        /// <summary>
        /// IList.Add().  Add an item to the list.
        /// </summary>
        /// <param name="item"></param>
        public void Add(T item)
        {
            var indexNewEntry = _count;

            IList<T> bucket;
            var localIndex = GetLocalIndex(indexNewEntry, out bucket);
            bucket[localIndex] = item;

            _count = indexNewEntry + 1;
        }

        /// <summary>Gets or sets the element at the specified index.</summary>
        /// <returns>The element at the specified index.</returns>
        /// <param name="index">The zero-based index of the element to get or set.</param>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="index" /> is not a valid index in the <see cref="T:System.Collections.Generic.IList`1" />.</exception>
        /// <exception cref="T:System.NotSupportedException">The property is set and the <see cref="T:System.Collections.Generic.IList`1" /> is read-only.</exception>
        public T this[int index]
        {
            get
            {
                IList<T> bucket;
                var localIndex = GetLocalIndex(index, out bucket);
                return bucket[localIndex];
            }
            set
            {
                IList<T> bucket;
                var localIndex = GetLocalIndex(index, out bucket);
                bucket[localIndex] = value;
            }
        }

        /// <summary>Removes all items from the <see cref="T:System.Collections.Generic.ICollection`1" />.</summary>
        /// <exception cref="T:System.NotSupportedException">The <see cref="T:System.Collections.Generic.ICollection`1" /> is read-only. </exception>
        public void Clear()
        {
            _count = 0;
            _buckets = new IList<T>[1];
        }

        /// <summary>Determines whether the <see cref="T:System.Collections.Generic.ICollection`1" /> contains a specific value.</summary>
        /// <returns>true if <paramref name="item" /> is found in the <see cref="T:System.Collections.Generic.ICollection`1" />; otherwise, false.</returns>
        /// <param name="item">The object to locate in the <see cref="T:System.Collections.Generic.ICollection`1" />.</param>
        public bool Contains(T item)
        {
            return IndexOf(item) >= 0;
        }

        /// <summary>Copies the elements of the <see cref="T:System.Collections.Generic.ICollection`1" /> to an <see cref="T:System.Array" />, starting at a particular <see cref="T:System.Array" /> index.</summary>
        /// <param name="array">The one-dimensional <see cref="T:System.Array" /> that is the destination of the elements copied from <see cref="T:System.Collections.Generic.ICollection`1" />. The <see cref="T:System.Array" /> must have zero-based indexing.</param>
        /// <param name="arrayIndex">The zero-based index in <paramref name="array" /> at which copying begins.</param>
        /// <exception cref="T:System.ArgumentNullException">
        /// <paramref name="array" /> is null.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="arrayIndex" /> is less than 0.</exception>
        /// <exception cref="T:System.ArgumentException">The number of elements in the source <see cref="T:System.Collections.Generic.ICollection`1" /> is greater than the available space from <paramref name="arrayIndex" /> to the end of the destination <paramref name="array" />.</exception>
        public void CopyTo(T[] array, int arrayIndex)
        {
            for (var i = 0; i < _count; i++) array[arrayIndex + i] = this[i];
        }

        /// <summary>not implemented Removes the first occurrence of a specific object from the <see cref="T:System.Collections.Generic.ICollection`1" />.</summary>
        /// <returns>true if <paramref name="item" /> was successfully removed from the <see cref="T:System.Collections.Generic.ICollection`1" />; otherwise, false. This method also returns false if <paramref name="item" /> is not found in the original <see cref="T:System.Collections.Generic.ICollection`1" />.</returns>
        /// <param name="item">The object to remove from the <see cref="T:System.Collections.Generic.ICollection`1" />.</param>
        /// <exception cref="T:System.NotSupportedException">The <see cref="T:System.Collections.Generic.ICollection`1" /> is read-only.</exception>
        public bool Remove(T item)
        {
            throw new NotImplementedException();
        }

        /// <summary>Gets the number of elements contained in the <see cref="T:System.Collections.Generic.ICollection`1" />.</summary>
        /// <returns>The number of elements contained in the <see cref="T:System.Collections.Generic.ICollection`1" />.</returns>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>Gets a value indicating whether the <see cref="T:System.Collections.Generic.ICollection`1" /> is read-only.</summary>
        /// <returns>true if the <see cref="T:System.Collections.Generic.ICollection`1" /> is read-only; otherwise, false.</returns>
        public bool IsReadOnly
        {
            get { return false; }
        }

        /// <summary>Returns an enumerator that iterates through the collection.</summary>
        /// <returns>A <see cref="T:System.Collections.Generic.IEnumerator`1" /> that can be used to iterate through the collection.</returns>
        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < _count; i++) yield return this[i];
        }

        /// <summary>Returns an enumerator that iterates through a collection.</summary>
        /// <returns>An <see cref="T:System.Collections.IEnumerator" /> object that can be used to iterate through the collection.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }


        /// <summary>Determines the index of a specific item in the <see cref="T:System.Collections.Generic.IList`1" />.</summary>
        /// <returns>The index of <paramref name="item" /> if found in the list; otherwise, -1.</returns>
        /// <param name="item">The object to locate in the <see cref="T:System.Collections.Generic.IList`1" />.</param>
        public int IndexOf(T item)
        {
            for (var i = 0; i < Count; i++)
            {
                if (this[i].Equals(item)) return i;
            }
            return -1;
        }

        /// <summary>not implemented.  Inserts an item to the <see cref="T:System.Collections.Generic.IList`1" /> at the specified index.</summary>
        /// <param name="index">The zero-based index at which <paramref name="item" /> should be inserted.</param>
        /// <param name="item">The object to insert into the <see cref="T:System.Collections.Generic.IList`1" />.</param>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="index" /> is not a valid index in the <see cref="T:System.Collections.Generic.IList`1" />.</exception>
        /// <exception cref="T:System.NotSupportedException">The <see cref="T:System.Collections.Generic.IList`1" /> is read-only.</exception>
        public void Insert(int index, T item)
        {
            throw new NotImplementedException();
        }

        /// <summary>not implemented.  Removes the <see cref="T:System.Collections.Generic.IList`1" /> item at the specified index.</summary>
        /// <param name="index">The zero-based index of the item to remove.</param>
        /// <exception cref="T:System.ArgumentOutOfRangeException">
        /// <paramref name="index" /> is not a valid index in the <see cref="T:System.Collections.Generic.IList`1" />.</exception>
        /// <exception cref="T:System.NotSupportedException">The <see cref="T:System.Collections.Generic.IList`1" /> is read-only.</exception>
        public void RemoveAt(int index)
        {
            throw new NotImplementedException();
        }
    }
}
