using Chelmsford.Web.Configuration;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace Chelmsford.Web.Umbraco;

/// <summary>
/// Makes sure the tender panel has its own CMS login (an editor, not an administrator), from <c>Panel:CmsUser</c>.
/// Safe to run on every start: an existing user is left alone unless the configured password no longer works.
/// </summary>
public sealed class PanelUserProvisioner(
    IUserService users,
    IBackOfficeUserManager userManager,
    IOptions<PanelOptions> options,
    ILogger<PanelUserProvisioner> logger)
{
    public async Task EnsureAsync()
    {
        var settings = options.Value.CmsUser;
        if (!settings.IsConfigured)
        {
            logger.LogInformation("No Panel:CmsUser is configured, so no panel CMS login is created");
            return;
        }

        var email = settings.Email!.Trim();
        var password = settings.Password!;
        var existing = users.GetByEmail(email);
        if (existing is null)
        {
            existing = await CreateAsync(settings.Name, email);
            if (existing is null)
            {
                return;
            }

            logger.LogInformation("Created the panel CMS user {Email}", email);
        }

        var identity = await userManager.FindByIdAsync(existing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (identity is not null && await userManager.CheckPasswordAsync(identity, password))
        {
            return;
        }

        var change = await users.ChangePasswordAsync(
            Constants.Security.SuperUserKey,
            new global::Umbraco.Cms.Core.Models.Membership.ChangeUserPasswordModel { UserKey = existing.Key, NewPassword = password });
        if (change.Success)
        {
            logger.LogInformation("Set the password of the panel CMS user {Email}", email);
        }
        else
        {
            logger.LogWarning("Could not set the password of the panel CMS user {Email}: {Status}", email, change.Status);
        }
    }

    private async Task<IUser?> CreateAsync(string name, string email)
    {
        var create = new UserCreateModel
        {
            Email = email,
            UserName = email,
            Name = string.IsNullOrWhiteSpace(name) ? "Tender panel" : name.Trim(),
            UserGroupKeys = new HashSet<Guid> { Constants.Security.EditorGroupKey }
        };

        var result = await users.CreateAsync(Constants.Security.SuperUserKey, create, approveUser: true);
        if (!result.Success)
        {
            logger.LogWarning("Could not create the panel CMS user {Email}: {Status}", email, result.Status);
            return null;
        }

        return users.GetByEmail(email);
    }
}
