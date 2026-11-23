window.chat = {
    attachMedia: async (el, streamRef, mime) => {
        if (!el) return;
        const buf = await streamRef.arrayBuffer();
        const url = URL.createObjectURL(new Blob([buf], { type: mime || 'application/octet-stream' }));
        if (el.dataset.blob) URL.revokeObjectURL(el.dataset.blob);
        el.dataset.blob = url;
        el.src = url;
    },
    releaseMedia: (el) => {
        if (el && el.dataset && el.dataset.blob) { URL.revokeObjectURL(el.dataset.blob); delete el.dataset.blob; }
    },
    // theme: "system" | "light" | "dark"
    setTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme || 'system');
    },

    // appearance: font size (px), content width (px, 0 = full), line height (percent), font key
    setUi(fs, w, lh, font) {
        const s = document.documentElement.style;
        const fonts = {
            tahoma: 'Tahoma, "Segoe UI", sans-serif',
            serif: 'Georgia, "Times New Roman", "Noto Naskh Arabic", serif',
            mono: '"Cascadia Code", Consolas, monospace'
        };
        s.setProperty('--ui-fs', fs + 'px');
        s.setProperty('--ui-w', w > 0 ? w + 'px' : '100%');
        s.setProperty('--ui-lh', String(lh / 100));
        if (fonts[font]) s.setProperty('--ui-font', fonts[font]); else s.removeProperty('--ui-font');
    },

    setLang(lang) {
        document.documentElement.setAttribute('lang', lang || 'fa');
    },

    // look: CSS variables (accent colour, bubbles, spacing); a null value removes the variable
    setStyle(json) {
        const v = JSON.parse(json);
        const s = document.documentElement.style;
        for (const k in v) { if (v[k] === null) s.removeProperty(k); else s.setProperty(k, v[k]); }
    },

    // keyboard shortcuts: { action: "Ctrl+Shift+O", ... }
    setKeys(json) {
        const map = {};
        const v = JSON.parse(json);
        for (const a in v) if (v[a]) map[v[a]] = a;
        window.chat.keyMap = map;
    },

    focusEl(el) { if (el) el.focus(); },

    isDark() {
        const t = document.documentElement.getAttribute('data-theme');
        if (t === 'dark' || t === 'amoled' || t === 'midnight' || t === 'nord') return true;
        if (t === 'light' || t === 'sepia') return false;
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    isNarrow() {
        return window.matchMedia('(max-width: 768px)').matches;
    },

    isCoarse() {
        return window.matchMedia('(pointer: coarse)').matches;
    },

    // Sends dropped / pasted files to .NET as base64 (one call per file).
    async sendFiles(files, dotnet) {
        const MAX = 200 * 1024 * 1024; // the real limits are enforced (and configurable) in the app
        for (const f of files) {
            if (f.size > MAX) {
                await dotnet.invokeMethodAsync('OnFileTooBig', f.name);
                continue;
            }
            const b64 = await new Promise((resolve, reject) => {
                const r = new FileReader();
                r.onload = () => resolve(String(r.result).split(',')[1] || '');
                r.onerror = () => reject(r.error);
                r.readAsDataURL(f);
            });
            await dotnet.invokeMethodAsync('OnFileDropped', f.name || 'image.png', b64);
        }
    },

    // Drag & drop anywhere on the window shows an overlay; dropping adds the files to the message.
    initDrop(dotnet) {
        if (window.__dropInit) return;
        window.__dropInit = true;

        const hasFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
        let depth = 0;

        window.addEventListener('dragenter', (e) => {
            if (!hasFiles(e)) return;
            e.preventDefault();
            depth++;
            document.body.classList.add('dragging');
        });
        window.addEventListener('dragover', (e) => {
            if (!hasFiles(e)) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
        });
        window.addEventListener('dragleave', (e) => {
            if (!hasFiles(e)) return;
            depth = Math.max(0, depth - 1);
            if (depth === 0) document.body.classList.remove('dragging');
        });
        window.addEventListener('drop', (e) => {
            if (!hasFiles(e)) return;
            e.preventDefault();
            depth = 0;
            document.body.classList.remove('dragging');
            window.chat.sendFiles(Array.from(e.dataTransfer.files), dotnet);
        });
    },

    // Enter = send (desktop), Shift+Enter = new line. On touch devices Enter is always a new line.
    initComposer(el, dotnet) {
        if (!el || el.__init) return;
        el.__init = true;
        window.chat.dotnet = dotnet; // used by the delegated click handlers below

        // Ctrl+V with an image / file in the clipboard attaches it (plain text pastes normally).
        el.addEventListener('paste', (e) => {
            const files = e.clipboardData && e.clipboardData.files ? Array.from(e.clipboardData.files) : [];
            if (files.length === 0) return;
            e.preventDefault();
            window.chat.sendFiles(files, dotnet);
        });

        const resize = () => {
            el.style.height = 'auto';
            el.style.height = Math.min(el.scrollHeight, 200) + 'px';
        };
        el.__resize = resize;
        el.addEventListener('input', resize);
        // "/" at the start: ask the app for matching shortcuts (only while it matters)
        el.addEventListener('input', () => {
            const v = el.value;
            const want = v.startsWith('/') && !/\s/.test(v);
            if (want || el.__slash) {
                el.__slash = want;
                dotnet.invokeMethodAsync('OnComposerInput', want ? v : '');
            }
        });
        el.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && !window.chat.isCoarse()) {
                e.preventDefault();
                dotnet.invokeMethodAsync('OnEnter');
            }
        });

        if (!window.chat.isCoarse()) setTimeout(() => el.focus(), 50);
    },

    // Right-click menu (Cut / Copy / Paste / Select all) for desktop; touch devices use the system menu.
    initContextMenu(dotnet, labels) {
        window.chat.ctxLabels = labels;
        if (window.__ctxInit) return;
        window.__ctxInit = true;

        let menu = null;
        const close = () => { if (menu) { menu.remove(); menu = null; } };

        document.addEventListener('contextmenu', (e) => {
            if (window.chat.isCoarse()) return;
            const t = e.target;
            if (!(t instanceof Element)) return;
            if (t.closest('a, button, summary, video, audio, .att-use')) return;

            const editable = t.closest('textarea, input:not([type=checkbox]):not([type=radio]):not([type=range]):not([type=file]), [contenteditable=true]');
            const hasSel = editable && 'selectionStart' in editable
                ? editable.selectionStart !== editable.selectionEnd
                : String(window.getSelection() || '').length > 0;
            const readOnly = editable && (editable.readOnly || editable.disabled);

            e.preventDefault();
            close();
            const L = window.chat.ctxLabels || {};
            const items = [];
            if (editable && !readOnly) items.push([L.cut || 'Cut', hasSel, () => document.execCommand('cut')]);
            items.push([L.copy || 'Copy', hasSel, () => document.execCommand('copy')]);
            if (editable && !readOnly) items.push([L.paste || 'Paste', true, async () => {
                const text = await dotnet.invokeMethodAsync('OnPasteRequest');
                if (!text) return;
                editable.focus();
                if (!document.execCommand('insertText', false, text)) {
                    const s = editable.selectionStart, en = editable.selectionEnd;
                    editable.setRangeText(text, s, en, 'end');
                    editable.dispatchEvent(new Event('input', { bubbles: true }));
                }
            }]);
            items.push([L.all || 'Select all', true, () => {
                if (editable && 'select' in editable) editable.select();
                else {
                    const root = t.closest('.message, .msg, .bubble, .md, pre, p, li, td, div') || document.body;
                    window.getSelection().selectAllChildren(root);
                }
            }]);

            menu = document.createElement('div');
            menu.className = 'ctx-menu';
            for (const [label, enabled, action] of items) {
                const b = document.createElement('button');
                b.type = 'button';
                b.textContent = label;
                b.disabled = !enabled;
                b.addEventListener('mousedown', (ev) => ev.preventDefault()); // keep the focus / selection
                b.addEventListener('click', async () => { close(); try { await action(); } catch { } });
                menu.appendChild(b);
            }
            document.body.appendChild(menu);
            const r = menu.getBoundingClientRect();
            menu.style.left = Math.max(4, Math.min(e.clientX, window.innerWidth - r.width - 4)) + 'px';
            menu.style.top = Math.max(4, Math.min(e.clientY, window.innerHeight - r.height - 4)) + 'px';
        });
        document.addEventListener('mousedown', (e) => { if (menu && !menu.contains(e.target)) close(); }, true);
        document.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); }, true);
        window.addEventListener('blur', close);
        window.addEventListener('resize', close);
        document.addEventListener('scroll', close, true);
    },

    setCtxLabels(labels) { window.chat.ctxLabels = labels; },

    setValue(el, v) {
        if (!el) return;
        el.value = v;
        if (el.__resize) el.__resize();
        el.focus();
        el.setSelectionRange(v.length, v.length);
    },

    takeValue(el) {
        const v = el.value;
        el.value = '';
        if (el.__resize) el.__resize();
        if (!window.chat.isCoarse()) el.focus();
        return v;
    },

    // Tracks which question is at the top of the chat and whether the chat is scrolled to the bottom.
    initRail(dotnet) {
        const el = document.getElementById('messages');
        if (!el || el.__rail) return;
        el.__rail = true;

        let ticking = false, lastActive = -2, lastBottom = true;

        const check = () => {
            ticking = false;
            const top = el.getBoundingClientRect().top;
            const users = el.querySelectorAll('.msg.user');
            let active = -1;
            for (const u of users) {
                if (u.getBoundingClientRect().top - top <= 80) active = parseInt(u.dataset.idx, 10);
                else break;
            }
            if (active === -1 && users.length) active = parseInt(users[0].dataset.idx, 10);
            if (active !== lastActive) {
                lastActive = active;
                dotnet.invokeMethodAsync('OnActiveQuestion', active);
            }

            const bottom = el.scrollHeight - el.scrollTop - el.clientHeight < 120;
            if (bottom !== lastBottom) {
                lastBottom = bottom;
                dotnet.invokeMethodAsync('OnAtBottom', bottom);
            }
        };

        el.__check = () => {
            if (!ticking) { ticking = true; requestAnimationFrame(check); }
        };
        el.addEventListener('scroll', el.__check, { passive: true });
    },

    // Smoothly scroll to a message and flash it.
    scrollToMsg(idx) {
        const target = document.getElementById('m-' + idx);
        const el = document.getElementById('messages');
        if (!target || !el) return;
        el.__stick = false;
        target.scrollIntoView({ block: 'start', behavior: 'smooth' });
        target.classList.add('flash');
        setTimeout(() => target.classList.remove('flash'), 1400);
    },

    scrollToBottom(id) {
        const el = document.getElementById(id);
        if (!el) return;
        el.__stick = true;
        el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
    },

    // Follow the stream, but only while the user is at (or near) the bottom.
    scroll(id, force) {
        const el = document.getElementById(id);
        if (!el) return;
        if (!el.__scrollInit) {
            el.__scrollInit = true;
            el.__stick = true;
            el.addEventListener('scroll', () => {
                el.__stick = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
            }, { passive: true });
        }
        if (window.chat.restoreThink) window.chat.restoreThink();
        if (force || el.__stick !== false) {
            el.scrollTop = el.scrollHeight;
            el.__stick = true;
        }
        if (el.__check) el.__check();
    },

    async copyText(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            let ok = false;
            try { ok = document.execCommand('copy'); } catch (_) { }
            document.body.removeChild(ta);
            return ok;
        }
    }
};

