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
  "_help": "Text to image. Optional LoRA.",
  "auth": {
    "type": "header",
    "name": "X-API-KEY"
  },
  "models": {
    "path": "/v1/engines/all-models",
    "method": "GET",
    "list": "$.model_filenames[*]"
  },
  "inputs": [
    { "id": "seed", "label": "Seed (-1 = random)", "type": "number", "default": "-1" },
    {
      "id": "aspect",
      "label": "Aspect ratio",
      "type": "select",
      "options": [
        "704*1408",
        "768*1344",
        "832*1216",
        "896*1152",
        "1024*1024",
        "1152*896",
        "1216*832",
        "1344*768",
        "1408*704"
      ],
      "default": "1152*896"
    },
    {
      "id": "performance",
      "label": "Performance",
      "type": "select",
      "options": [
        "Speed",
        "Quality",
        "Extreme Speed"
      ],
      "default": "Speed"
    },
    {
      "id": "negative",
      "label": "Negative prompt",
      "type": "text",
      "default": ""
    },
    {
      "id": "lora",
      "label": "LoRA file name (optional)",
      "type": "text",
      "default": ""
    },
    {
      "id": "lora_weight",
      "label": "LoRA weight (-2 to 2)",
      "type": "number",
      "default": "0.8"
    }
  ],
  "request": {
    "method": "POST",
    "path": "/v1/generation/text-to-image",
    "body": {
      "prompt": "{{prompt}}",
      "negative_prompt": "{{input.negative}}",
      "style_selections": [
        "Fooocus V2",
        "Fooocus Enhance",
        "Fooocus Sharp"
      ],
      "performance_selection": "{{input.performance}}",
      "aspect_ratios_selection": "{{input.aspect}}",
      "image_number": 1,
      "image_seed": "{{input.seed}}",
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": true,
      "async_process": true,
      "loras": [
        {
          "$if": "{{input.lora}}",
          "enabled": true,
          "model_name": "{{input.lora}}",
          "weight": "{{input.lora_weight}}"
        }
      ]
    }
  },
  "job": {
    "id": "$.job_id",
    "cancel": { "method": "POST", "path": "/v1/generation/stop" },
    "poll": {
      "method": "GET",
      "path": "/v1/generation/query-job",
      "query": { "job_id": "{{job}}", "require_step_preview": "false" },
      "intervalSec": 1,
      "timeoutSec": 1800,
      "statusPath": "$.job_stage",
      "done": [ "success" ],
      "failed": [ "error" ],
      "errorPath": "$.job_status",
      "progressPath": "$.job_progress",
      "progressMax": 100
    },
    "response": {
      "seeds": "$.job_result[*].seed",
      "results": [ { "from": "$.job_result[*].base64", "as": "base64", "mime": "image/png" } ]
    }
  }
}
"""),
        new("Fooocus-API: Upscale / Vary", "http://127.0.0.1:8888", """
{
  "kind": "image",
  "_help": "Attach ONE image. Vary: the message can describe the image (optional).",
  "auth": {
    "type": "header",
    "name": "X-API-KEY"
  },
  "models": {
    "path": "/v1/engines/all-models",
    "method": "GET",
    "list": "$.model_filenames[*]"
  },
  "inputs": [
    { "id": "seed", "label": "Seed (-1 = random)", "type": "number", "default": "-1" },
    {
      "id": "method",
      "label": "Method",
      "type": "select",
      "options": [
        "Upscale (2x)",
        "Upscale (1.5x)",
        "Upscale (Fast 2x)",
        "Upscale (Custom)",
        "Vary (Subtle)",
        "Vary (Strong)"
      ],
      "default": "Upscale (2x)"
    },
    {
      "id": "custom_scale",
      "label": "Custom upscale factor (1.0-5.0)",
      "type": "number",
      "default": ""
    },
    {
      "id": "performance",
      "label": "Performance",
      "type": "select",
      "options": [
        "Speed",
        "Quality",
        "Extreme Speed"
      ],
      "default": "Speed"
    },
    {
      "id": "negative",
      "label": "Negative prompt",
      "type": "text",
      "default": ""
    }
  ],
  "request": {
    "method": "POST",
    "path": "/v2/generation/image-upscale-vary",
    "body": {
      "prompt": "{{prompt}}",
      "negative_prompt": "{{input.negative}}",
      "style_selections": [],
      "performance_selection": "{{input.performance}}",
      "image_number": 1,
      "image_seed": "{{input.seed}}",
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": true,
      "async_process": true,
      "input_image": "{{image1_base64}}",
      "uov_method": "{{input.method}}",
      "upscale_value": "{{input.custom_scale}}"
    }
  },
  "job": {
    "id": "$.job_id",
    "cancel": { "method": "POST", "path": "/v1/generation/stop" },
    "poll": {
      "method": "GET",
      "path": "/v1/generation/query-job",
      "query": { "job_id": "{{job}}", "require_step_preview": "false" },
      "intervalSec": 1,
      "timeoutSec": 1800,
      "statusPath": "$.job_stage",
      "done": [ "success" ],
      "failed": [ "error" ],
      "errorPath": "$.job_status",
      "progressPath": "$.job_progress",
      "progressMax": 100
    },
    "response": {
      "seeds": "$.job_result[*].seed",
      "results": [ { "from": "$.job_result[*].base64", "as": "base64", "mime": "image/png" } ]
    }
  },
  "allowEmptyPrompt": true
}
"""),
        new("Fooocus-API: Image Prompt / FaceSwap", "http://127.0.0.1:8888", """
{
  "kind": "image",
  "_help": "Attach 1 or 2 reference images and write a prompt.",
  "auth": {
    "type": "header",
    "name": "X-API-KEY"
  },
  "models": {
    "path": "/v1/engines/all-models",
    "method": "GET",
    "list": "$.model_filenames[*]"
  },
  "inputs": [
    { "id": "seed", "label": "Seed (-1 = random)", "type": "number", "default": "-1" },
    {
      "id": "cn_type",
      "label": "Image use",
      "type": "select",
      "options": [
        "ImagePrompt",
        "FaceSwap",
        "PyraCanny",
        "CPDS"
      ],
      "default": "ImagePrompt"
    },
    {
      "id": "cn_weight",
      "label": "Image weight (0-2)",
      "type": "number",
      "default": "0.6"
    },
    {
      "id": "cn_stop",
      "label": "Stop at (0-1)",
      "type": "number",
      "default": "0.5"
    },
    {
      "id": "aspect",
      "label": "Aspect ratio",
      "type": "select",
      "options": [
        "704*1408",
        "768*1344",
        "832*1216",
        "896*1152",
        "1024*1024",
        "1152*896",
        "1216*832",
        "1344*768",
        "1408*704"
      ],
      "default": "1152*896"
    },
    {
      "id": "performance",
      "label": "Performance",
      "type": "select",
      "options": [
        "Speed",
        "Quality",
        "Extreme Speed"
      ],
      "default": "Speed"
    },
    {
      "id": "negative",
      "label": "Negative prompt",
      "type": "text",
      "default": ""
    },
    {
      "id": "lora",
      "label": "LoRA file name (optional)",
      "type": "text",
      "default": ""
    },
    {
      "id": "lora_weight",
      "label": "LoRA weight (-2 to 2)",
      "type": "number",
      "default": "0.8"
    }
  ],
  "request": {
    "method": "POST",
    "path": "/v2/generation/image-prompt",
    "body": {
      "prompt": "{{prompt}}",
      "negative_prompt": "{{input.negative}}",
      "style_selections": [
        "Fooocus V2",
        "Fooocus Enhance",
        "Fooocus Sharp"
      ],
      "performance_selection": "{{input.performance}}",
      "aspect_ratios_selection": "{{input.aspect}}",
      "image_number": 1,
      "image_seed": "{{input.seed}}",
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": true,
      "async_process": true,
      "image_prompts": [
        {
          "cn_img": "{{image1_base64}}",
          "cn_type": "{{input.cn_type}}",
          "cn_weight": "{{input.cn_weight}}",
          "cn_stop": "{{input.cn_stop}}"
        },
        {
          "$if": "{{image2_base64}}",
          "cn_img": "{{image2_base64}}",
          "cn_type": "{{input.cn_type}}",
          "cn_weight": "{{input.cn_weight}}",
          "cn_stop": "{{input.cn_stop}}"
        }
      ],
      "loras": [
        {
          "$if": "{{input.lora}}",
          "enabled": true,
          "model_name": "{{input.lora}}",
          "weight": "{{input.lora_weight}}"
        }
      ]
    }
  },
  "job": {
    "id": "$.job_id",
    "cancel": { "method": "POST", "path": "/v1/generation/stop" },
    "poll": {
      "method": "GET",
      "path": "/v1/generation/query-job",
      "query": { "job_id": "{{job}}", "require_step_preview": "false" },
      "intervalSec": 1,
      "timeoutSec": 1800,
      "statusPath": "$.job_stage",
      "done": [ "success" ],
      "failed": [ "error" ],
      "errorPath": "$.job_status",
      "progressPath": "$.job_progress",
      "progressMax": 100
    },
    "response": {
      "seeds": "$.job_result[*].seed",
      "results": [ { "from": "$.job_result[*].base64", "as": "base64", "mime": "image/png" } ]
    }
  }
}
"""),
        new("Fooocus-API: Outpaint", "http://127.0.0.1:8888", """
{
  "kind": "image",
  "_help": "Attach ONE image. The message (optional) describes the new area.",
  "auth": {
    "type": "header",
    "name": "X-API-KEY"
  },
  "models": {
    "path": "/v1/engines/all-models",
    "method": "GET",
    "list": "$.model_filenames[*]"
  },
  "inputs": [
    { "id": "seed", "label": "Seed (-1 = random)", "type": "number", "default": "-1" },
    {
      "id": "direction",
      "label": "Direction",
      "type": "select",
      "options": [
        "Left",
        "Right",
        "Top",
        "Bottom"
      ],
      "default": "Left"
    },
    {
      "id": "performance",
      "label": "Performance",
      "type": "select",
      "options": [
        "Speed",
        "Quality",
        "Extreme Speed"
      ],
      "default": "Speed"
    },
    {
      "id": "negative",
      "label": "Negative prompt",
      "type": "text",
      "default": ""
    },
    {
      "id": "aspect",
      "label": "Aspect ratio",
      "type": "select",
      "options": [
        "704*1408",
        "768*1344",
        "832*1216",
        "896*1152",
        "1024*1024",
        "1152*896",
        "1216*832",
        "1344*768",
        "1408*704"
      ],
      "default": "1152*896"
    }
  ],
  "request": {
    "method": "POST",
    "path": "/v2/generation/image-inpaint-outpaint",
    "body": {
      "prompt": "",
      "negative_prompt": "{{input.negative}}",
      "style_selections": [
        "Fooocus V2",
        "Fooocus Enhance",
        "Fooocus Sharp"
      ],
      "performance_selection": "{{input.performance}}",
      "aspect_ratios_selection": "{{input.aspect}}",
      "image_number": 1,
      "image_seed": "{{input.seed}}",
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": true,
      "async_process": true,
      "input_image": "{{image1_base64}}",
      "outpaint_selections": [
        "{{input.direction}}"
      ],
      "outpaint_distance_left": -1,
      "outpaint_distance_right": -1,
      "outpaint_distance_top": -1,
      "outpaint_distance_bottom": -1,
      "inpaint_additional_prompt": "{{prompt}}"
    }
  },
  "job": {
    "id": "$.job_id",
    "cancel": { "method": "POST", "path": "/v1/generation/stop" },
    "poll": {
      "method": "GET",
      "path": "/v1/generation/query-job",
      "query": { "job_id": "{{job}}", "require_step_preview": "false" },
      "intervalSec": 1,
      "timeoutSec": 1800,
      "statusPath": "$.job_stage",
      "done": [ "success" ],
      "failed": [ "error" ],
      "errorPath": "$.job_status",
      "progressPath": "$.job_progress",
      "progressMax": 100
    },
    "response": {
      "seeds": "$.job_result[*].seed",
      "results": [ { "from": "$.job_result[*].base64", "as": "base64", "mime": "image/png" } ]
    }
  },
  "allowEmptyPrompt": true
}
"""),
        new("Fooocus-API: Inpaint", "http://127.0.0.1:8888", """
{
  "kind": "image",
  "_help": "Attach TWO images: first the original, second the mask (same size; WHITE = area to change, BLACK = keep). The message describes what should appear.",
  "auth": {
    "type": "header",
    "name": "X-API-KEY"
  },
  "models": {
    "path": "/v1/engines/all-models",
    "method": "GET",
    "list": "$.model_filenames[*]"
  },
  "inputs": [
    { "id": "seed", "label": "Seed (-1 = random)", "type": "number", "default": "-1" },
    {
      "id": "performance",
      "label": "Performance",
      "type": "select",
      "options": [
        "Speed",
        "Quality",
        "Extreme Speed"
      ],
      "default": "Speed"
    },
    {
      "id": "negative",
      "label": "Negative prompt",
      "type": "text",
      "default": ""
    },
    {
      "id": "aspect",
      "label": "Aspect ratio",
      "type": "select",
      "options": [
        "704*1408",
        "768*1344",
        "832*1216",
        "896*1152",
        "1024*1024",
        "1152*896",
        "1216*832",
        "1344*768",
        "1408*704"
      ],
      "default": "1152*896"
    }
  ],
  "request": {
    "method": "POST",
    "path": "/v2/generation/image-inpaint-outpaint",
    "body": {
      "prompt": "",
      "negative_prompt": "{{input.negative}}",
      "style_selections": [
        "Fooocus V2",
        "Fooocus Enhance",
        "Fooocus Sharp"
      ],
      "performance_selection": "{{input.performance}}",
      "aspect_ratios_selection": "{{input.aspect}}",
      "image_number": 1,
      "image_seed": "{{input.seed}}",
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": true,
      "async_process": true,
      "input_image": "{{image1_base64}}",
      "input_mask": "{{image2_base64}}",
      "inpaint_additional_prompt": "{{prompt}}"
    }
  },
  "job": {
    "id": "$.job_id",
    "cancel": { "method": "POST", "path": "/v1/generation/stop" },
    "poll": {
      "method": "GET",
      "path": "/v1/generation/query-job",
      "query": { "job_id": "{{job}}", "require_step_preview": "false" },
      "intervalSec": 1,
      "timeoutSec": 1800,
      "statusPath": "$.job_stage",
      "done": [ "success" ],
      "failed": [ "error" ],
      "errorPath": "$.job_status",
      "progressPath": "$.job_progress",
      "progressMax": 100
    },
    "response": {
      "seeds": "$.job_result[*].seed",
      "results": [ { "from": "$.job_result[*].base64", "as": "base64", "mime": "image/png" } ]
    }
  }
}
"""),
        new("Fooocus-API: Describe image", "http://127.0.0.1:8888", """
{
  "kind": "text",
  "_help": "Attach ONE image; the reply is a text description (a prompt you can reuse).",
  "auth": {
    "type": "header",
    "name": "X-API-KEY"
  },
  "models": {
    "path": "/v1/engines/all-models",
    "method": "GET",
    "list": "$.model_filenames[*]"
  },
  "inputs": [
    {
      "id": "type",
      "label": "Image type",
      "type": "select",
      "options": [
        "Photo",
        "Anime"
      ],
      "default": "Photo"
    }
  ],
  "request": {
    "method": "POST",
    "path": "/v1/tools/describe-image",
    "body": {
      "image": {
        "file": "{{image1_base64}}",
        "mime": "{{image1_mime}}",
        "filename": "image.png"
      }
    },
    "query": {
      "type": "{{input.type}}"
    },
    "bodyType": "multipart"
  },
  "response": {
    "type": "json",
    "text": "$.describe"
  },
  "allowEmptyPrompt": true
}
"""),
        new("Ollama (API بومی، چت)", "http://localhost:11434", """
{
  "kind": "chat",
  "auth": { "type": "none" },
  "_help": "Ollama native chat API (http://localhost:11434). Install Ollama and pull a model first.",
  "models": { "path": "/api/tags", "method": "GET", "list": "$.models[*].name" },
  "request": {
    "method": "POST",
    "path": "/api/chat",
    "body": { "model": "{{model}}", "messages": "{{messages_with_system}}", "stream": true }
  },
  "stream": { "format": "ndjson", "textPath": "$.message.content", "errorPath": "$.error" }
}
"""),
        new("چت با نمایش تفکر مدل (سبک DeepSeek)", "https://api.deepseek.com/v1", """
{
  "kind": "chat",
  "_help": "Chat API that streams a reasoning field (reasoning_content); the model's thinking is shown as a quoted block. Needs an API key.",
  "auth": { "type": "bearer" },
  "models": { "path": "/models", "method": "GET", "list": "$.data[*].id" },
  "request": {
    "method": "POST",
    "path": "/chat/completions",
    "body": { "model": "{{model}}", "messages": "{{messages_with_system}}", "stream": true }
  },
  "stream": {
    "format": "sse",
    "textPath": "$.choices[0].delta.content",
    "reasoningPath": "$.choices[0].delta.reasoning_content",
    "errorPath": "$.error.message"
  }
}
"""),
        new("OpenAI Images (تصویر)", "https://api.openai.com/v1", """
{
  "kind": "image",
  "_help": "OpenAI image generation. Needs an OpenAI API key.",
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
  "_help": "Text to speech with OpenAI. Needs an OpenAI API key; the reply is an audio file you can play.",
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
  "_help": "Local Stable Diffusion WebUI (start it with the --api option). Models are read from the server.",
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
        new("ComfyUI (تصویر، گردش‌کار پیش‌فرض)", "http://127.0.0.1:8188", """
{
  "kind": "image",
  "_help": "Local ComfyUI, default text-to-image workflow. Use the port ComfyUI listens on (8188 or 8000).",
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
    "cancel": { "method": "POST", "path": "/interrupt" },
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
        new("ComfyUI: Wan 2.2 (ویدیو)", "http://127.0.0.1:8188", """
{
  "kind": "video",
  "_help": "Local ComfyUI, Wan 2.2 5B text-to-video. The model box is the diffusion model file; needs several minutes on a mid-range GPU. Use the port ComfyUI listens on.",
  "auth": {
    "type": "none"
  },
  "models": {
    "path": "/models/diffusion_models",
    "method": "GET",
    "list": "$[*]"
  },
  "inputs": [
    {
      "id": "width",
      "label": "Width",
      "type": "number",
      "default": "608"
    },
    {
      "id": "height",
      "label": "Height",
      "type": "number",
      "default": "384"
    },
    {
      "id": "length",
      "label": "Frames (121 = 5 s at 24 fps)",
      "type": "number",
      "default": "121"
    },
    {
      "id": "fps",
      "label": "FPS",
      "type": "number",
      "default": "24"
    },
    {
      "id": "steps",
      "label": "Steps",
      "type": "slider",
      "min": 10,
      "max": 50,
      "step": 1,
      "default": "20"
    },
    {
      "id": "cfg",
      "label": "CFG",
      "type": "slider",
      "min": 1,
      "max": 10,
      "step": 0.5,
      "default": "5"
    },
    {
      "id": "negative",
      "label": "Negative prompt",
      "type": "text",
      "default": "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走"
    },
    {
      "id": "clip",
      "label": "CLIP file",
      "type": "text",
      "default": "umt5_xxl_fp8_e4m3fn_scaled.safetensors"
    },
    {
      "id": "vae",
      "label": "VAE file",
      "type": "text",
      "default": "wan2.2_vae.safetensors"
    }
  ],
  "request": {
    "method": "POST",
    "path": "/prompt",
    "body": {
      "prompt": {
        "3": {
          "inputs": {
            "seed": "{{seed}}",
            "steps": "{{input.steps}}",
            "cfg": "{{input.cfg}}",
            "sampler_name": "uni_pc",
            "scheduler": "simple",
            "denoise": 1,
            "model": [
              "48",
              0
            ],
            "positive": [
              "6",
              0
            ],
            "negative": [
              "7",
              0
            ],
            "latent_image": [
              "55",
              0
            ]
          },
          "class_type": "KSampler"
        },
        "6": {
          "inputs": {
            "text": "{{prompt}}",
            "clip": [
              "38",
              0
            ]
          },
          "class_type": "CLIPTextEncode"
        },
        "7": {
          "inputs": {
            "text": "{{input.negative}}",
            "clip": [
              "38",
              0
            ]
          },
          "class_type": "CLIPTextEncode"
        },
        "8": {
          "inputs": {
            "samples": [
              "3",
              0
            ],
            "vae": [
              "39",
              0
            ]
          },
          "class_type": "VAEDecode"
        },
        "37": {
          "inputs": {
            "unet_name": "{{model}}",
            "weight_dtype": "default"
          },
          "class_type": "UNETLoader"
        },
        "38": {
          "inputs": {
            "clip_name": "{{input.clip}}",
            "type": "wan",
            "device": "default"
          },
          "class_type": "CLIPLoader"
        },
        "39": {
          "inputs": {
            "vae_name": "{{input.vae}}"
          },
          "class_type": "VAELoader"
        },
        "48": {
          "inputs": {
            "shift": 8,
            "model": [
              "37",
              0
            ]
          },
          "class_type": "ModelSamplingSD3"
        },
        "55": {
          "inputs": {
            "width": "{{input.width}}",
            "height": "{{input.height}}",
            "length": "{{input.length}}",
            "batch_size": 1,
            "vae": [
              "39",
              0
            ]
          },
          "class_type": "Wan22ImageToVideoLatent"
        },
        "57": {
          "inputs": {
            "fps": "{{input.fps}}",
            "bit_depth": "auto",
            "color_space": "sRGB",
            "codec": "none",
            "images": [
              "8",
              0
            ]
          },
          "class_type": "CreateVideo"
        },
        "58": {
          "inputs": {
            "filename_prefix": "video/AiChat",
            "format": "auto",
            "format.codec": "auto",
            "codec": "auto",
            "video": [
              "57",
              0
            ]
          },
          "class_type": "SaveVideo"
        }
      }
    }
  },
  "job": {
    "id": "$.prompt_id",
    "cancel": { "method": "POST", "path": "/interrupt" },
    "poll": {
      "method": "GET",
      "path": "/history/{{job}}",
      "intervalSec": 5,
      "timeoutSec": 3600,
      "statusPath": "$.*.status.status_str",
      "done": [
        "success"
      ],
      "failed": [
        "error"
      ],
      "errorPath": "$.*.status.messages"
    },
    "response": {
      "type": "json",
      "results": [
        {
          "from": "$.*.outputs.*.images[*]",
          "as": "url",
          "urlTemplate": "/view?filename={{item.filename|url}}&subfolder={{item.subfolder|url}}&type={{item.type|url}}"
        }
      ]
    }
  }
}
"""),
        new("Viggle: ویدیو ← انیمیشن سه‌بعدی (FBX / GLB)", "https://apis.viggle.ai/v1", """
{
  "kind": "file",
  "_help": "Extracts a 3D skeleton animation (FBX or GLB) from a video. Attach the video file (mp4 / mov / webm) to your message, or write a public direct link to the video as the message. Result: Mixamo, MetaHuman body and MetaHuman full rigs. Costs credits per second of video.",
  "auth": { "type": "bearer" },
  "allowEmptyPrompt": true,
  "inputs": [
    { "id": "format", "label": "File format", "type": "select", "options": ["fbx", "glb"], "default": "fbx" },
    { "id": "smoothing", "label": "Smooth the motion", "type": "bool", "default": "false" },
    { "id": "fps", "label": "Frames per second", "type": "number", "default": "25" }
  ],
  "request": {
    "method": "POST",
    "path": "/motions",
    "bodyType": "multipart",
    "body": {
      "motion_video": { "file": "{{video1_base64}}", "mime": "{{video1_mime}}", "filename": "{{video1_name}}" },
      "motion_video_url": "{{prompt|http}}",
      "type": "glb",
      "enable_smoothing": "{{input.smoothing}}",
      "target_fps": "{{input.fps}}"
    }
  },
  "job": {
    "id": "$.id",
    "poll": {
      "method": "GET",
      "path": "/motions/{{job}}/export?download_type={{input.format}}",
      "intervalSec": 5,
      "timeoutSec": 900,
      "statusPath": "$.status",
      "done": ["ready"],
      "failed": ["failed", "cancelled"],
      "errorPath": "$.error.message"
    },
    "response": {
      "type": "json",
      "results": [
        { "from": "$.mixamo_url", "as": "url", "mime": "application/octet-stream", "ext": "{{input.format}}" },
        { "from": "$.metahuman_body_url", "as": "url", "mime": "application/octet-stream", "ext": "{{input.format}}" },
        { "from": "$.metahuman_url", "as": "url", "mime": "application/octet-stream", "ext": "{{input.format}}" }
      ]
    }
  }
}
"""),
        new("ارسال فایل (ویدیو / صدا / JSON / هر فایل) به API", "https://api.example.com/v1", """
{
  "kind": "file",
  "_help": "Template for APIs that take a file upload. Attach any file (video, audio, JSON, zip, ...) to the message. {{file1_*}} = first attached file, {{video1_*}} = first video, {{audio1_*}} = first audio; _base64, _mime, _name, _dataurl and _text (the content of a text / JSON file) are available.",
  "auth": { "type": "bearer" },
  "allowEmptyPrompt": true,
  "request": {
    "method": "POST",
    "path": "/upload",
    "bodyType": "multipart",
    "body": {
      "file": { "file": "{{file1_base64}}", "mime": "{{file1_mime}}", "filename": "{{file1_name}}" },
      "note": "{{prompt}}"
    }
  },
  "response": { "type": "json", "text": "$.message", "results": [ { "from": "$.url", "as": "url" } ] }
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
