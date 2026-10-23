// SharedMemory (File: SharedMemory\MacSharedObject.cs)
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

#if NET10_0_OR_GREATER
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static SharedMemory.MacNativeMethods;

namespace SharedMemory
{
    /// <summary>
    /// A buffer's memory on macOS: an <c>shm_open</c> object, mapped, and the lock file under /var/tmp that names it, which this process holds with a
    /// shared <c>flock</c> until it is disposed or finalised.
    /// </summary>
    /// <remarks>
    /// <para>An object cannot be locked, listed, or told apart from a newer one of the same name: <c>flock</c> on it gives <c>ENOTSUP</c>, and its
    /// <c>fstat</c> has no inode. So the rules of <see cref="LinuxSharedFile"/> run on the lock file, whose name comes from <see cref="UnixFiles.LockFileName"/>,
    /// and the object goes with it. Each object gets a new random name, which only its lock file records, so an abandoned object is never mistaken for a
    /// live one.</para>
    /// <list type="bullet">
    /// <item>Create: write the lock file in full under a temporary name, naming a new object; create the object, 0600, size it, and map it; take the shared
    /// lock, and publish the lock file with <c>link()</c>, which fails if the name is taken. A taken name whose lock file nobody holds is removed, object
    /// first, and the link tried again; otherwise the name is in use, and the new object is removed.</item>
    /// <item>Open: a lock file nobody holds is removed with its object, and the name does not exist. Otherwise take the shared lock, check that the name
    /// still leads to the same file, and open and map the whole of the object it names.</item>
    /// <item>Close: let go of the shared lock, then whoever gets an exclusive lock was the last holder, and removes the object and then the lock file.</item>
    /// <item>Exit: when the process exits normally, the close step runs for every lock file it still holds, as on Linux (<see cref="ClosedAtExit"/>).</item>
    /// </list>
    /// <para>The lock file is written before its object exists and removed after its object is gone, so every object has a file that names it: a process
    /// killed partway through a create leaves a temporary file naming the object, and one killed partway through a close leaves a lock file nobody holds,
    /// which the next create or open of the name removes with its object.</para>
    /// <para>The lock file's descriptor is never mapped, as on Linux, where a lock on a mapped descriptor outlived a killed process. On the Mac a killed
    /// holder's lock was free at once either way.</para>
    /// <para>The lock file holds, little-endian: the magic 0x4B4C4D53 (<c>SMLK</c>), the format version 1, the length of the object's name, each a uint32,
    /// then the name in ASCII, with no terminator. A lock file of another version is refused with an <see cref="IOException"/> naming it, and left alone.</para>
    /// </remarks>
    internal sealed unsafe class MacSharedObject : IDisposable, IClosedAtExit
    {
        const uint Magic = 0x4B4C4D53;
        const uint Version = 1;
        const int HeaderSize = 12;

        /// <summary>
        /// The longest object name macOS accepts, the leading slash included.
        /// </summary>
        const int MaxObjectName = 31;

        /// <summary>
        /// How many times a create or an open starts again when the lock file under the name changes underneath it, before it gives up with an
        /// <see cref="IOException"/>.
        /// </summary>
        const int Attempts = 100;

        /// <summary>
        /// How long a process waits for a global lock folder that another process has made but not yet opened to everyone.
        /// </summary>
        const int FolderWait = 1000;

        const ushort PrivateFile = 0x180;       // 0600
        const ushort PerUserFolder = 0x1C0;     // 0700
        const ushort GlobalFolder = 0x3FF;      // 01777

        readonly string _path;
        readonly Stat _identity;
        readonly string _objectName;
        readonly object _closing = new object();
        WeakReference<IClosedAtExit> _held;
        SafeFileHandle _handle;

        MacSharedObject(string path, Stat identity, string objectName, SafeFileHandle handle, Mapping memory)
        {
            _path = path;
            _identity = identity;
            _objectName = objectName;
            _handle = handle;
            Memory = memory;
        }

