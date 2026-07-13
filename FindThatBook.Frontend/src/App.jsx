import { useState, useEffect, useRef } from 'react';

// API Configuration - auto detect environment
const apiUrl = 'https://findthatbook.runasp.net';

export default function App() {
    const [query, setQuery] = useState('');
    const [candidates, setCandidates] = useState([]);
    const [isLoading, setIsLoading] = useState(false);
    const [loaderText, setLoaderText] = useState('Extracting search parameters');
    const [error, setError] = useState(null);
    const [duration, setDuration] = useState('');
    const [apiStatus, setApiStatus] = useState('unknown');
    const [isSearched, setIsSearched] = useState(false);

    const loaderIntervalRef = useRef(null);

    // Check API Status on load
    useEffect(() => {
        const checkApiStatus = async () => {
            setApiStatus('unknown');
            try {
                const response = await fetch(`${apiUrl}/api/search`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ query: '' })
                });
                
                if (response.status === 400 || response.ok) {
                    setApiStatus('active');
                } else {
                    setApiStatus('inactive');
                }
            } catch (e) {
                setApiStatus('inactive');
            }
        };

        checkApiStatus();
    }, []);

    // Clean up loader interval on unmount
    useEffect(() => {
        return () => {
            if (loaderIntervalRef.current) {
                clearInterval(loaderIntervalRef.current);
            }
        };
    }, []);

    // Start loading text rotation
    const startLoadingTextRotation = () => {
        const messages = [
            'Extracting search parameters',
            'Querying Open Library for works and authors',
            'Differentiating illustrators, adaptors, and contributors',
            'Scoring and applying matching hierarchy',
            'Re-ranking and generating explanations'
        ];
        
        let msgIdx = 0;
        setLoaderText(messages[0]);
        
        if (loaderIntervalRef.current) {
            clearInterval(loaderIntervalRef.current);
        }

        loaderIntervalRef.current = setInterval(() => {
            msgIdx = (msgIdx + 1) % messages.length;
            setLoaderText(messages[msgIdx]);
        }, 2200);
    };

    // Handle Form Submit
    const handleSearch = async (searchQuery) => {
        if (!searchQuery.trim()) return;

        setIsLoading(true);
        setError(null);
        setIsSearched(true);
        startLoadingTextRotation();
        
        const startTime = performance.now();

        try {
            const response = await fetch(`${apiUrl}/api/search`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'application/json'
                },
                body: JSON.stringify({ query: searchQuery })
            });

            const data = await response.json();
            const endTime = performance.now();
            const durationSec = ((endTime - startTime) / 1000).toFixed(2);

            if (loaderIntervalRef.current) {
                clearInterval(loaderIntervalRef.current);
            }
            setIsLoading(false);

            if (!response.ok) {
                setError(data.error || 'Server returned an error.');
                return;
            }

            setCandidates(data);
            setDuration(durationSec);
        } catch (err) {
            if (loaderIntervalRef.current) {
                clearInterval(loaderIntervalRef.current);
            }
            setIsLoading(false);
            setError('Could not reach the Web API server. Ensure the backend project is running locally and the API URL is correct.');
        }
    };

    const handleFormSubmit = (e) => {
        e.preventDefault();
        handleSearch(query);
    };

    const handleExampleClick = (example) => {
        setQuery(example);
        handleSearch(example);
    };

    // Helper: Map explanation back to matching tier design
    const getTierMetadata = (explanation) => {
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
    };

    // Fallback cover image generator
    const getPlaceholderCover = (title) => {
        return (
            <div className="book-cover-placeholder">
                <span>{title}</span>
            </div>
        );
    };

    return (
        <div className="app-container">
            <header>
                <div className="header-container">
                    <div className="logo">
                        <h1>Find That Book</h1>
                    </div>
                    <div className="api-status-container">
                        <span className={`status-badge ${
                            apiStatus === 'active' ? 'status-active' :
                            apiStatus === 'inactive' ? 'status-inactive' : 'status-unknown'
                        }`}>
                            <span className="status-dot"></span>
                            {apiStatus === 'active' ? 'Connected' : apiStatus === 'inactive' ? 'Disconnected' : 'Checking API...'}
                        </span>
                    </div>
                </div>
            </header>

            <main>
                <section className="search-section">
                    <div className="search-container">
                        <h2>Find That Book</h2>
                        
                        <form onSubmit={handleFormSubmit} className="search-form">
                            <div className="search-input-wrapper">
                                <input 
                                    type="text" 
                                    value={query}
                                    onChange={(e) => setQuery(e.target.value)}
                                    placeholder="Type author, title, keywords, publish year..." 
                                    required 
                                    autoComplete="off"
                                />
                                <button type="submit" className="search-btn">
                                    <span>Search</span>
                                    <span>→</span>
                                </button>
                            </div>
                        </form>
                    </div>
                </section>

                <section className="results-section">
                    {isSearched && !isLoading && !error && (
                        <div className="results-header">
                            <h3>{candidates.length} Match{candidates.length !== 1 ? 'es' : ''} Found</h3>
                            <span className="processing-time">Completed in {duration}s</span>
                        </div>
                    )}

                    {/* Loader */}
                    {isLoading && (
                        <div className="loader-container">
                            <div className="spinner"></div>
                            <p>{loaderText}</p>
                        </div>
                    )}

                    {/* Error State */}
                    {error && (
                        <div className="error-container">
                            <h4>Search Failed</h4>
                            <p>{error}</p>
                            {error.includes('Gemini API key is required') && (
                                <div className="error-action">
                                    <p className="helper-text">Gemini API key must be configured in the Web API configuration.</p>
                                </div>
                            )}
                        </div>
                    )}

                    {/* Empty State */}
                    {!isSearched && !isLoading && !error && (
                        <div className="empty-container">
                            <h3>No Results Found</h3>
                        </div>
                    )}

                    {/* Results Grid */}
                    {!isLoading && !error && candidates.length > 0 && (
                        <div className="results-grid">
                            {candidates.map((book, idx) => {
                                const tier = getTierMetadata(book.explanation);
                                return (
                                    <div key={idx} className="book-card">
                                        <span className={`tier-tag ${tier.badgeClass}`}>{tier.label}</span>
                                        <div className="book-cover-container">
                                            {book.cover_url ? (
                                                <img 
                                                    className="book-cover" 
                                                    src={book.cover_url} 
                                                    alt={`Cover of ${book.title}`} 
                                                />
                                            ) : getPlaceholderCover(book.title)}
                                        </div>
                                        <div className="book-info">
                                            <div className="book-title-row">
                                                <h4 className="book-title">{book.title}</h4>
                                            </div>
                                            <div className="book-meta">
                                                <div className="meta-item">
                                                    <span>{book.author || 'Unknown Author'}</span>
                                                </div>
                                                <div className="meta-item">
                                                    <span>First Published: {book.first_publish_year || 'N/A'}</span>
                                                </div>
                                            </div>
                                            <div className={`explanation-box ${tier.borderClass}`}>
                                                {book.explanation}
                                            </div>
                                            <a href={book.open_library_url} target="_blank" rel="noopener noreferrer" className="ol-btn">
                                                <span>View on Open Library ↗</span>
                                            </a>
                                        </div>
                                    </div>
                                );
                            })}
                        </div>
                    )}
                </section>
            </main>

            <footer>
                <div className="footer-container">
                </div>
            </footer>
        </div>
    );
}
