namespace ForgeDesk.App.Services.Security;

/// <summary>Maps secret keys to Credential Manager target names ("ForgeDesk:github.com").</summary>
internal static class CredentialTarget
{
    public const string Prefix = "ForgeDesk:";

    public static string For(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var trimmed = key.Trim();
        return trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? trimmed : Prefix + trimmed;
    }
}
