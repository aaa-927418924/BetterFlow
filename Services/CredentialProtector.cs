using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AutoPrompt.Services;

internal static class CredentialProtector
{
    private const uint UiForbidden = 0x1;

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var clearBytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var protectedBytes = Transform(clearBytes, protect: true);
            try { return Convert.ToBase64String(protectedBytes); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(clearBytes); }
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return string.Empty;

        var encryptedBytes = Convert.FromBase64String(protectedValue);
        try
        {
            var clearBytes = Transform(encryptedBytes, protect: false);
            try { return Encoding.UTF8.GetString(clearBytes); }
            finally { CryptographicOperations.ZeroMemory(clearBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(encryptedBytes); }
    }

    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new DataBlob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            var succeeded = protect
                ? CryptProtectData(ref input, "Auto Prompt API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), "APIキーをWindowsのユーザー保護領域で処理できませんでした。");

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size);
                Marshal.FreeHGlobal(input.Data);
            }
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
