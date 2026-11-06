using ApiAggregation.Domain.Common.Results;
using ApiAggregation.Infrastructure.Providers;
using System.Buffers;
using System.Text.Json;

namespace ApiAggregation.Infrastructure.Http;

/// <summary>
/// Reads JSON response bodies with a one MiB limit, including bodies without a declared length.
/// </summary>
public static class BoundedJsonReader
{
    private const int MaxBodyBytes = 1_048_576;
    private const int ReadBufferSize = 16_384;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads and deserializes JSON while enforcing the response size limit.
    /// </summary>
    /// <typeparam name="T">The expected JSON payload type.</typeparam>
    /// <param name="content">The response content to read.</param>
    /// <param name="cancellationToken">Cancels body reading and deserialization.</param>
    /// <returns>The deserialized value or a failure for oversized or invalid JSON.</returns>
    public static async Task<Result<T>> ReadAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (content.Headers.ContentLength > MaxBodyBytes)
            return AppResult.Failure<T>(ProviderErrors.ResponseTooLarge);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

        try
        {
            while (true)
            {
                // Read one byte past the limit to distinguish an exact fit from an oversized body.
                var remaining = MaxBodyBytes - (int)body.Length;
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(ReadBufferSize, remaining + 1)), cancellationToken);

                if (read == 0)
                    break;

                if (read > remaining)
                    return AppResult.Failure<T>(ProviderErrors.ResponseTooLarge);

                body.Write(buffer, 0, read);
            }

            body.Position = 0;
            var value = await JsonSerializer.DeserializeAsync<T>(body, JsonOptions, cancellationToken);

            return value is null
                ? AppResult.Failure<T>(ProviderErrors.InvalidPayload)
                : AppResult.Success(value);
        }
        catch (JsonException)
        {
            return AppResult.Failure<T>(ProviderErrors.InvalidPayload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
