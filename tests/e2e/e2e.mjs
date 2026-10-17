// End-to-end tests against a real Jellyfin started by run.sh.
// Sets the server up through its own API, checks every plugin loads and answers, then opens each
// plugin page in a real browser and fails on script errors.
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

const JF = process.env.JF || 'http://127.0.0.1:8096';
// The pretend Sonarr, Seerr and the rest listen on fixed ports, moved by FAKE_PORT_OFFSET so two runs can go side by side.
const P = (port) => port + Number(process.env.FAKE_PORT_OFFSET || 0);
// The Docker container run.sh started Jellyfin in.
const CONTAINER = process.env.E2E_NAME || 'medic-e2e';
const AUTH = 'MediaBrowser Client="e2e", Device="e2e", DeviceId="medic-e2e", Version="1.0"';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let token = '';
let passed = 0;
let failed = 0;
const only = process.argv.slice(2);

// The most one call to Jellyfin may take. Jellyfin gives up on a server that never answers after 100 s
// (HttpClient's default), and the slowest call here waits about 10 s on a silent Seerr, so only a hung request reaches it.
const REQUEST_TIMEOUT_MS = 120000;

async function api(method, path, body, raw) {
    const headers = { Authorization: AUTH + (token ? `, Token="${token}"` : '') };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    // One signal covers both the answer and reading its body, so a body that stops halfway also ends here.
    const signal = AbortSignal.timeout(REQUEST_TIMEOUT_MS);
    const named = (e) => {
        if (e.name !== 'TimeoutError') throw e;
        throw new Error(`${method} ${path} got no answer within ${REQUEST_TIMEOUT_MS / 1000} s`);
    };
    const res = await fetch(JF + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal }).catch(named);
    if (raw) return res;
    const text = await res.text().catch(named);
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
    return { status: res.status, json, text };
}

async function check(name, fn) {
    if (only.length && !only.some((o) => name.toLowerCase().includes(o.toLowerCase()))) return;
    try {
        const detail = await fn();
        passed++;
        console.log(`PASS  ${name}${detail ? '  (' + detail + ')' : ''}`);
    } catch (e) {
        failed++;
        console.log(`FAIL  ${name}  -> ${e.message}`);
    }
}

function expect(cond, message) { if (!cond) throw new Error(message); }

async function ok(path) {
    const r = await api('GET', path);
    expect(r.status === 200, `${path} returned ${r.status} ${r.text.slice(0, 200)}`);
    return r.json;
}

// ---------- Server setup ----------

async function setup() {
    for (let i = 0; i < 60; i++) {
        const r = await api('GET', '/Startup/Configuration').catch(() => null);
        if (r && r.status === 200) break;
        await sleep(1000);
    }
    await api('POST', '/Startup/Configuration', { UICulture: 'en-US', MetadataCountryCode: 'GB', PreferredMetadataLanguage: 'en' });
    await api('GET', '/Startup/User');
    await api('POST', '/Startup/User', { Name: 'admin', Password: 'admin' });
    await api('POST', '/Startup/RemoteAccess', { EnableRemoteAccess: true, EnableAutomaticPortMapping: false });
    await api('POST', '/Startup/Complete');

    const login = await api('POST', '/Users/AuthenticateByName', { Username: 'admin', Pw: 'admin' });
    expect(login.status === 200, 'admin sign-in failed: ' + login.status);
    token = login.json.AccessToken;

    // Films only from the local NFO files, so nothing reaches the internet.
    await api('POST', '/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false', {
        LibraryOptions: {
            PathInfos: [{ Path: '/media/movies' }],
            MetadataSavers: [], DisabledLocalMetadataReaders: [], TypeOptions: [{ Type: 'Movie', MetadataFetchers: [], ImageFetchers: [] }],
            EnableRealtimeMonitor: false, EnableChapterImageExtraction: false, EnableTrickplayImageExtraction: false
        }
    });
    await api('POST', '/Library/Refresh');
    let films = 0;
    for (let i = 0; i < 90 && films < 12; i++) {
        await sleep(2000);
        const r = await api('GET', '/Items?Recursive=true&IncludeItemTypes=Movie');
        films = r.json?.TotalRecordCount || 0;
    }
    return { userId: login.json.User.Id, serverId: login.json.ServerId, films };
}

// ---------- Tests ----------

const ctx = await setup();

await check('library scanned', async () => {
    expect(ctx.films >= 12, `found ${ctx.films} films`);
    return `${ctx.films} films`;
});

await check('all three plugins loaded', async () => {
    const plugins = await ok('/Plugins');
    const names = plugins.map((p) => p.Name);
    for (const want of ['Jellyfin Medic', 'Medic Picks', 'Medic Profiles']) {
        const p = plugins.find((x) => x.Name === want);
        expect(p, `${want} missing (have ${names.join(', ')})`);
        expect(p.Status === 'Active', `${want} is ${p.Status}`);
    }
});

const medicGets = [
    '/JellyfinMedic/Report', '/JellyfinMedic/Now', '/JellyfinMedic/LoadGuard', '/JellyfinMedic/MediaReport',
    '/JellyfinMedic/Tracks/Scan', '/JellyfinMedic/Tracks/Progress', '/JellyfinMedic/Version', '/JellyfinMedic/Iptv',
    '/JellyfinMedic/MedicSettings', '/JellyfinMedic/Plugins', '/JellyfinMedic/Usage', '/JellyfinMedic/TranscodeLog',
    '/JellyfinMedic/Weekly', '/JellyfinMedic/Cleanup', '/JellyfinMedic/JellyfinSettings',
    '/JellyfinMedic/Schedule/GetCalendar', '/JellyfinMedic/Schedule/GetRunHistory', '/JellyfinMedic/Schedule/PreviewScheduleDiff',
    '/JellyfinMedic/Schedule/ListBackups', '/JellyfinMedic/Schedule/GetUntimedTasks', '/MedicPicks/Admin/Status', '/MedicProfiles/Status', '/MedicProfiles/History'
];
for (const path of medicGets) {
    await check(`GET ${path}`, async () => { await ok(path); });
}

await check('Jellyfin Medic serves its stylesheet to anyone, and nothing else from there', async () => {
    // The page links it, and a stylesheet link carries no sign-in, so it's served without one.
    const css = await fetch(JF + '/JellyfinMedic/Assets/medic.css');
    const text = await css.text();
    expect(css.status === 200, 'medic.css: ' + css.status);
    expect((css.headers.get('content-type') || '').startsWith('text/css'), 'content type: ' + css.headers.get('content-type'));
    expect(css.headers.get('x-content-type-options') === 'nosniff', 'x-content-type-options: ' + css.headers.get('x-content-type-options'));
    expect((css.headers.get('cache-control') || '').includes('no-cache'), 'cache-control: ' + css.headers.get('cache-control'));
    expect(text.includes('#JellyfinMedicPage .so-topbar'), 'not the Medic stylesheet: ' + text.slice(0, 120));
    for (const name of ['medic.html', 'assist.js', '..%2Fmedic.html']) {
        const other = await fetch(JF + '/JellyfinMedic/Assets/' + name);
        expect(other.status === 404, `${name}: ${other.status}`);
    }
    return `${text.length} bytes`;
});

await check('Medic Picks keeps both keys when they are saved at the same time', async () => {
    // The settings page saves the TMDb and Seerr keys together; neither may be lost.
    try {
        for (let i = 0; i < 20; i++) {
            await api('POST', '/MedicPicks/Admin/TmdbKey', { Key: '' });
            await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
            const saved = await Promise.all([
                api('POST', '/MedicPicks/Admin/TmdbKey', { Key: 'e2e-tmdb-key' }),
                api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' })
            ]);
            expect(saved.every((r) => r.status === 204), 'save: ' + saved.map((r) => r.status).join(', '));
            const status = await ok('/MedicPicks/Admin/Status');
            expect(status.tmdbKeySet && status.seerrKeySet, `round ${i + 1}: ` + JSON.stringify(status));
        }
        return '20 rounds, both keys kept';
    } finally {
        await api('POST', '/MedicPicks/Admin/TmdbKey', { Key: '' });
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
    }
});

await check('support export hides secrets in name/value pairs, tokens and dashless keys', async () => {
    // A plugin's settings with fake secrets in shapes that used to get through. Written from inside the
    // container: the folder belongs to Jellyfin's user, not the one running the tests.
    const file = '/config/plugins/configurations/E2E.SecretShapes.xml';
    const secrets = ['FAKEeyJhbGciOiJIUzI1NiJ9', '0123456789abcdef0123456789abcdef', 'fedcba9876543210fedcba9876543210', 'FAKE-COOKIE-VALUE', 'FAKEbasicDXNlcjpwYXNz', 'fakeattrcookie'];
    const xml = `<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration>
  <Headers>
    <HeaderItem><Key>Authorization</Key><Value>Bearer FAKEeyJhbGciOiJIUzI1NiJ9.FAKEpayload.FAKEsig</Value></HeaderItem>
    <HeaderItem><Key>X-Api-Key</Key><Value>0123456789abcdef0123456789abcdef</Value></HeaderItem>
    <HeaderItem><Key>Accept</Key><Value>application/json</Value></HeaderItem>
    <Header name="Cookie" value="sid=fakeattrcookie" />
  </Headers>
  <ServerKey>fedcba9876543210fedcba9876543210</ServerKey>
  <AuthCookie>session=FAKE-COOKIE-VALUE; Path=/</AuthCookie>
  <ProxyAuth>Basic FAKEbasicDXNlcjpwYXNz</ProxyAuth>
  <LibraryId>3f2504e0-4f89-11d3-9a0c-0305e82c3301</LibraryId>
</PluginConfiguration>`;
    execFileSync('docker', ['exec', '-i', CONTAINER, 'sh', '-c', 'cat > "$1"', 'sh', file], { input: xml });
    try {
        const r = await api('GET', '/JellyfinMedic/Export');
        expect(r.status === 200, 'export ' + r.status);
        const part = r.text.slice(r.text.indexOf('E2E.SecretShapes.xml'));
        const leaked = secrets.filter((s) => part.includes(s));
        expect(leaked.length === 0, 'leaked: ' + leaked.join(', ') + '\n' + part.slice(0, 1200));
        expect(part.includes('<Key>Authorization</Key>') && part.includes('<Value>application/json</Value>') && part.includes('name="Cookie"'), 'header names or harmless values were hidden:\n' + part.slice(0, 1200));
        expect(part.includes('3f2504e0-4f89-11d3-9a0c-0305e82c3301'), 'a dashed id was hidden');
        return 'no secret left, labels and ids kept';
    } finally {
        execFileSync('docker', ['exec', CONTAINER, 'rm', '-f', file]);
    }
});

await check('Dashboards polling at the same moment don\'t queue behind each other', async () => {
    // Several Dashboards (or tabs) ask for "now" within the same second; none should wait for another's CPU reading.
    await ok('/JellyfinMedic/Now');
    const started = Date.now();
    await Promise.all([1, 2, 3, 4].map(() => ok('/JellyfinMedic/Now')));
    const seconds = (Date.now() - started) / 1000;
    expect(seconds < 1.5, `4 readings at once took ${seconds.toFixed(1)} s`);
    return `4 at once in ${seconds.toFixed(2)} s`;
});

await check('storage: library drives and growing folders are listed', async () => {
    let specs = (await ok('/JellyfinMedic/Report')).Specs || [];
    const drive = specs.find((x) => /^Drive for .*Movies/.test(x.Label));
    expect(drive, 'no library drive line: ' + specs.filter((x) => x.Group === 'Storage').map((x) => x.Label).join(', '));
    expect(/free of/.test(drive.Value), drive.Value);
    await sleep(3000); // folder sizes are measured in the background
    specs = (await ok('/JellyfinMedic/Report')).Specs || [];
    const meta = specs.find((x) => x.Label === 'Metadata folder');
    expect(meta && !/measuring/.test(meta.Value), 'metadata size: ' + JSON.stringify(meta));
    return drive.Value;
});

await check('security: sign-ins from several places are counted per account', async () => {
    // Two people with long names each sign in from two addresses. Counted per account that's two each,
    // which isn't "several places"; lumping long entries together made it one "user" with four.
    // A third person signs in from three addresses, which is several places: the check must still see them.
    // run.sh trusts Docker's gateway as a proxy, so X-Forwarded-For sets each sign-in's address.
    const people = [
        ['a-very-long-account-name-for-testing-one', [10, 11]],
        ['a-very-long-account-name-for-testing-two', [12, 13]],
        ['roamer', [14, 15, 16]]
    ];
    const ids = [];
    try {
        for (const [name, addresses] of people) {
            const made = await api('POST', '/Users/New', { Name: name, Password: 'e2e-pass' });
            expect(made.status === 200, 'user not created: ' + made.text);
            ids.push(made.json.Id);
            for (const last of addresses) {
                const res = await fetch(JF + '/Users/AuthenticateByName', {
                    method: 'POST',
                    headers: { Authorization: AUTH.replace('medic-e2e', `e2e-${name}-${last}`), 'Content-Type': 'application/json', 'X-Forwarded-For': `203.0.113.${last}` },
                    body: JSON.stringify({ Username: name, Pw: 'e2e-pass' })
                });
                expect(res.status === 200, `sign-in ${name} from 203.0.113.${last}: ${res.status}`);
            }
        }
        await sleep(2000);
        // Without this the test would pass for the wrong reason: all sign-ins from one address.
        const log = await ok('/System/ActivityLog/Entries?limit=100');
        const seen = (log.Items || []).filter((e) => /203\.0\.113\.1[0-6]/.test((e.ShortOverview || '') + (e.Overview || ''))).length;
        expect(seen >= 7, `the forwarded addresses weren't used (${seen} entries); run.sh sets Docker's gateway as a known proxy before Jellyfin starts`);
        const findings = ((await ok('/JellyfinMedic/Report')).Findings || []).filter((f) => f.Area === 'Security');
        // "An account signs in from several places", or "N accounts sign in from several places".
        const roaming = findings.find((f) => /signs? in from several places/.test(f.Title));
        expect(/roamer: 3 addresses/.test(roaming?.Current || ''), 'the account with three addresses wasn\'t reported: ' + JSON.stringify(roaming));
        expect(!/a-very-long|a user/.test(roaming.Current), 'the accounts with two addresses were counted: ' + roaming.Current);
        return 'roamer reported with 3 addresses; the two accounts with 2 each not reported';
    } finally {
        for (const id of ids) await api('DELETE', `/Users/${id}`);
    }
});

await check('GPU/CPU advice: a slow software preset and full-frame trickplay are flagged', async () => {
    const enc = await ok('/System/Configuration/encoding');
    const cfg = await ok('/System/Configuration');
    await api('POST', '/System/Configuration/encoding', { ...enc, EncoderPreset: 'slower' });
    await api('POST', '/System/Configuration', { ...cfg, TrickplayOptions: { ...cfg.TrickplayOptions, EnableKeyFrameOnlyExtraction: false } });
    const titles = ((await ok('/JellyfinMedic/Report')).Findings || []).map((f) => f.Title);
    await api('POST', '/System/Configuration/encoding', enc);
    await api('POST', '/System/Configuration', cfg);
    expect(titles.includes('Software transcodes use a slow preset'), 'no preset finding: ' + titles.join(' | '));
    expect(titles.includes('Trickplay reads every frame'), 'no trickplay finding');
});

await check('theme checks: late @import, raw GitHub address and unbalanced braces', async () => {
    const branding = await ok('/System/Configuration/branding');
    const css = '.tweak { color: red; }\n@import url("https://raw.githubusercontent.com/example/theme/main/theme.css");\n.broken { color: blue;';
    const saved = await api('POST', '/System/Configuration/branding', { ...branding, CustomCss: css });
    expect(saved.status < 300, 'saving branding ' + saved.status);
    const titles = ((await ok('/JellyfinMedic/Report')).Findings || []).filter((f) => f.Area === 'Themes').map((f) => f.Title);
    await api('POST', '/System/Configuration/branding', branding);
    for (const want of ['A theme import in your custom CSS is ignored', 'A theme is loaded from raw.githubusercontent.com', 'Your custom CSS has unbalanced braces']) {
        expect(titles.includes(want), `missing "${want}": ${titles.join(' | ')}`);
    }
    return titles.length + ' theme findings';
});

await check('resource monitor measures Jellyfin and its FFmpeg processes', async () => {
    let res = null;
    for (let i = 0; i < 25 && !res; i++) {
        res = (await ok('/JellyfinMedic/Now')).Resources;
        if (!res) await sleep(2000);
    }
    expect(res, 'no resource reading after 50 seconds');
    expect(res.MemoryGb > 0.05 && res.MemoryGb < 64, 'odd memory ' + res.MemoryGb);
    expect(res.CpuPercent >= 0 && res.CpuPercent <= 100, 'odd CPU ' + res.CpuPercent);
    expect(Array.isArray(await ok('/JellyfinMedic/ResourceEvents')), 'events list');

    // Start a transcode, and the FFmpeg process Jellyfin starts for it should be counted.
    let film = null;
    for (let i = 0; i < 30 && !film; i++) {
        film = (await ok('/Items?Recursive=true&IncludeItemTypes=Movie&searchTerm=Long')).Items[0];
        if (!film) await sleep(2000);
    }
    expect(film, 'Long Film not scanned');
    const url = `/Videos/${film.Id}/main.m3u8?MediaSourceId=${film.Id}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=4000000&MaxWidth=1280&PlaySessionId=e2e-tx`;
    const playlist = await api('GET', url);
    expect(playlist.status === 200, 'transcode playlist ' + playlist.status);
    const segment = playlist.text.split('\n').find((l) => l && !l.startsWith('#'));
    api('GET', `/Videos/${film.Id}/${segment}`).catch(() => {});
    let seen = 0;
    for (let i = 0; i < 20 && !seen; i++) {
        await sleep(3000);
        seen = (await ok('/JellyfinMedic/Now')).Resources?.FfmpegProcesses || 0;
    }
    await api('DELETE', '/Videos/ActiveEncodings?deviceId=medic-e2e&playSessionId=e2e-tx');
    expect(seen > 0, 'the transcode\'s FFmpeg process was not counted');
    return `CPU ${res.CpuPercent}%, ${res.MemoryGb} GB, disk ${res.DiskMBps} MB/s, GPU ${res.GpuPercent ?? 'n/a'}`;
});

await check('Dashboard data: both routes answer an admin with every section, and refuse other accounts', async () => {
    // /Report saves today's disk readings, which the Disks section reads.
    await ok('/JellyfinMedic/Report');
    const data = await ok('/JellyfinMedic/Dashboard');
    expect(Object.keys(data.Errors || {}).length === 0, 'sections that failed: ' + JSON.stringify(data.Errors));
    // The names the Dashboard's panels read, nested ones included. Fields that can be null are left out, as the
    // JSON may omit them.
    const fields = ['GeneratedUtc', 'UtcOffsetMinutes', 'Errors', 'BusyHours', 'Today', 'Transcodes', 'Disks', 'Network', 'RepeatedErrors', 'People', 'Updates'];
    const shape = [
        ['', data, fields],
        ['BusyHours', data.BusyHours, ['Ready', 'HoursRecorded', 'Days', 'RunsInBusyHours']],
        ['BusyHours.Days[0][0]', data.BusyHours?.Days?.[0]?.[0], ['AverageStreams', 'AverageTranscodes', 'Busy', 'Runs']],
        ['Today', data.Today, ['Date', 'NowMinutes', 'Planned', 'Actual', 'Spikes', 'Guard', 'Summary']],
        ['Today.Summary', data.Today?.Summary, ['Planned', 'Runs', 'Running', 'Completed', 'Failed', 'Stopped', 'Cancelled', 'NotRun', 'Late']],
        ['Transcodes', data.Transcodes, ['Samples', 'Reasons', 'Top']],
        ['Disks', data.Disks, ['Drives', 'Freed']],
        ['Disks.Drives[0]', data.Disks?.Drives?.[0], ['Key', 'Holds', 'FreeGb', 'TotalGb', 'Series']],
        ['Network', data.Network, ['CurrentLimitMbps', 'MostStreams']],
        ['People', data.People, ['Total', 'AdminNames', 'Inactive', 'NoPassword']]
    ];
    const missing = shape.flatMap(([where, obj, names]) => names.filter((k) => obj === null || obj === undefined || !(k in obj)).map((k) => (where ? where + '.' : '') + k));
    expect(missing.length === 0, 'missing: ' + missing.join(', '));
    expect(data.BusyHours.Days.length === 7 && data.BusyHours.Days.every((d) => d.length === 24), 'the busy grid is not 7 by 24');
    expect(/^\d{4}-\d{2}-\d{2}$/.test(data.Today.Date), 'Today.Date: ' + data.Today.Date);
    expect(data.Disks.Drives.length > 0, 'no drives, though /Report has just read them');
    // Null when the activity log didn't answer in time, which a slow machine can cause.
    expect(data.People.Roaming == null || Array.isArray(data.People.Roaming), 'roaming accounts: ' + JSON.stringify(data.People.Roaming));

    let live = await ok('/JellyfinMedic/Dashboard/Live');
    for (let i = 0; i < 9 && live.Resources.length === 0; i++) {
        await sleep(5000); // the monitor's first reading comes about 30 seconds after Jellyfin starts
        live = await ok('/JellyfinMedic/Dashboard/Live');
    }
    expect(live.Now && Array.isArray(live.Now.RunningTasks), 'Now: ' + JSON.stringify(live.Now).slice(0, 200));
    expect(live.Resources.length > 0 && 'CpuPercent' in live.Resources[0], 'no resource readings');
    expect(JSON.stringify(live.Limits) === JSON.stringify({ CpuPercent: 90, MemoryPercent: 90, DiskMBps: 150, GpuPercent: 95 }), 'limits: ' + JSON.stringify(live.Limits));
    expect(Array.isArray(live.Paused), 'paused: ' + JSON.stringify(live.Paused));

    // An account that isn't an administrator, on its own device so the admin's session stays signed in.
    const name = 'e2e-not-admin';
    const device = AUTH.replace('medic-e2e', name);
    const made = await api('POST', '/Users/New', { Name: name, Password: 'e2e-pass' });
    expect(made.status === 200, 'user not created: ' + made.text);
    try {
        const signIn = await fetch(JF + '/Users/AuthenticateByName', {
            method: 'POST',
            headers: { Authorization: device, 'Content-Type': 'application/json' },
            body: JSON.stringify({ Username: name, Pw: 'e2e-pass' })
        });
        expect(signIn.status === 200, 'sign-in: ' + signIn.status);
        const userToken = (await signIn.json()).AccessToken;
        for (const path of ['/JellyfinMedic/Dashboard', '/JellyfinMedic/Dashboard/Live']) {
            const asUser = await fetch(JF + path, { headers: { Authorization: `${device}, Token="${userToken}"` } });
            expect(asUser.status === 403, `${path} answered an account that isn't an administrator: ${asUser.status}`);
            const anonymous = await fetch(JF + path);
            expect(anonymous.status === 401, `${path} answered a request with no sign-in: ${anonymous.status}`);
        }
    } finally {
        await api('DELETE', `/Users/${made.json.Id}`);
    }
    return `${fields.length} fields, ${data.Disks.Drives.length} drives, ${live.Resources.length} readings; 403 and 401 for the others`;
});

