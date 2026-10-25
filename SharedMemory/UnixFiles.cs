// SharedMemory (File: SharedMemory\UnixFiles.cs)
// Copyright (c) 2026 Justin Stenning
// http://spazzarama.com
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// The SharedMemory library is inspired by the following Code Project article:
//   "Fast IPC Communication Using Shared Memory and InterlockedCompareExchange"
//   http://www.codeproject.com/Articles/14740/Fast-IPC-Communication-Using-Shared-Memory-and-Int

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SharedMemory
{
    /// <summary>
    /// The rules for SharedMemory's files on Unix: which file a name maps to, and which files found under a name may be used. On Linux the file is the
    /// memory itself, in /dev/shm; on macOS it is a lock file under /var/tmp, which names the <c>shm_open</c> object that holds the memory.
    /// </summary>
    /// <remarks>
    /// Every build compiles this, so that its tests run on every OS. Only the net10.0 build uses it, on Unix.
    /// The file names are a contract between SharedMemory versions on the same machine: two processes find the same buffer through them.
    /// </remarks>
    internal static class UnixFiles
    {
        /// <summary>
        /// The start of every buffer's file name.
        /// </summary>
        internal const string Prefix = "SharedMemory.";

        /// <summary>
        /// The start of the name a new file is written under before it is published under its own.
        /// </summary>
        internal const string TemporaryPrefix = ".SharedMemory.tmp.";

        /// <summary>
        /// The longest file name, in UTF-8 bytes, that Linux and macOS accept.
        /// </summary>
        internal const int MaxFileNameBytes = 255;

        /// <summary>
        /// How many bytes of a longer file name are kept before the hash that replaces the rest.
        /// </summary>
        internal const int KeptBytes = 200;

        /// <summary>
        /// The number of base32 characters of the SHA-256 that follow the kept bytes of a long file name.
        /// </summary>
        internal const int HashCharacters = 26;

        /// <summary>
        /// The folder that holds the macOS lock folders. Not /tmp, whose nightly cleaner deletes files whose three times are all over 3 days old,
        /// held or not, and nothing a holder does moves a lock file's times.
        /// </summary>
        internal const string LockRoot = "/var/tmp/";

        const string GlobalScope = "Global\\";
        const string LocalScope = "Local\\";
        const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        /// <summary>
        /// Whether <paramref name="name"/> is machine-wide: it starts with <c>Global\</c>, case-sensitively, as .NET's named mutexes read it.
        /// Every other name, <c>Local\</c> ones included, belongs to the effective user.
        /// </summary>
        internal static bool IsGlobal(string name)
        {
            return name.StartsWith(GlobalScope, StringComparison.Ordinal);
        }

        /// <summary>
        /// The file name for <paramref name="name"/> in a process whose effective user ID is <paramref name="uid"/>:
        /// <c>SharedMemory.global.{X}</c> for <c>Global\X</c>, and <c>SharedMemory.u{uid}.{X}</c> for <c>Local\X</c> or <c>X</c>.
        /// </summary>
        /// <remarks>
        /// <para><c>{X}</c> is the rest of the name in UTF-8, with <c>%</c>, <c>/</c>, <c>\</c>, and bytes below 0x20 written as <c>%XX</c> in upper-case hex.</para>
        /// <para>A file name longer than 255 bytes keeps its first 200 bytes, cut back to the start of a character if one straddles byte 200, then <c>~</c>,
        /// then the first 26 characters of the RFC 4648 base32 encoding of the SHA-256 of the whole file name's UTF-8 bytes.</para>
        /// </remarks>
        internal static string FileName(string name, uint uid)
        {
            string scope = IsGlobal(name) ? "global." : "u" + uid.ToString(CultureInfo.InvariantCulture) + ".";
            string fileName = Prefix + scope + Escape(name);
            byte[] bytes = Encoding.UTF8.GetBytes(fileName);
            if (bytes.Length <= MaxFileNameBytes)
                return fileName;
            return Kept(bytes) + "~" + Hash(bytes);
        }

        /// <summary>
        /// The macOS lock file name for <paramref name="name"/>, in the folder <see cref="LockFolder"/> gives: <c>{X}~{H}</c>.
        /// </summary>
        /// <remarks>
        /// <para><c>{X}</c> is the rest of the name escaped as in <see cref="FileName"/>, cut back to its first 200 bytes, at the start of a character, when longer.
        /// <c>{H}</c> is the first 26 characters of the RFC 4648 base32 encoding of the SHA-256 of the whole of <c>{X}</c>'s UTF-8 bytes, before any cut.</para>
        /// <para>Every name has the hash, because the volume that holds /var/tmp folds case and Unicode normalisation (and <c>ß</c> with <c>SS</c>): <c>TEST</c>
        /// and <c>test</c>, or <c>café</c> composed and decomposed, would otherwise be one file there, where Windows and Linux keep them apart.</para>
        /// </remarks>
        internal static string LockFileName(string name)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Escape(name));
            return (bytes.Length <= KeptBytes ? Encoding.UTF8.GetString(bytes) : Kept(bytes)) + "~" + Hash(bytes);
        }

        /// <summary>
        /// The folder of the macOS lock file for <paramref name="name"/> in a process whose effective user ID is <paramref name="uid"/>, with its
        /// trailing slash: <c>/var/tmp/.SharedMemory-global/</c> for <c>Global\X</c>, and <c>/var/tmp/.SharedMemory-uid{uid}/</c> for <c>Local\X</c> or <c>X</c>.
        /// </summary>
        internal static string LockFolder(string name, uint uid)
        {
            return LockRoot + (IsGlobal(name) ? ".SharedMemory-global/" : ".SharedMemory-uid" + uid.ToString(CultureInfo.InvariantCulture) + "/");
        }

        /// <summary>
        /// The rest of <paramref name="name"/> once a <c>Global\</c> or <c>Local\</c> prefix is taken off, in UTF-8 terms, with <c>%</c>, <c>/</c>, <c>\</c>,
        /// and characters below 0x20 written as <c>%XX</c> in upper-case hex.
        /// </summary>
        static string Escape(string name)
        {
            string rest;
            if (IsGlobal(name))
                rest = name.Substring(GlobalScope.Length);
            else
                rest = name.StartsWith(LocalScope, StringComparison.Ordinal) ? name.Substring(LocalScope.Length) : name;

            var escaped = new StringBuilder(rest.Length);
            // Characters from U+0080 up encode to bytes of 0x80 and above, which are never escaped, so escaping characters escapes the bytes.
            foreach (char c in rest)
            {
                if (c == '%' || c == '/' || c == '\\' || c < 0x20)
                    escaped.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                else
                    escaped.Append(c);
            }
            return escaped.ToString();
        }

        /// <summary>
        /// The first 200 of <paramref name="bytes"/>, cut back to the start of a character if one straddles byte 200. <paramref name="bytes"/> holds more than 200.
        /// </summary>
        static string Kept(byte[] bytes)
        {
            int kept = KeptBytes;
            while (kept > 0 && (bytes[kept] & 0xC0) == 0x80)
                kept--;
            return Encoding.UTF8.GetString(bytes, 0, kept);
        }

        /// <summary>
        /// The first 26 characters of the base32 encoding of the SHA-256 of <paramref name="bytes"/>.
        /// </summary>
        static string Hash(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
                hash = sha.ComputeHash(bytes);
            return Base32(hash, HashCharacters);
        }

        /// <summary>
        /// The first <paramref name="characters"/> characters of the RFC 4648 base32 encoding of <paramref name="data"/>, upper case, without padding.
        /// </summary>
        internal static string Base32(byte[] data, int characters)
        {
            if (characters * 5 > data.Length * 8)
                throw new ArgumentOutOfRangeException("characters", characters, "More characters than the data has bits for.");

            var text = new char[characters];
            for (int i = 0; i < characters; i++)
            {
                int bit = i * 5;
                int index = bit / 8;
                int window = data[index] << 8 | (index + 1 < data.Length ? data[index + 1] : 0);
                text[i] = Base32Alphabet[window >> (11 - bit % 8) & 31];
            }
            return new string(text);
        }

        /// <summary>
        /// Why a file found under a name must not be used, or null if it may be.
        /// </summary>
        /// <param name="perUser">Whether the name belongs to the effective user (not <c>Global\</c>).</param>
        /// <param name="uid">The effective user ID of this process.</param>
        /// <param name="owner">The user ID that owns the file.</param>
        /// <param name="mode">The file's mode, its type bits included (<c>st_mode</c>).</param>
        /// <remarks>
        /// Every name needs a regular file. A per-user name also needs the file to belong to the effective user, with no write permission for group or others,
        /// so that another user cannot plant or change a buffer this user opens.
        /// </remarks>
        internal static string Refusal(bool perUser, uint uid, uint owner, uint mode)
        {
            const uint TypeMask = 0xF000;     // S_IFMT
            const uint RegularFile = 0x8000;  // S_IFREG
            const uint GroupOrOtherWrite = 0x12; // S_IWGRP | S_IWOTH

            if ((mode & TypeMask) != RegularFile)
                return "it is not a regular file";
            if (!perUser)
                return null;
            if (owner != uid)
                return "it belongs to user " + owner.ToString(CultureInfo.InvariantCulture) + ", not " + uid.ToString(CultureInfo.InvariantCulture);
            if ((mode & GroupOrOtherWrite) != 0)
                return "group or others can write to it (mode " + Convert.ToString((int)(mode & 0xFFF), 8).PadLeft(4, '0') + ")";
            return null;
        }

        /// <summary>
        /// Why a macOS lock folder found under its name must not be used, or null if it may be.
        /// </summary>
        /// <param name="perUser">Whether it is a per-user folder (not the <c>Global\</c> one).</param>
        /// <param name="uid">The effective user ID of this process.</param>
        /// <param name="owner">The user ID that owns the folder.</param>
        /// <param name="mode">The folder's mode, its type bits included, from <c>lstat</c>, so that a symbolic link is seen as one.</param>
        /// <remarks>
        /// A per-user folder must be a folder of the effective user's with mode 0700, so that nobody else can put a lock file in it. The global folder must
        /// be a folder with mode 1777, like /tmp: anyone may add a lock file, and only a file's owner may remove it. Whoever made it first owns it.
        /// </remarks>
        internal static string FolderRefusal(bool perUser, uint uid, uint owner, uint mode)
        {
            const uint TypeMask = 0xF000;      // S_IFMT
            const uint Directory = 0x4000;     // S_IFDIR
            const uint SymbolicLink = 0xA000;  // S_IFLNK

            if ((mode & TypeMask) == SymbolicLink)
                return "it is a symbolic link";
            if ((mode & TypeMask) != Directory)
                return "it is not a folder";
            if (perUser && owner != uid)
                return "it belongs to user " + owner.ToString(CultureInfo.InvariantCulture) + ", not " + uid.ToString(CultureInfo.InvariantCulture);
            uint expected = perUser ? 0x1C0u : 0x3FFu; // 0700, 01777
            if ((mode & 0xFFF) != expected)
                return "its mode is " + Octal(mode) + ", not " + Octal(expected);
            return null;
        }

        static string Octal(uint mode)
        {
            return Convert.ToString((int)(mode & 0xFFF), 8).PadLeft(4, '0');
        }

        /// <summary>
        /// The name of an <c>RpcBuffer</c> channel's election mutex on macOS: <paramref name="mutexName"/>, the name it has elsewhere, then <c>~</c>
        /// and the first 26 characters of the RFC 4648 base32 encoding of the SHA-256 of its UTF-8 bytes, <c>Global\</c> included.
        /// </summary>
        /// <remarks>
        /// .NET keeps its named mutexes in files on the volume that holds /tmp and $TMPDIR, which folds case and Unicode normalisation, so the channels
        /// <c>TEST</c> and <c>test</c> would otherwise share one election. .NET takes 255 UTF-8 bytes after a <c>Global\</c>: where the rest and the
        /// suffix would pass that, the rest keeps its first 200 bytes, cut back to the start of a character, as a long lock file name does.
        /// </remarks>
        internal static string ElectionMutexName(string mutexName)
        {
            string scope = IsGlobal(mutexName) ? GlobalScope : "";
            byte[] rest = Encoding.UTF8.GetBytes(mutexName.Substring(scope.Length));
            string suffix = "~" + Hash(Encoding.UTF8.GetBytes(mutexName));
            if (rest.Length + suffix.Length <= MaxFileNameBytes)
                return mutexName + suffix;
            return scope + Kept(rest) + suffix;
        }
    }
}
