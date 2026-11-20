# Источники и версии

Проверка ссылок/направления стека: 20 ноября 2026. Конкретные patch-версии фиксируются
Codex при создании проекта после проверки фактического окружения; пакет не содержит
скачанных SDK, моделей, бинарников или обещаний совместимости непроверенных сборок.

## Официальные источники

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Node.js download](https://nodejs.org/en/download/)
- [Node.js 24 archive](https://nodejs.org/en/download/archive/v24)
- [Git for Windows](https://git-scm.com/downloads/win)
- [WebView2 overview](https://learn.microsoft.com/en-us/microsoft-edge/webview2/)
- [WPF + WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)
- [FFmpeg downloads](https://ffmpeg.org/download.html)
- [FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html)
- [ffprobe](https://ffmpeg.org/ffprobe.html)
- [whisper.cpp](https://github.com/ggml-org/whisper.cpp)
- [whisper.cpp releases](https://github.com/ggml-org/whisper.cpp/releases)
- [YouTube Shorts upload requirements](https://support.google.com/youtube/answer/12779649)

YouTube разрешает Shorts до 3 минут с квадратным/вертикальным соотношением сторон.
В продукте выбран общий рабочий preset 9:16 1080×1920; это инженерный выбор проекта,
а не утверждение, что обе платформы требуют исключительно такие параметры.
Ограничения TikTok, safe areas, рекомендации bitrate и условия публикации нужно повторно
проверить в официальных источниках при реализации platform-presets/публикации.
Не переносить рекламные API-требования TikTok на обычную ручную загрузку без проверки.

Свойства библиотек/CLI проверять в документации выбранной версии, а не по случайным
snippet из поиска. Основной источник истинности API — установленный SDK/help и его документация.