// Every admin route in the plugins' controllers, read from the source so a new one is covered without a change here.
// Medic Picks marks admin routes one by one, all under Admin/; its unit tests check that the two go together.
function adminRoutes() {
    const root = new URL('../../', import.meta.url);
    const files = [
        ['src/JellyfinMedic/MedicController.cs'], ['src/JellyfinMedic/ScheduleController.cs'], ['src/JellyfinMedic/MedicDashboardController.cs'],
        ['Jellyfin.Plugin.MedicProfiles/Api/ProfilesController.cs'],
        ['Jellyfin.Plugin.MedicPicks/Api/PicksController.cs', (route) => route.startsWith('Admin/')]
    ];
    // Values that fit each route's parameters, so the request reaches the sign-in check rather than a 404.
    const values = { app: 'sonarr', requestId: '1', name: 'e2e' };
    const routes = [];
    for (const [file, include = () => true] of files) {
        const code = require('node:fs').readFileSync(new URL(file, root), 'utf8');
        const prefix = code.match(/^\[Route\("([^"]+)"\)\]/m)[1];
        // Each public instance method of the controller is an action, routed by its [Http*] or [Route] templates, or
        // at the controller's own route when it has neither.
        for (const m of code.matchAll(/((?:^ {4}\[.*\]\s*\n)*)^ {4}public (?!static\b)[\w<>\[\],?. ]+? \w+\(/gm)) {
            const attributes = m[1];
            if (/\[NonAction\]/.test(attributes)) continue;
            const verbs = [...attributes.matchAll(/\[Http(Get|Post|Put|Delete|Patch)(?:\("([^"]*)"\))?\]/g)];
            const routed = [...attributes.matchAll(/\[Route\("([^"]*)"\)\]/g)].map((r) => r[1]);
            // A verb with its own template answers there; a bare one takes the [Route] templates; a [Route] beside only
            // templated verbs answers any verb, so GET stands in for it.
            const pairs = verbs.flatMap(([, verb, template]) => (template !== undefined ? [template] : routed.length ? routed : ['']).map((t) => [verb, t]));
            if (!verbs.some(([, , template]) => template === undefined)) pairs.push(...routed.map((t) => ['Get', t]));
            if (!pairs.length) pairs.push(['Get', '']);
            for (const [verb, route] of pairs) {
                if (!include(route)) continue;
                const path = '/' + [prefix, route].filter(Boolean).join('/').replace(/\{(\w+)[^}]*\}/g, (_, p) => values[p] ?? '1');
                routes.push([verb.toUpperCase(), path]);
            }
        }
    }
    return routes;
}

await check('every admin route refuses an account that isn\'t an administrator', async () => {
    const routes = adminRoutes();
    expect(routes.length > 80, `only ${routes.length} admin routes found; has a controller moved?`);
    const name = 'e2e-not-admin-routes';
    const device = AUTH.replace('medic-e2e', name);
    const made = await api('POST', '/Users/New', { Name: name, Password: 'e2e-pass' });
    expect(made.status === 200, 'user not created: ' + made.text);
    try {
        const signIn = await fetch(JF + '/Users/AuthenticateByName', {
            method: 'POST',
            headers: { Authorization: device, 'Content-Type': 'application/json' },
            body: JSON.stringify({ Username: name, Pw: 'e2e-pass' })
        });
        expect(signIn.status === 200, 'sign-in: ' + signIn.status);
        const userToken = (await signIn.json()).AccessToken;
        const answered = [];
        for (const [method, path] of routes) {
            const res = await fetch(JF + path, {
                method,
                headers: { Authorization: `${device}, Token="${userToken}"`, 'Content-Type': 'application/json' },
                body: method === 'GET' ? undefined : '{}'
            });
            if (res.status !== 403) answered.push(`${method} ${path}: ${res.status}`);
        }
        expect(answered.length === 0, 'answered an account that isn\'t an administrator: ' + answered.join(', '));
    } finally {
        await api('DELETE', `/Users/${made.json.Id}`);
    }
    return `${routes.length} routes, each 403`;
});

await check('Jellyfin\'s API document still builds with Medic installed', async () => {
    // Jellyfin names the document's schemas by a type's short name, so a Medic type named like a Jellyfin one breaks it.
    const doc = await api('GET', '/api-docs/openapi.json');
    expect(doc.status === 200, `/api-docs/openapi.json returned ${doc.status} ${doc.text.slice(0, 200)}`);
    const medic = Object.keys(doc.json?.paths || {}).filter((path) => path.startsWith('/JellyfinMedic/'));
    expect(medic.includes('/JellyfinMedic/Dashboard') && medic.includes('/JellyfinMedic/Dashboard/Live'), 'the Dashboard routes are missing from the document');
    return medic.length + ' Medic routes in the document';
});

await check('schedule plan uses quarter-hour slots', async () => {
    const cal = await ok('/JellyfinMedic/Schedule/GetCalendar');
    expect(cal.AvoidedSlots?.length === 96, 'expected 96 avoided slots, got ' + cal.AvoidedSlots?.length);
    expect(cal.Days[0].Busy.length === 96, 'expected 96 busy slots');
});

await check('schedule: unscheduled tasks stay off unless chosen, and per-task choices apply', async () => {
    const tasks = await ok('/ScheduledTasks');
    const restart = tasks.find((t) => t.Name === 'Scheduled restart');
    expect(restart && restart.Triggers.length === 0, 'Scheduled restart should start with no schedule');
    const forUpdates = tasks.find((t) => t.Name === 'Restart Jellyfin for waiting updates');
    expect(forUpdates && forUpdates.Triggers.length === 0, 'Restart Jellyfin for waiting updates should start with no schedule: ' + JSON.stringify(forUpdates?.Triggers));
    const row = (plan, id) => plan.SchedulePlan.find((p) => p.TaskId.replace(/-/g, '') === id.replace(/-/g, ''));
    let plan = await ok('/JellyfinMedic/Schedule/PreviewScheduleDiff');
    let r = row(plan, restart.Id);
    expect(r.Choice === 'keep' && !r.Changes, 'unscheduled task would be given a schedule: ' + JSON.stringify(r));

    const scan = tasks.find((t) => t.Key === 'RefreshLibrary');
    expect(scan, 'no library scan task');
    expect(row(plan, scan.Id).Choice === 'medic', 'library scan should default to Medic');
    const set = await api('POST', `/JellyfinMedic/Schedule/SetChoice?taskId=${scan.Id}&choice=off`);
    expect(set.status === 200, 'SetChoice ' + set.status);
    plan = await ok('/JellyfinMedic/Schedule/PreviewScheduleDiff');
    r = row(plan, scan.Id);
    expect(r.Choice === 'off' && r.ProposedSchedule === 'Manual only' && r.Changes, 'off not planned: ' + JSON.stringify(r));
    await api('POST', `/JellyfinMedic/Schedule/SetChoice?taskId=${scan.Id}&choice=keep`);
    plan = await ok('/JellyfinMedic/Schedule/PreviewScheduleDiff');
    r = row(plan, scan.Id);
    expect(r.Choice === 'keep' && !r.Changes, 'keep not honoured: ' + JSON.stringify(r));
    await api('POST', `/JellyfinMedic/Schedule/SetChoice?taskId=${scan.Id}&choice=medic`);
});

await check('"Suggest" mode flags a better schedule in Checks', async () => {
    const titles = ((await ok('/JellyfinMedic/Report')).Findings || []).map((f) => f.Title);
    expect(titles.includes('A better schedule is available'), 'no suggestion: ' + titles.filter((t) => /schedul/i.test(t)).join(' | '));
});

await check('avoid window saves in 15-minute steps', async () => {
    const s = await ok('/JellyfinMedic/MedicSettings');
    const saved = await api('POST', '/JellyfinMedic/MedicSettings', { ...s, AvoidEnabled: true, AvoidStartMinute: 18 * 60 + 45, AvoidEndMinute: 22 * 60 + 15 });
    expect(saved.status === 200, 'save failed ' + saved.status);
    const back = await ok('/JellyfinMedic/MedicSettings');
    expect(back.AvoidStartMinute === 1125 && back.AvoidEndMinute === 1335, `got ${back.AvoidStartMinute}-${back.AvoidEndMinute}`);
    const cal = await ok('/JellyfinMedic/Schedule/GetCalendar');
    expect(cal.AvoidedSlots.filter(Boolean).length === 14, 'expected 14 avoided slots');
    await api('POST', '/JellyfinMedic/MedicSettings', { ...s, AvoidEnabled: false });
});

const MEDIC_DATA = '/config/plugins/configurations/JellyfinMedic';

// Medic's data files in the container (Jellyfin owns /config while it runs): read one (null when missing), write one, remove one.
function readMedicFile(name) {
    try {
        return execFileSync('docker', ['exec', CONTAINER, 'cat', `${MEDIC_DATA}/${name}`], { stdio: ['ignore', 'pipe', 'ignore'] }).toString();
    } catch {
        return null;
    }
}

function writeMedicFile(name, text) {
    execFileSync('docker', ['exec', '-i', CONTAINER, 'sh', '-c', 'cat > "$1"', 'sh', `${MEDIC_DATA}/${name}`], { input: text });
}

function removeMedicFile(name) {
    execFileSync('docker', ['exec', CONTAINER, 'rm', '-f', `${MEDIC_DATA}/${name}`]);
}

// The Dashboard's data is kept for a minute: waits for a build that started after `since` (a time in ms).
async function dashboardAfter(since) {
    for (let i = 0; i < 16; i++) {
        const data = await ok('/JellyfinMedic/Dashboard');
        if (new Date(data.GeneratedUtc).getTime() >= since) return data;
        await sleep(5000);
    }
    throw new Error('the Dashboard data was not built again within 80 seconds');
}

// Known data for the Dashboard panels around the server's today (the container runs in UTC): a usage profile past
// its 72 hours with busy evenings, three runs (one failed today, one last week in a busy hour), a CPU spike, a
// LoadGuard stop, two transcoded titles, ten days of a filling drive and a speed test.
function dashboardFixtures() {
    const midnight = new Date();
    midnight.setUTCHours(0, 0, 0, 0);
    const at = (days, hour, minute) => new Date(midnight.getTime() + days * 86400000 + (hour * 60 + minute) * 60000).toISOString();
    const buckets = Array.from({ length: 168 }, (_, i) => {
        const busy = i % 24 === 20 || i % 24 === 21;
        return { Samples: 7, StreamSum: busy ? 14 : 0, TranscodeSum: busy ? 7 : 0, MaxStreams: busy ? 3 : 0, MaxTranscodes: busy ? 1 : 0 };
    });
    return {
        'usage_profile.json': { FirstSampleUtc: at(-10, 0, 0), LastSampleUtc: at(0, 0, 0), TotalSamples: 1176, Buckets: buckets, QuarterSamples: [], QuarterStreamSums: [] },
        'run_history.json': [
            { TaskId: 'e2e-fixture-scan', TaskName: 'Fixture scan', StartUtc: at(0, 1, 0), EndUtc: at(0, 1, 20), Status: 'Completed' },
            { TaskId: 'e2e-fixture-trickplay', TaskName: 'Fixture trickplay', StartUtc: at(0, 2, 0), EndUtc: at(0, 2, 5), Status: 'Failed', ErrorMessage: 'Fixture error' },
            { TaskId: 'e2e-fixture-scan', TaskName: 'Fixture scan', StartUtc: at(-2, 20, 30), EndUtc: at(-2, 20, 50), Status: 'Completed' }
        ],
        'resource_events.json': [{ TimeUtc: at(0, 1, 10), Resource: 'CPU', Level: '95% of all CPU threads', Causes: [] }],
        'load_guard.json': [{ TimestampUtc: at(0, 1, 12), Action: 'stopped', TaskName: 'Fixture trickplay', MemoryUsedGb: 7.5, MemoryLimitGb: 8 }],
        'transcode_log.json': {
            SinceUtc: at(-9, 0, 0),
            Items: [
                { ItemId: 'a', Title: 'Fixture film (2020)', Samples: 12, Reasons: { AudioCodecNotSupported: 10, SubtitleCodecNotSupported: 4 }, Clients: ['Jellyfin Web'], LastSeenUtc: at(-1, 21, 0) },
                { ItemId: 'b', Title: 'Fixture series S01E01', Samples: 8, Reasons: { ContainerBitrateExceedsLimit: 6 }, Clients: ['Android TV'], LastSeenUtc: at(-2, 20, 0) }
            ]
        },
        // No path: the server can't read this drive now, so its last reading stands in (220 GB free, 20 GB a day).
        'disk_history.json': Array.from({ length: 10 }, (_, i) => ({ Key: '/e2e-fixture-drive', Day: at(i - 9, 0, 0).slice(0, 10), FreeGb: 400 - i * 20, Holds: 'Fixture films', Path: '' })),
        'speedtest.json': { TestedUtc: at(-1, 8, 0), DownloadMbps: 300, UploadMbps: 50, LatencyMs: 12, CurrentLimitBps: 0, IptvHdStreams: 30, Options: [] }
    };
}

await check('schedule apply changes nothing when Medic can\'t save its schedule, and a restore puts it back', async () => {
    const triggers = async () => JSON.stringify((await ok('/ScheduledTasks'))
        .map((t) => [t.Id, t.Triggers])
        .sort((a, b) => a[0].localeCompare(b[0])));
    const before = await triggers();
    // A folder where Medic first writes its schedule file makes that write fail, as a full disk would.
    execFileSync('docker', ['exec', CONTAINER, 'mkdir', '-p', `${MEDIC_DATA}/managed_schedule.json.tmp`]);
    try {
        const blocked = await api('POST', '/JellyfinMedic/Schedule/ApplyRecommendedSchedule');
        expect(blocked.json?.Success === false && /nothing was changed/.test(blocked.json.Message), `blocked apply: ${blocked.status} ${blocked.text.slice(0, 200)}`);
        expect(await triggers() === before, 'triggers changed although the apply failed');
    } finally {
        execFileSync('docker', ['exec', CONTAINER, 'rmdir', `${MEDIC_DATA}/managed_schedule.json.tmp`]);
    }

    const applied = (await api('POST', '/JellyfinMedic/Schedule/ApplyRecommendedSchedule')).json;
    expect(applied?.Success && applied.AppliedTasks > 0 && await triggers() !== before, 'apply: ' + JSON.stringify(applied));
    const restored = (await api('POST', `/JellyfinMedic/Schedule/RestorePreviousSchedule?backupFileName=${encodeURIComponent(applied.BackupFile)}`)).json;
    expect(restored?.Success && await triggers() === before, 'restore: ' + JSON.stringify(restored));
    return `${applied.AppliedTasks} tasks applied, then put back`;
});

await check('schedule backups each get their own name, and the newest 50 are kept', async () => {
    for (let i = 0; i < 55; i++) {
        const r = await api('POST', '/JellyfinMedic/Schedule/BackupCurrentSchedule');
        expect(r.json?.Success, `backup ${i}: ${r.status} ${r.text.slice(0, 200)}`);
    }
    const list = await ok('/JellyfinMedic/Schedule/ListBackups');
    expect(list.length === 50, `${list.length} backups kept`);
    return '55 made in a row, 50 kept';
});

await check('a damaged Medic file is set aside instead of written over', async () => {
    const file = `${MEDIC_DATA}/task_choices.json`;
    execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', `echo '{"damaged": true' > ${file}`]);
    const scan = (await ok('/ScheduledTasks')).find((t) => t.Key === 'RefreshLibrary');
    const set = await api('POST', `/JellyfinMedic/Schedule/SetChoice?taskId=${scan.Id}&choice=medic`);
    expect(set.status === 200, 'SetChoice ' + set.status);
    const kept = execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', `cat ${file}.bad 2>/dev/null || echo missing`]).toString();
    expect(kept.includes('"damaged"'), 'the damaged file was not kept: ' + kept.trim());
    return 'kept as task_choices.json.bad';
});

await check('IPTV lists each duplicate channel and its copies', async () => {
    const added = await api('POST', '/LiveTv/TunerHosts', { Type: 'm3u', Url: '/media/livetv/channels.m3u', FriendlyName: 'e2e', TunerCount: 1 });
    expect(added.status === 200, 'adding the M3U tuner failed ' + added.status);
    let channels = 0;
    for (let i = 0; i < 30 && channels < 9; i++) {
        await sleep(2000);
        channels = (await api('GET', '/LiveTv/Channels')).json?.TotalRecordCount || 0;
    }
    expect(channels >= 9, `only ${channels} channels`);
    const r = await api('POST', '/JellyfinMedic/Iptv');
    expect(r.status === 200, 'IPTV analysis ' + r.status);
    const dups = r.json.Channels.Duplicates || [];
    const bbc = dups.find((d) => /bbc one/i.test(d.Name));
    expect(bbc && bbc.Versions.length === 3, 'BBC One copies: ' + JSON.stringify(dups));
    return `${dups.length} channels with copies`;
});

await check('track cleanup leaves excluded films alone', async () => {
    const s = await ok('/JellyfinMedic/MedicSettings');
    const before = await ok('/JellyfinMedic/Tracks/Scan');
    const paths = (r) => (r.Changing?.length ? r.Changing : r.Sample || []).map((p) => p.Path || '').join('|');
    expect(/Two Tracks/.test(paths(before)) && /Keep Me Too/.test(paths(before)), 'both French-track films should be planned: ' + paths(before));
    await api('POST', '/JellyfinMedic/MedicSettings', { ...s, TracksExclude: 'Keep Me Too\n/media/elsewhere' });
    const after = await ok('/JellyfinMedic/Tracks/Scan');
    expect(/Two Tracks/.test(paths(after)) && !/Keep Me Too/.test(paths(after)), 'exclusion ignored: ' + paths(after));
    expect((await ok('/JellyfinMedic/MedicSettings')).TracksExclude === 'Keep Me Too\n/media/elsewhere', 'exclusions not saved');
    await api('POST', '/JellyfinMedic/MedicSettings', { ...s, TracksExclude: '' });
});

// ---------- Track cleanup on real files ----------

const fs = await import('node:fs');
const movies = `${process.env.WORK}/media/movies`;

// A short film with an audio track per language, in that order. "eng:Commentary" also gives the track a title.
function makeFilm(file, languages, seconds) {
    const inputs = languages.flatMap((_, i) => ['-f', 'lavfi', '-i', `sine=frequency=${300 + i * 200}:duration=${seconds}`]);
    const maps = languages.flatMap((_, i) => ['-map', String(i + 1)]);
    const tags = languages.flatMap((entry, i) => {
        const [language, title] = entry.split(/:(.*)/s);
        return [`-metadata:s:a:${i}`, `language=${language}`, ...(title ? [`-metadata:s:a:${i}`, `title=${title}`] : [])];
    });
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', `testsrc=size=320x180:rate=10:duration=${seconds}`,
        ...inputs, '-map', '0', ...maps, '-c:v', 'libx264', '-preset', 'ultrafast', '-c:a', 'aac', ...tags, file]);
}

const audioLanguages = (file) => JSON.parse(execFileSync('ffprobe', ['-v', 'error', '-select_streams', 'a', '-show_entries', 'stream_tags=language', '-of', 'json', file]).toString())
    .streams.map((x) => x.tags?.language).join(',');

async function runTracks() {
    const started = await api('POST', '/JellyfinMedic/Tracks/Run?dryRun=false');
    expect(started.status === 200, 'track run ' + started.status + ' ' + started.text);
    let p = null;
    for (let i = 0; i < 60 && (!p || p.Running); i++) {
        await sleep(1000);
        p = await ok('/JellyfinMedic/Tracks/Progress');
    }
    return p;
}

// Adds a film to the library and waits until Jellyfin has read its tracks. Returns its file.
async function addFilm(name, languages, seconds) {
    const dir = `${movies}/${name}`;
    const file = `${dir}/${name}.mkv`;
    fs.mkdirSync(dir, { recursive: true });
    makeFilm(file, languages, seconds);
    await api('POST', '/Library/Refresh');
    const title = encodeURIComponent(name.replace(/ \(\d{4}\)$/, ''));
    let audio = 0;
    for (let i = 0; i < 45 && audio < languages.length; i++) {
        await sleep(2000);
        const item = (await ok(`/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=${title}&Fields=MediaStreams`)).Items[0];
        audio = (item?.MediaStreams || []).filter((m) => m.Type === 'Audio').length;
    }
    expect(audio === languages.length, `Jellyfin did not pick up ${name}`);
    return file;
}

// Runs fn with track cleanup settings changed, and puts them back after.
async function withTrackSettings(changes, fn) {
    const s = await ok('/JellyfinMedic/MedicSettings');
    await api('POST', '/JellyfinMedic/MedicSettings', { ...s, ...changes });
    try {
        return await fn();
    } finally {
        await api('POST', '/JellyfinMedic/MedicSettings', s);
    }
}

await check('track cleanup checks the file itself before changing it', async () => {
    // Replace a film the way Sonarr would, with the French track first. Jellyfin (real time monitor off)
    // still lists English first, so a plan from its list alone would keep French and drop English.
    const file = `${movies}/Two Tracks (2015)/Two Tracks (2015).mkv`;
    const replacement = `${process.env.WORK}/reordered.mkv`;
    makeFilm(replacement, ['fre', 'eng'], 4);
    fs.renameSync(replacement, file);

    return withTrackSettings({ TracksExclude: 'Keep Me Too' }, async () => {
        const first = await runTracks();
        expect(first.Changed === 0 && first.Skipped === 1 && first.Failed === 0, 'stale plan was not refused: ' + JSON.stringify(first.Recent));
        expect(audioLanguages(file) === 'fre,eng', 'the file was changed anyway: ' + audioLanguages(file));

        // Medic asked Jellyfin to read the file again; once it has, the run strips the French track.
        let firstLanguage = '';
        for (let i = 0; i < 30 && !firstLanguage.startsWith('fr'); i++) {
            await sleep(2000);
            const item = (await ok('/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=Two%20Tracks&Fields=MediaStreams')).Items[0];
            firstLanguage = item?.MediaStreams?.find((m) => m.Type === 'Audio')?.Language || '';
        }
        expect(firstLanguage.startsWith('fr'), 'Jellyfin did not read the file again');
        const second = await runTracks();
        expect(second.Changed === 1 && second.Failed === 0, 'second run: ' + JSON.stringify(second.Recent));
        expect(audioLanguages(file) === 'eng', 'English should be the only audio left: ' + audioLanguages(file));
        return 'stale plan refused, then English kept';
    });
});

await check('track cleanup puts back an original left out by an interrupted swap', async () => {
    // What Jellyfin stopping between the two moves leaves behind: the original kept aside, the
    // cleaned copy still in its temp file, and nothing at the film's own path.
    const dir = `${movies}/Keep Me Too (2016)`;
    const file = `${dir}/Keep Me Too (2016).mkv`;
    const kept = `${dir}/.medic-originals/Keep Me Too (2016).mkv`;
    const temp = `${file}.medic-tmp.mkv`;
    fs.mkdirSync(`${dir}/.medic-originals`, { recursive: true });
    fs.renameSync(file, kept);
    fs.copyFileSync(kept, temp);

    return withTrackSettings({ TracksExclude: 'Two Tracks' }, async () => {
        const p = await runTracks();
        const notes = JSON.stringify(p.Recent);
        expect((p.Recent || []).some((r) => /Put back the original of Keep Me Too/.test(r)), 'original not put back: ' + notes);
        expect(!fs.existsSync(temp), 'the temp file was left behind');
        expect(p.Changed === 1 && p.Failed === 0, 'the film was not cleaned after being put back: ' + notes);
        expect(audioLanguages(file) === 'eng', 'cleaned film: ' + audioLanguages(file));
        expect(audioLanguages(kept) === 'eng,fre', 'kept original: ' + audioLanguages(kept));
        return 'original put back, then cleaned';
    });
});

await check('track cleanup refuses a new copy that comes out shorter than the original', async () => {
    // A file whose end can't be read (cut short here) still says it is 60 s long. FFmpeg copies what it can
    // and exits 0, even with -xerror; the copy must not replace the original.
    const file = await addFilm('Cut Short (2018)', ['eng', 'fre'], 60);
    fs.truncateSync(file, Math.floor(fs.statSync(file).size / 2));
    const size = fs.statSync(file).size;

    return withTrackSettings({ TracksExclude: 'Two Tracks\nKeep Me Too', TracksReplaceInPlace: true }, async () => {
        const p = await runTracks();
        const notes = JSON.stringify(p.Recent);
        expect(p.Changed === 0 && p.Skipped === 0 && p.Failed === 1, 'a short copy was accepted, or not counted as failed: ' + notes);
        expect((p.Recent || []).some((r) => /Problem with Cut Short.*didn't come out right \(it lasts/.test(r)), 'not refused for its length: ' + notes);
        expect(fs.statSync(file).size === size && audioLanguages(file) === 'eng,fre', 'the original was changed');
        expect(!fs.existsSync(`${file}.medic-tmp.mkv`), 'the temp file was left behind');
        return 'short copy refused, original untouched';
    });
});

await check('track cleanup leaves hardlinked files alone', async () => {
    // A film a torrent client seeds through a hard link: a cleaned copy would be a second file, not a smaller one.
    const file = await addFilm('Seeded (2019)', ['eng', 'fre'], 4);
    const seed = `${process.env.WORK}/seeding-copy.mkv`;
    fs.linkSync(file, seed);
    try {
        return await withTrackSettings({ TracksExclude: 'Cut Short' }, async () => {
            const p = await runTracks();
            const notes = JSON.stringify(p.Recent);
            expect(p.Changed === 0 && p.Skipped === 1 && p.Failed === 0 && /Seeded.*2 hard links/.test(notes), 'the hardlinked film was cleaned: ' + notes);
            expect(audioLanguages(file) === 'eng,fre' && fs.statSync(file).nlink === 2, 'the film was changed');
            return 'skipped, still shared with the seeding copy';
        });
    } finally {
        fs.unlinkSync(seed);
    }
});

await check('track cleanup keeps the file\'s owner and permissions', async () => {
    // Jellyfin runs as root here and writes the new copy and the originals folder; Sonarr and Radarr, as another
    // user, must still be able to manage both.
    const file = await addFilm('Owned (2020)', ['eng', 'fre'], 4);
    const dir = `${movies}/Owned (2020)`;
    fs.chmodSync(dir, 0o2770);
    fs.chmodSync(file, 0o660);
    const before = fs.statSync(file);
    const folder = fs.statSync(dir);
    return withTrackSettings({ TracksExclude: 'Cut Short\nSeeded' }, async () => {
        const p = await runTracks();
        expect(p.Changed === 1 && p.Failed === 0, 'not cleaned: ' + JSON.stringify(p.Recent));
        const after = fs.statSync(file);
        expect(audioLanguages(file) === 'eng', 'cleaned film: ' + audioLanguages(file));
        expect((after.mode & 0o777) === 0o660 && after.uid === before.uid && after.gid === before.gid,
            `now ${(after.mode & 0o777).toString(8)} ${after.uid}:${after.gid}, was 660 ${before.uid}:${before.gid}`);
        const describe = (st) => `${(st.mode & 0o7777).toString(8)} ${st.uid}:${st.gid}`;
        const originals = fs.statSync(`${dir}/.medic-originals`);
        const ignore = fs.statSync(`${dir}/.medic-originals/.ignore`);
        expect(describe(originals) === `2770 ${folder.uid}:${folder.gid}` && describe(ignore) === `660 ${folder.uid}:${folder.gid}`,
            `.medic-originals is ${describe(originals)} and its .ignore ${describe(ignore)}, the film's folder ${describe(folder)}`);
        return 'mode 660 and owner kept, originals folder like the film\'s';
    });
});

await check('track cleanup keeps the original of a file replaced between runs', async () => {
    // Sonarr or Radarr upgrading a cleaned film under the same name: the new release must be kept when it's
    // cleaned, not lost because .medic-originals already holds the old release's original.
    const file = await addFilm('Upgraded (2021)', ['eng', 'fre'], 4);
    const dir = `${movies}/Upgraded (2021)`;
    const kept = `${dir}/.medic-originals/Upgraded (2021).mkv`;
    return withTrackSettings({ TracksExclude: 'Cut Short\nSeeded\nOwned' }, async () => {
        const first = await runTracks();
        expect(first.Changed === 1 && first.Failed === 0, 'first clean: ' + JSON.stringify(first.Recent));
        expect(first.BytesKept > 0, 'space held by the kept original not reported: ' + first.BytesKept);
        const changes = await ok('/JellyfinMedic/Tracks/Changes');
        // Paths as the server sees them, inside its container.
        expect(changes.some((c) => c.Path.endsWith('/Upgraded (2021)/Upgraded (2021).mkv') && c.KeptOriginal?.endsWith('/Upgraded (2021)/.medic-originals/Upgraded (2021).mkv')), 'the change was not recorded: ' + JSON.stringify(changes.slice(0, 3)));

        const audioTracks = async () => {
            const item = (await ok('/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=Upgraded&Fields=MediaStreams')).Items[0];
            return { id: item?.Id, audio: (item?.MediaStreams || []).filter((m) => m.Type === 'Audio').length };
        };
        // Medic has Jellyfin re-read a file it cleaned. Jellyfin reads the tracks first and the file's date only
        // when it saves, so a file replaced while that refresh runs is recorded with the new date and the old
        // tracks, and from then on no refresh in Default mode reads it again. Wait for that refresh to finish.
        let now = await audioTracks();
        for (let i = 0; i < 30 && now.audio !== 1; i++) {
            await sleep(1000);
            now = await audioTracks();
        }
        expect(now.audio === 1, `Jellyfin did not re-read the cleaned file: it lists ${now.audio} audio tracks`);

        const upgrade = `${process.env.WORK}/upgrade.mkv`;
        makeFilm(upgrade, ['eng', 'fre'], 6);
        fs.renameSync(upgrade, file);
        const size = fs.statSync(file).size;
        // Jellyfin still lists the cleaned file's one track, so nothing would plan it: have Jellyfin read it, as a
        // scan would. A full refresh reads the tracks whatever date Jellyfin has, so the ordering above can't hide it.
        await api('POST', `/Items/${now.id}/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=false`);
        for (let i = 0; i < 30 && now.audio !== 2; i++) {
            await sleep(2000);
            now = await audioTracks();
        }
        expect(now.audio === 2, `Jellyfin did not read the new release: it lists ${now.audio} audio tracks`);
        const second = await runTracks();
        expect(second.Changed === 1, 'second clean: ' + JSON.stringify(second.Recent));
        const aside = fs.readdirSync(`${dir}/.medic-originals`).filter((f) => /^Upgraded \(2021\)\.replaced-\d{8}-\d{6}\.mkv$/.test(f));
        expect(aside.length === 1, 'the old release\'s original was not moved aside: ' + fs.readdirSync(`${dir}/.medic-originals`));
        expect(fs.statSync(kept).size === size && audioLanguages(kept) === 'eng,fre', 'the new release was not kept');
        const k = await ok('/JellyfinMedic/Tracks/KeptOriginals');
        expect(k.Orphans >= 1, 'the old original is not offered as one with no file: ' + JSON.stringify(k));
        return 'new release kept, old one moved aside and offered for deletion';
    });
});

await check('tidying earlier copies leaves a copy that doesn\'t check out', async () => {
    // Medic 1.0.4 to 1.0.7 left "Name.medic-stripped.mkv" beside the film, made with optional maps, so one
    // can lack the film's audio. It must not take the film's place; a sound one still does.
    const file = await addFilm('Leftover (2022)', ['eng', 'fre'], 4);
    const copy = file.replace(/\.mkv$/, '.medic-stripped.mkv');
    const kept = `${movies}/Leftover (2022)/.medic-originals/Leftover (2022).mkv`;
    const size = fs.statSync(file).size;
    try {
        execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-i', file, '-map', '0:v', '-c', 'copy', copy]);
        const refused = await api('POST', '/JellyfinMedic/Tracks/TidyCopies');
        expect(refused.status === 200 && refused.json?.Titles === 0
            && JSON.stringify(refused.json?.LeftTitles) === JSON.stringify([{ Name: 'Leftover (2022).mkv', Reason: 'it has no audio' }]), 'tidy: ' + refused.status + ' ' + refused.text);
        expect(fs.statSync(file).size === size && audioLanguages(file) === 'eng,fre', 'the film was changed');
        expect(fs.existsSync(copy) && !fs.existsSync(kept), 'the copy was moved');
        const notes = JSON.stringify((await ok('/JellyfinMedic/Tracks/Progress')).Recent);
        expect(/Left Leftover \(2022\)\.mkv and its earlier copy as they are: .*it has no audio/.test(notes), 'no warning: ' + notes);

        execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-i', file, '-map', '0:v', '-map', '0:a:0', '-c', 'copy', copy]);
        const tidied = await api('POST', '/JellyfinMedic/Tracks/TidyCopies');
        expect(tidied.status === 200 && tidied.json?.Titles === 1 && tidied.json?.LeftTitles?.length === 0, 'second tidy: ' + tidied.status + ' ' + tidied.text);
        expect(audioLanguages(file) === 'eng' && audioLanguages(kept) === 'eng,fre' && !fs.existsSync(copy), 'the sound copy did not take the film\'s place');
        return 'copy without audio left with a warning, sound copy put in place';
    } finally {
        fs.rmSync(copy, { force: true });
    }
});

await check('track cleanup keeps the film\'s own audio when only a commentary is in your language', async () => {
    // Scan only: the runs above are done, so nothing changes this file.
    await addFilm('Track Extras (2021)', ['jpn', 'eng:Commentary', 'fre:Commentary'], 4);
    const plan = ((await ok('/JellyfinMedic/Tracks/Scan')).Sample || []).find((p) => /Track Extras/.test(p.Path || ''));
    expect(plan, 'the French commentary should be planned to go');
    const kept = plan.Tracks.filter((t) => t.Kind === 'audio' && t.Keep).map((t) => `${t.Language}:${t.Title}`).join(',');
    expect(kept === 'jpn:,eng:Commentary', 'kept audio: ' + kept);
    return 'kept ' + kept;
});

await check('Medic Picks builds picks from watch history', async () => {
    // run.sh gives Samples 1, 2 and 11 the director Ada Example. Watching 1, 2, 5 and 6 makes Comedy and
    // Thriller the strongest genres (Samples 9 and 10 have two of them each) and leaves Samples 3 and 11 with
    // the same genres (Horror, Romance) and the same default rating, so only the director can put 11 above 3.
    const items = (await ok(`/Items?Recursive=true&IncludeItemTypes=Movie&UserId=${ctx.userId}`)).Items;
    const byName = new Map(items.map((item) => [item.Name, item]));
    for (const n of [1, 2, 5, 6]) {
        const item = byName.get(`Sample ${n}`);
        expect(item, `Sample ${n} is not in the library`);
        await api('POST', `/UserPlayedItems/${item.Id}?userId=${ctx.userId}`);
    }
    const before = (await ok('/MedicPicks/Me')).generatedUtc;
    const tasks = await ok('/ScheduledTasks');
    const build = tasks.find((t) => t.Key === 'MedicPicksBuild' || /personal picks/i.test(t.Name));
    expect(build, 'Build personal picks task missing');
    await api('POST', `/ScheduledTasks/Running/${build.Id}`);
    let me = null;
    for (let i = 0; i < 60 && !(me?.generatedUtc && me.generatedUtc !== before && me.inLibrary?.length); i++) {
        await sleep(1000);
        me = (await api('GET', '/MedicPicks/Me')).json;
    }
    expect(me?.generatedUtc && me.generatedUtc !== before && me.inLibrary?.length, 'no new picks after a minute: ' + JSON.stringify(me).slice(0, 300));
    const picks = me.inLibrary;
    const rank = (n) => picks.findIndex((p) => p.name === `Sample ${n}`);
    const order = picks.map((p) => p.name).join(', ');
    expect([1, 2, 5, 6].every((n) => rank(n) === -1), 'a watched film was picked: ' + order);
    // Genres: the two films in both favourite genres come first.
    expect(rank(9) >= 0 && rank(10) >= 0 && Math.max(rank(9), rank(10)) <= 1, 'Samples 9 and 10 are not the top two: ' + order);
    expect(/^You watch a lot of /.test(picks[rank(9)].reason), 'Sample 9 reason: ' + picks[rank(9)].reason);
    // People: the director lifts Sample 11 above Sample 3, its twin in genres.
    expect(rank(11) >= 0 && rank(3) >= 0 && rank(11) < rank(3), 'Sample 11 does not rank above Sample 3: ' + order);
    const reason = picks[rank(11)].reason;
    expect(reason === 'Directed by Ada Example, whose work you\'ve watched', 'Sample 11 reason: ' + reason);
    return `${picks.length} picks: ${order.split(', ').slice(0, 4).join(', ')}, ...`;
});

await check('Medic Picks updates the playlist in place on the next build, keeping its id', async () => {
    const find = async () => (await ok(`/Items?Recursive=true&IncludeItemTypes=Playlist&userId=${ctx.userId}`)).Items
        .filter((i) => /picks for you/i.test(i.Name));
    const before = await find();
    expect(before.length === 1, 'playlists before: ' + before.map((p) => p.Name).join(', '));
    const was = (await ok('/MedicPicks/Me')).generatedUtc;
    const build = (await ok('/ScheduledTasks')).find((t) => t.Key === 'MedicPicksBuild' || /personal picks/i.test(t.Name));
    await api('POST', `/ScheduledTasks/Running/${build.Id}`);
    let me = null;
    for (let i = 0; i < 60 && !(me?.generatedUtc && me.generatedUtc !== was && !me.rebuilding); i++) {
        await sleep(1000);
        me = (await api('GET', '/MedicPicks/Me')).json;
    }
    // The task saves part-way through; wait for it to finish.
    for (let i = 0; i < 60 && (await ok('/ScheduledTasks')).find((t) => t.Id === build.Id).State !== 'Idle'; i++) await sleep(1000);
    const after = await find();
    expect(after.length === 1 && after[0].Id === before[0].Id, `playlist ${before[0].Id} became ${after.map((p) => p.Id).join(', ')}`);
    const items = (await ok(`/Playlists/${after[0].Id}/Items?userId=${ctx.userId}`)).Items;
    expect(items.length === me.inLibrary.length, `${items.length} items in the playlist for ${me.inLibrary.length} picks`);
    return `kept ${after[0].Id} with ${items.length} items`;
});

await check('My picks page is served with no-cache', async () => {
    const res = await api('GET', '/MedicPicks/Page', undefined, true);
    expect(res.status === 200, 'status ' + res.status);
    expect((res.headers.get('cache-control') || '').includes('no-cache'), 'cache-control ' + res.headers.get('cache-control'));
});

await check('Medic Picks "My requests" says Seerr is out of reach instead of failing', async () => {
    // A Seerr that refuses connections, then one that never answers: both must come back as
    // reachable:false, which the page shows, and the second within the time limit per call.
    const http = await import('node:http');
    const silent = http.createServer(() => { /* never answers */ });
    await new Promise((resolve) => silent.listen(P(18991), '0.0.0.0', resolve));
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    const myRequests = async (url) => {
        // Each address is another server, which removes the saved key, so the key is saved after it.
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...cfg, JellyseerrUrl: url, ShowRequests: true });
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
        const started = Date.now();
        const r = await api('GET', '/MedicPicks/Me/Requests');
        return { ...r, seconds: (Date.now() - started) / 1000 };
    };
    try {
        const refused = await myRequests('http://host.docker.internal:9');
        expect(refused.status === 200 && refused.json?.reachable === false, `refused: ${refused.status} ${refused.text.slice(0, 200)}`);
        const silentSeerr = await myRequests(`http://host.docker.internal:${P(18991)}`);
        expect(silentSeerr.status === 200 && silentSeerr.json?.reachable === false, `no answer: ${silentSeerr.status} ${silentSeerr.text.slice(0, 200)}`);
        expect(silentSeerr.seconds < 30, `took ${silentSeerr.seconds} s`);
        return `both reported as out of reach (the silent one after ${silentSeerr.seconds.toFixed(0)} s)`;
    } finally {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        silent.closeAllConnections();
        silent.close();
    }
});

await check('My picks page names itself with the plugin\'s version', async () => {
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    expect(plugin, 'Medic Picks not installed');
    const version = plugin.Version.split('.').slice(0, 3).join('.');
    const page = (await api('GET', '/MedicPicks/Page')).text;
    const sent = (page.match(/var VERSION = '([^']*)'/) || [])[1];
    expect(sent === version, `the page says ${sent}, the plugin is ${version}`);
    return version;
});

await check('Medic Picks "My requests" says Seerr is out of reach when something else answers its address', async () => {
    // A sign-in page in front of Seerr, say: it answers every address with 200 and a web page.
    const http = await import('node:http');
    const signIn = http.createServer((req, res) => {
        res.writeHead(200, { 'Content-Type': 'text/html' });
        res.end('<html><body>Sign in</body></html>');
    });
    await new Promise((resolve) => signIn.listen(P(18996), '0.0.0.0', resolve));
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...cfg, JellyseerrUrl: `http://host.docker.internal:${P(18996)}`, ShowRequests: true });
    await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
    try {
        const mine = await api('GET', '/MedicPicks/Me/Requests');
        expect(mine.status === 200 && mine.json?.reachable === false, `My requests: ${mine.status} ${mine.text.slice(0, 200)}`);
        return 'reported as out of reach';
    } finally {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        signIn.close();
    }
});

await check('Medic Picks gives people only a Seerr address they can open', async () => {
    const id = 'b34ec5d1-b70f-435f-b77d-9b76979b03c7';
    const cfg = await ok(`/Plugins/${id}/Configuration`);
    const save = async (changes) => {
        const r = await api('POST', `/Plugins/${id}/Configuration`, { ...cfg, ...changes });
        expect(r.status === 204 || r.status === 200, 'saving settings ' + r.status);
    };
    try {
        // The server reaches Seerr inside Docker; people open it at its public address.
        await save({ JellyseerrUrl: 'http://seerr:5055', SeerrLinkUrl: 'https://requests.example.com/', SeerrDirectRequests: false });
        let me = await ok('/MedicPicks/Me');
        expect(me.seerrUrl === 'https://requests.example.com' && me.requestMode === 'link', `link: ${me.seerrUrl} (${me.requestMode})`);
        // Never an address that isn't http or https in a link.
        await save({ JellyseerrUrl: 'javascript:alert(1)//', SeerrLinkUrl: '', SeerrDirectRequests: false });
        me = await ok('/MedicPicks/Me');
        // No address at all: the field is left out of the answer.
        expect(me.seerrUrl == null && me.requestMode === 'none', `javascript: address: ${me.seerrUrl} (${me.requestMode})`);
        // Requests made from the page itself: nobody opens Seerr, so its address stays on the server.
        await save({ JellyseerrUrl: 'http://seerr:5055', SeerrLinkUrl: '', SeerrDirectRequests: true });
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
        me = await ok('/MedicPicks/Me');
        expect(me.seerrUrl == null && me.requestMode === 'direct', `direct requests: ${me.seerrUrl} (${me.requestMode})`);
        return 'public address in links; javascript: never; none with direct requests';
    } finally {
        await api('POST', `/Plugins/${id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
    }
});

await check('Medic Picks removes the saved Seerr key when the Seerr address moves to another server', async () => {
    // A saved key is only ever sent to the server it was entered for, as Medic Profiles does with its keys.
    const id = 'b34ec5d1-b70f-435f-b77d-9b76979b03c7';
    const cfg = await ok(`/Plugins/${id}/Configuration`);
    const saveAddress = (url) => api('POST', `/Plugins/${id}/Configuration`, { ...cfg, JellyseerrUrl: url });
    const keySaved = async () => (await ok('/MedicPicks/Admin/Status')).seerrKeySet;
    try {
        await saveAddress('http://seerr.example:5055');
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
        await saveAddress('http://SEERR.example:5055/seerr/');
        expect(await keySaved(), 'the key was removed when only the path changed');
        await saveAddress('http://elsewhere.example:5055');
        expect(!(await keySaved()), 'the key was kept for another server');
        return 'kept for a new path, removed for another server';
    } finally {
        await api('POST', `/Plugins/${id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
    }
});

// ---------- Medic Profiles, against a pretend Sonarr ----------

const fakeArr = await import('./fake-arr.mjs');
const arrServer = await fakeArr.start(P(18989));
const ARR = process.env.ARR_URL || `http://host.docker.internal:${P(18989)}`;

await check('Medic Picks approves and declines requests as the admin\'s own Seerr account', async () => {
    // Without X-API-User, Seerr acts as its owner account for any call made with the API key.
    const seerrServer = await fakeArr.startSeerr(P(18992));
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...cfg, JellyseerrUrl: `http://host.docker.internal:${P(18992)}`, ShowRequests: true });
    await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
    try {
        const approved = await api('POST', '/MedicPicks/Admin/Requests/7/Approve');
        const declined = await api('POST', '/MedicPicks/Admin/Requests/8/Decline');
        expect(approved.json?.ok && declined.json?.ok, `answers: ${approved.text} ${declined.text}`);
        const made = fakeArr.seerr.decisions.map((d) => `${d.action} ${d.id} as ${d.asUser}`).join(', ');
        expect(made === 'approve 7 as 42, decline 8 as 42', 'Seerr saw: ' + made);
        return made;
    } finally {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        seerrServer.close();
    }
});

await check('Medic Picks "My requests" skips a season entry it can\'t read instead of failing', async () => {
    // A season list holding something other than season entries; only season 2 is one, so only its download counts.
    const seerrServer = await fakeArr.startSeerr(P(18995));
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    await api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...cfg, JellyseerrUrl: `http://host.docker.internal:${P(18995)}`, ShowRequests: true });
    await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
    fakeArr.seerr.extraRequests.push({
        id: 13, status: 2, type: 'tv', createdAt: '2026-10-03T10:00:00.000Z', requestedBy: { id: 42 },
        seasons: [1, null, { seasonNumber: 2 }],
        media: {
            tmdbId: 1399, mediaType: 'tv', status: 3,
            downloadStatus: [
                { status: 'downloading', size: 100, sizeLeft: 50, episode: { seasonNumber: 2, episodeNumber: 1 } },
                { status: 'downloading', size: 100, sizeLeft: 0, episode: { seasonNumber: 3, episodeNumber: 1 } }
            ]
        }
    });
    try {
        const mine = await api('GET', '/MedicPicks/Me/Requests');
        expect(mine.status === 200, `My requests: ${mine.status} ${mine.text.slice(0, 200)}`);
        const row = (mine.json?.requests || []).find((r) => r.id === 13);
        expect(row?.progress === 50, 'request 13: ' + JSON.stringify(row));
        const waiting = await api('GET', '/MedicPicks/Admin/Requests');
        expect(waiting.status === 200, `requests waiting for approval: ${waiting.status} ${waiting.text.slice(0, 200)}`);
        return 'season 2 counted, the rest skipped';
    } finally {
        fakeArr.seerr.extraRequests.length = 0;
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        seerrServer.close();
    }
});

await check('Medic Picks shows only your own requests and keeps Discover-off accounts from requesting', async () => {
    // This Seerr ignores ?requestedBy= and lists someone else's request too; Medic Picks must drop it.
    const seerrServer = await fakeArr.startSeerr(P(18993));
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    const setCfg = (changes) => api('POST', `/Plugins/${plugin.Id}/Configuration`, { ...cfg, JellyseerrUrl: `http://host.docker.internal:${P(18993)}`, ShowRequests: true, SeerrDirectRequests: true, ...changes });
    await setCfg({});
    await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
    try {
        const mine = await ok('/MedicPicks/Me/Requests');
        const ids = (mine.requests || []).map((r) => r.id).join(',');
        expect(ids === '11', 'requests shown: ' + ids);

        await setCfg({ EnableDiscover: true, DiscoverDisabledUserIds: [ctx.userId] });
        const refused = await api('POST', '/MedicPicks/Request', { TmdbId: 552, MediaType: 'movie' });
        expect(refused.status === 403, `a Discover-off account could request: ${refused.status} ${refused.text.slice(0, 200)}`);
        await setCfg({ EnableDiscover: false });
        const serverOff = await api('POST', '/MedicPicks/Request', { TmdbId: 552, MediaType: 'movie' });
        expect(serverOff.status === 403, `a request went through with Discover off on the server: ${serverOff.status} ${serverOff.text.slice(0, 200)}`);
        return 'only request 11 shown; requests refused with 403';
    } finally {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        seerrServer.close();
    }
});

await check('Medic Picks sends a person\'s request to Seerr as their own Seerr account', async () => {
    // Without X-API-User, Seerr would make the request as its owner account, past that person's own limits.
    const seerrServer = await fakeArr.startSeerr(P(18997));
    const plugin = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const cfg = await ok(`/Plugins/${plugin.Id}/Configuration`);
    const name = 'e2e-seerr-requester';
    const device = AUTH.replace('medic-e2e', name);
    const made = await api('POST', '/Users/New', { Name: name, Password: 'e2e-pass' });
    expect(made.status === 200, 'user not created: ' + made.text);
    try {
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, {
            ...cfg, JellyseerrUrl: `http://host.docker.internal:${P(18997)}`, ShowRequests: true, SeerrDirectRequests: true,
            EnableDiscover: true, DiscoverDisabledUserIds: []
        });
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
        // This person is Seerr user 77; everyone else (the admin too) is 42.
        fakeArr.seerr.accounts[made.json.Id.replace(/-/g, '').toLowerCase()] = 77;
        const signIn = await fetch(JF + '/Users/AuthenticateByName', {
            method: 'POST',
            headers: { Authorization: device, 'Content-Type': 'application/json' },
            body: JSON.stringify({ Username: name, Pw: 'e2e-pass' })
        });
        expect(signIn.status === 200, 'sign-in: ' + signIn.status);
        const userToken = (await signIn.json()).AccessToken;
        const request = async (body) => {
            const res = await fetch(JF + '/MedicPicks/Request', {
                method: 'POST',
                headers: { Authorization: `${device}, Token="${userToken}"`, 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            const text = await res.text();
            expect(res.status === 200, `request ${JSON.stringify(body)}: ${res.status} ${text.slice(0, 200)}`);
            return JSON.parse(text);
        };
        const film = await request({ TmdbId: 603, MediaType: 'movie' });
        const series = await request({ TmdbId: 1399, MediaType: 'tv' });
        expect(film.status === 'requested' && series.status === 'requested', `answers: ${JSON.stringify(film)} ${JSON.stringify(series)}`);

        const seen = fakeArr.seerr.created.map((c) => `${JSON.stringify(c.body)} as ${c.asUser}`).join(', ');
        expect(seen === '{"mediaType":"movie","mediaId":603} as 77, {"mediaType":"tv","mediaId":1399,"seasons":"all"} as 77', 'Seerr saw: ' + seen);
        expect(fakeArr.seerr.created.every((c) => (c.contentType || '').startsWith('application/json')), 'content types: ' + fakeArr.seerr.created.map((c) => c.contentType).join(', '));
        return seen;
    } finally {
        fakeArr.seerr.created.length = 0;
        fakeArr.seerr.accounts = {};
        await api('DELETE', `/Users/${made.json.Id}`);
        await api('POST', `/Plugins/${plugin.Id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        seerrServer.close();
    }
});

await check('Medic Profiles connects to Sonarr', async () => {
    const saved = await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, SonarrKey: 'test-key', RadarrUrl: '' });
    expect(saved.status === 200 || saved.status === 204, 'saving settings ' + saved.status);
    const test = await api('POST', '/MedicProfiles/Test/sonarr');
    expect(test.json?.ok, 'test failed: ' + test.text);
});

await check('Medic Profiles never sends a saved key to a new server, and keeps a damaged keys file', async () => {
    const keySet = async () => (await ok('/MedicProfiles/Status')).sonarr.keySet;
    try {
        // Same server, key box left blank: the saved key stays.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR + '/', RadarrUrl: '' });
        expect(await keySet(), 'the key was dropped although the server is the same');
        // Another server, key box left blank: the key must be typed again.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: `http://host.docker.internal:${P(18994)}`, RadarrUrl: '' });
        expect(!(await keySet()), 'the saved key was kept for a different server');
    } finally {
        // Back to the pretend Sonarr whatever happened, so a failure here doesn't also fail the checks after it.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, SonarrKey: 'test-key', RadarrUrl: '' });
    }

    // A keys file that can't be read is set aside, not replaced by one holding only the new key.
    const dir = '/config/data/medic-profiles';
    execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', `echo '{not json' > ${dir}/secrets.json`]);
    await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, SonarrKey: 'test-key', RadarrUrl: '' });
    // It's kept beside it, named with the time it was set aside.
    const kept = execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', `cat ${dir}/secrets.json.*.bad`]).toString();
    expect(kept.includes('{not json'), 'the damaged file was not kept: ' + kept);
    expect(await keySet(), 'the new key was not saved');
    return 'key dropped for a new server; damaged file kept beside it';
});

await check('Medic Profiles shows indexer status', async () => {
    const r = await ok('/MedicProfiles/Indexers');
    const byName = Object.fromEntries((r.rows || []).map((x) => [x.name, x]));
    expect(byName['Broken Indexer']?.state === 'failing', 'broken: ' + JSON.stringify(byName['Broken Indexer']));
    expect(byName['Good Indexer']?.state === 'ok', 'good: ' + JSON.stringify(byName['Good Indexer']));
    expect(byName['Unused Indexer']?.state === 'off', 'unused: ' + JSON.stringify(byName['Unused Indexer']));
    expect((r.warnings || []).some((w) => /Broken Indexer/.test(w)), 'no health warning');
});

await check('Medic Profiles says when the queue is cut short, and can remove a download but leave it in the client', async () => {
    fakeArr.queue.total = 1234;
    try {
        const r = await ok('/MedicProfiles/Queue');
        expect(/first 1,000 of Sonarr's 1,234 queue entries/.test(r.notes?.sonarr || ''), 'note: ' + JSON.stringify(r.notes));
        const row = r.rows[0];
        const removed = await api('POST', '/MedicProfiles/Queue/Remove', { app: 'sonarr', queueIds: row.queueIds, block: false, keepInClient: true, title: row.mediaTitle });
        expect(removed.json?.ok, 'remove: ' + removed.text);
        const sent = fakeArr.received.filter((x) => x.method === 'DELETE' && x.path === '/api/v3/queue/bulk').at(-1);
        expect(/removeFromClient=false/.test(sent?.query || ''), 'sent: ' + JSON.stringify(sent));
        return `${r.rows.length} rows; removeFromClient=false sent`;
    } finally {
        fakeArr.queue.total = 0;
    }
});

await check('Medic Profiles blocks program files in qBittorrent, keeping the owner\'s own entries', async () => {
    const qbitServer = await fakeArr.startQbit(P(18080));
    // The owner's own list, switched off on purpose, holding *.zip, which is also one of Medic Profiles' patterns.
    Object.assign(fakeArr.qbit.prefs, { excluded_file_names_enabled: false, excluded_file_names: '*.nfo\n*.zip' });
    try {
        const QB = ARR.replace(`:${P(18989)}`, `:${P(18080)}`);
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, RadarrUrl: '', QbitUrl: QB, QbitUser: 'admin', QbitPassword: 'secret' });
        let state = await ok('/MedicProfiles/FileFilter');
        expect(state.reachable && !state.blocksPrograms, 'before: ' + JSON.stringify(state));
        const set = await api('POST', '/MedicProfiles/FileFilter', { block: true, archives: false });
        expect(set.json?.ok, 'block failed: ' + set.text);
        const sent = fakeArr.qbit.sets.at(-1);
        expect(sent.excluded_file_names_enabled === true, 'list not switched on');
        const list = sent.excluded_file_names.split('\n');
        expect(list.includes('*.exe') && list.includes('*.nfo') && list.includes('*.zip') && !list.includes('*.rar'), 'list: ' + sent.excluded_file_names);
        state = await ok('/MedicProfiles/FileFilter');
        expect(state.blocksPrograms && !state.blocksArchives, 'after: ' + JSON.stringify(state));
        await api('POST', '/MedicProfiles/FileFilter', { block: false, archives: false });
        const after = fakeArr.qbit.prefs;
        expect(after.excluded_file_names === '*.nfo\n*.zip' && after.excluded_file_names_enabled === false,
            `unblock left: ${JSON.stringify(after.excluded_file_names)}, switched ${after.excluded_file_names_enabled ? 'on' : 'off'}`);
        // One session for all of the above: opening the tab again doesn't sign in again.
        await ok('/MedicProfiles/FileFilter');
        expect(fakeArr.qbit.logins === 1, `signed in ${fakeArr.qbit.logins} times`);

        // A wrong password is tried once, not each time the tab opens: each failure counts towards qBittorrent's ban.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, RadarrUrl: '', QbitUrl: QB, QbitUser: 'admin', QbitPassword: 'wrong' });
        state = await ok('/MedicProfiles/FileFilter');
        expect(!state.reachable && /user name and password/.test(state.message), 'wrong password: ' + JSON.stringify(state));
        await ok('/MedicProfiles/FileFilter');
        expect(fakeArr.qbit.failedLogins === 1, `the wrong password was tried ${fakeArr.qbit.failedLogins} times`);
        return 'one sign-in for the session, one try for a wrong password';
    } finally {
        qbitServer.close();
    }
});

await check('Sonarr, Seerr and qBittorrent keys and passwords never follow a redirect to another address', async () => {
    // Each saved address answers with a 307 to another server, which must hear nothing: .NET resends X-Api-Key on a
    // redirect, and for a 307 the qBittorrent sign-in form, password and all (#206).
    const http = await import('node:http');
    const caught = [];
    const catcher = http.createServer((req, res) => {
        let body = '';
        req.on('data', (c) => { body += c; });
        req.on('end', () => {
            caught.push(`${req.method} ${req.url} key=${req.headers['x-api-key'] || ''} ${body}`);
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end('{"appName":"Sonarr","version":"4.0.15","id":1}');
        });
    });
    const target = `http://host.docker.internal:${P(18997)}`;
    const redirector = http.createServer((req, res) => {
        req.resume();
        res.writeHead(307, { Location: target + req.url });
        res.end();
    });
    await new Promise((resolve) => catcher.listen(P(18997), '0.0.0.0', resolve));
    await new Promise((resolve) => redirector.listen(P(18998), '0.0.0.0', resolve));
    const moved = `http://host.docker.internal:${P(18998)}`;
    const picks = (await ok('/Plugins')).find((p) => p.Name === 'Medic Picks');
    const picksCfg = await ok(`/Plugins/${picks.Id}/Configuration`);
    try {
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: moved, SonarrKey: 'test-key', RadarrUrl: '', QbitUrl: moved, QbitUser: 'admin', QbitPassword: 'secret' });
        const sonarr = await api('POST', '/MedicProfiles/Test/sonarr');
        expect(sonarr.json?.ok === false && sonarr.json.message.includes(target), 'Sonarr Test: ' + sonarr.text);
        const queue = await api('GET', '/MedicProfiles/Queue');
        expect(queue.text.includes(target), 'Queue: ' + queue.text.slice(0, 300));
        const qbit = await ok('/MedicProfiles/FileFilter');
        expect(!qbit.reachable && qbit.message.includes(target), 'qBittorrent: ' + JSON.stringify(qbit));

        await api('POST', `/Plugins/${picks.Id}/Configuration`, { ...picksCfg, JellyseerrUrl: moved, ShowRequests: true });
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-seerr-key' });
        const seerr = await api('POST', '/MedicPicks/Admin/TestSeerr');
        expect(seerr.json?.ok === false && seerr.json.message.includes(target), 'Seerr Test: ' + seerr.text);
        const mine = await api('GET', '/MedicPicks/Me/Requests');
        expect(mine.status === 200 && mine.json?.reachable === false, `My requests: ${mine.status} ${mine.text.slice(0, 200)}`);

        expect(caught.length === 0, 'the other address received: ' + caught.join(' | '));
        return 'Sonarr, Seerr and qBittorrent each named where they were sent; the other address heard nothing';
    } finally {
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, SonarrKey: 'test-key', RadarrUrl: '', QbitUrl: '' });
        await api('POST', `/Plugins/${picks.Id}/Configuration`, picksCfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
        catcher.close();
        redirector.close();
    }
});

