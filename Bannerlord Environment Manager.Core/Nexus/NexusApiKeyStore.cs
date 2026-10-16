using BannerlordEnvironmentManager.Core.Localization;

namespace BannerlordEnvironmentManager.Core.Nexus;

public enum NexusApiKeyState
{
    NotSet,
    Available,
    Unreadable
}

public sealed record NexusApiKeyStatus(NexusApiKeyState State, string Message, string? Masked = null);

// The key is written encrypted, under local application data, which does not roam and is not the
// folder any settings bundle walks. The ciphertext is bound to this Windows user on this machine by
// the protector, so a copied file is useless.
//
// The only key BEM keeps is the one Nexus sends when the user signs in. Nexus grants single sign-on
// only to an application that does not take personal API keys, so a key pasted into an earlier BEM
// lives under its own file name, is never read, and is deleted at startup.
public sealed class NexusApiKeyStore
{
    public const string FileName = "nexus-signin-key.dat";

    public const string PastedKeyFileName = "nexus-api-key.dat";

    // True only when this call is what deleted a pasted key, so the user is told once.
    public static bool DiscardPastedKey(string directory)
    {
        var path = Path.Combine(directory, PastedKeyFileName);

        try
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly ISecretProtector protector;

    public NexusApiKeyStore(string directory, ISecretProtector protector)
    {
        this.protector = protector;
        FilePath = Path.Combine(directory, FileName);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bannerlord Environment Manager");

    public string FilePath { get; }

    public NexusApiKey? Load()
    {
        if (ReadCipherText() is not { } cipherText)
            return null;

        try
        {
            return NexusApiKey.TryCreate(protector.Unprotect(cipherText));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public NexusApiKeyStatus Status()
    {
        if (ReadCipherText() is null)
            return new NexusApiKeyStatus(NexusApiKeyState.NotSet, Strings.Current["Core.Nexus.ApiKeyStore.NotSet"]);

        return Load() is { } key
            ? new NexusApiKeyStatus(
                NexusApiKeyState.Available,
                Strings.Current.Format("Core.Nexus.ApiKeyStore.Stored", key.Masked),
                key.Masked)
            : new NexusApiKeyStatus(
                NexusApiKeyState.Unreadable,
                Strings.Current["Core.Nexus.ApiKeyStore.Undecryptable"]);
    }

    // The round trip is verified before anything is written. A protector that cannot reverse itself
    // would otherwise leave a file that can never be read again, which reads to the user as the key
    // they signed in for having silently vanished.
    public NexusApiKeyStatus Save(NexusApiKey key)
    {
        byte[] cipherText;

        try
        {
            cipherText = protector.Protect(key.Reveal());

            if (protector.Unprotect(cipherText) != key.Reveal())
                return new NexusApiKeyStatus(
                    NexusApiKeyState.Unreadable,
                    Strings.Current["Core.Nexus.ApiKeyStore.CouldNotRoundTrip"]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new NexusApiKeyStatus(
                NexusApiKeyState.Unreadable,
                Strings.Current.Format("Core.Nexus.ApiKeyStore.CouldNotEncrypt", ex.Message));
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, cipherText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new NexusApiKeyStatus(
                NexusApiKeyState.Unreadable,
                Strings.Current.Format("Core.Nexus.ApiKeyStore.CouldNotWrite", ex.Message));
        }

        return new NexusApiKeyStatus(
            NexusApiKeyState.Available,
            Strings.Current.Format("Core.Nexus.ApiKeyStore.Saved", key.Masked),
            key.Masked);
    }

    public NexusApiKeyStatus Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                return new NexusApiKeyStatus(NexusApiKeyState.NotSet, Strings.Current["Core.Nexus.ApiKeyStore.Removed"]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new NexusApiKeyStatus(
                NexusApiKeyState.Unreadable,
                Strings.Current.Format("Core.Nexus.ApiKeyStore.CouldNotRemove", ex.Message));
        }

        return new NexusApiKeyStatus(NexusApiKeyState.NotSet, Strings.Current["Core.Nexus.ApiKeyStore.NothingToRemove"]);
    }

    private byte[]? ReadCipherText()
    {
        try
        {
            return File.Exists(FilePath) && new FileInfo(FilePath).Length > 0 ? File.ReadAllBytes(FilePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
