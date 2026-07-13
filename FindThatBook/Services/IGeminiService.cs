using FindThatBook.Models;

namespace FindThatBook.Services
{
    public interface IGeminiService
    {
        bool HasApiKey();
        Task<ExtractedQuery> ExtractQueryAsync(string rawQuery);
        Task<List<BookCandidate>> RerankAndExplainAsync(string rawQuery, ExtractedQuery extractedQuery, List<InternalBookMetadata> candidates);
    }
}
