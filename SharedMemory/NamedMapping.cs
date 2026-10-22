// SharedMemory (File: SharedMemory\NamedMapping.cs)
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
using System.IO.MemoryMappedFiles;

namespace SharedMemory
{
    /// <summary>
    /// Shared memory with a name, which other processes open by that name: the library's own handle on the memory behind <see cref="SharedBuffer"/>.
    /// </summary>
    /// <remarks>
    /// <para>On Windows the memory is a named <see cref="MemoryMappedFile"/>. <see cref="SharedBuffer"/> assigns it to its protected
    /// <c>Mmf</c> field, maps its views from there, and disposes it, as it did before this type existed.</para>
    /// <para>Elsewhere the net10.0 build throws <see cref="PlatformNotSupportedException"/>, naming the platform. The other builds leave it to
    /// <see cref="MemoryMappedFile"/>, which throws its own.</para>
    /// </remarks>
    internal sealed class NamedMapping : IDisposable
    {
        private NamedMapping(MemoryMappedFile file)
        {
            File = file;
        }

        /// <summary>
        /// Creates shared memory of <paramref name="size"/> bytes under <paramref name="name"/>.
        /// </summary>
        /// <exception cref="System.IO.IOException">The name is already in use.</exception>
        internal static NamedMapping CreateNew(string name, long size)
        {
#if NET10_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                throw PlatformSupport.NotSupported("named shared memory");
#endif
            return new NamedMapping(MemoryMappedFile.CreateNew(name, size));
        }

        /// <summary>
        /// Opens the shared memory another instance created under <paramref name="name"/>.
        /// </summary>
        /// <exception cref="System.IO.FileNotFoundException">Nothing exists under the name.</exception>
        internal static NamedMapping OpenExisting(string name)
        {
#if NET10_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                throw PlatformSupport.NotSupported("named shared memory");
#endif
            return new NamedMapping(MemoryMappedFile.OpenExisting(name));
        }

        /// <summary>
        /// The <see cref="MemoryMappedFile"/>, for <see cref="SharedBuffer"/> to assign to its protected <c>Mmf</c> field.
        /// </summary>
        internal MemoryMappedFile File { get; private set; }

        /// <summary>
        /// Releases what this instance holds besides <see cref="File"/>, which <see cref="SharedBuffer"/> disposes. On Windows that is nothing.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
