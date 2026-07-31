using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ReactorSoftInterlock.Infrastructure.Logging;

public sealed class WindowsExperimentCredentialStore : IExperimentCredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public Task<string?> ReadTokenAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<string?>(null);
        }

        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error, "Windows Credential Manager could not read the GitLab credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var token = Marshal.PtrToStringUni(
                credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
            return Task.FromResult<string?>(token);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public Task StoreTokenAsync(string target, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }

        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "ReactorSoftInterlock"
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows Credential Manager could not store the GitLab credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var index = 0; index < bytes.Length; index++)
            {
                Marshal.WriteByte(blob, index, 0);
            }
            Marshal.FreeCoTaskMem(blob);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(
                    error,
                    "Windows Credential Manager could not clear the GitLab credential.");
            }
        }

        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
