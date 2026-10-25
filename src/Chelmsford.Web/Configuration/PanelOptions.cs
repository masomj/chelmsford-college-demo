namespace Chelmsford.Web.Configuration;

/// <summary>The "Panel" section: a CMS login created for the tender panel to look around the back office.</summary>
public sealed class PanelOptions
{
    public const string SectionName = "Panel";

    public PanelCmsUser CmsUser { get; set; } = new();
}

/// <summary>With no <see cref="Email"/> or <see cref="Password"/> no panel user is created.</summary>
public sealed class PanelCmsUser
{
    public string Name { get; set; } = "Tender panel";
    public string? Email { get; set; }
    public string? Password { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>
/// The "Reports" section: the sign-in for /reports/access. With no <see cref="Password"/> the reports are switched off
/// and the address returns 404.
/// </summary>
public sealed class ReportsOptions
{
    public const string SectionName = "Reports";

    public string UserName { get; set; } = "reports";
    public string? Password { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Password);
}