const EXT = {
    python: 'py', py: 'py', csharp: 'cs', cs: 'cs', javascript: 'js', js: 'js', typescript: 'ts', ts: 'ts',
    json: 'json', html: 'html', css: 'css', xml: 'xml', yaml: 'yml', yml: 'yml', sql: 'sql',
    bash: 'sh', sh: 'sh', shell: 'sh', powershell: 'ps1', ps1: 'ps1', java: 'java', cpp: 'cpp', c: 'c',
    go: 'go', rust: 'rs', php: 'php', markdown: 'md', md: 'md', razor: 'razor', kotlin: 'kt',
    swift: 'swift', ruby: 'rb', csv: 'csv', ini: 'ini', toml: 'toml', dockerfile: 'dockerfile'
};

// Delegated handlers: code blocks and images in answers are re-rendered while streaming,
// so listeners are attached once on the document.
document.addEventListener('click', async (e) => {
    const t = e.target;
    if (!t || !t.closest) return;

    // click on an image inside an answer -> open the viewer (with download)
    const img = t.closest('.md-img');
    if (img && window.chat.dotnet) {
        window.chat.dotnet.invokeMethodAsync('OnOpenImage', img.currentSrc || img.src, img.alt || 'image');
        return;
    }

    // save a code block as a file
    const save = t.closest('.code-save');
    if (save && window.chat.dotnet) {
        const pre = save.parentElement;
        const code = pre.querySelector('code');
        const lang = (pre.getAttribute('data-lang') || '').toLowerCase();
        const ext = EXT[lang] || 'txt';
        window.chat.dotnet.invokeMethodAsync('OnSaveText', 'code.' + ext, code ? code.innerText : '');
        return;
    }

    if (!t.closest('.code-copy')) return;
    handleCopy(t.closest('.code-copy'));
});

