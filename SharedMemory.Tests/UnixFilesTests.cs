using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The rules for SharedMemory's files on Unix: names to file names, and which files may be used. They are pure
    /// functions in every library build, so these run on every OS. The expected long names were computed apart from the
    /// library, in Python, from the rules as written.
    /// </summary>
    [TestClass]
    public class UnixFilesTests
    {
        const uint Uid = 1000;

        [TestMethod]
        [DataRow("TEST", "SharedMemory.u1000.TEST")]
        [DataRow("Local\\TEST", "SharedMemory.u1000.TEST")]
        [DataRow("Global\\TEST", "SharedMemory.global.TEST")]
        [DataRow("a/b%c", "SharedMemory.u1000.a%2Fb%25c")]
        [DataRow("TEST_evt_dataexists", "SharedMemory.u1000.TEST_evt_dataexists")]
        public void FileName_SpecificationSamples(string name, string expected)
        {
            Assert.AreEqual(expected, UnixFiles.FileName(name, Uid));
        }

        [TestMethod]
        [DataRow("global\\TEST", "SharedMemory.u1000.global%5CTEST")]
        [DataRow("GLOBAL\\TEST", "SharedMemory.u1000.GLOBAL%5CTEST")]
        [DataRow("local\\TEST", "SharedMemory.u1000.local%5CTEST")]
        [DataRow("Global/TEST", "SharedMemory.u1000.Global%2FTEST")]
        [DataRow("xGlobal\\TEST", "SharedMemory.u1000.xGlobal%5CTEST")]
        [DataRow("Global\\Local\\TEST", "SharedMemory.global.Local%5CTEST")]
        [DataRow("Local\\Global\\TEST", "SharedMemory.u1000.Global%5CTEST")]
        [DataRow("Global\\", "SharedMemory.global.")]
        [DataRow("Local\\", "SharedMemory.u1000.")]
        public void FileName_ScopePrefixIsCaseSensitiveAndOnlyAtTheStart(string name, string expected)
        {
            Assert.AreEqual(expected, UnixFiles.FileName(name, Uid));
        }

        [TestMethod]
        public void FileName_PerUserNamesCarryTheUid()
        {
            Assert.AreEqual("SharedMemory.u0.TEST", UnixFiles.FileName("TEST", 0));
            Assert.AreEqual("SharedMemory.u4294967294.TEST", UnixFiles.FileName("TEST", 4294967294));
            Assert.AreEqual("SharedMemory.global.TEST", UnixFiles.FileName("Global\\TEST", 0));
        }

        [TestMethod]
        [DataRow('%', "%25")]
        [DataRow('/', "%2F")]
        [DataRow('\\', "%5C")]
        public void FileName_EscapesPercentAndSlashes(char c, string escaped)
        {
            Assert.AreEqual("SharedMemory.u1000.a" + escaped + "b", UnixFiles.FileName("a" + c + "b", Uid));
        }

        [TestMethod]
        public void FileName_EscapesEveryByteBelow0x20()
        {
            for (int b = 0; b < 0x20; b++)
            {
                string expected = "SharedMemory.u1000.a%" + b.ToString("X2") + "b";
                Assert.AreEqual(expected, UnixFiles.FileName("a" + (char)b + "b", Uid), "byte 0x" + b.ToString("X2"));
            }
        }

        [TestMethod]
        [DataRow(" ")]
        [DataRow("\u007F")]
        [DataRow("~")]
        [DataRow(".")]
        [DataRow("..")]
        [DataRow(":*?\"<>|")]
        [DataRow("é")]
        [DataRow("日本")]
        [DataRow("\U0001F600")]
        public void FileName_KeepsEverythingElse(string kept)
        {
            Assert.AreEqual("SharedMemory.u1000.a" + kept + "b", UnixFiles.FileName("a" + kept + "b", Uid));
        }

        [TestMethod]
        public void FileName_Exactly255Bytes_IsKept()
        {
            string expected = "SharedMemory.u1000." + new string('a', 236);
            Assert.AreEqual(255, Encoding.UTF8.GetByteCount(expected));

            Assert.AreEqual(expected, UnixFiles.FileName(new string('a', 236), Uid));
        }

        [TestMethod]
        public void FileName_256Bytes_KeepsFirst200BytesThenHash()
        {
            string actual = UnixFiles.FileName(new string('a', 237), Uid);

            Assert.AreEqual("SharedMemory.u1000." + new string('a', 181) + "~SSIZDDQDPYDTHDENVCRWUVLGJ2", actual);
            Assert.AreEqual(227, Encoding.UTF8.GetByteCount(actual));
        }

        [TestMethod]
        public void FileName_LongNamesSharingTheirFirst200Bytes_StayApart()
        {
            string first = UnixFiles.FileName(new string('a', 240) + "1", Uid);
            string second = UnixFiles.FileName(new string('a', 240) + "2", Uid);

            Assert.AreEqual("SharedMemory.u1000." + new string('a', 181) + "~3WNVJRXJTW2WCHMRYUYMWBWGBB", first);
            Assert.AreEqual("SharedMemory.u1000." + new string('a', 181) + "~PPKW6BQBBD6ZK2BWZKVXRYSG3J", second);
        }

        [TestMethod]
        public void FileName_CharacterAcrossByte200_IsCutBeforeIt()
        {
            // 19 bytes of prefix and 180 of 'a' end at byte 199; U+00E9 is bytes 199 and 200.
            string actual = UnixFiles.FileName(new string('a', 180) + "é" + new string('b', 100), Uid);

            Assert.AreEqual("SharedMemory.u1000." + new string('a', 180) + "~GYNFTPXQ5CTVZSJWJWPYVXIVPQ", actual);
        }

        [TestMethod]
        public void FileName_LongNameIsMeasuredAfterEscaping()
        {
            string actual = UnixFiles.FileName(new string('/', 100), Uid);

            var kept = new StringBuilder("SharedMemory.u1000.");
            for (int i = 0; i < 60; i++)
                kept.Append("%2F");
            Assert.AreEqual(kept + "%~TXXEJMG7E6WSPK6G6B65GQ5NKR", actual);
        }

        [TestMethod]
        public void FileName_LongGlobalName()
        {
            Assert.AreEqual("SharedMemory.global." + new string('g', 180) + "~RMIYJACFEP3JR4T7HJTNABSRJB",
                UnixFiles.FileName("Global\\" + new string('g', 300), Uid));
        }

        [TestMethod]
        public void Base32_Rfc4648Alphabet()
        {
            byte[] foobar = Encoding.ASCII.GetBytes("foobar");

            Assert.AreEqual("MZXW6YTB", UnixFiles.Base32(Encoding.ASCII.GetBytes("fooba"), 8));
            Assert.AreEqual("MZXW6YTBO", UnixFiles.Base32(foobar, 9));
            Assert.AreEqual("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", UnixFiles.Base32(
                new byte[] { 0x00, 0x44, 0x32, 0x14, 0xC7, 0x42, 0x54, 0xB6, 0x35, 0xCF, 0x84, 0x65, 0x3A, 0x56, 0xD7, 0xC6, 0x75, 0xBE, 0x77, 0xDF }, 32));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => UnixFiles.Base32(foobar, 10));
        }

        const uint Regular = 0x8000;
        const uint Directory = 0x4000;
        const uint SymbolicLink = 0xA000;

        [TestMethod]
        [DataRow(true, Uid, Regular | 0x180u)]   // 0600
        [DataRow(true, Uid, Regular | 0x1A0u)]   // 0640
        [DataRow(true, Uid, Regular | 0x1A4u)]   // 0644
        [DataRow(true, Uid, Regular | 0x100u)]   // 0400
        [DataRow(false, Uid, Regular | 0x180u)]
        [DataRow(false, 1001u, Regular | 0x180u)]  // another user's global file
        [DataRow(false, 1001u, Regular | 0x1B6u)]  // 0666
        public void Refusal_UsableFiles(bool perUser, uint owner, uint mode)
        {
            Assert.IsNull(UnixFiles.Refusal(perUser, Uid, owner, mode));
        }

        [TestMethod]
        public void Refusal_PerUserFileOfAnotherUser()
        {
            Assert.AreEqual("it belongs to user 1001, not 1000", UnixFiles.Refusal(true, Uid, 1001, Regular | 0x180));
            Assert.AreEqual("it belongs to user 0, not 1000", UnixFiles.Refusal(true, Uid, 0, Regular | 0x180));
            Assert.AreEqual("it belongs to user 1001, not 1000", UnixFiles.Refusal(true, Uid, 1001, Regular | 0x1B6));
        }

        [TestMethod]
        [DataRow(0x190u, "0620")]
        [DataRow(0x182u, "0602")]
        [DataRow(0x1B6u, "0666")]
        [DataRow(0x9B6u, "4666")]
        public void Refusal_PerUserFileWritableByGroupOrOthers(uint permissions, string shown)
        {
            Assert.AreEqual("group or others can write to it (mode " + shown + ")", UnixFiles.Refusal(true, Uid, Uid, Regular | permissions));
        }

        [TestMethod]
        [DataRow(true, Directory | 0x1C0u)]
        [DataRow(false, Directory | 0x1C0u)]
        [DataRow(true, SymbolicLink | 0x1FFu)]
        [DataRow(false, 0x1000u | 0x180u)]  // FIFO
        public void Refusal_NotARegularFile(bool perUser, uint mode)
        {
            Assert.AreEqual("it is not a regular file", UnixFiles.Refusal(perUser, Uid, Uid, mode));
        }
    }
}
