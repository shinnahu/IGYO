// Development only: keeps IGYO's translations in step with its source. Nothing here ships with the browser.
//
//   node tools/lang.js check        every text the interface shows has a translation in every language file,
//                                   and no language file holds a text that is no longer used
//   node tools/lang.js keys         lists the texts the interface shows (what a new language file has to translate)
//   node tools/lang.js candidates   lists quoted texts in the source that look like interface text but are not
//                                   marked for translation yet
//
// A text is marked for translation by passing it through tr('…') in the pages' scripts or L.T("…") in the
// app's C#. The English text is the key; language files are Assets/Lang/<id>.json: { "name": …, "words": { … } }.
'use strict';
const fs = require('fs');
const path = require('path');

const src = path.join(__dirname, '..', 'src', 'IGYO');
const assets = path.join(src, 'Assets');
const SCRIPTS = ['Ui/chrome.js', 'Ui/overlays.js', 'Ui/start.js', 'Ui/fx.js', 'Ui/picker.js', 'NewTab/newtab.js', 'Settings/core.js', 'Settings/settings.js', 'Settings/themes.js', 'Settings/bookmarks.js'];
const PAGES = ['Ui/chrome.html', 'Ui/picker.html', 'NewTab/index.html', 'Settings/index.html'];
const CSHARP = fs.readdirSync(src).filter((f) => f.endsWith('.cs') && f !== 'SelfTest.cs');

// ----- reading quoted texts out of source -----

// Quoted texts of a script: { start, end, value, quote, holes } (holes: it is a `template` with ${…} in it).
function scriptStrings(text) {
  const found = [];
  let i = 0;
  let last = '';   // the last character that wasn't white space, to tell a division from a regular expression
  const n = text.length;
  function template(from) {
    let j = from + 1, holes = false, value = '';
    while (j < n && text[j] !== '`') {
      if (text[j] === '\\') { value += text[j] + (text[j + 1] || ''); j += 2; continue; }
      if (text[j] === '$' && text[j + 1] === '{') {
        holes = true;
        let depth = 1;
        j += 2;
        while (j < n && depth > 0) {
          if (text[j] === '`') { j = template(j).end; continue; }
          if (text[j] === "'" || text[j] === '"') { const q = text[j]; j++; while (j < n && text[j] !== q) j += text[j] === '\\' ? 2 : 1; j++; continue; }
          if (text[j] === '{') depth++;
          else if (text[j] === '}') depth--;
          j++;
        }
        value += '${…}';
        continue;
      }
      value += text[j++];
    }
    return { start: from, end: j + 1, value, quote: '`', holes };
  }
  while (i < n) {
    const c = text[i];
    if (c === '/' && text[i + 1] === '/') { while (i < n && text[i] !== '\n') i++; continue; }
    if (c === '/' && text[i + 1] === '*') { i = text.indexOf('*/', i + 2); i = i < 0 ? n : i + 2; continue; }
    if (c === '/' && (last === '' || '(,=:[!&|?{};'.includes(last) || /(return|typeof)\s*$/.test(text.slice(Math.max(0, i - 8), i)))) {
      let j = i + 1, inClass = false;
      while (j < n && text[j] !== '\n' && (inClass || text[j] !== '/')) { if (text[j] === '\\') j++; else if (text[j] === '[') inClass = true; else if (text[j] === ']') inClass = false; j++; }
      i = j + 1;
      last = '/';
      continue;
    }
    if (c === "'" || c === '"') {
      let j = i + 1, value = '';
      while (j < n && text[j] !== c && text[j] !== '\n') { if (text[j] === '\\') { value += text[j] + (text[j + 1] || ''); j += 2; } else value += text[j++]; }
      found.push({ start: i, end: j + 1, value, quote: c, holes: false });
      i = j + 1;
      last = c;
      continue;
    }
    if (c === '`') { const t = template(i); found.push(t); i = t.end; last = '`'; continue; }
    if (!/\s/.test(c)) last = c;
    i++;
  }
  return found;
}