async function handleCopy(btn) {
    const code = btn.parentElement.querySelector('code');
    const ok = await window.chat.copyText(code ? code.innerText : '');
    const orig = btn.getAttribute('data-orig') || btn.textContent;
    btn.setAttribute('data-orig', orig);
    btn.textContent = ok ? (btn.dataset.done || 'Copied') : (btn.dataset.fail || 'Failed');
    setTimeout(() => { btn.textContent = orig; }, 1500);
}

// Report uncaught JavaScript errors to the in-app log.
window.addEventListener('error', (e) => {
    if (window.chat.dotnet) window.chat.dotnet.invokeMethodAsync('OnJsError', `${e.message} (${e.filename}:${e.lineno})`);
});
window.addEventListener('unhandledrejection', (e) => {
    if (window.chat.dotnet) window.chat.dotnet.invokeMethodAsync('OnJsError', 'Unhandled promise rejection: ' + (e.reason && e.reason.message || e.reason));
});

document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && window.chat.dotnet && !e.defaultPrevented) {
        window.chat.dotnet.invokeMethodAsync('OnEscape');
    }
});

// ===================== search inside the chat =====================
// Matches are highlighted with the CSS Custom Highlight API, which does not touch the DOM
// (Blazor owns the DOM, so wrapping matches in <mark> would break its diffing).
(function () {
    const S = { ranges: [], cur: -1 };

    // Persian/Arabic-friendly normalisation. Returns the normalised text and, for every
    // normalised character, the index of the original character (for building DOM ranges).
    function normalize(text) {
        let out = '';
        const map = [];
        for (let i = 0; i < text.length; i++) {
            let c = text[i];
            const code = c.charCodeAt(0);
            if (code === 0x200c || code === 0x200d || code === 0x200e || code === 0x200f || code === 0x0640 ||
                (code >= 0x064b && code <= 0x065f) || code === 0x0670) continue;      // ZWNJ, tatweel, diacritics
            if (c === 'ي' || c === 'ى') c = 'ی';
            else if (c === 'ك') c = 'ک';
            else if (c === 'ة') c = 'ه';
            else if (c === 'أ' || c === 'إ' || c === 'ٱ') c = 'ا';
            else if (code >= 0x06f0 && code <= 0x06f9) c = String(code - 0x06f0);      // Persian digits
            else if (code >= 0x0660 && code <= 0x0669) c = String(code - 0x0660);      // Arabic-Indic digits
            for (const x of c.toLowerCase()) { out += x; map.push(i); }
        }
        return { out, map };
    }

    function textNodes() {
        const root = document.querySelector('#messages .thread');
        if (!root) return [];
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
            acceptNode(n) {
                const p = n.parentElement;
                if (!p || !n.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
                if (p.closest('button, .actions, .user-actions, .model-tag, textarea, .edit-box')) return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
            }
        });
        const list = [];
        while (walker.nextNode()) list.push(walker.currentNode);
        return list;
    }

    function paint() {
        if (window.CSS && CSS.highlights) {
            CSS.highlights.delete('search');
            CSS.highlights.delete('search-current');
            if (S.ranges.length) {
                CSS.highlights.set('search', new Highlight(...S.ranges));
                if (S.cur >= 0) CSS.highlights.set('search-current', new Highlight(S.ranges[S.cur]));
            }
        } else if (S.cur >= 0) {
            const sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(S.ranges[S.cur]);
        }
    }

    function reveal() {
        if (S.cur < 0) return;
        const el = S.ranges[S.cur].startContainer.parentElement;
        const box = document.getElementById('messages');
        if (box) box.__stick = false;
        if (el) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }

    window.chat.searchRun = function (query) {
        S.ranges = [];
        S.cur = -1;
        const q = normalize(query || '').out.trim();
        if (q) {
            for (const node of textNodes()) {
                const { out, map } = normalize(node.nodeValue);
                let from = 0, idx;
                while ((idx = out.indexOf(q, from)) !== -1) {
                    const r = document.createRange();
                    r.setStart(node, map[idx]);
                    r.setEnd(node, map[idx + q.length - 1] + 1);
                    S.ranges.push(r);
                    from = idx + q.length;
                }
            }
        }
        if (S.ranges.length) S.cur = S.ranges.length - 1;   // like Telegram: start from the newest match
        paint();
        reveal();
        return [S.cur + 1, S.ranges.length];
    };

    // delta -1 = older match, +1 = newer match (wraps around)
    window.chat.searchStep = function (delta) {
        const n = S.ranges.length;
        if (n === 0) return [0, 0];
        S.cur = (S.cur + delta + n) % n;
        paint();
        reveal();
        return [S.cur + 1, n];
    };

    window.chat.searchClear = function () {
        S.ranges = [];
        S.cur = -1;
        if (window.CSS && CSS.highlights) {
            CSS.highlights.delete('search');
            CSS.highlights.delete('search-current');
        }
    };
})();