// What the page's "Try qBittorrent again" button posts.
async function tryQbitAgain() {
    const r = await api('POST', '/MedicProfiles/FileFilter/Retry', {});
    expect(r.status === 200, `FileFilter/Retry returned ${r.status} ${r.text.slice(0, 200)}`);
    return r.json;
}

await check('Medic Profiles signs in to qBittorrent 5.2, and tries a refused sign-in again only when asked', async () => {
    const qbitServer = await fakeArr.startQbit(P(18080));
    Object.assign(fakeArr.qbit, { v52: true, banned: true, logins: 0, failedLogins: 0 });
    try {
        const QB = ARR.replace(`:${P(18989)}`, `:${P(18080)}`);
        const save = () => api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, RadarrUrl: '', QbitUrl: QB, QbitUser: 'admin', QbitPassword: 'secret' });
        // Banned: the page says so, and opening it again doesn't knock on qBittorrent's door.
        await save();
        let state = await ok('/MedicProfiles/FileFilter');
        expect(!state.reachable && state.refused && /blocked sign-ins/.test(state.message), 'banned: ' + JSON.stringify(state));
        await ok('/MedicProfiles/FileFilter');
        expect(fakeArr.qbit.logins === 1, `tried ${fakeArr.qbit.logins} times while banned`);
        // The ban ends. Saving the same settings doesn't sign in; Try again does, with 5.2's empty 204 answer.
        fakeArr.qbit.banned = false;
        await save();
        await ok('/MedicProfiles/FileFilter');
        expect(fakeArr.qbit.logins === 1, `saving the same settings signed in: ${fakeArr.qbit.logins} sign-ins`);
        state = await tryQbitAgain();
        expect(state.reachable && !state.refused, 'after the ban: ' + JSON.stringify(state));
        expect(fakeArr.qbit.logins === 2, `signed in ${fakeArr.qbit.logins} times`);
        // 5.2 answers a wrong password with 401.
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, RadarrUrl: '', QbitUrl: QB, QbitUser: 'admin', QbitPassword: 'wrong' });
        state = await ok('/MedicProfiles/FileFilter');
        expect(!state.reachable && /user name and password/.test(state.message), 'wrong password: ' + JSON.stringify(state));
        return 'ban reported, retried on Try again, 204 sign-in accepted, 401 refused';
    } finally {
        Object.assign(fakeArr.qbit, { v52: false, banned: false });
        qbitServer.close();
    }
});

