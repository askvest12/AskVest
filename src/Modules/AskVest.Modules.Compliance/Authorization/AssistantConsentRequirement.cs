using Microsoft.AspNetCore.Authorization;

namespace AskVest.Modules.Compliance.Authorization;

/// <summary>
/// Requires the caller to have accepted every compliance document whose
/// RequiredFor category is "assistant". Combined with the practice
/// eligibility requirement on the Assistant policy.
/// </summary>
internal sealed class AssistantConsentRequirement : IAuthorizationRequirement;
