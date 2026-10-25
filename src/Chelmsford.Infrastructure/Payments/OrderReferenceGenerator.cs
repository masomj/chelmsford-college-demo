using System.Security.Cryptography;

namespace Chelmsford.Infrastructure.Payments;

/// <summary>Creates the human-friendly order reference printed on receipts and read out over the phone.</summary>
public interface IOrderReferenceGenerator
{
    string Next();
}

public sealed class OrderReferenceGenerator : IOrderReferenceGenerator
{
    public const string Prefix = "CC-";

    /// <summary>No 0/O or 1/I, so a reference can't be misread.</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int Length = 6;

    public string Next() => Prefix + new string(RandomNumberGenerator.GetItems<char>(Alphabet, Length));
}
