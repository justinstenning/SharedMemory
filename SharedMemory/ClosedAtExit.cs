// SharedMemory (File: SharedMemory\ClosedAtExit.cs)
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
using System.Collections.Generic;

namespace SharedMemory
{
    /// <summary>
    /// What a Unix backend holds that has a close step: a Linux file in /dev/shm, or a macOS lock file and the object it names.
    /// </summary>
    internal interface IClosedAtExit
    {
        /// <summary>
        /// The close step, once, whichever of dispose, the finaliser, and the process's exit comes first. Never throws.
        /// </summary>
        void Close();
    }

    /// <summary>
    /// The files this process holds, whose close step runs when the process exits normally (<see cref="AppDomain.ProcessExit"/>, which .NET raises
    /// when <c>Main</c> returns or on <see cref="Environment.Exit"/>). Without it a buffer disposed just before the process ends, whose dispose its
    /// owner had deferred to another thread, or never disposed, would keep its file until the name is next used or the user logs out, since .NET
    /// runs no finalisers at exit. A process ended by a signal, SIGTERM and SIGINT included, by an unhandled exception, or by a kill still leaves
    /// its files.
    /// </summary>
    /// <remarks>
    /// <para>Only the close step runs. The mappings stay: other threads go on running until the process ends and may still use them.</para>
    /// <para>The references are weak, so that a file nobody disposes is still collected and finalised. They track resurrection, so that a file
    /// collected but not yet finalised when the process exits, after which no finaliser runs, still gets its close step.</para>
    /// </remarks>
    internal static class ClosedAtExit
    {
        static readonly HashSet<WeakReference<IClosedAtExit>> Files = new HashSet<WeakReference<IClosedAtExit>>();

        static ClosedAtExit()
        {
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => CloseAll();
        }

        internal static WeakReference<IClosedAtExit> Add(IClosedAtExit file)
        {
            var entry = new WeakReference<IClosedAtExit>(file, true);
            lock (Files)
                Files.Add(entry);
            return entry;
        }

        internal static void Remove(WeakReference<IClosedAtExit> entry)
        {
            lock (Files)
                Files.Remove(entry);
        }

        static void CloseAll()
        {
            WeakReference<IClosedAtExit>[] entries;
            lock (Files)
            {
                entries = new WeakReference<IClosedAtExit>[Files.Count];
                Files.CopyTo(entries);
            }
            foreach (WeakReference<IClosedAtExit> entry in entries)
            {
                IClosedAtExit file;
                if (entry.TryGetTarget(out file))
                    file.Close();
            }
        }
    }
}
#endif