await check('Medic Profiles spends no qBittorrent sign-in on save, Test or block after a refusal, and Try again spends one', async () => {
    const qbitServer = await fakeArr.startQbit(P(18080));
    Object.assign(fakeArr.qbit, { logins: 0, failedLogins: 0 });
    try {
        const QB = ARR.replace(`:${P(18989)}`, `:${P(18080)}`);
        // What the page sends: the password box is empty unless a new one was typed.
        const save = (password, extra) => api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, RadarrUrl: '', QbitUrl: QB, QbitUser: 'admin', QbitPassword: password, ...extra });
        // Start signed in, so the wrong password below is new to Medic and not one an earlier check left refused.
        await save('secret');
        await ok('/MedicProfiles/FileFilter');
        Object.assign(fakeArr.qbit, { logins: 0, failedLogins: 0 });
        await save('wrong');
        let state = await ok('/MedicProfiles/FileFilter');
        expect(!state.reachable && state.refused, 'wrong password: ' + JSON.stringify(state));
        expect(fakeArr.qbit.logins === 1, `the wrong password was tried ${fakeArr.qbit.logins} times`);

        // Every button the page has posts the settings first; none of them may spend another sign-in.
        await save('');
        await ok('/MedicProfiles/FileFilter');
        await save('');
        await api('POST', '/MedicProfiles/Test/sonarr', {});
        await save('', { ClearRadarrKey: true });
        await save('');
        const blocked = await api('POST', '/MedicProfiles/FileFilter', { block: true, archives: false });
        expect(blocked.json && !blocked.json.ok && blocked.json.refused, 'block: ' + blocked.text);
        await save('');
        await api('POST', '/MedicProfiles/FileFilter', { block: false, archives: false });
        await ok('/MedicProfiles/FileFilter');
        expect(fakeArr.qbit.logins === 1, `save, Test, Remove key and block spent ${fakeArr.qbit.logins - 1} more sign-ins`);

        // Try again spends exactly one; the second refusal stops sign-ins until the password changes or Try again.
        state = await tryQbitAgain();
        expect(fakeArr.qbit.logins === 2, `Try again signed in ${fakeArr.qbit.logins - 1} times`);
        expect(state.refused && /stopped/.test(state.message), 'second refusal: ' + JSON.stringify(state));
        await save('');
        await ok('/MedicProfiles/FileFilter');
        expect(fakeArr.qbit.logins === 2, `signed in after stopping: ${fakeArr.qbit.logins} sign-ins`);
        await tryQbitAgain();
        expect(fakeArr.qbit.logins === 3, `Try again after two refusals signed in ${fakeArr.qbit.logins - 2} times`);

        // A password changed in Medic is tried at once.
        await save('secret');
        state = await ok('/MedicProfiles/FileFilter');
        expect(state.reachable && !state.refused, 'right password: ' + JSON.stringify(state));
        expect(fakeArr.qbit.logins === 4, `the new password took ${fakeArr.qbit.logins - 3} sign-ins`);
        return `${fakeArr.qbit.logins} sign-ins in all`;
    } finally {
        qbitServer.close();
    }
});

// ---------- Exposure check, without asking any outside service ----------

await check('exposure check says it tested from this server, and a failed TLS handshake counts as an answer', async () => {
    const run = async (host, port, protocol) => {
        const r = await api('POST', `/JellyfinMedic/ExposureCheck?host=${encodeURIComponent(host)}&port=${port}&protocol=${protocol}`);
        expect(r.status === 200, `${host}:${port} returned ${r.status} ${r.text.slice(0, 200)}`);
        return r.json;
    };
    // The pretend Sonarr only speaks plain http, so an https request to it fails the TLS handshake,
    // the same way a certificate this server doesn't trust does.
    const arr = new URL(ARR);
    const tls = await run(arr.hostname, Number(arr.port), 'https');
    expect(tls.Reachable && tls.FromThisServer, 'TLS failure: ' + JSON.stringify(tls));
    const unknown = await run('medic-e2e.invalid', 443, 'https');
    expect(unknown.Checked && !unknown.Reachable && unknown.FromThisServer, 'unknown domain: ' + JSON.stringify(unknown));
    const withPath = await run('medic-e2e.invalid/System', 443, 'https');
    expect(withPath.Error && !withPath.Checked, 'host with a path: ' + JSON.stringify(withPath));
    const defaults = await ok('/JellyfinMedic/ExposureDefaults');
    expect(!defaults.PublicIp, 'public IP looked up without being asked: ' + defaults.PublicIp);
});

