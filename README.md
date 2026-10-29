# AI Chat — چت چندسرویسی (ChatGPT-style) با .NET MAUI Blazor Hybrid

یک برنامه‌ی C# برای ویندوز و اندروید که چند API هوش مصنوعی را در یک محیط چت شبیه ChatGPT کنار هم نگه می‌دارد.

## امکانات
- سرویس‌دهنده‌ها: **OpenAI-compatible** (OpenAI، OpenRouter، DeepSeek، Groq، Mistral، xAI، Together، Ollama، LM Studio و ...)، **Anthropic (Claude)** و **Google Gemini**
- ثبت چند API و جابه‌جایی سریع بین سرویس و مدل از نوار بالا (حتی وسط یک گفتگو)
- دریافت خودکار لیست مدل‌ها از هر سرویس + امکان نوشتن دستی نام مدل
- نمایش زنده پاسخ (Streaming) و دکمه‌ی توقف و تولید مجدد
- رندر Markdown، جدول و بلوک کد با دکمه‌ی کپی
- پشتیبانی کامل فارسی/راست‌به‌چپ (هر پاراگراف جهت خودش را تشخیص می‌دهد)
- ذخیره‌ی تاریخچه‌ی گفتگوها روی دستگاه؛ کلید API ها در SecureStorage
- تم تیره/روشن خودکار، رابط واکنش‌گرا (سایدبار کشویی در موبایل)

## پیش‌نیازها
1. Visual Studio 2026 با workload **«.NET Multi-platform App UI development»** (MAUI) و **.NET 10 SDK**
2. برای اندروید: Android SDK و یک امولاتور یا گوشی (USB Debugging روشن)

## اجرا
1. پوشه‌ی `AiChat` را با Visual Studio باز کنید (`AiChat.csproj`).
2. بالای صفحه هدف اجرا را انتخاب کنید:
   - **Windows Machine** برای ویندوز
   - یک **Android Emulator / Device** برای اندروید
3. `F5` را بزنید.
4. در برنامه: **تنظیمات و API ها ← افزودن API جدید** → الگوی آماده را انتخاب کنید، کلید را بگذارید، «دریافت خودکار لیست مدل‌ها» را بزنید و ذخیره کنید.

## نکات
- **ارسال پیام**: در ویندوز `Enter` ارسال و `Shift+Enter` خط جدید است. در موبایل `Enter` خط جدید می‌سازد و با دکمه‌ی ارسال، پیام می‌رود.
- **Ollama / LM Studio**: روی ویندوز آدرس `http://localhost:11434/v1` کار می‌کند. در **امولاتور اندروید** به‌جای `localhost` از `http://10.0.2.2:11434/v1` استفاده کنید؛ روی گوشی واقعی از IP کامپیوتر در شبکه (و Ollama را با `OLLAMA_HOST=0.0.0.0` اجرا کنید).
- **جهت رابط**: از تنظیمات می‌توانید بین راست‌به‌چپ و چپ‌به‌راست جابه‌جا شوید.
- **ذخیره‌ی کلیدها**: اگر SecureStorage روی دستگاهی در دسترس نباشد، برنامه به‌صورت خودکار از Preferences استفاده می‌کند.
- داده‌ها در پوشه‌ی `FileSystem.AppDataDirectory` (فایل‌های `providers.json` و `conversations.json`) ذخیره می‌شوند.

## ساختار
```
AiChat/
  AiChat.csproj
  MauiProgram.cs / App.xaml / MainPage.xaml      ← میزبان BlazorWebView
  Models/Models.cs                               ← مدل‌ها و الگوهای آماده
  Services/
    Providers.cs                                 ← OpenAI / Claude / Gemini (Streaming + لیست مدل‌ها)
    ChatState.cs                                 ← وضعیت برنامه، ارسال، توقف، تاریخچه
    StorageService.cs                            ← ذخیره‌سازی JSON + SecureStorage
    MarkdownRenderer.cs                          ← Markdown امن (HTML خام غیرفعال)
  Components/                                    ← ChatApp، Sidebar، MessageView، SettingsDialog
  wwwroot/ (index.html، css/app.css، js/app.js)  ← ظاهر شبیه ChatGPT
  Platforms/Android, Platforms/Windows
```

## افزودن سرویس جدید
اگر سرویسی API سازگار با OpenAI دارد، فقط در تنظیمات با نوع «سازگار با OpenAI» و Base URL آن اضافه‌اش کنید. برای APIهای با فرمت متفاوت، یک کلاس جدید با رابط `IChatProvider` در `Services/Providers.cs` بنویسید و در `ProviderFactory` ثبت کنید.
