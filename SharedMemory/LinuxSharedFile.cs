// SharedMemory (File: SharedMemory\LinuxSharedFile.cs)
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
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using static SharedMemory.LinuxNativeMethods;

namespace SharedMemory
{
    /// <summary>
    /// A buffer's file in /dev/shm on Linux, which this process holds open, with a shared <c>flock</c>, until it is disposed or finalised.
    /// </summary>
    /// <remarks>
    /// <para>The file names come from <see cref="UnixFiles.FileName"/>. Every process that has a buffer open holds a shared lock on its file, so that a file
    /// nobody holds a lock on is one whose last holder died without closing it. The rules give the same lifetime as a named mapping on Windows:</para>
    /// <list type="bullet">
    /// <item>Create: write the whole file, 0600 and preallocated, under a temporary name, take the shared lock, and publish it with <c>link()</c>, which fails if
    /// the name is taken. A taken name whose file nobody holds is removed and the link tried again; otherwise the name is in use.</item>
    /// <item>Open: a file nobody holds is removed, and the name does not exist. Otherwise take the shared lock, then check that the name still leads to the
    /// same file, which a process removing it may have unlinked meanwhile.</item>
    /// <item>Close: whoever gets an exclusive lock was the last holder, and removes the file.</item>
    /// <item>Exit: when the process exits normally, the close step runs for every file it still holds, disposed or not, as Windows closes a
    /// process's handles at exit (<see cref="ClosedAtExit"/>). The mappings stay until the process ends.</item>
    /// <item>Create-or-open, for events: a file somebody holds is opened. Anything else is created, its contents written before it is published, and a
    /// file nobody holds is removed first rather than opened, as a Windows event is gone once its last handle has closed.</item>
    /// </list>
    /// <para>A file is only ever removed by a process holding its exclusive lock, after checking that the name still leads to it, so a newer file published
    /// under the same name is never removed by mistake. Converting a shared lock to an exclusive one gives the shared lock up first, so a closer that fails
    /// to get the exclusive lock holds nothing, as it should.</para>
    /// <para>The lock and the mapping use two open file descriptions of the file (<see cref="OpenForMapping"/>). A lock lasts until the last reference to its
    /// description goes, and a mapping is such a reference. When a multithreaded process is killed, its mappings can go after it has been reaped (up to 47 ms
    /// later in a test of 300 kills), so a lock taken on the mapped description outlived its process and made a dead owner's buffer look in use.</para>
    /// </remarks>
    internal sealed unsafe class LinuxSharedFile : IDisposable, IClosedAtExit
    {
        const string Folder = "/dev/shm/";

        /// <summary>
        /// How many times a create or an open starts again when the file under the name changes underneath it, before it gives up with an <see cref="IOException"/>.
        /// </summary>
        const int Attempts = 100;

        readonly string _path;
        readonly Statx _identity;
        readonly object _closing = new object();
        WeakReference<IClosedAtExit> _held;
        SafeFileHandle _handle;

        LinuxSharedFile(string path, Statx identity, SafeFileHandle handle)
        {
            _path = path;
            _identity = identity;
            _handle = handle;
        }

        /// <summary>
        /// Runs the close step for a buffer that was never disposed, as Windows releases a named mapping whose handles are finalised.
        /// </summary>
        ~LinuxSharedFile()
        {
            Close();
        }

        /// <summary>
        /// Whether this backend runs here: Linux on x64 or arm64.
        /// </summary>
        [SupportedOSPlatformGuard("linux")]
        internal static bool IsSupported
        {
            get
            {
                return OperatingSystem.IsLinux()
                    && (RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64);
            }
        }

        /// <summary>
        /// The file's path in /dev/shm.
        /// </summary>
        internal string FilePath
        {
            get { return _path; }
        }

        /// <summary>
        /// The file's size when this instance created or opened it.
        /// </summary>
        internal long Length
        {
            get { return (long)_identity.Size; }
        }