// Ctrl+F opens the in-chat search instead of the browser's find box.
document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === 'f' && window.chat.dotnet) {
        e.preventDefault();
        window.chat.dotnet.invokeMethodAsync('OnFind');
    }
});


// settings search: open the collapsed groups around an element, scroll to it and flash it
window.chat = window.chat || {};
window.chat.gotoSetting = function (id) {
    requestAnimationFrame(function () {
        var el = document.getElementById(id);
        if (!el) return;
        var d = el.closest('details');
        while (d) { d.open = true; d = d.parentElement ? d.parentElement.closest('details') : null; }
        el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        el.classList.remove('set-flash');
        void el.offsetWidth;
        el.classList.add('set-flash');
        setTimeout(function () { el.classList.remove('set-flash'); }, 2200);
    });
};

// ---------------------------------------------------------------- image viewer: zoom, pan, pinch, rotate, copy
(function () {
    const v = { scale: 1, x: 0, y: 0, rot: 0, fit: 1, pts: new Map(), pinch0: 0, scale0: 1, dotnet: null, bound: null };

    function els() { return { stage: document.getElementById('viewer-stage'), img: document.getElementById('viewer-img') }; }

    function apply() {
        const { img } = els();
        if (!img) return;
        img.style.transform = 'translate(' + v.x + 'px,' + v.y + 'px) scale(' + v.scale + ') rotate(' + v.rot + 'deg)';
        const z = document.getElementById('viewer-zoom');
        if (z) z.textContent = Math.round(v.scale * 100) + '%';
        const { stage } = els();
        if (stage) stage.classList.toggle('zoomed', v.scale > 1.01);
    }

    function clamp() {
        const { stage, img } = els();
        if (!stage || !img) return;
        if (v.scale <= 1.001) { v.x = 0; v.y = 0; return; }
        const rotated = (v.rot / 90) % 2 !== 0;
        const w = (rotated ? img.offsetHeight : img.offsetWidth) * v.scale;
        const h = (rotated ? img.offsetWidth : img.offsetHeight) * v.scale;
        const mx = Math.max(0, (w - stage.clientWidth) / 2) + 60;
        const my = Math.max(0, (h - stage.clientHeight) / 2) + 60;
        v.x = Math.max(-mx, Math.min(mx, v.x));
        v.y = Math.max(-my, Math.min(my, v.y));
    }

    function zoomAt(factor, cx, cy) {
        const { stage } = els();
        if (!stage) return;
        const r = stage.getBoundingClientRect();
        const px = (cx === undefined ? r.left + r.width / 2 : cx) - (r.left + r.width / 2);
        const py = (cy === undefined ? r.top + r.height / 2 : cy) - (r.top + r.height / 2);
        const ns = Math.max(1, Math.min(12, v.scale * factor));
        const k = ns / v.scale;
        v.x = px - (px - v.x) * k;
        v.y = py - (py - v.y) * k;
        v.scale = ns;
        clamp();
        apply();
    }

    function reset() { v.scale = 1; v.x = 0; v.y = 0; v.rot = 0; apply(); }

    function bind(stage) {
        if (v.bound === stage) return;
        v.bound = stage;
        stage.addEventListener('wheel', (e) => {
            e.preventDefault();
            zoomAt(e.deltaY < 0 ? 1.15 : 1 / 1.15, e.clientX, e.clientY);
        }, { passive: false });
        stage.addEventListener('pointerdown', (e) => {
            stage.setPointerCapture(e.pointerId);
            v.pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (v.pts.size === 2) {
                const [a, b] = [...v.pts.values()];
                v.pinch0 = Math.hypot(a.x - b.x, a.y - b.y);
                v.scale0 = v.scale;
            }
        });
        stage.addEventListener('pointermove', (e) => {
            const p = v.pts.get(e.pointerId);
            if (!p) return;
            if (v.pts.size === 2) {
                v.pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
                const [a, b] = [...v.pts.values()];
                const d = Math.hypot(a.x - b.x, a.y - b.y);
                if (v.pinch0 > 0) zoomAt((v.scale0 * d / v.pinch0) / v.scale, (a.x + b.x) / 2, (a.y + b.y) / 2);
            } else if (v.scale > 1.001) {
                v.x += e.clientX - p.x;
                v.y += e.clientY - p.y;
                v.pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
                clamp();
                apply();
            }
        });
        const up = (e) => { v.pts.delete(e.pointerId); v.pinch0 = 0; };
        stage.addEventListener('pointerup', up);
        stage.addEventListener('pointercancel', up);
        stage.addEventListener('dblclick', (e) => {
            if (v.scale > 1.01) reset(); else zoomAt(2.5, e.clientX, e.clientY);
        });
    }

    document.addEventListener('keydown', (e) => {
        if (!document.getElementById('viewer-stage')) return;
        if (e.key === '+' || e.key === '=') zoomAt(1.25);
        else if (e.key === '-') zoomAt(0.8);
        else if (e.key === '0') reset();
        else if (e.key === 'r' || e.key === 'R') { v.rot = (v.rot + 90) % 360; clamp(); apply(); }
        else if ((e.key === 'Escape' || e.key === 'ArrowLeft' || e.key === 'ArrowRight') && v.dotnet) {
            e.preventDefault();
            v.dotnet.invokeMethodAsync('OnViewerKey', e.key);
        }
    });

    window.chat = window.chat || {};
    window.chat.viewerInit = function (dotnet) {
        v.dotnet = dotnet;
        const { stage, img } = els();
        if (!stage) return;
        bind(stage);
        reset();
        const info = () => {
            const el = document.getElementById('viewer-info');
            if (el && img.naturalWidth) el.textContent = img.naturalWidth + '×' + img.naturalHeight;
        };
        if (img.complete) info(); else img.addEventListener('load', info, { once: true });
    };
    window.chat.viewerZoom = function (f) { zoomAt(f); };
    window.chat.viewerToggle = function () { if (v.scale > 1.01 || v.rot) reset(); else zoomAt(2.5); };
    window.chat.viewerRotate = function () { v.rot = (v.rot + 90) % 360; clamp(); apply(); };
    window.chat.viewerCopy = async function () {
        try {
            const { img } = els();
            if (!img || !navigator.clipboard || !window.ClipboardItem) return false;
            const blob = await (await fetch(img.src)).blob();
            // the clipboard accepts PNG: convert other types through a canvas
            let out = blob;
            if (blob.type !== 'image/png') {
                const c = document.createElement('canvas');
                c.width = img.naturalWidth; c.height = img.naturalHeight;
                c.getContext('2d').drawImage(img, 0, 0);
                out = await new Promise((r) => c.toBlob(r, 'image/png'));
            }
            await navigator.clipboard.write([new ClipboardItem({ 'image/png': out })]);
            return true;
        } catch (e) { return false; }
    };
})();

