using System.Net;
using System.Net.Http;
using System.Text;
using TLOverlay.Core.Translation;
using Xunit;

namespace TLOverlay.Core.Tests;

public class GoogleTranslateTranslatorTests
{
    private static HttpClient Stub(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out StubHttpMessageHandler handler)
    {
        handler = new StubHttpMessageHandler(responder);
        return new HttpClient(handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void TheFreeEndpointsNestedArraysAreRead()
    {
        // What translate_a/single actually answers with.
        const string Body = """[[["ประตูจะไม่เปิด","The gate will not open",null,null,10]],null,"en"]""";

        Assert.Equal("ประตูจะไม่เปิด", GoogleTranslateTranslator.ParseFreeResponse(Body));
    }

    [Fact]
    public void EverySentenceOfALongLineIsKept()
    {
        // Long dialogue comes back split. Taking only the first piece would drop
        // the rest of the line, which is the sort of bug that looks like a bad
        // translation rather than a bug.
        const string Body =
            """[[["ประตูจะไม่เปิด ","The gate will not open ",null,null,3],["จนกว่าตราจะแตก","until the seal is broken",null,null,3]],null,"en"]""";

        Assert.Equal("ประตูจะไม่เปิด จนกว่าตราจะแตก", GoogleTranslateTranslator.ParseFreeResponse(Body));
    }

    [Fact]
    public void AnHtmlChallengePageIsReportedAsRateLimitingRatherThanCrashing()
    {
        CloudTranslationException error = Assert.Throws<CloudTranslationException>(
            () => GoogleTranslateTranslator.ParseFreeResponse("<html>sorry</html>"));

        Assert.Contains("ถี่เกินไป", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudTranslationV2IsReadAndUnescaped()
    {
        // v2 HTML-escapes its output even when format is "text", so an apostrophe
        // arrives as &#39; and would be shown that way over the game.
        const string Body =
            """{"data":{"translations":[{"translatedText":"อย่าเพิ่งไป&#39;นะ"}]}}""";

        Assert.Equal("อย่าเพิ่งไป'นะ", GoogleTranslateTranslator.ParseCloudResponse(Body));
    }

    [Fact]
    public async Task WithoutAKeyTheFreeEndpointIsUsed()
    {
        HttpClient client = Stub(
            _ => Json("""[[["ทดสอบ","test",null,null,10]],null,"en"]"""),
            out StubHttpMessageHandler handler);

        await using var translator = new GoogleTranslateTranslator(apiKey: null, client: client);

        Assert.Equal("ทดสอบ", await translator.TranslateAsync("test"));
        Assert.Equal("google:free", translator.Id);

        Uri requested = handler.Requests[0].RequestUri!;
        Assert.Contains("translate_a/single", requested.ToString(), StringComparison.Ordinal);
        Assert.Contains("tl=th", requested.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAKeyTheOfficialApiIsUsed()
    {
        HttpClient client = Stub(
            _ => Json("""{"data":{"translations":[{"translatedText":"ทดสอบ"}]}}"""),
            out StubHttpMessageHandler handler);

        await using var translator = new GoogleTranslateTranslator("secret-key", client: client);

        Assert.Equal("ทดสอบ", await translator.TranslateAsync("test"));

        // A different product with different output, so it must not share cached
        // lines with the free one.
        Assert.Equal("google:v2", translator.Id);
        Assert.Contains("translation.googleapis.com", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeingRateLimitedSaysWhatToDoAboutIt()
    {
        HttpClient client = Stub(_ => Json("", HttpStatusCode.TooManyRequests), out _);

        await using var translator = new GoogleTranslateTranslator(apiKey: null, client: client);

        CloudTranslationException error = await Assert.ThrowsAsync<CloudTranslationException>(
            () => translator.TranslateAsync("test"));

        Assert.Contains("429", error.Message, StringComparison.Ordinal);
        Assert.Contains("API key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyInputCostsNoRequest()
    {
        HttpClient client = Stub(_ => Json("[]"), out StubHttpMessageHandler handler);

        await using var translator = new GoogleTranslateTranslator(apiKey: null, client: client);

        Assert.Equal(string.Empty, await translator.TranslateAsync("   "));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GlossaryTermsSurviveTheRoundTrip()
    {
        // The placeholder goes out and comes back; the term is restored here, not
        // by the translator, because matching it after the text is Thai is
        // impossible.
        HttpClient client = Stub(
            _ => Json("""[[["[[0]] ฟื้นฟูพลัง","[[0]] restores",null,null,10]],null,"en"]"""),
            out _);

        var glossary = new GlossaryService([new GlossaryEntry("Estus Flask", null)]);

        await using var translator = new GoogleTranslateTranslator(apiKey: null, glossary: glossary, client: client);

        Assert.Equal("Estus Flask ฟื้นฟูพลัง", await translator.TranslateAsync("Estus Flask restores"));
    }
}

public class OpenAiTranslatorTests
{
    private static HttpResponseMessage Reply(string content)
    {
        // Built rather than interpolated: a raw string literal holding this many
        // consecutive closing braces needs more dollar signs than it is worth.
        string body = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content } },
            },
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task TheKeyModelAndPromptAllGoOut()
    {
        var handler = new StubHttpMessageHandler(_ => Reply("ประตูจะไม่เปิด"));

        await using var translator = new OpenAiTranslator(
            new OpenAiOptions { ApiKey = "sk-test", Model = "gpt-4o-mini" },
            client: new HttpClient(handler));

        Assert.Equal("ประตูจะไม่เปิด", await translator.TranslateAsync("The gate will not open."));

        HttpRequestMessage request = handler.Requests[0];

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", request.Headers.Authorization?.Parameter);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());

        string body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("gpt-4o-mini", body, StringComparison.Ordinal);
        Assert.Contains("translation engine", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScaffoldingTheModelLeaksIsStrippedHereToo()
    {
        var handler = new StubHttpMessageHandler(_ => Reply("Thai: \"ประตูจะไม่เปิด\""));

        await using var translator = new OpenAiTranslator(
            new OpenAiOptions { ApiKey = "sk-test" },
            client: new HttpClient(handler));

        Assert.Equal("ประตูจะไม่เปิด", await translator.TranslateAsync("The gate will not open."));
    }

    [Fact]
    public async Task WithoutAKeyItSaysSoRatherThanCallingAnything()
    {
        var handler = new StubHttpMessageHandler(_ => Reply("ไม่ควรถูกเรียก"));

        await using var translator = new OpenAiTranslator(
            new OpenAiOptions { ApiKey = string.Empty },
            client: new HttpClient(handler));

        await Assert.ThrowsAsync<CloudTranslationException>(() => translator.TranslateAsync("test"));
        Assert.Empty(handler.Requests);
        Assert.False(await translator.IsReadyAsync());
    }

    [Theory]
    [InlineData(401, "API key")]
    [InlineData(404, "โมเดล")]
    [InlineData(429, "เครดิต")]
    public void StatusCodesBecomeSomethingAPlayerCanActOn(int status, string expected)
    {
        CloudTranslationException error = OpenAiTranslator.Failure((HttpStatusCode)status, string.Empty);

        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProvidersOwnMessageIsKeptWhenThereIsOne()
    {
        // For a wrong model name or a disabled account this is the only text that
        // says which of the two it was.
        CloudTranslationException error = OpenAiTranslator.Failure(
            HttpStatusCode.NotFound,
            """{"error":{"message":"The model `gpt-9` does not exist"}}""");

        Assert.Contains("gpt-9", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHtmlErrorPageFromAProxyIsNotACrash()
    {
        CloudTranslationException error = OpenAiTranslator.Failure(HttpStatusCode.BadGateway, "<html>502</html>");

        Assert.Contains("502", error.Message, StringComparison.Ordinal);
    }
}
