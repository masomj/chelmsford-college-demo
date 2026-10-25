using Chelmsford.Core.Events;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using static Chelmsford.Web.Umbraco.DocTypes;

namespace Chelmsford.Web.Umbraco;

// SaveAsync on content types is marked obsolete in 17 in favour of IContentTypeEditingService's
// create/update models. It is still supported until Umbraco 19 and keeps this installer far simpler.
#pragma warning disable CS0618

/// <summary>
/// Creates the CMS schema in code so the site builds itself from an empty database (for example in a
/// fresh container). Idempotent: anything missing is added, nothing is ever removed.
/// </summary>
public sealed class SchemaInstaller(
    IContentTypeService contentTypeService,
    IDataTypeService dataTypeService,
    PropertyEditorCollection propertyEditors,
    IConfigurationEditorJsonSerializer configurationSerializer,
    IShortStringHelper shortStringHelper,
    ILogger<SchemaInstaller> logger)
{
    /// <summary>A property. FormerNames are earlier labels that are renamed to Name on existing installs (a label an editor changed is left alone).</summary>
    private sealed record Prop(string Alias, string Name, IDataType DataType, string? Description = null, bool Mandatory = false, string[]? FormerNames = null);

    private sealed record Group(string Alias, string Name, IReadOnlyList<Prop> Props);

    private sealed record DataTypes(
        IDataType Text,
        IDataType TextArea,
        IDataType RichText,
        IDataType Image,
        IDataType DateTime,
        IDataType Integer,
        IDataType Boolean,
        IDataType Label);

    public async Task InstallAsync()
    {
        var dt = await ResolveBuiltInsAsync();

        var amount = await EnsureDataTypeAsync("Amount (£)", Constants.PropertyEditors.Aliases.Decimal, "Umb.PropertyEditorUi.Decimal",
            ValueStorageType.Decimal, new Dictionary<string, object> { ["min"] = 0, ["step"] = 0.01 }, dt.Text);
        var eventType = await EnsureDataTypeAsync("Event type", Constants.PropertyEditors.Aliases.DropDownListFlexible, "Umb.PropertyEditorUi.Dropdown",
            ValueStorageType.Nvarchar, new Dictionary<string, object> { ["items"] = EventTypes.All.ToArray(), ["multiple"] = false }, dt.Text);
        var itemType = await EnsureDataTypeAsync("Payable item type", Constants.PropertyEditors.Aliases.DropDownListFlexible, "Umb.PropertyEditorUi.Dropdown",
            ValueStorageType.Nvarchar, new Dictionary<string, object> { ["items"] = PayableItemTypes.ToArray(), ["multiple"] = false }, dt.Text);

        // Leaf types first, so parents can list them as allowed children.
        var campaign = await EnsureContentTypeAsync(Campaign, "Campaign", "icon-flag", false,
        [
            new Group("content", "Content",
            [
                new Prop(Props.Label, "Label", dt.Text, "Short tag shown above the heading, such as Open Evening"),
                new Prop(Props.Heading, "Heading", dt.Text, Mandatory: true),
                new Prop(Props.Text, "Text", dt.TextArea),
                new Prop(Props.ButtonText, "Button text", dt.Text),
                new Prop(Props.ButtonUrl, "Button link", dt.Text, "A page on this site, such as /events/open-evening/"),
                new Prop(Props.Image, "Image", dt.Image)
            ]),
            new Group("schedule", "Schedule",
            [
                new Prop(Props.ShowFrom, "Show from", dt.DateTime, "The homepage shows the first campaign that is live now, so open-event campaigns go up and come down automatically"),
                new Prop(Props.ShowUntil, "Show until", dt.DateTime, "The homepage shows the first campaign that is live now, so open-event campaigns go up and come down automatically")
            ])
        ]);

        var course = await EnsureContentTypeAsync(Course, "Course", "icon-school", false,
        [
            new Group("synced", "From the college's course data (read-only)",
            [
                new Prop(Props.CourseId, "Course ID", dt.Label, "The college's course ID. Set by the integration layer"),
                new Prop(Props.SourceTitle, "Title in the college's data", dt.Label),
                new Prop(Props.LastSynced, "Last changed in the college's data", dt.Label, "When the college's data for this course last changed on the website", FormerNames: ["Last synced"])
            ]),
            new Group("marketing", "Marketing",
            [
                new Prop(Props.HeroImage, "Hero image", dt.Image, "Replaces the default subject photo"),
                new Prop(Props.VideoUrl, "Video link", dt.Text, "YouTube or Vimeo link"),
                new Prop(Props.StudentQuote, "Student quote", dt.TextArea),
                new Prop(Props.StudentName, "Student name", dt.Text, "For example: Ellie, Level 3 Fashion student"),
                new Prop(Props.CampaignMessage, "Campaign message", dt.Text, "A short message shown near the top of the course page")
            ])
        ]);

        var evt = await EnsureContentTypeAsync(Event, "Event", "icon-calendar-alt", false,
        [
            new Group("details", "Details",
            [
                new Prop(Props.EventType, "Event type", eventType, Mandatory: true),
                new Prop(Props.StartsAt, "Starts at", dt.DateTime, "UK time", true),
                new Prop(Props.EndsAt, "Ends at", dt.DateTime, "UK time"),
                new Prop(Props.Venue, "Venue", dt.Text, "For example: Moulsham Street campus"),
                new Prop(Props.Address, "Address", dt.TextArea),
                new Prop(Props.Summary, "Summary", dt.TextArea, "One or two sentences for event listings"),
                new Prop(Props.Body, "Description", dt.RichText),
                new Prop(Props.Image, "Image", dt.Image)
            ]),
            new Group("booking", "Booking",
            [
                new Prop(Props.Capacity, "Capacity", dt.Integer, "Total places. Leave empty for no limit"),
                new Prop(Props.BookingOpen, "Booking open", dt.Boolean)
            ])
        ]);

        var paymentsPage = await EnsureContentTypeAsync(PaymentsPage, "Payments page", "icon-credit-card", false,
            [new Group("content", "Content", [new Prop(Props.Intro, "Intro", dt.TextArea)])]);

        var contentPage = await EnsureContentTypeAsync(ContentPage, "Content page", "icon-document", false,
        [
            new Group("content", "Content",
            [
                new Prop(Props.Summary, "Summary", dt.TextArea),
                new Prop(Props.Body, "Body", dt.RichText)
            ])
        ]);

        var coursePicker = await EnsureDataTypeAsync("Course picker", Constants.PropertyEditors.Aliases.MultiNodeTreePicker, "Umb.PropertyEditorUi.ContentPicker",
            ValueStorageType.Ntext,
            new Dictionary<string, object>
            {
                ["startNode"] = new Dictionary<string, object> { ["type"] = "content" },
                ["filter"] = course.Key.ToString(),
                ["minNumber"] = 0,
                ["maxNumber"] = 0
            },
            dt.Text);

        var payableItem = await EnsureContentTypeAsync(PayableItem, "Payable item", "icon-coin", false,
        [
            new Group("item", "Payable item",
            [
                new Prop(Props.ItemType, "Type", itemType, Mandatory: true),
                new Prop(Props.Description, "Description", dt.TextArea, "Shown to the person paying"),
                new Prop(Props.Amount, "Amount (£)", amount, Mandatory: true),
                new Prop(Props.Courses, "Courses", coursePicker, "The courses this payment applies to"),
                new Prop(Props.DueDate, "Due date", dt.DateTime),
                new Prop(Props.Places, "Places", dt.Integer, "Trips only"),
                new Prop(Props.CostCode, "Cost code", dt.Text, "Finance cost code, passed to the payment provider"),
                new Prop(Props.TakesPlaceOn, "Takes place on", dt.DateTime, "Trips only")
            ])
        ]);

        var campaignFolder = await EnsureContentTypeAsync(CampaignFolder, "Campaigns", "icon-megaphone", false, []);
        var courseListing = await EnsureContentTypeAsync(CourseListing, "Courses", "icon-books", false,
            [new Group("content", "Content", [new Prop(Props.Intro, "Intro", dt.TextArea)])]);
        var eventListing = await EnsureContentTypeAsync(EventListing, "Events", "icon-calendar", false,
            [new Group("content", "Content", [new Prop(Props.Intro, "Intro", dt.TextArea)])]);
        var payableFolder = await EnsureContentTypeAsync(PayableItemFolder, "Payable items", "icon-wallet", true, []);

        var home = await EnsureContentTypeAsync(Home, "Home", "icon-home", true,
        [
            new Group("seo", "Search engines", [new Prop(Props.MetaDescription, "Meta description", dt.TextArea)]),
            new Group("video", "Video",
            [
                new Prop(Props.VideoHeading, "Video heading", dt.Text),
                new Prop(Props.VideoText, "Video text", dt.TextArea),
                new Prop(Props.VideoUrl, "Video link", dt.Text, "YouTube or Vimeo link"),
                new Prop(Props.VideoPoster, "Video poster image", dt.Image)
            ])
        ]);

        await EnsureAllowedChildrenAsync(campaignFolder, campaign);
        await EnsureAllowedChildrenAsync(courseListing, course);
        await EnsureAllowedChildrenAsync(eventListing, evt);
        await EnsureAllowedChildrenAsync(payableFolder, payableItem);
        await EnsureAllowedChildrenAsync(home, campaignFolder, courseListing, eventListing, paymentsPage, contentPage);
    }

    private async Task<DataTypes> ResolveBuiltInsAsync()
    {
        var text = await FindAsync(Constants.PropertyEditors.Aliases.TextBox, "Textstring")
                   ?? throw new InvalidOperationException("No Textstring data type found; the Umbraco install looks incomplete.");
        var textArea = await FindAsync(Constants.PropertyEditors.Aliases.TextArea, "Textarea") ?? text;
        var richText = await FindAsync(Constants.PropertyEditors.Aliases.RichText, "Richtext editor") ?? textArea;
        var image = await FindAsync(Constants.PropertyEditors.Aliases.MediaPicker3, "Image Media Picker") ?? text;
        var dateTime = await FindAsync(Constants.PropertyEditors.Aliases.DateTime, "Date Picker with time")
                       ?? await FindAsync(Constants.PropertyEditors.Aliases.DateTimeUnspecified, null)
                       ?? text;
        var integer = await FindAsync(Constants.PropertyEditors.Aliases.Integer, "Numeric") ?? text;
        var boolean = await FindAsync(Constants.PropertyEditors.Aliases.Boolean, "True/false") ?? text;
        var label = await FindAsync(Constants.PropertyEditors.Aliases.Label, "string", contains: true) ?? text;
        return new DataTypes(text, textArea, richText, image, dateTime, integer, boolean, label);
    }

    private async Task<IDataType?> FindAsync(string editorAlias, string? preferredName, bool contains = false)
    {
        var candidates = (await dataTypeService.GetByEditorAliasAsync(editorAlias)).OrderBy(d => d.Id).ToList();
        if (preferredName is not null)
        {
            var preferred = candidates.FirstOrDefault(d => d.Name is { } name &&
                (contains ? name.Contains(preferredName, StringComparison.OrdinalIgnoreCase) : name.Equals(preferredName, StringComparison.OrdinalIgnoreCase)));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return candidates.FirstOrDefault();
    }

    /// <summary>Finds or creates a configured data type. If creation fails, falls back so the site still boots.</summary>
    private async Task<IDataType> EnsureDataTypeAsync(
        string name,
        string editorAlias,
        string editorUiAlias,
        ValueStorageType storageType,
        Dictionary<string, object> configuration,
        IDataType fallback)
    {
        try
        {
            if (await dataTypeService.GetAsync(name) is { } existing)
            {
                return existing;
            }

            if (!propertyEditors.TryGet(editorAlias, out var editor) || editor is null)
            {
                logger.LogWarning("Property editor {Editor} not found; {Name} falls back to {Fallback}", editorAlias, name, fallback.Name);
                return fallback;
            }

            var dataType = new DataType(editor, configurationSerializer, -1)
            {
                Name = name,
                EditorUiAlias = editorUiAlias,
                DatabaseType = storageType,
                ConfigurationData = configuration
            };

            var attempt = await dataTypeService.CreateAsync(dataType, Constants.Security.SuperUserKey);
            if (attempt.Success && attempt.Result is not null)
            {
                logger.LogInformation("Created data type {Name}", name);
                return attempt.Result;
            }

            logger.LogWarning("Could not create data type {Name} ({Status}); falling back to {Fallback}", name, attempt.Status, fallback.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating data type {Name} failed; falling back to {Fallback}", name, fallback.Name);
        }

        return fallback;
    }

    private async Task<IContentType> EnsureContentTypeAsync(string alias, string name, string icon, bool allowedAsRoot, IReadOnlyList<Group> groups)
    {
        var contentType = contentTypeService.Get(alias);
        var changed = false;
        if (contentType is null)
        {
            contentType = new ContentType(shortStringHelper, -1)
            {
                Alias = alias,
                Name = name,
                Icon = icon,
                AllowedAsRoot = allowedAsRoot
            };
            changed = true;
        }

        foreach (var group in groups)
        {
            foreach (var prop in group.Props)
            {
                if (contentType.PropertyTypeExists(prop.Alias))
                {
                    // Idempotent rename of a label from an earlier version of the schema.
                    var existing = contentType.PropertyTypes.FirstOrDefault(p => p.Alias == prop.Alias);
                    if (existing is not null && prop.FormerNames is { Length: > 0 } former
                        && former.Contains(existing.Name, StringComparer.Ordinal))
                    {
                        existing.Name = prop.Name;
                        existing.Description = prop.Description;
                        changed = true;
                    }

                    continue;
                }

                if (!contentType.PropertyGroups.Contains(group.Alias))
                {
                    contentType.AddPropertyGroup(group.Alias, group.Name);
                }

                contentType.AddPropertyType(
                    new PropertyType(shortStringHelper, prop.DataType, prop.Alias)
                    {
                        Name = prop.Name,
                        Description = prop.Description,
                        Mandatory = prop.Mandatory
                    },
                    group.Alias,
                    group.Name);
                changed = true;
            }
        }

        if (changed)
        {
            await contentTypeService.SaveAsync(contentType, Constants.Security.SuperUserKey);
            logger.LogInformation("Saved document type {Alias}", alias);
            contentType = contentTypeService.Get(alias) ?? contentType;
        }

        return contentType;
    }

    private async Task EnsureAllowedChildrenAsync(IContentType parent, params IContentType[] children)
    {
        var allowed = (parent.AllowedContentTypes ?? []).ToList();
        var changed = false;
        foreach (var child in children)
        {
            if (allowed.All(a => a.Key != child.Key))
            {
                allowed.Add(new ContentTypeSort(child.Key, allowed.Count, child.Alias));
                changed = true;
            }
        }

        if (changed)
        {
            parent.AllowedContentTypes = allowed;
            await contentTypeService.SaveAsync(parent, Constants.Security.SuperUserKey);
        }
    }
}
