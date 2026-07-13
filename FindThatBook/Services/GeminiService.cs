using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FindThatBook.Models;
using Microsoft.Extensions.Configuration;

namespace FindThatBook.Services
{
    public class GeminiService : IGeminiService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        public GeminiService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _configuration = configuration;
        }

        public bool HasApiKey()
        {
            return !string.IsNullOrWhiteSpace(GetApiKey());
        }

        private string GetApiKey()
        {
            var configKey = _configuration["Gemini:ApiKey"];
            if (!string.IsNullOrWhiteSpace(configKey))
            {
                return configKey.Trim();
            }

            var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                return envKey.Trim();
            }

            return string.Empty;
        }

        private string GetModelName()
        {
            var configModel = _configuration["Gemini:Model"];
            if (!string.IsNullOrWhiteSpace(configModel))
            {
                return configModel.Trim();
            }

            return "gemini-3.5-flash";
        }

        public async Task<ExtractedQuery> ExtractQueryAsync(string rawQuery)
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException("Gemini API Key is missing. Please configure it in the UI settings, set the GEMINI_API_KEY environment variable, or add it to appsettings.json.");
            }

            var modelName = GetModelName();

            var prompt = $@"
Analyze the following messy library discovery search query. Extract:
1. Title: The most likely book title, or null if none.
2. Author: The most likely author name, or null if none.
3. Keywords: An array of any other terms (like publication year '1937', edition tags 'illustrated', 'deluxe', translation, volume, publisher, etc.).

Raw user query: ""{rawQuery}""

Return ONLY a JSON object that matches this C# class representation:
{{
  ""title"": ""extracted title or null"",
  ""author"": ""extracted author or null"",
  ""keywords"": [""keyword1"", ""keyword2""]
}}
Do not include any markdown tags, backticks, or other text outside the JSON.
";

            try
            {
                var responseJson = await CallGeminiApiAsync(prompt, apiKey, modelName);
                if (responseJson != null)
                {
                    responseJson = CleanJsonResponse(responseJson);
                    return ParseExtractedQuery(responseJson);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error calling Gemini for field extraction: {ex.Message}", ex);
            }

            throw new InvalidOperationException("Failed to extract query fields using Gemini AI.");
        }

        public async Task<List<BookCandidate>> RerankAndExplainAsync(
            string rawQuery,
            ExtractedQuery extractedQuery,
            List<InternalBookMetadata> candidates)
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException("Gemini API Key is missing. Please configure it in the UI settings, set the GEMINI_API_KEY environment variable, or add it to appsettings.json.");
            }

            if (candidates.Count == 0)
            {
                return new List<BookCandidate>();
            }

            var modelName = GetModelName();

            // Build list of candidate representations for the LLM
            var candidateListStr = new StringBuilder();
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var authorsStr = string.Join(", ", c.Authors.Select(a => $"{a.Name} ({a.Role} - {a.DetailedRole})"));
                candidateListStr.AppendLine($"Candidate {i + 1}:");
                candidateListStr.AppendLine($"- ID: {c.Key}");
                candidateListStr.AppendLine($"- Title: {c.Title}");
                candidateListStr.AppendLine($"- Resolved Authors: {authorsStr}");
                candidateListStr.AppendLine($"- First Publish Year: {c.FirstPublishYear}");
                candidateListStr.AppendLine($"- Algorithmic Matching Tier: {c.MatchingTier} ({c.MatchingTierReason})");
                candidateListStr.AppendLine();
            }

            var prompt = $@"
You are a library catalog assistant. Re-rank these candidate books based on how well they match the raw user query and the extracted search fields, then generate a 1-sentence explanation of why each book matches the query.

Raw User Query: ""{rawQuery}""
AI Extracted Search Fields:
- Title: ""{extractedQuery.Title}""
- Author: ""{extractedQuery.Author}""
- Keywords: {JsonSerializer.Serialize(extractedQuery.Keywords)}

Open Library Candidates:
{candidateListStr}

Rules for Re-ranking and Explanations:
1. Re-rank candidates in order of match strength:
   - Primary author + exact title matches are strongest.
   - Contributor-only author + exact title matches are lower signal than primary authors. Explain this role difference (e.g. J.R.R. Tolkien is primary author, Dixon is illustrator/adaptor).
   - Near matches/partial title or author matches follow.
   - Author-only matches are fallback matches.
   - Keyword-only matches are the weakest.
