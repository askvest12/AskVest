using AskVest.Modules.Identity.Messaging;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Identity.Messaging;

public sealed class MessageTemplatesTests
{
    [Fact]
    public void Verification_Messages_Contain_Code()
    {
        MessageTemplates.EmailVerification("u@x.com", "123456").TextBody.ShouldContain("123456");
        MessageTemplates.DeviceVerification("u@x.com", "123456").TextBody.ShouldContain("123456");
        MessageTemplates.PasswordReset("u@x.com", "123456").TextBody.ShouldContain("123456");
        MessageTemplates.PinReset("u@x.com", "123456").TextBody.ShouldContain("123456");
        MessageTemplates.PhoneVerification("+234", "123456").Body.ShouldContain("123456");
    }

    [Fact]
    public void Alert_Messages_Never_Contain_Codes()
    {
        var alerts = new[]
        {
            MessageTemplates.NewDeviceSignIn("u@x.com", "Chrome on Linux"),
            MessageTemplates.PasswordChanged("u@x.com"),
            MessageTemplates.PasswordResetCompleted("u@x.com"),
            MessageTemplates.PinChanged("u@x.com"),
            MessageTemplates.RefreshReuseDetected("u@x.com"),
            MessageTemplates.DeletionRequested("u@x.com", 30),
            MessageTemplates.SignUpAttemptOnExistingAccount("u@x.com"),
        };

        foreach (var alert in alerts)
        {
            alert.TextBody.ShouldNotContain("code is");
            alert.TextBody.ShouldNotContain("verification code");
        }
    }

    [Fact]
    public void No_Message_Contains_Secrets_Placeholders()
    {
        var messages = new (string Subject, string Body)[]
        {
            (MessageTemplates.EmailVerification("u@x.com", "123456").Subject, MessageTemplates.EmailVerification("u@x.com", "123456").TextBody),
            (MessageTemplates.PasswordReset("u@x.com", "123456").Subject, MessageTemplates.PasswordReset("u@x.com", "123456").TextBody),
            (MessageTemplates.NewDeviceSignIn("u@x.com", "d").Subject, MessageTemplates.NewDeviceSignIn("u@x.com", "d").TextBody),
            (MessageTemplates.RefreshReuseDetected("u@x.com").Subject, MessageTemplates.RefreshReuseDetected("u@x.com").TextBody),
        };

        foreach (var (subject, body) in messages)
        {
            var combined = subject + " " + body;
            combined.ShouldNotContain("http://");
            combined.ShouldNotContain("https://");
            combined.ShouldNotContain("password=");
            combined.ShouldNotContain("token=");
            combined.ShouldNotContain("hash=");
        }
    }

    [Fact]
    public void Every_Message_States_Recourse()
    {
        var messages = new[]
        {
            MessageTemplates.EmailVerification("u@x.com", "1").TextBody,
            MessageTemplates.DeviceVerification("u@x.com", "1").TextBody,
            MessageTemplates.PasswordReset("u@x.com", "1").TextBody,
            MessageTemplates.PinReset("u@x.com", "1").TextBody,
            MessageTemplates.NewDeviceSignIn("u@x.com", "d").TextBody,
            MessageTemplates.PasswordChanged("u@x.com").TextBody,
            MessageTemplates.RefreshReuseDetected("u@x.com").TextBody,
            MessageTemplates.DeletionRequested("u@x.com", 30).TextBody,
            MessageTemplates.SignUpAttemptOnExistingAccount("u@x.com").TextBody,
        };

        foreach (var body in messages)
        {
            body.ShouldContain("If you did not", Case.Insensitive);
        }
    }
}