// ---------------------------------------------------------------- video controls
window.chat = window.chat || {};
window.chat.mediaSkip = function (el, s) { if (el) el.currentTime = Math.max(0, Math.min(el.duration || 1e9, el.currentTime + s)); };
window.chat.mediaRate = function (el, r) { if (el) el.playbackRate = r; };
window.chat.mediaLoop = function (el, on) { if (el) el.loop = !!on; };
window.chat.mediaPip = async function (el) {
    try {
        if (!el) return;
        if (document.pictureInPictureElement) await document.exitPictureInPicture();
        else if (document.pictureInPictureEnabled) await el.requestPictureInPicture();
    } catch (e) { }
};
window.chat.mediaFull = function (el) {
    try { if (el.requestFullscreen) el.requestFullscreen(); else if (el.webkitEnterFullscreen) el.webkitEnterFullscreen(); } catch (e) { }
};
window.chat.mediaFrame = function (el) {
    try {
        if (!el || !el.videoWidth) return null;
        const c = document.createElement('canvas');
        c.width = el.videoWidth; c.height = el.videoHeight;
        c.getContext('2d').drawImage(el, 0, 0);
        return c.toDataURL('image/png').split(',')[1];
    } catch (e) { return null; }
};
// keyboard on a focused video: space, arrows, F, M, L, < >
document.addEventListener('keydown', (e) => {
    const v = document.activeElement;
    if (!v || v.tagName !== 'VIDEO') return;
    if (e.key === 'ArrowRight') { v.currentTime += 5; e.preventDefault(); }
    else if (e.key === 'ArrowLeft') { v.currentTime -= 5; e.preventDefault(); }
    else if (e.key === 'f' || e.key === 'F') { window.chat.mediaFull(v); e.preventDefault(); }
    else if (e.key === 'm' || e.key === 'M') { v.muted = !v.muted; e.preventDefault(); }
    else if (e.key === 'l' || e.key === 'L') { v.loop = !v.loop; e.preventDefault(); }
    else if (e.key === '>' ) { v.playbackRate = Math.min(4, v.playbackRate + 0.25); }
    else if (e.key === '<' ) { v.playbackRate = Math.max(0.25, v.playbackRate - 0.25); }
});

