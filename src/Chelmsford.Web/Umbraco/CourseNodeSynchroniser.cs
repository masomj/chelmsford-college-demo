using System.Globalization;
using Chelmsford.Core.Courses;
using Chelmsford.Core.Integration;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using static Chelmsford.Web.Umbraco.DocTypes;

namespace Chelmsford.Web.Umbraco;

/// <summary>
/// Keeps one published "course" page under Courses for every active synced course, so editors can add
/// images, video and student stories. Only the read-only synced labels and the node name are ever
/// written here; marketing fields belong to editors and are never touched.
/// </summary>
public sealed class CourseNodeSynchroniser(
    IContentService contentService,
    IContentTypeService contentTypeService,
    ILogger<CourseNodeSynchroniser> logger) : ICourseSyncObserver
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task CoursesSyncedAsync(IReadOnlyList<SyncedCourse> activeCourses, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            Synchronise(activeCourses);
        }
        finally
        {
            Gate.Release();
        }
    }

    private void Synchronise(IReadOnlyList<SyncedCourse> activeCourses)
    {
        var listing = FindCourseListing();
        if (listing is null)
        {
            logger.LogWarning("No Courses page found in the CMS yet; course pages will be created on the next sync");
            return;
        }

        var children = Children(listing.Id).Where(c => c.ContentType.Alias == Course).ToList();
        var byCourseId = new Dictionary<int, IContent>();
        foreach (var child in children)
        {
            if (int.TryParse(child.GetValue<string>(Props.CourseId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                byCourseId.TryAdd(id, child);
            }
        }

        var activeIds = activeCourses.Select(c => c.Id).ToHashSet();
        int created = 0, updated = 0, unpublished = 0;

        foreach (var course in activeCourses)
        {
            var name = string.IsNullOrWhiteSpace(course.Heading) ? course.Title : course.Heading;
            if (!byCourseId.TryGetValue(course.Id, out var node))
            {
                node = contentService.Create(name, listing.Id, Course);
                node.SetValue(Props.CourseId, course.Id.ToString(CultureInfo.InvariantCulture));
                node.SetValue(Props.SourceTitle, course.Title);
                node.SetValue(Props.LastSynced, ChangedLabel(course));
                Publish(node);
                created++;
                continue;
            }

            var changed = node.Name != name
                          || node.GetValue<string>(Props.SourceTitle) != course.Title
                          || node.GetValue<string>(Props.LastSynced) != ChangedLabel(course)
                          || !node.Published;
            if (!changed)
            {
                continue;
            }

            node.Name = name;
            node.SetValue(Props.SourceTitle, course.Title);
            node.SetValue(Props.LastSynced, ChangedLabel(course));
            Publish(node);
            updated++;
        }

        foreach (var (id, node) in byCourseId)
        {
            if (!activeIds.Contains(id) && node.Published)
            {
                contentService.Unpublish(node);
                unpublished++;
            }
        }

        if (created + updated + unpublished > 0)
        {
            logger.LogInformation("Course pages: {Created} created, {Updated} updated, {Unpublished} unpublished", created, updated, unpublished);
        }
    }

    /// <summary>
    /// When the college's data for this course last changed. The sync only moves this date when the
    /// course's source row changes, so unchanged courses keep their date.
    /// </summary>
    private static string ChangedLabel(SyncedCourse course) =>
        UkTimeLabel(course.SyncedUtc);

    private static string UkTimeLabel(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Chelmsford.Web.Services.UkTime.London);
        return local.ToString("d MMMM yyyy HH:mm", CultureInfo.GetCultureInfo("en-GB")) + " UK time";
    }

    private void Publish(IContent node)
    {
        var result = contentService.SaveAndPublish(node, []);
        if (!result.Success)
        {
            logger.LogWarning("Could not publish course page {Name}: {Status}", node.Name, result.Result);
        }
    }

    private IContent? FindCourseListing()
    {
        var type = contentTypeService.Get(CourseListing);
        if (type is null)
        {
            return null;
        }

        foreach (var root in contentService.GetRootContent())
        {
            var match = Children(root.Id).FirstOrDefault(c => c.ContentTypeId == type.Id);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private List<IContent> Children(int parentId) => contentService.AllChildren(parentId);
}
