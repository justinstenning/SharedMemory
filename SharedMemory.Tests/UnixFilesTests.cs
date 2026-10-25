using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// The rules for SharedMemory's files on Unix: names to Linux file names and macOS lock file names, and which files
    /// and lock folders may be used. They are pure functions in every library build, so these run on every OS. The
    /// expected long names and hashes were computed apart from the library, in Python, from the rules as written.
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

        const uint MacUid = 502;

        [TestMethod]
        [DataRow("TEST", "/var/tmp/.SharedMemory-uid502/TEST~STXALEZV4WD6KAOMJP4QME7AQF")]
        [DataRow("test", "/var/tmp/.SharedMemory-uid502/test~T6DNBAMIJR6WLGRP5KQMKWWQCW")]
        [DataRow("Local\\TEST", "/var/tmp/.SharedMemory-uid502/TEST~STXALEZV4WD6KAOMJP4QME7AQF")]
        [DataRow("Global\\TEST", "/var/tmp/.SharedMemory-global/TEST~STXALEZV4WD6KAOMJP4QME7AQF")]
        [DataRow("a/b%c", "/var/tmp/.SharedMemory-uid502/a%2Fb%25c~I6CGOVAJQWCGJXCW4RVQ5MO27S")]
        [DataRow("TEST_evt_dataexists", "/var/tmp/.SharedMemory-uid502/TEST_evt_dataexists~SFP4VEMNC5AD2MPUBLL6FURVDV")]
        [DataRow("caf\u00E9", "/var/tmp/.SharedMemory-uid502/caf\u00E9~QUHX3RBZCD7YSD4IPHAO2JX6NF")]
        [DataRow("cafe\u0301", "/var/tmp/.SharedMemory-uid502/cafe\u0301~QHXQMC6NTCW4PASOWXA23KB4GJ")]
        public void LockFile_SpecificationSamples(string name, string expected)
        {
            Assert.AreEqual(expected, UnixFiles.LockFolder(name, MacUid) + UnixFiles.LockFileName(name));
        }

        /// <summary>
        /// The Mac's volume folds case, Unicode normalisation, and ß with SS, so names that Windows and Linux keep apart
        /// must differ in more than those: their hashes, upper-case letters and digits that no folding changes, differ.
        /// </summary>
        [TestMethod]
        [DataRow("TEST", "test")]
        [DataRow("TEST", "Test")]
        [DataRow("test", "Test")]
        [DataRow("caf\u00E9", "cafe\u0301")]
        [DataRow("Stra\u00DFe", "STRASSE")]
        public void LockFileName_NamesTheVolumeFoldsTogether_DifferInTheirHash(string first, string second)
        {
            string firstName = UnixFiles.LockFileName(first);
            string secondName = UnixFiles.LockFileName(second);

            string firstHash = firstName.Substring(firstName.LastIndexOf('~') + 1);
            string secondHash = secondName.Substring(secondName.LastIndexOf('~') + 1);
            Assert.AreNotEqual(firstHash, secondHash);
        }

        [TestMethod]
        [DataRow("Test", "Test~KMXKVPMVOSEA3P3WXG4MYAEDFQ")]
        [DataRow("Stra\u00DFe", "Stra\u00DFe~LCRXPDAYYQLSNTKTYKSMO7OL5W")]
        [DataRow("STRASSE", "STRASSE~XF4ALLYJJFUU2EW6ZBENZUUU73")]
        public void LockFileName_EveryNameHasTheHash(string name, string expected)
        {
            Assert.AreEqual(expected, UnixFiles.LockFileName(name));
        }

        [TestMethod]
        public void LockFolder_LocalPrefixIsTheUsersAndGlobalIsShared()
        {
            Assert.AreEqual("/var/tmp/.SharedMemory-uid502/", UnixFiles.LockFolder("TEST", MacUid));
            Assert.AreEqual("/var/tmp/.SharedMemory-uid502/", UnixFiles.LockFolder("Local\\TEST", MacUid));
            Assert.AreEqual("/var/tmp/.SharedMemory-uid0/", UnixFiles.LockFolder("TEST", 0));
            Assert.AreEqual("/var/tmp/.SharedMemory-global/", UnixFiles.LockFolder("Global\\TEST", MacUid));
            Assert.AreEqual("/var/tmp/.SharedMemory-global/", UnixFiles.LockFolder("Global\\TEST", 0));
            Assert.AreEqual("/var/tmp/.SharedMemory-uid502/", UnixFiles.LockFolder("global\\TEST", MacUid));
            Assert.AreEqual(UnixFiles.LockFileName("TEST"), UnixFiles.LockFileName("Local\\TEST"));
            Assert.AreEqual(UnixFiles.LockFileName("TEST"), UnixFiles.LockFileName("Global\\TEST"));
            Assert.AreEqual("Global%5CTEST~67ZKTU6H3BOIWQLDNOCOELQTZP", UnixFiles.LockFileName("Local\\Global\\TEST"));
        }

        [TestMethod]
        public void LockFileName_200Bytes_IsKeptWhole()
        {
            Assert.AreEqual(new string('a', 200) + "~YKUQRWMPLX4YPLPEDNP44IJQM7", UnixFiles.LockFileName(new string('a', 200)));
        }

        [TestMethod]
        public void LockFileName_201Bytes_KeepsFirst200BytesThenTheHashOfTheWhole()
        {
            string actual = UnixFiles.LockFileName(new string('a', 201));

            Assert.AreEqual(new string('a', 200) + "~VEXP3AQQSNZ6LD42EBLN5YA6QB", actual);
            Assert.AreEqual(227, Encoding.UTF8.GetByteCount(actual));
        }

        [TestMethod]
        public void LockFileName_LongNamesSharingTheirFirst200Bytes_StayApart()
        {
            Assert.AreEqual(new string('a', 200) + "~BNFLUCKD6XTTNKEATJBNX6RSHR", UnixFiles.LockFileName(new string('a', 240) + "1"));
            Assert.AreEqual(new string('a', 200) + "~SIDNZNM5NPIIEL5GJPCE2C4FB3", UnixFiles.LockFileName(new string('a', 240) + "2"));
        }

        [TestMethod]
        public void LockFileName_CharacterAcrossByte200_IsCutBeforeIt()
        {
            // 199 bytes of 'a'; U+00E9 is bytes 199 and 200.
            Assert.AreEqual(new string('a', 199) + "~ZCV24UDPOOEJW6NCDB6QEAF6Y7", UnixFiles.LockFileName(new string('a', 199) + "\u00E9bbb"));
        }

        [TestMethod]
        public void LockFileName_LongNameIsMeasuredAfterEscaping()
        {
            var kept = new StringBuilder();
            for (int i = 0; i < 66; i++)
                kept.Append("%2F");
            Assert.AreEqual(kept + "%2~BXRGTEE63XCDXJCIUTWIYAUKSY", UnixFiles.LockFileName(new string('/', 100)));
        }

        [TestMethod]
        public void LockFileName_LongGlobalName()
        {
            Assert.AreEqual("/var/tmp/.SharedMemory-global/" + new string('g', 200) + "~CUJKN5W3QDZUIXZK6VMY3QBKZZ",
                UnixFiles.LockFolder("Global\\" + new string('g', 300), MacUid) + UnixFiles.LockFileName("Global\\" + new string('g', 300)));
        }

        [TestMethod]
        [DataRow(true, MacUid, Directory | 0x1C0u)]     // 0700
        [DataRow(false, MacUid, Directory | 0x3FFu)]    // 01777
        [DataRow(false, 501u, Directory | 0x3FFu)]      // made first by another user
        [DataRow(false, 0u, Directory | 0x3FFu)]
        public void FolderRefusal_UsableFolders(bool perUser, uint owner, uint mode)
        {
            Assert.IsNull(UnixFiles.FolderRefusal(perUser, MacUid, owner, mode));
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void FolderRefusal_SymbolicLink(bool perUser)
        {
            Assert.AreEqual("it is a symbolic link", UnixFiles.FolderRefusal(perUser, MacUid, MacUid, SymbolicLink | 0x1EDu));
        }

        [TestMethod]
        [DataRow(true, Regular | 0x1C0u)]
        [DataRow(false, Regular | 0x3FFu)]
        [DataRow(true, 0x1000u | 0x1C0u)]  // FIFO
        public void FolderRefusal_NotAFolder(bool perUser, uint mode)
        {
            Assert.AreEqual("it is not a folder", UnixFiles.FolderRefusal(perUser, MacUid, MacUid, mode));
        }

        [TestMethod]
        public void FolderRefusal_PerUserFolderOfAnotherUser()
        {
            Assert.AreEqual("it belongs to user 501, not 502", UnixFiles.FolderRefusal(true, MacUid, 501, Directory | 0x1C0));
            Assert.AreEqual("it belongs to user 0, not 502", UnixFiles.FolderRefusal(true, MacUid, 0, Directory | 0x1C0));
        }

        [TestMethod]
        [DataRow(true, 0x1EDu, "its mode is 0755, not 0700")]
        [DataRow(true, 0x1E8u, "its mode is 0750, not 0700")]
        [DataRow(true, 0x140u, "its mode is 0500, not 0700")]
        [DataRow(true, 0x3C0u, "its mode is 1700, not 0700")]
        [DataRow(false, 0x1EDu, "its mode is 0755, not 1777")]
        [DataRow(false, 0x1FFu, "its mode is 0777, not 1777")]
        [DataRow(false, 0x3EDu, "its mode is 1755, not 1777")]
        [DataRow(false, 0x1C0u, "its mode is 0700, not 1777")]
        public void FolderRefusal_WrongMode(bool perUser, uint permissions, string reason)
        {
            Assert.AreEqual(reason, UnixFiles.FolderRefusal(perUser, MacUid, MacUid, Directory | permissions));
        }

        /// <summary>
        /// The election mutex on macOS: the name RpcBuffer passes elsewhere, <c>~</c>, and 26 base32 characters of its SHA-256,
        /// <c>Global\</c> included, for three names, the hashes computed apart from the library.
        /// </summary>
        [TestMethod]
        [DataRow("TESTSharedMemory_MasterMutex", "TESTSharedMemory_MasterMutex~AJBIHJLAWEI3LCI5HYPHG5KDGG")]
        [DataRow("testSharedMemory_MasterMutex", "testSharedMemory_MasterMutex~X7BXTZHTUMTU5YWIGH2KS6OVUG")]
        [DataRow("Global\\TESTSharedMemory_MasterMutex", "Global\\TESTSharedMemory_MasterMutex~NRNFSN2CF47SS3W2EL2VBW232A")]
        public void ElectionMutexName_TheNameThenItsHash(string mutexName, string expected)
        {
            Assert.AreEqual(expected, UnixFiles.ElectionMutexName(mutexName));
        }

        /// <summary>
        /// .NET takes 255 UTF-8 bytes after a <c>Global\</c>, so 228 before the suffix. A longer name keeps its first 200
        /// bytes, cut back to the start of a character, before the suffix; the hash is still of the whole name.
        /// </summary>
        [TestMethod]
        public void ElectionMutexName_228Bytes_KeptWhole()
        {
            string mutexName = new string('k', 204) + "SharedMemory_MasterMutex";
            Assert.AreEqual(mutexName + "~IVI4Y2T4Q73IDSJFCQLIWESEII", UnixFiles.ElectionMutexName(mutexName));
        }

        [TestMethod]
        public void ElectionMutexName_229Bytes_KeepsTheFirst200()
        {
            string mutexName = new string('k', 205) + "SharedMemory_MasterMutex";
            Assert.AreEqual(new string('k', 200) + "~NYJHFPA54WZVZHLYHQBL7WJTTB", UnixFiles.ElectionMutexName(mutexName));
        }

        [TestMethod]
        public void ElectionMutexName_CutThroughACharacter_CutsBackToItsStart()
        {
            string mutexName = "a" + new string('\u00E9', 120) + "SharedMemory_MasterMutex";
            Assert.AreEqual("a" + new string('\u00E9', 99) + "~S2XAP6I3UKPL2MINSVNWYOE2DS", UnixFiles.ElectionMutexName(mutexName));
        }

        [TestMethod]
        public void ElectionMutexName_LongGlobalName_KeepsThePrefixAnd200BytesAfterIt()
        {
            string mutexName = "Global\\" + new string('g', 230) + "SharedMemory_MasterMutex";
            Assert.AreEqual("Global\\" + new string('g', 200) + "~MONUTEFS52WNIEKWDYCWJXVHSA", UnixFiles.ElectionMutexName(mutexName));
        }
    }
}
