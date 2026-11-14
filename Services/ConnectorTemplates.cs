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
      "image_seed": -1,
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": false,
      "async_process": false,
      "loras": [
        {
          "$if": "{{input.lora}}",
          "enabled": true,
          "model_name": "{{input.lora}}",
          "weight": "{{input.lora_weight}}"
        }
      ]
    },
    "headers": {
      "Accept": "image/png"
    }
  },
  "response": {
    "type": "binary"
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
      "image_seed": -1,
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": false,
      "async_process": false,
      "input_image": "{{image1_base64}}",
      "uov_method": "{{input.method}}",
      "upscale_value": "{{input.custom_scale}}"
    },
    "headers": {
      "Accept": "image/png"
    }
  },
  "response": {
    "type": "binary"
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
      "image_seed": -1,
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": false,
      "async_process": false,
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
    },
    "headers": {
      "Accept": "image/png"
    }
  },
  "response": {
    "type": "binary"
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
      "image_seed": -1,
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": false,
      "async_process": false,
      "input_image": "{{image1_base64}}",
      "outpaint_selections": [
        "{{input.direction}}"
      ],
      "outpaint_distance_left": -1,
      "outpaint_distance_right": -1,
      "outpaint_distance_top": -1,
      "outpaint_distance_bottom": -1,
      "inpaint_additional_prompt": "{{prompt}}"
    },
    "headers": {
      "Accept": "image/png"
    }
  },
  "response": {
    "type": "binary"
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
      "image_seed": -1,
      "sharpness": 2,
      "guidance_scale": 4,
      "base_model_name": "{{model}}",
      "refiner_model_name": "None",
      "refiner_switch": 0.5,
      "save_meta": true,
      "save_extension": "png",
      "require_base64": false,
      "async_process": false,
      "input_image": "{{image1_base64}}",
      "input_mask": "{{image2_base64}}",
      "inpaint_additional_prompt": "{{prompt}}"
    },
    "headers": {
      "Accept": "image/png"
    }
  },
  "response": {
    "type": "binary"
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
