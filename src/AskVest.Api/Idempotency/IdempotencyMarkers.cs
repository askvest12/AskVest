namespace AskVest.Api;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RequiresIdempotencyAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class IdempotencyExemptAttribute : Attribute;