// export to PDF: the HTML page is printed from a hidden frame (the system print dialog offers "Save as PDF")
window.chat = window.chat || {};
window.chat.printHtml = function (html) {
    return new Promise((resolve) => {
        const f = document.createElement('iframe');
        f.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden';
        f.srcdoc = html;
        f.onload = () => {
            try { f.contentWindow.focus(); f.contentWindow.print(); } catch (e) { }
            setTimeout(() => { f.remove(); resolve(true); }, 2000);
        };
        document.body.appendChild(f);
    });
};

// ---- voice: microphone recording and audio playback
window.chat = window.chat || {};
window.chat.micStart = async function () {
    try {
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !window.MediaRecorder) return 'unsupported';
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        const types = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus'];
        const type = types.find(t => MediaRecorder.isTypeSupported(t));
        const rec = new MediaRecorder(stream, type ? { mimeType: type } : {});
        const m = { stream, rec, chunks: [] };
        rec.ondataavailable = e => { if (e.data && e.data.size) m.chunks.push(e.data); };
        rec.start();
        window.chat._mic = m;
        return '';
    } catch (e) {
        return String((e && (e.message || e.name)) || e);
    }
};
window.chat.micStop = function () {
    return new Promise(resolve => {
        const m = window.chat._mic;
        window.chat._mic = null;
        if (!m) { resolve({ data: '', mime: '' }); return; }
        m.rec.onstop = async () => {
            m.stream.getTracks().forEach(t => t.stop());
            const blob = new Blob(m.chunks, { type: m.rec.mimeType || 'audio/webm' });
            const buf = new Uint8Array(await blob.arrayBuffer());
            let bin = '';
            for (let i = 0; i < buf.length; i += 0x8000) bin += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
            resolve({ data: btoa(bin), mime: blob.type || 'audio/webm' });
        };
        try { m.rec.stop(); } catch (e) { resolve({ data: '', mime: '' }); }
    });
};
window.chat.micCancel = function () {
    const m = window.chat._mic;
    window.chat._mic = null;
    if (!m) return;
    try { m.rec.onstop = null; m.rec.stop(); } catch (e) { }
    m.stream.getTracks().forEach(t => t.stop());
};
window.chat.playAudio = function (b64, mime) {
    return new Promise((resolve, reject) => {
        window.chat.stopAudio();
        const a = new Audio('data:' + mime + ';base64,' + b64);
        window.chat._aud = { a, resolve };
        a.onended = () => { window.chat._aud = null; resolve(); };
        a.onerror = () => { window.chat._aud = null; reject(new Error('audio playback failed')); };
        a.play().catch(err => { window.chat._aud = null; reject(err); });
    });
};
window.chat.stopAudio = function () {
    const x = window.chat._aud;
    window.chat._aud = null;
    if (!x) return;
    try { x.a.pause(); } catch (e) { }
    x.resolve();
};
window.chat.appendValue = function (el, text) {
    if (!el) return;
    const cur = el.value || '';
    el.value = cur + (cur && !/\s$/.test(cur) ? ' ' : '') + text;
    if (el.__resize) el.__resize();
    el.focus();
    el.setSelectionRange(el.value.length, el.value.length);
};


