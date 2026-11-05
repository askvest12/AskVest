namespace AskVest.Modules.Identity.Messaging;

public abstract record OutboundMessage;

public sealed record QueuedEmail(EmailMessage Message) : OutboundMessage;

public sealed record QueuedSms(SmsMessage Message) : OutboundMessage;
