namespace Vespa.Models;

/// <summary>
/// Naming helpers shared by attribute metadata and schema generation.
/// </summary>
public static class VespaNaming
{
    /// <summary>
    /// Converts a C# member name to the snake_case form Vespa fields use,
    /// matching the JSON serialization policy (<c>ArtistName</c> → <c>artist_name</c>).
    /// </summary>
    public static string ToSnakeCase(string name) =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
