#if HAS_RPC_BUFFER
using System;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// An RpcBuffer that tells which end of its channel it became, and answers every call with the payload, its own increment
    /// added to each byte, so that a caller can tell which end answered. Compiled into the tests and the RPC peer.
    /// </summary>
    public sealed class RpcEndpoint : RpcBuffer
    {
        public RpcEndpoint(string name, byte increment)
            : base(name, (Func<ulong, byte[], byte[]>)((id, payload) => Add(payload, increment)), bufferCapacity: 4096)
        {
        }

        /// <summary>
        /// Whether this instance won the election. The owner writes into the buffer named {name}_Slave_SharedMemory_MMF, the opener into {name}_Master_SharedMemory_MMF.
        /// </summary>
        public bool IsChannelOwner
        {
            get { return WriteBuffer.Name.EndsWith("_Slave_SharedMemory_MMF", StringComparison.Ordinal); }
        }

        public static byte[] Add(byte[] payload, byte increment)
        {
            var answer = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
                answer[i] = (byte)(payload[i] + increment);
            return answer;
        }
    }
}
#endif
