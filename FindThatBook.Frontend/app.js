// API Configuration
const apiUrl = 'http://localhost:5091';

// DOM Elements
const searchForm = document.getElementById('searchForm');
const queryInput = document.getElementById('queryInput');
const resultsHeader = document.getElementById('resultsHeader');
const resultsCount = document.getElementById('resultsCount');
const resultsGrid = document.getElementById('resultsGrid');
const loader = document.getElementById('loader');
const loaderText = document.getElementById('loaderText');
const errorState = document.getElementById('errorState');
const errorTitle = document.getElementById('errorTitle');
const errorMessage = document.getElementById('errorMessage');
const errorAction = document.getElementById('errorAction');
const emptyState = document.getElementById('emptyState');
const apiStatusBadge = document.getElementById('apiStatus');
const exampleTags = document.querySelectorAll('.example-tag');

// Example Tags Click
exampleTags.forEach(tag => {
    tag.addEventListener('click', () => {
        queryInput.value = tag.textContent;
        searchForm.dispatchEvent(new Event('submit'));
    });
});

// Check API Status
async function checkApiStatus() {
    apiStatusBadge.className = 'status-badge status-unknown';
    try {
        const response = await fetch(`${apiUrl}/api/search`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ query: '' })
        });
        
        if (response.status === 400 || response.ok) {
            apiStatusBadge.className = 'status-badge status-active';
        } else {
            apiStatusBadge.className = 'status-badge status-inactive';
        }
    } catch (e) {
        apiStatusBadge.className = 'status-badge status-inactive';
    }
}

// Initial status check
checkApiStatus();

// Submit Search Form
searchForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const query = queryInput.value.trim();
    if (!query) return;

    showLoading();
    const startTime = performance.now();

    try {
        const response = await fetch(`${apiUrl}/api/search`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Accept': 'application/json'
            },
            body: JSON.stringify({ query: query })
        });

        const data = await response.json();
        const endTime = performance.now();
        const durationSec = ((endTime - startTime) / 1000).toFixed(2);

        if (!response.ok) {
            showError(data.error || 'Server returned an error.', response.status);
            return;
        }

        renderResults(data, durationSec);
    } catch (err) {
        showError('Could not connect to the server!', 0);
    }
});

// UI Helper: Show Loading State
function showLoading() {
    loader.classList.remove('hidden');
    resultsGrid.classList.add('hidden');
    resultsHeader.classList.add('hidden');
    errorState.classList.add('hidden');
    emptyState.classList.add('hidden');
    
    const messages = [
        'AI is extracting search parameters...',
        'Querying Open Library for works and authors...',
        'Cross-referencing work details to resolve primary authors...',
        'Differentiating illustrators, adaptors, and contributors...',
        'Scoring and applying matching hierarchy...',
        'AI is re-ranking and generating explanations...'
    ];
    let msgIdx = 0;
    loaderText.textContent = messages[0];
    
    if (window.loaderInterval) clearInterval(window.loaderInterval);
    window.loaderInterval = setInterval(() => {
        msgIdx = (msgIdx + 1) % messages.length;
        loaderText.textContent = messages[msgIdx];
    }, 2500);
}

// UI Helper: Show Error State
function showError(msg, status) {
    if (window.loaderInterval) clearInterval(window.loaderInterval);
    loader.classList.add('hidden');
    resultsGrid.classList.add('hidden');
    resultsHeader.classList.add('hidden');
    emptyState.classList.add('hidden');
    errorState.classList.remove('hidden');

    errorTitle.textContent = status === 400 ? 'Validation Error' : 'Search Failure';
    errorMessage.textContent = msg;

    if (msg.includes('Gemini API key is required')) {
        errorAction.classList.remove('hidden');
    } else {
        errorAction.classList.add('hidden');
    }
}

// UI Helper: Render Results
function renderResults(candidates, durationSec) {
    if (window.loaderInterval) clearInterval(window.loaderInterval);
    loader.classList.add('hidden');

    if (candidates.length === 0) {
        resultsCount.textContent = '0 Matches Found';
        resultsHeader.classList.remove('hidden');
        resultsGrid.classList.add('hidden');
        emptyState.classList.remove('hidden');
        return;
    }

    resultsCount.textContent = `${candidates.length} Match${candidates.length > 1 ? 'es' : ''} Found`;
    resultsHeader.classList.remove('hidden');
    resultsGrid.classList.remove('hidden');

    resultsGrid.innerHTML = '';
    candidates.forEach(book => {
        const tier = getTierMetadata(book.explanation);
        
        const card = document.createElement('div');
        card.className = 'book-card';

        const authorStr = book.author || 'Unknown Author';
        const coverHtml = book.cover_url 
            ? `<img class="book-cover" src="${book.cover_url}" alt="Cover of ${book.title}">`
            : getPlaceholderCover(book.title);

        card.innerHTML = `
            <div class="book-cover-container">
                ${coverHtml}
            </div>
            <div class="book-info">
                <span class="tier-tag ${tier.badgeClass}">${tier.label}</span>
                <div class="book-title-row">
                    <h4 class="book-title">${book.title}</h4>
                </div>
                <div class="book-meta">
                    <div class="meta-item">
                        <span>${authorStr}</span>
                    </div>
                    <div class="meta-item">
                        <span>First Published: ${book.first_publish_year || 'N/A'}</span>
                    </div>
                </div>
                <div class="explanation-box ${tier.borderClass}">
                    ${book.explanation}
                </div>
                <a href="${book.open_library_url}" target="_blank" class="ol-btn">
                    <span>View on Open Library ↗</span>
                </a>
            </div>
        `;
        resultsGrid.appendChild(card);
    });
}

// Map explanation text or matching content back to design metadata
function getTierMetadata(explanation) {
    const text = explanation.toLowerCase();
    
    if (text.includes('exact title') && text.includes('primary author')) {
        return {
            label: 'Exact Match',
            badgeClass: 'tier-exact-primary',
            borderClass: 'tier-exact-primary-border'
        };
    } else if (text.includes('contributor')) {
        return {
            label: 'Contributor Match',
            badgeClass: 'tier-exact-contributor',
            borderClass: 'tier-exact-contributor-border'
        };
    } else if (text.includes('near') || text.includes('partial') || text.includes('variant')) {
        return {
            label: 'Near Match',
            badgeClass: 'tier-near',
            borderClass: 'tier-near-border'
        };
    } else if (text.includes('fallback') || text.includes('works by')) {
        return {
            label: 'Author Works',
            badgeClass: 'tier-fallback',
            borderClass: 'tier-fallback-border'
        };
    } else {
        return {
            label: 'Relevance Match',
            badgeClass: 'tier-keyword',
            borderClass: 'tier-keyword-border'
        };
    }
}

function getPlaceholderCover(title) {
    return `
        <div class="book-cover-placeholder">
            <span style="font-size: 1.5rem; margin-bottom: 0.25rem;">📖</span>
            <span>${title}</span>
        </div>
    `;
}
