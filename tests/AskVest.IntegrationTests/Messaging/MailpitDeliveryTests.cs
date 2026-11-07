using System.Net.Http.Json;
using System.Text.Json;
using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Messaging;

[CollectionDefinition(Name)]
public sealed class MailpitCollectionDefinition : ICollectionFixture<MailpitFixture>
{
    public const string Name = "mailpit";
}

public sealed class MailpitFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.32.0")
        .WithPortBinding(1025, assignRandomHostPort: true)
        .WithPortBinding(8025, assignRandomHostPort: true)
        .WithCleanUp(true)
        .Build();

    public int SmtpPort { get; private set; }
    public int ApiPort { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        SmtpPort = _container.GetMappedPublicPort(1025);
        ApiPort = _container.GetMappedPublicPort(8025);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[Collection(MailpitCollectionDefinition.Name)]
public sealed class MailpitDeliveryTests(MailpitFixture fixture)
{
    [Fact]
    public async Task Smtp_Adapter_Delivers_To_Mailpit()
    {
        var options = Options.Create(new MessagingOptions
        {
            Smtp =
            {
                Host = "127.0.0.1",
                Port = fixture.SmtpPort,
                FromAddress = "noreply@askvest.test",
                FromName = "AskVest",
                EnableStartTls = false,
            },
        });
        var transport = new SmtpEmailTransport(options);

        var recipient = $"user-{Guid.CreateVersion7():N}@example.com";
        var code = "483920";
        await transport.SendAsync(
            MessageTemplates.EmailVerification(recipient, code),
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{fixture.ApiPort}") };
        var message = await WaitForMessageAsync(http, recipient, TestContext.Current.CancellationToken);

        message.Subject.ShouldBe("Verify your AskVest email");
        message.Text.ShouldContain(code);
        message.Text.ShouldContain("If you did not");
    }

    [Fact]
    public async Task Smtp_Adapter_Delivers_Alert_Email()
    {
        var options = Options.Create(new MessagingOptions
        {
            Smtp =
            {
                Host = "127.0.0.1",
                Port = fixture.SmtpPort,
                FromAddress = "noreply@askvest.test",
                EnableStartTls = false,
            },
        });
        var transport = new SmtpEmailTransport(options);

        var recipient = $"alert-{Guid.CreateVersion7():N}@example.com";
        await transport.SendAsync(
            MessageTemplates.NewDeviceSignIn(recipient, "Chrome on Linux"),
            TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{fixture.ApiPort}") };
        var message = await WaitForMessageAsync(http, recipient, TestContext.Current.CancellationToken);

        message.Subject.ShouldBe("New sign-in to your AskVest account");
        message.Text.ShouldContain("Chrome on Linux");
        message.Text.ShouldNotContain("code is");
    }

    private static async Task<MailpitMessage> WaitForMessageAsync(
        HttpClient http,
        string recipient,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 50; i++)
        {
            var list = await http.GetFromJsonAsync<JsonElement>("/api/v1/messages", cancellationToken);
            foreach (var entry in list.GetProperty("messages").EnumerateArray())
            {
                var to = entry.GetProperty("To").EnumerateArray()
                    .Select(t => t.GetProperty("Address").GetString())
                    .ToList();
                if (to.Contains(recipient))
                {
                    var id = entry.GetProperty("ID").GetString()!;
                    var detail = await http.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}", cancellationToken);
                    return new MailpitMessage(
                        entry.GetProperty("Subject").GetString() ?? string.Empty,
                        detail.GetProperty("Text").GetString() ?? string.Empty);
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"No Mailpit message arrived for {recipient}.");
    }

    private sealed record MailpitMessage(string Subject, string Text);
}
