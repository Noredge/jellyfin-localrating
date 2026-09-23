// Dependency-free browser regression fixture. Never connects to a Jellyfin server.
// Run: node tests/web/server.cjs; open http://127.0.0.1:18767 and run the tests.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assets = path.resolve(__dirname, '../../src/Jellyfin.Plugin.LocalRating/Web');
const routes = {
    '/narrow': [path.join(__dirname, 'narrow.html'), 'text/html; charset=utf-8'],
    '/': [path.join(__dirname, 'fixture.html'), 'text/html; charset=utf-8'],
    '/fixture.js': [path.join(__dirname, 'fixture.js'), 'text/javascript; charset=utf-8'],
    '/configuration-fixture.js': [path.join(__dirname, 'configuration-fixture.js'), 'text/javascript; charset=utf-8'],
    '/localrating.js': [path.join(assets, 'localrating.js'), 'text/javascript; charset=utf-8'],
    '/localrating.css': [path.join(assets, 'localrating.css'), 'text/css; charset=utf-8']
};
http.createServer((req, res) => {
    const pathname = new URL(req.url, 'http://127.0.0.1').pathname;
    if (pathname === '/configuration') {
        const html = fs.readFileSync(path.join(assets, 'configuration.html'), 'utf8');
        res.setHeader('Content-Type', 'text/html; charset=utf-8');
        res.setHeader('Cache-Control', 'no-store');
        // Match Jellyfin's treatment of unknown localization placeholders in plugin HTML.
        const localized = html.replace(/\$\{([^}]+)\}/g, '$1');
        res.end(localized.replace('<script>', '<script src="/configuration-fixture.js"></script><script>'));
        return;
    }
    const route = routes[pathname];
    if (!route) { res.writeHead(404); res.end(); return; }
    res.setHeader('Content-Type', route[1]);
    res.setHeader('Cache-Control', 'no-store');
    res.end(fs.readFileSync(route[0]));
}).listen(18767, '127.0.0.1', () => console.log('Web regression fixture: http://127.0.0.1:18767'));