        /// <summary>
        /// Runs the close step for a buffer that was never disposed, as Windows releases a named mapping whose handles are finalised.
        /// </summary>
        ~MacSharedObject()
        {
            Close();
        }

        /// <summary>
        /// Whether this backend runs here: macOS on arm64 or x64. Only arm64 has been run.
        /// </summary>
        [SupportedOSPlatformGuard("macos")]
        internal static bool IsSupported
        {
            get
            {
                return OperatingSystem.IsMacOS()
                    && (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || RuntimeInformation.ProcessArchitecture == Architecture.X64);
            }
        }

        /// <summary>
        /// The object, mapped: as many bytes as the creator asked for, or for an opener the whole object, a whole number of pages.
        /// Disposing this instance unmaps it once nobody holds a pointer into it.
        /// </summary>
        internal Mapping Memory { get; private set; }

        /// <summary>
        /// Has the close step run when the process exits normally, if nothing has run it by then (<see cref="ClosedAtExit"/>).
        /// </summary>
        internal void CloseAtExit()
        {
            lock (_closing)
            {
                if (_handle != null && _held == null)
                    _held = ClosedAtExit.Add(this);
            }
        }

        /// <summary>
        /// Creates the object for <paramref name="name"/>, <paramref name="size"/> bytes of zeros, and its lock file.
        /// </summary>
        /// <exception cref="IOException">The name is in use, or there is no memory for the object.</exception>
        /// <exception cref="UnauthorizedAccessException">The lock folder or a lock file under the name is not private to this user.</exception>
        [SupportedOSPlatform("macos")]
        internal static MacSharedObject Create(string name, long size)
        {
            uint uid = GetEffectiveUserId();
            bool perUser = !UnixFiles.IsGlobal(name);
            string folder = Folder(name, uid, perUser, true);
            string path = folder + UnixFiles.LockFileName(name);
            string temporary = folder + UnixFiles.TemporaryPrefix + RandomName();
            string objectName = "/SM" + RandomName();

            SafeFileHandle handle = CreateLockFile(temporary, objectName);
            Mapping memory = null;
            try
            {
                memory = CreateObject(objectName, name, size);
                Stat created = Status(handle, temporary);
                for (int attempt = 1; ; attempt++)
                {
                    if (Link(temporary, path) == 0)
                        return new MacSharedObject(path, created, objectName, handle, memory);
                    int error = Marshal.GetLastPInvokeError();
                    if (error != EEXIST)
                        throw Failure(error, "publish", path);
                    if (attempt == Attempts)
                        throw KeptChanging(name, path);

                    // The name is taken, by a buffer in use or by a lock file whose last holder died without closing it.
                    Stat status;
                    string existingObject;
                    SafeFileHandle existing = OpenLockFile(path, name, perUser, uid, out status, out existingObject);
                    if (existing == null)
                        continue;
                    using (existing)
                    {
                        if (TryLock(existing, LOCK_EX | LOCK_NB, path))
                        {
                            // Nobody holds it: remove it and its object, and publish this one.
                            RemoveIfNamed(path, status, existingObject, true);
                            continue;
                        }

                        // Somebody holds it. It is in use, unless that somebody is removing it: wait for a shared lock to find out.
                        Lock(existing, LOCK_SH, path);
                        if (IsNamed(path, status))
                        {
                            // In use. Let go of it as any holder does, which removes it if its holders closed meanwhile.
                            CloseStep(existing, path, status, existingObject);
                            throw new IOException("The shared memory name '" + name + "' is already in use (" + path + ").");
                        }
                    }
                }
            }
            catch
            {
                if (memory != null)
                {
                    memory.Dispose();
                    ShmUnlink(objectName);
                }
                handle.Dispose();
                throw;
            }
            finally
            {
                Unlink(temporary);
            }
        }

