using AskVest.Api;
using System.Net;
using System.Net.Http.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class ApiOptionsTests
{
    [Fact]
    public void RateLimit_Defaults_AreStrict()
    {
        var options = new RateLimitingOptions();
        options.Global.PermitLimit.ShouldBeGreaterThan(0);
        options.Strict.PermitLimit.ShouldBeLessThan(options.Global.PermitLimit);
    }

    [Fact]
    public void Idempotency_Defaults_AreValid()
    {
        var options = new IdempotencyOptions();
        options.RetentionHours.ShouldBeGreaterThan(0);
        options.LockSeconds.ShouldBeGreaterThan(0);
        options.MaxStoredResponseBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Diagnostics_Defaults_ProbesOff()
        => new DiagnosticsOptions().EnableProbes.ShouldBeFalse();

    [Fact]
    public void Docs_Defaults_Disabled()
        => new DocsOptions().Enabled.ShouldBeFalse();

    [Fact]
    public async Task Validation_Filter_Returns_Envelope_On_Failure()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IValidator<ProbeRequest>>(new ProbeValidatorProbe());
        var app = builder.Build();
        app.MapPost("/probe", (ProbeRequest request) => Results.Ok(new { ok = true }))
            .AddEndpointFilter<ValidationEndpointFilter<ProbeRequest>>();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var response = await client.PostAsJsonAsync("/probe", new { name = (string?)null }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("\"success\":false");
        body.ShouldContain("validation.failed");
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

}

internal sealed record ProbeRequest(string? Name);

internal sealed class ProbeValidatorProbe : AbstractValidator<ProbeRequest>
{
    public ProbeValidatorProbe() => RuleFor(x => x.Name).NotEmpty();
}