2. The explanation must be a single, concise sentence (max 2) citing concrete matched fields (e.g., ""Exact title match; Tolkien is primary author; Dixon listed as adaptor."").
3. Make sure to return up to 5 candidates in the final ordered list.
4. Output MUST be ONLY a JSON array matching the structure of this C# model:
[
  {{
    ""title"": ""The Hobbit"",
    ""author"": ""J.R.R. Tolkien"",
    ""first_publish_year"": 1937,
    ""explanation"": ""Exact title match; Tolkien is primary author; Dixon listed as adaptor."",
    ""open_library_id"": ""OL12345W"",
    ""open_library_url"": ""https://openlibrary.org/works/OL12345W"",
    ""cover_url"": ""https://covers.openlibrary.org/b/id/12345-M.jpg""
  }}
]
Do not wrap in markdown or backticks. Return the JSON array directly.
";

            try
            {
                var responseJson = await CallGeminiApiAsync(prompt, apiKey, modelName);
                if (responseJson != null)
                {
                    responseJson = CleanJsonResponse(responseJson);
                    var result = ParseBookCandidates(responseJson);
                    if (result != null && result.Count > 0)
                    {
                        var matchedCandidates = new List<BookCandidate>();
                        foreach (var res in result)
                        {
                            var original = candidates.FirstOrDefault(c =>
                                c.Key == res.OpenLibraryId ||
                                c.Key.Replace("/works/", "") == res.OpenLibraryId ||
                                c.Key.Replace("/works/", "") == res.OpenLibraryId.Replace("/works/", "")
                            );

                            if (original != null)
                            {
                                res.OpenLibraryId = original.Key.Replace("/works/", "");
                                res.OpenLibraryUrl = $"https://openlibrary.org{original.Key}";
                                res.CoverUrl = original.CoverUrl;
                                matchedCandidates.Add(res);
                            }
                            else
                            {
                                var originalByName = candidates.FirstOrDefault(c =>
                                    c.Title.Equals(res.Title, StringComparison.OrdinalIgnoreCase)
                                );
                                if (originalByName != null)
                                {
                                    res.OpenLibraryId = originalByName.Key.Replace("/works/", "");
                                    res.OpenLibraryUrl = $"https://openlibrary.org{originalByName.Key}";
                                    res.CoverUrl = originalByName.CoverUrl;
                                    matchedCandidates.Add(res);
                                }
                                else
                                {
                                    if (!res.OpenLibraryId.StartsWith("/works/") && !res.OpenLibraryId.StartsWith("OL"))
                                    {
                                        continue;
                                    }
                                    var cleanId = res.OpenLibraryId.Replace("/works/", "");
                                    res.OpenLibraryId = cleanId;
                                    res.OpenLibraryUrl = $"https://openlibrary.org/works/{cleanId}";
                                    matchedCandidates.Add(res);
                                }
                            }
                        }

                        return matchedCandidates;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error calling Gemini for re-ranking: {ex.Message}", ex);
            }

            throw new InvalidOperationException("Failed to re-rank and explain candidates using Gemini AI.");
        }

        private async Task<string?> CallGeminiApiAsync(string prompt, string apiKey, string model)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    maxOutputTokens = 4096
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);

                if (doc.RootElement.TryGetProperty("candidates", out var candidatesProp) &&
                    candidatesProp.ValueKind == JsonValueKind.Array &&
                    candidatesProp.GetArrayLength() > 0)
                {
                    var firstCandidate = candidatesProp[0];
                    if (firstCandidate.TryGetProperty("content", out var contentProp) &&
                        contentProp.TryGetProperty("parts", out var partsProp) &&
                        partsProp.ValueKind == JsonValueKind.Array &&
                        partsProp.GetArrayLength() > 0)
                    {
                        var firstPart = partsProp[0];
                        if (firstPart.TryGetProperty("text", out var textProp))
                        {
                            return textProp.GetString();
                        }
                    }
                }
            }
            else
            {
                var errorMsg = await response.Content.ReadAsStringAsync();
                throw new Exception($"Gemini API returned error status {response.StatusCode}: {errorMsg}");
            }

            return null;
        }

        private string CleanJsonResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Trim();

            int firstCurly = text.IndexOf('{');
            int lastCurly = text.LastIndexOf('}');
            int firstSquare = text.IndexOf('[');
            int lastSquare = text.LastIndexOf(']');

            if (firstCurly >= 0 && lastCurly > firstCurly && (firstSquare < 0 || firstCurly < firstSquare))
            {
                return text.Substring(firstCurly, lastCurly - firstCurly + 1);
            }
            else if (firstSquare >= 0 && lastSquare > firstSquare)
            {
                return text.Substring(firstSquare, lastSquare - firstSquare + 1);
            }

            return text;
        }

        private ExtractedQuery ParseExtractedQuery(string json)
        {
            var extracted = new ExtractedQuery();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    root = root[0];
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("extractedQuery", out var innerProp))
                    {
                        root = innerProp;
                    }
                    else if (root.TryGetProperty("result", out var innerResult))
                    {
                        root = innerResult;
                    }
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                    {
                        extracted.Title = titleProp.GetString();
                    }
                    if (root.TryGetProperty("author", out var authorProp) && authorProp.ValueKind == JsonValueKind.String)
                    {
                        extracted.Author = authorProp.GetString();
                    }
                    if (root.TryGetProperty("keywords", out var keywordsProp) && keywordsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in keywordsProp.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                var val = item.GetString();
                                if (!string.IsNullOrEmpty(val))
                                {
                                    extracted.Keywords.Add(val);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback to regex parsing if JSON document model deserialization fails
                var titleMatch = Regex.Match(json, @"""title""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
                if (titleMatch.Success) extracted.Title = titleMatch.Groups[1].Value;

                var authorMatch = Regex.Match(json, @"""author""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
                if (authorMatch.Success) extracted.Author = authorMatch.Groups[1].Value;

                var keywordsMatch = Regex.Match(json, @"""keywords""\s*:\s*\[([^\]]*)\]", RegexOptions.IgnoreCase);
                if (keywordsMatch.Success)
                {
                    var keys = keywordsMatch.Groups[1].Value;
                    foreach (Match m in Regex.Matches(keys, @"""([^""]*)"""))
                    {
                        extracted.Keywords.Add(m.Groups[1].Value);
                    }
                }
            }

            return extracted;
        }

        private List<BookCandidate> ParseBookCandidates(string json)
        {
            var candidates = new List<BookCandidate>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("results", out var innerResults) && innerResults.ValueKind == JsonValueKind.Array)
                    {
                        root = innerResults;
                    }
                    else if (root.TryGetProperty("candidates", out var innerCandidates) && innerCandidates.ValueKind == JsonValueKind.Array)
                    {
                        root = innerCandidates;
                    }
                    else
                    {
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                root = prop.Value;
                                break;
                            }
                        }
                    }
                }

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        var candidate = new BookCandidate();
                        if (item.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                        {
                            candidate.Title = titleProp.GetString() ?? "";
                        }
                        if (item.TryGetProperty("author", out var authorProp) && authorProp.ValueKind == JsonValueKind.String)
                        {
                            candidate.Author = authorProp.GetString() ?? "";
                        }
                        if (item.TryGetProperty("first_publish_year", out var yearProp))
                        {
                            if (yearProp.ValueKind == JsonValueKind.Number && yearProp.TryGetInt32(out var yearVal))
                            {
                                candidate.FirstPublishYear = yearVal;
                            }
                            else if (yearProp.ValueKind == JsonValueKind.String && int.TryParse(yearProp.GetString(), out var yearParsed))
                            {
                                candidate.FirstPublishYear = yearParsed;
                            }
                        }
                        if (item.TryGetProperty("explanation", out var expProp) && expProp.ValueKind == JsonValueKind.String)
                        {
                            candidate.Explanation = expProp.GetString() ?? "";
                        }
                        if (item.TryGetProperty("open_library_id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        {
                            candidate.OpenLibraryId = idProp.GetString() ?? "";
                        }
                        if (item.TryGetProperty("open_library_url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                        {
                            candidate.OpenLibraryUrl = urlProp.GetString() ?? "";
                        }
                        if (item.TryGetProperty("cover_url", out var coverProp) && coverProp.ValueKind == JsonValueKind.String)
                        {
                            candidate.CoverUrl = coverProp.GetString();
                        }

                        candidates.Add(candidate);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ParseBookCandidates JSON parsing: {ex.Message}");
            }

            return candidates;
        }
    }
}
