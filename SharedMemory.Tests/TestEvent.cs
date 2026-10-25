using System;
using System.Threading;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// A named event opened through the library's internal NamedEvent, as BufferWithLocks and CircularBuffer open
    /// theirs. On Windows it also disposes the EventWaitHandle, which those classes dispose from their protected
    /// properties. Not in Support: the peer process cannot see the library's internals.
    /// </summary>
    sealed class TestEvent : IDisposable
    {
        readonly NamedEvent namedEvent;

        TestEvent(string name, bool initialState, EventResetMode mode)
        {
            namedEvent = NamedEvent.CreateOrOpen(name, initialState, mode, () => namedEvent.WaitHandle);
        }

        public static TestEvent CreateOrOpen(string name, bool initialState, EventResetMode mode)
        {
            return new TestEvent(name, initialState, mode);
        }

        public bool WaitOne(int millisecondsTimeout)
        {
            return namedEvent.WaitOne(millisecondsTimeout);
        }

        public bool Set()
        {
            return namedEvent.Set();
        }

        public bool Reset()
        {
            return namedEvent.Reset();
        }

        public void Dispose()
        {
            EventWaitHandle handle = namedEvent.WaitHandle;
            namedEvent.Dispose();
            if (handle != null)
                handle.Dispose();
        }
    }
}
