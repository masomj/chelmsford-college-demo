namespace Chelmsford.Core.Courses;

/// <summary>
/// DEMO ONLY. The college's demo data has no campus field, so campus is derived from the
/// subject area until the real source is confirmed during discovery. In the real build this
/// comes straight from the college's data through the integration layer.
/// </summary>
public static class CampusMapping
{
    public const string MoulshamStreet = "Moulsham Street";
    public const string PrincesRoad = "Princes Road";

    public static readonly IReadOnlyList<string> All = [MoulshamStreet, PrincesRoad];

    private static readonly HashSet<string> PrincesRoadSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "Construction",
        "Electrical Installation",
        "Engineering",
        "Green Skills"
    };

    public static string ForSubject(string subject) =>
        PrincesRoadSubjects.Contains(subject) ? PrincesRoad : MoulshamStreet;
}
