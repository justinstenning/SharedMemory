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
        /// Packet number <paramref name="packet"/>, counting from 1, of a message that an RpcBuffer on these channels sends in
        /// <paramref name="totalPackets"/> packets: its bytes start at (packet - 1) × PacketPayload in the payload, and are
        /// PacketPayload long, or what is left of the payload.
        /// </summary>
        public static byte[] Packet(MessageType type, ulong msgId, ulong responseId, byte[] payload, int packet, int totalPackets)
        {
            int offset = (packet - 1) * PacketPayload;
            int length = Math.Min(PacketPayload, payload.Length - offset);
            var bytes = new byte[length];
            Buffer.BlockCopy(payload, offset, bytes, 0, length);
            return Packet(new PacketHeader
            {
                HeaderLength = HeaderSize,
                Type = type,
                PayloadSize = payload.Length,
                MsgId = msgId,
                ResponseId = responseId,
                PayloadOffset = offset,
                PacketLength = length,
            }, bytes);
        }

        /// <summary>
        /// A packet with the header's fields as given, then <paramref name="afterHeader"/> from byte 32: any header bytes past
        /// byte 32, then the packet's part of the payload. The header as protocol 2 lays it out: HeaderLength at byte 0,
        /// MsgType at 1, Flags at 2, PayloadSize at 4, MsgId at 8, ResponseId at 16, PayloadOffset at 24, PacketLength at 28.
        /// Nothing is checked, so it builds packets that no RpcBuffer sends.
        /// </summary>
        public static byte[] Packet(PacketHeader header, byte[] afterHeader)
        {
            var bytes = new byte[HeaderSize + afterHeader.Length];
            bytes[0] = (byte)header.HeaderLength;
            bytes[1] = (byte)header.Type;
            BitConverter.GetBytes(header.Flags).CopyTo(bytes, 2);
            BitConverter.GetBytes(header.PayloadSize).CopyTo(bytes, 4);
            BitConverter.GetBytes(header.MsgId).CopyTo(bytes, 8);
            BitConverter.GetBytes(header.ResponseId).CopyTo(bytes, 16);
            BitConverter.GetBytes(header.PayloadOffset).CopyTo(bytes, 24);
            BitConverter.GetBytes(header.PacketLength).CopyTo(bytes, 28);
            Buffer.BlockCopy(afterHeader, 0, bytes, HeaderSize, afterHeader.Length);
            return bytes;
        }

        /// <summary>
        /// Changes the size of the whole message that a packet claims, leaving the rest of the packet as it is.
        /// </summary>
        public static void SetPayloadSize(byte[] packet, int payloadSize)
        {
            BitConverter.GetBytes(payloadSize).CopyTo(packet, 4);
        }

        public static void WritePacket(CircularBuffer ring, byte[] packet)
        {
            Assert.AreEqual(packet.Length, ring.Write(packet, 0, 1000), "the packet did not fit in the ring");
        }

        /// <summary>
        /// A packet's header. Packet and TotalPackets, counting from 1, are worked out from the offset and the sizes, as an
        /// RpcBuffer on these channels sends PacketPayload bytes in each packet but the last.
        /// </summary>
        public struct PacketHeader
        {
            public int HeaderLength;
            public MessageType Type;
            public ushort Flags;
            public int PayloadSize;
            public ulong MsgId;
            public ulong ResponseId;
            public int PayloadOffset;
            public int PacketLength;
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
            header.HeaderLength = bytes[0];
            header.Type = (MessageType)bytes[1];
            header.Flags = BitConverter.ToUInt16(bytes, 2);
            header.PayloadSize = BitConverter.ToInt32(bytes, 4);
            header.MsgId = BitConverter.ToUInt64(bytes, 8);
            header.ResponseId = BitConverter.ToUInt64(bytes, 16);
            header.PayloadOffset = BitConverter.ToInt32(bytes, 24);
            header.PacketLength = BitConverter.ToInt32(bytes, 28);
            header.Packet = header.PayloadOffset / PacketPayload + 1;
            header.TotalPackets = header.PayloadSize == 0 ? 1 : (header.PayloadSize + PacketPayload - 1) / PacketPayload;
            return true;
        }
    }
}
#endif
