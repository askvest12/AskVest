namespace AskVest.Modules.Identity.Crypto;

public sealed class HashingBusyException : Exception
{
    public HashingBusyException()
        : base("Credential hashing capacity is saturated.")
    {
    }

    public HashingBusyException(string message)
        : base(message)
    {
    }

    public HashingBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