// Quoted texts of C#: { start, end, value, holes, verbatim } (holes: an interpolated $"…").
function csharpStrings(text) {
  const found = [];
  let i = 0;
  const n = text.length;
  function quoted(from, verbatim, interpolated) {
    let j = from + 1, value = '', holes = false;
    while (j < n) {
      const c = text[j];
      if (verbatim && c === '"' && text[j + 1] === '"') { value += '""'; j += 2; continue; }
      if (!verbatim && c === '\\') { value += c + (text[j + 1] || ''); j += 2; continue; }
      if (c === '"') break;
      if (interpolated && c === '{' && text[j + 1] === '{') { value += '{{'; j += 2; continue; }
      if (interpolated && c === '}' && text[j + 1] === '}') { value += '}}'; j += 2; continue; }
      if (interpolated && c === '{') {
        holes = true;
        let depth = 1;
        j++;
        while (j < n && depth > 0) {
          if (text[j] === '"') { j = quoted(j, false, false).end; continue; }
          if (text[j] === '$' && text[j + 1] === '"') { j = quoted(j + 1, false, true).end; continue; }
          if (text[j] === '{') depth++;
          else if (text[j] === '}') depth--;
          j++;
        }
        value += '{…}';
        continue;
      }
      value += c;
      j++;
    }
    return { end: j + 1, value, holes };
  }
  while (i < n) {
    const c = text[i];
    if (c === '/' && text[i + 1] === '/') { while (i < n && text[i] !== '\n') i++; continue; }
    if (c === '/' && text[i + 1] === '*') { i = text.indexOf('*/', i + 2); i = i < 0 ? n : i + 2; continue; }
    if (c === "'") { i += text[i + 1] === '\\' ? 4 : 3; continue; }   // a character like 'x' or '\n'
    let at = i, verbatim = false, interpolated = false;
    while (text[at] === '$' || text[at] === '@') { if (text[at] === '$') interpolated = true; else verbatim = true; at++; }
    if (text[at] === '"' && (at === i || at - i <= 2)) {
      const q = quoted(at, verbatim, interpolated);
      found.push({ start: i, end: q.end, value: q.value, holes: q.holes, verbatim, interpolated });
      i = q.end;
      continue;
    }
    i++;
  }
  return found;
}

const unescapeScript = (s) => s.replace(/\\(u\{[0-9a-fA-F]+\}|u[0-9a-fA-F]{4}|x[0-9a-fA-F]{2}|[\s\S])/g, (all, e) =>
  e === 'n' ? '\n' : e === 't' ? '\t' : e === 'r' ? '\r' : e[0] === 'u' ? String.fromCodePoint(parseInt(e.replace(/[u{}]/g, ''), 16)) : e[0] === 'x' ? String.fromCharCode(parseInt(e.slice(1), 16)) : e);
const unescapeCsharp = (s, verbatim) => verbatim ? s.replace(/""/g, '"') : s.replace(/\\(u[0-9a-fA-F]{4}|[\s\S])/g, (all, e) =>
  e === 'n' ? '\n' : e === 't' ? '\t' : e === 'r' ? '\r' : e[0] === 'u' ? String.fromCharCode(parseInt(e.slice(1), 16)) : e);

