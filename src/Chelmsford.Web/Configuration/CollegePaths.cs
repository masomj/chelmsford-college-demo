namespace Chelmsford.Web.Configuration;

/// <summary>
/// Where the college's own material and the site's data live. Both sit outside the web project
/// (and outside git): locally next to the repo, in the container on a mounted volume.
/// </summary>
public sealed class CollegePaths
{
    public CollegePaths(string contentPath, string dataPath)
    {
        ContentPath = contentPath;
        DataPath = dataPath;
    }

    /// <summary>The college-content folder (images, source data).</summary>
    public string ContentPath { get; }

    public string ImagesPath => Path.Combine(ContentPath, "images");

    /// <summary>Databases, data-protection keys and other state that must survive a redeploy.</summary>
    public string DataPath { get; }

    public string KeysPath => Path.Combine(DataPath, "keys");

    public static CollegePaths From(IConfiguration configuration, string contentRootPath)
    {
        string Resolve(string? configured, string fallback)
        {
            var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(contentRootPath, value));
        }

        return new CollegePaths(
            Resolve(configuration["College:ContentPath"], "college-content"),
            Resolve(configuration["College:DataPath"], "data"));
    }
}
