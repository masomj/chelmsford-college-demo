namespace Chelmsford.Web.Umbraco;

/// <summary>Document type and property aliases, so controllers, views and seeding agree.</summary>
public static class DocTypes
{
    public const string Home = "home";
    public const string CampaignFolder = "campaignFolder";
    public const string Campaign = "campaign";
    public const string CourseListing = "courseListing";
    public const string Course = "course";
    public const string EventListing = "eventListing";
    public const string Event = "event";
    public const string PaymentsPage = "paymentsPage";
    public const string ContentPage = "contentPage";
    public const string PayableItemFolder = "payableItemFolder";
    public const string PayableItem = "payableItem";

    public static class Props
    {
        // Shared
        public const string MetaDescription = "metaDescription";
        public const string Intro = "intro";
        public const string Summary = "summary";
        public const string Body = "body";
        public const string Image = "image";

        // Home
        public const string VideoHeading = "videoHeading";
        public const string VideoText = "videoText";
        public const string VideoUrl = "videoUrl";
        public const string VideoPoster = "videoPoster";

        // Campaign
        public const string Label = "label";
        public const string Heading = "heading";
        public const string Text = "text";
        public const string ButtonText = "buttonText";
        public const string ButtonUrl = "buttonUrl";
        public const string ShowFrom = "showFrom";
        public const string ShowUntil = "showUntil";

        // Course
        public const string CourseId = "courseId";
        public const string SourceTitle = "sourceTitle";
        public const string LastSynced = "lastSynced";
        public const string HeroImage = "heroImage";
        public const string StudentQuote = "studentQuote";
        public const string StudentName = "studentName";
        public const string CampaignMessage = "campaignMessage";

        // Event
        public const string EventType = "eventType";
        public const string StartsAt = "startsAt";
        public const string EndsAt = "endsAt";
        public const string Venue = "venue";
        public const string Address = "address";
        public const string Capacity = "capacity";
        public const string BookingOpen = "bookingOpen";

        // Payable item
        public const string ItemType = "itemType";
        public const string Description = "description";
        public const string Amount = "amount";
        public const string Courses = "courses";
        public const string DueDate = "dueDate";
        public const string Places = "places";
        public const string CostCode = "costCode";
        public const string TakesPlaceOn = "takesPlaceOn";
    }

    /// <summary>Values offered by the "Payable item type" dropdown, in the order editors see them.</summary>
    public static readonly IReadOnlyList<string> PayableItemTypes = ["Course deposit", "Materials fee", "Trip", "One-off payment"];
}