// ---- configurable keyboard shortcuts (same rules as KeyBindings.FromEvent in C#)
(function () {
    const CODES = { Comma: ',', Period: '.', Slash: '/', Semicolon: ';', Quote: "'", BracketLeft: '[', BracketRight: ']', Backslash: '\\', Minus: '-', Equal: '=', Backquote: '`' };
    const NAMES = ['Escape', 'Delete', 'Enter', 'Tab', 'Backspace', 'Home', 'End', 'PageUp', 'PageDown', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Insert'];
    function combo(e) {
        const code = e.code || '';
        let k;
        if (/^Key[A-Z]$/.test(code)) k = code[3];
        else if (/^Digit[0-9]$/.test(code)) k = code[5];
        else if (CODES[code]) k = CODES[code];
        else if (e.key === ' ') k = 'Space';
        else if (e.key && e.key.length === 1) k = e.key.toUpperCase();
        else if (/^F([1-9]|1[0-2])$/.test(e.key)) k = e.key;
        else if (NAMES.indexOf(e.key) >= 0) k = e.key;
        else return null;
        const mods = [];
        const ctrl = e.ctrlKey || e.metaKey;
        if (ctrl) mods.push('Ctrl');
        if (e.altKey) mods.push('Alt');
        if (e.shiftKey) mods.push('Shift');
        if (!ctrl && !e.altKey && !/^F\d+$/.test(k)) return null;
        return mods.concat([k]).join('+');
    }
    document.addEventListener('keydown', (e) => {
        const map = window.chat && window.chat.keyMap;
        if (!map || !window.chat.dotnet) return;
        if (e.target && e.target.closest && e.target.closest('[data-keyrec]')) return;   // the shortcut recorder
        const c = combo(e);
        const a = c && map[c];
        if (!a) return;
        e.preventDefault();
        window.chat.dotnet.invokeMethodAsync('OnShortcut', a);
    }, true);
})();

// While the user scrolls inside a thinking / tools box, the stream must not re-create that box
// (Blazor replaces the markup on every chunk, which kills the touch scroll). The UI is held until the finger
// has been gone for a moment; the scroll position of the box is restored afterwards.
(function () {
    let dn = null, holding = false, timer = 0, last = null;
    const boxOf = t => (t && t.closest) ? t.closest('.think-body') : null;
    const idxOf = b => Array.prototype.indexOf.call(document.querySelectorAll('#messages .think-body'), b);
    function hold(on) {
        if (holding === on) return;
        holding = on;
        if (dn) dn.invokeMethodAsync('OnThinkHold', on).catch(() => { });
    }
    function begin(e) {
        const b = boxOf(e.target);
        if (!b) return;
        clearTimeout(timer);
        last = { idx: idxOf(b), top: b.scrollTop };
        hold(true);
    }
    function later(e) {
        if (!holding) return;
        const b = boxOf(e.target);
        if (b) last = { idx: idxOf(b), top: b.scrollTop };
        clearTimeout(timer);
        timer = setTimeout(() => { window.chat.__thinkRestore = last; hold(false); }, 1200);
    }
    window.chat.initThinkHold = function (ref) {
        dn = ref;
        if (window.__thinkHoldInit) return;
        window.__thinkHoldInit = true;
        document.addEventListener('touchstart', begin, { capture: true, passive: true });
        document.addEventListener('pointerdown', begin, { capture: true, passive: true });
        document.addEventListener('wheel', e => { begin(e); later(e); }, { capture: true, passive: true });
        document.addEventListener('scroll', later, { capture: true, passive: true });
        document.addEventListener('touchend', later, { capture: true, passive: true });
        document.addEventListener('touchcancel', later, { capture: true, passive: true });
        document.addEventListener('pointerup', later, { capture: true, passive: true });
    };
    window.chat.restoreThink = function () {
        const r = window.chat.__thinkRestore;
        if (!r) return;
        window.chat.__thinkRestore = null;
        const b = document.querySelectorAll('#messages .think-body')[r.idx];
        if (b) b.scrollTop = r.top;
    };
})();
