using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// A file with a secret that lies in the state directory: on Windows encrypted with DPAPI for
/// the current user, outside Windows — plain JSON with permissions 600 (only the owner reads it).
/// Shared code of the session store and the token store.
/// </summary>
internal sealed class ProtectedFile<T>
    where T : class
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly byte[] _entropy;

    public ProtectedFile(string path, string entropy)
    {
        _path = path;
        _entropy = Encoding.UTF8.GetBytes(entropy);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public string FilePath => _path;

    /// <summary>Reads the file. A damaged or foreign file is silently discarded (returns <c>null</c>).</summary>
    public T? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Unprotect(File.ReadAllBytes(_path)));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            // The file was created by another user, is damaged or is outdated by format.
            return null;
        }
    }

    public void Save(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        WriteAtomic(Protect(json));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // The file is busy with another process — its content is invalid anyway.
        }
    }

    /// <summary>Writing through a temporary file, so that a failure does not leave a truncated file.</summary>
    private void WriteAtomic(byte[] payload)
    {
        string temporary = _path + ".tmp";

        // On Unix the permissions are set on creation: a file with a secret must not be accessible to others
        // even for a moment. On Windows access is restricted by the user directory and DPAPI.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(payload);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    private byte[] Protect(byte[] payload) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(payload, _entropy, DataProtectionScope.CurrentUser)
            : payload;

    private byte[] Unprotect(byte[] payload) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(payload, _entropy, DataProtectionScope.CurrentUser)
            : payload;
}
