using Chelmsford.Core.Payments;
using Chelmsford.Web.Services;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Extensions;
using static Chelmsford.Web.Umbraco.DocTypes;

namespace Chelmsford.Web.Umbraco;

/// <summary>
/// Payable items are created by finance and curriculum staff in the CMS. This reads the published
/// items, and works outside a web request too (for example from a payment webhook).
/// </summary>
public sealed class UmbracoPayableItemCatalogue(SiteContent content) : IPayableItemCatalogue
{
    public Task<IReadOnlyList<PayableItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(content.Read(cache =>
        {
            var folder = content.Root(cache, PayableItemFolder);
            return (IReadOnlyList<PayableItem>)(folder?.Children()?
                .Where(c => c.ContentType.Alias == DocTypes.PayableItem)
                .Select(c => Map(cache, c))
                .OfType<PayableItem>()
                .ToList() ?? []);
        }));

    public Task<PayableItem?> GetAsync(Guid key, CancellationToken cancellationToken = default) =>
        Task.FromResult(content.Read(cache =>
            cache.GetById(key) is { } node && node.ContentType.Alias == DocTypes.PayableItem ? Map(cache, node) : null));

    private static PayableItem? Map(IPublishedContentCache cache, IPublishedContent node)
    {
        if (node.Decimal(Props.Amount) is not { } amount || amount <= 0)
        {
            return null;
        }

        var courseIds = node.PickedKeys(Props.Courses)
            .Select(key => cache.GetById(key))
            .Where(c => c is not null && c.ContentType.Alias == Course)
            .Select(c => c!.Int(Props.CourseId))
            .OfType<int>()
            .Distinct()
            .ToList();

        return new PayableItem(
            node.Key,
            node.Name ?? string.Empty,
            ParseType(node.Text(Props.ItemType)),
            node.Text(Props.Description),
            (int)Math.Round(amount * 100m, MidpointRounding.AwayFromZero),
            courseIds,
            node.DateTime(Props.DueDate),
            node.Int(Props.Places),
            node.Text(Props.CostCode),
            node.DateTime(Props.TakesPlaceOn));
    }

    public static PayableItemType ParseType(string? label) => label?.Trim().ToLowerInvariant() switch
    {
        "course deposit" => PayableItemType.CourseDeposit,
        "materials fee" => PayableItemType.MaterialsFee,
        "trip" => PayableItemType.Trip,
        _ => PayableItemType.OneOff
    };
}
