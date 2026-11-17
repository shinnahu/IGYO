using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // What the bridge keeps for one tab Claude acts in: IGYO's own world in its current document (where the element
    // references of read_page and find live), the console messages and network requests seen since Claude started
    // there, the scale of the last picture (computer coordinates are in that picture's pixels) and a recording.
    sealed class TabAgent
    {
        public readonly BrowserTab Tab;
        public TabAgent(BrowserTab tab) { Tab = tab; }

        public bool Claudes;                 // opened by Claude: marked in the tab strip, and the only kind Claude may close
        public CoreWebView2 WorldCore;       // the engine view the world below belongs to (a tab gets a new one when it is loaded again)
        public int World;                    // the execution context of IGYO's world in the current document; 0 for none yet
        public double Scale;                 // picture pixels per CSS pixel in the last picture; 0 before the first
        public int ViewW, ViewH;             // the page's visible size in CSS pixels at that picture
        public CoreWebView2 TrackedCore;     // the engine view whose console and network are being followed
        public readonly List<string> Console = new List<string>();
        public readonly List<NetRequest> Network = new List<NetRequest>();
        public readonly Dictionary<string, NetRequest> NetById = new Dictionary<string, NetRequest>();
        public bool Recording;
        public readonly List<byte[]> Frames = new List<byte[]>();
        public readonly List<int> Delays = new List<int>();   // hundredths of a second each frame stays
        public DateTime LastFrame = DateTime.MinValue;

        public const int MaxConsole = 400, MaxNetwork = 400, MaxFrames = 120;

        public void AddConsole(string level, string text, string source)
        {
            var line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " [" + level + "] " + BridgeTools.OneLine(text, 1500) +
                (string.IsNullOrEmpty(source) ? "" : "  (" + BridgeTools.OneLine(source, 200) + ")");
            Console.Add(line);
            if (Console.Count > MaxConsole) Console.RemoveRange(0, Console.Count - MaxConsole);
        }
    }

    sealed class NetRequest
    {
        public string Id, Method = "", Url = "", Type = "", Status = "pending", Time = "";
    }

    // The scripts and the engine calls behind the acting tools. Every script is IGYO's own, run in a world apart
    // from the page's scripts; what Claude sends travels as values, never as script (except in javascript_tool,
    // which runs Claude's script in the page itself, on sites where the person allowed acting).
    static class BridgeAgent
    {
        // ----- IGYO's lasting world in a document (element references live there until the document changes) -----

        public static async Task<Dictionary<string, object>> InWorldAsync(TabAgent agent, string function, object[] arguments, CancellationToken cancel)
        {
            var core = agent.Tab.Core ?? throw new ToolProblem("The tab is not loaded.");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (agent.World == 0 || agent.WorldCore != core)
                {
                    var tree = Json.Obj(Json.Parse(await BridgeTools.Cdp(core, "Page.getFrameTree", "{}", cancel, 8)));
                    var frame = Json.Str(Json.Obj(Json.Get(Json.Obj(Json.Get(tree, "frameTree")), "frame")), "id");
                    if (string.IsNullOrEmpty(frame)) throw new ToolProblem("The tab has no document.");
                    var world = Json.Obj(Json.Parse(await BridgeTools.Cdp(core, "Page.createIsolatedWorld",
                        Json.Write(new Dictionary<string, object> { ["frameId"] = frame, ["worldName"] = "IGYOAgent" }), cancel, 8)));
                    agent.World = Convert.ToInt32(Json.Get(world, "executionContextId") ?? 0, CultureInfo.InvariantCulture);
                    agent.WorldCore = core;
                }
                var call = new Dictionary<string, object>
                {
                    ["functionDeclaration"] = function,
                    ["executionContextId"] = agent.World,
                    ["arguments"] = arguments.Select(a => new Dictionary<string, object> { ["value"] = a }).ToList(),
                    ["returnByValue"] = true,
                    ["awaitPromise"] = false,
                    ["silent"] = true,
                };
                string answer;
                try { answer = await BridgeTools.Cdp(core, "Runtime.callFunctionOn", Json.Write(call), cancel, 15); }
                catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is TimeoutException))
                {
                    agent.World = 0;   // the document changed since: a new world, and the old references are gone
                    if (attempt == 0) continue;
                    throw;
                }
                var parsed = Json.Obj(Json.Parse(answer));
                if (Json.Get(parsed, "exceptionDetails") != null) throw new ToolProblem("IGYO's script could not run in this page.");
                return Json.Obj(Json.Get(Json.Obj(Json.Get(parsed, "result")), "value")) ?? new Dictionary<string, object>();
            }
            throw new ToolProblem("IGYO's script could not run in this page.");
        }

        // The element a reference names, as an object the engine can hand to its own calls (file inputs).
        public static async Task<string> ElementObjectAsync(TabAgent agent, string reference, CancellationToken cancel)
        {
            await InWorldAsync(agent, "function () { return {}; }", new object[0], cancel);   // makes sure the world is there
            var call = new Dictionary<string, object>
            {
                ["functionDeclaration"] = "function (ref) { const T = globalThis.__igyo; const w = T && T.refs.get(ref); const el = w && w.deref(); return el && el.isConnected ? el : null; }",
                ["executionContextId"] = agent.World,
                ["arguments"] = new List<object> { new Dictionary<string, object> { ["value"] = reference } },
                ["returnByValue"] = false,
                ["silent"] = true,
            };
            var answer = Json.Obj(Json.Parse(await BridgeTools.Cdp(agent.Tab.Core, "Runtime.callFunctionOn", Json.Write(call), cancel, 10)));
            return Json.Str(Json.Obj(Json.Get(answer, "result")), "objectId");
        }

        // What every script shares: references, whether a box is a secret one, how an element is described.
        const string Common = @"
  const T = globalThis.__igyo || (globalThis.__igyo = { refs: new Map(), ids: new WeakMap(), next: 1 });
  const refOf = (el) => { let id = T.ids.get(el); if (!id) { id = 'ref_' + (T.next++); T.ids.set(el, id); T.refs.set(id, new WeakRef(el)); } return id; };
  const byRef = (ref) => { const w = T.refs.get(String(ref)); const el = w && w.deref(); return el && el.isConnected ? el : null; };
  const clean = (s, n) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim().slice(0, n);
  const secret = (el) => {
    if (!el || !el.getAttribute) return false;
    const type = (el.getAttribute('type') || '').toLowerCase();
    if (type === 'password') return true;
    const auto = (el.getAttribute('autocomplete') || '').toLowerCase();
    if (/(^|\s)(cc-[a-z-]+|one-time-code|current-password|new-password)(\s|$)/.test(auto)) return true;
    const hint = [el.getAttribute('name'), el.id, el.getAttribute('aria-label'), el.getAttribute('placeholder')].join(' ').toLowerCase();
    return /card.?num|cardnumber|cc.?num|cvc|cvv|csc|security.?code|one.?time|otp|verification.?code|passcode|password|passwd|\bpin\b/.test(hint);
  };
  const shown = (el) => {
    try {
      if (!el.getClientRects().length) return false;
      const s = getComputedStyle(el);
      return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
    } catch (e) { return false; }
  };
  const roleOf = (el) => {
    const given = el.getAttribute('role');
    if (given) return given.split(' ')[0].toLowerCase();
    const t = el.tagName.toLowerCase();
    if (t === 'a') return el.hasAttribute('href') ? 'link' : '';
    if (t === 'button' || t === 'summary') return 'button';
    if (t === 'select') return el.multiple ? 'listbox' : 'combobox';
    if (t === 'textarea') return 'textbox';
    if (t === 'input') {
      const ty = (el.getAttribute('type') || 'text').toLowerCase();
      const map = { checkbox: 'checkbox', radio: 'radio', button: 'button', submit: 'button', reset: 'button', image: 'button', range: 'slider', file: 'file input', search: 'searchbox', hidden: '', number: 'spinbutton' };
      return ty in map ? map[ty] : 'textbox';
    }
    if (/^h[1-6]$/.test(t)) return 'heading';
    if (t === 'img') return el.getAttribute('alt') ? 'img' : '';
    const kinds = { nav: 'navigation', main: 'main', form: 'form', dialog: 'dialog', header: 'banner', footer: 'contentinfo', aside: 'complementary',
                    ul: 'list', ol: 'list', li: 'listitem', table: 'table', tr: 'row', td: 'cell', th: 'columnheader', option: 'option', video: 'video', audio: 'audio', iframe: 'frame' };
    if (t in kinds) return kinds[t];
    if (el.isContentEditable && el.hasAttribute('contenteditable')) return 'textbox';
    return '';
  };
  const ACTS = new Set(['link', 'button', 'checkbox', 'radio', 'textbox', 'searchbox', 'combobox', 'listbox', 'slider', 'switch', 'tab', 'menuitem',
    'menuitemcheckbox', 'menuitemradio', 'option', 'file input', 'spinbutton', 'treeitem', 'clickable', 'focusable']);
  const nameOf = (el, role) => {
    let n = el.getAttribute('aria-label') || '';
    if (!n) { const by = el.getAttribute('aria-labelledby'); if (by) n = by.split(/\s+/).map((id) => { const x = document.getElementById(id); return x ? (x.innerText || x.textContent) : ''; }).join(' '); }
    if (!n && el.labels && el.labels.length) n = el.labels[0].innerText || el.labels[0].textContent;
    if (!n && el.tagName === 'IMG') n = el.getAttribute('alt');
    if (!n && el.tagName === 'INPUT' && /^(button|submit|reset)$/i.test(el.type)) n = el.value;
    if (!n && ['textbox', 'searchbox', 'combobox', 'listbox', 'spinbutton'].indexOf(role) < 0) n = el.innerText || el.textContent;
    if (!n) n = el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('name') || '';
    return clean(n, 100);
  };
  const pointer = (el) => {
    try {
      if (getComputedStyle(el).cursor !== 'pointer') return false;
      const up = el.parentElement;
      return !up || getComputedStyle(up).cursor !== 'pointer';
    } catch (e) { return false; }
  };
  const describe = (el) => {
    if (!el) return 'nothing';
    const role = roleOf(el) || el.tagName.toLowerCase();
    const name = nameOf(el, role);
    return role + (name ? ' ""' + name.replace(/""/g, ""'"") + '""' : '');
  };";

        // The page as a list of what can be acted on (filter "interactive") or of everything with a role or text of
        // its own ("all"), each with a reference to give back. Indented by how they sit inside each other.
        public const string ReadPageFunction = @"function (filter, refId, maxChars) {" + Common + @"
  const root = refId ? byRef(refId) : (document.body || document.documentElement);
  if (!root) return { missing: true };
  const all = filter === 'all';
  const lines = [];
  let chars = 0, more = 0, visited = 0;
  const ownText = (el) => { let s = ''; for (const n of el.childNodes) if (n.nodeType === 3) s += n.nodeValue; return clean(s, 120); };
  const walk = (el, depth) => {
    if (++visited > 30000) return;
    if (el !== root && !shown(el) && el.tagName !== 'DETAILS' && !(el.shadowRoot)) return;
    let role = roleOf(el);
    if (!role && pointer(el)) role = 'clickable';
    if (!role && el.hasAttribute('tabindex') && el.tabIndex >= 0) role = 'focusable';
    const acts = ACTS.has(role);
    const text = all && !role ? ownText(el) : '';
    const include = all ? !!role || !!text : acts;
    let next = depth;
    if (include) {
      let line = '  '.repeat(Math.min(depth, 24)) + (role || 'text');
      const name = role ? nameOf(el, role) : text;
      if (name) line += ' ""' + name.replace(/""/g, ""'"") + '""';
      if (acts || role === 'heading' || role === 'img' || role === 'dialog' || role === 'form') line += ' [' + refOf(el) + ']';
      if (el.tagName === 'A' && el.href) line += ' href=' + clean(el.href, 200);
      if (/^H[1-6]$/.test(el.tagName)) line += ' level=' + el.tagName[1];
      if (role === 'textbox' || role === 'searchbox' || role === 'spinbutton' || role === 'combobox') {
        if (secret(el)) line += ' (a password, card or code box: what is in it is not shown)';
        else if (el.tagName === 'SELECT') { const o = el.selectedOptions && el.selectedOptions[0]; line += ' selected=""' + clean(o ? o.text : '', 60) + '""'; }
        else if (typeof el.value === 'string' && el.value) line += ' value=""' + clean(el.value, 80).replace(/""/g, ""'"") + '""';
        else if (el.isContentEditable && el.innerText) line += ' value=""' + clean(el.innerText, 80) + '""';
        if (el.getAttribute('placeholder') && name !== clean(el.getAttribute('placeholder'), 100)) line += ' placeholder=""' + clean(el.getAttribute('placeholder'), 60) + '""';
      }
      if (role === 'checkbox' || role === 'radio' || role === 'switch') line += (el.checked || el.getAttribute('aria-checked') === 'true') ? ' checked' : ' not checked';
      if (el.disabled || el.getAttribute('aria-disabled') === 'true') line += ' disabled';
      if (el.getAttribute('aria-expanded')) line += el.getAttribute('aria-expanded') === 'true' ? ' expanded' : ' collapsed';
      if (chars + line.length + 1 > maxChars) more++;
      else { lines.push(line); chars += line.length + 1; }
      next = depth + 1;
    }
    for (const child of el.children) walk(child, next);
    if (el.shadowRoot) for (const child of el.shadowRoot.children) walk(child, next);
  };
  walk(root, 0);
  return { href: location.href, title: document.title || '', ready: document.readyState, lines: lines, more: more,
           frames: document.querySelectorAll('iframe, frame').length, vw: innerWidth, vh: innerHeight };
}";

        // Elements that match what Claude is looking for, best first: by their role, name, text and hints.
        public const string FindFunction = @"function (query, max) {" + Common + @"
  const words = String(query).toLowerCase().split(/[^\p{L}\p{N}]+/u).filter((w) => w.length > 1 || /\p{N}/u.test(w));
  const phrase = String(query).toLowerCase().trim();
  const found = [];
  let visited = 0;
  const walk = (el) => {
    if (++visited > 30000) return;
    if (!shown(el) && !el.shadowRoot) return;
    let role = roleOf(el);
    if (!role && pointer(el)) role = 'clickable';
    if (role) {
      const name = nameOf(el, role);
      const hay = [role, name, el.getAttribute('placeholder'), el.getAttribute('title'), el.getAttribute('name'), el.id, el.getAttribute('type'), el.tagName === 'A' ? el.getAttribute('href') : '']
        .filter(Boolean).join(' ').toLowerCase();
      let score = 0;
      for (const w of words) if (hay.indexOf(w) >= 0) score++;
      if (phrase && hay.indexOf(phrase) >= 0) score += 3;
      if (score > 0) {
        if (ACTS.has(role)) score += 0.5;
        const r = el.getBoundingClientRect();
        found.push({ score: score, line: describe(el) + ' [' + refOf(el) + ']' + (secret(el) ? ' (a password, card or code box)' : '') +
          ' at ' + Math.round(r.left + r.width / 2) + ',' + Math.round(r.top + r.height / 2) + (r.bottom < 0 || r.top > innerHeight ? ' (scrolled out of view)' : '') });
      }
    }
    for (const child of el.children) walk(child);
    if (el.shadowRoot) for (const child of el.shadowRoot.children) walk(child);
  };
  walk(document.body || document.documentElement);
  found.sort((a, b) => b.score - a.score);
  return { href: location.href, title: document.title || '', count: found.length, lines: found.slice(0, max).map((f) => f.line) };
}";

        // Where an element is (its middle, in the page's CSS pixels), after it is scrolled into view.
        public const string PointFunction = @"function (ref) {" + Common + @"
  const el = byRef(ref);
  if (!el) return { missing: true };
  try { el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' }); } catch (e) {}
  const r = el.getBoundingClientRect();
  return { x: r.left + r.width / 2, y: r.top + r.height / 2, w: r.width, h: r.height, desc: describe(el), secret: secret(el),
           file: el.tagName === 'INPUT' && (el.getAttribute('type') || '').toLowerCase() === 'file', multiple: !!el.multiple, href: location.href };
}";

        // What is at a point of the page (for the answer: what was clicked).
        public const string AtFunction = @"function (x, y) {" + Common + @"
  let el = document.elementFromPoint(x, y);
  while (el && el.shadowRoot) { const inner = el.shadowRoot.elementFromPoint(x, y); if (!inner || inner === el) break; el = inner; }
  if (!el) return { desc: 'nothing', href: location.href };
  let target = el;
  for (let n = el; n && n !== document.body; n = n.parentElement) { const r = roleOf(n); if (ACTS.has(r) || pointer(n)) { target = n; break; } }
  return { desc: describe(target), secret: secret(target), frame: el.tagName === 'IFRAME' || el.tagName === 'FRAME', href: location.href };
}";

        // Where the keyboard is: whether typing would go into a secret box, or into a frame IGYO can't look into.
        public const string FocusFunction = @"function () {" + Common + @"
  let el = document.activeElement, cross = false, depth = 0;
  while (el && (el.tagName === 'IFRAME' || el.tagName === 'FRAME') && depth < 5) {
    let doc = null;
    try { doc = el.contentDocument; } catch (e) { doc = null; }
    if (!doc) { cross = true; break; }
    el = doc.activeElement; depth++;
  }
  return { desc: el && el !== document.body ? describe(el) : 'the page itself', secret: secret(el), cross: cross, href: location.href };
}";

        // Fills in a box, list or switch, the way a person's typing or choosing would show to the page's own scripts.
        public const string FormFunction = @"function (ref, value) {" + Common + @"
  const el = byRef(ref);
  if (!el) return { missing: true };
  if (secret(el)) return { refused: 'secret', desc: describe(el), href: location.href };
  const tag = el.tagName;
  const type = (el.getAttribute('type') || '').toLowerCase();
  if (tag === 'INPUT' && type === 'file') return { refused: 'file', desc: describe(el), href: location.href };
  const fire = (name) => el.dispatchEvent(new Event(name, { bubbles: true }));
  try { el.scrollIntoView({ block: 'center', behavior: 'instant' }); } catch (e) {}
  if (tag === 'SELECT') {
    const want = String(value).trim();
    let index = -1;
    for (let i = 0; i < el.options.length; i++) { const o = el.options[i]; if (o.value === want || clean(o.text, 400) === want) { index = i; break; } }
    if (index < 0) for (let i = 0; i < el.options.length; i++) { if (clean(el.options[i].text, 400).toLowerCase() === want.toLowerCase()) { index = i; break; } }
    if (index < 0) return { refused: 'option', options: [...el.options].slice(0, 40).map((o) => clean(o.text, 60)), desc: describe(el), href: location.href };
    el.selectedIndex = index; fire('input'); fire('change');
    return { done: 'chose ""' + clean(el.options[index].text, 60) + '""', desc: describe(el), href: location.href };
  }
  if (tag === 'INPUT' && (type === 'checkbox' || type === 'radio')) {
    const want = value === true || /^(true|1|yes|on|checked)$/i.test(String(value));
    if (el.checked !== want) el.click();
    return { done: el.checked ? 'checked' : 'not checked', desc: describe(el), href: location.href };
  }
  if (tag === 'INPUT' || tag === 'TEXTAREA') {
    const proto = tag === 'INPUT' ? HTMLInputElement.prototype : HTMLTextAreaElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    try { el.focus({ preventScroll: true }); } catch (e) {}
    setter.call(el, String(value)); fire('input'); fire('change');
    return { done: 'filled in', desc: describe(el), href: location.href };
  }
  if (el.isContentEditable) {
    el.focus();
    const range = document.createRange(); range.selectNodeContents(el);
    const sel = getSelection(); sel.removeAllRanges(); sel.addRange(range);
    document.execCommand('insertText', false, String(value));
    return { done: 'filled in', desc: describe(el), href: location.href };
  }
  return { refused: 'kind', desc: describe(el), href: location.href };
}";

        // ----- the engine's input: mouse and keys go to this page only, never to the PC's own mouse or keyboard -----

        public static Task MouseAsync(CoreWebView2 core, string type, double x, double y, string button, int buttons, int clicks, int modifiers, CancellationToken cancel, double dx = 0, double dy = 0)
        {
            var p = new Dictionary<string, object>
            {
                ["type"] = type, ["x"] = Math.Round(x, 1), ["y"] = Math.Round(y, 1), ["button"] = button, ["buttons"] = buttons, ["clickCount"] = clicks, ["modifiers"] = modifiers,
            };
            if (type == "mouseWheel") { p["deltaX"] = dx; p["deltaY"] = dy; }
            return BridgeTools.Cdp(core, "Input.dispatchMouseEvent", Json.Write(p), cancel, 10);
        }

        public static async Task ClickAsync(CoreWebView2 core, double x, double y, string button, int count, int modifiers, CancellationToken cancel)
        {
            int mask = button == "right" ? 2 : button == "middle" ? 4 : 1;
            await MouseAsync(core, "mouseMoved", x, y, "none", 0, 0, modifiers, cancel);
            for (int i = 1; i <= count; i++)
            {
                await MouseAsync(core, "mousePressed", x, y, button, mask, i, modifiers, cancel);
                await MouseAsync(core, "mouseReleased", x, y, button, 0, i, modifiers, cancel);
            }
        }

        public static async Task DragAsync(CoreWebView2 core, double x0, double y0, double x1, double y1, int modifiers, CancellationToken cancel)
        {
            await MouseAsync(core, "mouseMoved", x0, y0, "none", 0, 0, modifiers, cancel);
            await MouseAsync(core, "mousePressed", x0, y0, "left", 1, 1, modifiers, cancel);
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                await MouseAsync(core, "mouseMoved", x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps, "left", 1, 0, modifiers, cancel);
                await Task.Delay(16, cancel);
            }
            await MouseAsync(core, "mouseReleased", x1, y1, "left", 0, 1, modifiers, cancel);
        }

        // "ctrl", "shift", "alt" (and "cmd", "meta", "win", which mean Ctrl on Windows) as the engine's bits.
        public static int ModifiersOf(string text)
        {
            int bits = 0;
            foreach (var part in (text ?? "").ToLowerInvariant().Split(new[] { '+', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                bits |= ModifierBit(part);
            return bits;
        }

        static int ModifierBit(string name)
        {
            switch (name)
            {
                case "alt": case "option": return 1;
                case "ctrl": case "control": case "cmd": case "command": case "meta": case "super": case "win": case "windows": return 2;
                case "shift": return 8;
                default: return 0;
            }
        }

        static readonly Dictionary<string, (string key, string code, int vk)> Named = new Dictionary<string, (string, string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = ("Enter", "Enter", 13), ["return"] = ("Enter", "Enter", 13), ["kp_enter"] = ("Enter", "NumpadEnter", 13),
            ["tab"] = ("Tab", "Tab", 9), ["escape"] = ("Escape", "Escape", 27), ["esc"] = ("Escape", "Escape", 27),
            ["backspace"] = ("Backspace", "Backspace", 8), ["delete"] = ("Delete", "Delete", 46), ["del"] = ("Delete", "Delete", 46),
            ["space"] = (" ", "Space", 32), ["insert"] = ("Insert", "Insert", 45),
            ["up"] = ("ArrowUp", "ArrowUp", 38), ["arrowup"] = ("ArrowUp", "ArrowUp", 38), ["down"] = ("ArrowDown", "ArrowDown", 40), ["arrowdown"] = ("ArrowDown", "ArrowDown", 40),
            ["left"] = ("ArrowLeft", "ArrowLeft", 37), ["arrowleft"] = ("ArrowLeft", "ArrowLeft", 37), ["right"] = ("ArrowRight", "ArrowRight", 39), ["arrowright"] = ("ArrowRight", "ArrowRight", 39),
            ["home"] = ("Home", "Home", 36), ["end"] = ("End", "End", 35), ["pageup"] = ("PageUp", "PageUp", 33), ["page_up"] = ("PageUp", "PageUp", 33),
            ["pagedown"] = ("PageDown", "PageDown", 34), ["page_down"] = ("PageDown", "PageDown", 34),
        };

        // One key, possibly with modifiers ("ctrl+a", "shift+Tab", "Enter", "F5"). False when the name is not known.
        public static async Task<bool> KeyAsync(CoreWebView2 core, string combo, CancellationToken cancel)
        {
            var parts = combo.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) return false;
            string last = parts[parts.Count - 1];
            int modifiers = 0;
            foreach (var p in parts.Take(parts.Count - 1)) { int bit = ModifierBit(p.ToLowerInvariant()); if (bit == 0) return false; modifiers |= bit; }
            string key, code, text = null;
            int vk;
            if (Named.TryGetValue(last, out var named)) { key = named.key; code = named.code; vk = named.vk; if (vk == 13) text = "\r"; else if (vk == 32) text = " "; }
            else if (last.Length > 1 && (last[0] == 'f' || last[0] == 'F') && int.TryParse(last.Substring(1), out int f) && f >= 1 && f <= 12) { key = "F" + f; code = key; vk = 111 + f; }
            else if (last.Length == 1 && char.IsLetter(last[0]) && last[0] < 128)
            {
                char c = last[0];
                bool upper = (modifiers & 8) != 0 || char.IsUpper(c);
                key = upper ? char.ToUpperInvariant(c).ToString() : char.ToLowerInvariant(c).ToString();
                code = "Key" + char.ToUpperInvariant(c);
                vk = char.ToUpperInvariant(c);
                text = key;
            }
            else if (last.Length == 1 && char.IsDigit(last[0])) { key = last; code = "Digit" + last; vk = last[0]; text = last; }
            else if (last.Length == 1) { key = last; code = ""; vk = 0; text = last; }
            else return false;
            if ((modifiers & 3) != 0) text = null;   // with Ctrl or Alt a key is a command, not a character
            var down = new Dictionary<string, object> { ["type"] = text != null ? "keyDown" : "rawKeyDown", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk, ["modifiers"] = modifiers };
            if (text != null) { down["text"] = text; down["unmodifiedText"] = text; }
            await BridgeTools.Cdp(core, "Input.dispatchKeyEvent", Json.Write(down), cancel, 10);
            await BridgeTools.Cdp(core, "Input.dispatchKeyEvent", Json.Write(new Dictionary<string, object> { ["type"] = "keyUp", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk, ["modifiers"] = modifiers }), cancel, 10);
            return true;
        }

        // Text typed where the keyboard is in the page: lines as Enter, tabs as Tab, the rest inserted as it is.
        public static async Task TypeAsync(CoreWebView2 core, string text, CancellationToken cancel)
        {
            var piece = new StringBuilder();
            async Task Flush()
            {
                if (piece.Length == 0) return;
                await BridgeTools.Cdp(core, "Input.insertText", Json.Write(new Dictionary<string, object> { ["text"] = piece.ToString() }), cancel, 10);
                piece.Clear();
            }
            foreach (char c in text.Replace("\r\n", "\n"))
            {
                if (c == '\n' || c == '\t')
                {
                    await Flush();
                    await KeyAsync(core, c == '\n' ? "Enter" : "Tab", cancel);
                }
                else piece.Append(c);
            }
            await Flush();
        }

        // ----- pictures -----

        // A picture of what the tab shows, with the private parts covered (see BridgeTools.ShotAsync), at the size
        // Claude reads well. Also gives the scale between the picture and the page.
        public static async Task<(byte[] jpeg, int width, int height, int cssW, int cssH, string href, string title)> PictureAsync(CoreWebView2 core, CancellationToken cancel, int? clipX = null, int? clipY = null, int? clipW = null, int? clipH = null)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var before = await BridgeTools.RunAsync(core, BridgeTools.CoverFunction, new object[0], cancel);
                string request = "{\"format\":\"png\",\"optimizeForSpeed\":true}";
                double zoom = 1;
                if (clipW.HasValue)
                {
                    zoom = Math.Max(1, Math.Min(4, 900.0 / Math.Max(1, clipW.Value)));
                    request = Json.Write(new Dictionary<string, object>
                    {
                        ["format"] = "png",
                        ["clip"] = new Dictionary<string, object> { ["x"] = clipX.Value, ["y"] = clipY.Value, ["width"] = clipW.Value, ["height"] = clipH.Value, ["scale"] = zoom },
                    });
                }
                string shot = await BridgeTools.Cdp(core, "Page.captureScreenshot", request, cancel, 10);
                var after = await BridgeTools.RunAsync(core, BridgeTools.CoverFunction, new object[0], cancel);
                if (BridgeTools.Str(before, "sig") != BridgeTools.Str(after, "sig")) continue;
                var covers = Json.Arr(Json.Get(after, "rects")).Select(r => Json.Arr(r).Select(n => { try { return (int)Math.Round(Convert.ToDouble(n, CultureInfo.InvariantCulture)); } catch { return 0; } }).ToArray())
                    .Where(r => r.Length == 4).ToList();
                int cssW = BridgeTools.Num(after, "w"), cssH = BridgeTools.Num(after, "h");
                if (clipW.HasValue)
                {
                    covers = covers.Select(r => new[] { r[0] - clipX.Value, r[1] - clipY.Value, r[2], r[3] }).ToList();
                    cssW = clipW.Value; cssH = clipH.Value;
                }
                byte[] jpeg = BridgeTools.Compose(Convert.FromBase64String(BridgeTools.PictureData(shot)), cssW, cssH, covers, out int width, out int height);
                return (jpeg, width, height, cssW, cssH, BridgeTools.Str(after, "href"), BridgeTools.Str(after, "title"));
            }
            throw new ToolProblem("The page kept moving, so a picture with its private parts covered could not be made. Try again in a moment.");
        }

        // A frame for a recording: smaller, with what was done written along the bottom and a ring where it was done.
        public static byte[] Frame(byte[] jpeg, string label, PointF? at)
        {
            using (var memory = new MemoryStream(jpeg))
            using (var source = Image.FromStream(memory))
            {
                double scale = Math.Min(1, 960.0 / source.Width);
                int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
                using (var frame = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(frame))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawImage(source, 0, 0, w, h);
                        if (at.HasValue)
                        {
                            float x = (float)(at.Value.X * scale), y = (float)(at.Value.Y * scale);
                            using (var ring = new Pen(Color.FromArgb(230, 217, 119, 87), 3))
                                g.DrawEllipse(ring, x - 13, y - 13, 26, 26);
                        }
                        if (!string.IsNullOrEmpty(label))
                        {
                            using (var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var band = new SolidBrush(Color.FromArgb(190, 20, 20, 24)))
                            {
                                var size = g.MeasureString(label, font);
                                g.FillRectangle(band, 0, h - size.Height - 8, Math.Min(w, size.Width + 16), size.Height + 8);
                                g.DrawString(label, font, Brushes.White, 8, h - size.Height - 4);
                            }
                        }
                    }
                    using (var output = new MemoryStream())
                    using (var parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                        frame.Save(output, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
                        return output.ToArray();
                    }
                }
            }
        }
    }

    // An animated GIF from pictures. WPF's encoder makes the frames (each with a palette of its own, which looks far
    // better than one fixed palette); it writes them without timing and without looping, so each frame gets a
    // "graphic control" block with its delay, and the file the block that makes viewers loop.
    static class GifMaker
    {
        [MethodImpl(MethodImplOptions.NoInlining)]   // WPF is only loaded when a recording is saved
        public static byte[] Make(IList<byte[]> frames, IList<int> delays)
        {
            var encoder = new System.Windows.Media.Imaging.GifBitmapEncoder();
            foreach (var jpeg in frames)
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(jpeg),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad));
            using (var memory = new MemoryStream())
            {
                encoder.Save(memory);
                return Animate(memory.ToArray(), delays);
            }
        }

        static byte[] Animate(byte[] gif, IList<int> delays)
        {
            if (gif.Length < 14 || Encoding.ASCII.GetString(gif, 0, 3) != "GIF") return gif;
            var o = new MemoryStream();
            o.Write(Encoding.ASCII.GetBytes("GIF89a"), 0, 6);
            o.Write(gif, 6, 7);   // the screen descriptor
            int p = 13;
            int flags = gif[10];
            if ((flags & 0x80) != 0) { int size = 3 << ((flags & 7) + 1); o.Write(gif, p, size); p += size; }
            var loop = new List<byte> { 0x21, 0xFF, 0x0B };
            loop.AddRange(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            loop.AddRange(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
            o.Write(loop.ToArray(), 0, loop.Count);
            int frame = 0;
            while (p < gif.Length)
            {
                byte b = gif[p];
                if (b == 0x3B) break;
                if (b == 0x21 && p + 1 < gif.Length)
                {
                    byte label = gif[p + 1];
                    int q = p + 2;
                    while (q < gif.Length && gif[q] != 0) q += gif[q] + 1;
                    q++;
                    if (label != 0xF9 && label != 0xFF) o.Write(gif, p, Math.Min(q, gif.Length) - p);   // its own timing and loop blocks are replaced
                    p = q;
                    continue;
                }
                if (b == 0x2C && p + 10 <= gif.Length)
                {
                    int delay = frame < delays.Count ? Math.Max(2, delays[frame]) : 100;
                    o.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04, (byte)(delay & 0xFF), (byte)((delay >> 8) & 0xFF), 0x00, 0x00 }, 0, 8);
                    int q = p + 10;
                    int local = gif[p + 9];
                    if ((local & 0x80) != 0) q += 3 << ((local & 7) + 1);
                    q++;   // the smallest code size
                    while (q < gif.Length && gif[q] != 0) q += gif[q] + 1;
                    q++;
                    o.Write(gif, p, Math.Min(q, gif.Length) - p);
                    p = q;
                    frame++;
                    continue;
                }
                break;
            }
            o.WriteByte(0x3B);
            return o.ToArray();
        }
    }
}