// ---------- Pages in a real browser ----------

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || undefined });
// A fixed language: GitHub's runner otherwise hands the browser "en-us@posix", which Jellyfin's web app can't format numbers with.
const page = await browser.newPage({ locale: 'en-GB' });
const errors = [];
page.on('pageerror', (e) => errors.push(e.message + ' @ ' + String(e.stack || '').split('\n').slice(1, 3).join(' ').trim()));
// Sign in through the real login form; Jellyfin ties tokens to the browser's device ID.
await page.goto(JF + '/web/index.html#/login');
await page.waitForSelector('#txtManualName, input[type="text"]', { timeout: 60000 });
await page.fill('#txtManualName', 'admin');
await page.fill('#txtManualPassword', 'admin');
await page.click('.manualLoginForm button[type="submit"]');
await page.waitForURL(/home/, { timeout: 60000 });

// Opens a plugin page, then clicks through each of its tabs, failing on any script error.
async function openPage(name, hash, selector, tabs) {
    await check(`${name} page and its tabs open without script errors`, async () => {
        errors.length = 0;
        await page.goto(JF + '/web/index.html#' + hash);
        await page.waitForSelector(selector, { timeout: 30000 });
        await sleep(2000);
        if (selector.startsWith('#JellyfinMedicPage')) await settleWhatsNew(page);
        let count = 0;
        if (tabs) {
            const buttons = await page.locator(selector.split(' ')[0] + ' ' + tabs).all();
            for (const b of buttons) {
                if (!(await b.isVisible())) continue;
                await b.click();
                await sleep(1500);
                count++;
            }
        }
        // Jellyfin's own web app reports CancelledError when navigating away cancels its requests.
        const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
        expect(ours.length === 0, ours.join(' | '));
        return count ? `${count} tabs` : '';
    });
}

await openPage('Jellyfin Medic', '/configurationpage?name=JellyfinMedic', '#JellyfinMedicPage .so-tab', '.so-tab');

await check('Jellyfin Medic page is styled by its stylesheet', async () => {
    await page.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    await page.waitForSelector('#JellyfinMedicPage .so-topbar', { timeout: 30000 });
    // Only medic.css lays the top bar out as a row.
    await page.waitForFunction(() => getComputedStyle(document.querySelector('#JellyfinMedicPage .so-topbar')).display === 'flex', null, { timeout: 10000 });
    return 'top bar laid out as a row';
});
await openPage('Medic Profiles', '/configurationpage?name=MedicProfiles', '#MedicProfilesPage .mp-tab', '.mp-tab');
await openPage('Medic Picks settings', '/configurationpage?name=Medic%20Picks', '#MedicPicksConfigForm');

await check('Medic Picks settings: the user boxes show, and a key that isn\'t saved says so', async () => {
    await page.goto(JF + '/web/index.html#/configurationpage?name=Medic%20Picks');
    await page.waitForSelector('#DiscoverUsers .discoverUser', { timeout: 30000 });
    const upgraded = await page.evaluate(() => Array.from(document.querySelectorAll('#DiscoverUsers .discoverUser')).every((i) => i.classList.contains('emby-checkbox')));
    expect(upgraded, 'the user boxes are plain inputs, not emby-checkbox');
    const discoverWas = await page.isChecked('#EnableDiscover');
    const tmdbKey = /MedicPicks\/Admin\/TmdbKey/;
    await page.route(tmdbKey, (route) => route.fulfill({ status: 500, body: '' }));
    try {
        await page.fill('#TmdbKey', 'e2e-not-a-real-key');
        await page.click('#SaveTmdbKey');
        await page.getByText('The TMDb key wasn\'t saved').waitFor({ timeout: 10000 });
        expect(await page.isChecked('#EnableDiscover') === discoverWas, 'Discover picks stayed switched on after the key failed to save');
    } finally {
        await page.unroute(tmdbKey);
        await page.keyboard.press('Escape');
    }
});

await check('Medic Picks settings: a Seerr key typed with another server\'s address is the one kept', async () => {
    // Saving the new address removes the old server's key, so the page must save the typed key after it.
    const id = 'b34ec5d1-b70f-435f-b77d-9b76979b03c7';
    const cfg = await ok(`/Plugins/${id}/Configuration`);
    const typeNewServer = async (url) => {
        await api('POST', `/Plugins/${id}/Configuration`, { ...cfg, JellyseerrUrl: 'http://seerr.example:5055' });
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: 'e2e-old-seerr-key' });
        await page.goto(JF + '/web/index.html#/configurationpage?name=Medic%20Picks');
        await page.reload();
        await page.waitForFunction(() => (document.querySelector('#SeerrKeyStatus')?.textContent || '').startsWith('A Seerr key is saved'), null, { timeout: 30000 });
        await page.fill('#JellyseerrUrl', url);
        await page.fill('#SeerrKey', 'e2e-new-seerr-key');
    };
    const keySaved = async () => (await ok('/MedicPicks/Admin/Status')).seerrKeySet;
    try {
        await typeNewServer('http://elsewhere.example:5055');
        await page.click('#SaveSeerrKey');
        await page.getByText('Seerr key and address saved.').waitFor({ timeout: 10000 });
        await page.keyboard.press('Escape');
        expect(await keySaved(), 'Save key: no key left after saving it with the new address');

        await typeNewServer('http://elsewhere.example:5056');
        const saved = page.waitForResponse((r) => r.url().includes('/MedicPicks/Admin/Status'));
        await page.click('#MedicPicksConfigPage button[type="submit"]');
        await saved;
        expect(await keySaved(), 'Save: no key left after saving it with the new address');
        return 'kept by Save key and by Save';
    } finally {
        await api('POST', `/Plugins/${id}/Configuration`, cfg);
        await api('POST', '/MedicPicks/Admin/SeerrKey', { Key: '' });
    }
});

await check('Medic Picks settings: changing who gets Discover picks rebuilds their picks, and My picks follows at once', async () => {
    // Stored Discover picks stay until the next build, so My picks must not show them to someone unticked.
    const id = 'b34ec5d1-b70f-435f-b77d-9b76979b03c7';
    const cfg = await ok(`/Plugins/${id}/Configuration`);
    const rebuilds = /MedicPicks\/Admin\/(Refresh|Rebuild)$/;
    // Answered here, so no build runs while the later checks do.
    await page.route(rebuilds, (route) => route.fulfill({ status: 204, body: '' }));
    const saveAndCatch = async () => {
        const rebuild = page.waitForRequest(rebuilds, { timeout: 10000 });
        await page.click('#MedicPicksConfigPage button[type="submit"]');
        const req = await rebuild.catch(() => null);
        await page.keyboard.press('Escape');
        return req;
    };
    try {
        await api('POST', `/Plugins/${id}/Configuration`, { ...cfg, EnableDiscover: true, DiscoverDisabledUserIds: [] });
        await page.goto(JF + '/web/index.html#/configurationpage?name=Medic%20Picks');
        await page.reload();
        const box = page.locator(`#DiscoverUsers .discoverUser[data-userid="${ctx.userId}"]`);
        await box.waitFor({ state: 'attached', timeout: 30000 });
        await box.setChecked(false, { force: true });
        const unticked = await saveAndCatch();
        expect(unticked && unticked.url().endsWith('/Admin/Rebuild') && unticked.postData().includes(ctx.userId),
            'unticking a person rebuilt: ' + (unticked ? unticked.url() + ' ' + unticked.postData() : 'nothing'));
        const me = await ok('/MedicPicks/Me');
        expect(Array.isArray(me.discover) && me.discover.length === 0, 'an unticked person still gets Discover picks: ' + JSON.stringify(me.discover).slice(0, 200));

        await page.locator('#EnableDiscover').setChecked(false, { force: true });
        const switchedOff = await saveAndCatch();
        expect(switchedOff && switchedOff.url().endsWith('/Admin/Refresh'), 'switching Discover off rebuilt: ' + (switchedOff ? switchedOff.url() : 'nothing'));

        const direct = await api('POST', '/MedicPicks/Admin/Rebuild', { UserIds: [ctx.userId] });
        expect(direct.status === 204, `rebuilding one person: ${direct.status} ${direct.text.slice(0, 200)}`);
        return 'one person rebuilt when unticked, everyone when Discover went off';
    } finally {
        await page.unroute(rebuilds);
        await api('POST', `/Plugins/${id}/Configuration`, cfg);
    }
});

await check('Medic Profiles says so when one of its lists can\'t be loaded', async () => {
    const blocked = /MedicProfiles\/Blocked/;
    await page.route(blocked, (route) => route.abort());
    try {
        await page.goto(JF + '/web/index.html#/configurationpage?name=MedicProfiles');
        await page.waitForSelector('#MedicProfilesPage .mp-tab', { timeout: 30000 });
        await page.click('#MedicProfilesPage .mp-tab[data-tab="blocked"]');
        await page.getByText('Medic Profiles couldn’t load the blocked list').waitFor({ timeout: 10000 });
    } finally {
        await page.unroute(blocked);
    }
});

await check('Medic Profiles settings: Try qBittorrent again shows after a refusal and spends one sign-in a click', async () => {
    const qbitServer = await fakeArr.startQbit(P(18080));
    Object.assign(fakeArr.qbit, { logins: 0, failedLogins: 0 });
    try {
        await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, RadarrUrl: '', QbitUrl: ARR.replace(`:${P(18989)}`, `:${P(18080)}`), QbitUser: 'admin', QbitPassword: 'wrong' });
        await page.goto(JF + '/web/index.html#/configurationpage?name=MedicProfiles');
        await page.reload();
        await page.waitForSelector('#MedicProfilesPage .mp-tab', { timeout: 30000 });
        await page.click('#MedicProfilesPage .mp-tab[data-tab="settings"]');
        const button = page.locator('#mp-qbit-retry');
        await button.waitFor({ state: 'visible', timeout: 10000 });
        expect(fakeArr.qbit.logins === 1, `opening the page signed in ${fakeArr.qbit.logins} times`);
        await button.click();
        await page.locator('#mp-qbit-state', { hasText: 'stopped' }).waitFor({ timeout: 10000 });
        expect(await button.isVisible(), 'the button went away while sign-ins are stopped');
        expect(fakeArr.qbit.logins === 2, `one click signed in ${fakeArr.qbit.logins - 1} times`);
        return 'shown, one sign-in';
    } finally {
        qbitServer.close();
    }
});

await check('Medic Profiles can\'t save its settings: the old key stays with the old address, and the page says nothing was changed', async () => {
    const status = () => ok('/MedicProfiles/Status');
    const saved = await api('POST', '/MedicProfiles/Settings', { SonarrUrl: ARR, SonarrKey: 'test-key', RadarrUrl: '' });
    expect(saved.status === 204, 'setting up: ' + saved.status);
    // A folder where Jellyfin writes Medic Profiles' settings makes that write fail, as a full disk would.
    const file = '/config/plugins/configurations/Jellyfin.Plugin.MedicProfiles.xml';
    execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', 'mv "$1" "$1.e2e-aside" && mkdir "$1"', 'sh', file]);
    try {
        const other = `http://host.docker.internal:${P(18994)}`;
        const failed = await api('POST', '/MedicProfiles/Settings', { SonarrUrl: other, SonarrKey: 'other-key', RadarrUrl: '' });
        expect(failed.status === 500 && /nothing was changed/.test(failed.json?.detail || ''), `failed save: ${failed.status} ${failed.text.slice(0, 200)}`);
        const after = await status();
        expect(after.sonarr.url === ARR && after.sonarr.keySet, 'settings after the failed save: ' + JSON.stringify(after.sonarr));
        // The pretend Sonarr only takes test-key, so this passes only with the old key back.
        const test = await api('POST', '/MedicProfiles/Test/sonarr');
        expect(test.json?.ok, 'the old key was not put back: ' + test.text);

        await page.goto(JF + '/web/index.html#/configurationpage?name=MedicProfiles');
        await page.reload();
        await page.waitForSelector('#MedicProfilesPage .mp-tab', { timeout: 30000 });
        await page.click('#MedicProfilesPage .mp-tab[data-tab="settings"]');
        await page.fill('#mp-sonarr-url', other);
        await page.fill('#mp-sonarr-key', 'other-key');
        await page.click('#mp-settings button[type="submit"]');
        await page.locator('#mp-settings-note', { hasText: 'nothing was changed' }).waitFor({ timeout: 10000 });
        return await page.locator('#mp-settings-note').textContent();
    } finally {
        execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', 'rmdir "$1" && mv "$1.e2e-aside" "$1"', 'sh', file]);
    }
});

await check('My picks page opens signed in', async () => {
    errors.length = 0;
    await page.goto(JF + '/MedicPicks/Page');
    await page.waitForLoadState('networkidle');
    await sleep(2000);
    expect(errors.length === 0, errors.join(' | '));
});

await check('schedule Preview: choosing per task updates the plan', async () => {
    errors.length = 0;
    await page.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    await page.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    await settleWhatsNew(page);
    await page.click('#JellyfinMedicPage .so-tab[data-tab="schedule"]');
    await page.click('#md-preview');
    await page.locator('#md-preview-box select').first().waitFor({ timeout: 30000 });
    const name = await page.locator('#md-preview-box tbody tr td').first().textContent();
    const rowSelect = () => page.locator('#md-preview-box tbody tr', { hasText: name }).locator('select');
    const was = await rowSelect().inputValue();
    const to = was === 'keep' ? 'off' : 'keep';
    await rowSelect().selectOption(to);
    await page.waitForFunction(([n, v]) => Array.from(document.querySelectorAll('#md-preview-box tbody tr'))
        .some((tr) => tr.cells[0].textContent === n && tr.querySelector('select').value === v && !tr.querySelector('select').disabled), [name, to], { timeout: 30000 });
    await rowSelect().selectOption(was);
    await sleep(1500);
    const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
    expect(ours.length === 0, ours.join(' | '));
});

await check('schedule lists the tasks with no time set, with what each does and a recommendation', async () => {
    const list = await ok('/JellyfinMedic/Schedule/GetUntimedTasks');
    const row = (name) => list.find((t) => t.TaskName === name);
    expect(row('Scheduled restart')?.Recommendation === 'Off', 'Scheduled restart: ' + JSON.stringify(row('Scheduled restart')));
    expect(row('Restart Jellyfin for waiting updates')?.Recommendation === 'On', 'Restart for updates: ' + JSON.stringify(row('Restart Jellyfin for waiting updates')));
    expect(list.every((t) => t.WhatItDoes && t.Why && ['On', 'Off', 'Your call'].includes(t.Recommendation)), JSON.stringify(list));
    await page.click('#JellyfinMedicPage .so-tab[data-tab="schedule"]');
    const shown = page.locator('#md-untimed-rows tr', { hasText: 'Scheduled restart' });
    await shown.waitFor({ timeout: 30000 });
    expect((await shown.textContent()).includes('Off'), 'the Scheduled restart row has no recommendation: ' + await shown.textContent());
    return list.length + ' tasks with no time set';
});

await check('exposure check result says when it only tested from this server', async () => {
    errors.length = 0;
    await page.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    await page.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    await settleWhatsNew(page);
    await page.click('#JellyfinMedicPage .so-tab[data-tab="dashboard"]');
    await sleep(2000); // the boxes fill in from Medic's suggestions first
    await page.fill('#md-expose-host', 'medic-e2e.invalid');
    await page.fill('#md-expose-port', '443');
    await page.selectOption('#md-expose-protocol', 'https');
    await page.click('#md-expose');
    await page.waitForFunction(() => !/^Checking/.test(document.getElementById('md-expose-result').textContent), null, { timeout: 30000 });
    const result = page.locator('#md-expose-result');
    const text = await result.textContent();
    expect(/when this server tried it/.test(text) && !/from the internet/.test(text), 'said: ' + text);
    expect(!/md-ok-text/.test(await result.getAttribute('class')), 'shown as all clear: ' + text);
    const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
    expect(ours.length === 0, ours.join(' | '));
});

await check('exposure check keeps the port and protocol you chose, and names the address it tested', async () => {
    // The public IP lookup asks a service on the internet, so it gets a made-up answer here.
    const lookup = /\/JellyfinMedic\/ExposureDefaults\?.*publicIp=true/i;
    await page.route(lookup, (route) => route.fulfill({ json: { PublicIp: '203.0.113.7', Port: 443, Protocol: 'https' } }));
    try {
        await medicTab('dashboard', true);
        await sleep(2000); // the boxes fill in from Medic's suggestions first
        await page.fill('#md-expose-port', '8443');
        await page.selectOption('#md-expose-protocol', 'http');
        await page.click('#md-expose-use-ip');
        await page.waitForFunction(() => document.getElementById('md-expose-host').value === '203.0.113.7', null, { timeout: 10000 });
        const boxes = () => page.evaluate(() => ['md-expose-protocol', 'md-expose-host', 'md-expose-port'].map((id) => document.getElementById(id).value).join(' '));
        let seen = await boxes();
        expect(seen === 'http 203.0.113.7 8443', '"Use my IP" changed the chosen port or protocol: ' + seen);
        // A domain that can't resolve keeps the check on this server.
        await page.fill('#md-expose-host', 'medic-e2e.invalid');
        await page.click('#JellyfinMedicPage .so-tab[data-tab="schedule"]');
        const defaults = page.waitForResponse(/\/JellyfinMedic\/ExposureDefaults/, { timeout: 30000 });
        await page.click('#JellyfinMedicPage .so-tab[data-tab="dashboard"]');
        await defaults;
        await sleep(500);
        seen = await boxes();
        expect(seen === 'http medic-e2e.invalid 8443', 'coming back to the tab changed the boxes: ' + seen);
        await page.click('#md-expose');
        await page.waitForFunction(() => !/^Checking/.test(document.getElementById('md-expose-result').textContent), null, { timeout: 30000 });
        const text = await page.locator('#md-expose-result').textContent();
        expect(text.includes('http://medic-e2e.invalid:8443'), 'the result doesn\'t name what was tested: ' + text);
        return text.slice(0, 80);
    } finally {
        await page.unroute(lookup);
    }
});

