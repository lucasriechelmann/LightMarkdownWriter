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
        }
    });

    post({ type: 'ready' });
})();
