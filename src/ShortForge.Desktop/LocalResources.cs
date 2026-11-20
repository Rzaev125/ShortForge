using System.IO;
using Microsoft.Web.WebView2.Core;
using ShortForge.Core;
using ShortForge.Infrastructure;

namespace ShortForge.Desktop;

// The browser receives opaque URLs. Only native code can register media paths.
internal sealed class LocalResources
{
    private readonly Dictionary<Guid, MediaProject> projects = new();
    private readonly string ui = Path.Combine(AppContext.BaseDirectory, "ui");
    private const string Headers = "X-Content-Type-Options: nosniff\r\nCache-Control: no-store";
    private const int MediaChunkBytes = 2 * 1024 * 1024;

    public void Register(MediaProject project) => projects[project.Id] = project;

    private (Guid Token, Guid ClipId, int Revision, string Path)? preview;

    public string RegisterPreview(Guid clipId, int revision, string file)
    {
        ClearPreview();
        var token = Guid.NewGuid();
        preview = (token, clipId, revision, file);
        return $"https://{BridgeRequest.Host}/caption-preview/{token:D}";
    }

    public void InvalidatePreview(Guid clipId, int revision)
    {
        if (preview is {} old && old.ClipId == clipId && old.Revision < revision) ClearPreview();
    }

    public void ClearPreview()
    {
        if (preview is not {} old) return;
        preview = null;
        try { File.Delete(old.Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Serve(CoreWebView2 web, CoreWebView2WebResourceRequestedEventArgs e)
    {
        CoreWebView2WebResourceResponse Response(Stream? content, int status, string reason, string headers) =>
            web.Environment.CreateWebResourceResponse(content, status, reason, headers);
        try
        {
            var request = e.Request;
            var uri = new Uri(request.Uri);
            if (uri.Scheme != "https" || uri.IdnHost != BridgeRequest.Host || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || request.Method is not ("GET" or "HEAD"))
            {
                e.Response = Response(null, 403, "Forbidden", Headers);
                return;
            }
            string? path = null, mime = null;
            var parts = uri.AbsolutePath.Split('/');
            if (uri.AbsolutePath == "/index.html")
            {
                path = Path.Combine(ui, "index.html");
                mime = "text/html; charset=utf-8";
            }
            else if (parts.Length == 3 && parts[1] == "assets" && parts[2].Length > 0 &&
                parts[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                mime = Path.GetExtension(parts[2]) switch
                {
                    ".js" => "application/javascript", ".css" => "text/css", ".woff2" => "font/woff2",
                    ".png" => "image/png", ".svg" => "image/svg+xml", _ => null
                };
                if (mime != null) path = Path.Combine(ui, "assets", parts[2]);
            }
            else if (parts.Length == 3 && parts[1] == "caption-preview" && preview is {} rendered &&
                Guid.TryParseExact(parts[2], "D", out var token) && token == rendered.Token)
            {
                path = rendered.Path;
                mime = "video/mp4";
            }
            else if (parts.Length is 3 or 4 && Guid.TryParseExact(parts[2], "D", out var id) &&
                projects.TryGetValue(id, out var project))
            {
                if (parts.Length == 3 && parts[1] == "media" && project.SourceAvailable)
                {
                    path = project.OriginalPath;
                    mime = project.Container.Split(',')[0] switch
                    {
                        "mov" => "video/mp4",
                        "matroska" when Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase) => "video/webm",
                        "matroska" => "video/x-matroska", "avi" => "video/x-msvideo",
                        "mpegts" => "video/mp2t", _ => "application/octet-stream"
                    };
                }
                else if (parts.Length == 4 && parts[1] == "proxy" && parts[3] == Path.GetFileName(project.ProxyPath))
                { path = project.ProxyPath; mime = "video/mp4"; }
                else if (parts.Length == 3 && parts[1] == "thumb") { path = project.ThumbnailPath; mime = "image/jpeg"; }
            }
            if (path == null) { e.Response = Response(null, 404, "Not Found", Headers); return; }
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            bool handedOff = false;
            try
            {
                var size = file.Length;
                var rangeHeader = request.Headers.Contains("Range") ? request.Headers.GetHeader("Range") : null;
                var range = ByteRange.Resolve(rangeHeader, size);
                if (range == null)
                {
                    e.Response = Response(null, 416, "Range Not Satisfiable", $"{Headers}\r\nContent-Range: bytes */{size}");
                    return;
                }
                var (start, length) = range.Value;
                // Chromium requests bytes=0- even for GiB-sized videos. Limit each
                // WebView2 interop response and advertise the actual range delivered.
                if (parts.Length >= 3 && parts[1] is "media" or "proxy" or "caption-preview" && request.Method == "GET" && length > MediaChunkBytes)
                {
                    if (rangeHeader == null)
                    {
                        e.Response = Response(null, 400, "Range required", Headers + "\r\nAccept-Ranges: bytes");
                        return;
                    }
                    length = MediaChunkBytes;
                }
                var headers = $"{Headers}\r\nAccept-Ranges: bytes\r\nContent-Type: {mime}\r\nContent-Length: {length}";
                if (rangeHeader != null) headers += $"\r\nContent-Range: bytes {start}-{start + length - 1}/{size}";
                if (mime!.StartsWith("text/html", StringComparison.Ordinal))
                    headers += "\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; media-src 'self'; connect-src 'self'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'";
                Stream? body = request.Method == "HEAD" ? null : new FileSlice(file, start, length);
                e.Response = Response(body, rangeHeader == null ? 200 : 206, rangeHeader == null ? "OK" : "Partial Content", headers);
                handedOff = body != null;
            }
            finally { if (!handedOff) file.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException)
        {
            e.Response = Response(null, 404, "Not Found", Headers);
        }
    }
}

internal sealed class FileSlice(FileStream file, long start, long length) : Stream
{
    private long position;
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => true;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = checked(offset + (origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length, _ => throw new ArgumentException() }));
        if (target < 0 || target > length) throw new IOException("Invalid range");
        return position = target;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytes = RandomAccess.Read(file.SafeFileHandle, buffer.AsSpan(offset, (int)Math.Min(count, length - position)), start + position);
        position += bytes;
        return bytes;
    }
    protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