await check('Medic stops polling while its browser tab is hidden and after you leave the page', async () => {
    await medicTab('dashboard');
    // The dashboard's live tiles ask for /JellyfinMedic/Dashboard/Live every 15 seconds.
    let polls = 0;
    const count = (req) => { if (/\/JellyfinMedic\/Dashboard\/Live(\?|$)/.test(req.url())) polls++; };
    page.on('request', count);
    try {
        await page.evaluate(() => {
            Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
            document.dispatchEvent(new Event('visibilitychange'));
        });
        polls = 0;
        await sleep(17000);
        const whileHidden = polls;
        await page.evaluate(() => {
            delete document.hidden;
            document.dispatchEvent(new Event('visibilitychange'));
        });
        await sleep(2000);
        expect(whileHidden === 0, `${whileHidden} polls while the tab was hidden`);
        expect(polls > 0, 'no poll after the tab was shown again');

        await page.goto(JF + '/web/index.html#/home');
        await page.waitForURL(/#\/home/, { timeout: 30000 });
        await sleep(1000);
        polls = 0;
        await sleep(17000);
        expect(polls === 0, `${polls} polls after leaving the page`);
        return 'none while hidden or away';
    } finally {
        page.off('request', count);
    }
});

// Medic's page shows "What's new" once per person per version, a moment after it opens, and tells the server
// once it's on screen. Call this on an open Medic page: while the server says this version isn't seen yet, it
// waits for the box to open and closes it, so the box can't open later over what a check clicks. A box that
// never opens for an unseen version fails here, as that is a regression of its own.
async function settleWhatsNew(p) {
    const info = await ok('/JellyfinMedic/Version');
    if (info.SeenVersion !== info.Version) {
        const opened = await p.waitForSelector('#md-whatsnew:not([hidden])', { timeout: 15000 }).then(() => true, () => false);
        expect(opened, `"What's new" didn't open for ${info.Version}, which the server says this person hasn't seen (last seen: ${info.SeenVersion || 'none'})`);
    }
    const close = p.locator('#md-whatsnew-close:visible');
    if (await close.count()) await close.click();
}

// Opens Medic's page on a tab, closing "What's new" if it's showing. Fresh reloads it, so nothing the
// page kept from an earlier visit (the films it found, say) is reused.
async function medicTab(name, fresh) {
    await page.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    if (fresh) {
        // Coming from a page outside the web app (My picks, say), the goto is a full load, and the web app checks
        // its saved sign-in with GET /System/Info as it starts. A reload that cuts that request off makes it
        // fail, and the web app then drops the saved token for good (it clears it on any failure, not only on
        // a 401), so this page and every later check land on the sign-in screen. The page showing means the
        // check is done.
        await page.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
        await page.reload();
    }
    await page.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    await settleWhatsNew(page);
    await page.click(`#JellyfinMedicPage .so-tab[data-tab="${name}"]`);
}

// Signs in as the test admin in a new browser context, for checks that need their own clock or time zone.
async function adminPage(ctx) {
    const p = await ctx.newPage();
    await p.goto(JF + '/web/index.html#/login');
    await p.waitForSelector('#txtManualName', { timeout: 60000 });
    await p.fill('#txtManualName', 'admin');
    await p.fill('#txtManualPassword', 'admin');
    await p.click('.manualLoginForm button[type="submit"]');
    await p.waitForURL(/home/, { timeout: 60000 });
    return p;
}

const REPORT = /\/JellyfinMedic\/Report(\?|$)/;

// Waits for Medic's page to finish loading the report, and returns what it shows.
async function reportOutcome(p) {
    await p.waitForFunction(() => {
        const s = document.querySelector('#JellyfinMedicPage #so-status');
        const checked = document.querySelector('#JellyfinMedicPage #md-checked');
        if (!s) return false;
        if (!s.hidden && s.classList.contains('error')) return true;
        return s.hidden && /^Last checked/.test(checked?.textContent || '');
    }, null, { timeout: 60000 });
    return p.evaluate(() => {
        const s = document.querySelector('#JellyfinMedicPage #so-status');
        return { status: s.hidden ? '' : s.textContent, checked: document.querySelector('#JellyfinMedicPage #md-checked').textContent };
    });
}

await check('Medic report load: a dropped connection is tried once more before an error shows', async () => {
    const ctx = await browser.newContext({ locale: 'en-GB' });
    try {
        const p = await adminPage(ctx);
        let asked = 0;
        // As when the browser drops the first request, say because the page loaded twice in a moment.
        await p.route(REPORT, (route) => (asked++ === 0 ? route.abort('connectionreset') : route.continue()));
        await p.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
        const shown = await reportOutcome(p);
        expect(shown.status === '' && /^Last checked/.test(shown.checked), `after one dropped request: status "${shown.status}", "${shown.checked}"`);
        expect(asked === 2, `the report was asked for ${asked} time(s)`);
        return 'shown after asking once more';
    } finally {
        await ctx.close();
    }
});

await check('Medic report load: a server error shows the error at once, without asking again', async () => {
    const ctx = await browser.newContext({ locale: 'en-GB' });
    try {
        const p = await adminPage(ctx);
        let asked = 0;
        await p.route(REPORT, (route) => { asked++; return route.fulfill({ status: 500, body: '' }); });
        await p.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
        const shown = await reportOutcome(p);
        expect(/checks couldn't run/.test(shown.status), 'after a server error: ' + JSON.stringify(shown));
        await p.waitForTimeout(2000);
        expect(asked === 1, `the report was asked for ${asked} times after a server error`);
        return 'error shown, asked once';
    } finally {
        await ctx.close();
    }
});

await check('Medic report load: a report the page can\'t draw says so, not that the checks couldn\'t run', async () => {
    const ctx = await browser.newContext({ locale: 'en-GB' });
    try {
        const p = await adminPage(ctx);
        const logged = [];
        p.on('console', (m) => { if (m.type() === 'error') logged.push(m.text()); });
        const real = await ok('/JellyfinMedic/Report');
        // Findings that aren't a list make the dashboard throw while drawing them.
        await p.route(REPORT, (route) => route.fulfill({ json: { ...real, Findings: 'not a list' } }));
        await p.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
        const shown = await reportOutcome(p);
        expect(/couldn't show/.test(shown.status) && !/couldn't run/.test(shown.status), 'after a drawing error: ' + JSON.stringify(shown));
        expect(logged.some((t) => /Jellyfin Medic/.test(t)), 'nothing in the browser console: ' + logged.join(' | '));
        return shown.status;
    } finally {
        await ctx.close();
    }
});

await check('Medic report load: a report request the browser drops writes no error to the Jellyfin log', async () => {
    // A plugin repository that never answers keeps the report waiting on its link check.
    const http = await import('node:http');
    let reached = 0;
    const silent = http.createServer(() => { reached++; });
    await new Promise((resolve) => silent.listen(P(18981), '0.0.0.0', resolve));
    const cfg = await ok('/System/Configuration');
    const repo = { Name: 'E2E silent repository', Url: `http://host.docker.internal:${P(18981)}/manifest-${Date.now()}.json`, Enabled: true };
    await api('POST', '/System/Configuration', { ...cfg, PluginRepositories: [...(cfg.PluginRepositories || []), repo] });
    const newest = execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', 'ls /config/log/*.log | sort | tail -1']).toString().trim();
    const from = Number(execFileSync('docker', ['exec', CONTAINER, 'stat', '-c', '%s', newest]).toString().trim());
    try {
        const leave = new AbortController();
        const asked = fetch(JF + '/JellyfinMedic/Report', { headers: { Authorization: `${AUTH}, Token="${token}"` }, signal: leave.signal }).catch((e) => e);
        for (let i = 0; i < 100 && reached === 0; i++) await sleep(100);
        expect(reached > 0, 'the report never reached the silent repository');
        leave.abort();
        await asked;
        await sleep(4000);
        const added = execFileSync('docker', ['exec', CONTAINER, 'tail', '-c', `+${from + 1}`, newest]).toString();
        const ours = added.split('\n').filter((l) => /\[ERR\]/.test(l) && /\/JellyfinMedic\/Report/.test(l));
        expect(ours.length === 0, 'logged: ' + ours.join(' | ').slice(0, 400));
        return 'nothing logged';
    } finally {
        await api('POST', '/System/Configuration', cfg);
        silent.closeAllConnections();
        silent.close();
    }
});

await check('Medic dashboard: every line of tiles reaches the right edge at 1920, 1280 and 390 px', async () => {
    // Tiles on the first line: four on a wide screen, two in the middle band, one on a phone.
    const perLine = { 1920: 4, 1280: 2, 390: 1 };
    const widths = [];
    try {
        for (const width of [1920, 1280, 390]) {
            await page.setViewportSize({ width, height: 1000 });
            await medicTab('dashboard');
            await page.waitForFunction(() => !/Checking/.test(document.getElementById('md-health-big')?.textContent || 'Checking'), null, { timeout: 30000 });
            const found = await page.evaluate(() => {
                // A long name with no spaces, like a release path in a log message, must wrap inside its tile.
                const long = document.createElement('li');
                long.textContent = '/media/downloads/' + 'Some.Release.Name.2026.2160p.UHD.BluRay.Remux.DV.HDR.HEVC.TrueHD.Atmos.7.1.GROUP'.repeat(2) + '.mkv';
                document.getElementById('md-errors').appendChild(long);
                const board = document.querySelector('#JellyfinMedicPage [data-panel="dashboard"] .md-board, #JellyfinMedicPage [data-panel="dashboard"] .md-tiles');
                const right = board.getBoundingClientRect().right;
                const tiles = Array.from(board.querySelectorAll('.md-tile')).filter((t) => t.offsetParent !== null);
                const lines = new Map();
                tiles.forEach((t) => {
                    const r = t.getBoundingClientRect();
                    const top = Math.round(r.top);
                    const line = lines.get(top) || { end: 0, count: 0 };
                    lines.set(top, { end: Math.max(line.end, r.right), count: line.count + 1 });
                });
                const result = {
                    first: [...lines.entries()].sort((a, b) => a[0] - b[0])[0][1].count,
                    short: [...lines.entries()].filter(([, l]) => right - l.end > 2).map(([top, l]) => `line at ${top}px ends ${Math.round(right - l.end)}px early`),
                    overflow: tiles.filter((t) => t.scrollWidth > t.clientWidth + 1).map((t) => t.querySelector('.md-tile-head')?.textContent || '?')
                };
                long.remove();
                return result;
            });
            expect(found.short.length === 0, `${width}px: ${found.short.join('; ')}`);
            expect(found.overflow.length === 0, `${width}px: content wider than its tile in ${found.overflow.join(', ')}`);
            expect(found.first === perLine[width], `${width}px: ${found.first} tiles on the first line, expected ${perLine[width]}`);
            widths.push(width);
        }
    } finally {
        await page.setViewportSize({ width: 1280, height: 720 });
    }
    return 'full lines, ' + widths.map((w) => `${perLine[w]} to a line at ${w} px`).join(', ');
});

await check('Medic dashboard: on a phone, tiles follow the priority order', async () => {
    try {
        await page.setViewportSize({ width: 390, height: 1000 });
        await medicTab('dashboard');
        await page.waitForFunction(() => !/Checking/.test(document.getElementById('md-health-big')?.textContent || 'Checking'), null, { timeout: 30000 });
        const order = await page.evaluate(() => Array.from(document.querySelectorAll('#JellyfinMedicPage [data-panel="dashboard"] .md-tile'))
            .filter((t) => t.offsetParent !== null)
            .sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top)
            .map((t) => (t.querySelector('.md-tile-head')?.textContent || '?').trim()));
        const expected = ['Health', 'Right now', 'Top issues', 'Today: planned and actual', 'Load, last 10 minutes', 'Disks', 'This week',
            'Busy hours and tasks', 'Why it transcodes', 'Disk trend and forecast', 'Network', 'Repeated log errors', 'People and access', 'Updates', 'Maintenance'];
        expect(JSON.stringify(order) === JSON.stringify(expected), 'order: ' + order.join(', '));
        return order.length + ' tiles in priority order';
    } finally {
        await page.setViewportSize({ width: 1280, height: 720 });
    }
});

await check('Medic dashboard: Today moves with the clock in server time, and fetches the data again after 5 minutes, quietly when that fails', async () => {
    // A browser 14 hours ahead of UTC: positions must still follow the server's day.
    const ctx = await browser.newContext({ locale: 'en-GB', timezoneId: 'Pacific/Kiritimati' });
    await ctx.clock.install();
    try {
        const p = await adminPage(ctx);
        let fetches = 0;
        p.on('request', (r) => { if (/\/JellyfinMedic\/Dashboard(\?|$)/.test(r.url())) fetches++; });
        await p.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
        await p.waitForSelector('#JellyfinMedicPage #md-day .md-now-line', { timeout: 30000 });
        await settleWhatsNew(p);
        const data = await ok('/JellyfinMedic/Dashboard');
        const left = () => p.evaluate(() => parseFloat(document.querySelector('#JellyfinMedicPage #md-day .md-now-line').style.left));
        const first = await left();
        expect(Math.abs(first - data.Today.NowMinutes / 1440 * 100) < 1, `now line at ${first}%, the server's minute ${data.Today.NowMinutes} is ${(data.Today.NowMinutes / 1440 * 100).toFixed(1)}%`);
        const zone = await p.textContent('#md-day-zone');
        expect(/server/.test(zone), 'no note that the times are the server\'s: ' + zone);
        const before = fetches;
        if (1440 - data.Today.NowMinutes < 7) {
            // Too close to the server's midnight for the line to move 4 minutes: the day rolls over instead.
            await p.clock.fastForward('07:00');
            await p.waitForTimeout(1500);
            expect(fetches > before, 'the data was not fetched again when the server\'s day rolled over');
            return 'near the server\'s midnight: fetched again on the rollover';
        }
        await p.clock.fastForward('04:00');
        await p.waitForTimeout(1000);
        const moved = await left();
        expect(moved > first + 0.15, `the now line stayed at ${first}% after 4 minutes (now ${moved}%)`);
        expect(fetches === before, 'the data was fetched again before 5 minutes');
        // The next fetch fails, as during a Jellyfin restart: it must not leave an error on the page.
        await p.route('**/JellyfinMedic/Dashboard', (route) => route.abort());
        await p.clock.fastForward('02:00');
        await p.waitForTimeout(1500);
        expect(fetches > before, 'the data was not fetched again after 6 minutes');
        const banner = await p.evaluate(() => {
            const s = document.querySelector('#JellyfinMedicPage #so-status');
            return s && !s.hidden ? s.textContent : '';
        });
        expect(banner === '', 'a failed background fetch left this on the page: ' + banner);
        return `now line ${first.toFixed(2)}% to ${moved.toFixed(2)}% after 4 minutes, fetched again quietly after 6`;
    } finally {
        await ctx.close();
    }
});

await check('Medic dashboard: Details on a top issue opens Checks at that finding, whatever filter Checks was on', async () => {
    const folders = await ok('/Library/VirtualFolders');
    const movies = folders.find((f) => f.Name === 'Movies');
    const original = JSON.parse(JSON.stringify(movies.LibraryOptions));
    const setOptions = (o) => api('POST', '/Library/VirtualFolders/LibraryOptions', { Id: movies.ItemId, LibraryOptions: o });
    try {
        // Trickplay during scans is an Improve finding, so the Top issues list can't be empty.
        await setOptions({ ...original, EnableTrickplayImageExtraction: true, ExtractTrickplayImagesDuringLibraryScan: true });
        await medicTab('recs', true);
        await page.click('#JellyfinMedicPage .so-filter[data-filter="Tip"]');
        await page.click('#JellyfinMedicPage .so-tab[data-tab="dashboard"]');
        await page.waitForSelector('#md-issues li[data-key]', { timeout: 30000 });
        const key = await page.getAttribute('#md-issues li[data-key]', 'data-key');
        await page.click('#md-issues li[data-key] button:has-text("Details")');
        const landed = await page.evaluate(() => {
            const card = document.activeElement;
            return {
                checksShown: !document.querySelector('#JellyfinMedicPage [data-panel="recs"]').hidden,
                focusedKey: card && card.getAttribute('data-key'),
                marked: !!(card && card.classList.contains('is-target'))
            };
        });
        expect(landed.checksShown, 'Checks did not open');
        expect(landed.focusedKey === key, `focus is on "${landed.focusedKey}", expected "${key}"`);
        expect(landed.marked, 'the card is not marked');
        return 'landed on ' + key;
    } finally {
        await setOptions(original);
    }
});

await check('Medic dashboard: memory shows its real share, not 0%', async () => {
    const now = await ok('/JellyfinMedic/Now');
    expect(typeof now.MemoryPercent === 'number' && now.MemoryPercent > 0, 'MemoryPercent is ' + now.MemoryPercent);
    const expected = now.MemoryGb / now.MemoryLimitGb * 100;
    expect(Math.abs(now.MemoryPercent - expected) <= 1, `MemoryPercent ${now.MemoryPercent}, expected about ${expected.toFixed(1)}`);
    await medicTab('dashboard');
    await page.waitForFunction(() => /\((?!0%\))[\d.]+%\)/.test(document.getElementById('md-now')?.textContent || ''), null, { timeout: 20000 });
    return now.MemoryPercent + '%';
});

await check('Dashboard panels: with no data yet, each 3a panel says so instead of staying empty', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => !/Loading/.test(document.getElementById('md-today-summary')?.textContent || 'Loading'), null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const text = await page.evaluate(() => ['md-disks-big', 'md-today-summary', 'md-net-big', 'md-errors', 'md-load']
        .map((id) => [id, (document.getElementById(id)?.textContent || '').trim()]));
    const empty = text.filter(([, t]) => t === '' || /undefined|null|NaN/.test(t)).map(([id]) => id);
    expect(empty.length === 0, 'empty or broken: ' + empty.join(', ') + ' ' + JSON.stringify(text));
    expect(data.Network.Test === undefined || data.Network.Test === null ? /No speed test/.test(text[2][1]) : true, 'Network: ' + text[2][1]);
    return 'every 3a panel has text';
});

await check('Dashboard panels: a section the server couldn\'t build shows its message, and the other panels still render', async () => {
    const data = await ok('/JellyfinMedic/Dashboard');
    const broken = { ...data, Network: null, Errors: { Network: 'Couldn\'t be read (IOException). The Jellyfin log has the details.' } };
    await page.route('**/JellyfinMedic/Dashboard', (route) => route.fulfill({ json: broken }));
    try {
        await medicTab('dashboard', true);
        await page.waitForFunction(() => /IOException/.test(document.getElementById('md-net')?.textContent || ''), null, { timeout: 30000 });
        const summary = await page.textContent('#md-today-summary');
        expect(/planned/.test(summary), 'Today did not render beside the broken section: ' + summary);
        const rows = await page.evaluate(() => ({ dt: document.querySelectorAll('#md-net dt').length, dd: document.querySelectorAll('#md-net dd').length }));
        expect(rows.dt === rows.dd, `Network's message: ${rows.dd} values for ${rows.dt} names`);
        return 'Network shows the message; Today: ' + summary;
    } finally {
        await page.unroute('**/JellyfinMedic/Dashboard');
    }
});

await check('Dashboard panels: a panel that fails to draw says so, and the panels after it still render', async () => {
    const data = await ok('/JellyfinMedic/Dashboard');
    // A Today section of a shape the page doesn't expect makes that panel throw while drawing.
    await page.route('**/JellyfinMedic/Dashboard', (route) => route.fulfill({ json: { ...data, Today: { ...data.Today, Planned: 'not a list' } } }));
    try {
        await medicTab('dashboard', true);
        await page.waitForFunction(() => /Couldn't show this panel/.test(document.getElementById('md-today-summary')?.textContent || ''), null, { timeout: 30000 });
        const seen = await page.evaluate(() => ({
            later: ['md-net-big', 'md-busy-summary', 'md-people-big'].map((id) => document.getElementById(id).textContent),
            banner: (() => { const b = document.querySelector('#JellyfinMedicPage #so-status'); return b && !b.hidden ? b.textContent : ''; })()
        }));
        expect(seen.later.every((t) => !/Loading/.test(t)) && seen.banner === '', 'after the failed panel: ' + JSON.stringify(seen));
        return 'Today says it failed; ' + seen.later.join(' | ').slice(0, 80);
    } finally {
        await page.unroute('**/JellyfinMedic/Dashboard');
    }
});

await check('Dashboard panels: a section the server couldn\'t build hides its hints until its data comes back', async () => {
    const data = await ok('/JellyfinMedic/Dashboard');
    const failed = 'Couldn\'t be read (IOException). The Jellyfin log has the details.';
    const broken = { ...data, Today: null, BusyHours: null, Errors: { Today: failed, BusyHours: failed } };
    let served = 0;
    const ctx = await browser.newContext({ locale: 'en-GB' });
    await ctx.clock.install();
    try {
        const p = await adminPage(ctx);
        await p.route('**/JellyfinMedic/Dashboard', (route) => route.fulfill({ json: served++ === 0 ? broken : data }));
        await p.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
        await p.waitForFunction(() => /IOException/.test(document.getElementById('md-today-summary')?.textContent || ''), null, { timeout: 30000 });
        const hints = () => p.evaluate(() => ['md-day-detail', 'md-day-zone', 'md-busy-detail'].map((id) => document.getElementById(id).textContent));
        const hidden = await hints();
        expect(hidden.every((t) => t === ''), 'hints beside a failed section: ' + JSON.stringify(hidden));
        // The next fetch, after 5 minutes, brings the data back.
        await p.clock.fastForward('05:30');
        await p.waitForTimeout(2000);
        const back = await hints();
        expect(served >= 2 && /to see its details/.test(back[0]) && /to see its runs/.test(back[2]), 'after the data came back: ' + JSON.stringify(back));
        return 'hidden while failed, back with the data';
    } finally {
        await ctx.close();
    }
});

await check('Dashboard panels: with no data yet, each 3b panel says so instead of staying empty', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => !/Loading/.test(document.getElementById('md-busy-summary')?.textContent || 'Loading'), null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const text = await page.evaluate(() => ['md-busy-summary', 'md-why-summary', 'md-trend', 'md-people-big', 'md-people']
        .map((id) => [id, (document.getElementById(id)?.textContent || '').trim()]));
    const empty = text.filter(([, t]) => t === '' || /undefined|null|NaN/.test(t)).map(([id]) => id);
    expect(empty.length === 0, 'empty or broken: ' + empty.join(', ') + ' ' + JSON.stringify(text));
    if (!data.BusyHours.Ready) expect(/Still learning, \d+ hours recorded/.test(text[0][1]), 'Busy hours: ' + text[0][1]);
    if (!data.Transcodes.Samples) expect(/Nothing caught transcoding/.test(text[1][1]), 'Why it transcodes: ' + text[1][1]);
    return text.map(([, t]) => t.slice(0, 40)).join(' | ');
});

let dashFixtures = null;

await check('Dashboard panels: fixture data written, and the Dashboard built again from it', async () => {
    const files = dashboardFixtures();
    dashFixtures = Object.fromEntries(Object.keys(files).map((name) => [name, readMedicFile(name)]));
    for (const [name, value] of Object.entries(files)) writeMedicFile(name, JSON.stringify(value));
    const since = Date.now();
    const data = await dashboardAfter(since);
    expect(data.Today.Actual.some((r) => r.TaskName === 'Fixture trickplay'), 'the fixture runs are not in Today: ' + JSON.stringify(data.Today.Actual));
    return `${data.Today.Actual.length} runs today, ${data.Disks.Drives.length} drives`;
});

await check('Dashboard panels: Right now, Disks and Load show the live readings, the drive with least room and the alert lines', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => document.querySelectorAll('#md-load .md-meter').length === 4, null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const disks = await page.textContent('#md-disks-big');
    expect(/\d+% free|GB free|TB free/.test(disks), 'Disks: ' + disks);
    const charts = await page.evaluate(() => Array.from(document.querySelectorAll('#md-load .md-meter svg[role="img"]')).map((s) => s.getAttribute('aria-label')));
    expect(charts.length >= 2 && charts.every((l) => /alert at/.test(l)), 'Load charts: ' + JSON.stringify(charts));
    const ffmpeg = await page.textContent('#md-load-ffmpeg');
    expect(/FFmpeg/.test(ffmpeg), 'FFmpeg line: ' + ffmpeg);
    return `${charts.length} charts; ${disks.trim()} on ${data.Disks.Drives.length} drives`;
});

await check('Dashboard panels: Right now shows CPU, memory and who is watching, and leaves the rest to the Load panel', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => /Memory/.test(document.getElementById('md-now')?.textContent || ''), null, { timeout: 30000 });
    const seen = await page.evaluate(() => ({
        rows: Array.from(document.querySelectorAll('#md-now dt')).map((d) => d.textContent),
        chart: !!document.getElementById('md-spark'),
        cpu: document.getElementById('md-cpu').textContent
    }));
    expect(JSON.stringify(seen.rows) === JSON.stringify(['Memory', 'Watching']) && !seen.chart && /^\d+%$/.test(seen.cpu), 'Right now: ' + JSON.stringify(seen));
    return `CPU ${seen.cpu}; ${seen.rows.join(', ')}`;
});

await check('Dashboard panels: Load draws a line from the first reading after a restart', async () => {
    // The monitor's first reading comes about 30 seconds after Jellyfin starts.
    let live = await ok('/JellyfinMedic/Dashboard/Live');
    for (let i = 0; i < 12 && live.Resources.length === 0; i++) {
        await sleep(5000);
        live = await ok('/JellyfinMedic/Dashboard/Live');
    }
    expect(live.Resources.length > 0, 'the resource monitor has no readings after a minute');
    await page.route('**/JellyfinMedic/Dashboard/Live', (route) => route.fulfill({ json: { ...live, Resources: live.Resources.slice(-1) } }));
    try {
        await medicTab('dashboard', true);
        await page.waitForFunction(() => document.querySelectorAll('#md-load .md-line-value').length > 0, null, { timeout: 30000 });
        const points = await page.$$eval('#md-load .md-line-value', (lines) => lines.map((l) => l.getAttribute('points').trim().split(/\s+/).length));
        expect(points.every((n) => n === 2), 'points per line from one reading: ' + JSON.stringify(points));
        const labels = await page.$$eval('#md-load .md-meter svg[role="img"]', (svgs) => svgs.map((v) => v.getAttribute('aria-label')));
        expect(labels.every((l) => /in the last 1 minute;/.test(l)), 'chart summaries: ' + JSON.stringify(labels));
        return points.length + ' lines from one reading';
    } finally {
        await page.unroute('**/JellyfinMedic/Dashboard/Live');
    }
});

