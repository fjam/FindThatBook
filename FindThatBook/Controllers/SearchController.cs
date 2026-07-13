using FindThatBook.Models;
using FindThatBook.Services;
using Microsoft.AspNetCore.Mvc;

namespace FindThatBook.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SearchController : ControllerBase
    {
        private readonly IGeminiService _geminiService;
        private readonly IOpenLibraryService _openLibraryService;
        private readonly ILogger<SearchController> _logger;

        public SearchController(
            IGeminiService geminiService,
            IOpenLibraryService openLibraryService,
            ILogger<SearchController> logger)
        {
            _geminiService = geminiService;
            _openLibraryService = openLibraryService;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> Search([FromBody] SearchRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest(new { error = "Search query is required." });
            }
         
            if (!_geminiService.HasApiKey())
            {
                _logger.LogWarning("Search aborted: Gemini API key is missing.");
                return BadRequest(new { 
                    error = "Gemini API key is required but was not provided. Configure Gemini:ApiKey in appsettings.json."
                });
            }

            try
            {
                _logger.LogInformation("Processing search query: '{Query}'", request.Query);

                // 2. Use AI to Extract Useful information
                var extractedQuery = await _geminiService.ExtractQueryAsync(request.Query);
                _logger.LogInformation("AI Extracted fields - Title: '{Title}', Author: '{Author}', Keywords: {Keywords}", 
                    extractedQuery.Title, extractedQuery.Author, string.Join(", ", extractedQuery.Keywords));

                // 3. search Open Library for candidates
                var candidates = await _openLibraryService.SearchBooksAsync(extractedQuery, request.Query);
                _logger.LogInformation("Open Library returned {Count} matching candidates.", candidates.Count);

                if (candidates.Count == 0)
                {
                    return Ok(new List<BookCandidate>());
                }

                var rerankedCandidates = await _geminiService.RerankAndExplainAsync(
                    request.Query, 
                    extractedQuery, 
                    candidates
                );

                _logger.LogInformation("Successfully completed search and re-ranking. Returning {Count} candidates.", rerankedCandidates.Count);
                return Ok(rerankedCandidates);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing search for query: '{Query}'", request.Query);
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
