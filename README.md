# AI Chat

[فارسی](README.fa.md)

A ChatGPT-style chat app for **Windows** and **Android**, built with C# / .NET 10 (.NET MAUI Blazor Hybrid).
Add the API keys of several AI services, switch between them and their models in one click, and keep all your chats on your own device.

## Features

**Providers**
- OpenAI-compatible APIs: OpenAI, OpenRouter, DeepSeek, Groq, Mistral, xAI, Together, Ollama, LM Studio, and anything with a `/chat/completions` endpoint
- Anthropic (Claude) and Google Gemini
- Several APIs at once, a model list per API (fetched automatically or typed by hand), and the last model of each API is remembered
- "Test models": sends a tiny message to each model to find out which ones really work, with automatic rate-limit handling

**Chat**
- Streaming answers, Markdown, code blocks with copy / save buttons, RTL (Persian) and LTR text
- Edit sent messages (versions are kept, switch with ‹ n/N ›), regenerate, stop
- Pin and rename chats, search inside a chat, search all chats (names or full text), question rail for long chats
- Projects: shared instructions and reference files for a group of chats

**Files**
- Attach images, text/code files and PDFs (drag & drop, paste, file picker; camera on phones)
- PDFs can be sent as extracted text (any provider) or as the original file (Claude, Gemini)
- Images and files produced by the model can be viewed and downloaded
- If a model rejects images, they are excluded automatically so the chat keeps working

**App**
- Persian and English interface, dark / light / system theme
- Appearance settings: text size, content width, line spacing, font
- In-app log viewer (API keys and message text are never logged)
- Android back button navigates one level up

## Privacy

- API keys are stored in the platform's secure storage (Android Keystore / Windows credential store) and never written to the data files.
- Chats, projects and attachments are stored locally in the app data folder. Nothing is sent anywhere except the requests you make to the AI services you configured.

## Build

Requirements: Visual Studio 2022/2026 with the **.NET Multi-platform App UI** workload and the **.NET 10 SDK**.

```bash
git clone <your-repo-url>
cd AiChat
dotnet build -f net10.0-windows10.0.19041.0     # Windows
dotnet build -f net10.0-android                 # Android
```

Or open `AiChat.csproj` in Visual Studio, pick *Windows Machine* or an Android device/emulator and press F5.

## Using it

1. Open **Settings → APIs → Add API**, choose a preset (or *OpenAI-compatible* with your own Base URL), paste the key, fetch the models and save.
2. Pick the API and the model from the top bar and start chatting.
3. Local models: use the Ollama / LM Studio presets (on Android use your computer's IP instead of `localhost`).

## Project layout

| Folder | Content |
| --- | --- |
| `Services/` | Providers (OpenAI-compatible, Anthropic, Gemini), `ChatState`, storage, logging, markdown, translations |
| `Components/` | Razor UI (chat, sidebar, settings, dialogs) |
| `Models/` | Data classes |
| `wwwroot/` | CSS and JavaScript of the web UI |
| `Platforms/` | Android and Windows specifics |

## Adding a language

UI texts use the Persian text as the key (`L["..."]`). Add the new translations in `Services/Translations.cs`.

## License

[MIT](LICENSE)

## Releases

Push a tag (`git tag v1.0.1 && git push origin v1.0.1`) and the *Release* workflow builds the Windows installer and the Android APK and attaches them to a GitHub Release. Optional secrets for a stable Android signing key: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`.
