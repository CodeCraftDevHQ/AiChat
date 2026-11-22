namespace AiChat.Services;

/// <summary>Text the user can paste into any AI assistant to get a connector JSON for their API.</summary>
public static class ConnectorGuide
{
    public const string AiPrompt = """"
You write "connector" JSON files for the app "AI Chat". A connector describes how to call an HTTP API (chat, image, video, audio, 3D, ...) and where the result is in the response.

TASK
I will give you information about an API (documentation text, a curl / PowerShell / Python example, or just the service name).
Reply with ONE valid JSON object (no comments, no trailing commas) in a single code block, then 2-4 short lines telling me what to fill in
(Base URL, API key, model names). If something essential is missing, ask me first. Never invent endpoints you are not sure about.

FORMAT (all keys optional except "request" and a way to read the result)
{
  "kind": "chat | image | video | audio | 3d | text | file",
  "_help": "one sentence telling the user what this connector does and what to prepare",
  "auth": { "type": "bearer | header | query | none", "name": "header or query parameter name (for header/query)" },
  "headers": { "Accept": "image/png" },
  "models": { "static": ["a", "b"] }   OR   { "path": "/models", "method": "GET", "list": "$.data[*].id" },
  "allowEmptyPrompt": false,
  "inputs": [ { "id": "size", "label": "Size", "type": "text | number | slider | select | bool", "options": ["..."], "min": 0, "max": 1, "step": 0.1, "default": "1024x1024" } ],
  "request": {
    "method": "POST", "path": "/v1/generate", "query": { "a": "b" }, "headers": { },
    "bodyType": "json (default) | form | multipart | text | none",
    "body": { "model": "{{model}}", "prompt": "{{prompt}}", "size": "{{input.size}}" }
  },
  "response": {
    "type": "binary | json",
    "mime": "optional, e.g. video/mp4",
    "text": "$.message",
    "results": [ { "from": "$.data[*].b64_json", "as": "base64 | url | text", "mime": "image/png", "urlTemplate": "/view?file={{item.name|url}}" } ]
  },
  "job": {                                    // only for asynchronous APIs: submit, then poll
    "id": "$.id",
    "poll": { "method": "GET", "path": "/jobs/{{job}}", "intervalSec": 5, "timeoutSec": 900,
              "statusPath": "$.status", "done": ["succeeded"], "failed": ["failed"], "errorPath": "$.error" },
    "response": { "type": "json", "results": [ { "from": "$.output.url", "as": "url", "mime": "video/mp4" } ] }
  },
  "stream": {                                 // only when kind is "chat"
    "format": "sse | ndjson | none", "textPath": "$.choices[0].delta.content",
    "reasoningPath": "$.choices[0].delta.reasoning_content", "errorPath": "$.error.message"
  }
}

PLACEHOLDERS (inside any string of request / headers / path / query)
{{prompt}} = the user's message; {{model}} = chosen model; {{key}} = API key; {{seed}} = random integer; {{system}} = system prompt;
{{input.ID}} = value of an input field; {{job}} = job id (poll requests);
{{image1_base64}}, {{image2_base64}}, ... = images attached to the message (raw base64), {{imageN_dataurl}}, {{imageN_mime}};
{{file1_base64}}, {{video1_base64}}, {{audio1_base64}}, ... = any attached file / the first video / audio (also _mime, _name, _dataurl, and _text = the content of a text or JSON file);
{{prompt|http}} = the message, but only when it is a web address (otherwise the field is removed);
{{messages}} / {{messages_with_system}} / {{messages_vision}} = chat history as OpenAI-style [{role, content}] (chat connectors);
inside urlTemplate: {{item}} and {{item.field}} = the selected result node; add |url to URL-encode, e.g. {{item.name|url}}.

RULES
- A value that is exactly one placeholder keeps its type: "steps": "{{input.steps}}" becomes a number when the input is type number or slider.
- A field whose placeholder has no value (for example no image attached) is removed from the request automatically.
- An object or array item with "$if": "{{input.x}}" is removed when x is empty / false / missing.
- Multipart file field: "image": { "file": "{{image1_base64}}", "mime": "{{image1_mime}}", "filename": "image.png" }; for a video: "motion_video": { "file": "{{video1_base64}}", "mime": "{{video1_mime}}", "filename": "{{video1_name}}" }.
- A result rule may have "ext": "fbx" (or "{{input.format}}") to set the file extension of generic files.
- JSON paths: $.a.b, $.list[0].x, $.list[*], $.*.outputs.*, $['odd-name'] (no filters).
- "response.type": "binary" saves the raw response body (image / video / audio / model file); the content type decides the file type.
- "as": "url" downloads the URL (relative URLs use the Base URL; the key is only sent to the API's own host); "base64" accepts raw base64 or a data: URI.
- Without "statusPath", polling ends when the "results" paths first match.
- Use the user's real values from the documentation (paths, header names, field names). Put every setting the user may want to change into "inputs".
- Put the API's base address in the answer notes (the app has a separate Base URL field); request paths are relative to it.

EXAMPLE 1 (image, base64 in JSON)
{
  "kind": "image", "_help": "OpenAI image generation.", "auth": { "type": "bearer" },
  "models": { "static": ["gpt-image-1"] },
  "inputs": [ { "id": "size", "label": "Size", "type": "select", "options": ["1024x1024", "1536x1024"], "default": "1024x1024" } ],
  "request": { "method": "POST", "path": "/images/generations", "body": { "model": "{{model}}", "prompt": "{{prompt}}", "size": "{{input.size}}" } },
  "response": { "type": "json", "results": [ { "from": "$.data[*].b64_json", "as": "base64", "mime": "image/png" } ] }
}

EXAMPLE 2 (chat, streamed NDJSON)
{
  "kind": "chat", "auth": { "type": "none" },
  "models": { "path": "/api/tags", "method": "GET", "list": "$.models[*].name" },
  "request": { "method": "POST", "path": "/api/chat", "body": { "model": "{{model}}", "messages": "{{messages_with_system}}", "stream": true } },
  "stream": { "format": "ndjson", "textPath": "$.message.content", "errorPath": "$.error" }
}

NOW WRITE THE CONNECTOR FOR THIS API:
(paste the documentation / example request / service name here)
"""";
}