        /// <summary>
        /// Reads from the file at <paramref name="offset"/> through the locked descriptor. Unlike a mapping, a read leaves no reference behind for the lock
        /// to outlive.
        /// </summary>
        /// <returns>The number of bytes read, fewer than asked for only at the end of the file.</returns>
        internal int Read(Span<byte> buffer, long offset)
        {
            return RandomAccess.Read(_handle, buffer, offset);
        }

        /// <summary>
        /// Has the close step run when the process exits normally, if nothing has run it by then (<see cref="ClosedAtExit"/>). The owner calls it once it has
        /// finished with <see cref="Read"/> and <see cref="OpenForMapping"/>, which use the locked descriptor that the close step closes.
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
        /// Opens the file again, as a new open file description that holds no lock, for the caller to map and close. See the remarks on the class for why
        /// the mapping must not use the locked one.
        /// </summary>
        internal SafeFileHandle OpenForMapping()
        {
            string path = "/proc/self/fd/" + _handle.DangerousGetHandle();
            int fd;
            int error;
            do
            {
                fd = LinuxNativeMethods.Open(path, O_RDWR | O_CLOEXEC);
                error = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (error == EINTR);
            if (fd < 0)
                throw Failure(error, "open for mapping", _path);
            return new SafeFileHandle((IntPtr)fd, true);
        }

        /// <summary>
        /// Creates the file for <paramref name="name"/>, <paramref name="size"/> bytes of zeros.
        /// </summary>
        /// <exception cref="IOException">The name is in use, or /dev/shm has no room for the file.</exception>
        /// <exception cref="UnauthorizedAccessException">A file under the name belongs to another user or is not private to this one.</exception>
        [SupportedOSPlatform("linux")]
        internal static LinuxSharedFile Create(string name, long size)
        {
            uint uid = GetEffectiveUserId();
            string path = Folder + UnixFiles.FileName(name, uid);
            bool perUser = !UnixFiles.IsGlobal(name);
            string temporary = Folder + UnixFiles.TemporaryPrefix + UnixFiles.Base32(RandomNumberGenerator.GetBytes(17), 26);

            SafeFileHandle handle = CreateTemporary(temporary, name, size);
            try
            {
                Statx created = Status(handle, temporary);
                for (int attempt = 1; ; attempt++)
                {
                    if (Link(temporary, path) == 0)
                        return new LinuxSharedFile(path, created, handle);
                    int error = Marshal.GetLastPInvokeError();
                    if (error != EEXIST)
                        throw Failure(error, "publish", path);
                    if (attempt == Attempts)
                        throw KeptChanging(name, path);

                    // The name is taken, by a buffer in use or by a file whose last holder died without closing it.
                    Statx status;
                    SafeFileHandle existing = OpenFile(path, name, perUser, uid, out status);
                    if (existing == null)
                        continue;
                    using (existing)
                    {
                        if (TryLock(existing, LOCK_EX | LOCK_NB, path))
                        {
                            // Nobody holds it: remove it, and publish this one.
                            RemoveIfNamed(path, status, true);
                            continue;
                        }

                        // Somebody holds it. It is in use, unless that somebody is removing it: wait for a shared lock to find out.
                        Lock(existing, LOCK_SH, path);
                        if (IsNamed(path, status))
                        {
                            // In use. Let go of it as any holder does, which removes it if its holders closed meanwhile.
                            CloseStep(existing, path, status);
                            throw new IOException("The shared memory name '" + name + "' is already in use (" + path + ").");
                        }
                    }
                }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            finally
            {
                Unlink(temporary);
            }
        }

        /// <summary>
        /// Opens the file another instance created for <paramref name="name"/>.
        /// </summary>
        /// <exception cref="FileNotFoundException">No buffer exists under the name.</exception>
        /// <exception cref="UnauthorizedAccessException">The file under the name belongs to another user or is not private to this one.</exception>
        [SupportedOSPlatform("linux")]
        internal static LinuxSharedFile Open(string name)
        {
            uint uid = GetEffectiveUserId();
            string path = Folder + UnixFiles.FileName(name, uid);
            bool perUser = !UnixFiles.IsGlobal(name);

            for (int attempt = 1; ; attempt++)
            {
                Statx status;
                SafeFileHandle handle = OpenFile(path, name, perUser, uid, out status);
                if (handle == null)
                    throw NotFound(name, path);
                try
                {
                    if (TryLock(handle, LOCK_EX | LOCK_NB, path))
                    {
                        // Nobody holds it: its last holder died without closing it. Remove it, as that holder would have.
                        RemoveIfNamed(path, status, false);
                        throw NotFound(name, path);
                    }
                    Lock(handle, LOCK_SH, path);
                    if (IsNamed(path, status))
                        return new LinuxSharedFile(path, status, handle);
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
        /// Opens the file somebody holds under <paramref name="name"/>, or else creates it holding <paramref name="contents"/>, as a named event is created
        /// or opened on Windows. A file nobody holds was left by a process that died holding it: it is removed and the file created afresh, not opened.
        /// </summary>
        /// <remarks>Only a create writes a temporary file, so opening a file somebody holds needs no room in /dev/shm.</remarks>
        /// <exception cref="IOException">/dev/shm has no room for a new file.</exception>
        /// <exception cref="UnauthorizedAccessException">The file under the name belongs to another user or is not private to this one.</exception>
        [SupportedOSPlatform("linux")]
        internal static LinuxSharedFile CreateOrOpen(string name, byte[] contents)
        {
            uint uid = GetEffectiveUserId();
            string path = Folder + UnixFiles.FileName(name, uid);
            bool perUser = !UnixFiles.IsGlobal(name);
            string temporary = null;
            SafeFileHandle created = null;
            Statx createdStatus = default;
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    Statx foundStatus;
                    SafeFileHandle found = OpenFile(path, name, perUser, uid, out foundStatus);
                    if (found != null)
                    {
                        try
                        {
                            if (!TryLock(found, LOCK_EX | LOCK_NB, path))
                            {
                                // Somebody holds it: open it, unless it was removed while this process waited for its lock.
                                Lock(found, LOCK_SH, path);
                                if (IsNamed(path, foundStatus))
                                {
                                    var opened = new LinuxSharedFile(path, foundStatus, found);
                                    found = null;
                                    return opened;
                                }
                            }
                            else
                            {
                                // Nobody holds it: its last holder died without closing it. Remove it, as that holder would have, and create a new one.
                                RemoveIfNamed(path, foundStatus, true);
                            }
                        }
                        finally
                        {
                            if (found != null)
                                found.Dispose();
                        }
                    }

                    // Nothing anybody holds is under the name: publish a new file, written in full first.
                    if (created == null)
                    {
                        temporary = Folder + UnixFiles.TemporaryPrefix + UnixFiles.Base32(RandomNumberGenerator.GetBytes(17), 26);
                        created = CreateTemporary(temporary, name, contents.Length);
                        RandomAccess.Write(created, contents, 0);
                        createdStatus = Status(created, temporary);
                    }
                    if (Link(temporary, path) == 0)
                    {
                        var published = new LinuxSharedFile(path, createdStatus, created);
                        created = null;
                        return published;
                    }
                    int error = Marshal.GetLastPInvokeError();
                    if (error != EEXIST)
                        throw Failure(error, "publish", path);
                    // Another process published one first: open it.
                    if (attempt == Attempts)
                        throw KeptChanging(name, path);
                }
            }
            finally
            {
                if (created != null)
                    created.Dispose();
                if (temporary != null)
                    Unlink(temporary);
            }
        }

        void IClosedAtExit.Close()
        {
            Close();
        }

        /// <summary>
        /// Lets go of the file, removing it if this was its last holder.
        /// </summary>
        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// The close step, once, whichever of dispose, the finaliser, and the process's exit comes first. The others wait for it to finish, so that
        /// the process does not exit while another thread is halfway through it, and then find nothing left to close.
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
                CloseStep(handle, _path, _identity);
                handle.Dispose();
            }
        }

        /// <summary>
        /// Creates the file under <paramref name="temporary"/>: 0600, <paramref name="size"/> bytes allocated so that a full /dev/shm fails here rather than with
        /// SIGBUS on first touch, and locked shared.
        /// </summary>
        [SupportedOSPlatform("linux")]
        static SafeFileHandle CreateTemporary(string temporary, string name, long size)
        {
            FileStream stream;
            try
            {
                // FileStream takes its own shared flock for FileShare.ReadWrite on this descriptor; the library's own LOCK_SH below is the same lock.
                stream = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = 0,
                    PreallocationSize = size,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            }
            catch (IOException e)
            {
                // FileStream has deleted the file. Its message names the temporary file, which means nothing to the caller.
                throw new IOException("Could not create shared memory '" + name + "' of " + size + " bytes in " + Folder + ": " + e.Message, e);
            }

            try
            {
                // Preallocation leaves the length at 0.
                stream.SetLength(size);
            }
            catch
            {
                stream.Dispose();
                Unlink(temporary);
                throw;
            }

            // This instance keeps the descriptor after the stream is gone.
            SafeFileHandle handle = stream.SafeFileHandle;
            GC.SuppressFinalize(stream);
            try
            {
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
        /// Opens the file under <paramref name="path"/> and checks that this process may use it, or returns null if no file is there.
        /// </summary>
        static SafeFileHandle OpenFile(string path, string name, bool perUser, uint uid, out Statx status)
        {
            int fd;
            int error;
            do
            {
                fd = LinuxNativeMethods.Open(path, O_RDWR | O_CLOEXEC);
                error = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (error == EINTR);
            if (error == ENOENT)
            {
                status = default;
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
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        static Statx Status(SafeFileHandle handle, string path)
        {
            Statx status;
            if (StatxDescriptor(handle, "", AT_EMPTY_PATH, StatxFields, &status) != 0)
                throw Failure(Marshal.GetLastPInvokeError(), "read the status of", path);
            if ((status.Mask & StatxFields) != StatxFields)
                throw new IOException("Could not read the owner, mode, and inode of " + path + ".");
            return status;
        }

        /// <summary>
        /// Whether <paramref name="path"/> still leads to the file <paramref name="identity"/> describes.
        /// </summary>
        static bool IsNamed(string path, Statx identity)
        {
            Statx now;
            return StatxPath(AT_FDCWD, path, 0, StatxFields, &now) == 0
                && now.Inode == identity.Inode && now.DeviceMajor == identity.DeviceMajor && now.DeviceMinor == identity.DeviceMinor;
        }

        /// <summary>
        /// Removes <paramref name="path"/> if it still leads to the file <paramref name="identity"/> describes. The caller holds that file's exclusive lock,
        /// so no other process can replace it in between.
        /// </summary>
        static void RemoveIfNamed(string path, Statx identity, bool throwOnError)
        {
            if (!IsNamed(path, identity) || Unlink(path) == 0)
                return;
            int error = Marshal.GetLastPInvokeError();
            if (throwOnError && error != ENOENT)
                throw Failure(error, "remove the abandoned file", path);
        }

        /// <summary>
        /// The close step, which never throws: if this was the file's last holder, remove the file.
        /// </summary>
        static void CloseStep(SafeFileHandle handle, string path, Statx identity)
        {
            int result;
            do
                result = Flock(handle, LOCK_EX | LOCK_NB);
            while (result != 0 && Marshal.GetLastPInvokeError() == EINTR);
            if (result == 0)
                RemoveIfNamed(path, identity, false);
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
            return new IOException("The file for shared memory '" + name + "' kept changing while this process tried to use it (" + path + ").");
        }

        static Exception Failure(int error, string action, string path)
        {
            string message = "Could not " + action + " " + path + ": " + Marshal.GetPInvokeErrorMessage(error);
            if (error == EACCES || error == EPERM)
                return new UnauthorizedAccessException(message);
            return new IOException(message, error);
        }
    }
}
#endif
