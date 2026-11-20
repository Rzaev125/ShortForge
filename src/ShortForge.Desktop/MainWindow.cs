using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using ShortForge.Core;
using ShortForge.Infrastructure;

namespace ShortForge.Desktop;

public sealed class MainWindow : Window
{
    private readonly string directory;
    private readonly string? pickerDirectory;
    private readonly ToolSettings tools;
    private readonly LocalResources resources = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly WebView2 view = new();
    private readonly Task<Library> library;
    private readonly HashSet<Task> pending = new();
    private readonly SemaphoreSlim heavyGate = new(1);
    private string? queueFailure;
    private bool exportWorkerActive, exportWakeRequested;
    private CancellationTokenSource? current;
    private Guid? currentJobId;
    private bool closing, drained, preparingClose;
    private Guid flushId;
    private TaskCompletionSource<bool>? editorFlush;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow(string dataDirectory, string? pickerDirectory = null)
    {
        Title = "ShortForge";
        Width = Math.Min(1280, SystemParameters.WorkArea.Width);
        Height = Math.Min(820, SystemParameters.WorkArea.Height);
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(11, 15, 22));
        view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 15, 22);
        Content = view;
        directory = dataDirectory;
        this.pickerDirectory = pickerDirectory;
        tools = new(directory);
        library = Task.Run(() => { Directory.CreateDirectory(directory); return new Library(directory); });
        SourceInitialized += (_, _) =>
        {
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        };
        Loaded += async (_, _) => await StartAsync();
        Closing += Shutdown;
        Closed += (_, _) => view.Dispose();
    }

    private async Task StartAsync()
    {
        try
        {
            await library;
            if (closing) return;
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "ui", "index.html")))
                throw new UserError("ui_missing", "Файлы интерфейса отсутствуют. Выполните scripts/Build.ps1.");
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(directory, "webview"));
            if (closing) return;
            await view.EnsureCoreWebView2Async(environment);
            if (closing) return;
            var web = view.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDefaultScriptDialogsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.IsZoomControlEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.NavigationStarting += (_, e) => e.Cancel = !BridgeRequest.IsPage(e.Uri);
            web.FrameNavigationStarting += (_, e) => e.Cancel = true;
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.DownloadStarting += (_, e) => e.Cancel = true;
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            web.WebResourceRequested += (_, e) => resources.Serve(web, e);
            web.WebMessageReceived += Receive;
            web.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    ShowError("Локальный интерфейс не загрузился. Проверьте папку ui рядом с приложением.");
            };
            web.Navigate(BridgeRequest.Page);
            WakeExports(await library);
        }
        catch (Exception ex)
        {
            if (!closing) ShowError(ex switch
            {
                WebView2RuntimeNotFoundException => "Установите Microsoft Edge WebView2 Evergreen Runtime x64 и откройте ShortForge снова.",
                UserError error => error.Message,
                _ => "Не удалось открыть локальное хранилище или запустить WebView2. Проверьте права записи в %LOCALAPPDATA%\\ShortForge и версию WebView2 Runtime."
            });
        }
    }

    private void ShowError(string message) => Content = new TextBlock
    {
        Text = message, Foreground = Brushes.White, Margin = new Thickness(32),
        FontSize = 18, TextWrapping = TextWrapping.Wrap
    };

    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // This event is for the top-level document. Frame.WebMessageReceived is never subscribed.
        if (closing || !BridgeRequest.IsPage(e.Source) || !BridgeRequest.IsPage(view.CoreWebView2.Source)) return;
        var work = HandleAsync(e);
        pending.Add(work);
        try { await work; }
        finally { pending.Remove(work); }
    }

    private async Task HandleAsync(CoreWebView2WebMessageReceivedEventArgs e)
    {
        Guid? id = null;
        try
        {
            var json = e.WebMessageAsJson;
            id = BridgeRequest.ReplyId(json);
            var request = BridgeRequest.Parse(json);
            string? dropped = null;
            if (request.Type == "media.importDropped")
            {
                if (e.AdditionalObjects is not { Count: 1 } objects || objects[0] is not CoreWebView2File file)
                    throw new UserError("invalid_drop", "Перетащите один локальный видеофайл.");
                dropped = file.Path;
            }
            else if (e.AdditionalObjects is { Count: > 0 })
                throw new UserError("invalid_drop", "Эта команда не принимает файлы.");
            // A native picker runs a nested message loop. Leave the WebView2 callback first.
            await Task.Yield();
            lifetime.Token.ThrowIfCancellationRequested();
            var result = await DispatchAsync(request, dropped);
            Send(new { version = 1, id, ok = true, result });
        }
        catch (Exception ex)
        {
            var (code, message) = ex switch
            {
                UserError error => (error.Code, error.Message),
                OperationCanceledException => ("cancelled", "Операция отменена."),
                UnauthorizedAccessException => ("access_denied", "Нет доступа к файлу или папке проекта."),
                IOException => ("io_error", "Не удалось прочитать или записать файл. Проверьте доступ и свободное место."),
                _ => ("storage_error", "Не удалось выполнить операцию. Проверьте доступ к локальному хранилищу.")
            };
            var diagnosticId = Guid.NewGuid().ToString("N")[..12];
            try
            {
                var logs = Path.Combine(directory, "logs");
                Directory.CreateDirectory(logs);
                await File.AppendAllTextAsync(Path.Combine(logs, "desktop.log"),
                    $"{DateTimeOffset.UtcNow:O} {diagnosticId} {code} {ex.GetType().Name}\n");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            Send(new { version = 1, id, ok = false, error = new { code, userMessage = message, retryable = true, diagnosticId } });
        }
    }

    private void Send(object message)
    {
        if (!closing && BridgeRequest.IsPage(view.CoreWebView2.Source))
            view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    private async Task<object?> DispatchAsync(BridgeRequest request, string? dropped)
    {
        var db = await library;
        switch (request.Type)
        {
            case "exports.start":
                return await StartExportsAsync(db, request);
            case "exports.list":
                return new { jobs = await Task.Run(db.ListExportJobs), issue = queueFailure };
            case "exports.resume":
                WakeExports(db);
                return true;
            case "exports.cancel":
                return await Task.Run(() => db.CancelExport(request.JobId!.Value));
            case "exports.retry":
                var retried = await Task.Run(() => db.RetryExport(request.JobId!.Value));
                if (retried) WakeExports(db);
                return retried;
            case "exports.openFolder":
                var folder = await Task.Run(() => db.ExportFolder(request.JobId!.Value));
                var explorer = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
                explorer.ArgumentList.Add(folder);
                using (System.Diagnostics.Process.Start(explorer)) { }
                return true;
            case "editor.flushed":
                return request.JobId == flushId && editorFlush?.TrySetResult(true) == true;
            case "editor.open":
                return await Task.Run(() => db.OpenEdit(request.ProjectId!.Value, request.ClipId!.Value));
            case "editor.create":
                return await Task.Run(() => db.CreateEdit(request.ProjectId!.Value, request.SelectionId, request.StartId));
            case "editor.save":
                var saved = await Task.Run(() => db.SaveEdit(request.ProjectId!.Value, request.ClipId!.Value, request.Revision, request.Edit!, request.Cue));
                resources.InvalidatePreview(saved.Id, saved.Revision);
                return saved;
            case "projects.list":
                return (await Task.Run(db.List)).Select(Describe).ToArray();
            case "projects.open":
                return Describe(await Task.Run(() => db.Open(request.ProjectId!.Value)));
            case "settings.get":
                return await DiagnosticsAsync();
            case "settings.useAutomaticTools":
                await Task.Run(tools.UseAutomaticSearch);
                return await DiagnosticsAsync();
            case "settings.pickTools":
                var picker = new OpenFolderDialog { Title = "Выберите папку с ffprobe.exe и ffmpeg.exe" };
                if (picker.ShowDialog(this) == true)
                    await Task.Run(() => tools.SetAsync(picker.FolderName, lifetime.Token));
                return await DiagnosticsAsync();
            case "jobs.cancel":
                if (current == null || request.JobId != currentJobId) return false;
                current.Cancel();
                return true;
        }
        if (current != null) throw new UserError("busy", "Дождитесь завершения текущей операции или отмените её.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        current = operation;
        currentJobId = request.Id;
        bool holdsHeavyGate = false;
        try
        {
            if (!heavyGate.Wait(0))
            {
                Stage(request.Id, "Ожидаем завершения текущей обработки");
                await heavyGate.WaitAsync(operation.Token);
            }
            holdsHeavyGate = true;
            var progress = new Progress<ImportProgress>(update =>
            {
                if (currentJobId == request.Id) Stage(request.Id, update.Stage, update.Percent);
            });
            if (request.Type is "editor.srt" or "editor.captionVideo")
            {
                bool video = request.Type == "editor.captionVideo";
                var dialog = new SaveFileDialog
                {
                    Title = video ? "Сохранить клип MP4" : "Сохранить субтитры SRT",
                    Filter = video ? "Видео MP4|*.mp4" : "Субтитры SRT|*.srt",
                    DefaultExt = video ? ".mp4" : ".srt", AddExtension = true, OverwritePrompt = !video,
                    InitialDirectory = pickerDirectory ?? "", FileName = video ? "Клип.mp4" : "Субтитры.srt", AddToRecent = false
                };
                if (dialog.ShowDialog(this) != true) return false;
                var destination = dialog.FileName;
                if (video)
                    await Task.Run(() => db.RenderCaptionClipAsync(request.ProjectId!.Value,request.ClipId!.Value,
                        request.Revision,destination,CaptionTool("ffmpeg"),CaptionTool("ffprobe"),operation.Token));
                else
                    await Task.Run(() => db.WriteCaptionSrtAsync(request.ProjectId!.Value,request.ClipId!.Value,
                        request.Revision,destination,operation.Token));
                return true;
            }
            if (request.Type == "editor.autoFrame")
            {
                var clip = await Task.Run(() => db.AutoFrameAsync(request.ProjectId!.Value, request.ClipId!.Value,
                    request.Revision, request.IntervalMs, CaptionTool("ffmpeg"), CaptionTool("ffprobe"), progress, operation.Token));
                resources.InvalidatePreview(clip.Id, clip.Revision);
                return clip;
            }
            if (request.Type == "editor.preview")
            {
                var clipId = request.ClipId!.Value;
                var file = await Task.Run(() => db.RenderCaptionPreviewAsync(request.ProjectId!.Value, clipId, request.Revision,
                    request.OffsetUs, CaptionTool("ffmpeg"), CaptionTool("ffprobe"), operation.Token));
                return new { url = resources.RegisterPreview(clipId, request.Revision, file), request.Revision };
            }
            if (request.Type == "clips.mark")
                return Describe(await Task.Run(() => db.MarkClip(request.ProjectId!.Value, request.SelectionId!.Value, request.StartId!.Value, request.ClipState!)));
            if (request.Type == "clips.find")
                return Describe(await Task.Run(() => db.FindClipsAsync(request.ProjectId!.Value, request.Search!, operation.Token, progress)));
            if (request.Type == "transcript.start")
            {
                return Describe(await Task.Run(() => db.TranscribeAsync(request.ProjectId!.Value, request.AudioTrack!.Value,
                    request.Language!, tools.Resolve("ffprobe"), tools.Resolve("ffmpeg"), operation.Token, progress)));
            }
            if (request.Type == "media.measureFrameIntervals")
            {
                return Describe(await Task.Run(() => db.MeasureFrameIntervalsAsync(request.ProjectId!.Value,
                    tools.Resolve("ffprobe") ?? throw new UserError("missing_ffprobe", "Выберите папку с ffprobe.exe в настройках."),
                    operation.Token, progress)));
            }
            if (request.Type is "media.createProxy" or "media.rebuildProxy")
            {
                var converted = await Task.Run(() => db.CreateProxyAsync(request.ProjectId!.Value, () => (
                    tools.Resolve("ffprobe") ?? throw new UserError("missing_ffprobe", "Выберите папку с ffprobe.exe в настройках."),
                    tools.Resolve("ffmpeg") ?? throw new UserError("missing_ffmpeg", "Для прокси нужен ffmpeg.exe. Выберите его папку в настройках.")),
                    operation.Token, progress, request.AudioTrack, request.ProxyId));
                return Describe(converted);
            }
            var path = dropped ?? PickVideo();
            if (path == null) return null;
            if (!Path.IsPathFullyQualified(path) || !Path.IsPathRooted(path) ||
                new Uri(path).IsUnc || !File.Exists(path))
                throw new UserError("local_file", "Выберите существующий видеофайл на локальном диске.");
            bool relink = request.Type == "media.relink";
            Stage(request.Id, relink ? "Проверяем соответствие файла" : "probe");
            var project = await Task.Run(async () =>
            {
                if (relink) return await db.RelinkAsync(request.ProjectId!.Value, path, operation.Token);
                var probe = tools.Resolve("ffprobe") ?? throw new UserError("missing_ffprobe", "Для импорта нужен ffprobe.exe. Укажите папку FFmpeg в настройках.");
                return await db.ImportAsync(path, probe, tools.Resolve("ffmpeg"), operation.Token, progress);
            });
            return Describe(project);
        }
        finally
        {
            if (holdsHeavyGate) heavyGate.Release();
            current = null;
            currentJobId = null;
            Stage(request.Id, "");
        }
    }

    private async Task<bool> StartExportsAsync(Library db, BridgeRequest request)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Папка для экспорта", InitialDirectory = pickerDirectory ?? "", AddToRecent = false
        };
        if (dialog.ShowDialog(this) != true) return false;
        string directoryName = dialog.FolderName;
        var batch = request.Batch!;
        var projectId = request.ProjectId!.Value;
        await Task.Run(() =>
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var ids = batch.ClipIds.ToHashSet();
            if (request.SelectionId is Guid selectionId)
            {
                var selection = db.Open(projectId).Selection;
                if (selection?.Id != selectionId || !batch.CandidateStarts.All(start =>
                    selection.Clips.Any(clip => clip.StartId == start && clip.State == "selected")))
                    throw new UserError("selection_changed", "Подборка изменилась. Откройте проект и выберите клипы снова.");
                foreach (int start in batch.CandidateStarts)
                    ids.Add(db.CreateEdit(projectId, selectionId, start).Id);
            }
            db.EnqueueExports(projectId, ids.ToArray(), directoryName, new ExportSettings(batch.Fps,
                batch.NormalizeAudio, batch.IncludeSrt, batch.IncludeManifest, batch.PreferNvenc));
        }, lifetime.Token);
        WakeExports(db);
        return true;
    }

    private void WakeExports(Library db)
    {
        if (closing) return;
        exportWakeRequested = true;
        if (exportWorkerActive) return;
        exportWorkerActive = true;
        queueFailure = null;
        var work = DrainAsync();
        pending.Add(work);
        _ = ForgetWhenDoneAsync(work);
        async Task DrainAsync()
        {
            try
            {
                while (exportWakeRequested && !closing)
                {
                    exportWakeRequested = false;
                    await db.RunExportQueueAsync(() => (CaptionTool("ffmpeg"), CaptionTool("ffprobe")), lifetime.Token, heavyGate);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                queueFailure = "Не удалось обработать очередь. Проверьте доступ к локальному хранилищу и возобновите экспорт.";
                try
                {
                    var logs = Directory.CreateDirectory(Path.Combine(directory, "logs"));
                    var frames = new System.Diagnostics.StackTrace(ex).GetFrames();
                    var calls = string.Join(" ", frames.Select(f => f.GetMethod()?.Name).Take(8));
                    await File.AppendAllTextAsync(Path.Combine(logs.FullName, "desktop.log"),
                        $"{DateTimeOffset.UtcNow:O} export_worker {ex.GetType().Name} {ex.HResult:X8} {calls}\n");
                }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            }
            finally { exportWorkerActive = false; }
        }
        async Task ForgetWhenDoneAsync(Task task)
        {
            await task;
            pending.Remove(task);
        }
    }

    private string CaptionTool(string name) => tools.Resolve(name) ??
        throw new UserError("missing_" + name, $"Для видео с субтитрами нужен {name}.exe. Выберите папку FFmpeg в настройках.");

    private void Stage(Guid jobId, string stage, int? percent = null) =>
        Send(new { version = 1, type = "import.stage", payload = new { jobId, stage, percent } });

    private string? PickVideo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите локальное видео", CheckFileExists = true,
            InitialDirectory = pickerDirectory ?? "", AddToRecent = false,
            Filter = "Видеофайлы|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.ts;*.mts;*.mpg;*.wmv|Все файлы|*.*"
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async Task<object> DiagnosticsAsync()
    {
        var webView = view.CoreWebView2.Environment.BrowserVersionString;
        return await Task.Run(async () =>
        {
            string? directory;
            try { directory = tools.ReadDirectory(); }
            catch (UserError error) when (error.Code == "tool_settings")
            {
                return new { ffmpeg = (string?)null, ffprobe = (string?)null, proxyEncoders = (ProxyEncoders?)null, webView, customFolder = true, settingsError = (string?)error.Message };
            }
            async Task<string?> Version(string name, string? exe)
            {
                if (exe == null) return null;
                var version = await Library.ToolVersionAsync(exe, lifetime.Token);
                return version?.StartsWith(name + " version ", StringComparison.OrdinalIgnoreCase) == true ? version : null;
            }
            var ffmpeg = ToolSettings.Resolve("ffmpeg", directory);
            var versions = await Task.WhenAll(Version("ffmpeg", ffmpeg), Version("ffprobe", ToolSettings.Resolve("ffprobe", directory)));
            var proxyEncoders = ffmpeg != null && versions[0] != null ? await ProxyEncoders.ReadAsync(ffmpeg, lifetime.Token) : null;
            return new { ffmpeg = versions[0], ffprobe = versions[1], proxyEncoders, webView, customFolder = directory != null, settingsError = (string?)null };
        });
    }

    private object Describe(MediaProject project)
    {
        resources.Register(project);
        return new
        {
            project.Id, project.Name, project.UpdatedUtc, project.DurationUs, project.Width, project.Height,
            project.VideoCodec, project.Container, project.AudioTracks, project.Fps, project.Rotation, project.SourceAvailable, project.Color, project.AnamorphicAspect,
            usingProxy = project.ProxyPath != null, project.ProxyId, project.ProxyNeedsRefresh, project.ProxyAudioTrack, project.AudioTrackLabels, project.OfflineAudioTracks, project.FrameIntervals, project.Transcript,
            project.Selection, project.Edits,
            previewUrl = project.ProxyPath != null
                ? $"https://{BridgeRequest.Host}/proxy/{project.Id:D}/{Path.GetFileName(project.ProxyPath)}"
                : project.SourceAvailable ? $"https://{BridgeRequest.Host}/media/{project.Id:D}" : null,
            thumbnailUrl = project.ThumbnailPath != null ? $"https://{BridgeRequest.Host}/thumb/{project.Id:D}" : null
        };
    }

    private async void Shutdown(object? sender, CancelEventArgs e)
    {
        if (drained) return;
        e.Cancel = true;
        if (closing || preparingClose) return;
        preparingClose = true;
        var web = view.CoreWebView2;
        if (web != null && BridgeRequest.IsPage(web.Source))
        {
            flushId = Guid.NewGuid();
            editorFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                string accepted = await web.ExecuteScriptAsync(
                    $"window.dispatchEvent(new CustomEvent('shortforge-flush-editor',{{cancelable:true,detail:'{flushId:D}'}}))")
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (accepted == "false") await editorFlush.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or COMException)
            {
                if (MessageBox.Show(this, "Не удалось подтвердить сохранение правок. Закрыть приложение?",
                    "ShortForge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    preparingClose = false;
                    return;
                }
            }
            finally { editorFlush = null; }
        }
        closing = true;
        lifetime.Cancel();
        await Task.Yield();
        try { await Task.WhenAll(pending.ToArray()); }
        finally { resources.ClearPreview(); drained = true; Close(); }
    }
}