await check('Dashboard panels: Today shows the planned and actual runs, the failed one with the word, spikes and LoadGuard', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => document.querySelectorAll('#md-day .md-day-block').length > 0, null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const seen = await page.evaluate(() => ({
        planned: document.querySelectorAll('#md-day .md-day-block.md-planned').length,
        actual: document.querySelectorAll('#md-day .md-day-block.md-actual').length,
        failed: Array.from(document.querySelectorAll('#md-day .md-run-failed')).map((b) => b.textContent + '|' + b.getAttribute('aria-label')),
        spikes: document.querySelectorAll('#md-day .md-day-mark.md-spike').length,
        guard: document.querySelectorAll('#md-day .md-day-mark.md-guard').length,
        summary: document.getElementById('md-today-summary').textContent
    }));
    expect(seen.planned === data.Today.Planned.length, `planned blocks ${seen.planned}, the data has ${data.Today.Planned.length}`);
    expect(seen.actual === data.Today.Actual.length, `actual blocks ${seen.actual}, the data has ${data.Today.Actual.length}`);
    expect(seen.failed.some((f) => /^failed\|Fixture trickplay/.test(f)), 'failed run: ' + JSON.stringify(seen.failed));
    expect(seen.spikes === data.Today.Spikes.length && seen.guard === data.Today.Guard.length, `marks ${seen.spikes}/${seen.guard}, data ${data.Today.Spikes.length}/${data.Today.Guard.length}`);
    const s = data.Today.Summary;
    expect(seen.summary.includes(`${s.Planned} planned`) && seen.summary.includes(`${s.Runs} run`), 'summary: ' + seen.summary);
    const hint = await page.textContent('#md-day-detail');
    expect(/to see its details/.test(hint), 'no hint before a run is chosen: ' + hint);
    await page.focus('#md-day .md-run-failed');
    const detail = await page.textContent('#md-day-detail');
    expect(/Fixture trickplay: failed.*Fixture error/.test(detail), 'the focused block\'s details: ' + detail);
    return seen.summary;
});

await check('Dashboard panels: a task that runs all day gets one line under Today instead of a block per run', async () => {
    const data = await ok('/JellyfinMedic/Dashboard');
    const runs = Array.from({ length: 20 }, (_, i) => ({
        TaskId: 'e2e-often', TaskName: 'Fixture notifier', StartMinutes: i * 3, EndMinutes: i * 3 + 1,
        Status: i === 5 ? 'Failed' : 'Completed', ErrorMessage: i === 5 ? 'Fixture timeout' : null
    }));
    await page.route('**/JellyfinMedic/Dashboard', (route) => route.fulfill({ json: { ...data, Today: { ...data.Today, Actual: [...data.Today.Actual, ...runs] } } }));
    try {
        await medicTab('dashboard', true);
        await page.waitForFunction(() => /Fixture notifier/.test(document.getElementById('md-day-repeats')?.textContent || ''), null, { timeout: 30000 });
        const seen = await page.evaluate(() => ({
            line: document.getElementById('md-day-repeats').textContent,
            blocks: Array.from(document.querySelectorAll('#md-day .md-actual')).filter((b) => /Fixture notifier/.test(b.getAttribute('aria-label'))).length
        }));
        expect(/Fixture notifier, 20 runs \(1 failed\), last at/.test(seen.line), 'line: ' + seen.line);
        expect(seen.blocks === 1, `${seen.blocks} blocks for the notifier, expected only its failed run`);
        return seen.line;
    } finally {
        await page.unroute('**/JellyfinMedic/Dashboard');
    }
});

await check('Dashboard panels: Today draws a cancelled run as cancelled, not as a normal run', async () => {
    const data = await ok('/JellyfinMedic/Dashboard');
    const run = { TaskId: 'e2e-cancelled', TaskName: 'Fixture cancelled', StartMinutes: 120, EndMinutes: 150, Status: 'Cancelled', ErrorMessage: null };
    await page.route('**/JellyfinMedic/Dashboard', (route) => route.fulfill({ json: { ...data, Today: { ...data.Today, Actual: [...data.Today.Actual, run] } } }));
    try {
        await medicTab('dashboard', true);
        await page.waitForFunction(() => Array.from(document.querySelectorAll('#md-day .md-actual')).some((b) => /Fixture cancelled/.test(b.getAttribute('aria-label') || b.title || '')), null, { timeout: 30000 });
        const block = await page.evaluate(() => {
            const b = Array.from(document.querySelectorAll('#md-day .md-actual')).find((x) => /Fixture cancelled/.test(x.getAttribute('aria-label') || x.title || ''));
            return { stopped: b.classList.contains('md-run-stopped'), text: b.textContent };
        });
        expect(block.stopped && block.text === 'cancelled', 'the cancelled run: ' + JSON.stringify(block));
        return 'drawn as cancelled';
    } finally {
        await page.unroute('**/JellyfinMedic/Dashboard');
    }
});

await check('Dashboard panels: when the live readings stop updating, the Dashboard says so, and the note goes when they come back', async () => {
    await page.route('**/JellyfinMedic/Dashboard/Live', (route) => route.fulfill({ status: 500, body: '' }));
    let routed = true;
    try {
        await medicTab('dashboard', true);
        await page.waitForSelector('#md-stale:not([hidden])', { timeout: 30000 });
        const note = await page.textContent('#md-stale');
        expect(/live readings .*stopped updating/.test(note), 'note: ' + note);
        await page.unroute('**/JellyfinMedic/Dashboard/Live');
        routed = false;
        await page.waitForSelector('#md-stale[hidden]', { state: 'attached', timeout: 30000 });
        return note;
    } finally {
        if (routed) await page.unroute('**/JellyfinMedic/Dashboard/Live');
    }
});

await check('Medic settings: a blank FFmpeg threads box saves as automatic, and "Fixed it" answers with a body', async () => {
    const before = await ok('/JellyfinMedic/MedicSettings');
    try {
        await medicTab('tracks', true);
        await page.waitForSelector('#md-tracks-save:not([disabled])', { timeout: 30000 });
        await page.fill('#md-tracks-threads', '');
        await page.click('#md-tracks-save');
        await page.waitForFunction(() => /Saved/.test(document.getElementById('md-tracks-saved')?.textContent || ''), null, { timeout: 30000 });
        const saved = await ok('/JellyfinMedic/MedicSettings');
        expect(saved.TracksFfmpegThreads === 0, 'saved threads: ' + saved.TracksFfmpegThreads);
        const shown = await page.inputValue('#md-tracks-threads');
        expect(shown === '0', 'the box shows ' + JSON.stringify(shown));
        const reset = await api('POST', '/JellyfinMedic/Logs/ResetCount');
        expect(reset.status === 200 && reset.json?.Reset === true, `reset: ${reset.status} ${reset.text}`);
        return 'saved as 0, shown back; reset answered ' + reset.text;
    } finally {
        await api('POST', '/JellyfinMedic/MedicSettings', before);
    }
});

await check('Dashboard panels: the 15-second refresh leaves alone what has not changed, the keyboard\'s place on Today and the paused list', async () => {
    const live = await ok('/JellyfinMedic/Dashboard/Live');
    const paused = [{ TaskId: 'e2e-fixture-trickplay', TaskName: 'Fixture trickplay', StoppedUtc: new Date().toISOString() }];
    await page.route('**/JellyfinMedic/Dashboard/Live', (route) => route.fulfill({ json: { ...live, Paused: paused } }));
    let ticks = 0;
    const count = (req) => { if (/\/JellyfinMedic\/Dashboard\/Live(\?|$)/.test(req.url())) ticks++; };
    try {
        await medicTab('dashboard', true);
        await page.waitForFunction(() => document.querySelector('#md-day .md-run-failed') && document.querySelector('#md-paused li'), null, { timeout: 30000 });
        await page.$eval('#md-paused ul', (ul) => { ul.dataset.seen = 'before'; });
        await page.focus('#md-day .md-run-failed');
        page.on('request', count);
        await sleep(16000);
        const after = await page.evaluate(() => ({
            focus: document.activeElement?.classList.contains('md-run-failed') || false,
            list: !!document.querySelector('#md-paused ul[data-seen="before"]')
        }));
        expect(ticks > 0, 'no refresh in 16 seconds');
        expect(after.focus && after.list, `after a refresh: ${after.focus ? 'the keyboard is still on the failed run' : 'the keyboard left the failed run'}, ${after.list ? 'the paused list is the same' : 'the paused list was written again'}`);
        return `${ticks} refresh(es); focus and list kept`;
    } finally {
        page.off('request', count);
        await page.unroute('**/JellyfinMedic/Dashboard/Live');
    }
});

await check('Dashboard panels: the 5-minute refresh keeps the keyboard on the run or the hour it was on', async () => {
    const ctx = await browser.newContext({ locale: 'en-GB' });
    await ctx.clock.install();
    try {
        const p = await adminPage(ctx);
        let fetches = 0;
        p.on('request', (r) => { if (/\/JellyfinMedic\/Dashboard(\?|$)/.test(r.url())) fetches++; });
        await p.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
        await p.waitForSelector('#JellyfinMedicPage #md-day .md-run-failed', { timeout: 30000 });
        await settleWhatsNew(p);
        const refresh = async () => {
            const before = fetches;
            await p.clock.fastForward('05:30');
            await p.waitForTimeout(2000);
            expect(fetches > before, 'the data was not fetched again after 5 minutes');
        };
        await p.focus('#md-day .md-run-failed');
        await refresh();
        const today = await p.evaluate(() => ({
            kept: document.activeElement?.classList.contains('md-run-failed') || false,
            detail: document.getElementById('md-day-detail').textContent
        }));
        expect(today.kept && /Fixture trickplay/.test(today.detail), 'Today after the refresh: ' + JSON.stringify(today));
        await p.focus('#md-busy .md-busy-clash[aria-label*="Fixture scan"]');
        await refresh();
        const busy = await p.evaluate(() => document.activeElement?.classList.contains('md-busy-clash') || false);
        expect(busy, 'the keyboard left the busy hour after the refresh');
        return 'kept on the failed run and on the busy hour';
    } finally {
        await ctx.close();
    }
});

await check('Dashboard panels: at the server\'s midnight Today fetches the new day and starts over', async () => {
    const data = await ok('/JellyfinMedic/Dashboard');
    const nextDay = new Date(Date.parse(data.Today.Date + 'T00:00:00Z') + 86400000).toISOString().slice(0, 10);
    const none = { Planned: 0, Runs: 0, Running: 0, Completed: 0, Failed: 0, Stopped: 0, Cancelled: 0, NotRun: 0, Late: 0 };
    const late = { ...data, Today: { ...data.Today, NowMinutes: 1438 } };
    const next = { ...data, Today: { ...data.Today, Date: nextDay, NowMinutes: 1, Planned: [], Actual: [], Spikes: [], Guard: [], Summary: none } };
    let served = 0;
    const ctx = await browser.newContext({ locale: 'en-GB' });
    await ctx.clock.install();
    try {
        const p = await adminPage(ctx);
        await p.route('**/JellyfinMedic/Dashboard', (route) => route.fulfill({ json: served++ === 0 ? late : next }));
        await p.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
        await p.waitForSelector('#JellyfinMedicPage #md-day .md-now-line', { timeout: 30000 });
        await settleWhatsNew(p);
        expect(served === 1, `the data was fetched ${served} times on opening`);
        // Three minutes is under the 5-minute refresh: only the end of the server's day can make the page fetch again.
        await p.clock.fastForward('03:00');
        await p.waitForTimeout(2000);
        const after = await p.evaluate(() => ({
            summary: document.getElementById('md-today-summary').textContent,
            blocks: document.querySelectorAll('#md-day .md-day-block').length
        }));
        expect(served >= 2, 'the data was not fetched again at the server\'s midnight');
        expect(/^0 planned, 0 runs/.test(after.summary) && after.blocks === 0, 'Today did not start over: ' + JSON.stringify(after));
        return `fetched ${served} times; ${after.summary}`;
    } finally {
        await ctx.close();
    }
});

await check('Dashboard panels: Today names its lanes beside the timeline, where they hide no run or mark', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => document.querySelectorAll('#md-day .md-day-block').length > 0, null, { timeout: 30000 });
    const seen = await page.evaluate(() => ({
        inside: document.querySelectorAll('#md-day .md-day-lane').length,
        beside: Array.from(document.querySelectorAll('.md-day-lanes span')).map((l) => l.textContent)
    }));
    expect(seen.inside === 0 && JSON.stringify(seen.beside) === JSON.stringify(['Planned', 'Ran']), 'lane names: ' + JSON.stringify(seen));
    return seen.beside.join(', ');
});

await check('Dashboard panels: a failed run late in the evening stays inside Today on a phone', async () => {
    const data = await ok('/JellyfinMedic/Dashboard');
    const late = { TaskId: 'e2e-late', TaskName: 'Fixture late task', StartMinutes: 1430, EndMinutes: 1432, Status: 'Failed', ErrorMessage: 'Fixture error' };
    await page.route('**/JellyfinMedic/Dashboard', (route) => route.fulfill({ json: { ...data, Today: { ...data.Today, Actual: [...data.Today.Actual, late] } } }));
    try {
        await page.setViewportSize({ width: 390, height: 844 });
        await medicTab('dashboard', true);
        await page.waitForFunction(() => Array.from(document.querySelectorAll('#md-day .md-run-failed')).some((b) => /Fixture late task/.test(b.getAttribute('aria-label'))), null, { timeout: 30000 });
        const edges = await page.evaluate(() => {
            const day = document.getElementById('md-day').getBoundingClientRect();
            const block = Array.from(document.querySelectorAll('#md-day .md-run-failed')).find((b) => /Fixture late task/.test(b.getAttribute('aria-label'))).getBoundingClientRect();
            return { day: Math.round(day.right), block: Math.round(block.right), width: Math.round(block.width) };
        });
        expect(edges.block <= edges.day, `the late block ends at ${edges.block} px, the timeline at ${edges.day} px`);
        return `${edges.width} px wide, ends at ${edges.block} of ${edges.day}`;
    } finally {
        await page.unroute('**/JellyfinMedic/Dashboard');
        await page.setViewportSize({ width: 1280, height: 720 });
    }
});

await check('Dashboard panels: Network shows the speed test and the suggestion, Repeated log errors the scanner\'s list', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => /Mbps up/.test(document.getElementById('md-net-big')?.textContent || ''), null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const net = await page.textContent('#md-net');
    expect(/Suggested limit/.test(net) && net.includes(`for ${data.Network.Suggested.Viewers} viewer`), 'Network: ' + net);
    const errors = await page.$$eval('#md-errors li', (lis) => lis.map((li) => li.textContent));
    expect(data.RepeatedErrors.length === 0 ? /No repeated errors/.test(errors[0] || '') : errors.length === data.RepeatedErrors.length, 'errors: ' + JSON.stringify(errors));
    return `${(await page.textContent('#md-net-big')).trim()}; ${errors.length} log line(s)`;
});

await check('Dashboard panels: Busy hours shows 7 by 24 hours, outlines the runs in busy hours and lets the keyboard reach them', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => document.querySelectorAll('#md-busy .md-busy-cell').length === 168, null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const hours = data.BusyHours.Days.flat();
    const clashes = hours.filter((h) => h.Busy && h.Runs.length > 0).length;
    const withRuns = hours.filter((h) => h.Runs.length > 0).length;
    const seen = await page.evaluate(() => ({
        clash: document.querySelectorAll('#md-busy .md-busy-clash').length,
        focusable: document.querySelectorAll('#md-busy button.md-busy-cell').length,
        summary: document.getElementById('md-busy-summary').textContent
    }));
    expect(clashes > 0 && seen.clash === clashes, `${seen.clash} outlined hours, the data has ${clashes} busy hours with runs`);
    expect(seen.focusable === withRuns, `${seen.focusable} focusable hours, the data has ${withRuns} hours with runs`);
    expect(seen.summary.includes(`${data.BusyHours.RunsInBusyHours} run`) && /Most watched: \w{3} \d/.test(seen.summary), 'summary: ' + seen.summary);
    if (data.UtcOffsetMinutes !== -new Date().getTimezoneOffset()) expect(/server's/.test(seen.summary), 'the hours are the server\'s, yet the summary does not say so: ' + seen.summary);
    const hint = await page.textContent('#md-busy-detail');
    expect(/to see its runs/.test(hint), 'no hint before an hour is chosen: ' + hint);
    await page.focus('#md-busy .md-busy-clash[aria-label*="Fixture scan"]');
    const detail = await page.textContent('#md-busy-detail');
    expect(/Fixture scan/.test(detail) && /busy/.test(detail), 'the focused hour\'s details: ' + detail);
    return seen.summary;
});

await check('Dashboard panels: Why it transcodes shows each reason\'s share and its titles, and asks for the media report while there is none', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => document.querySelectorAll('#md-why li').length > 0, null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const seen = await page.evaluate(() => ({
        reasons: Array.from(document.querySelectorAll('#md-why li')).map((li) => li.textContent),
        titles: document.querySelectorAll('#md-why-titles li').length,
        summary: document.getElementById('md-why-summary').textContent
    }));
    expect(seen.reasons.length === data.Transcodes.Reasons.length, `${seen.reasons.length} reasons, the data has ${data.Transcodes.Reasons.length}`);
    expect(/Audio format not supported/.test(seen.reasons[0]) && /50%/.test(seen.reasons[0]), 'first reason: ' + seen.reasons[0]);
    expect(seen.titles === data.Transcodes.Top.length, `${seen.titles} titles, the data has ${data.Transcodes.Top.length}`);
    expect(seen.summary.includes(`Caught transcoding ${data.Transcodes.Samples} times`), 'summary: ' + seen.summary);
    if (!data.Transcodes.MediaReportUtc) expect(/Build the media report/.test(seen.summary), 'no media report, yet the summary says: ' + seen.summary);
    return seen.summary;
});

await check('Dashboard panels: Disk trend draws each drive\'s days with the projection to full', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => document.querySelectorAll('#md-trend .md-trend-drive').length > 0, null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const seen = await page.evaluate(() => Array.from(document.querySelectorAll('#md-trend .md-trend-drive')).map((d) => ({
        text: d.textContent,
        projection: d.querySelectorAll('.md-line-projection').length,
        label: d.querySelector('svg[role="img"]')?.getAttribute('aria-label') || ''
    })));
    expect(seen.length === data.Disks.Drives.length, `${seen.length} drives drawn, the data has ${data.Disks.Drives.length}`);
    // The fixture drive: 400 GB free ten days ago, 220 GB today, so 20 GB a day and full in 11 days.
    const fixture = seen.find((d) => /Fixture films/.test(d.text));
    expect(fixture && fixture.projection === 1 && /220 GB free, using 20 GB a day, full in about 11 days/.test(fixture.text) && /full in/.test(fixture.label),
        'the filling fixture drive: ' + JSON.stringify(fixture));
    expect(seen.every((d) => d.label || /after 3 days/.test(d.text)), 'a drive with neither a chart summary nor the 3 days note: ' + JSON.stringify(seen));
    return seen.length + ' drives';
});

await check('Dashboard panels: Disk trend marks the days Medic freed space', async () => {
    const data = await ok('/JellyfinMedic/Dashboard');
    const drive = data.Disks.Drives.find((d) => d.Holds === 'Fixture films');
    expect(drive, 'the fixture drive is not in the data');
    // Nothing has freed space on the e2e server: serve its data with one freed day, three days ago.
    const day = drive.Series[drive.Series.length - 4].Day;
    await page.route('**/JellyfinMedic/Dashboard', (route) => route.fulfill({ json: { ...data, Disks: { ...data.Disks, Freed: [{ Day: day, Bytes: 5368709120, Kinds: ['Transcodes'] }] } } }));
    try {
        await medicTab('dashboard', true);
        await page.waitForFunction(() => document.querySelectorAll('#md-trend .md-line-freed').length > 0, null, { timeout: 30000 });
        const marked = await page.evaluate(() => Array.from(document.querySelectorAll('#md-trend .md-trend-drive'))
            .filter((d) => /Fixture films/.test(d.textContent))
            .map((d) => ({ lines: d.querySelectorAll('.md-line-freed').length, label: d.querySelector('svg[role="img"]').getAttribute('aria-label') }))[0]);
        expect(marked && marked.lines === 1 && /freed space on 1 day/.test(marked.label), 'the freed day on the fixture drive: ' + JSON.stringify(marked));
        return 'marked ' + day;
    } finally {
        await page.unroute('**/JellyfinMedic/Dashboard');
    }
});

await check('Dashboard panels: People and access counts the accounts and lists the administrators', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => /account/.test(document.getElementById('md-people-big')?.textContent || ''), null, { timeout: 30000 });
    const data = await ok('/JellyfinMedic/Dashboard');
    const big = (await page.textContent('#md-people-big')).trim();
    expect(big === `${data.People.Total} account${data.People.Total === 1 ? '' : 's'}`, 'People: ' + big);
    const text = await page.textContent('#md-people');
    expect(text.includes(`Administrators: ${data.People.AdminNames.length}`), 'administrators: ' + text);
    return big;
});

// Once Medic has read a media report it keeps it in memory for the rest of the run, so this comes after every check without one.
await check('Dashboard panels: a media report and an unreadable disk history written, and the Dashboard built again', async () => {
    expect(dashFixtures, 'the fixture check did not run');
    for (const name of ['media_report.json', 'disk_history.json.bad']) {
        if (!(name in dashFixtures)) dashFixtures[name] = readMedicFile(name);
    }
    writeMedicFile('media_report.json', JSON.stringify({ GeneratedUtc: new Date().toISOString(), Files: 16, Bytes: 1, HardwareAcceleration: 'none', Compatibility: { DtsOrTrueHdOnly: 4, ImageSubtitles: 2, OldContainers: 0, Hdr: 0 } }));
    // Medic sets a file it can't read aside as .bad and starts a new one.
    writeMedicFile('disk_history.json', '{ not json');
    const since = Date.now();
    const data = await dashboardAfter(since);
    expect(data.Transcodes.MediaReportUtc, 'the data has no media report date');
    return `${data.Disks.Drives.length} drives after the unreadable history`;
});

