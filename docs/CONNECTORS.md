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
    "bodyType": "json",                             // json (default) | form | text | none
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
              "statusPath": "$.status", "done": ["succeeded"], "failed": ["failed"], "errorPath": "$.error" },
    "response": { "type": "json", "results": [ { "from": "$.output.url", "as": "url" } ] }
  }
}
```

## Placeholders
`{{prompt}}`, `{{model}}`, `{{key}}`, `{{seed}}` (random number), `{{system}}`, `{{job}}`,
`{{image_base64}}` / `{{image_dataurl}}` / `{{image_mime}}` (first image attached to your message),
`{{input.NAME}}`, and inside `urlTemplate`: `{{item}}`, `{{item.field}}`. Add `|url` to URL-encode (`{{item.name|url}}`).

* A value that is exactly one placeholder keeps its type (numbers stay numbers).
* A field whose placeholder has no value (e.g. no image attached) is removed from the request.

## JSON paths
`$.a.b`, `$.list[0].x`, `$.list[*]`, `$.*.outputs.*`, `$['odd-name']`.

## Results
`as: "base64"` (plain base64 or a `data:` URI), `as: "url"` (downloaded; the key is only sent to the API's own host), `as: "text"`.
Without a job, `"response": {"type":"binary"}` saves the response body. With a job and no `statusPath`,
polling ends when the `results` paths first match (used for ComfyUI `/history/{id}`).
Images open in the viewer, video/audio play inside the chat, other files (e.g. `.glb`) have a save button.

Not supported (yet): multipart uploads, streaming results, scripting.
