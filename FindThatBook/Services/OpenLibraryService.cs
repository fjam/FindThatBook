using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FindThatBook.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FindThatBook.Services
{
    public class OpenLibraryService : IOpenLibraryService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OpenLibraryService> _logger;

        public OpenLibraryService(HttpClient httpClient, ILogger<OpenLibraryService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            
            // Add required User-Agent for Open Library API guidelines
            //if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            //{
            //    _httpClient.DefaultRequestHeaders.Add("User-Agent", "FindThatBook/1.0 (contact@findthatbook.com)");
            //}
        }

        public async Task<List<InternalBookMetadata>> SearchBooksAsync(ExtractedQuery extractedQuery, string rawQuery)
        {
            // If it is an author-only search (extracted Author but no Title)
            if (string.IsNullOrWhiteSpace(extractedQuery.Title) && !string.IsNullOrWhiteSpace(extractedQuery.Author))
            {
                _logger.LogInformation("Performing author-only fallback search for: {Author}", extractedQuery.Author);
                return await SearchByAuthorFallbackAsync(extractedQuery.Author);
            }

            _logger.LogInformation("Performing standard Open Library search. Title: '{Title}', Author: '{Author}'", extractedQuery.Title, extractedQuery.Author);

            // Construct search query URL
            var queryParams = new List<string>();
            if (!string.IsNullOrWhiteSpace(extractedQuery.Title))
            {
                queryParams.Add($"title={Uri.EscapeDataString(extractedQuery.Title)}");
            }
            if (!string.IsNullOrWhiteSpace(extractedQuery.Author))
            {
                queryParams.Add($"author={Uri.EscapeDataString(extractedQuery.Author)}");
            }

            if (queryParams.Count == 0)
            {
                // Fallback to raw query if no title/author extracted
                queryParams.Add($"q={Uri.EscapeDataString(rawQuery)}");
            }

            var url = $"https://openlibrary.org/search.json?{string.Join("&", queryParams)}&limit=15";
            
            List<InternalBookMetadata> candidates = new();

            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Open Library Search API returned error status: {StatusCode}", response.StatusCode);
                    return candidates;
                }

                var jsonString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonString);
                
                if (doc.RootElement.TryGetProperty("docs", out var docsProp) && docsProp.ValueKind == JsonValueKind.Array)
                {
                    var tasks = new List<Task<InternalBookMetadata?>>();
                    
                    foreach (var docEl in docsProp.EnumerateArray())
                    {
                        tasks.Add(ProcessSearchDocAsync(docEl, extractedQuery));
                    }

                    var results = await Task.WhenAll(tasks);
                    
                    foreach (var metadata in results)
                    {
                        if (metadata != null)
                        {
                            candidates.Add(metadata);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during Open Library search.");
            }

            // Order candidates by Matching Tier strength
            return candidates
                .OrderBy(c => GetTierRank(c.MatchingTier))
                .Take(15) // Limit to top 15 candidates for re-ranking
                .ToList();
        }

        private async Task<InternalBookMetadata?> ProcessSearchDocAsync(JsonElement docEl, ExtractedQuery extractedQuery)
        {
            try
            {
                if (!docEl.TryGetProperty("key", out var keyProp) || keyProp.GetString() == null)
                {
                    return null;
                }

                var key = keyProp.GetString()!; // e.g. /works/OL12345W
                var title = docEl.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                
                int? firstPublishYear = null;
                if (docEl.TryGetProperty("first_publish_year", out var yearProp) && yearProp.ValueKind == JsonValueKind.Number)
                {
                    firstPublishYear = yearProp.GetInt32();
                }

                string? coverUrl = null;
                if (docEl.TryGetProperty("cover_i", out var coverIProp))
                {
                    if (coverIProp.ValueKind == JsonValueKind.Number)
                    {
                        coverUrl = $"https://covers.openlibrary.org/b/id/{coverIProp.GetInt32()}-M.jpg";
                    }
                    else if (coverIProp.ValueKind == JsonValueKind.String && int.TryParse(coverIProp.GetString(), out var coverId))
                    {
                        coverUrl = $"https://covers.openlibrary.org/b/id/{coverId}-M.jpg";
                    }
                }

                // Resolve authors from Search Doc (as a fallback or starting point)
                var searchAuthorKeys = new List<string>();
                var searchAuthorNames = new List<string>();

                if (docEl.TryGetProperty("author_key", out var authKeysProp) && authKeysProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in authKeysProp.EnumerateArray())
                    {
                        if (el.GetString() != null) searchAuthorKeys.Add(el.GetString()!);
                    }
                }

                if (docEl.TryGetProperty("author_name", out var authNamesProp) && authNamesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in authNamesProp.EnumerateArray())
                    {
                        if (el.GetString() != null) searchAuthorNames.Add(el.GetString()!);
                    }
                }

                // Build mapping of author keys to names from search doc
                var authorMap = new Dictionary<string, string>();
                for (int i = 0; i < Math.Min(searchAuthorKeys.Count, searchAuthorNames.Count); i++)
                {
                    var cleanKey = searchAuthorKeys[i].StartsWith("/authors/") ? searchAuthorKeys[i] : $"/authors/{searchAuthorKeys[i]}";
                    authorMap[cleanKey] = searchAuthorNames[i];
                }

                // Resolve primary authors from canonical work record as required by PDF
                var primaryAuthorKeys = await FetchPrimaryAuthorKeysAsync(key);

                var resolvedAuthors = new List<AuthorWithRole>();

                if (primaryAuthorKeys.Count > 0)
                {
                    // Mark authors from work.authors as primary
                    foreach (var authKey in primaryAuthorKeys)
                    {
                        var name = "Unknown Author";
                        if (authorMap.TryGetValue(authKey, out var mappedName))
                        {
                            name = mappedName;
                        }
                        else
                        {
                            // Fetch author details if not found in search doc
                            name = await FetchAuthorNameAsync(authKey) ?? "Unknown Author";
                        }

                        resolvedAuthors.Add(new AuthorWithRole
                        {
                            Name = name,
                            Key = authKey,
                            Role = "Primary",
                            DetailedRole = "Author"
                        });
                    }

                    // Any other author in search doc is a contributor
                    foreach (var entry in authorMap)
                    {
                        if (!primaryAuthorKeys.Contains(entry.Key))
                        {
                            resolvedAuthors.Add(new AuthorWithRole
                            {
                                Name = entry.Value,
                                Key = entry.Key,
                                Role = "Contributor",
                                DetailedRole = "Contributor" // e.g. Illustrator, Editor, Adaptor
                            });
                        }
                    }
                }
                else
                {
                    // Fallback if work has no authors in its record: treat first author in search doc as primary, rest as contributors
                    for (int i = 0; i < searchAuthorKeys.Count; i++)
                    {
                        var cleanKey = searchAuthorKeys[i].StartsWith("/authors/") ? searchAuthorKeys[i] : $"/authors/{searchAuthorKeys[i]}";
                        resolvedAuthors.Add(new AuthorWithRole
                        {
                            Name = searchAuthorNames.Count > i ? searchAuthorNames[i] : "Unknown Author",
                            Key = cleanKey,
                            Role = i == 0 ? "Primary" : "Contributor",
                            DetailedRole = i == 0 ? "Author" : "Contributor"
                        });
                    }
                }

                // Apply matching hierarchy to categorize candidate
                var metadata = new InternalBookMetadata
                {
                    Key = key,
                    Title = title,
                    Authors = resolvedAuthors,
                    FirstPublishYear = firstPublishYear,
                    CoverUrl = coverUrl
                };

                DetermineMatchingTier(metadata, extractedQuery);

                return metadata;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing search document.");
                return null;
            }
        }

        private async Task<List<string>> FetchPrimaryAuthorKeysAsync(string workKey)
        {
            var keys = new List<string>();
            var workId = workKey.Replace("/works/", "");
            var url = $"https://openlibrary.org/works/{workId}.json";

            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    return keys;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("authors", out var authorsProp) && authorsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var authorEl in authorsProp.EnumerateArray())
                    {
                        if (authorEl.TryGetProperty("author", out var innerAuthor) && 
                            innerAuthor.TryGetProperty("key", out var keyProp) && 
                            keyProp.GetString() != null)
                        {
                            var rawKey = keyProp.GetString()!;
                            var cleanKey = rawKey.StartsWith("/authors/") ? rawKey : $"/authors/{rawKey}";
                            keys.Add(cleanKey);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching work details for {WorkKey}", workKey);
            }

            return keys;
        }

        private async Task<string?> FetchAuthorNameAsync(string authorKey)
        {
            var authorId = authorKey.Replace("/authors/", "");
            var url = $"https://openlibrary.org/authors/{authorId}.json";

            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                {
                    return nameProp.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching author name for {AuthorKey}", authorKey);
            }

            return null;
        }

        private async Task<List<InternalBookMetadata>> SearchByAuthorFallbackAsync(string authorName)
        {
            var candidates = new List<InternalBookMetadata>();

            // Step 1: Find author key from Search API
            var searchUrl = $"https://openlibrary.org/search.json?author={Uri.EscapeDataString(authorName)}&limit=5";
            string? authorKey = null;
            string canonicalAuthorName = authorName;

            try
            {
                var response = await _httpClient.GetAsync(searchUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("docs", out var docsProp) && 
                        docsProp.ValueKind == JsonValueKind.Array && 
                        docsProp.GetArrayLength() > 0)
                    {
                        var firstDoc = docsProp[0];
                        if (firstDoc.TryGetProperty("author_key", out var keysProp) && 
                            keysProp.ValueKind == JsonValueKind.Array && 
                            keysProp.GetArrayLength() > 0)
                        {
                            authorKey = keysProp[0].GetString();
                        }
                        if (firstDoc.TryGetProperty("author_name", out var namesProp) && 
                            namesProp.ValueKind == JsonValueKind.Array && 
                            namesProp.GetArrayLength() > 0)
                        {
                            canonicalAuthorName = namesProp[0].GetString() ?? authorName;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding author key for {AuthorName}", authorName);
            }

            if (string.IsNullOrWhiteSpace(authorKey))
            {
                return candidates;
            }

            // Ensure key is clean
            var authorId = authorKey.Replace("/authors/", "");
            
            // Get Canonical author details to be sure
            var canonName = await FetchAuthorNameAsync(authorId);
            if (!string.IsNullOrWhiteSpace(canonName))
            {
                canonicalAuthorName = canonName;
            }

            // Step 2: Fetch works by author
            var worksUrl = $"https://openlibrary.org/authors/{authorId}/works.json?limit=15";
            try
            {
                var response = await _httpClient.GetAsync(worksUrl);
                if (!response.IsSuccessStatusCode) return candidates;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("entries", out var entriesProp) && entriesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entriesProp.EnumerateArray())
                    {
                        var key = entry.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? "" : "";
                        var title = entry.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                        
                        int? publishYear = null;
                        if (entry.TryGetProperty("first_publish_date", out var dateProp) && dateProp.GetString() != null)
                        {
                            var dateStr = dateProp.GetString()!;
                            var yearMatch = Regex.Match(dateStr, @"\b\d{4}\b");
                            if (yearMatch.Success && int.TryParse(yearMatch.Value, out var parsedYear))
                            {
                                publishYear = parsedYear;
                            }
                        }

                        string? coverUrl = null;
                        if (entry.TryGetProperty("covers", out var coversProp) && 
                            coversProp.ValueKind == JsonValueKind.Array && 
                            coversProp.GetArrayLength() > 0)
                        {
                            var coverIdEl = coversProp[0];
                            if (coverIdEl.ValueKind == JsonValueKind.Number)
                            {
                                coverUrl = $"https://covers.openlibrary.org/b/id/{coverIdEl.GetInt32()}-M.jpg";
                            }
                            else if (coverIdEl.ValueKind == JsonValueKind.String && int.TryParse(coverIdEl.GetString(), out var parsedCoverId))
                            {
                                coverUrl = $"https://covers.openlibrary.org/b/id/{parsedCoverId}-M.jpg";
                            }
                        }

                        var metadata = new InternalBookMetadata
                        {
                            Key = key,
                            Title = title,
                            Authors = new List<AuthorWithRole>
                            {
                                new AuthorWithRole
                                {
                                    Name = canonicalAuthorName,
                                    Key = $"/authors/{authorId}",
                                    Role = "Primary",
                                    DetailedRole = "Author"
                                }
                            },
                            FirstPublishYear = publishYear,
                            CoverUrl = coverUrl,
                            MatchingTier = "AuthorFallback",
                            MatchingTierReason = $"Author-only fallback: top works by {canonicalAuthorName}"
                        };

                        candidates.Add(metadata);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching works for author key {AuthorKey}", authorKey);
            }

            return candidates;
        }

        private void DetermineMatchingTier(InternalBookMetadata candidate, ExtractedQuery extractedQuery)
        {
            var isTitleMatch = false;
            var isNearTitleMatch = false;
            var hasPrimaryAuthorMatch = false;
            var hasContributorAuthorMatch = false;

            if (!string.IsNullOrWhiteSpace(extractedQuery.Title))
            {
                isTitleMatch = NormalizeString(candidate.Title) == NormalizeString(extractedQuery.Title);
                isNearTitleMatch = isTitleMatch || IsNearMatch(candidate.Title, extractedQuery.Title);
            }

            if (!string.IsNullOrWhiteSpace(extractedQuery.Author))
            {
                foreach (var author in candidate.Authors)
                {
                    if (IsAuthorMatch(extractedQuery.Author, author.Name))
                    {
                        if (author.Role == "Primary")
                        {
                            hasPrimaryAuthorMatch = true;
                        }
                        else
                        {
                            hasContributorAuthorMatch = true;
                        }
                    }
                }
            }

            // Matching Hierarchy application:
            // a. Exact/normalized title + primary author match (strongest)
            if (isTitleMatch && hasPrimaryAuthorMatch)
            {
                candidate.MatchingTier = "ExactTitlePrimaryAuthor";
                candidate.MatchingTierReason = "Exact title match and primary author match.";
                return;
            }

            // b. Exact/normalized title + contributor-only author (lower rank)
            if (isTitleMatch && hasContributorAuthorMatch && !hasPrimaryAuthorMatch)
            {
                candidate.MatchingTier = "ExactTitleContributorAuthor";
                candidate.MatchingTierReason = "Exact title match but author is listed as a contributor.";
                return;
            }

            // c. Near-match title + author match (candidate)
            if (isNearTitleMatch && (hasPrimaryAuthorMatch || hasContributorAuthorMatch))
            {
                candidate.MatchingTier = "NearTitleAuthor";
                candidate.MatchingTierReason = hasPrimaryAuthorMatch 
                    ? "Near title match with primary author match." 
                    : "Near title match with contributor author match.";
                return;
            }

            // d. Author-only match (fallback if query had author but we couldn't match title or no title was provided)
            if (hasPrimaryAuthorMatch || hasContributorAuthorMatch)
            {
                candidate.MatchingTier = "AuthorFallback";
                candidate.MatchingTierReason = "Author match with no matching title.";
                return;
            }

            // e. Keyword / Search relevance match
            candidate.MatchingTier = "KeywordMatch";
            candidate.MatchingTierReason = "Keyword or search relevance match.";
        }

        private int GetTierRank(string tier)
        {
            return tier switch
            {
                "ExactTitlePrimaryAuthor" => 1,
                "ExactTitleContributorAuthor" => 2,
                "NearTitleAuthor" => 3,
                "AuthorFallback" => 4,
                "KeywordMatch" => 5,
                _ => 6
            };
        }

        private string NormalizeString(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            
            // Convert to lowercase and normalize diacritics
            var normalized = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            
            foreach (var c in normalized)
            {
                var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                    {
                        sb.Append(c);
                    }
                }
            }
            
            // Replace multiple spaces with a single space
            return Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
        }

        private bool IsNearMatch(string titleA, string titleB)
        {
            var normA = NormalizeString(titleA);
            var normB = NormalizeString(titleB);
            
            if (string.IsNullOrEmpty(normA) || string.IsNullOrEmpty(normB)) return false;
            
            if (normA == normB) return true;
            if (normA.Contains(normB) || normB.Contains(normA)) return true;
            
            // Word overlap check
            var wordsA = normA.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var wordsB = normB.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            var intersect = wordsA.Intersect(wordsB, StringComparer.OrdinalIgnoreCase).Count();
            var minLength = Math.Min(wordsA.Length, wordsB.Length);
            
            if (minLength == 0) return false;
            
            double overlapRatio = (double)intersect / minLength;
            return overlapRatio >= 0.6;
        }

        private bool IsAuthorMatch(string extractedAuthor, string candidateAuthor)
        {
            var normExtracted = NormalizeString(extractedAuthor);
            var normCandidate = NormalizeString(candidateAuthor);
            
            if (string.IsNullOrEmpty(normExtracted) || string.IsNullOrEmpty(normCandidate)) return false;
            
            if (normExtracted == normCandidate) return true;
            if (normExtracted.Contains(normCandidate) || normCandidate.Contains(normExtracted)) return true;
            
            var tokensExtracted = normExtracted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tokensCandidate = normCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            var common = tokensExtracted.Intersect(tokensCandidate, StringComparer.OrdinalIgnoreCase)
                                         .Where(t => t.Length > 1)
                                         .Any();
            return common;
        }
    }
}
