using System.Security.Cryptography;
using System.Text;
using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chelmsford.Infrastructure.Payments;

/// <summary>
/// Secure payment links for one subject and one item. The raw token is only ever in the link itself;
/// the database stores its SHA-256 hash, so a leaked database can't be turned into working links.
/// </summary>
public sealed class PaymentLinkService(
    SiteDbContext db,
    IPaymentService payments,
    IPayableItemCatalogue catalogue,
    TimeProvider clock,
    ILogger<PaymentLinkService> logger) : IPaymentLinkService
{
    private const int TokenBytes = 32;

    public async Task<string> CreateAsync(
        PaymentSubjectKind kind,
        string subjectReference,
        Guid itemKey,
        TimeSpan validFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectReference);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validFor, TimeSpan.Zero);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        var now = clock.GetUtcNow().UtcDateTime;
        var link = new PaymentLink
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(token),
            SubjectKind = kind,
            SubjectReference = subjectReference.Trim().ToUpperInvariant(),
            ItemKey = itemKey,
            CreatedUtc = now,
            ExpiresUtc = now.Add(validFor)
        };

        db.PaymentLinks.Add(link);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Payment link {LinkId} created for {Kind} {Subject}, item {Item}, expires {Expires:u}", link.Id, kind, link.SubjectReference, itemKey, link.ExpiresUtc);
        return token;
    }

    public async Task<ResolvedPaymentLink?> ResolveAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = Hash(token.Trim());
        var link = await db.PaymentLinks.AsNoTracking().FirstOrDefaultAsync(l => l.TokenHash == hash, cancellationToken);
        if (link is null || link.UsedUtc is not null || link.ExpiresUtc <= clock.GetUtcNow().UtcDateTime)
        {
            return null;
        }

        var subject = await payments.FindSubjectAsync(link.SubjectKind, link.SubjectReference, cancellationToken);
        var item = await catalogue.GetAsync(link.ItemKey, cancellationToken);
        if (subject is null || item is null)
        {
            logger.LogWarning("Payment link {LinkId} points at a subject or item that no longer exists", link.Id);
            return null;
        }

        return new ResolvedPaymentLink(link, subject, item);
    }

    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
