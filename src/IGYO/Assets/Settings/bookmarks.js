// IGYO — Settings > Bookmarks: the bookmark manager.
// Folders on the left, the chosen folder's contents on the right. Every change is done by the app
// (bm/* in SettingsPage.cs); afterwards the tree is read again, keeping the chosen folder and the scroll position.
(() => {
  'use strict';

  const ST = window.ST;
  const tr = window.IGYO.tr;
  const { h, icon, api, onSync, onLeave, notice, settled, dialog, confirm, menu, header, button } = ST;

  ST.section('bookmarks', {
    fill: true,   // the two panes take the window's height and scroll inside
    render(page) {
      let roots = [];
      let hasChrome = false;
      let chosen = 0;              // the folder whose contents are listed
      let filter = '';
      let dragged = 0;
      let lastTree = '';
      let watching = 0;
      const expanded = new Set();  // folders opened in the tree
      const byId = new Map();
      const parentOf = new Map();

      const plural = (n) => (n === 1 ? tr('1 bookmark') : tr('{0} bookmarks', n));
      const links = (node) => (node.children ? node.children.reduce((n, c) => n + links(c), 0) : 1);
      const pathOf = (node) => { const path = []; for (let n = node; n; n = parentOf.get(n.id)) path.unshift(n); return path; };
      // True when "folder" is "node" itself or somewhere inside it (a folder can't be moved into itself).
      const inside = (node, folder) => { for (let n = folder; n; n = parentOf.get(n.id)) if (n === node) return true; return false; };

      header(page, tr('Bookmarks'), tr('Drag a row to change its place, or onto a folder to move it there.'));
      const search = h('input', { type: 'search', class: 't-input st-bm-filter', placeholder: tr('Search all bookmarks'), 'aria-label': tr('Search all bookmarks'), spellcheck: false });
      const tree = h('div', { class: 't-card st-bm-tree', role: 'tree', 'aria-label': tr('Folders') });
      const crumbs = h('div', { class: 'st-bm-crumbs' });
      const list = h('div', { class: 'st-bm-list', role: 'list' });
      page.append(
        h('div', { class: 'st-bm-bar' },
          h('label', { class: 'st-bm-search' }, h('span', { html: icon('search') }), search),
          button(tr('New folder'), newFolder, { icon: 'folder-plus' }),
          button(tr('Add bookmark'), addBookmark, { icon: 'plus' }),
          button(tr('Import'), (node) => menu(node, [
            hasChrome ? { label: tr('From Google Chrome…'), run: () => importFrom('bm/import-chrome') } : null,
            { label: tr('From a file (HTML)…'), run: () => importFrom('bm/import-file') },
          ]), { icon: 'download' }),
          button(tr('Export'), async () => settled(await api.post('bm/export')), { icon: 'upload', title: tr('Export to a file (HTML)') })),
        h('div', { class: 'st-bm' }, tree, h('div', { class: 't-card st-bm-pane' }, crumbs, list)));

      // ----- reading -----

      // Reads the tree; draws it again only when something changed. True when it did.
      async function load() {
        let data;
        try { data = await api.get('/api/bookmarks'); } catch { return false; }
        if (!list.isConnected) return false;
        const json = JSON.stringify(data);
        if (json === lastTree) return false;
        const first = !lastTree;
        lastTree = json;
        roots = data.roots || [];
        hasChrome = !!data.chrome;
        byId.clear();
        parentOf.clear();
        const walk = (node, parent) => { byId.set(node.id, node); parentOf.set(node.id, parent); (node.children || []).forEach((c) => walk(c, node)); };
        roots.forEach((r) => walk(r, null));
        if (!byId.has(chosen) || !byId.get(chosen).children) chosen = roots[0] ? roots[0].id : 0;
        if (first) roots.forEach((r) => expanded.add(r.id));
        draw();
        return true;
      }

      function draw() {
        const top = [tree.scrollTop, list.scrollTop];
        const focused = document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.bm : null;
        tree.replaceChildren(...roots.map((r) => treeNode(r, 0)));
        drawList();
        [tree.scrollTop, list.scrollTop] = top;
        const again = focused && page.querySelector(`[data-bm="${focused}"]`);
        if (again) again.focus({ preventScroll: true });
      }

      function choose(id) {
        chosen = id;
        filter = '';
        search.value = '';
        for (const folder of pathOf(byId.get(id))) expanded.add(folder.id);
        draw();
        list.scrollTop = 0;
      }

      // ----- the folder tree -----

      function treeNode(folder, depth) {
        const kids = folder.children.filter((c) => c.children);
        const open = expanded.has(folder.id);
        const twist = h('button', { type: 'button', class: 'st-tree-twist' + (open ? ' is-open' : '') + (kids.length ? '' : ' is-leaf'), tabIndex: -1, 'aria-label': open ? tr('Close the folder') : tr('Open the folder'), html: icon('chevron-right') });
        const line = h('div', {
          class: 'st-tree-row' + (folder.id === chosen && !filter ? ' is-chosen' : ''), role: 'treeitem', tabIndex: 0, 'data-bm': 't' + folder.id,
          'aria-selected': String(folder.id === chosen), 'aria-expanded': kids.length ? String(open) : null, vars: { '--depth': String(depth) },
        }, twist, h('span', { class: 'st-tree-icon', html: icon('folder') }), h('span', { class: 'st-tree-name', text: folder.title || tr('Folder') }), h('span', { class: 'st-tree-count', text: String(links(folder)) }));
        const flip = (to) => { if (to) expanded.add(folder.id); else expanded.delete(folder.id); draw(); };
        twist.addEventListener('click', (event) => { event.stopPropagation(); flip(!open); });
        line.addEventListener('click', () => choose(folder.id));
        line.addEventListener('keydown', (event) => {
          const rows = [...tree.querySelectorAll('.st-tree-row')];
          const at = rows.indexOf(line);
          if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); choose(folder.id); }
          else if (event.key === 'ArrowDown' && rows[at + 1]) { event.preventDefault(); rows[at + 1].focus(); }
          else if (event.key === 'ArrowUp' && rows[at - 1]) { event.preventDefault(); rows[at - 1].focus(); }
          else if (event.key === 'ArrowRight' && kids.length && !open) { event.preventDefault(); flip(true); }
          else if (event.key === 'ArrowLeft' && open && kids.length) { event.preventDefault(); flip(false); }
        });
        // A dragged row can be dropped on a folder here to move it into that folder.
        line.addEventListener('dragover', (event) => {
          if (!dragged || inside(byId.get(dragged), folder)) return;
          event.preventDefault();
          event.dataTransfer.dropEffect = 'move';
          clearMarks();
          line.classList.add('is-drop-into');
        });
        line.addEventListener('dragleave', () => line.classList.remove('is-drop-into'));
        line.addEventListener('drop', (event) => {
          if (!dragged || inside(byId.get(dragged), folder)) return;
          event.preventDefault();
          const id = dragged;
          clearMarks();
          move(id, folder.id);
        });
        const node = h('div', { class: 'st-tree-node', role: 'none' }, line);
        if (open) for (const kid of kids) node.append(treeNode(kid, depth + 1));
        return node;
      }

      // ----- the list -----

      function clearMarks() {
        for (const node of page.querySelectorAll('.is-drop-before, .is-drop-after, .is-drop-into')) node.classList.remove('is-drop-before', 'is-drop-after', 'is-drop-into');
      }

      function picture(node) {
        if (node.children) return h('span', { class: 'st-bm-icon', html: icon('folder') });
        if (!node.icon) return h('span', { class: 'st-bm-icon', html: icon('globe') });
        const image = h('img', { class: 'st-bm-icon', alt: '', src: node.icon });
        image.addEventListener('error', () => image.replaceWith(h('span', { class: 'st-bm-icon', html: icon('globe') })));
        return image;
      }

      const tool = (name, label, run) => h('button', { type: 'button', class: 't-iconbtn st-bm-tool', title: label, 'aria-label': label, html: icon(name), onclick: (event) => { event.stopPropagation(); run(); } });

      // position: the row's place in its folder (for reordering); where: its folder's path, when the row is a search result.
      function listRow(node, position, where) {
        const folder = !!node.children;
        const line = h('div', {
          class: 'st-bm-row' + (folder ? ' is-folder' : ''), role: 'listitem', tabIndex: 0, 'data-bm': String(node.id), draggable: !where,
          title: folder ? node.title : `${node.title}\n${node.url}`,
        },
          where ? null : h('span', { class: 'st-bm-grip', 'aria-hidden': 'true', html: icon('grip') }),
          picture(node),
          h('span', { class: 'st-bm-title', text: node.title || (folder ? tr('Folder') : node.url) }),
          h('span', { class: 'st-bm-url', text: (folder ? plural(links(node)) : node.url) + (where ? '  ·  ' + tr('in {0}', where) : '') }),
          h('span', { class: 'st-bm-tools' },
            where ? tool('folder', tr('Show in its folder'), () => choose(parentOf.get(node.id).id)) : null,
            tool('edit', tr('Edit'), () => edit(node)),
            tool('trash', tr('Delete'), () => remove(node))));
        line.addEventListener('dblclick', (event) => { if (!event.target.closest('button')) open(node); });
        line.addEventListener('keydown', (event) => {
          if (event.target !== line) return;
          const rows = [...list.querySelectorAll('.st-bm-row')];
          const at = rows.indexOf(line);
          if (event.key === 'Enter') { event.preventDefault(); open(node); }
          else if (event.key === 'F2') { event.preventDefault(); edit(node); }
          else if (event.key === 'Delete') { event.preventDefault(); remove(node); }
          else if (event.altKey && !where && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
            // The keyboard's way to reorder: the new place is counted with the row still where it is.
            event.preventDefault();
            const to = event.key === 'ArrowUp' ? position - 1 : position + 2;
            if (to >= 0 && to <= byId.get(chosen).children.length) move(node.id, chosen, to);
          } else if (event.key === 'ArrowDown' && rows[at + 1]) { event.preventDefault(); rows[at + 1].focus(); }
          else if (event.key === 'ArrowUp' && rows[at - 1]) { event.preventDefault(); rows[at - 1].focus(); }
        });
        if (where) return line;

        line.addEventListener('dragstart', (event) => {
          dragged = node.id;
          event.dataTransfer.effectAllowed = 'move';
          event.dataTransfer.setData('text/plain', node.url || node.title || '');
          requestAnimationFrame(() => line.classList.add('is-dragging'));
        });
        line.addEventListener('dragend', () => { dragged = 0; line.classList.remove('is-dragging'); clearMarks(); });
        line.addEventListener('dragover', (event) => {
          if (!dragged || dragged === node.id) return;
          event.preventDefault();
          event.dataTransfer.dropEffect = 'move';
          const box = line.getBoundingClientRect();
          const at = (event.clientY - box.top) / box.height;
          // The middle of a folder's row means "into it"; the edges of any row mean "before" or "after".
          const into = folder && at > 0.28 && at < 0.72;
          clearMarks();
          line.classList.add(into ? 'is-drop-into' : at < 0.5 ? 'is-drop-before' : 'is-drop-after');
        });
        line.addEventListener('drop', (event) => {
          if (!dragged || dragged === node.id) return;
          event.preventDefault();
          event.stopPropagation();
          const id = dragged;
          const into = line.classList.contains('is-drop-into');
          const after = line.classList.contains('is-drop-after');
          clearMarks();
          if (into) move(id, node.id);
          else move(id, chosen, position + (after ? 1 : 0));
        });
        return line;
      }

      function drawList() {
        const folder = byId.get(chosen);
        if (!folder) { crumbs.replaceChildren(); list.replaceChildren(); return; }
        if (filter) {
          const found = [];
          for (const node of byId.values()) {
            if (roots.includes(node)) continue;
            if (`${node.title}\n${node.url || ''}`.toLowerCase().includes(filter)) found.push(node);
          }
          crumbs.replaceChildren(h('span', { class: 'st-bm-count', text: found.length ? tr('{0} found', found.length) : '' }));
          list.replaceChildren(...found.map((node) => listRow(node, -1, pathOf(parentOf.get(node.id)).map((p) => p.title).join(' › '))));
          if (!found.length) list.append(h('p', { class: 'st-note st-pad', text: tr('No bookmarks match "{0}".', search.value.trim()) }));
          return;
        }
        const path = pathOf(folder);
        crumbs.replaceChildren(
          ...path.flatMap((p, i) => [
            i ? h('span', { class: 'st-bm-sep', 'aria-hidden': 'true', html: icon('chevron-right') }) : null,
            i === path.length - 1 ? h('span', { class: 'st-bm-here', text: p.title }) : h('button', { type: 'button', class: 't-btn is-ghost is-s', text: p.title, onclick: () => choose(p.id) }),
          ]).filter(Boolean),
          h('span', { class: 'st-bm-count', text: plural(links(folder)) }));
        list.replaceChildren(...folder.children.map((node, i) => listRow(node, i, '')));
        if (!folder.children.length) list.append(h('p', { class: 'st-note st-pad', text: tr('This folder is empty.') }));
      }

      // Dropping on the empty part of the list puts the row at the end of the folder.
      list.addEventListener('dragover', (event) => { if (dragged && event.target === list) { event.preventDefault(); clearMarks(); } });
      list.addEventListener('drop', (event) => {
        if (!dragged || event.target !== list) return;
        event.preventDefault();
        move(dragged, chosen);
      });

      search.addEventListener('input', () => { filter = search.value.trim().toLowerCase(); draw(); });
      search.addEventListener('keydown', (event) => { if (event.key === 'Escape' && search.value) { event.preventDefault(); search.value = ''; filter = ''; draw(); } });

      // ----- doing -----

      function open(node) {
        if (node.children) choose(node.id);
        else api.post('bm/open', { id: node.id }).then((r) => settled(r));
      }

      // index: the place among the folder's rows as they are now (the app allows for the row leaving its old place); none means the end.
      async function move(id, folder, index) {
        const body = { id, folder };
        if (index != null) body.index = index;
        settled(await api.post('bm/move', body));
        await load();
      }

      async function edit(node) {
        const folder = !!node.children;
        await dialog({
          title: folder ? tr('Edit folder') : tr('Edit bookmark'), ok: tr('Save'),
          fields: [{ key: 'title', label: tr('Name'), value: node.title, max: 200 }].concat(folder ? [] : [{ key: 'url', label: tr('Address'), value: node.url, required: true }]),
          submit: async (values) => (await api.post('bm/edit', { id: node.id, title: values.title, url: values.url || '' })).error,
        });
        load();
      }

      async function remove(node) {
        const count = node.children ? links(node) : 0;
        if (count && !await confirm(tr('Delete the folder "{0}"?', node.title), count === 1 ? tr("The 1 bookmark in it is deleted too. This can't be undone.") : tr("The {0} bookmarks in it are deleted too. This can't be undone.", count), tr('Delete'), true)) return;
        settled(await api.post('bm/delete', { id: node.id }), node.children ? tr('Deleted the folder "{0}".', node.title) : tr('Deleted "{0}".', node.title));
        load();
      }

      async function newFolder() {
        await dialog({
          title: tr('New folder'), ok: tr('Create'),
          message: tr('In "{0}".', (byId.get(chosen) || {}).title || ''),
          fields: [{ key: 'title', label: tr('Name'), value: tr('New folder'), max: 200, required: true }],
          submit: async (values) => (await api.post('bm/add-folder', { parent: chosen, title: values.title })).error,
        });
        load();
      }

      async function addBookmark() {
        await dialog({
          title: tr('Add bookmark'), ok: tr('Add'),
          message: tr('In "{0}".', (byId.get(chosen) || {}).title || ''),
          fields: [{ key: 'title', label: tr('Name'), max: 200 }, { key: 'url', label: tr('Address'), placeholder: 'example.com', required: true }],
          submit: async (values) => (await api.post('bm/add', { parent: chosen, title: values.title, url: values.url })).error,
        });
        load();
      }

      // The app asks which file or Chrome profile in windows of its own and doesn't say when it is done,
      // so the tree is looked at again for a while (and whenever the page gets the focus back).
      async function importFrom(command) {
        if (!settled(await api.post(command))) return;
        clearInterval(watching);
        const until = Date.now() + 120000;
        watching = setInterval(async () => { if (Date.now() > until || await load()) clearInterval(watching); }, 1500);
      }

      const look = () => { if (!document.hidden) load(); };
      window.addEventListener('focus', look);
      document.addEventListener('visibilitychange', look);
      onLeave(() => {
        clearInterval(watching);
        window.removeEventListener('focus', look);
        document.removeEventListener('visibilitychange', look);
      });
      onSync(() => { load(); });   // also when the app says something changed
    },
  });
})();
