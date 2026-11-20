# Changelog

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
