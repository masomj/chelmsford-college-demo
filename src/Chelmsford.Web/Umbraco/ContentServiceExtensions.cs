using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace Chelmsford.Web.Umbraco;

internal static class ContentServiceExtensions
{
    /// <summary>All children of a node (drafts included), paging through as needed.</summary>
    public static List<IContent> AllChildren(this IContentService contentService, int parentId, int max = 5000)
    {
        var all = new List<IContent>();
        long page = 0;
        while (all.Count < max)
        {
            var batch = contentService.GetPagedChildren(parentId, page, 200, out var total, propertyAliases: null, filter: null, ordering: null, loadTemplates: false).ToList();
            all.AddRange(batch);
            if (batch.Count == 0 || all.Count >= total)
            {
                break;
            }

            page++;
        }

        return all;
    }
}
