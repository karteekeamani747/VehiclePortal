window.addEventListener('load', () => {
    setTimeout(() => {
        const token = localStorage.getItem('vp_token');
        if (!token) return;

        // Intercept all API fetch calls and inject the token
        const originalFetch = window.fetch;
        window.fetch = function (url, opts = {}) {
            const urlStr = typeof url === 'string' ? url : url?.url || '';
            if (urlStr.includes('/api/') || urlStr.includes('localhost:7001')) {
                opts = opts || {};
                opts.headers = opts.headers || {};
                if (typeof opts.headers.set === 'function') {
                    opts.headers.set('Authorization', 'Bearer ' + token);
                } else {
                    opts.headers['Authorization'] = 'Bearer ' + token;
                }
            }
            return originalFetch.call(this, url, opts);
        };

        // Auto-authorize so the lock icon shows and no manual step needed
        if (window.ui) {
            window.ui.authActions.authorize({
                Bearer: {
                    name: 'Bearer',
                    schema: { type: 'http', scheme: 'bearer', bearerFormat: 'JWT' },
                    value: token
                }
            });
        }

        console.log('[Swagger] Ready — token injected automatically.');
    }, 1000);
});