        /// <summary>
        /// Opens the object another instance created for <paramref name="name"/>, through its lock file.
        /// </summary>
        /// <exception cref="FileNotFoundException">No buffer exists under the name.</exception>
        /// <exception cref="UnauthorizedAccessException">The lock folder or the lock file under the name is not private to this user.</exception>
        [SupportedOSPlatform("macos")]
        internal static MacSharedObject Open(string name)
        {
            uint uid = GetEffectiveUserId();
            bool perUser = !UnixFiles.IsGlobal(name);
            string folder = Folder(name, uid, perUser, false);
            string path = (folder ?? UnixFiles.LockFolder(name, uid)) + UnixFiles.LockFileName(name);
            if (folder == null)
                throw NotFound(name, path);

            for (int attempt = 1; ; attempt++)
            {
                Stat status;
                string objectName;
                SafeFileHandle handle = OpenLockFile(path, name, perUser, uid, out status, out objectName);
                if (handle == null)
                    throw NotFound(name, path);
                try
                {
                    if (TryLock(handle, LOCK_EX | LOCK_NB, path))
                    {
                        // Nobody holds it: its last holder died without closing it. Remove it, as that holder would have.
                        RemoveIfNamed(path, status, objectName, false);
                        throw NotFound(name, path);
                    }
                    Lock(handle, LOCK_SH, path);
                    if (IsNamed(path, status))
                    {
                        Mapping memory;
                        try
                        {
                            memory = OpenObject(objectName, name, path);
                        }
                        catch
                        {
                            CloseStep(handle, path, status, objectName);
                            throw;
                        }
                        return new MacSharedObject(path, status, objectName, handle, memory);
                    }
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }

                // Removed while this process waited for its lock: look again.
                handle.Dispose();
                if (attempt == Attempts)
                    throw KeptChanging(name, path);
            }
        }

        /// <summary>
        /// Lets go of the object and its lock file, removing both if this was their last holder, and unmaps the object.
        /// </summary>
        public void Dispose()
        {
            Close();
            Memory.Dispose();
            GC.SuppressFinalize(this);
        }

        void IClosedAtExit.Close()
        {
            Close();
        }

        /// <summary>
        /// The close step, once, whichever of dispose, the finaliser, and the process's exit comes first. The others wait for it to finish, so that
        /// the process does not exit while another thread is halfway through it, and then find nothing left to close. The mapping is left alone.
        /// </summary>
        void Close()
        {
            lock (_closing)
            {
                SafeFileHandle handle = _handle;
                _handle = null;
                if (handle == null)
                    return;
                if (_held != null)
                    ClosedAtExit.Remove(_held);
                if (handle.IsClosed)
                    return;
                CloseStep(handle, _path, _identity, _objectName);
                handle.Dispose();
            }
        }

        static string RandomName()
        {
            return UnixFiles.Base32(RandomNumberGenerator.GetBytes(17), 26);
        }

        /// <summary>
        /// The lock folder for <paramref name="name"/>, with its trailing slash, after checking that this process may use it (<see cref="UnixFiles.FolderRefusal"/>).
        /// With <paramref name="create"/>, makes it if it is missing; without, returns null if it is missing.
        /// </summary>
        /// <remarks>
        /// <c>mkdir</c> applies the umask, so a new folder is given its mode with <c>chmod</c> afterwards. A global folder of this user's with another mode
        /// was left so by a process of this user's killed between the two, or changed by hand: it is given 1777 again, so that a create or open finishes what
        /// that one began. Another user's process may be between its two calls, so another user's global folder with the wrong mode is looked at again
        /// every 10 ms for up to a second before it is refused.
        /// </remarks>
        static string Folder(string name, uint uid, bool perUser, bool create)
        {
            string folder = UnixFiles.LockFolder(name, uid);
            // Without the slash, so that lstat describes a symbolic link rather than what it leads to.
            string path = folder.Substring(0, folder.Length - 1);
            ushort mode = perUser ? PerUserFolder : GlobalFolder;
            if (create)
            {
                if (Mkdir(path, mode) == 0)
                {
                    if (Chmod(path, mode) != 0)
                        throw Failure(Marshal.GetLastPInvokeError(), "set the mode of", path);
                }
                else
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != EEXIST)
                        throw Failure(error, "make", path);
                }
            }

