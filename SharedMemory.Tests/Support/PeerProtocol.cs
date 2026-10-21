using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// What the tests and the peer process share: how data is written on the command line, and how
    /// each side identifies the library build it loaded. Compiled into the tests and the peer.
    /// </summary>
    public static class PeerProtocol
    {
        public static string ToHex(byte[] data)
        {
            var hex = new StringBuilder(data.Length * 2);
            foreach (byte b in data)
                hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        public static byte[] FromHex(string hex)
        {
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; i++)
                data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return data;
        }

        /// <summary>
        /// The SHA-256 of the SharedMemory.dll this process loaded, so that the tests can check the
        /// peer runs the same library build as they do.
        /// </summary>
        public static string LibraryHash()
        {
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(typeof(SharedBuffer).Assembly.Location))
                return ToHex(sha.ComputeHash(file));
        }
    }
}
