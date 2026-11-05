namespace AskVest.Modules.Identity.Contracts;

public sealed record SetPinRequest(string? Password, string? Pin);

public sealed record ChangePinRequest(string? CurrentPin, string? Pin);

public sealed record ResetPinRequest(string? Code, string? Password, string? Pin);

public sealed record StepUpRequest(string? Purpose, string? Pin, string? Binding);
