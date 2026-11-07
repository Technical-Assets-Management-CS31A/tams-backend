using System.Security.Cryptography;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Common;

/// <summary>
/// Files under Media:Path (default /app/media, the compose volume); media_files.storage_path is relative
/// to it. Paths are always generated here, never taken from a client.
/// </summary>
static class MediaStore
{
    public static string Root(IConfiguration cfg) => cfg["Media:Path"] is { Length: > 0 } p ? p : "/app/media";

    public static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>Writes the bytes and adds the media_files row to the caller's unit of work.</summary>
    public static async Task<MediaFile> SaveAsync(TamsDbContext db, IConfiguration cfg, MediaKind kind, string folder,
        byte[] bytes, string mime, string ext, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var rel = $"{folder}/{now:yyyy}/{now:MM}/{id}.{ext}";
        var full = Path.Combine(Root(cfg), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct);

        var media = new MediaFile
        {
            Id = id, Kind = kind, StoragePath = rel, MimeType = mime, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), CreatedAt = now,
        };
        db.MediaFiles.Add(media);
        return media;
    }

    public static async Task<byte[]?> ReadAllAsync(IFormFile? file, long maxBytes, CancellationToken ct)
    {
        if (file is null || file.Length is 0 || file.Length > maxBytes) return null;
        var bytes = new byte[file.Length];
        await using var stream = file.OpenReadStream();
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    public static IResult Serve(IConfiguration cfg, MediaFile? media)
    {
        if (media is null) return Http.Problems.NotFound("MEDIA_NOT_FOUND", "File not found.");
        var full = Path.Combine(Root(cfg), media.StoragePath);
        return File.Exists(full)
            ? Results.File(full, media.MimeType)
            : Http.Problems.NotFound("MEDIA_NOT_FOUND", "The file is missing from storage.");
    }
}
