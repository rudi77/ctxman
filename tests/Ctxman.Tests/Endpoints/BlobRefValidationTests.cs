using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// Spec §2.6 / §10: <c>blob_ref.key</c> ist content-addressed (sha256). Ein client-gelieferter
/// Nicht-sha256-Key (z. B. ein Path-Traversal-Versuch) wird beim Append mit 400 abgewiesen, bevor
/// er persistiert wird und später ungeprüft in den <c>IBlobStore</c> fließen könnte.
/// </summary>
public sealed class BlobRefValidationTests
{
    private const string Tenant = "tenant-blobref";

    private static HttpClient ClientFor(CtxmanWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant);
        return client;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/v1/sessions", new { });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session_id").GetString()!;
    }

    private static HttpRequestMessage Append(string sid, object body, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/segments")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("../tenant-victim/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("not-a-sha256")]
    [InlineData("ABCDEF0000000000000000000000000000000000000000000000000000000000")] // uppercase
    public async Task Append_WithNonSha256BlobKey_Returns400(string key)
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var body = new
        {
            kind = "tool_result",
            role = "tool",
            blob_ref = new { store = "fs", key, size_bytes = 10, content_type = "text/plain" },
        };

        var response = await client.SendAsync(Append(sid, body, "idem-badkey-" + key.GetHashCode()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Append_WithValidSha256BlobKey_Returns201()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var validKey = new string('a', 64); // 64 lowercase-hex ⇒ gültiger content-addressed Key.
        var body = new
        {
            kind = "tool_result",
            role = "tool",
            blob_ref = new { store = "fs", key = validKey, size_bytes = 10, content_type = "text/plain" },
        };

        var response = await client.SendAsync(Append(sid, body, "idem-goodkey-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