await check('Dashboard panels: an unreadable disk history empties only the Disk panels', async () => {
    // The data built just before. Opening the tab runs the checks, which record today's disk reading again.
    const data = await ok('/JellyfinMedic/Dashboard');
    expect(data.Disks && data.Disks.Drives.length === 0, 'the data still has drives: ' + JSON.stringify(data.Disks));
    await medicTab('dashboard', true);
    await page.waitForFunction(() => /first daily reading/.test(document.getElementById('md-trend')?.textContent || ''), null, { timeout: 30000 });
    const seen = await page.evaluate(() => ({
        disks: document.getElementById('md-disks-big').textContent,
        today: document.getElementById('md-today-summary').textContent,
        net: document.getElementById('md-net-big').textContent,
        why: document.querySelectorAll('#md-why li').length
    }));
    expect(seen.disks === 'No readings yet', 'Disks: ' + seen.disks);
    expect(/planned/.test(seen.today) && /Mbps up/.test(seen.net) && seen.why > 0, 'the other panels: ' + JSON.stringify(seen));
    return 'Disks: ' + seen.disks + '; Today: ' + seen.today;
});

await check('Dashboard panels: with a media report, each reason names what in the library causes it and how many files have it', async () => {
    await medicTab('dashboard', true);
    await page.waitForFunction(() => /in the library/.test(document.getElementById('md-why')?.textContent || ''), null, { timeout: 30000 });
    const text = await page.textContent('#md-why');
    expect(/Only DTS or TrueHD audio: 4 files in the library/.test(text) && /Picture subtitles \(PGS, VobSub\): 2 files in the library/.test(text), 'causes: ' + text);
    const summary = await page.textContent('#md-why-summary');
    expect(/media report of/.test(summary), 'the summary does not date the report: ' + summary);
    return 'audio: 4 files, picture subtitles: 2 files';
});

await check('Dashboard panels: the data files that were there before are back', async () => {
    expect(dashFixtures, 'the fixture check did not run');
    for (const [name, before] of Object.entries(dashFixtures)) {
        if (before === null) removeMedicFile(name);
        else writeMedicFile(name, before);
    }
    const differ = Object.entries(dashFixtures).filter(([name, before]) => readMedicFile(name) !== before).map(([name]) => name);
    expect(differ.length === 0, 'not put back: ' + differ.join(', '));
    return Object.keys(dashFixtures).length + ' files put back';
});

await check('Medic page: Off/Suggest and the time range are separate buttons, and the arrow keys reach every tab', async () => {
    await medicTab('settings');
    const pressed = () => page.evaluate(() => Array.from(document.querySelectorAll('#JellyfinMedicPage .md-seg button[aria-pressed="true"]'))
        .map((b) => b.dataset.range || b.dataset.mode).sort().join(','));
    await page.click('#JellyfinMedicPage .md-seg button[data-mode="off"]');
    expect(await pressed() === 'full,off', 'after Off: ' + await pressed());
    await page.click('#JellyfinMedicPage .so-tab[data-tab="schedule"]');
    await page.click('#JellyfinMedicPage .md-seg button[data-range="night"]');
    expect(await pressed() === 'night,off', 'after Night: ' + await pressed());
    await page.click('#JellyfinMedicPage .md-seg button[data-range="full"]');

    await page.click('#JellyfinMedicPage .so-tab[data-tab="tracks"]');
    await page.keyboard.press('ArrowRight');
    const now = await page.evaluate(() => document.querySelector('#JellyfinMedicPage .so-tab[aria-selected="true"]').dataset.tab);
    expect(now === 'media', 'ArrowRight from Tracks went to ' + now);
    const linked = await page.evaluate(() => {
        const tab = document.querySelector('#JellyfinMedicPage .so-tab[data-tab="media"]');
        const panel = document.getElementById(tab.getAttribute('aria-controls') || '-');
        return !!panel && panel.dataset.panel === 'media' && panel.getAttribute('aria-labelledby') === tab.id;
    });
    expect(linked, 'the Media report tab and its panel don\'t point at each other');
    return 'Off kept the range; Night kept Off; Tracks then Media report';
});

await check('Tracks settings: a keep language Medic doesn\'t know is still saved, and a note names it', async () => {
    const s = await ok('/JellyfinMedic/MedicSettings');
    const note = page.locator('#md-tracks-langs-note');
    // Saving is switched on once the settings have loaded, so they can't overwrite what's typed after that.
    const loaded = () => page.waitForSelector('#md-tracks-save:not([disabled])', { timeout: 30000 });
    errors.length = 0;
    try {
        await medicTab('tracks', true);
        await loaded();
        await page.fill('#md-tracks-langs-in', 'eng, xx');
        await page.click('#md-tracks-save');
        await note.filter({ hasText: 'xx' }).waitFor({ timeout: 15000 });
        expect(!/\beng\b/.test(await note.textContent()), 'the note names a code Medic knows: ' + await note.textContent());

        await medicTab('tracks', true);
        await loaded();
        const typed = await page.inputValue('#md-tracks-langs-in');
        expect(typed === 'eng, xx', 'after a reload the box holds ' + typed);
        await note.filter({ hasText: 'xx' }).waitFor({ timeout: 15000 });
        const shown = await note.textContent();
        const saved = await ok('/JellyfinMedic/MedicSettings');
        expect(saved.TracksKeepLanguages === 'eng, xx' && (saved.TracksUnknownLanguages || []).join() === 'xx',
            `saved ${saved.TracksKeepLanguages}, unknown ${JSON.stringify(saved.TracksUnknownLanguages)}`);
        // The list is worked out by the server: settings posted back as read still save, and a list in them is ignored.
        const again = await api('POST', '/JellyfinMedic/MedicSettings', { ...saved, TracksUnknownLanguages: ['yy'] });
        expect(again.status === 200 && (again.json?.TracksUnknownLanguages || []).join() === 'xx', `posting the settings back: ${again.status} ${again.text.slice(0, 200)}`);

        await page.fill('#md-tracks-langs-in', 'eng');
        await page.click('#md-tracks-save');
        await note.waitFor({ state: 'hidden', timeout: 15000 });
        const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
        expect(ours.length === 0, ours.join(' | '));
        return shown;
    } finally {
        await api('POST', '/JellyfinMedic/MedicSettings', s);
    }
});

await check('"What\'s new" takes the focus, closes with Escape and gives the focus back', async () => {
    await medicTab('settings');
    await page.click('#md-changelog-all');
    await page.waitForSelector('#md-whatsnew:not([hidden])', { timeout: 15000 });
    const inside = await page.evaluate(() => document.activeElement?.id);
    expect(inside === 'md-whatsnew-close', 'focus is on ' + inside);
    await page.keyboard.press('Escape');
    expect(await page.locator('#md-whatsnew').isHidden(), 'Escape left it open');
    const back = await page.evaluate(() => document.activeElement?.id);
    expect(back === 'md-changelog-all', 'focus went to ' + back);
});

await check('playback test sends its token in a header, not in the address', async () => {
    // The samples have no 1080p film, so the page is shown the 720p one when it asks for 1080p.
    const longFilm = (await ok(`/Items?Recursive=true&IncludeItemTypes=Movie&searchTerm=Long&Fields=MediaSources,Path&UserId=${ctx.userId}`)).Items[0];
    expect(longFilm, 'Long Film not scanned');
    await page.route(/\/Items\?.*MinWidth=1800/, (route) => route.fulfill({ json: { Items: [longFilm], TotalRecordCount: 1 } }));
    const videoUrls = [];
    const watch = (req) => { if (/\/Videos\//.test(req.url())) videoUrls.push(req.url()); };
    const accept = (dialog) => dialog.accept();
    page.on('request', watch);
    page.on('dialog', accept);
    try {
        await medicTab('tests', true);
        await page.click('#so-run-playback');
        await page.waitForSelector('#so-playback-results .so-test-result', { timeout: 180000 });
        const card = await page.locator('#so-playback-results .so-test-result').first().textContent();
        expect(!/Transcoding failed/.test(card), 'the test failed: ' + card.slice(0, 300));
        expect(videoUrls.length >= 3, `${videoUrls.length} video requests`);
        const withKey = videoUrls.filter((u) => /[?&](api_key|ApiKey)=/i.test(u));
        expect(withKey.length === 0, 'token in the address: ' + withKey[0]);
        return `${videoUrls.length} requests, none with the token in the address`;
    } finally {
        page.off('request', watch);
        page.off('dialog', accept);
        await page.unroute(/\/Items\?.*MinWidth=1800/);
    }
});

await check('"Remove duplicate files" says which titles it left and shows the server\'s own error', async () => {
    const tidy = /\/JellyfinMedic\/Tracks\/TidyCopies/;
    const accept = (dialog) => dialog.accept();
    page.on('dialog', accept);
    try {
        await medicTab('tracks', true);
        const result = page.locator('#md-tracks-tidy-result');
        const press = async (reply) => {
            await page.unroute(tidy);
            await page.route(tidy, reply);
            await page.click('#md-tracks-tidy');
            await page.waitForFunction(() => !document.querySelector('#md-tracks-tidy').disabled, null, { timeout: 10000 });
            return (await result.textContent()).trim();
        };
        // A name that looks like markup must show as text.
        const leftTitles = [{ Name: 'Film <b>(2020)</b>.mkv', Reason: 'it has no audio' }, { Name: 'Other (2021).mkv', Reason: 'FFprobe couldn\'t read it' }];
        const left = await press((route) => route.fulfill({ json: { Titles: 1, CopiesDeleted: 0, Freed: 0, FreedText: '0 KB', LeftTitles: leftTitles } }));
        expect(left.includes('1 title(s) back to one file.')
            && left.includes('2 title(s) left as they are, as their copy didn\'t check out: Film <b>(2020)</b>.mkv (it has no audio); Other (2021).mkv (FFprobe couldn\'t read it).')
            && (await result.locator('b').count()) === 0, 'shown: ' + left);
        const problem = 'FFprobe couldn\'t be found, so the earlier copies can\'t be checked and nothing was tidied.';
        const missing = await press((route) => route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'An error occurred', status: 500, detail: problem }) }));
        expect(missing === problem, 'shown: ' + missing);
        const busy = await press((route) => route.fulfill({ status: 409, contentType: 'text/plain', body: 'Wait for the track run to finish first.' }));
        expect(busy === 'Wait for the track run to finish first.', 'shown: ' + busy);
        return 'left titles with their reason, then the Problem and the 409 messages';
    } finally {
        page.off('dialog', accept);
        await page.unroute(tidy);
    }
});

await check('admin banner on the home page for a serious error, and it can be dismissed', async () => {
    // Pretend the database reported damage, by adding the line to the newest log file.
    // Written from inside the container: the log belongs to Jellyfin's user, not the one running the tests.
    const newest = execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', 'ls /config/log/*.log | sort | tail -1']).toString().trim();
    const now = new Date().toISOString().replace('T', ' ').slice(0, 19);
    // "What's new" was seen on Medic's page above, so its home banner is gone too.
    expect(!((await api('GET', '/JellyfinMedic/Alerts')).json || []).some((a) => a.Kind === 'whatsnew'), '"What\'s new" banner still showing after seeing it in Medic');
    // A warning with words that would raise the full-disk banner must not count: only errors do. Both lines
    // go in at once, so the result that shows the database error was read after the warning too.
    const warning = `[${now}.000 +00:00] [WRN] [28] MediaBrowser.Providers.Manager.ProviderManager: Could not cache an image, it will be fetched again later: No space left on device`;
    const line = `[${now}.000 +00:00] [ERR] [42] Microsoft.EntityFrameworkCore: SQLite Error 11: 'database disk image is malformed'.`;
    execFileSync('docker', ['exec', CONTAINER, 'sh', '-c', `printf '%s\\n%s\\n' "$1" "$2" >> "$3"`, 'sh', warning, line, newest]);
    let alerts = [];
    for (let i = 0; i < 70 && !alerts.some((a) => a.Kind === 'critical'); i++) {
        alerts = (await api('GET', '/JellyfinMedic/Alerts')).json || [];
        if (!alerts.some((a) => a.Kind === 'critical')) await sleep(2000); // cached for 2 minutes
    }
    expect(alerts.some((a) => a.Id.startsWith('critical:database')), 'no database alert: ' + JSON.stringify(alerts));
    expect(!alerts.some((a) => a.Id.startsWith('critical:disk')), 'a warning raised a banner: ' + JSON.stringify(alerts));
    const inChecks = ((await ok('/JellyfinMedic/Report')).Findings || []).some((f) => f.Area === 'Logs' && /database looks damaged/.test(f.Title));
    expect(inChecks, 'the banner\'s problem is missing from Checks');

    errors.length = 0;
    await page.goto(JF + '/web/index.html#/home');
    await page.reload(); // the banner checks at most every 5 minutes within one page load
    const banner = page.locator('#jellyfin-medic-banners');
    await banner.waitFor({ timeout: 30000 });
    expect(/database looks damaged/i.test(await banner.textContent()), 'wrong banner text');
    await banner.locator('button[aria-label="Dismiss"]').first().click();
    await page.reload();
    await sleep(5000);
    expect(!(await banner.isVisible().catch(() => false)), 'banner came back after dismissing');
    const ours = errors.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
    expect(ours.length === 0, ours.join(' | '));
});

await check('admin banner goes away on signing out, and an answer that arrives late isn\'t shown to the next person', async () => {
    // Served by the test: the admin dismissed the real alert above, and the server remembers that on every device.
    const alert = { Id: 'critical:e2e-sign-out', Kind: 'critical', Title: 'Test alert for admins', Detail: 'Served by the e2e test.' };
    const name = 'e2e-viewer';
    const made = await api('POST', '/Users/New', { Name: name, Password: 'e2e-pass' });
    expect(made.status === 200, 'user not created: ' + made.text);
    const ctx = await browser.newContext({ locale: 'en-GB' });
    try {
        const p = await ctx.newPage();
        let held = null; // a list while answers are kept back, so they arrive late
        await p.route('**/JellyfinMedic/Alerts', async (route) => {
            if (held) held.push(route);
            else await route.fulfill({ json: [alert] });
        });
        // Only the visible form: the web app can keep an earlier sign-in page, hidden, after signing out.
        const signIn = async (user, password) => {
            await p.waitForSelector('#txtManualName:visible', { timeout: 60000 });
            await p.fill('#txtManualName:visible', user);
            await p.fill('#txtManualPassword:visible', password);
            await p.click('.manualLoginForm button[type="submit"]:visible');
            await p.waitForURL(/#\/home/, { timeout: 60000 });
        };
        const signOut = async () => {
            await p.click('button[aria-controls="app-user-menu"]');
            await p.locator('#app-user-menu [role="menuitem"]', { hasText: /sign out/i }).click();
            await p.waitForSelector('#txtManualName:visible', { timeout: 60000 });
        };
        const box = p.locator('#jellyfin-medic-banners');
        const shown = () => p.evaluate(() => document.getElementById('jellyfin-medic-banners')?.textContent || '');
        const problems = [];

        await p.goto(JF + '/web/index.html#/login');
        await signIn('admin', 'admin');
        expect(await box.waitFor({ timeout: 30000 }).then(() => true, () => false), 'no banner for the admin after signing in, on ' + p.url());
        await signOut();
        await box.waitFor({ state: 'detached', timeout: 5000 }).catch(() => { /* reported below */ });
        const stayed = await shown();
        if (stayed) problems.push('the banner stayed on the sign-in screen after signing out: ' + stayed);

        // A fresh page, so what follows doesn't depend on the sign-out above being handled.
        await p.reload();
        held = [];
        const asked = p.waitForRequest('**/JellyfinMedic/Alerts', { timeout: 30000 }).then(() => true, () => false);
        await signIn('admin', 'admin');
        expect(await asked, 'the banner didn\'t ask for alerts after the admin signed in again');
        await signOut();
        await signIn(name, 'e2e-pass');
        // Released only once the banner has seen who is signed in now, half a second after the page shows.
        await p.waitForTimeout(3000);
        for (const route of held) await route.fulfill({ json: [alert] });
        await box.waitFor({ timeout: 3000 }).catch(() => { /* not shown, as it should be */ });
        const late = await shown();
        if (late) problems.push(`the admin's answer, held back until ${name} (not an admin) had signed in, was shown to them: ` + late);
        expect(problems.length === 0, problems.join('; '));
        return 'removed on signing out; the late answer was dropped';
    } finally {
        await ctx.close();
        await api('DELETE', `/Users/${made.json.Id}`);
    }
});

await check('Picks: 30 + a genre explains a short list, and "Show me different ones" moves on', async () => {
    await page.goto(JF + '/MedicPicks/Page');
    await page.waitForSelector('#prefs:not([hidden])', { timeout: 30000 });
    const names = () => page.evaluate(() => Array.from(document.querySelectorAll('#library li')).map((li) => li.querySelector('h3, strong, .title')?.textContent || li.textContent).join('|'));
    await page.selectOption('#count', '30');
    await page.click('#genres-summary');
    await page.locator('#genre-list input').first().check();
    await page.click('#apply');
    await page.waitForFunction(() => /Updated just now/.test(document.getElementById('prefs-note').textContent), null, { timeout: 60000 });
    expect(!(await page.locator('#apply').isDisabled()), 'Update button still disabled');
    expect(await page.locator('#few-note').isVisible(), 'no note explaining the short list');

    // Back to any genre, 5 at a time, then cycle.
    await page.click('#genres-clear');
    await page.selectOption('#count', '5');
    await page.click('#apply');
    await page.waitForFunction(() => /Updated just now/.test(document.getElementById('prefs-note').textContent), null, { timeout: 60000 });
    const first = await names();
    await page.evaluate(() => { document.getElementById('prefs-note').textContent = ''; });
    await page.click('#more');
    await page.waitForFunction(() => /Updated just now/.test(document.getElementById('prefs-note').textContent), null, { timeout: 60000 });
    const second = await names();
    expect(first && second && first !== second, `same picks after "Show me different ones": ${first}`);
    return 'picks changed';
});

await check('one-time on every device: a dismissed banner and a seen "What\'s new" stay gone on another device', async () => {
    // A second, fresh browser is a new device: nothing remembered locally.
    const other = await browser.newContext({ locale: 'en-GB' });
    const p2 = await other.newPage();
    const errs = [];
    p2.on('pageerror', (e) => errs.push(e.message));
    await p2.goto(JF + '/web/index.html#/login');
    await p2.waitForSelector('#txtManualName', { timeout: 60000 });
    await p2.fill('#txtManualName', 'admin');
    await p2.fill('#txtManualPassword', 'admin');
    await p2.click('.manualLoginForm button[type="submit"]');
    await p2.waitForURL(/home/, { timeout: 60000 });
    await sleep(6000);
    expect(!(await p2.locator('#jellyfin-medic-banners').isVisible().catch(() => false)), 'dismissed banner came back on another device');
    await p2.goto(JF + '/web/index.html#/configurationpage?name=JellyfinMedic');
    await p2.waitForSelector('#JellyfinMedicPage .so-tab', { timeout: 30000 });
    await sleep(3000);
    expect(!(await p2.locator('#md-whatsnew').isVisible()), '"What\'s new" showed again on another device');
    await p2.click('#JellyfinMedicPage .so-tab[data-tab="settings"]');
    await p2.click('#md-changelog-all');
    await p2.waitForSelector('#md-whatsnew:not([hidden])', { timeout: 15000 });
    const versions = await p2.locator('#md-whatsnew-body h3').count();
    expect(versions >= 5, 'full changelog shows only ' + versions + ' versions');
    await other.close();
    const ours = errs.filter((e) => !/ResizeObserver|^CancelledError/i.test(e));
    expect(ours.length === 0, ours.join(' | '));
    return versions + ' versions in the full changelog';
});

await check('Tracks: unsaved track settings are saved before a scan or a run starts, and the run\'s confirm names them', async () => {
    const before = await ok('/JellyfinMedic/MedicSettings');
    const posts = [];
    const dialogs = [];
    const watch = (req) => {
        const m = req.method() === 'POST' && req.url().match(/\/JellyfinMedic\/(MedicSettings|Tracks\/Scan\/Start|Tracks\/Run)\b/);
        if (m) posts.push(m[1]);
    };
    const accept = (dialog) => { dialogs.push(dialog.message()); dialog.accept(); };
    // Nothing is stripped: the run is answered here and never reaches the server.
    await page.route('**/JellyfinMedic/Tracks/Run?*', (route) => route.fulfill({ json: { Started: true, DryRun: false } }));
    page.on('request', watch);
    page.on('dialog', accept);
    try {
        await medicTab('tracks', true);
        await page.waitForSelector('#md-tracks-save:not([disabled])', { timeout: 15000 });
        await page.fill('#md-tracks-langs-in', 'eng, FRE');
        await page.fill('#md-tracks-exclude-in', 'Seeded\n\n  Cut Short  \nOne\nTwo\nThree\nFour');
        await page.click('#md-tracks-scan');
        for (let i = 0; i < 50 && !posts.includes('Tracks/Scan/Start'); i++) await sleep(200);
        expect(posts.includes('Tracks/Scan/Start'), 'the scan didn\'t start: ' + posts.join(', '));
        expect(posts.indexOf('MedicSettings') === 0, 'no save before the scan: ' + posts.join(', '));
        const saved = await ok('/JellyfinMedic/MedicSettings');
        expect(/Seeded/.test(saved.TracksExclude || ''), 'saved exclusions: ' + saved.TracksExclude);
        expect(/first/i.test(await page.locator('#md-tracks-saved').textContent()), 'no note that the settings were saved first');
        for (let i = 0; i < 60 && (await ok('/JellyfinMedic/Tracks/Scan/Status')).Running; i++) await sleep(1000);

        posts.length = 0;
        await page.check('#md-tracks-inplace-in');
        await page.evaluate(() => { document.getElementById('md-tracks-run-panel').hidden = false; });
        await page.click('#md-tracks-go');
        for (let i = 0; i < 50 && !posts.includes('Tracks/Run'); i++) await sleep(200);
        expect(posts.join(', ') === 'MedicSettings, Tracks/Run', 'requests for Strip: ' + posts.join(', '));
        expect((await ok('/JellyfinMedic/MedicSettings')).TracksReplaceInPlace === true, 'replace in place wasn\'t saved');
        const confirm = dialogs[dialogs.length - 1] || '';
        for (const part of ['eng, fre', 'Seeded', 'Cut Short', 'and 1 more', 'originals aren\'t kept']) {
            expect(confirm.includes(part), `the confirm doesn't say "${part}": ${confirm}`);
        }
        return 'saved before Scan and Strip; the confirm names the values';
    } finally {
        page.off('request', watch);
        page.off('dialog', accept);
        await page.unroute('**/JellyfinMedic/Tracks/Run?*');
        await api('POST', '/JellyfinMedic/MedicSettings', before);
    }
});

await browser.close();
arrServer.close();
console.log(`${passed}/${passed + failed} passed`);
process.exit(failed ? 1 : 0);
