using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SharedMemory
{
    [System.Security.SuppressUnmanagedCodeSecurity]
    internal class UnsafeNativeMethods
    {
        private UnsafeNativeMethods() { }

        #region Native Imports

        /// <summary>
        /// Allow copying memory from one IntPtr to another. Required as the <see cref="System.Runtime.InteropServices.Marshal.Copy(System.IntPtr, System.IntPtr[], int, int)"/> implementation does not provide an appropriate override.
        /// </summary>
        /// <param name="dest"></param>
        /// <param name="src"></param>
        /// <param name="count"></param>
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        internal static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        #endregion

    }
}