            var waited = Stopwatch.StartNew();
            bool repaired = false;
            while (true)
            {
                Stat status;
                if (LinkStatus(path, &status) != 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == ENOENT && !create)
                        return null;
                    throw Failure(error, "read the status of", path);
                }
                string refusal = UnixFiles.FolderRefusal(perUser, uid, status.Uid, status.Mode);
                if (refusal == null)
                    return folder;
                bool isFolder = (status.Mode & 0xF000) == 0x4000;
                if (!perUser && isFolder && status.Uid == uid && !repaired)
                {
                    // This user's: only this user or root can rename or replace it in /var/tmp, so the path still names the folder lstat saw.
                    if (Chmod(path, GlobalFolder) != 0)
                        throw Failure(Marshal.GetLastPInvokeError(), "set the mode of", path);
                    repaired = true;
                    continue;
                }
                if (perUser || !isFolder || waited.ElapsedMilliseconds >= FolderWait)
                    throw new UnauthorizedAccessException("SharedMemory will not use " + path + " for '" + name + "': " + refusal + ".");
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Writes the lock file naming <paramref name="objectName"/> under <paramref name="temporary"/>, 0600, and takes its shared lock.
        /// </summary>
        static SafeFileHandle CreateLockFile(string temporary, string objectName)
        {
            var contents = new byte[HeaderSize + objectName.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(contents, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(4), Version);
            BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(8), (uint)objectName.Length);
            Encoding.ASCII.GetBytes(objectName, 0, objectName.Length, contents, HeaderSize);

            int fd;
            int error;
            do
            {
                fd = MacNativeMethods.Open(temporary, O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC, PrivateFile);
                error = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (error == EINTR);
            if (fd < 0)
                throw Failure(error, "create", temporary);

            var handle = new SafeFileHandle((IntPtr)fd, true);
            try
            {
                RandomAccess.Write(handle, contents, 0);
                Lock(handle, LOCK_SH | LOCK_NB, temporary);
                return handle;
            }
            catch
            {
                handle.Dispose();
                Unlink(temporary);
                throw;
            }
        }

        /// <summary>
        /// Creates the object <paramref name="objectName"/>, 0600 and <paramref name="size"/> bytes, and maps it. On failure nothing is left behind.
        /// </summary>
        static Mapping CreateObject(string objectName, string name, long size)
        {
            int fd = ShmOpen(objectName, O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC, PrivateFile);
            if (fd < 0)
                throw Failure(Marshal.GetLastPInvokeError(), "create the object " + objectName + " for", "'" + name + "'");
            try
            {
                // An object can be sized once only.
                if (Ftruncate(fd, size) != 0)
                    throw NoMemory(name, size, Marshal.GetLastPInvokeError());
                nint address = Mmap(0, (nuint)size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
                if (address == -1)
                    throw NoMemory(name, size, Marshal.GetLastPInvokeError());
                return Mapping.Of(address, size);
            }
            catch
            {
                ShmUnlink(objectName);
                throw;
            }
            finally
            {
                // The mapping outlives the descriptor.
                MacNativeMethods.Close(fd);
            }
        }

        /// <summary>
        /// Opens and maps the whole of the object <paramref name="objectName"/>, which the lock file <paramref name="path"/> names. Its size is a whole
        /// number of pages; <see cref="SharedBuffer"/> reads the buffer's own size from its header.
        /// </summary>
        /// <exception cref="FileNotFoundException">The object is gone, though its lock file is held: launchd removes a user's objects when the user logs out.</exception>
        static Mapping OpenObject(string objectName, string name, string path)
        {
            int fd = ShmOpen(objectName, O_RDWR | O_CLOEXEC, 0);
            if (fd < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == ENOENT)
                    throw new FileNotFoundException("No shared memory named '" + name + "' exists: " + path + " names " + objectName + ", which is gone.", path);
                throw Failure(error, "open the object " + objectName + " for", "'" + name + "'");
            }
            try
            {
                Stat status;
                if (FileStatus(fd, &status) != 0)
                    throw Failure(Marshal.GetLastPInvokeError(), "read the status of", objectName);
                nint address = Mmap(0, (nuint)status.Size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
                if (address == -1)
                    throw Failure(Marshal.GetLastPInvokeError(), "map", objectName);
                return Mapping.Of(address, status.Size);
            }
            finally
            {
                MacNativeMethods.Close(fd);
            }
        }

        /// <summary>
        /// Opens the lock file under <paramref name="path"/>, checks that this process may use it, and reads the name of the object it names, or returns
        /// null if no file is there.
        /// </summary>
        static SafeFileHandle OpenLockFile(string path, string name, bool perUser, uint uid, out Stat status, out string objectName)
        {
            int fd;
            int error;
            do
            {
                fd = MacNativeMethods.Open(path, O_RDWR | O_CLOEXEC, 0);
                error = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (error == EINTR);
            if (error == ENOENT)
            {
                status = default;
                objectName = null;
                return null;
            }
            if (fd < 0)
                throw Failure(error, "open", path);

            var handle = new SafeFileHandle((IntPtr)fd, true);
            try
            {
                status = Status(handle, path);
                string refusal = UnixFiles.Refusal(perUser, uid, status.Uid, status.Mode);
                if (refusal != null)
                    throw new UnauthorizedAccessException("SharedMemory will not use " + path + " for '" + name + "': " + refusal + ".");
                objectName = ReadObjectName(handle, path, name);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The name of the object the lock file names. A lock file never changes once published, so it can be read before it is locked.
        /// </summary>
        static string ReadObjectName(SafeFileHandle handle, string path, string name)
        {
            Span<byte> contents = stackalloc byte[HeaderSize + MaxObjectName + 1];
            int length = 0;
            int read;
            while (length < contents.Length && (read = RandomAccess.Read(handle, contents.Slice(length), length)) > 0)
                length += read;

            if (length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(contents) != Magic)
                throw NotALockFile(path, name);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(contents.Slice(4));
            if (version != Version)
                throw new IOException("The lock file " + path + " for '" + name + "' has format version " + version + "; this SharedMemory reads version " + Version + ".");
            uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(contents.Slice(8));
            if (nameLength < 2 || nameLength > MaxObjectName || length != HeaderSize + nameLength || contents[HeaderSize] != '/')
                throw NotALockFile(path, name);
            Span<byte> objectName = contents.Slice(HeaderSize, (int)nameLength);
            foreach (byte b in objectName)
            {
                if (b <= 0x20 || b >= 0x7F)
                    throw NotALockFile(path, name);
            }
            return Encoding.ASCII.GetString(objectName);
        }

        static Stat Status(SafeFileHandle handle, string path)
        {
            Stat status;
            if (FileStatus(handle, &status) != 0)
                throw Failure(Marshal.GetLastPInvokeError(), "read the status of", path);
            return status;
        }

        /// <summary>
        /// Whether <paramref name="path"/> still leads to the file <paramref name="identity"/> describes.
        /// </summary>
        static bool IsNamed(string path, Stat identity)
        {
            Stat now;
            return PathStatus(path, &now) == 0 && now.Inode == identity.Inode && now.Device == identity.Device;
        }

        /// <summary>
        /// Removes the object <paramref name="objectName"/> and then the lock file <paramref name="path"/>, if the path still leads to the lock file
        /// <paramref name="identity"/> describes. The caller holds that file's exclusive lock, so no other process can replace it in between.
        /// </summary>
        static void RemoveIfNamed(string path, Stat identity, string objectName, bool throwOnError)
        {
            if (!IsNamed(path, identity))
                return;
            if (ShmUnlink(objectName) != 0)
            {
                // Gone already, or this process may not remove it, in which case its lock file stays too, so that the object is not lost.
                int error = Marshal.GetLastPInvokeError();
                if (error != ENOENT)
                {
                    if (throwOnError)
                        throw Failure(error, "remove the abandoned object " + objectName + " named by", path);
                    return;
                }
            }
            if (Unlink(path) == 0)
                return;
            int unlinkError = Marshal.GetLastPInvokeError();
            if (throwOnError && unlinkError != ENOENT)
                throw Failure(unlinkError, "remove the abandoned file", path);
        }

        /// <summary>
        /// The close step, which never throws: if this was the last holder, remove the object and its lock file.
        /// </summary>
        /// <remarks>
        /// The shared lock goes first. A <c>LOCK_EX | LOCK_NB</c> that fails keeps it on macOS, where Linux drops it inside the call, so two holders
        /// closing at once would each find the other's lock and both leave the lock file and its object. Without it, the second to try finds the
        /// first gone.
        /// </remarks>
        static void CloseStep(SafeFileHandle handle, string path, Stat identity, string objectName)
        {
            int result;
            do
                result = Flock(handle, LOCK_UN);
            while (result != 0 && Marshal.GetLastPInvokeError() == EINTR);
            do
                result = Flock(handle, LOCK_EX | LOCK_NB);
            while (result != 0 && Marshal.GetLastPInvokeError() == EINTR);
            if (result == 0)
                RemoveIfNamed(path, identity, objectName, false);
        }

        static void Lock(SafeFileHandle handle, int operation, string path)
        {
            if (!TryLock(handle, operation, path))
                throw new IOException("Could not lock " + path + ": another process holds it.");
        }

        /// <summary>
        /// Takes the lock, or returns false if <see cref="LOCK_NB"/> was given and another process holds a conflicting one.
        /// </summary>
        static bool TryLock(SafeFileHandle handle, int operation, string path)
        {
            while (Flock(handle, operation) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == EWOULDBLOCK)
                    return false;
                if (error != EINTR)
                    throw Failure(error, "lock", path);
            }
            return true;
        }

        static FileNotFoundException NotFound(string name, string path)
        {
            return new FileNotFoundException("No shared memory named '" + name + "' exists (" + path + ").", path);
        }

        static IOException KeptChanging(string name, string path)
        {
            return new IOException("The lock file for shared memory '" + name + "' kept changing while this process tried to use it (" + path + ").");
        }

        static IOException NotALockFile(string path, string name)
        {
            return new IOException("SharedMemory cannot use " + path + " for '" + name + "': it is not a SharedMemory lock file.");
        }

        static IOException NoMemory(string name, long size, int error)
        {
            return new IOException("Could not create shared memory '" + name + "' of " + size + " bytes: " + Marshal.GetPInvokeErrorMessage(error), error);
        }

        static Exception Failure(int error, string action, string path)
        {
            string message = "Could not " + action + " " + path + ": " + Marshal.GetPInvokeErrorMessage(error);
            if (error == EACCES || error == EPERM)
                return new UnauthorizedAccessException(message);
            return new IOException(message, error);
        }

        /// <summary>
        /// A mapping of an object, which <see cref="SharedBuffer"/> reads and writes through, as it does through a view of a <c>MemoryMappedFile</c>
        /// elsewhere. Releasing it unmaps it, once nobody holds a pointer into it.
        /// </summary>
        internal sealed class Mapping : SafeBuffer
        {
            Mapping()
                : base(true)
            {
            }

            internal static Mapping Of(nint address, long length)
            {
                var mapping = new Mapping();
                mapping.SetHandle(address);
                mapping.Initialize((ulong)length);
                return mapping;
            }

            protected override bool ReleaseHandle()
            {
                return Munmap(handle, (nuint)ByteLength) == 0;
            }
        }
    }
}
#endif
