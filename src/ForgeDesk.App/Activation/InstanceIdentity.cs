using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ForgeDesk.Core.Common;

namespace ForgeDesk.App.Activation;

/// <summary>
/// Names of the single-instance mutex and activation pipe. They are per Windows user (SID), and
/// per data folder when FORGEDESK_DATA_DIR points elsewhere, so a portable copy or a UI-test
/// instance never forwards its arguments to the user's everyday ForgeDesk.
/// </summary>
internal sealed record InstanceIdentity(string MutexName, string PipeName)
{
    public static InstanceIdentity ForCurrentUser() =>
        Create(CurrentUserKey(), Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable));

    public static InstanceIdentity Create(string userKey, string? dataDirectoryOverride)
    {
        var suffix = Sanitize(string.IsNullOrWhiteSpace(userKey) ? "user" : userKey);
        if (!string.IsNullOrWhiteSpace(dataDirectoryOverride))
        {
            var folder = Path.GetFullPath(dataDirectoryOverride.Trim()).TrimEnd('\\', '/');
            if (OperatingSystem.IsWindows())
            {
                folder = folder.ToUpperInvariant();
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(folder));
            suffix += "." + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        return new InstanceIdentity($"ForgeDesk.SingleInstance.{suffix}", $"ForgeDesk.Activation.{suffix}");
    }

    private static string CurrentUserKey()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User?.Value is { Length: > 0 } sid)
                {
                    return sid;
                }
            }
            catch (System.Security.SecurityException)
            {
                // Fall back to the user name below.
            }
        }

        return Environment.UserName;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return builder.ToString();
    }
}
