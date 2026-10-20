using System.Text.RegularExpressions;
using ClinicSaaS.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Infrastructure.Storage;

public sealed class LocalFileStorageOptions
{
    public const string Section = "FileStorage:Local";
    /// <summary>Private directory for attachment bytes, outside any web root. Relative paths resolve against the working directory.</summary>
    public string Root { get; set; } = ".file-storage";
}

/// <summary>
/// Development-only attachment storage on the local disk (Program.cs refuses it in Production). The directory is
/// never served: there is no static-file middleware, and bytes leave only through the authorized download endpoint.
/// Only server-generated keys are accepted, and every resolved path is checked to stay inside the root.
/// </summary>
public sealed partial class LocalDiskFileStorage(IOptions<LocalFileStorageOptions> options) : IFileStorage
{
    // "{tenant:N}/{yyyy}/{MM}/{random:N}" — the only shape AttachmentService generates.
    [GeneratedRegex("^[0-9a-f]{32}/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private string Root => Path.GetFullPath(options.Value.Root);

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = Resolve(key);
        CreateDirectory(Path.GetDirectoryName(path)!);
        // Write to a temporary name, then move without overwriting: a reader never sees a partial file and an
        // existing object is never replaced.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var fileOptions = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = FileMode;
            await using (var file = new FileStream(temp, fileOptions))
                await content.CopyToAsync(file, ct);
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        if (string.IsNullOrEmpty(key) || !KeyPattern().IsMatch(key))
            throw new ArgumentException("Invalid storage key.", nameof(key));
        var root = Root;
        var full = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        // Defense in depth: the pattern already excludes "..", separators and rooted paths.
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Invalid storage key.", nameof(key));
        return full;
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, DirectoryMode);
    }
}
