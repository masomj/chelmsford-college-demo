using System.Globalization;
using Chelmsford.Core.Access;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace Chelmsford.Web.Umbraco;

/// <summary>Records CMS (back office) sign-ins, failed sign-ins and sign-outs in the access log.</summary>
public sealed class CmsAccessRecorder(IServiceScopeFactory scopes, ILogger<CmsAccessRecorder> logger)
{
    private const string CmsPath = "/umbraco";

    public async Task RecordAsync(string kind, UserNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var userName = LookUpName(
                scope.ServiceProvider.GetRequiredService<IUserService>(),
                notification.AffectedUserId,
                trustSuperUserId: kind != AccessKinds.CmsLoginFailed);
            await scope.ServiceProvider.GetRequiredService<IAccessLog>().RecordAsync(
                new AccessLogEntry
                {
                    Kind = kind,
                    OccurredUtc = DateTime.UtcNow,
                    UserName = userName,
                    Path = CmsPath,
                    IpPrefix = IpAnonymiser.Prefix(notification.IpAddress)
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tracking must never stop someone signing in.
            logger.LogWarning(ex, "Could not record a CMS {Kind} event", kind);
        }
    }

    /// <summary>
    /// The built-in administrator has the id -1. After a successful sign-in or sign-out that id is reliable; after a
    /// failed sign-in it may just mean "nobody was found", so a failed attempt is only named when the id is a real user's.
    /// </summary>
    private static string? LookUpName(IUserService users, string? affectedUserId, bool trustSuperUserId)
    {
        if (!int.TryParse(affectedUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            || id < -1
            || (id == -1 && !trustSuperUserId))
        {
            return null;
        }

        var user = users.GetUserById(id);
        return user is null ? null : string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name;
    }
}

public sealed class CmsLoginHandler(CmsAccessRecorder recorder) : INotificationAsyncHandler<UserLoginSuccessNotification>
{
    public Task HandleAsync(UserLoginSuccessNotification notification, CancellationToken cancellationToken) =>
        recorder.RecordAsync(AccessKinds.CmsLogin, notification, cancellationToken);
}

public sealed class CmsLoginFailedHandler(CmsAccessRecorder recorder) : INotificationAsyncHandler<UserLoginFailedNotification>
{
    public Task HandleAsync(UserLoginFailedNotification notification, CancellationToken cancellationToken) =>
        recorder.RecordAsync(AccessKinds.CmsLoginFailed, notification, cancellationToken);
}

public sealed class CmsLogoutHandler(CmsAccessRecorder recorder) : INotificationAsyncHandler<UserLogoutSuccessNotification>
{
    public Task HandleAsync(UserLogoutSuccessNotification notification, CancellationToken cancellationToken) =>
        recorder.RecordAsync(AccessKinds.CmsLogout, notification, cancellationToken);
}
