using TLOverlay.Core.Translation;

namespace TLOverlay.Core.Tests;

/// <summary>Records calls so cache tests can assert the model was skipped.</summary>
internal sealed class FakeTranslator : ITranslator
{
    private readonly Func<string, string> _translate;

    public FakeTranslator(string id = "fake:v1", Func<string, string>? translate = null)
    {
        Id = id;
        _translate = translate ?? (text => "TH:" + text);
    }

    public string Id { get; }

    public int CallCount { get; private set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(_translate(text));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
