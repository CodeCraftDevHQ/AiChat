window.chat = {
    // theme: "system" | "light" | "dark"
    setTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme || 'system');
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

// One delegated handler for every code-block "copy" button (the blocks are re-rendered while streaming).
document.addEventListener('click', async (e) => {
    const btn = e.target.closest && e.target.closest('.code-copy');
    if (!btn) return;
    const code = btn.parentElement.querySelector('code');
    const ok = await window.chat.copyText(code ? code.innerText : '');
    btn.textContent = ok ? 'کپی شد ✓' : 'ناموفق';
    setTimeout(() => { btn.textContent = 'کپی'; }, 1500);
});
