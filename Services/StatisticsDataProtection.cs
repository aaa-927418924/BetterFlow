using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterFlow.Services;

internal sealed class ProtectedStatisticsSnapshot
{
    public long TotalInputCharacters { get; set; }
    public long TotalInputMilliseconds { get; set; }
}

/// <summary>
/// Protects local usage totals with Windows DPAPI and current-user scope.
/// </summary>
internal static class StatisticsDataProtection
{
    private const uint CryptProtectUiForbidden = 0x1;
    private const int MaximumProtectedPayloadBytes = 16 * 1024;
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("Better Flow usage statistics v1");

    public static string Protect(long totalInputCharacters, long totalInputMilliseconds)
    {
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(new ProtectedStatisticsSnapshot
        {
            TotalInputCharacters = Math.Max(0, totalInputCharacters),
            TotalInputMilliseconds = Math.Max(0, totalInputMilliseconds)
        });

        IntPtr inputPointer = IntPtr.Zero;
        IntPtr entropyPointer = IntPtr.Zero;
        DataBlob protectedBlob = default;
        try
        {
            var inputBlob = CreateBlob(clearBytes, out inputPointer);
            var entropyBlob = CreateBlob(OptionalEntropy, out entropyPointer);
            if (!CryptProtectData(
                    ref inputBlob,
                    "Better Flow usage statistics",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out protectedBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var encrypted = new byte[protectedBlob.ByteCount];
            Marshal.Copy(protectedBlob.Data, encrypted, 0, encrypted.Length);
            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            FreeAndZeroLocalBlob(protectedBlob, zeroMemory: false);
            FreeHGlobal(inputPointer, clearBytes.Length);
            FreeHGlobal(entropyPointer, OptionalEntropy.Length);
        }
    }

    public static bool TryUnprotect(string? encodedPayload, out ProtectedStatisticsSnapshot statistics)
    {
        statistics = new ProtectedStatisticsSnapshot();
        if (string.IsNullOrWhiteSpace(encodedPayload) || encodedPayload.Length > MaximumProtectedPayloadBytes * 2)
            return false;

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(encodedPayload);
        }
        catch (FormatException)
        {
            return false;
        }

        if (encrypted.Length is 0 or > MaximumProtectedPayloadBytes)
            return false;

        IntPtr inputPointer = IntPtr.Zero;
        IntPtr entropyPointer = IntPtr.Zero;
        DataBlob clearBlob = default;
        try
        {
            var inputBlob = CreateBlob(encrypted, out inputPointer);
            var entropyBlob = CreateBlob(OptionalEntropy, out entropyPointer);
            if (!CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out clearBlob))
                return false;

            if (clearBlob.ByteCount is <= 0 or > MaximumProtectedPayloadBytes)
                return false;

            var clearBytes = new byte[clearBlob.ByteCount];
            Marshal.Copy(clearBlob.Data, clearBytes, 0, clearBytes.Length);
            try
            {
                var decoded = JsonSerializer.Deserialize<ProtectedStatisticsSnapshot>(clearBytes);
                if (decoded is null || decoded.TotalInputCharacters < 0 || decoded.TotalInputMilliseconds < 0)
                    return false;

                statistics = decoded;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
        finally
        {
            FreeAndZeroLocalBlob(clearBlob, zeroMemory: true);
            FreeHGlobal(inputPointer, encrypted.Length);
            FreeHGlobal(entropyPointer, OptionalEntropy.Length);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes, out IntPtr pointer)
    {
        pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { ByteCount = bytes.Length, Data = pointer };
    }

    private static void FreeHGlobal(IntPtr pointer, int byteCount)
    {
        if (pointer == IntPtr.Zero)
            return;

        if (byteCount > 0)
        {
            var zeros = new byte[byteCount];
            Marshal.Copy(zeros, 0, pointer, byteCount);
        }
        Marshal.FreeHGlobal(pointer);
    }

    private static void FreeAndZeroLocalBlob(DataBlob blob, bool zeroMemory)
    {
        if (blob.Data == IntPtr.Zero)
            return;

        if (zeroMemory && blob.ByteCount is > 0 and <= MaximumProtectedPayloadBytes)
        {
            var zeros = new byte[blob.ByteCount];
            Marshal.Copy(zeros, 0, blob.Data, blob.ByteCount);
        }
        LocalFree(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int ByteCount;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
