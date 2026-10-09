using System.Text.Json;

namespace PawprintTagger.Core;

/// <summary>Firefox native messaging: native-endian uint32 length, followed by UTF-8 JSON.</summary>
public static class BrowserMessageCodec
{
    public const int MaximumMessageBytes = 512 * 1024;
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default) where T : class
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1, 3), cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToUInt32(header);
        if (length is 0 or > MaximumMessageBytes) throw new InvalidDataException("The browser message is too large or empty.");
        var buffer = new byte[(int)length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(buffer, Options) ?? throw new InvalidDataException("The browser message is empty.");
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (data.Length > MaximumMessageBytes) throw new InvalidDataException("The reply is too large.");
        await stream.WriteAsync(BitConverter.GetBytes((uint)data.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
