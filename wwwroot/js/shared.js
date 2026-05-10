/**
 * shared.js — loaded on every portal page before any other script.
 * Place at: wwwroot/js/shared.js
 */
const PortalUtils = {

    // ── Token storage ─────────────────────────────────────────────────────────
    getToken: () => localStorage.getItem('vp_token'),
    saveToken: (t) => localStorage.setItem('vp_token', t),
    removeToken: () => localStorage.removeItem('vp_token'),

    // ── User storage ──────────────────────────────────────────────────────────
    getUser: () => {
        try {
            return JSON.parse(localStorage.getItem('vp_user') || 'null');
        } catch { return null; }
    },
    saveUser: (u) => localStorage.setItem('vp_user', JSON.stringify(u)),
    removeUser: () => localStorage.removeItem('vp_user'),

    // ── Logout ────────────────────────────────────────────────────────────────
    logout: function () {
        const token = localStorage.getItem('vp_token');
        let redirectUrl = '/admin/login.html';

        if (token) {
            const payload = PortalUtils.parseJwt(token);
            if (payload) {
                const role = payload.role;
                if (role === 'Seller' || role === 'Buyer') {
                    redirectUrl = '/seller/login.html';
                }
            }
        }

        localStorage.removeItem('vp_token');
        localStorage.removeItem('vp_user');
        window.location.href = redirectUrl;
    },

    // ── JWT decode ────────────────────────────────────────────────────────────
    parseJwt: (token) => {
        if (!token) return null;
        try {
            const base64Url = token.split('.')[1];
            const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
            return JSON.parse(window.atob(base64));
        } catch { return null; }
    },

    // ── Token expiry check ────────────────────────────────────────────────────
    isTokenAlive: (payload) => {
        if (!payload?.exp) return false;
        return Date.now() < payload.exp * 1000;
    },

    // ── Role helpers ──────────────────────────────────────────────────────────
    hasRole: (payload, role) => {
        if (!payload) return false;
        const roles = payload.role;
        if (!roles) return false;
        return Array.isArray(roles) ? roles.includes(role) : roles === role;
    },

    isSuperAdmin: (payload) => PortalUtils.hasRole(payload, 'SuperAdmin'),
    isSeller: (payload) => PortalUtils.hasRole(payload, 'Seller'),
    isBuyer: (payload) => PortalUtils.hasRole(payload, 'Buyer'),

    // ── Page guard ────────────────────────────────────────────────────────────
    requireRole: (role) => {
        const token = PortalUtils.getToken();
        const payload = PortalUtils.parseJwt(token);

        if (!token || !payload || !PortalUtils.isTokenAlive(payload)) {
            PortalUtils.redirectToLogin(role);
            return false;
        }

        if (!PortalUtils.hasRole(payload, role)) {
            PortalUtils.redirectToLogin(role);
            return false;
        }

        return true;
    },

    redirectToLogin: (role) => {
        const loginPages = {
            SuperAdmin: '/admin/login.html',
            Seller: '/seller/login.html',
            Buyer: '/buyer/login.html'
        };
        window.location.href = loginPages[role] || '/admin/login.html';
    },

    // ── API helpers ───────────────────────────────────────────────────────────
    getAuthHeaders: () => ({
        'Authorization': `Bearer ${PortalUtils.getToken()}`,
        'Content-Type': 'application/json'
    }),

    apiFetch: async (url, options = {}) => {
        const response = await fetch(url, {
            ...options,
            headers: {
                ...PortalUtils.getAuthHeaders(),
                ...(options.headers || {})
            }
        });

        if (response.status === 401) {
            PortalUtils.logout();
            return null;
        }

        return response;
    },

    // ── UI helpers ────────────────────────────────────────────────────────────
    esc: (str) => {
        if (str == null) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#x27;');
    },

    toast: (msg, type = 'success', durationMs = 3000) => {
        let el = document.getElementById('vp-toast');
        if (!el) {
            el = document.createElement('div');
            el.id = 'vp-toast';
            el.style.cssText = `
                position: fixed; bottom: 1.5rem; right: 1.5rem; z-index: 9999;
                padding: 0.65rem 1.1rem; border-radius: 8px; font-size: 0.8rem;
                display: none; max-width: 320px; font-family: 'Segoe UI', sans-serif;
            `;
            document.body.appendChild(el);
        }

        const styles = {
            success: 'background:#1e293b; border:1px solid #34d399; color:#34d399;',
            error: 'background:#1e293b; border:1px solid #f87171; color:#f87171;',
            info: 'background:#1e293b; border:1px solid #38bdf8; color:#38bdf8;'
        };

        el.style.cssText += styles[type] || styles.info;
        el.textContent = msg;
        el.style.display = 'block';

        setTimeout(() => { el.style.display = 'none'; }, durationMs);
    },

    formatDate: (utcString) => {
        if (!utcString) return '—';
        return new Date(utcString).toLocaleString();
    },

    roleBadge: (role) => {
        const styles = {
            SuperAdmin: 'background:rgba(56,189,248,0.12);  color:#38bdf8;',
            Seller: 'background:rgba(52,211,153,0.12);  color:#34d399;',
            Buyer: 'background:rgba(167,139,250,0.12); color:#a78bfa;'
        };
        const style = styles[role] || 'background:rgba(148,163,184,0.12); color:#94a3b8;';
        return `<span style="padding:0.2rem 0.6rem; border-radius:9999px; font-size:0.7rem;
                font-weight:500; ${style}">${PortalUtils.esc(role)}</span>`;
    }
};