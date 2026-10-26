#if HAS_RPC_BUFFER
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// RPC packets built and read by hand, for the tests that stand in for a peer at the ring level. The packet format lives
    /// here alone, so that the tests that use it need no change when it does.
    /// </summary>
    internal static class RpcPackets
    {
        /// <summary>
        /// The node size of the channels these tests make, and so the most a packet can take.
        /// </summary>
        public const int Capacity = 256;

        public const int HeaderSize = 32;

        /// <summary>
        /// The payload bytes in each packet of a message but its last.
        /// </summary>
        public const int PacketPayload = Capacity - HeaderSize;

        /// <summary>
        /// One packet as RpcProtocolHeaderV1 lays it out: MsgType at byte 0, bytes 1 to 7 zero, MsgId at 8,
        /// PayloadSize at 16, CurrentPacket at 20, TotalPackets at 22, ResponseId at 24, then the packet's part of the payload.
        /// </summary>
        public static byte[] Packet(MessageType type, ulong msgId, ulong responseId, byte[] payload, int packet, int totalPackets)
        {
            int offset = (packet - 1) * PacketPayload;
            int length = Math.Min(PacketPayload, payload.Length - offset);
            var bytes = new byte[HeaderSize + length];
            bytes[0] = (byte)type;
            BitConverter.GetBytes(msgId).CopyTo(bytes, 8);
            BitConverter.GetBytes(payload.Length).CopyTo(bytes, 16);
            BitConverter.GetBytes((ushort)packet).CopyTo(bytes, 20);
            BitConverter.GetBytes((ushort)totalPackets).CopyTo(bytes, 22);
            BitConverter.GetBytes(responseId).CopyTo(bytes, 24);
            Buffer.BlockCopy(payload, offset, bytes, HeaderSize, length);
            return bytes;
        }

        /// <summary>
        /// Changes the size of the whole message that a packet claims, leaving the rest of the packet as it is.
        /// </summary>
        public static void SetPayloadSize(byte[] packet, int payloadSize)
        {
            BitConverter.GetBytes(payloadSize).CopyTo(packet, 16);
        }

        public static void WritePacket(CircularBuffer ring, byte[] packet)
        {
            Assert.AreEqual(packet.Length, ring.Write(packet, 0, 1000), "the packet did not fit in the ring");
        }

        public struct PacketHeader
        {
            public MessageType Type;
            public ulong MsgId;
            public ulong ResponseId;
            public int Packet;
            public int TotalPackets;
        }

        public static bool TryReadPacket(CircularBuffer ring, int timeoutMs, out PacketHeader header)
        {
            var bytes = new byte[Capacity];
            header = new PacketHeader();
            if (ring.Read(bytes, 0, timeoutMs) < HeaderSize)
            {
                return false;
            }
            header.Type = (MessageType)bytes[0];
            header.MsgId = BitConverter.ToUInt64(bytes, 8);
            header.ResponseId = BitConverter.ToUInt64(bytes, 24);
            header.Packet = BitConverter.ToUInt16(bytes, 20);
            header.TotalPackets = BitConverter.ToUInt16(bytes, 22);
            return true;
        }
    }
}
#endif
