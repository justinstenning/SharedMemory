using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// A <see cref="SharedBuffer"/> that creates no events, so that the lifetime of the shared memory
    /// can be tested apart from the lifetime of named events. Compiled into the tests and the peer.
    /// </summary>
    public class EventFreeBuffer : SharedBuffer
    {
        public EventFreeBuffer(string name, long bufferSize)
            : base(name, bufferSize, true)
        {
            Open();
        }

        public EventFreeBuffer(string name)
            : base(name, 0, false)
        {
            Open();
        }

        public void WriteBytes(byte[] data, long bufferPosition = 0)
        {
            Write(data, bufferPosition);
        }

        public byte[] ReadBytes(int count, long bufferPosition = 0)
        {
            var data = new byte[count];
            Read(data, bufferPosition);
            return data;
        }
    }
}
