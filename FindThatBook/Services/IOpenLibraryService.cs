using FindThatBook.Models;

namespace FindThatBook.Services
{
    public interface IOpenLibraryService
    {
        Task<List<InternalBookMetadata>> SearchBooksAsync(ExtractedQuery extractedQuery, string rawQuery);
    }
}
