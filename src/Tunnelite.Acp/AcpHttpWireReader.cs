using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Tunnelite.Acp;

internal sealed record ParsedHttpRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);

internal readonly record struct ParsedRequestHeaderBlock(
    string Method,
    string Target,
    Dictionary<string, string> Headers);

internal static class AcpHttpWireReader
{
    public const int ChunkSize = 8192;
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxActionBodyBytes = 256 * 1024;
    private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static async Task<ParsedHttpRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            var accumulated = await ReadHeaderBytesAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (accumulated is null)
            {
                return null;
            }

            var (allAccumulated, headerEndIndex) = accumulated.Value;
            var headerBlock = ParseRequestHeaders(allAccumulated, headerEndIndex);
            if (headerBlock is null)
            {
                return null;
            }

            var (method, rawTarget, headers) = headerBlock.Value;
            var (path, query) = SplitPathAndQuery(rawTarget);
            var bodyStartOffset = headerEndIndex + 4;
            var body = await ReadRequestBodyAsync(
                stream,
                headers,
                allAccumulated,
                bodyStartOffset,
                buffer,
                cancellationToken).ConfigureAwait(false);

            return new ParsedHttpRequest(method, path, query, headers, body);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string? TryGetWebSocketUpgradeKey(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Upgrade", out var upgrade) ||
            !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return headers.TryGetValue("Sec-WebSocket-Key", out var key) && !string.IsNullOrWhiteSpace(key)
            ? key
            : null;
    }

