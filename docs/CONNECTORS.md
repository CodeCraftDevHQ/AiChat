# Custom connectors

Choose **API type → Custom connector (JSON)** in Settings → API, pick a template and edit it.
A connector is one JSON file that tells the app how to talk to an HTTP API and where the result is.
Every chat message you send becomes `{{prompt}}`; results (image / video / audio / any file) are added to the reply.

```jsonc
{
  "kind": "image",                                  // informational
  "auth": { "type": "bearer" },                     // bearer | header (+name) | query (+name) | none — uses the API key field
  "headers": { "Accept": "image/png" },             // optional, sent with every request
  "models": { "static": ["a", "b"] },               // or { "path": "/models", "method": "GET", "list": "$.data[*].id" }
  "inputs": [                                       // settings fields shown under the editor -> {{input.id}}
    { "id": "size", "label": "Size", "type": "select", "options": ["512x512", "1024x1024"], "default": "1024x1024" }
    // types: text | number | select | bool
  ],
  "request": {
    "method": "POST", "path": "/v1/generate",       // relative to Base URL (or a full http(s) URL)
    "query": { "a": "b" },
    "headers": { },
    "bodyType": "json",                             // json (default) | form | multipart | text | none
    "body": { "model": "{{model}}", "prompt": "{{prompt}}", "size": "{{input.size}}" }
  },
  "response": {
    "type": "json",                                 // binary = the body itself is the file
    "text": "$.message",                            // optional text shown in the chat
    "results": [
      { "from": "$.data[*].b64_json", "as": "base64", "mime": "image/png" },
      { "from": "$.data[*].url",      "as": "url" }, // optional "urlTemplate": "/view?file={{item.name|url}}"
      { "from": "$.caption",          "as": "text" }
    ]
  },
  "job": {                                          // optional: async APIs (video, 3D, ComfyUI ...)
    "id": "$.id",
    "poll": { "method": "GET", "path": "/jobs/{{job}}", "intervalSec": 5, "timeoutSec": 900,
              "statusPath": "$.status", "done": ["succeeded"], "failed": ["failed"], "errorPath": "$.error",
              "progressPath": "$.progress", "progressMax": 100 },   // optional: shows "running · 42% · 0:35" while waiting
    "cancel": { "method": "POST", "path": "/jobs/{{job}}/cancel" },  // optional: sent when you press Stop
    "response": { "type": "json", "results": [ { "from": "$.output.url", "as": "url" } ] }
  }
}
```

## Placeholders
`{{prompt}}`, `{{model}}`, `{{key}}`, `{{seed}}` (random number), `{{system}}`, `{{job}}`,
`{{image1_base64}}` / `{{image2_base64}}` ... , `{{imageN_dataurl}}`, `{{imageN_mime}}` (the images attached to your message, in order; `image_*` without a number = first), `{{images_base64}}` (array of all),
`{{input.NAME}}`, and inside `urlTemplate`: `{{item}}`, `{{item.field}}`. Add `|url` to URL-encode (`{{item.name|url}}`).

* A value that is exactly one placeholder keeps its type (numbers stay numbers).
* A field whose placeholder has no value (e.g. no image attached) is removed from the request.
* An object (or array item) with `"$if": "{{input.lora}}"` is removed when that value is empty, `false` or missing:
  `"loras": [ { "$if": "{{input.lora}}", "model_name": "{{input.lora}}", "weight": "{{input.lora_weight}}" } ]`.

## JSON paths
`$.a.b`, `$.list[0].x`, `$.list[*]`, `$.*.outputs.*`, `$['odd-name']`.

## Results
`as: "base64"` (plain base64 or a `data:` URI), `as: "url"` (downloaded; the key is only sent to the API's own host), `as: "text"`.
Without a job, `"response": {"type":"binary"}` saves the response body. With a job and no `statusPath`,
polling ends when the `results` paths first match (used for ComfyUI `/history/{id}`).
Images open in the viewer, video/audio play inside the chat, other files (e.g. `.glb`) have a save button.

## Multipart uploads
`"bodyType": "multipart"`: every body field becomes a form field; a field written as
`{ "file": "{{image1_base64}}", "mime": "{{image1_mime}}", "filename": "image.png" }` is sent as a file
(skipped when the image is missing). Example: the Fooocus "Describe image" template.

In a reply, hover an image and press ↪ to use it as the input of your next message (upscale, vary, ...).

Not supported: scripting.

## Chat connectors (`"kind": "chat"`)
For chat APIs that are not OpenAI/Anthropic/Gemini compatible. The conversation is available as
`{{messages}}` (OpenAI-style `[{role, content}]`), `{{messages_with_system}}` (system prompt first),
`{{messages_vision}}` / `{{messages_vision_with_system}}` (images as `image_url` parts) and `{{system}}`.

```jsonc
"stream": {
  "format": "sse",                              // sse (default) | ndjson | none (one JSON answer)
  "textPath": "$.choices[0].delta.content",     // text in every event (required)
  "reasoningPath": "$.choices[0].delta.reasoning_content", // optional: shown as a quoted "thinking" block
  "errorPath": "$.error.message"                // optional: shown as an error
}
```
A chat connector appears as a normal chat provider (model list, model test, attachments as text/images).

## Controls for normal APIs (sliders)
In Settings → API → Advanced settings you can define **Controls** (same format as connector `inputs`, plus
`"type": "slider"` with `min` / `max` / `step`) and use them in the extra body parameters:

```json
Controls:   [ { "id": "temperature", "label": "Temperature", "type": "slider", "min": 0, "max": 2, "step": 0.1, "default": "0.7" } ]
Extra body: { "temperature": "{{input.temperature}}" }
```

## Slash shortcuts
Settings → Chat → Shortcuts. A JSON array; typing `/` at the start of a message suggests them.
`{{text}}` is replaced by whatever you typed after the command:

```json
[ { "name": "translate", "description": "Translate to English", "prompt": "Translate to English:\n\n{{text}}" } ]
```
