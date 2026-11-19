// Development only: serves IGYO's own pages (browser UI, New Tab, Settings) to an ordinary browser,
// so themes and pages can be worked on without starting IGYO. Nothing here ships with the browser.
//
//   node tools/serve.js --root src/IGYO/Assets/Ui --port 5310
//
// /shared/*   -> Assets/Shared/*
// /theme.css  -> built from Assets/Themes/<id>.json and its kit, the same way IGYO builds it (see Themes.cs)
// /theme.js   -> the theme's name, kit and options, for the page
// The theme is taken from ?theme=<id> on the page's address, or set for everyone with /__dev/theme?id=<id>.
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');

const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf('--' + name); return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback; };
const assets = path.resolve(arg('assets', path.join(__dirname, '..', 'src', 'IGYO', 'Assets')));
const root = path.resolve(arg('root', path.join(assets, 'Ui')));
const port = Number(arg('port', process.env.PORT || 5310));
let currentTheme = arg('theme', 'igyo-dark');

const TYPES = {
  '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg', '.gif': 'image/gif', '.webp': 'image/webp', '.woff2': 'font/woff2', '.woff': 'font/woff',
  '.ttf': 'font/ttf', '.ico': 'image/x-icon', '.txt': 'text/plain; charset=utf-8',
};

// ----- themes (keep in step with Themes.cs) -----