    public static async Task WriteWebSocketHandshakeAsync(
        NetworkStream stream,
        string secWebSocketKey,
        CancellationToken cancellationToken)
    {
        var acceptHash = SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey.Trim() + WebSocketAcceptGuid));
        var acceptKey = Convert.ToBase64String(acceptHash);

        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

        var handshakeBytes = Encoding.ASCII.GetBytes(handshake);
        await stream.WriteAsync(handshakeBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteSseHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const string headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var header =
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:; base-uri 'none'; frame-ancestors 'none'\r\n" +
            "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string? TryGetQueryParam(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && string.Equals(part[..eq], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }
        return null;
    }

    private static async Task<(byte[] AccumulatedBytes, int HeaderEndIndex)?> ReadHeaderBytesAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var headerStream = new MemoryStream();
        var headerEndIndex = -1;
        var totalRead = 0;

        while (headerEndIndex < 0 && totalRead < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            headerStream.Write(buffer, 0, read);
            totalRead += read;

            var currentBytes = headerStream.GetBuffer();
            headerEndIndex = FindHeaderDelimiter(currentBytes.AsSpan(0, (int)headerStream.Length));
        }

        return headerEndIndex < 0 ? null : (headerStream.ToArray(), headerEndIndex);
    }

    private static ParsedRequestHeaderBlock? ParseRequestHeaders(
        byte[] allAccumulated,
        int headerEndIndex)
    {
        var headerText = Encoding.ASCII.GetString(allAccumulated, 0, headerEndIndex);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLineParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLineParts.Length < 2)
        {
            return null;
        }

        var method = requestLineParts[0].ToUpperInvariant();
        var target = requestLineParts[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        return new ParsedRequestHeaderBlock(method, target, headers);
    }

    private static async Task<byte[]> ReadRequestBodyAsync(
        NetworkStream stream,
        IReadOnlyDictionary<string, string> headers,
        byte[] allAccumulated,
        int bodyStartOffset,
        byte[] scratchBuffer,
        CancellationToken cancellationToken)
    {
        var isChunked = headers.TryGetValue("Transfer-Encoding", out var teRaw) &&
                        teRaw.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        if (isChunked)
        {
            return await ReadChunkedBodyAsync(
                stream,
                allAccumulated,
                bodyStartOffset,
                scratchBuffer,
                cancellationToken).ConfigureAwait(false);
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var clRaw) && int.TryParse(clRaw, out var parsedLen))
        {
            contentLength = Math.Clamp(parsedLen, 0, MaxActionBodyBytes);
        }

        if (contentLength <= 0)
        {
            return [];
        }

        return await ReadFixedLengthBodyAsync(
            stream,
            allAccumulated,
            bodyStartOffset,
            contentLength,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFixedLengthBodyAsync(
        NetworkStream stream,
        byte[] allAccumulated,
        int bodyStartOffset,
        int contentLength,
        CancellationToken cancellationToken)
    {
        var alreadyReadBodyBytes = allAccumulated.Length - bodyStartOffset;
        var contentBody = new byte[contentLength];
        var copied = Math.Min(contentLength, Math.Max(0, alreadyReadBodyBytes));
        if (copied > 0)
        {
            Buffer.BlockCopy(allAccumulated, bodyStartOffset, contentBody, 0, copied);
        }

        var remaining = contentLength - copied;
        var offset = copied;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(contentBody.AsMemory(offset, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            offset += read;
            remaining -= read;
        }

        return contentBody;
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        NetworkStream stream,
        byte[] initialBuffer,
        int initialOffset,
        byte[] scratchBuffer,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var reader = new ChunkedStreamBuffer(stream, initialBuffer, initialOffset, scratchBuffer);

        while (output.Length < MaxActionBodyBytes)
        {
            var sizeLine = await reader.ReadLineCrLfAsync(cancellationToken).ConfigureAwait(false);
            if (sizeLine is null)
            {
                break;
            }

            var semi = sizeLine.IndexOf(';');
            var hexPart = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hexPart, NumberStyles.HexNumber, null, out var chunkLen) || chunkLen < 0)
            {
                break;
            }

            if (chunkLen == 0)
            {
                await reader.ReadLineCrLfAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            if (!await reader.CopyChunkToAsync(output, chunkLen, MaxActionBodyBytes, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        return output.ToArray();
    }

    private static int FindHeaderDelimiter(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i <= span.Length - 4; i++)
        {
            if (span[i] == (byte)'\r' &&
                span[i + 1] == (byte)'\n' &&
                span[i + 2] == (byte)'\r' &&
                span[i + 3] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static (string Path, string Query) SplitPathAndQuery(string rawTarget)
    {
        var qIndex = rawTarget.IndexOf('?');
        return qIndex < 0
            ? (rawTarget, string.Empty)
            : (rawTarget[..qIndex], rawTarget[(qIndex + 1)..]);
    }

    private sealed class ChunkedStreamBuffer
    {
        private readonly NetworkStream _stream;
        private readonly byte[] _scratchBuffer;
        private readonly List<byte> _tail;
        private int _readPos;

        public ChunkedStreamBuffer(
            NetworkStream stream,
            byte[] initialBuffer,
            int initialOffset,
            byte[] scratchBuffer)
        {
            _stream = stream;
            _scratchBuffer = scratchBuffer;
            _tail = new List<byte>(Math.Max(256, initialBuffer.Length - initialOffset));
            for (var i = initialOffset; i < initialBuffer.Length; i++)
            {
                _tail.Add(initialBuffer[i]);
            }
        }

        public async Task<string?> ReadLineCrLfAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                for (var i = _readPos; i <= _tail.Count - 2; i++)
                {
                    if (_tail[i] == (byte)'\r' && _tail[i + 1] == (byte)'\n')
                    {
                        var len = i - _readPos;
                        var chars = new char[len];
                        for (var c = 0; c < len; c++)
                        {
                            chars[c] = (char)_tail[_readPos + c];
                        }
                        _readPos = i + 2;
                        CompactIfConsumed();
                        return new string(chars);
                    }
                }

                if (!await ReadMoreFromStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
            }
        }

        public async Task<bool> CopyChunkToAsync(
            MemoryStream output,
            int chunkLen,
            int maxOutputBytes,
            CancellationToken cancellationToken)
        {
            while (_tail.Count - _readPos < chunkLen + 2)
            {
                if (!await ReadMoreFromStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            for (var i = 0; i < chunkLen && output.Length < maxOutputBytes; i++)
            {
                output.WriteByte(_tail[_readPos + i]);
            }

            _readPos += chunkLen + 2;
            CompactIfConsumed();
            return true;
        }

        private async Task<bool> ReadMoreFromStreamAsync(CancellationToken cancellationToken)
        {
            var read = await _stream.ReadAsync(_scratchBuffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            for (var i = 0; i < read; i++)
            {
                _tail.Add(_scratchBuffer[i]);
            }
            return true;
        }

        private void CompactIfConsumed()
        {
            if (_readPos > 0 && _readPos == _tail.Count)
            {
                _tail.Clear();
                _readPos = 0;
            }
            else if (_readPos >= 4096 && _readPos >= _tail.Count / 2)
            {
                _tail.RemoveRange(0, _readPos);
                _readPos = 0;
            }
        }
    }
}
