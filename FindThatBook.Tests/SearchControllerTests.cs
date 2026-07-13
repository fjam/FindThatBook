using FindThatBook.Controllers;
using FindThatBook.Models;
using FindThatBook.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FindThatBook.Tests
{
    public class SearchControllerTests
    {
        private readonly Mock<IGeminiService> _mockGeminiService;
        private readonly Mock<IOpenLibraryService> _mockOpenLibraryService;
        private readonly Mock<ILogger<SearchController>> _mockLogger;
        private readonly SearchController _controller;

        public SearchControllerTests()
        {
            _mockGeminiService = new Mock<IGeminiService>();
            _mockOpenLibraryService = new Mock<IOpenLibraryService>();
            _mockLogger = new Mock<ILogger<SearchController>>();
            
            _controller = new SearchController(
                _mockGeminiService.Object, 
                _mockOpenLibraryService.Object, 
                _mockLogger.Object
            );
        }

        [Fact]
        public async Task Search_WhenQueryIsEmpty_ReturnsBadRequest()
        {
            var request = new SearchRequest { Query = "" };
            var result = await _controller.Search(request);
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequestResult.Value);
        }

        [Fact]
        public async Task Search_WhenGeminiApiKeyIsMissing_AbortsSearchAndReturnsBadRequest()
        {
            var request = new SearchRequest { Query = "tolkien hobbit" };
            _mockGeminiService.Setup(s => s.HasApiKey()).Returns(false);
            var result = await _controller.Search(request);
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Gemini API key is required", badRequestResult.Value?.ToString() ?? string.Empty);
            _mockGeminiService.Verify(s => s.ExtractQueryAsync(It.IsAny<string>()), Times.Never);
            _mockOpenLibraryService.Verify(s => s.SearchBooksAsync(It.IsAny<ExtractedQuery>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Search_WhenApiKeyIsProvided_PerformsSearchAndReturnsCandidates()
        {
            var request = new SearchRequest { Query = "tolkien hobbit" };
            var extractedQuery = new ExtractedQuery { Title = "The Hobbit", Author = "J.R.R. Tolkien" };
            var candidates = new List<InternalBookMetadata>
            {
                new InternalBookMetadata { Key = "/works/OL26320W", Title = "The Hobbit" }
            };
            var expectedResponse = new List<BookCandidate>
            {
                new BookCandidate { Title = "The Hobbit", Author = "J.R.R. Tolkien", Explanation = "Exact Match" }
            };

            _mockGeminiService.Setup(s => s.HasApiKey()).Returns(true);
            _mockGeminiService.Setup(s => s.ExtractQueryAsync(request.Query))
                .ReturnsAsync(extractedQuery);
            _mockOpenLibraryService.Setup(s => s.SearchBooksAsync(extractedQuery, request.Query))
                .ReturnsAsync(candidates);
            _mockGeminiService.Setup(s => s.RerankAndExplainAsync(request.Query, extractedQuery, candidates))
                .ReturnsAsync(expectedResponse);

            var result = await _controller.Search(request);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var actualCandidates = Assert.IsAssignableFrom<List<BookCandidate>>(okResult.Value);
            Assert.Single(actualCandidates);
            Assert.Equal("The Hobbit", actualCandidates[0].Title);
        }
    }
}