const kebab = (s) => s.replace(/([a-z])([A-Z0-9])/g, '$1-$2').toLowerCase();
const safeValue = (v) => typeof v === 'string' && v.length <= 400 && !/[;{}<>\\]|url\(|@import|expression\(/i.test(v);

function loadTheme(id) {
  const file = path.join(assets, 'Themes', String(id).replace(/[^a-z0-9-]/gi, '') + '.json');
  if (!fs.existsSync(file)) return null;
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function themeCss(theme) {
  const lines = [];
  const put = (name, value) => { if (safeValue(String(value))) lines.push(`  ${name}: ${value};`); };
  for (const [k, v] of Object.entries(theme.colors || {})) put('--c-' + kebab(k), v);
  for (const [k, v] of Object.entries(theme.fonts || {})) put('--f-' + kebab(k), v);
  const shape = theme.shape || {};
  if (Number.isFinite(shape.radius)) put('--radius', Math.max(0, Math.min(28, shape.radius)));
  if (Number.isFinite(shape.border)) put('--bw', Math.max(0, Math.min(4, shape.border)) + 'px');
  for (const [k, v] of Object.entries(theme.vars || {})) if (/^--[a-z0-9-]+$/i.test(k)) put(k, v);
  // The theme's own picture: only a data: address, as Themes.cs accepts it (its New Tab shows it).
  const wallpaper = String(theme.wallpaper || '');
  if (wallpaper.length <= 5 * 1024 * 1024 && /^data:image\//i.test(wallpaper) && !/["')\\ ]/.test(wallpaper)) lines.push(`  --wallpaper: url("${wallpaper}");`);
  const kitFile = path.join(assets, 'Themes', 'kits', String(theme.kit || 'igyo').replace(/[^a-z0-9-]/gi, '') + '.css');
  const kit = fs.existsSync(kitFile) ? fs.readFileSync(kitFile, 'utf8') : '';
  const custom = String(theme.css || '').replace(/@import[^;]*;/gi, '').replace(/url\(\s*(['"]?)(?!data:)[^)]*\)/gi, 'none');
  return `@layer base, kit, user;\n@layer kit {\n${kit}\n}\n:root {\n${lines.join('\n')}\n}\n@layer user {\n${custom}\n}\n`;
}

function themeMeta(theme) {
  return {
    id: theme.id, name: theme.name, kit: theme.kit || 'igyo', scheme: theme.scheme === 'light' ? 'light' : 'dark',
    options: theme.options || {}, fx: theme.fx || {}, v: Date.now(),
  };
}

function themeJs(theme) {
  return `(function(t){var r=document.documentElement;r.dataset.kit=t.kit;r.dataset.scheme=t.scheme;r.dataset.theme=t.id;` +
    `for(var k in t.options)r.setAttribute('data-o-'+k,t.options[k]);window.IGYO_THEME=t;})(${JSON.stringify(themeMeta(theme))});\n`;
}

function themeIdFor(req, url) {
  const direct = url.searchParams.get('theme');
  if (direct) return direct;
  try { const viaPage = new URL(req.headers.referer || '').searchParams.get('theme'); if (viaPage) return viaPage; } catch { /* no referer */ }
  return currentTheme;
}

// ----- server -----

function send(res, status, type, body) {
  res.writeHead(status, { 'Content-Type': type, 'Cache-Control': 'no-store' });
  res.end(body);
}

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  let p = decodeURIComponent(url.pathname);
  try {
    if (p === '/__dev/theme') {
      if (url.searchParams.get('id')) currentTheme = url.searchParams.get('id');
      return send(res, 200, TYPES['.json'], JSON.stringify({ theme: currentTheme }));
    }
    if (p === '/__dev/themes') {
      const dir = path.join(assets, 'Themes');
      const list = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => {
        const t = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
        return { id: t.id, name: t.name, kit: t.kit, scheme: t.scheme, description: t.description || '', inspiredBy: t.inspiredBy || '' };
      });
      return send(res, 200, TYPES['.json'], JSON.stringify(list));
    }
    if (p === '/__dev/suggest') {
      // Stands in for Google's suggestion service when testing a Debug build (NSB_SUGGEST_URL), so tests never contact Google.
      const q = url.searchParams.get('q') || '';
      const site = 'https://www.' + q.replace(/[^a-z0-9]/gi, '').toLowerCase() + '.test/';
      return send(res, 200, TYPES['.json'], JSON.stringify([q, [q + ' wiki', q + ' tier list', site, 'how to ' + q], ['', '', 'An example site', ''], [],
        { 'google:suggesttype': ['QUERY', 'QUERY', 'NAVIGATION', 'QUERY'] }]));
    }
    if (p === '/lang.js') {
      // The interface's words (keep in step with Lang.cs). English, unless ?lang=<id> on the page's address names a file in Assets/Lang.
      let id = url.searchParams.get('lang') || '';
      try { id = id || new URL(req.headers.referer || '').searchParams.get('lang') || ''; } catch { /* no referer */ }
      const file = path.join(assets, 'Lang', id.replace(/[^a-z0-9-]/gi, '') + '.json');
      const words = id && fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')).words || {} : {};
      return send(res, 200, TYPES['.js'], `(() => {
  const words = ${JSON.stringify(words)};
  const has = (text) => Object.prototype.hasOwnProperty.call(words, text);
  const tr = (text, ...args) => {
    const s = has(text) ? words[text] : String(text == null ? '' : text);
    return args.length ? s.replace(/\\{(\\d+)\\}/g, (all, n) => (Number(n) < args.length ? String(args[n]) : all)) : s;
  };
  const translatePage = (root) => {
    const from = root || document;
    for (const name of ['title', 'aria-label', 'placeholder', 'alt'])
      for (const node of from.querySelectorAll('[' + name + ']')) { const value = node.getAttribute(name); if (has(value)) node.setAttribute(name, words[value]); }
    for (const node of from.querySelectorAll('[data-t]'))
      for (const child of node.childNodes) { if (child.nodeType !== 3) continue; const value = child.nodeValue.trim(); if (has(value)) child.nodeValue = words[value]; }
  };
  window.IGYO = window.IGYO || {};
  Object.assign(window.IGYO, { tr, translatePage, lang: ${JSON.stringify(id || 'en')} });
})();
`);
    }
    if (p === '/theme.css' || p === '/theme.js') {
      const theme = loadTheme(themeIdFor(req, url)) || loadTheme('igyo-dark') || { id: 'none', kit: 'igyo', scheme: 'dark' };
      return send(res, 200, p.endsWith('.css') ? TYPES['.css'] : TYPES['.js'], p.endsWith('.css') ? themeCss(theme) : themeJs(theme));
    }
    // The New Tab page outside IGYO: pretend settings (its shortcuts point nowhere real) and a save that does nothing.
    if (p === '/config.js' && !fs.existsSync(path.join(root, 'config.js'))) {
      return send(res, 200, TYPES['.js'], 'window.IGYO_NEWTAB = ' + JSON.stringify({
        dev: true, engine: 'https://www.google.com/search?q=%s', engineName: 'Google', anim: url.searchParams.get('still') ? false : true,
        isPrivate: false, clock: true, showShortcuts: true, pictures: [], picture: '', shown: '', rotate: false,
        shortcuts: [{ name: 'Alpha', url: 'https://alpha.test/' }, { name: 'Beta mail', url: 'https://beta.test/' }, { name: 'Gamma', url: 'https://gamma.test/' }, { name: 'Delta docs', url: 'https://delta.test/' }],
      }) + ';\n');
    }
    if (req.method === 'POST' && p.startsWith('/api/')) return send(res, 200, TYPES['.json'], JSON.stringify({ ok: true, pictures: [], picture: '' }));
    let base = root;
    if (p.startsWith('/shared/')) { base = path.join(assets, 'Shared'); p = p.slice('/shared'.length); }
    else if (p.startsWith('/app-icons/')) { base = path.join(assets, 'AppIcons'); p = p.slice('/app-icons'.length); }   // Settings > Appearance > App icon
    if (p.endsWith('/')) p += 'index.html';
    const file = path.normalize(path.join(base, p));
    if (!file.startsWith(base)) return send(res, 403, TYPES['.txt'], 'Refused');
    if (!fs.existsSync(file) || fs.statSync(file).isDirectory()) return send(res, 404, TYPES['.txt'], 'Not found: ' + p);
    send(res, 200, TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream', fs.readFileSync(file));
  } catch (error) {
    send(res, 500, TYPES['.txt'], String(error && error.stack || error));
  }
}).listen(port, '127.0.0.1', () => console.log(`IGYO dev pages: http://localhost:${port}/  (root ${root})`));
