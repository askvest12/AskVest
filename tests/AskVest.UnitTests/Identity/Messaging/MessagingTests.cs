using AskVest.Modules.Identity.Configuration;
using AskVest.Modules.Identity.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Messaging;

public sealed class MessageChannelTests
{
    private static MessageChannel CreateChannel(int capacity = 4)
        => new(Options.Create(new MessagingOptions { QueueCapacity = capacity }),
            NullLogger<MessageChannel>.Instance);

    [Fact]
    public void Enqueue_Within_Capacity_Succeeds()
    {
        var channel = CreateChannel(2);
        channel.TryEnqueue(new QueuedEmail(new EmailMessage("a@b.c", "s", "b"))).ShouldBeTrue();
        channel.TryEnqueue(new QueuedSms(new SmsMessage("+234", "b"))).ShouldBeTrue();
    }

    [Fact]
    public void Enqueue_Beyond_Capacity_Returns_False()
    {
        var channel = CreateChannel(1);
        channel.TryEnqueue(new QueuedEmail(new EmailMessage("a@b.c", "s", "b"))).ShouldBeTrue();
        channel.TryEnqueue(new QueuedEmail(new EmailMessage("a@b.c", "s", "b"))).ShouldBeFalse();
    }
}

public sealed class MessageDispatchWorkerTests
{
    private sealed class FlakyEmailTransport(int failuresBeforeSuccess) : IEmailTransport
    {
        private int _attempts;
        public int Attempts => _attempts;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            _attempts++;
            return _attempts <= failuresBeforeSuccess
                ? Task.FromException(new InvalidOperationException("smtp down"))
                : Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailingSmsTransport : ISmsTransport
    {
        public int Attempts { get; private set; }

        public Task SendAsync(SmsMessage message, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromException(new InvalidOperationException("sms provider down"));
        }
    }

    private static (MessageChannel Channel, MessageDispatchWorker Worker) CreateWorker(
        IEmailTransport email,
        ISmsTransport sms,
        MessagingOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var opts = options ?? new MessagingOptions { MaxAttempts = 3 };
        opts.RetryBackoffSeconds.Clear();
        opts.RetryBackoffSeconds.Add(0);
        opts.RetryBackoffSeconds.Add(0);
        opts.RetryBackoffSeconds.Add(0);
        var channel = new MessageChannel(Options.Create(opts), NullLogger<MessageChannel>.Instance);
        var worker = new MessageDispatchWorker(
            channel, email, sms,
            Options.Create(opts),
            timeProvider ?? TimeProvider.System,
            NullLogger<MessageDispatchWorker>.Instance);
        return (channel, worker);
    }

    [Fact]
    public async Task Message_Is_Delivered_After_Retries()
    {
        var transport = new FlakyEmailTransport(failuresBeforeSuccess: 2);
        var (channel, worker) = CreateWorker(transport, new CapturingSmsTransport());
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            channel.TryEnqueue(new QueuedEmail(new EmailMessage("u@x.com", "s", "b"))).ShouldBeTrue();
            await WaitUntilAsync(() => transport.Attempts >= 3);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Permanent_Failure_Stops_After_MaxAttempts()
    {
        var sms = new AlwaysFailingSmsTransport();
        var (channel, worker) = CreateWorker(new CapturingEmailTransport(), sms);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            channel.TryEnqueue(new QueuedSms(new SmsMessage("+2348000000000", "code"))).ShouldBeTrue();
            await WaitUntilAsync(() => sms.Attempts >= 3);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            sms.Attempts.ShouldBe(3);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Shutdown_Stops_Processing()
    {
        var email = new FlakyEmailTransport(failuresBeforeSuccess: int.MaxValue);
        var (channel, worker) = CreateWorker(email, new CapturingSmsTransport());
        await worker.StartAsync(TestContext.Current.CancellationToken);
        channel.TryEnqueue(new QueuedEmail(new EmailMessage("u@x.com", "s", "b"))).ShouldBeTrue();
        await worker.StopAsync(TestContext.Current.CancellationToken);
        var attemptsAtStop = email.Attempts;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        email.Attempts.ShouldBe(attemptsAtStop);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("condition was not met in time");
    }
}

public sealed class DevelopmentSmsTransportTests
{
    [Fact]
    public async Task Forwards_Sms_As_Email()
    {
        var email = new CapturingEmailTransport();
        var options = new MessagingOptions { Sms = { Mode = SmsAdapterMode.Development, ForwardTo = "dev@localhost" } };
        var transport = new DevelopmentSmsTransport(email, Options.Create(options), NullLogger<DevelopmentSmsTransport>.Instance);

        await transport.SendAsync(new SmsMessage("+2348012345678", "Your code is 123456"), TestContext.Current.CancellationToken);

        var sent = email.Sent.ShouldHaveSingleItem();
        sent.To.ShouldBe("dev@localhost");
        sent.Subject.ShouldContain("SMS");
        sent.TextBody.ShouldContain("123456");
        sent.TextBody.ShouldContain("+2348012345678");
    }

    [Fact]
    public async Task Missing_ForwardTo_Throws()
    {
        var transport = new DevelopmentSmsTransport(
            new CapturingEmailTransport(),
            Options.Create(new MessagingOptions()),
            NullLogger<DevelopmentSmsTransport>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(
            () => transport.SendAsync(new SmsMessage("+234", "x"), TestContext.Current.CancellationToken));
    }
}
