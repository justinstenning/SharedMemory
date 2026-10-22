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
    /// The rules for SharedMemory's files on Unix: which file a name maps to, and which files found under a name may be used.
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
            string scope;
            string rest;
            if (IsGlobal(name))
            {
                scope = "global.";
                rest = name.Substring(GlobalScope.Length);
            }
            else
            {
                scope = "u" + uid.ToString(CultureInfo.InvariantCulture) + ".";
                rest = name.StartsWith(LocalScope, StringComparison.Ordinal) ? name.Substring(LocalScope.Length) : name;
            }

            var fileName = new StringBuilder(Prefix.Length + scope.Length + rest.Length);
            fileName.Append(Prefix).Append(scope);
            // Characters from U+0080 up encode to bytes of 0x80 and above, which are never escaped, so escaping characters escapes the bytes.
            foreach (char c in rest)
            {
                if (c == '%' || c == '/' || c == '\\' || c < 0x20)
                    fileName.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                else
                    fileName.Append(c);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(fileName.ToString());
            if (bytes.Length <= MaxFileNameBytes)
                return fileName.ToString();

            int kept = KeptBytes;
            while (kept > 0 && (bytes[kept] & 0xC0) == 0x80)
                kept--;
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
                hash = sha.ComputeHash(bytes);
            return Encoding.UTF8.GetString(bytes, 0, kept) + "~" + Base32(hash, HashCharacters);
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
    }
}
