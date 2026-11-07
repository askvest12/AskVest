namespace AskVest.Modules.Compliance.Contracts;

/// <summary>
/// One reason an eligibility mode is denied. <see cref="Code"/> is the stable
/// reason code clients branch on; <see cref="Message"/> is human readable.
/// </summary>
public sealed record EligibilityBlocker(string Code, string Message);
