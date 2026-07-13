# Find That Book

The application consists of a **.NET 8 Web API backend** and a **static frontend**.

Live Demo: https://fjam.github.io/FindThatBook/FindThatBook.Frontend/index.html

API: https://findthatbook.runasp.net

---

## Features Implemented

1. **API Key Guarding**: The backend checks if a Gemini API Key is configured before running any searches. If the key is missing from config, it returns an HTTP 400 Bad Request and halts execution. **Per requirements, the API key is read solely from backend configuration and is not accepted via client requests.**
2. **AI-Powered Query Extraction**: Messy search blobs are parsed by Gemini AI to extract `title`, `author`, and `keywords` hypotheses.
3. **Open Library Search Integration**: Queries the Open Library search, works, authors, and author works APIs, and de-duplicates results to canonical works.
4. **Primary Author vs. Contributor Resolution**: Resolves primary author IDs from canonical work records to separate them from contributors (illustrators, editors, adaptors) in search indexing.
5. **Hierarchical Matching Engine**: Classifies matches into tiers:
   - **Tier A**: Exact title and primary author.
   - **Tier B**: Exact title and contributor-only author.
   - **Tier C**: Near title match and author match.
   - **Tier D**: Author-only search fallback (returns top works).
   - **Tier E**: General search relevance match.
6. **AI Reranking & Explanations**: Reranks the top candidates and generates a concise "why it matched" explanation.
7. **Simple Static Frontend**: 
   - Live API status indicator polling.
   - Results displayed in a clean easy to read format.
---

## Setup and Running Instructions

### 1. Configure Gemini API Key (Backend)
The Gemini API key must be configured in the appsettings.json before using:
- **Configuration File**: Add it to `FindThatBook/appsettings.json` or `FindThatBook/appsettings.Development.json`:
  ```json
  {
    "Gemini": {
      "ApiKey": "GEMINI_API_KEY",
      "Model": "gemini-3.5-flash"
    }
  }
  ```

### 2. Run the Web API Backend
From the repository root, run:
```bash
cd FindThatBook
dotnet run
```
The API will start at:
- `http:localhost:5091`
- `https://localhost:7038`

### 3. Run the Frontend React Application
The frontend is a React application built with Vite and located in the [FindThatBook.Frontend](file:///C:/Users/FHome/source/repos/FindThatBook/FindThatBook.Frontend/) folder.
From the repository root, run:
```bash
cd FindThatBook.Frontend
npm install
npm run dev
```
Open `http://localhost:5173` in your browser.
- By default, it connects to the local backend API at `http://localhost:5091` when running locally, and shifts to `https://findthatbook.runasp.net` when hosted.

---

## API Usage Example

**Request:**
- **URL**: `POST http://localhost:5091/api/search`
- **Headers**: `Content-Type: application/json`
- **Body**:
```json
{
  "query": "tolkien hobbit illustrated deluxe 1937"
}
```

**Response (HTTP 200 OK):**
```json
[
  {
    "title": "The Hobbit",
    "author": "J.R.R. Tolkien",
    "first_publish_year": 1937,
    "explanation": "Exact title match; Tolkien is primary author; Dixon listed as adaptor.",
    "open_library_id": "OL26320W",
    "open_library_url": "https://openlibrary.org/works/OL26320W",
    "cover_url": "https://covers.openlibrary.org/b/id/12345-M.jpg"
  }
]
```

---


## Assumptions and Design Decisions

1. **API Key Location**: Assumed the Gemini API key should strictly be loaded from server-side configurations (e.g. `appsettings.json`) to protect API secrets. Client-side requests are blocked immediately at the controller level if no key is present. Key will not be accepted as in input.
2. **Remove duplication**: Assumed that multiple matches in Open Library search results should be de-duplicated to their canonical works by grouping results on their unique work keys (e.g., `/works/OL...W`).
3. **Primary Author Resolution**: Assumed that the authors returned in the main Open Library search result (`author_name`/`author_key`) can contain translators, illustrators, or other contributors. The backend cross-references these keys with `/works/{work_id}.json` to resolve primary authors.
4. **Parallel API Processing**: Fetching detailed work metadata from `/works/{work_id}.json` for multiple candidates is done concurrently using `Task.WhenAll` to optimize performance and lower latency.
5. **No Third-Party Frontend Dependencies**: The static frontend is built using only raw HTML, vanilla CSS, and vanilla JS.


---

## Testing Strategy

The project contains unit and integration tests under FindThatBook.Tests:
**`SearchControllerTests`**:
   - Verifies empty queries return bad requests.
   - Verifies that if the API key is missing from config, it immediately rejects the request with HTTP 400 and blocks further execution.
   - Verifies standard search success flow when key is present.

### How to Run Tests
From the repository root, run:
```bash
dotnet test
```

---

## Future Improvements

- Implement Server-Side Caching
- Rate Limiting & Retries
- UI Search History
- Autocomplete Suggestions
- Overall UI improvement
- More Complete Unit Tests
    - Unit tests for the OpenLibrary Service