// SharedMemory (File: SharedMemory\PlatformSupport.cs)
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
using System.Runtime.InteropServices;

namespace SharedMemory
{
    /// <summary>
    /// What the net10.0 build tells a caller on a platform it does not support.
    /// </summary>
    internal static class PlatformSupport
    {
        /// <summary>
        /// The exception for a <paramref name="feature"/> this platform lacks, naming the platform, its version, and the process architecture.
        /// </summary>
        /// <param name="feature">What is missing, as it reads after "does not support", for example "named events".</param>
        internal static PlatformNotSupportedException NotSupported(string feature)
        {
            return new PlatformNotSupportedException("SharedMemory does not support " + feature + " on " + Describe() + ".");
        }

        /// <summary>
        /// The exception for a <paramref name="feature"/> this platform lacks, naming the platform, its version, the process architecture, and
        /// <paramref name="reason"/>.
        /// </summary>
        /// <param name="feature">What is missing, as it reads after "does not support".</param>
        /// <param name="reason">Why, as it reads after the platform and a colon.</param>
        internal static PlatformNotSupportedException NotSupported(string feature, string reason)
        {
            return new PlatformNotSupportedException("SharedMemory does not support " + feature + " on " + Describe() + ": " + reason + ".");
        }

        private static string Describe()
        {
            return OperatingSystemName() + " " + Environment.OSVersion.Version + " (" + RuntimeInformation.ProcessArchitecture + ")";
        }

        private static string OperatingSystemName()
        {
            if (OperatingSystem.IsLinux())
                return "Linux";
            if (OperatingSystem.IsMacOS())
                return "macOS";
            if (OperatingSystem.IsMacCatalyst())
                return "Mac Catalyst";
            if (OperatingSystem.IsIOS())
                return "iOS";
            if (OperatingSystem.IsTvOS())
                return "tvOS";
            if (OperatingSystem.IsAndroid())
                return "Android";
            if (OperatingSystem.IsFreeBSD())
                return "FreeBSD";
            if (OperatingSystem.IsBrowser())
                return "the browser";
            if (OperatingSystem.IsWasi())
                return "WASI";
            return RuntimeInformation.OSDescription;
        }
    }
}
#endif
