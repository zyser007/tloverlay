using System.Net.Http;

namespace TLOverlay.Core.Tests;

/// <summary>
/// Serves canned responses so download and release-resolution tests never touch
/// the network. Also records the requests, which is how the resume tests assert
/// that a Range header actually went out.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_responder(request));
    }
}

/// <summary>
/// Hands out its payload a chunk at a time and lets the test act partway
/// through, which is how cancellation mid-transfer gets exercised.
/// </summary>
internal sealed class ChunkedStream : Stream
{
    private readonly byte[] _payload;
    private readonly int _chunkSize;
    private readonly Action<int>? _afterChunk;
    private int _position;

    public ChunkedStream(byte[] payload, int chunkSize, Action<int>? afterChunk = null)
    {
        _payload = payload;
        _chunkSize = chunkSize;
        _afterChunk = afterChunk;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _payload.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int remaining = _payload.Length - _position;
        if (remaining <= 0)
        {
            return ValueTask.FromResult(0);
        }

        int count = Math.Min(Math.Min(_chunkSize, buffer.Length), remaining);
        _payload.AsSpan(_position, count).CopyTo(buffer.Span);
        _position += count;

        _afterChunk?.Invoke(_position);

        return ValueTask.FromResult(count);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