// Does a quoted text look like something written for a person to read?
const looksLikeText = (s) => /[A-Za-z]{3}/.test(s) && (/^[A-Z"“(]/.test(s) || / /.test(s)) && !/^[a-z0-9_.\-/:#\[\]=*,>+ ()'"|@%]+$/.test(s) &&
  !/^(https?:|\/|\.|#|--|@|\\)/.test(s) && !/[{};]\s*$/.test(s) && !/^\s*</.test(s) && !/^[a-z-]+:\s*\S+$/.test(s) || /^[A-Z][a-z]+…?$/.test(s);

const before = (text, at, n) => text.slice(Math.max(0, at - n), at).replace(/\s+$/, '');
const after = (text, at, n) => text.slice(at, at + n).replace(/^\s+/, '');

// ----- what the source marks for translation -----

function usedKeys() {
  const keys = new Map();   // text -> where it is used
  const add = (key, where) => { if (!keys.has(key)) keys.set(key, where); };
  for (const file of SCRIPTS) {
    const text = fs.readFileSync(path.join(assets, file), 'utf8');
    for (const s of scriptStrings(text)) if (!s.holes && /\btr\($/.test(before(text, s.start, 6))) add(unescapeScript(s.value), file);
  }
  for (const file of CSHARP) {
    const text = fs.readFileSync(path.join(src, file), 'utf8');
    for (const s of csharpStrings(text)) if (!s.holes && /\bL\.(T|Key)\($/.test(before(text, s.start, 8))) add(unescapeCsharp(s.value, s.verbatim), file);
  }
  for (const file of PAGES) {
    const text = fs.readFileSync(path.join(assets, file), 'utf8');
    for (const m of text.matchAll(/\s(?:title|aria-label|placeholder|alt)="([A-Z][^"]*[a-z]{2}[^"]*)"/g)) add(decode(m[1]), file);
    for (const m of text.matchAll(/<[^>]*\sdata-t[^>]*>([^<]+)</g)) add(decode(m[1].trim()), file);
  }
  // Texts that come from data files the app itself ships: the theme editor's labels and the built-in themes' descriptions.
  const kits = JSON.parse(fs.readFileSync(path.join(assets, 'Themes', 'kits', 'kits.json'), 'utf8'));
  const walk = (node) => {
    if (Array.isArray(node)) return node.forEach(walk);
    if (!node || typeof node !== 'object') return;
    for (const [k, v] of Object.entries(node)) {
      if (typeof v === 'string' && ['label', 'hint', 'note', 'description'].includes(k)) add(v, 'kits.json');
      else walk(v);
    }
  };
  walk(kits);
  // An effect's choices are plain words in kits.json ("grow", "pop"); the editor shows them with a capital letter.
  for (const effect of kits.fx || []) for (const value of effect.values || []) if (typeof value === 'string') add(value[0].toUpperCase() + value.slice(1), 'kits.json');
  for (const file of fs.readdirSync(path.join(assets, 'Themes')).filter((f) => f.endsWith('.json'))) {
    const theme = JSON.parse(fs.readFileSync(path.join(assets, 'Themes', file), 'utf8'));
    if (theme.description) add(theme.description, 'Themes/' + file);
  }
  return keys;
}

const decode = (s) => s.replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&#39;/g, "'");

function languages() {
  const dir = path.join(assets, 'Lang');
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => ({ id: f.replace(/\.json$/, ''), ...JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')) }));
}

const holes = (s) => (s.match(/\{\d+\}/g) || []).sort().join(',');

// ----- commands -----

const command = process.argv[2] || 'check';

if (command === 'keys') {
  const keys = usedKeys();
  if (process.argv[3] === '--json') console.log(JSON.stringify([...keys.keys()], null, 1));
  else for (const [key, where] of keys) console.log(where.padEnd(24) + JSON.stringify(key));
  console.error(`${keys.size} texts`);
} else if (command === 'candidates') {
  for (const file of SCRIPTS) {
    const text = fs.readFileSync(path.join(assets, file), 'utf8');
    const seen = new Set();
    for (const s of scriptStrings(text)) {
      const value = unescapeScript(s.value);
      if (seen.has(value) || !looksLikeText(value) || /\btr\($/.test(before(text, s.start, 6))) continue;
      seen.add(value);
      console.log(`${file}:${text.slice(0, s.start).split('\n').length}\t${s.holes ? 'HOLES' : 'plain'}\t${JSON.stringify(value)}`);
    }
  }
  for (const file of CSHARP) {
    const text = fs.readFileSync(path.join(src, file), 'utf8');
    const seen = new Set();
    for (const s of csharpStrings(text)) {
      const value = unescapeCsharp(s.value, s.verbatim);
      if (seen.has(value) || !looksLikeText(value) || /\bL\.(T|Key)\($/.test(before(text, s.start, 8))) continue;
      seen.add(value);
      console.log(`${file}:${text.slice(0, s.start).split('\n').length}\t${s.holes ? 'HOLES' : 'plain'}\t${JSON.stringify(value)}`);
    }
  }
} else if (command === 'wrap') {
  // Marks plain quoted texts for translation. The given JSON file says what to leave alone:
  //   { "skip": ["exact text", …], "skipPatterns": ["regular expression", …], "csharp": ["File.cs", … the only C# files to touch] }
  // Used once, when translations were introduced; kept because it documents how the marks got there.
  const rules = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
  const exact = new Set(rules.skip || []);
  const patterns = (rules.skipPatterns || []).map((p) => new RegExp(p));
  const skip = { has: (value) => exact.has(value) || patterns.some((p) => p.test(value)) };
  const csharpFiles = rules.csharp || CSHARP;
  let wrapped = 0;
  const run = (file, full, strings, unescape, open, isMarked, notHere) => {
    let text = fs.readFileSync(full, 'utf8');
    const edits = [];
    for (const s of strings(text)) {
      const value = unescape(s);
      if (s.holes || !looksLikeText(value) || skip.has(value) || isMarked(text, s) || notHere(text, s)) continue;
      edits.push(s);
    }
    for (const s of edits.reverse()) text = text.slice(0, s.start) + open + text.slice(s.start, s.end) + ')' + text.slice(s.end);
    if (edits.length) fs.writeFileSync(full, text);
    wrapped += edits.length;
    console.log(String(edits.length).padStart(4) + '  ' + file);
  };
  const scriptNotHere = (text, s) => /(===|!==|==|!=|\bcase|\btypeof|\bkey:|\bid:|\bkind:|\bicon:|\bclass:|\btype:|\bvalue:)$/.test(before(text, s.start, 8)) || /^(===|!==|==|!=)/.test(after(text, s.end, 4)) ||
    (/^:/.test(after(text, s.end, 3)) && /[{,]$/.test(before(text, s.start, 3))) || /(console\.\w+|new Error|querySelector(All)?|getAttribute|setAttribute|addEventListener|\$)\($/.test(before(text, s.start, 22));
  const csharpNotHere = (text, s) => s.interpolated || /(==|!=|\bcase|\bconst string \w+ =|\[|\bLog\(|\bnameof\()$/.test(before(text, s.start, 48)) || /^(==|!=|\])/.test(after(text, s.end, 3));
  for (const file of SCRIPTS) run(file, path.join(assets, file), scriptStrings, (s) => unescapeScript(s.value), 'tr(', (text, s) => /\btr\($/.test(before(text, s.start, 6)), scriptNotHere);
  for (const file of csharpFiles) run(file, path.join(src, file), csharpStrings, (s) => unescapeCsharp(s.value, s.verbatim), 'L.T(', (text, s) => /\bL\.(T|Key)\($/.test(before(text, s.start, 8)), csharpNotHere);
  console.log(`${wrapped} texts marked`);
} else {
  const keys = usedKeys();
  const list = languages();
  let problems = 0;
  if (!list.length) { console.log('No language files in Assets/Lang.'); process.exit(1); }
  for (const language of list) {
    const words = language.words || {};
    const missing = [...keys.keys()].filter((k) => !(k in words));
    const unused = Object.keys(words).filter((k) => !keys.has(k));
    const wrongHoles = Object.keys(words).filter((k) => holes(k) !== holes(words[k]));
    const empty = Object.keys(words).filter((k) => typeof words[k] !== 'string' || !words[k].trim());
    console.log(`${language.id} (${language.name}): ${Object.keys(words).length} texts; ${missing.length} missing, ${unused.length} not used, ${wrongHoles.length} with other {0} places, ${empty.length} empty`);
    for (const k of missing.slice(0, 400)) console.log('   missing  ' + (keys.get(k) || '').padEnd(22) + JSON.stringify(k));
    for (const k of unused.slice(0, 100)) console.log('   not used ' + JSON.stringify(k));
    for (const k of wrongHoles) console.log('   places   ' + JSON.stringify(k) + ' -> ' + JSON.stringify(words[k]));
    for (const k of empty) console.log('   empty    ' + JSON.stringify(k));
    problems += missing.length + unused.length + wrongHoles.length + empty.length;
  }
  console.log(`${keys.size} texts in the source; ${problems ? problems + ' problems' : 'all language files are complete'}`);
  process.exit(problems ? 1 : 0);
}
