namespace AiChat.Services;

public record ConnectorTemplate(string Name, string BaseUrl, string Json);

/// <summary>Ready-made connector profiles the user can start from and edit.</summary>
public static class ConnectorTemplates
{
    public static readonly ConnectorTemplate[] All =
    {
        new("Fooocus-API (تصویر)", "http://127.0.0.1:8888", """
{
  "kind": "image",
  "auth": { "type": "header", "name": "X-API-KEY" },
  "models": { "path": "/v1/engines/all-models", "method": "GET", "list": "$.model_filenames[*]" },
  "inputs": [
    { "id": "aspect", "label": "Aspect ratio", "type": "select", "options": ["1152*896", "1024*1024", "896*1152", "1344*768", "768*1344"], "default": "1152*896" },
    { "id": "performance", "label": "Performance", "type": "select", "options": ["Speed", "Quality", "Extreme Speed"], "default": "Speed" },
    { "id": "negative", "label": "Negative prompt", "type": "text", "default": "" }
  ],
  "request": {
    "method": "POST",
    "path": "/v1/generation/text-to-image?accept_query=image/png",
    "headers": { "Accept": "image/png" },
    "body": {
      "prompt": "{{prompt}}",
      "negative_prompt": "{{input.negative}}",
      "style_selections": ["Fooocus V2", "Fooocus Enhance", "Fooocus Sharp"],
      "performance_selection": "{{input.performance}}",
      "aspect_ratios_selection": "{{input.aspect}}",
      "image_number": 1,
      "image_seed": -1,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "save_extension": "png",
      "require_base64": false,
      "async_process": false
    }
  },
  "response": { "type": "binary" }
}
"""),
        new("OpenAI Images (تصویر)", "https://api.openai.com/v1", """
{
  "kind": "image",
  "auth": { "type": "bearer" },
  "models": { "static": ["gpt-image-1", "dall-e-3"] },
  "inputs": [
    { "id": "size", "label": "Size", "type": "select", "options": ["1024x1024", "1536x1024", "1024x1536"], "default": "1024x1024" }
  ],
  "request": {
    "method": "POST",
    "path": "/images/generations",
    "body": { "model": "{{model}}", "prompt": "{{prompt}}", "size": "{{input.size}}", "n": 1 }
  },
  "response": {
    "type": "json",
    "results": [
      { "from": "$.data[*].b64_json", "as": "base64", "mime": "image/png" },
      { "from": "$.data[*].url", "as": "url" }
    ]
  }
}
"""),
        new("OpenAI Text-to-Speech (صوت)", "https://api.openai.com/v1", """
{
  "kind": "audio",
  "auth": { "type": "bearer" },
  "models": { "static": ["gpt-4o-mini-tts", "tts-1", "tts-1-hd"] },
  "inputs": [
    { "id": "voice", "label": "Voice", "type": "select", "options": ["alloy", "echo", "fable", "onyx", "nova", "shimmer"], "default": "alloy" }
  ],
  "request": {
    "method": "POST",
    "path": "/audio/speech",
    "body": { "model": "{{model}}", "input": "{{prompt}}", "voice": "{{input.voice}}", "response_format": "mp3" }
  },
  "response": { "type": "binary", "mime": "audio/mpeg" }
}
"""),
        new("AUTOMATIC1111 / Forge (تصویر)", "http://127.0.0.1:7860", """
{
  "kind": "image",
  "auth": { "type": "none" },
  "models": { "path": "/sdapi/v1/sd-models", "method": "GET", "list": "$[*].title" },
  "inputs": [
    { "id": "width", "label": "Width", "type": "number", "default": "512" },
    { "id": "height", "label": "Height", "type": "number", "default": "512" },
    { "id": "steps", "label": "Steps", "type": "number", "default": "20" },
    { "id": "negative", "label": "Negative prompt", "type": "text", "default": "" }
  ],
  "request": {
    "method": "POST",
    "path": "/sdapi/v1/txt2img",
    "body": {
      "prompt": "{{prompt}}",
      "negative_prompt": "{{input.negative}}",
      "width": "{{input.width}}",
      "height": "{{input.height}}",
      "steps": "{{input.steps}}",
      "seed": -1,
      "override_settings": { "sd_model_checkpoint": "{{model}}" }
    }
  },
  "response": { "type": "json", "results": [ { "from": "$.images[*]", "as": "base64", "mime": "image/png" } ] }
}
"""),
        new("ComfyUI (تصویر، گردش‌کار پیش‌فرض)", "http://127.0.0.1:8000", """
{
  "kind": "image",
  "auth": { "type": "none" },
  "models": { "path": "/models/checkpoints", "method": "GET", "list": "$[*]" },
  "inputs": [
    { "id": "width", "label": "Width", "type": "number", "default": "512" },
    { "id": "height", "label": "Height", "type": "number", "default": "512" },
    { "id": "steps", "label": "Steps", "type": "number", "default": "20" },
    { "id": "negative", "label": "Negative prompt", "type": "text", "default": "text, watermark" }
  ],
  "request": {
    "method": "POST",
    "path": "/prompt",
    "body": {
      "prompt": {
        "3": { "class_type": "KSampler", "inputs": { "seed": "{{seed}}", "steps": "{{input.steps}}", "cfg": 8, "sampler_name": "euler", "scheduler": "normal", "denoise": 1, "model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0] } },
        "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "{{model}}" } },
        "5": { "class_type": "EmptyLatentImage", "inputs": { "width": "{{input.width}}", "height": "{{input.height}}", "batch_size": 1 } },
        "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{prompt}}", "clip": ["4", 1] } },
        "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "{{input.negative}}", "clip": ["4", 1] } },
        "8": { "class_type": "VAEDecode", "inputs": { "samples": ["3", 0], "vae": ["4", 2] } },
        "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "AiChat", "images": ["8", 0] } }
      }
    }
  },
  "job": {
    "id": "$.prompt_id",
    "poll": { "method": "GET", "path": "/history/{{job}}", "intervalSec": 2, "timeoutSec": 600 },
    "response": {
      "type": "json",
      "results": [
        { "from": "$.*.outputs.*.images[*]", "as": "url", "urlTemplate": "/view?filename={{item.filename|url}}&subfolder={{item.subfolder|url}}&type={{item.type|url}}" }
      ]
    }
  }
}
"""),
        new("کار ناهمزمان عمومی (ویدیو / سه‌بعدی)", "https://api.example.com/v1", """
{
  "kind": "video",
  "_help": "Edit the paths and field names to match your service. Submit a job, poll its status, then download the file.",
  "auth": { "type": "bearer" },
  "models": { "static": ["model-name"] },
  "inputs": [
    { "id": "duration", "label": "Duration (s)", "type": "number", "default": "5" }
  ],
  "request": {
    "method": "POST",
    "path": "/generations",
    "body": { "model": "{{model}}", "prompt": "{{prompt}}", "duration": "{{input.duration}}", "image": "{{image_dataurl}}" }
  },
  "job": {
    "id": "$.id",
    "poll": {
      "method": "GET",
      "path": "/generations/{{job}}",
      "intervalSec": 5,
      "timeoutSec": 900,
      "statusPath": "$.status",
      "done": ["succeeded", "completed"],
      "failed": ["failed", "error", "canceled"],
      "errorPath": "$.error"
    },
    "response": {
      "type": "json",
      "results": [ { "from": "$.output.url", "as": "url", "mime": "video/mp4" } ]
    }
  }
}
""")
    };
}
