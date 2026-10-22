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
using System.IO;
using System.IO.MemoryMappedFiles;

namespace SharedMemory
{
    /// <summary>
    /// Shared memory with a name, which other processes open by that name: the library's own handle on the memory behind <see cref="SharedBuffer"/>.
    /// </summary>
    /// <remarks>
    /// <para>On Windows the memory is a named <see cref="MemoryMappedFile"/>. <see cref="SharedBuffer"/> assigns it to its protected
    /// <c>Mmf</c> field, maps its views from there, and disposes it, as it did before this type existed.</para>
    /// <para>On Linux x64 and arm64 the net10.0 build maps a file in /dev/shm, which this type holds open and removes when the last process
    /// lets go of it (<c>LinuxSharedFile</c>). <see cref="File"/> comes from <c>MemoryMappedFile.CreateFromFile</c>
    /// on a second descriptor of that file, so <see cref="SharedBuffer"/> maps its views as on Windows.</para>
    /// <para>Elsewhere the net10.0 build throws <see cref="PlatformNotSupportedException"/>, naming the platform. The other builds leave it to
    /// <see cref="MemoryMappedFile"/>, which throws its own.</para>
    /// </remarks>
    internal sealed class NamedMapping : IDisposable
    {
#if NET10_0_OR_GREATER
        private LinuxSharedFile _linuxFile;
#endif

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
            if (LinuxSharedFile.IsSupported)
                return Map(LinuxSharedFile.Create(name, size), size);
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
            if (LinuxSharedFile.IsSupported)
                return Map(LinuxSharedFile.Open(name), 0);
            if (!OperatingSystem.IsWindows())
                throw PlatformSupport.NotSupported("named shared memory");
#endif
            return new NamedMapping(MemoryMappedFile.OpenExisting(name));
        }

        /// <summary>
        /// The <see cref="MemoryMappedFile"/>, for <see cref="SharedBuffer"/> to assign to its protected <c>Mmf</c> field.
        /// </summary>
        internal MemoryMappedFile File { get; private set; }

#if NET10_0_OR_GREATER
        /// <summary>
        /// Maps <paramref name="linuxFile"/>, all of it when <paramref name="capacity"/> is 0, and takes it over.
        /// </summary>
        private static NamedMapping Map(LinuxSharedFile linuxFile, long capacity)
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle mapped = null;
            try
            {
                mapped = linuxFile.OpenForMapping();
                var file = MemoryMappedFile.CreateFromFile(mapped, null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                return new NamedMapping(file) { _linuxFile = linuxFile };
            }
            catch
            {
                if (mapped != null)
                    mapped.Dispose();
                linuxFile.Dispose();
                throw;
            }
        }
#endif

        /// <summary>
        /// Releases what this instance holds besides <see cref="File"/>, which <see cref="SharedBuffer"/> disposes. On Windows that is nothing;
        /// on Linux it is the file, which is removed if no other process holds it.
        /// </summary>
        public void Dispose()
        {
#if NET10_0_OR_GREATER
            if (_linuxFile != null)
            {
                _linuxFile.Dispose();
                _linuxFile = null;
            }
#endif
        }
    }
}
