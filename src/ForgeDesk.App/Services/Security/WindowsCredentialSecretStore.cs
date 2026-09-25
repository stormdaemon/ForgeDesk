using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ForgeDesk.App.Native;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Security;

namespace ForgeDesk.App.Services.Security;

/// <summary>
/// Stores secrets as generic credentials in Windows Credential Manager (UTF-16 blobs, persisted
/// for the local machine and the current user). Secret values are never logged.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const string UserName = "ForgeDesk";

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(CredentialTarget.For(key)));
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        Write(CredentialTarget.For(key), value);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = CredentialTarget.For(key);
        if (!NativeMethods.CredDelete(target, NativeMethods.CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != NativeMethods.ErrorNotFound)
            {
                throw Failure("remove", target, error);
            }
        }

        return Task.CompletedTask;
    }

    private static string? Read(string target)
    {
        if (!NativeMethods.CredRead(target, NativeMethods.CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error == NativeMethods.ErrorNotFound ? null : throw Failure("read", target, error);
        }

        using (handle)
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(handle.DangerousGetHandle());
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)(credential.CredentialBlobSize / sizeof(char)));
        }
    }

    private static void Write(string target, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        if (bytes.Length > NativeMethods.CredMaxCredentialBlobSize)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ForgeException(ErrorKind.InvalidInput, "This secret is too long to be stored in Windows Credential Manager.");
        }

        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        var targetName = Marshal.StringToHGlobalUni(target);
        var userName = Marshal.StringToHGlobalUni(UserName);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeMethods.Credential
            {
                Type = NativeMethods.CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = NativeMethods.CredPersistLocalMachine,
                UserName = userName,
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw Failure("save", target, Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            // Do not leave the secret lying around in unmanaged or managed memory.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetName);
            Marshal.FreeHGlobal(userName);
        }
    }

    private static ForgeException Failure(string verb, string target, int error) =>
        new(ErrorKind.StorageFailure,
            $"ForgeDesk could not {verb} your credentials in Windows Credential Manager.",
            "Make sure you are signed in to Windows with your own account, then try again.",
            $"{target}: {new Win32Exception(error).Message} (error {error})");
}
