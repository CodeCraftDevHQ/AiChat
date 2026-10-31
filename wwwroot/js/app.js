window.chat = {
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

    isDark() {
        const t = document.documentElement.getAttribute('data-theme');
        if (t === 'dark') return true;
        if (t === 'light') return false;
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
        const MAX = 6 * 1024 * 1024;
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
        el.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && !window.chat.isCoarse()) {
                e.preventDefault();
                dotnet.invokeMethodAsync('OnEnter');
            }
        });

        if (!window.chat.isCoarse()) setTimeout(() => el.focus(), 50);
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
