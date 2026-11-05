namespace AskVest.Modules.Identity.Messaging;

public sealed record EmailMessage(string To, string Subject, string TextBody);
