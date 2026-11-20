# Архитектура ShortForge

## Зафиксированный стек

- .NET 10 LTS / C#, Windows x64.
- WPF — окно и native-dialogs; WebView2 — React UI.
- React + TypeScript strict + Vite. CSS tokens; Tailwind допустим при необходимости.
- SQLite через Microsoft.Data.Sqlite с небольшим явным migration layer.
- FFmpeg/ffprobe и whisper.cpp как дочерние процессы.
- Тесты core/infrastructure: xUnit; UI: Vitest/Testing Library, browser flows — Playwright.
- Для локального смыслового анализа: адаптер к локальному LLM; конкретный runtime/model
  выбрать отдельно после проверки совместимости, RAM, лицензии и качества.
- Для face tracking: C#-совместимый inference, например ONNX Runtime при совместимой модели.
  Конкретный detector не считать выбранным до проверки лицензии/выходов/скорости.

Не использовать отдельный web backend для desktop MVP. Не поднимать публичный порт.
Dev-сервер Vite допустим только в режиме разработки. Production UI — локальные build-ресурсы.

## Предлагаемое дерево после инициализации Codex

Каркас создан в P00. Результаты сборки и актуальные сценарии запуска описаны в [README разработки](../README.md).

```text
ShortForge.slnx
global.json
Directory.Build.props
Directory.Packages.props
src/ShortForge.Core/
src/ShortForge.Infrastructure/
src/ShortForge.Desktop/
src/ShortForge.Ui/
tests/ShortForge.Core.Tests/
tests/ShortForge.Infrastructure.Tests/
tests/fixtures/
scripts/Check-Environment.ps1
scripts/Build.ps1
scripts/Run.ps1
scripts/Publish.ps1
docs/
PLANS.md
STATUS.md
AGENTS.md
```

Core: сущности, проверки, time mapping, интерфейсы. Без WPF/FFmpeg UI-зависимостей.
Infrastructure: процессы, SQLite, ASR/LLM adapters, media workspace, export graph.
Desktop: WPF, bridge, native-dialogs, управление временем жизни приложения.
Ui: экраны и состояние представления; без произвольного доступа к файловой системе.

## Мост UI ↔ host

WebView2 postMessage, JSON-envelope с version, id и type. На каждую команду — ответ
с тем же id; длительные операции возвращают jobId, потом события прогресса.

Пример контракта (не готовая реализация):

```json
{"version":1,"id":"req-01","type":"media.pick","payload":{}}
```

```json
{"version":1,"id":"req-01","ok":true,"result":{"projectId":"p-01"}}
```

```json
{"version":1,"type":"job.progress","payload":{"jobId":"j-01","stage":"transcribe","progress":null}}
```

Ошибки: code, userMessage, retryable, diagnosticId; никаких stack traces/секретов в UI.
Разрешённые команды: projects.list/open, media.pick/importDropped, transcript.start/update,
clips.propose/update, preview.render, exports.start, jobs.cancel/retry, settings.get/update.
Дополнять контракт сознательно, покрывать boundary validation тестами.

Host проверяет origin и source frame; принимает сообщения только от собственного UI.
Ограничить навигацию/новые окна, отключить ненужные web permissions. Внешние ссылки
открывать в системном браузере только из явного действия пользователя и через allowlist-схем.
Не использовать широкие AddHostObjectToScript-объекты с произвольным запуском команд.

Пути и исполняемые файлы выбираются/проверяются C#. UI передаёт ID, а не команды shell.
Drag-and-drop больших файлов — через host/native path mechanism, не FileReader/base64.

## Просмотр медиа

UI получает URL только зарегистрированного в проекте media/proxy asset.
Разрешить доступ лишь к project workspace, исключить path traversal и произвольный file://.
Реализовать корректную отдачу byte-range/seek для большого файла либо документированную
безопасную virtual host mapping стратегию. Не считывать весь файл для одного HTTP range.
Не маппить весь диск в web-origin.
Если кодек исходника не поддержан WebView2 — создать proxy H.264/AAC.
Сохранять mapping к исходному времени, учитывать rotation и VFR.

## Сущности

| Сущность | Основные поля |
|---|---|
| Project | id, name, createdUtc, updatedUtc, settingsVersion |
| MediaAsset | id, projectId, originalPath, size, mtime, fingerprint, durationUs, streamsJson |
| Transcript | id, mediaId, audioStream, model/version, language, settingsHash |
| TranscriptSegment | id, transcriptId, startUs, endUs, rawText, editedText |
| WordTiming | segmentId, startUs, endUs, text, provenance/quality |
| Clip | id, projectId, startUs, endUs, title, selected, selectionMethod, rationale |
| CaptionCue | clipId, sourceRange/clipRange, text, words, styleVersion |
| CropKeyframe | clipId, timeUs, normalizedX/Y/width/height, source/manual |
| Job | id, kind, state, inputsHash, progress, createdUtc, updatedUtc, errorCode |
| ExportArtifact | jobId, clipId, path, preset, size, probeJson, completedUtc |

