using System.Text.Json;
using Chelmsford.Core.Payments;
using Microsoft.AspNetCore.DataProtection;

namespace Chelmsford.Web.Services;

/// <summary>Who is paying for whom during one payment journey. Held in a short-lived, encrypted cookie.</summary>
public sealed class PayContext
{
    public PaymentSubjectKind? SubjectKind { get; set; }
    public string? SubjectReference { get; set; }
    public PayerType PayerType { get; set; }
    public string? PayerName { get; set; }
    public string? PayerEmail { get; set; }
    public string? Relationship { get; set; }
    public Guid? PaymentLinkId { get; set; }

    /// <summary>The route the payer took: "student", "link" or "guest".</summary>
    public string? Route { get; set; }

    /// <summary>Orders started in this session. An order page only renders if its reference is here.</summary>
    public List<string> Orders { get; set; } = [];

    public bool HasSubject => SubjectKind is not null && !string.IsNullOrEmpty(SubjectReference);

    public bool HasPayer => !string.IsNullOrWhiteSpace(PayerName) && !string.IsNullOrWhiteSpace(PayerEmail);

    public Payer ToPayer() => new(PayerType, PayerName ?? string.Empty, PayerEmail ?? string.Empty, Relationship);

    public bool OwnsOrder(string reference) => Orders.Contains(reference, StringComparer.OrdinalIgnoreCase);

    public void AddOrder(string reference)
    {
        if (!OwnsOrder(reference))
        {
            Orders.Add(reference);
        }

        if (Orders.Count > 20)
        {
            Orders.RemoveAt(0);
        }
    }
}

/// <summary>Reads and writes <see cref="PayContext"/> in a cookie protected with ASP.NET Core data protection.</summary>
public sealed class PayContextProtector(IDataProtectionProvider provider)
{
    public const string CookieName = "cc_pay";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ITimeLimitedDataProtector _protector = provider.CreateProtector("Chelmsford.PayContext").ToTimeLimitedDataProtector();

    public PayContext Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
        {
            return new PayContext();
        }

        try
        {
            var json = _protector.Unprotect(value, out _);
            return JsonSerializer.Deserialize<PayContext>(json) ?? new PayContext();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return new PayContext();
        }
    }

    public void Write(HttpResponse response, PayContext context)
    {
        var protectedValue = _protector.Protect(JsonSerializer.Serialize(context), Lifetime);
        response.Cookies.Append(CookieName, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = response.HttpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = Lifetime,
            Path = "/pay"
        });
    }

    public void Clear(HttpResponse response) => response.Cookies.Delete(CookieName, new CookieOptions { Path = "/pay" });
}
