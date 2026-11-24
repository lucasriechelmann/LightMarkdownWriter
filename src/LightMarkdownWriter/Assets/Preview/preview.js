'use strict';

(() => {
    const content = document.getElementById('content');
    const base = document.getElementById('base');
    const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');
    const post = (message) => window.chrome.webview.postMessage(message);

    // ---------- Rendering ----------

    // Rendered diagrams keyed by theme + source, so typing elsewhere doesn't re-run Mermaid for every diagram.
    const svgCache = new Map();
    let generation = 0;
    let diagramId = 0;

    const mermaidTheme = () => (darkQuery.matches ? 'dark' : 'default');
    const initMermaid = () =>
        mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: mermaidTheme() });

    initMermaid();
    hljs.configure({ ignoreUnescapedHTML: true });

    function highlightCode(root) {
        // Like GitHub: only highlight fenced blocks that name a language the bundle knows.
        root.querySelectorAll('pre > code[class*="language-"]').forEach((el) => {
            const lang = [...el.classList].find((c) => c.startsWith('language-'))?.slice(9);
            if (lang && hljs.getLanguage(lang)) hljs.highlightElement(el);
        });
    }

    async function renderDiagrams(gen) {
        const theme = mermaidTheme();
        for (const el of content.querySelectorAll('.mermaid')) {
            if (gen !== generation) return; // a newer render started; abandon this one
            const source = el.dataset.src ?? el.textContent;
            el.dataset.src = source;
            const key = theme + '\n' + source;

            let svg = svgCache.get(key);
            if (svg === undefined) {
                const id = 'mermaid-' + ++diagramId;
                try {
                    ({ svg } = await mermaid.render(id, source));
                    svgCache.set(key, svg);
                } catch (err) {
                    document.getElementById(id)?.remove();
                    document.getElementById('d' + id)?.remove();
                    if (gen !== generation) return;
                    const pre = document.createElement('pre');
                    pre.className = 'mermaid-error';
                    pre.textContent = 'Mermaid: ' + (err?.message ?? err);
                    el.replaceChildren(pre);
                    continue;
                }
                if (gen !== generation) return;
            }
            el.innerHTML = svg;
        }
        if (svgCache.size > 200) svgCache.clear();
        reapplySync(); // diagrams change block heights
    }

    function render(message) {
        const gen = ++generation;
        base.href = message.baseHref || 'https://app.local/';
        totalLines = Math.max(1, message.totalLines || 1);
        content.innerHTML = message.html;
        highlightCode(content);
        collectAnchors();
        scrollToLine(message.line ?? syncLine, message.edge ?? syncEdge);
        renderDiagrams(gen);
    }

    // ---------- Scroll sync ----------
    // Every block carries data-line (0-based source line). Positions between blocks are interpolated.

    let anchors = [];          // [{ line, el }] in source order, one per line
    let totalLines = 1;
    let syncLine = 0;          // last synced position (fractional source line)
    let syncEdge = null;       // 'top' | 'bottom' | null
    let ignoreScrollUntil = 0; // suppresses the scroll events our own scrollTo() causes
    let scrollQueued = false;

    function collectAnchors() {
        anchors = [];
        let last = -1;
        for (const el of content.querySelectorAll('[data-line]')) {
            const line = Number(el.dataset.line);
            if (line > last && el.offsetParent !== null) { // keep the outermost element per line, skip hidden
                anchors.push({ line, el });
                last = line;
            }
        }
    }

    const maxScroll = () => document.documentElement.scrollHeight - window.innerHeight;

    // Anchor positions plus virtual start/end points so the whole document is covered.
    function points() {
        const list = anchors.map((a) => ({ line: a.line, y: a.el.getBoundingClientRect().top + window.scrollY }));
        if (!list.length || list[0].line > 0) list.unshift({ line: 0, y: 0 });
        list.push({ line: totalLines, y: Math.max(document.documentElement.scrollHeight, list[list.length - 1].y) });
        return list;
    }

    function lineToY(line) {
        const p = points();
        for (let i = 0; i < p.length - 1; i++) {
            const a = p[i], b = p[i + 1];
            if (line < b.line) {
                const t = b.line === a.line ? 0 : (line - a.line) / (b.line - a.line);
                return a.y + (b.y - a.y) * Math.max(0, t);
            }
        }
        return maxScroll();
    }

    function yToLine(y) {
        const p = points();
        for (let i = 0; i < p.length - 1; i++) {
            const a = p[i], b = p[i + 1];
            if (y < b.y) {
                const t = b.y === a.y ? 0 : (y - a.y) / (b.y - a.y);
                return a.line + (b.line - a.line) * Math.max(0, t);
            }
        }
        return totalLines;
    }

    function scrollToLine(line, edge) {
        syncLine = line;
        syncEdge = edge;
        const y = edge === 'top' ? 0 : edge === 'bottom' ? maxScroll() : lineToY(line);
        if (Math.abs(window.scrollY - y) < 1) return;
        ignoreScrollUntil = performance.now() + 150;
        window.scrollTo(0, y);
    }

    const reapplySync = () => scrollToLine(syncLine, syncEdge);

    window.addEventListener('scroll', () => {
        if (performance.now() < ignoreScrollUntil || scrollQueued) return;
        scrollQueued = true;
        requestAnimationFrame(() => {
            scrollQueued = false;
            const y = window.scrollY;
            syncEdge = y <= 1 ? 'top' : y >= maxScroll() - 1 ? 'bottom' : null;
            syncLine = yToLine(y);
            post({ type: 'scroll', line: syncLine, edge: syncEdge });
        });
    });

    // Late-loading images change heights; keep the synced position in place.
    content.addEventListener('load', reapplySync, true);
    window.addEventListener('resize', reapplySync);

    // ---------- Export ----------
    // Works on a copy of the rendered content, always in the light theme (exports are for sharing/printing).

    // github.com light syntax colors (from hljs-github.min.css), for inline styles in clipboard HTML.
    const hljsLight = {
        'hljs-keyword': '#d73a49', 'hljs-doctag': '#d73a49', 'hljs-type': '#d73a49', 'hljs-template-tag': '#d73a49',
        'hljs-template-variable': '#d73a49', 'hljs-title': '#6f42c1', 'hljs-attr': '#005cc5', 'hljs-attribute': '#005cc5',
        'hljs-literal': '#005cc5', 'hljs-meta': '#005cc5', 'hljs-number': '#005cc5', 'hljs-operator': '#005cc5',
        'hljs-variable': '#005cc5', 'hljs-selector-attr': '#005cc5', 'hljs-selector-class': '#005cc5',
        'hljs-selector-id': '#005cc5', 'hljs-section': '#005cc5', 'hljs-string': '#032f62', 'hljs-regexp': '#032f62',
        'hljs-built_in': '#e36209', 'hljs-symbol': '#e36209', 'hljs-comment': '#6a737d', 'hljs-code': '#6a737d',
        'hljs-formula': '#6a737d', 'hljs-name': '#22863a', 'hljs-quote': '#22863a', 'hljs-selector-tag': '#22863a',
        'hljs-selector-pseudo': '#22863a', 'hljs-bullet': '#735c0f', 'hljs-addition': '#22863a', 'hljs-deletion': '#b31d28',
    };

    const mono = 'Consolas, "Cascadia Mono", "Courier New", monospace';
    const border = '#d1d9e0';

    // Email clients and Word ignore <style> blocks in pasted HTML; only inline styles survive.
    function inlineStyles(root) {
        const set = (selector, css) => root.querySelectorAll(selector).forEach((el) => el.style.cssText += css);
        root.style.cssText = 'font-family: "Segoe UI", -apple-system, sans-serif; font-size: 14px; line-height: 1.5; color: #1f2328;';
        set('h1, h2', `border-bottom: 1px solid ${border}; padding-bottom: .3em;`);
        set('pre', `background: #f6f8fa; padding: 12px 16px; border-radius: 6px; font-family: ${mono}; font-size: 13px; line-height: 1.45; white-space: pre-wrap; color: #1f2328;`);
        set('pre code', `font-family: ${mono}; background: transparent; padding: 0;`);
        root.querySelectorAll('code').forEach((el) => {
            if (!el.closest('pre')) el.style.cssText += `font-family: ${mono}; font-size: 85%; background: #eff1f3; padding: .2em .4em; border-radius: 6px;`;
        });
        set('table', 'border-collapse: collapse; margin-bottom: 16px;');
        set('th, td', `border: 1px solid ${border}; padding: 6px 13px;`);
        set('th', 'background: #f6f8fa; font-weight: 600;');
        set('blockquote', `border-left: 4px solid ${border}; padding: 0 1em; color: #59636e; margin: 0 0 16px 0;`);
        set('a', 'color: #0969da;');
        set('img', 'max-width: 100%;');

        // GitHub alerts (> [!NOTE] etc.), with github.com's light colors.
        const alertColors = { note: '#0969da', tip: '#1a7f37', important: '#8250df', warning: '#9a6700', caution: '#d1242f' };
        root.querySelectorAll('.markdown-alert').forEach((el) => {
            const kind = [...el.classList].find((c) => c.startsWith('markdown-alert-'))?.slice(15);
            const color = alertColors[kind] ?? '#59636e';
            el.style.cssText += `border-left: 4px solid ${color}; padding: 8px 16px; margin: 0 0 16px 0;`;
            const title = el.querySelector('.markdown-alert-title');
            if (title) {
                title.style.cssText += `color: ${color}; font-weight: 600; margin: 0 0 4px 0;`;
                title.querySelector('svg')?.setAttribute('fill', color); // where SVG is shown at all
            }
        });
        root.querySelectorAll('pre [class*="hljs-"]').forEach((el) => {
            const color = [...el.classList].map((c) => hljsLight[c]).find(Boolean);
            if (color) el.style.color = color;
            if (el.classList.contains('hljs-section') || el.classList.contains('hljs-strong')) el.style.fontWeight = '700';
            if (el.classList.contains('hljs-emphasis')) el.style.fontStyle = 'italic';
        });
    }

    // Rasterize an SVG (for clipboard: Outlook/Word don't render inline SVG). 2x for sharpness.
    function svgToPng(svgText) {
        const doc = new DOMParser().parseFromString(svgText, 'image/svg+xml');
        const svg = doc.documentElement;
        const [, , w, h] = (svg.getAttribute('viewBox') || '0 0 600 400').split(/[\s,]+/).map(Number);
        svg.setAttribute('width', w);
        svg.setAttribute('height', h);
        svg.style.maxWidth = '';
        const source = new XMLSerializer().serializeToString(svg);
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => {
                const scale = 2;
                const canvas = document.createElement('canvas');
                canvas.width = Math.ceil(w * scale);
                canvas.height = Math.ceil(h * scale);
                const ctx = canvas.getContext('2d');
                ctx.fillStyle = '#ffffff';
                ctx.fillRect(0, 0, canvas.width, canvas.height);
                ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
                resolve({ src: canvas.toDataURL('image/png'), width: Math.round(w) });
            };
            img.onerror = () => reject(new Error('could not rasterize diagram'));
            img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(source);
        });
    }

    async function exportContent(forClipboard) {
        const clone = content.cloneNode(true);

        // Diagrams: light theme; plain SVG text labels (foreignObject HTML labels would taint the canvas).
        const diagrams = [...clone.querySelectorAll('.mermaid')];
        if (diagrams.length) {
            mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default', htmlLabels: false, flowchart: { htmlLabels: false } });
            try {
                for (const el of diagrams) {
                    const source = el.dataset.src ?? el.textContent;
                    const id = 'export-' + ++diagramId;
                    try {
                        const { svg } = await mermaid.render(id, source);
                        if (forClipboard) {
                            const png = await svgToPng(svg);
                            el.innerHTML = `<img src="${png.src}" width="${png.width}" alt="diagram">`;
                        } else {
                            el.innerHTML = svg;
                        }
                    } catch (err) {
                        document.getElementById(id)?.remove();
                        document.getElementById('d' + id)?.remove();
                        el.innerHTML = '';
                        const pre = document.createElement('pre');
                        pre.textContent = source;
                        el.replaceWith(pre);
                    }
                }
            } finally {
                initMermaid(); // back to the live preview's theme and settings
            }
        }

        // Front matter is metadata: shown in the preview, left out of exports.
        clone.querySelectorAll('.front-matter').forEach((el) => el.remove());

        // Internal bookkeeping attributes don't belong in exported HTML.
        clone.querySelectorAll('[data-line], [data-src], [data-highlighted]').forEach((el) => {
            el.removeAttribute('data-line');
            el.removeAttribute('data-src');
            el.removeAttribute('data-highlighted');
        });
        clone.removeAttribute('style'); // the live preview's accent override

        if (!forClipboard) return clone.innerHTML;

        // Clipboard: a self-styled <div> fragment (base font/color inline, since there's no stylesheet).
        inlineStyles(clone);
        const wrapper = document.createElement('div');
        wrapper.style.cssText = clone.style.cssText;
        wrapper.innerHTML = clone.innerHTML;
        return wrapper.outerHTML;
    }

    // ---------- Links, theme, messaging ----------

    // With <base> pointing at the document folder, "#anchor" would navigate away; scroll in-page instead.
    document.addEventListener('click', (e) => {
        const link = e.target.closest('a[href^="#"]');
        if (!link) return;
        e.preventDefault();
        const id = decodeURIComponent(link.getAttribute('href').slice(1));
        document.getElementById(id)?.scrollIntoView({ block: 'start' });
    });

    darkQuery.addEventListener('change', () => {
        initMermaid();
        renderDiagrams(++generation);
    });

    function applyTheme(message) {
        document.documentElement.style.setProperty('--page-bg', message.background);
        // github-markdown-css colors links with --fgColor-accent; use the Windows accent instead.
        if (message.accent) content.style.setProperty('--fgColor-accent', message.accent);
    }

    window.chrome.webview.addEventListener('message', (e) => {
        const m = e.data;
        switch (m?.type) {
            case 'render': render(m); break;
            case 'scrollToLine': scrollToLine(m.line, m.edge); break;
            case 'theme': applyTheme(m); break;
            case 'export':
                exportContent(m.forClipboard).then(
                    (html) => post({ type: 'exportResult', id: m.id, html }),
                    (err) => post({ type: 'exportResult', id: m.id, error: String(err?.message ?? err) }));
                break;
        }
    });

    post({ type: 'ready' });
})();
