using GameHelper;
using GameOffsets.Natives;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RitualHelper
{
    internal static class Mem
    {
        private static IntPtr handle = IntPtr.Zero;
        private static int handlePid;

        private static void EnsureHandle()
        {
            int pid = (int)Core.Process.Pid;
            if (handle != IntPtr.Zero && handlePid == pid)
                return;
            Close();
            handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
            handlePid = pid;
        }

        public static void Close()
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
            handlePid = 0;
        }

        public static T Read<T>(IntPtr address) where T : unmanaged
        {
            if (address == IntPtr.Zero) return default;
            EnsureHandle();
            T result = default;
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemory(handle, address, ref result);
            return result;
        }

        public static byte[] ReadBytes(IntPtr address, int count)
        {
            if (address == IntPtr.Zero || count <= 0 || count > 4096)
                return Array.Empty<byte>();
            EnsureHandle();
            var buf = new byte[count];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(handle, address, buf);
            return buf;
        }

        public static string ReadStdWString(IntPtr addr)
        {
            if (addr == IntPtr.Zero) return string.Empty;
            
            var wstr = Read<StdWString>(addr);
            if (wstr.Length <= 0 || wstr.Length > 1000 || wstr.Capacity < 0) return string.Empty;

            var byteLength = wstr.Length * 2;
            byte[] bytes;

            // Small String Optimization (SSO)
            if (wstr.Capacity <= 8)
            {
                // String is stored directly in the struct memory where the pointers would normally be.
                // We read directly from the struct's address space.
                bytes = ReadBytes(addr, byteLength);
            }
            else
            {
                if (wstr.Buffer == IntPtr.Zero) return string.Empty;
                bytes = ReadBytes(wstr.Buffer, byteLength);
            }

            if (bytes == null || bytes.Length == 0) return string.Empty;
            return Encoding.Unicode.GetString(bytes);
        }

        public static string ReadWideString(IntPtr address, int maxChars)
        {
            if (address == IntPtr.Zero || maxChars <= 0)
                return string.Empty;
            var bytes = ReadBytes(address, maxChars * 2);
            if (bytes.Length == 0)
                return string.Empty;
            var s = Encoding.Unicode.GetString(bytes);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        public static string ReadAsciiString(IntPtr address, int maxChars)
        {
            if (address == IntPtr.Zero || maxChars <= 0)
                return string.Empty;
            var bytes = ReadBytes(address, maxChars);
            if (bytes.Length == 0)
                return string.Empty;
            int z = Array.IndexOf(bytes, (byte)0);
            return z >= 0 ? Encoding.ASCII.GetString(bytes, 0, z) : Encoding.ASCII.GetString(bytes);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
