# Changelog

## 1.3.0
- Project knowledge base: add documents (txt, md, csv, json, html, docx, pptx, xlsx, pdf, UTF-16 / Windows-1256 text) to a project; Persian-aware BM25 search with optional embeddings, automatic context, a search tool for the model, sources under the answer, incremental sync of a folder on the computer
- Chats of one project can be searched and read by the model; read-only, jailed folder tools (list / read files of the project folder)
- Local server (Windows): OpenAI-compatible API (`/v1/models`, `/v1/chat/completions`, streaming) and an MCP endpoint (ask_ai, list / read conversations, list projects, search project knowledge) on 127.0.0.1 with a bearer token
- MCP apps catalog: ready entries with fill-in fields, export of your servers as `mcpServers` JSON
- Appearance: accent and bubble colours, bubble shape, spacing, more themes; configurable keyboard shortcuts (computer)
- Favourite models, recent models, quick model switch (Ctrl+K) and a system prompt per model
- Chat tags with a filter above the chat list, tag management (rename / merge / delete) and chat templates (persona, API + model, tools, memory, project, extra prompt, tags)
- Two-model chat: the second model can use tools too
- Menus and dialogs: composer menus open above their own button, searchable pickers instead of native lists, chat menu stays on screen
- Mobile: thinking / tools boxes can be scrolled while streaming, no flash of an empty screen at start, computer-only settings are hidden, compare dialog fits phones

## 1.2.0
- Tools (function calling) for OpenAI-compatible APIs, Claude and Gemini: date & time, calculator, web search (model-controlled count / pages / time range, limits), read page; on by default; all calls of an answer in one collapsible box like the thinking box
- Code execution tool: built-in JavaScript sandbox (Windows + Android) and, on Windows, Python / PowerShell / CMD / Node.js / C# script or your own programs; per-language switch, ask-before-run, clean environment, limits; the question is a dialog with the code shown
- MCP client: remote servers (Streamable HTTP) and local programs (stdio, Windows); tool list, per-tool switches, ask-before-run, secrets in secure storage, JSON import; dead servers no longer slow down messages
- Long-term memory: remembered facts across chats, tools to remember / forget, memory suggestions from a chat, per-chat / persona override, import / export
- Automatic summary of long chats (model-only), editable summary banner, "summarize now"
- Personas: system prompt, generation settings, API / model, tools and memory per persona; welcome-screen chips; import / export
- Prompt library: searchable picker and editor for the `/commands`
- Sidebar: select many chats to delete or move to a project
- Settings: search box (Persian + English) that opens and highlights the setting; tools settings regrouped into collapsible groups
- Composer: web search, tools and prompts in one compact menu (more room to type)
- Web search: one retry after temporary failures, clearer errors for the model, tool failures written to the log
- Model test: no default rate limit (automatic handling); per-model rate-limit handling, "continue test" button, configurable limits
- Fixes: "node already has a parent" in multi-round tool calls; empty answers after tool results; answers that arrived only as "reasoning" (some DeepSeek versions); tool lines not grouped after a thinking block; save buttons that ignored clicks until a field lost focus

## 1.1.0
- Connectors: any HTTP API as an image / video / audio / chat provider; async jobs with progress and cancel; 14 templates; add from URL or catalog with a trust preview
- Web search (DuckDuckGo, Brave, SearXNG, custom) with optional page reading and sources
- Collapsible thinking box for reasoning models
- Token and cost estimates, generation settings (global / API / chat)
- Gallery of generated media; seed read-back and re-use
- App lock (PIN), backup / restore
- Settings reorganised: Model answers, Web search, Tokens & cost, Security & privacy
- Android: content no longer slides under the status / navigation bar; language picker buttons fixed
- Windows installer installs the WebView2 Runtime when it is missing
- Unit tests + CI, release workflow (installer + APK)

## 1.0.0
- First release: multi-provider chat (OpenAI-compatible, Claude, Gemini), projects, attachments, Persian / English UI
