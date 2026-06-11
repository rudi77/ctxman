using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Endpoints;

/// <summary>
/// API-Tests für <c>POST /v1/sessions/{sid}/blobs</c> (Spec §4.3 / §7, WP2-Akzeptanz 6, 7).
/// Content-adressiert (key = sha256), tenant-geprefixter Storage-Pfad, korrekte size_bytes /
/// content_type, Dedup bei gleichem Inhalt, snake_case-Wire-Format.
/// </summary>
public sealed class BlobEndpointsTests
{
    private const string Tenant = "tenant-blob";

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

    private static HttpRequestMessage Upload(string sid, byte[] content, string contentType)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/sessions/{sid}/blobs");
        var bytes = new ByteArrayContent(content);
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        req.Content = bytes;
        return req;
    }

    [Fact] // Akzeptanz 6 / §4.3: 201 mit { blob_ref } — key = sha256, korrekte size_bytes/content_type (snake_case).
    public async Task UploadBlob_Returns201_WithContentAddressedBlobRef()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var content = Encoding.UTF8.GetBytes("content-addressed upload payload");
        var expectedKey = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var response = await client.SendAsync(Upload(sid, content, "text/plain"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("blob_ref", out var blobRef), "must contain snake_case blob_ref");
        Assert.Equal(expectedKey, blobRef.GetProperty("key").GetString()); // §2.6: key = sha256(content).
        Assert.Equal(content.LongLength, blobRef.GetProperty("size_bytes").GetInt64());
        Assert.Equal("text/plain", blobRef.GetProperty("content_type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(blobRef.GetProperty("store").GetString()));
    }

    [Fact] // Akzeptanz 6 / §2.6: gleicher Inhalt ⇒ gleicher Key (Dedup).
    public async Task UploadBlob_SameContentTwice_YieldsSameKey()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);
        var sid = await CreateSessionAsync(client);

        var content = Encoding.UTF8.GetBytes("dedup me");

        var first = await client.SendAsync(Upload(sid, content, "application/octet-stream"));
        var second = await client.SendAsync(Upload(sid, content, "application/octet-stream"));
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        var firstKey = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("blob_ref").GetProperty("key").GetString();
        var secondKey = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("blob_ref").GetProperty("key").GetString();
        Assert.Equal(firstKey, secondKey);
    }

    [Fact] // Akzeptanz 6 / §10: Storage-Pfad ist tenant-geprefixt ({root}/{tenant}/{key}).
    public async Task UploadBlob_StoredUnderTenantPrefixedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "ctxman-blob-pathtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var factory = new CtxmanWebAppFactory().WithSetting("blobstore:Root", root);
            var client = ClientFor(factory);
            var sid = await CreateSessionAsync(client);

            var content = Encoding.UTF8.GetBytes("path probe");
            var response = await client.SendAsync(Upload(sid, content, "text/plain"));
            response.EnsureSuccessStatusCode();
            var key = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("blob_ref").GetProperty("key").GetString()!;

            var expectedPath = Path.Combine(root, Tenant, key);
            Assert.True(File.Exists(expectedPath), $"blob must be stored under tenant-prefixed path {expectedPath}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact] // Akzeptanz 6 / §10: Upload auf fremde/unbekannte Session ⇒ 404 (kein Existenz-Leak).
    public async Task UploadBlob_UnknownSession_Returns404()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = ClientFor(factory);

        var response = await client.SendAsync(Upload(Ulid.NewUlid().ToString(), Encoding.UTF8.GetBytes("x"), "text/plain"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
