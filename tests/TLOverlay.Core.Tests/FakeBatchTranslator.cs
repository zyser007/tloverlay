using TLOverlay.Core.Translation;

namespace TLOverlay.Core.Tests;

/// <summary>
/// A translator that can answer a whole batch, and counts which path was taken -
/// which is how the tests tell "one request for the screen" from "one request
/// per line" without a network.
/// </summary>
internal sealed class FakeBatchTranslator : ITranslator, IBatchTranslator
{
    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>> _batch;
    private readonly Func<string, string> _single;

    public FakeBatchTranslator(
        string id = "fake-batch:v1",
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? batch = null,
        Func<string, string>? single = null)
    {
        Id = id;
        _batch = batch ?? (lines => [.. lines.Select(line => "TH:" + line)]);
        _single = single ?? (line => "TH:" + line);
    }

    public string Id { get; }

    public int CallCount { get; private set; }

    public int BatchCallCount { get; private set; }

    /// <summary>The lines the last batch call was asked to translate.</summary>
    public IReadOnlyList<string> LastBatch { get; private set; } = [];

    /// <summary>Runs while a batch is in flight, for asserting on pipeline state.</summary>
    public Action? WhileTranslating { get; set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        CallCount++;
        WhileTranslating?.Invoke();
        return Task.FromResult(_single(text));
    }

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        BatchCallCount++;
        LastBatch = [.. lines];
        WhileTranslating?.Invoke();
        return Task.FromResult(_batch(lines));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
