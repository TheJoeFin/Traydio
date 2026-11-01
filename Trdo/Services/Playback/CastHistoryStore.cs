using System;
using System.IO;
using System.Threading.Tasks;

namespace Trdo.Services.Playback;

internal sealed class CastHistoryStore
{
    private readonly string _path;
    private bool _canWrite = true;

    public CastHistoryStore(string path) => _path = path;

    public async Task<CastHistoryDocument> LoadAsync()
    {
        try
        {
            if (File.Exists(_path))
                return CastHistoryStorageFormat.Parse(await File.ReadAllTextAsync(_path).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                   or FormatException or NotSupportedException)
        {
            _canWrite = false;
            throw;
        }

        return CastHistoryPolicy.CreateDocument();
    }

    public async Task SaveAsync(string json)
    {
        if (!_canWrite)
            throw new IOException("Casting history cannot be saved because the existing file could not be read.");

        string temporary = _path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
