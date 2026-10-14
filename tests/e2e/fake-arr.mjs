// A pretend Sonarr for the Medic Profiles tests: answers the API calls Medic Profiles makes with
// fixed data, and remembers what was sent to it.
import http from 'node:http';

export const received = [];

const indexers = [
    { id: 1, name: 'Good Indexer', protocol: 'torrent', enableRss: true, enableAutomaticSearch: true, enableInteractiveSearch: true },
    { id: 2, name: 'Broken Indexer', protocol: 'usenet', enableRss: true, enableAutomaticSearch: true, enableInteractiveSearch: true },
    { id: 3, name: 'Unused Indexer', protocol: 'torrent', enableRss: false, enableAutomaticSearch: false, enableInteractiveSearch: false }
];

const routes = {
    '/api/v3/system/status': () => ({ appName: 'Sonarr', version: '4.0.9.2244' }),
    '/api/v3/indexer': () => indexers,
    '/api/v3/indexerstatus': () => [{ indexerId: 2, initialFailure: new Date(Date.now() - 3 * 3600e3).toISOString(), mostRecentFailure: new Date().toISOString(), disabledTill: new Date(Date.now() + 3600e3).toISOString() }],
    '/api/v3/health': () => [{ source: 'IndexerStatusCheck', type: 'warning', message: 'Indexers unavailable due to failures: Broken Indexer' }],
    '/api/v3/queue': () => ({ page: 1, pageSize: 100, totalRecords: 0, records: [] }),
    '/api/v3/blocklist': () => ({ page: 1, pageSize: 100, totalRecords: 0, records: [] }),
    '/api/v3/qualityprofile': () => [],
    '/api/v3/customformat': () => [],
    '/api/v3/downloadclient': () => [{ id: 1, name: 'qBittorrent', implementation: 'QBittorrent', protocol: 'torrent', enable: true, fields: [{ name: 'host', value: 'qbit' }, { name: 'port', value: 8080 }] }]
};

export function start(port) {
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            const path = req.url.split('?')[0];
            received.push({ method: req.method, path, body, key: req.headers['x-api-key'] });
            const route = routes[path];
            if (req.headers['x-api-key'] !== 'test-key') { res.writeHead(401); res.end(); return; }
            res.writeHead(route ? 200 : 404, { 'Content-Type': 'application/json' });
            res.end(route ? JSON.stringify(route()) : '{}');
        });
    });
    return new Promise((resolve) => server.listen(port, '0.0.0.0', () => resolve(server)));
}

// A pretend qBittorrent Web UI: sign-in with admin/secret, and the "Excluded file names" preference.
export const qbit = { prefs: { excluded_file_names_enabled: false, excluded_file_names: '*.nfo' }, sets: [], logins: 0, failedLogins: 0 };

export function startQbit(port) {
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            const path = req.url.split('?')[0];
            const signedIn = /SID=good/.test(req.headers.cookie || '');
            if (path === '/api/v2/auth/login') {
                const form = new URLSearchParams(body);
                qbit.logins++;
                if (form.get('username') === 'admin' && form.get('password') === 'secret') {
                    res.writeHead(200, { 'Set-Cookie': 'SID=good; HttpOnly; path=/' });
                    res.end('Ok.');
                } else {
                    qbit.failedLogins++;
                    res.writeHead(200); res.end('Fails.');
                }
                return;
            }
            if (path === '/api/v2/auth/logout') { res.writeHead(200); res.end(); return; }
            if (!signedIn) { res.writeHead(403); res.end('Forbidden'); return; }
            if (path === '/api/v2/app/preferences') {
                res.writeHead(200, { 'Content-Type': 'application/json' });
                res.end(JSON.stringify(qbit.prefs));
                return;
            }
            if (path === '/api/v2/app/setPreferences') {
                const json = JSON.parse(new URLSearchParams(body).get('json'));
                qbit.sets.push(json);
                Object.assign(qbit.prefs, json);
                res.writeHead(200); res.end();
                return;
            }
            res.writeHead(404); res.end();
        });
    });
    return new Promise((resolve) => server.listen(port, '0.0.0.0', () => resolve(server)));
}

// A pretend Seerr: every Jellyfin account is Seerr user 42, and it remembers which Seerr user each
// approve or decline was made as (X-API-User). Its request list ignores ?requestedBy= on purpose and
// always holds one request by user 42 and one by user 7.
export const seerr = { decisions: [] };

const requestList = {
    pageInfo: { results: 2 },
    results: [
        { id: 11, status: 1, type: 'movie', createdAt: '2026-10-01T10:00:00.000Z', media: { tmdbId: 550, mediaType: 'movie', status: 2 }, requestedBy: { id: 42 } },
        { id: 12, status: 1, type: 'movie', createdAt: '2026-10-02T10:00:00.000Z', media: { tmdbId: 551, mediaType: 'movie', status: 2 }, requestedBy: { id: 7 } }
    ]
};

export function startSeerr(port) {
    const server = http.createServer((req, res) => {
        const path = req.url.split('?')[0];
        if (req.headers['x-api-key'] !== 'e2e-seerr-key') { res.writeHead(401); res.end(); return; }
        res.setHeader('Content-Type', 'application/json');
        if (req.method === 'GET' && path.startsWith('/api/v1/user/jellyfin/')) {
            res.end(JSON.stringify({ id: 42, displayName: 'admin' }));
            return;
        }
        if (req.method === 'GET' && path === '/api/v1/request') {
            res.end(JSON.stringify(requestList));
            return;
        }
        const title = path.match(/^\/api\/v1\/movie\/(\d+)$/);
        if (req.method === 'GET' && title) {
            res.end(JSON.stringify({ id: Number(title[1]), title: 'Film ' + title[1], releaseDate: '2000-01-01' }));
            return;
        }
        const decision = path.match(/^\/api\/v1\/request\/(\d+)\/(approve|decline)$/);
        if (req.method === 'POST' && decision) {
            seerr.decisions.push({ id: Number(decision[1]), action: decision[2], asUser: req.headers['x-api-user'] ?? null });
            res.end(JSON.stringify({ id: Number(decision[1]), status: decision[2] === 'approve' ? 2 : 3 }));
            return;
        }
        res.writeHead(404); res.end('{}');
    });
    return new Promise((resolve) => server.listen(port, '0.0.0.0', () => resolve(server)));
}
