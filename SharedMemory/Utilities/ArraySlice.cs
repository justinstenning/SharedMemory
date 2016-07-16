using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Permissions;

namespace SharedMemory.Utilities
{
    /// <summary>
    /// Like ArraySegment, but works with any IList, not just array
    /// </summary>
    /// <typeparam name="T">The type that will be stored in the elements of this fixed array buffer.</typeparam>
    [PermissionSet(SecurityAction.LinkDemand)]
    [PermissionSet(SecurityAction.InheritanceDemand)]
    public struct ArraySlice<T> : IList<T>

    {
        private readonly IList<T> _list;
        private readonly int _offset;
        private readonly int _count;

        /// <summary>
        /// No slicing.  Just mirror the array
        /// </summary>
        /// <param name="list"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public ArraySlice(IList<T> list)
        {
            if (list == null)
                throw new ArgumentNullException("list");

            _list = list;
            _offset = 0;
            _count = list.Count;
        }

        /// <summary>
        /// Slice the array.
        /// </summary>
        /// <param name="list"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        /// <exception cref="ArgumentException"></exception>
        public ArraySlice(IList<T> list, int offset, int count)
        {
            if (list == null)
                throw new ArgumentNullException("list");
            if (offset < 0)
                throw new ArgumentOutOfRangeException("offset", "ArgumentOutOfRange_NeedNonNegNum");
            if (count < 0)
                throw new ArgumentOutOfRangeException("count", "ArgumentOutOfRange_NeedNonNegNum");
            if (list.Count - offset < count)
                throw new ArgumentException("Argument_InvalidOffLen");

            _list = list;
            _offset = offset;
            _count = count;
        }

        /// <summary>
        /// 
        /// </summary>
        public IList<T> List
        {
            get
            {
                return _list;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public int Offset
        {
            get
            {
                return _offset;
            }
        }

        public int Count
        {
            get
            {
                return _count;
            }
        }

        public override int GetHashCode()
        {
            return null == _list
                        ? 0
                        : _list.GetHashCode() ^ _offset ^ _count;
        }

        /// <summary>Indicates whether this instance and a specified object are equal.</summary>
        /// <returns>true if <paramref name="obj" /> and this instance are the same type and represent the same value; otherwise, false.</returns>
        /// <param name="obj">Another object to compare to. </param>
        /// <filterpriority>2</filterpriority>
        public override bool Equals(Object obj)
        {
            if (obj is ArraySlice<T>)
                return Equals((ArraySlice<T>)obj);
            else
                return false;
        }

        /// <summary>Indicates whether this instance and a specified object are equal.</summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public bool Equals(ArraySlice<T> obj)
        {
            return obj._list == _list && obj._offset == _offset && obj._count == _count;
        }

        /// <summary>Indicates whether this instance and a specified object are equal.</summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static bool operator ==(ArraySlice<T> a, ArraySlice<T> b)
        {
            return a.Equals(b);
        }

        /// <summary>Indicates whether this instance and a specified object are not equal.</summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static bool operator !=(ArraySlice<T> a, ArraySlice<T> b)
        {
            return !(a == b);
        }

        #region IList<T>

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
                if (_list == null)
                    throw new InvalidOperationException("InvalidOperation_NullArray");
                if (index < 0 || index >= _count)
                    throw new ArgumentOutOfRangeException("index");

                return _list[_offset + index];
            }

            set
            {
                if (_list == null)
                    throw new InvalidOperationException("InvalidOperation_NullArray");
                if (index < 0 || index >= _count)
                    throw new ArgumentOutOfRangeException("index");

                _list[_offset + index] = value;
            }
        }

        /// <summary>Determines the index of a specific item in the <see cref="T:System.Collections.Generic.IList`1" />.</summary>
        /// <returns>The index of <paramref name="item" /> if found in the list; otherwise, -1.</returns>
        /// <param name="item">The object to locate in the <see cref="T:System.Collections.Generic.IList`1" />.</param>
        public int IndexOf(T item)
        {
            if (_list == null)
                throw new InvalidOperationException("InvalidOperation_NullArray");

            for (var i = 0; i < Count; i++)
            {
                if (this[i].Equals(item)) return i;
            }
            return -1;
        }

        void IList<T>.Insert(int index, T item)
        {
            throw new NotSupportedException();
        }

        void IList<T>.RemoveAt(int index)
        {
            throw new NotSupportedException();
        }
        #endregion

        #region ICollection<T>
        bool ICollection<T>.IsReadOnly
        {
            get
            {
                // the indexer setter does not throw an exception although IsReadOnly is true.
                // This is to match the behavior of arrays.
                return true;
            }
        }

        void ICollection<T>.Add(T item)
        {
            throw new NotSupportedException();
        }

        void ICollection<T>.Clear()
        {
            throw new NotSupportedException();
        }

        bool ICollection<T>.Contains(T item)
        {
            if (_list == null)
                throw new InvalidOperationException("InvalidOperation_NullArray");

            return IndexOf(item) >= 0;
        }

        void ICollection<T>.CopyTo(T[] array, int arrayIndex)
        {
            throw new NotSupportedException();
        }

        bool ICollection<T>.Remove(T item)
        {
            throw new NotSupportedException();
        }
        #endregion

        #region IEnumerable<T>
        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            if (_list == null)
                throw new InvalidOperationException("InvalidOperation_NullArray");

            return new ArraySliceEnumerator(this);
        }
        #endregion

        #region IEnumerable
        IEnumerator IEnumerable.GetEnumerator()
        {
            if (_list == null)
                throw new InvalidOperationException("InvalidOperation_NullArray");

            return new ArraySliceEnumerator(this);
        }
        #endregion

        [Serializable]
        private sealed class ArraySliceEnumerator : IEnumerator<T>
        {
            private IList<T> _array;
            private int _start;
            private int _end;
            private int _current;

            internal ArraySliceEnumerator(ArraySlice<T> arraySlice)
            {
                _array = arraySlice._list;
                _start = arraySlice._offset;
                _end = _start + arraySlice._count;
                _current = _start - 1;
            }

            public bool MoveNext()
            {
                if (_current < _end)
                {
                    _current++;
                    return _current < _end;
                }
                return false;
            }

            public T Current
            {
                get
                {
                    if (_current < _start) throw new InvalidOperationException("InvalidOperation_EnumNotStarted");
                    if (_current >= _end) throw new InvalidOperationException("InvalidOperation_EnumEnded");
                    return _array[_current];
                }
            }

            object IEnumerator.Current
            {
                get
                {
                    return Current;
                }
            }

            void IEnumerator.Reset()
            {
                _current = _start - 1;
            }

            public void Dispose()
            {
            }
        }
    }
}