Таблицы могут корректироваться, но миграции обязательны. Не хранить большие WAV/видео/модели
в SQLite. UI не владеет source-of-truth для проектов и очереди.

## Время

В core хранить целое количество микросекунд (Int64) и именованные типы/методы преобразования.
Внешние CLI/JSON могут использовать миллисекунды/секунды; нормализовать на границе адаптера.
Интервалы полуоткрытые [start, end); обязательны 0 ≤ start < end ≤ duration.
Исходная шкала отсчитывается от нормализованного первого presentation timestamp.
Учесть отрицательные/ненулевые stream start_time и разные начала аудио/видео.
Не выводить таймкоды из номера кадра, если VFR.

Простой trim: clipTime = sourceTime - clipStart. Ограничивать subtitle cues границами клипа.
При появлении удаления пауз нужен один EditDecisionList с кусочно-линейным mapping;
его обязаны использовать audio/video/captions/crop. До этого не удалять паузы незаметно.

## Рабочие данные

- По умолчанию `%LOCALAPPDATA%\ShortForge\` — DB, settings, logs, managed cache.
- Workspace и model directory можно перенести на другой диск из настроек.
- Исходник остаётся на месте; кэш имеет собственные идентификаторы, не пользовательские имена.
- FFmpeg/whisper paths хранятся в настройках; tools/model binaries не попадают в git.
- Достаточно базовой миграции + backup перед сложной миграцией; без enterprise-слоёв.
- Ключ кэша: источник, выбранная аудиодорожка, этап, версия инструмента/модели и настройки.
- На импорт вычислить быстрый fingerprint (size/mtime/части файла); для строгой проверки
  relink и переносимого экспорта при необходимости полный hash без блокировки UI.

## Pipeline и очередь

Pipeline: probe → proxy/audio → ASR → candidates → edit → captions/crop → render → validate.
Каждый этап выдаёт артефакт с версией; ошибка не уничтожает законченные этапы.

Состояния Job: Queued, Running, Completed, Failed, Cancelled, Interrupted.
Running при старте приложения превращается в Interrupted и ожидает явного retry/продолжения.
Не обещать побайтное продолжение FFmpeg: незавершённый рендер обычно запускается заново.

Semaphore/Channel: один тяжёлый этап по умолчанию, CPU threads ограничены настройкой.
Cancellable child processes; stdout/stderr читаются одновременно, stderr tail ограничен.
CancellationToken + kill process tree именно текущей задачи + ожидание выхода/cleanup.
ffprobe имеет таймаут; долгий ASR — отмену/диагностику активности, не произвольный короткий таймаут.

Завершение Job=Completed только после сохранения и проверки результата.
Процент прогресса на основе реальных данных; неизвестный прогресс отображать без выдуманного числа.

## Работа с FFmpeg

- Отдельный builder аргументов; ProcessStartInfo.ArgumentList, InvariantCulture.
- Явный stream mapping выбранных дорожек; отсутствие аудио допустимо.
- Точный trim с перекодированием, а не stream copy с неточными границами ключевых кадров.
- Нормализовать rotation, SAR, размер/чётность, fps и pixel format.
- Для HDR → SDR выбрать и протестировать tonemap либо ясно сообщать об ограничении;
  простое удаление color tags недопустимо.
- Для фильтра subtitles/ASS избегать сложных пользовательских путей: создавать собственный
  workspace с контролируемыми именами, relative path и отдельное корректное escaping filter syntax.
  ArgumentList сам по себе не экранирует синтаксис filtergraph.
- Progress `-progress` читать как key/value; не парсить локализованный текст консоли.
- NVENC включать только после тестового encode, а не по наличию пункта в `-encoders`.
- Итоговый filename уникален; сначала временный файл на том же томе, затем probe и rename.

## ASR и LLM

ASR-adapter изолирует формат конкретного whisper CLI. Проверить `--help` и fixture-output,
не угадывать названия полей JSON, поддерживаемые GPU и flags word-timestamps.
При изменении версии CLI — контрактный тест.
Токены ASR не всегда равны словам; нужна нормализация и проверка на реальном аудио.

LLM не возвращает произвольные команды FFmpeg/код. Только выбранные segment IDs,
структурированную оценку/объяснение и текстовые метаданные с лимитами длины.
Весь ответ валидируется до изменения проекта. Отказ сети/провайдера не блокирует ручной режим.
Никаких автоматических расходов; облачный провайдер выключен по умолчанию.

## Поставка

Первый deliverable — portable win-x64 publish directory, проверенный на Windows.
Single-file packaging не навязывать: WebView2, native SQLite и media dependencies требуют проверки.
Шрифты/модели/бинарники распространять только с подходящей лицензией и нужными notices.
Сначала надёжный запуск с настроенными путями, затем installer и автоматические загрузки.
