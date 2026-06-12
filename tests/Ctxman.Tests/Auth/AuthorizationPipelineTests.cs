using System.Net;
using System.Security.Claims;
using System.Text;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Tests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ctxman.Tests.Auth;

/// <summary>
/// Acceptance tests for the authorization pipeline stage (Spec §4.1, §8; WP7 AC §5, §6).
/// Pipeline order: TenantResolution → Authentication → Authorization.
/// These tests are RED until the developer wires ICtxmanAuthorizationHandler invocation
/// into the Minimal API pipeline.
/// </summary>
public sealed class AuthorizationPipelineTests
{
    private static StringContent EmptyJsonBody()
        => new("{}", Encoding.UTF8, "application/json");

    // ──────────────────────────────────────────────────────────────────────────
    // Test 1: Default handler (AllowAllWithinTenantAuthorizationHandler) must
    //         allow POST /v1/sessions → 201 Created (Spec §4.1, §4.3).
    // ──────────────────────────────────────────────────────────────────────────
    [Fact] // Spec §4.1: Default-Handler erlaubt alles – Endpunkt liefert 201, NICHT 403.
    public async Task DefaultHandler_PostSessions_Returns201()
    {
        using var factory = new CtxmanWebAppFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-default");

        var response = await client.PostAsync("/v1/sessions", EmptyJsonBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 2: A DI-injected Deny handler must short-circuit before the endpoint
    //         body and return 403 Forbidden (Spec §4.1 WP7 AC §5).
    //
    // EXPECTED FAILURE: currently returns 201 because the handler is never invoked.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact] // Spec §4.1: Deny-Handler ⇒ 403 Forbidden (Authorization short-circuits endpoint).
    public async Task DenyHandler_PostSessions_Returns403()
    {
        using var baseFactory = new CtxmanWebAppFactory();
        using var factory = baseFactory.WithAuthorizationHandler<DenyAllAuthorizationHandler>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-deny");

        var response = await client.PostAsync("/v1/sessions", EmptyJsonBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 3: Pipeline order — tenant MUST be resolved before authorization runs.
    //         RecordingAuthorizationHandler captures the TenantId it receives;
    //         after the request, assert it equals the header value (Spec §4.1).
    //
    // EXPECTED FAILURE: handler is never invoked, so CapturedTenantId stays null.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact] // Spec §4.1: Tenant-Resolution läuft VOR Authorization — Handler empfängt aufgelösten Tenant.
    public async Task TenantResolved_BeforeAuthorization_HandlerReceivesCorrectTenantId()
    {
        using var baseFactory = new CtxmanWebAppFactory();
        var recording = new RecordingAuthorizationHandler();

        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICtxmanAuthorizationHandler>();
            services.AddSingleton<ICtxmanAuthorizationHandler>(recording);
        }));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-order");

        var response = await client.PostAsync("/v1/sessions", EmptyJsonBody());

        // Handler must have been called (tenant captured) AND endpoint must succeed.
        Assert.Equal("t-order", recording.CapturedTenantId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 4: ResourceAction is passed to the handler — ResourceType and Operation
    //         must be non-empty for POST /v1/sessions (Spec §4.1).
    //         Assertion is intentionally loose (non-empty) to avoid coupling to the
    //         exact string the developer chooses.
    //
    // EXPECTED FAILURE: handler is never invoked, so CapturedAction stays null.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact] // Spec §4.1: ResourceAction enthält ResourceType und Operation (non-empty) für POST /v1/sessions.
    public async Task ResourceAction_PostSessions_HasNonEmptyResourceTypeAndOperation()
    {
        using var baseFactory = new CtxmanWebAppFactory();
        var recording = new RecordingAuthorizationHandler();

        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICtxmanAuthorizationHandler>();
            services.AddSingleton<ICtxmanAuthorizationHandler>(recording);
        }));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-action");

        await client.PostAsync("/v1/sessions", EmptyJsonBody());

        Assert.NotNull(recording.CapturedAction);
        Assert.False(string.IsNullOrWhiteSpace(recording.CapturedAction!.ResourceType),
            "ResourceAction.ResourceType must be non-empty for POST /v1/sessions");
        Assert.False(string.IsNullOrWhiteSpace(recording.CapturedAction!.Operation),
            "ResourceAction.Operation must be non-empty for POST /v1/sessions");
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// Test-internal helpers — same assembly as DenyAllAuthorizationHandler (above).
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Records the first invocation of AuthorizeAsync so tests can assert pipeline order
/// (tenant resolved before authorization) and action content (Spec §4.1).
/// Always returns Allow so the endpoint body executes normally.
/// </summary>
internal sealed class RecordingAuthorizationHandler : ICtxmanAuthorizationHandler
{
    /// <summary>TenantId received by the first AuthorizeAsync call; null if never called.</summary>
    public string? CapturedTenantId { get; private set; }

    /// <summary>ResourceAction received by the first AuthorizeAsync call; null if never called.</summary>
    public ResourceAction? CapturedAction { get; private set; }

    public ValueTask<AuthzDecision> AuthorizeAsync(
        TenantContext tenant,
        ClaimsPrincipal? caller,
        ResourceAction action,
        CancellationToken ct)
    {
        // Capture on first call; subsequent calls on the same instance are ignored.
        CapturedTenantId ??= tenant.TenantId;
        CapturedAction ??= action;
        return ValueTask.FromResult(AuthzDecision.Allow);
    }
}
