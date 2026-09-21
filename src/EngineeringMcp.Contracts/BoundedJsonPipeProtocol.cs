using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EngineeringMcp.Contracts;

/// <summary>
/// Bounded framed-JSON pipe protocol plus the shared IPC constants used by every
/// diagnostic probe pipe (request/response byte limits, canonical pipe names, and the
/// constant-time token comparison). Kept beside the protocol so client and server
/// implementations cannot drift apart.
/// </summary>
public static class BoundedJsonPipeProtocol
{
    /// <summary>Maximum framed request size accepted by the ASP.NET backend probe pipe.</summary>
    public const int BackendProbeMaxRequestBytes = 32 * 1024;

    /// <summary>Maximum framed response size accepted by the ASP.NET backend probe client.</summary>
    public const int BackendProbeMaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>Maximum framed request size accepted by the WPF probe pipe.</summary>
    public const int WpfProbeMaxRequestBytes = 64 * 1024;

    /// <summary>Maximum framed response size accepted by the WPF probe client.</summary>
    public const int WpfProbeMaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Pipe name of the in-process ASP.NET backend probe adapter for a target process.</summary>
    public static string AspNetProbePipeName(int processId) => $"EngineeringMcp.AspNetProbe.{processId}";

    /// <summary>Pipe name of the in-process WPF probe for a target process.</summary>
    public static string WpfProbePipeName(int processId) => $"EngineeringMcp.WpfProbe.{processId}";

    /// <summary>Length-safe constant-time comparison for probe bearer tokens.</summary>
    public static bool FixedTimeEquals(string supplied, string expected)
    {
        var a = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

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
