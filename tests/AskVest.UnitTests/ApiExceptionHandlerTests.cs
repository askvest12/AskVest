using System.Text.Json;
using AskVest.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests;

public sealed class ApiExceptionHandlerTests
{
    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<IOptions<JsonOptions>>(Options.Create(new JsonOptions()))
            .BuildServiceProvider();
        return context;
    }

    [Fact]
    public async Task Database_failure_maps_to_service_unavailable()
    {
        var context = CreateContext();
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("wrapper", new NpgsqlException("connection refused")),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("service.unavailable");
    }

    [Fact]
    public async Task Unknown_failure_maps_to_server_unexpected()
    {
        var context = CreateContext();
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("boom"),
            TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("server.unexpected");
    }
}
