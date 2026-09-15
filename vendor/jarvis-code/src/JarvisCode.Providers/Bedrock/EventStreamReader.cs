using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using JarvisCode.Core.Providers;

namespace JarvisCode.Providers.Bedrock;

/// <summary>One decoded application/vnd.amazon.eventstream message.</summary>
internal sealed record EventStreamMessage(IReadOnlyDictionary<string, string> Headers, byte[] Payload)
{
    public string? EventType => Headers.GetValueOrDefault(":event-type");

    public string? MessageType => Headers.GetValueOrDefault(":message-type");
}

/// <summary>
/// Decodes the AWS eventstream binary framing Bedrock streams responses in:
/// [4B total length][4B headers length][4B prelude CRC][headers][payload][4B message CRC].
/// CRCs are not validated — the stream already rides TLS — and only string
/// headers are materialized; other header types are skipped by their size.
/// </summary>
internal static class EventStreamReader
{
    /// <summary>Frames larger than this are corrupt framing, not data (Bedrock chunks are small).</summary>
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async IAsyncEnumerable<EventStreamMessage> ReadMessagesAsync(
        Stream stream,
        string providerName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prelude = new byte[12];
        while (true)
        {
            int first;
            try
            {
                first = await stream.ReadAsync(prelude.AsMemory(0, 1), cancellationToken);
            }
            catch (IOException)
            {
                yield break; // the connection closed between frames — end of stream
            }

            if (first == 0)
                yield break;
            await stream.ReadExactlyAsync(prelude.AsMemory(1, 11), cancellationToken);

            int totalLength = BinaryPrimitives.ReadInt32BigEndian(prelude.AsSpan(0, 4));
            int headersLength = BinaryPrimitives.ReadInt32BigEndian(prelude.AsSpan(4, 4));
            int payloadLength = totalLength - 12 - headersLength - 4;
            if (totalLength is < 16 or > MaxFrameBytes || headersLength < 0 || payloadLength < 0)
                throw new ProviderException($"{providerName}: corrupt event stream frame (length {totalLength}).");

            var body = new byte[headersLength + payloadLength + 4];
            await stream.ReadExactlyAsync(body, cancellationToken);

            yield return new EventStreamMessage(
                ParseHeaders(body.AsSpan(0, headersLength), providerName),
                body.AsSpan(headersLength, payloadLength).ToArray());
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseHeaders(ReadOnlySpan<byte> data, string providerName)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        int offset = 0;
        while (offset < data.Length)
        {
            int nameLength = data[offset++];
            if (offset + nameLength > data.Length)
                throw new ProviderException($"{providerName}: corrupt event stream headers.");
            var name = Encoding.UTF8.GetString(data.Slice(offset, nameLength));
            offset += nameLength;

            if (offset >= data.Length)
                throw new ProviderException($"{providerName}: corrupt event stream headers.");
            byte type = data[offset++];
            switch (type)
            {
                case 0 or 1: // bool true / false — no value bytes
                    break;
                case 2:
                    offset += 1;
                    break;
                case 3:
                    offset += 2;
                    break;
                case 4:
                    offset += 4;
                    break;
                case 5 or 8: // long / timestamp
                    offset += 8;
                    break;
                case 6 or 7: // byte array / string, 2-byte big-endian length
                {
                    if (offset + 2 > data.Length)
                        throw new ProviderException($"{providerName}: corrupt event stream headers.");
                    int valueLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                    offset += 2;
                    if (offset + valueLength > data.Length)
                        throw new ProviderException($"{providerName}: corrupt event stream headers.");
                    if (type == 7)
                        headers[name] = Encoding.UTF8.GetString(data.Slice(offset, valueLength));
                    offset += valueLength;
                    break;
                }
                case 9: // uuid
                    offset += 16;
                    break;
                default:
                    throw new ProviderException($"{providerName}: unknown event stream header type {type}.");
            }
        }

        return headers;
    }
}
