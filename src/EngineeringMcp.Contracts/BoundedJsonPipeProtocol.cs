using System.Buffers.Binary;
using System.Text.Json;

namespace EngineeringMcp.Contracts;

public static class BoundedJsonPipeProtocol
{
    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        int maxPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > maxPayloadBytes)
            throw new InvalidDataException($"Framed JSON payload exceeds the {maxPayloadBytes}-byte safety limit.");

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        int maxPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maxPayloadBytes)
            throw new InvalidDataException($"Invalid framed JSON payload length '{length}'.");

        var payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload);
    }
}
