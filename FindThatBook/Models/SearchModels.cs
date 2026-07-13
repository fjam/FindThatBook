using System.Text.Json.Serialization;

namespace FindThatBook.Models
{
    public class SearchRequest
    {
        public string Query { get; set; } = string.Empty;
    }

    public class ExtractedQuery
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = new();
    }

    public class BookCandidate
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; } = string.Empty;

        [JsonPropertyName("open_library_id")]
        public string OpenLibraryId { get; set; } = string.Empty;

        [JsonPropertyName("open_library_url")]
        public string OpenLibraryUrl { get; set; } = string.Empty;

        [JsonPropertyName("cover_url")]
        public string? CoverUrl { get; set; }
    }

    public class InternalBookMetadata
    {
        public string Key { get; set; } = string.Empty; // e.g. /works/OL12345W
        public string Title { get; set; } = string.Empty;
        public List<AuthorWithRole> Authors { get; set; } = new();
        public int? FirstPublishYear { get; set; }
        public string? CoverUrl { get; set; }
        public string MatchingTier { get; set; } = string.Empty;
        public string MatchingTierReason { get; set; } = string.Empty;
    }

    public class AuthorWithRole
    {
        public string Name { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty; // e.g. /authors/OL12345A
        public string Role { get; set; } = "Primary"; // "Primary" or "Contributor"
        public string DetailedRole { get; set; } = "Author"; // e.g. "Illustrator", "Editor", etc.
    }
}
