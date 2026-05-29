using System.Net;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MtVid;

internal sealed class LocalVideoServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonBodyOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpListener _listener;
    private readonly bool _enableUi;
    private readonly object _sessionLock = new();
    private readonly ConcurrentDictionary<string, PackJobState> _packJobs = new(StringComparer.Ordinal);
    private LoadedPackageSession? _session;
    private readonly CancellationTokenSource _cts = new();

    public LocalVideoServer(string? packagePath, string? password, int port, bool enableUi = false)
    {
        _enableUi = enableUi;

        if (!string.IsNullOrWhiteSpace(packagePath))
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException("Password is required when an input package is provided.");
            }

            _session = CreateSession(packagePath, password);
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(ListenLoop);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }

        foreach ((_, PackJobState job) in _packJobs)
        {
            TryDelete(job.OutputPath);
            TryDelete(job.InputPath);
        }

        _packJobs.Clear();
        _cts.Dispose();
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => ProcessRequest(context));
        }
    }

    private void ProcessRequest(HttpListenerContext context)
    {
        try
        {
            HttpListenerRequest request = context.Request;
            string path = request.Url?.AbsolutePath ?? string.Empty;

            if (_enableUi && path.Equals("/api/open", StringComparison.OrdinalIgnoreCase))
            {
                ProcessOpenRequest(context);
                return;
            }

            if (_enableUi && path.Equals("/api/pack", StringComparison.OrdinalIgnoreCase))
            {
                ProcessPackRequest(context);
                return;
            }

            if (_enableUi && path.Equals("/api/open-upload", StringComparison.OrdinalIgnoreCase))
            {
                ProcessOpenUploadRequest(context);
                return;
            }

            if (_enableUi && path.Equals("/api/pack-upload", StringComparison.OrdinalIgnoreCase))
            {
                ProcessPackUploadRequest(context);
                return;
            }

            if (_enableUi && path.StartsWith("/api/pack-jobs/", StringComparison.OrdinalIgnoreCase))
            {
                ProcessPackJobRequest(context, path);
                return;
            }

            if (_enableUi && path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
            {
                WriteStatus(context.Response);
                return;
            }

            if (_enableUi && path.Equals("/api/pick-file", StringComparison.OrdinalIgnoreCase))
            {
                ProcessPickFileRequest(context);
                return;
            }

            if (_enableUi && path.Equals("/api/package-info", StringComparison.OrdinalIgnoreCase))
            {
                ProcessPackageInfoRequest(context);
                return;
            }

            if (_enableUi && (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)))
            {
                WriteHomePage(context.Response);
                return;
            }

            if (_enableUi && path.Equals("/encrypt", StringComparison.OrdinalIgnoreCase))
            {
                WriteUiPage(context.Response, "encrypt");
                return;
            }

            if (_enableUi && path.Equals("/play", StringComparison.OrdinalIgnoreCase))
            {
                WriteUiPage(context.Response, "play");
                return;
            }

            if (!path.Equals("/stream", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            if (request.HttpMethod is not "GET" and not "HEAD")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            LoadedPackageSession? session;
            lock (_sessionLock)
            {
                session = _session;
            }

            if (session is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                context.Response.Close();
                return;
            }

            long totalLength = session.Header.OriginalLength;
            bool hasRange = TryParseRange(request.Headers["Range"], totalLength, out long start, out long end);

            if (request.Headers["Range"] is not null && !hasRange)
            {
                context.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                context.Response.Headers["Content-Range"] = $"bytes */{totalLength}";
                context.Response.Close();
                return;
            }

            if (!hasRange)
            {
                start = 0;
                end = totalLength > 0 ? totalLength - 1 : 0;
            }

            long responseLength = totalLength == 0 ? 0 : end - start + 1;

            context.Response.ContentType = session.Header.ContentType;
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.StatusCode = hasRange ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
            context.Response.ContentLength64 = responseLength;

            if (hasRange)
            {
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{totalLength}";
            }

            if (request.HttpMethod == "HEAD" || responseLength == 0)
            {
                context.Response.Close();
                return;
            }

            using PackageDecryptingStream stream = new(session.PackagePath, session.Header, session.EncryptionKey);
            stream.Seek(start, SeekOrigin.Begin);
            CopyExactly(stream, context.Response.OutputStream, responseLength);
            context.Response.OutputStream.Close();
        }
        catch
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }

            context.Response.Close();
        }
    }

    private void ProcessOpenRequest(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
            string body = reader.ReadToEnd();
                OpenRequest? payload = JsonSerializer.Deserialize<OpenRequest>(body, JsonBodyOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.PackagePath) || string.IsNullOrWhiteSpace(payload.Password))
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "packagePath and password are required." });
                return;
            }

            LoadedPackageSession next = CreateSession(payload.PackagePath, payload.Password);
            LoadedPackageSession? previous;

            lock (_sessionLock)
            {
                previous = _session;
                _session = next;
            }

            previous?.Dispose();

            WriteJson(context.Response, HttpStatusCode.OK, new
            {
                ok = true,
                message = "Package opened.",
                fileName = next.DisplayFileName,
                contentType = next.Header.ContentType
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteJson(context.Response, HttpStatusCode.Unauthorized, new { ok = false, message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            WriteJson(context.Response, HttpStatusCode.NotFound, new { ok = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = ex.Message });
        }
    }

    private void ProcessPackRequest(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
            string body = reader.ReadToEnd();
                PackRequest? payload = JsonSerializer.Deserialize<PackRequest>(body, JsonBodyOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.InputPath) || string.IsNullOrWhiteSpace(payload.OutputPath) || string.IsNullOrWhiteSpace(payload.Password))
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "inputPath, outputPath and password are required." });
                return;
            }

            int chunkMb = payload.ChunkMb.GetValueOrDefault(2);
            if (chunkMb <= 0 || chunkMb > 32)
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "chunkMb must be in range 1-32." });
                return;
            }

            int iterations = payload.Iterations.GetValueOrDefault(210000);
            if (iterations < 50000)
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "iterations must be >= 50000." });
                return;
            }

            string? outputDirectory = Path.GetDirectoryName(payload.OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            string contentType = GuessContentType(payload.InputPath);
            VideoPackager.EncryptVideo(payload.InputPath, payload.OutputPath, payload.Password, chunkMb * 1024 * 1024, contentType, iterations);

            WriteJson(context.Response, HttpStatusCode.OK, new
            {
                ok = true,
                message = "Package created.",
                outputPath = payload.OutputPath
            });
        }
        catch (FileNotFoundException ex)
        {
            WriteJson(context.Response, HttpStatusCode.NotFound, new { ok = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = ex.Message });
        }
    }

    private void ProcessOpenUploadRequest(HttpListenerContext context)
    {
        string? tempFile = null;
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            string password = context.Request.Headers["X-Password"] ?? string.Empty;
            string fileName = context.Request.Headers["X-File-Name"] ?? "upload.mtaf";
            if (string.IsNullOrWhiteSpace(password))
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "Password header is required." });
                return;
            }

            tempFile = Path.Combine(Path.GetTempPath(), $"mtvid-open-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
            using (FileStream outFile = new(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                context.Request.InputStream.CopyTo(outFile);
            }

            LoadedPackageSession next = CreateSession(tempFile, password, deleteOnDispose: true, preferredDisplayName: fileName);
            tempFile = null;

            LoadedPackageSession? previous;
            lock (_sessionLock)
            {
                previous = _session;
                _session = next;
            }

            previous?.Dispose();

            WriteJson(context.Response, HttpStatusCode.OK, new
            {
                ok = true,
                message = "Package uploaded and opened.",
                fileName = next.DisplayFileName,
                contentType = next.Header.ContentType
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteJson(context.Response, HttpStatusCode.Unauthorized, new { ok = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = ex.Message });
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    private void ProcessPackUploadRequest(HttpListenerContext context)
    {
        string? tempInput = null;
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            string password = context.Request.Headers["X-Password"] ?? string.Empty;
            string sourceName = context.Request.Headers["X-File-Name"] ?? "video.bin";
            string outputName = context.Request.Headers["X-Output-Name"] ?? BuildAbbreviatedMtafName(sourceName);
            int chunkMb = 2;
            string? chunkHeader = context.Request.Headers["X-Chunk-Mb"];
            if (!string.IsNullOrWhiteSpace(chunkHeader) && (!int.TryParse(chunkHeader, out chunkMb) || chunkMb is <= 0 or > 32))
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "X-Chunk-Mb must be in range 1-32." });
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "Password header is required." });
                return;
            }

            string srcExt = Path.GetExtension(sourceName);
            tempInput = Path.Combine(Path.GetTempPath(), $"mtvid-src-{Guid.NewGuid():N}{srcExt}");
            string tempOutput = Path.Combine(Path.GetTempPath(), $"mtvid-out-{Guid.NewGuid():N}.mtaf");

            using (FileStream outFile = new(tempInput, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                context.Request.InputStream.CopyTo(outFile);
            }

            string contentType = GuessContentType(sourceName);
            string jobId = Guid.NewGuid().ToString("N");
            PackJobState job = new()
            {
                JobId = jobId,
                InputPath = tempInput,
                OutputPath = tempOutput,
                OutputFileName = SanitizeFileName(outputName),
                SourceContentType = contentType,
                State = "processing",
                ProgressPercent = 0
            };

            tempInput = null;
            _packJobs[jobId] = job;

            _ = Task.Run(() => RunPackJob(job, password, chunkMb));

            WriteJson(context.Response, HttpStatusCode.OK, new
            {
                ok = true,
                jobId,
                state = job.State,
                progressPercent = job.ProgressPercent
            });
        }
        catch (Exception ex)
        {
            WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = ex.Message });
        }
        finally
        {
            TryDelete(tempInput);
        }
    }

    private void ProcessPackJobRequest(HttpListenerContext context, string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }

        string jobId = parts[2];
        if (!_packJobs.TryGetValue(jobId, out PackJobState? job))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }

        bool isDownload = parts.Length >= 4 && parts[3].Equals("download", StringComparison.OrdinalIgnoreCase);
        if (isDownload)
        {
            if (!string.Equals(job.State, "completed", StringComparison.Ordinal))
            {
                WriteJson(context.Response, HttpStatusCode.Conflict, new { ok = false, message = "Job is not completed yet." });
                return;
            }

            if (!File.Exists(job.OutputPath))
            {
                WriteJson(context.Response, HttpStatusCode.Gone, new { ok = false, message = "Output file is no longer available." });
                return;
            }

            try
            {
                using FileStream file = new(job.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{job.OutputFileName}\"";
                context.Response.ContentLength64 = file.Length;
                file.CopyTo(context.Response.OutputStream);
                context.Response.OutputStream.Close();
            }
            finally
            {
                TryDelete(job.OutputPath);
                TryDelete(job.InputPath);
                _packJobs.TryRemove(jobId, out _);
            }

            return;
        }

        WriteJson(context.Response, HttpStatusCode.OK, new
        {
            ok = true,
            jobId = job.JobId,
            state = job.State,
            progressPercent = job.ProgressPercent,
            error = job.ErrorMessage
        });
    }

    private void RunPackJob(PackJobState job, string password, int chunkMb)
    {
        try
        {
            job.State = "processing";
            VideoPackager.EncryptVideo(
                job.InputPath,
                job.OutputPath,
                password,
                chunkMb * 1024 * 1024,
                job.SourceContentType,
                210000,
                (processedBytes, totalBytes) =>
                {
                    if (totalBytes <= 0)
                    {
                        job.ProgressPercent = 100;
                        return;
                    }

                    int percent = (int)Math.Clamp((processedBytes * 100L) / totalBytes, 0, 100);
                    job.ProgressPercent = percent;
                });

            job.ProgressPercent = 100;
            job.State = "completed";
        }
        catch (Exception ex)
        {
            job.State = "failed";
            job.ErrorMessage = ex.Message;
            TryDelete(job.OutputPath);
        }
        finally
        {
            TryDelete(job.InputPath);
        }
    }

    private void WriteStatus(HttpListenerResponse response)
    {
        LoadedPackageSession? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session is null)
        {
            WriteJson(response, HttpStatusCode.OK, new { ok = true, loaded = false });
            return;
        }

        WriteJson(response, HttpStatusCode.OK, new
        {
            ok = true,
            loaded = true,
            fileName = session.DisplayFileName,
            contentType = session.Header.ContentType
        });
    }

    private void ProcessPickFileRequest(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "GET")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            string kind = (context.Request.QueryString["kind"] ?? "mtaf").ToLowerInvariant();
            bool multi = bool.TryParse(context.Request.QueryString["multi"], out bool parsed) && parsed;

            if (multi)
            {
                if (!TryPickFilePaths(kind, out List<string>? pickedPaths, out string? multiError))
                {
                    WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = multiError ?? "Files could not be selected." });
                    return;
                }

                WriteJson(context.Response, HttpStatusCode.OK, new { ok = true, paths = pickedPaths });
                return;
            }

            if (!TryPickFilePath(kind, out string? pickedPath, out string? error))
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = error ?? "File could not be selected." });
                return;
            }

            WriteJson(context.Response, HttpStatusCode.OK, new { ok = true, path = pickedPath });
        }
        catch (Exception ex)
        {
            WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = ex.Message });
        }
    }

    private void ProcessPackageInfoRequest(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
            string body = reader.ReadToEnd();
            PackageInfoRequest? payload = JsonSerializer.Deserialize<PackageInfoRequest>(body, JsonBodyOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.PackagePath))
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "packagePath is required." });
                return;
            }

            using FileStream fs = new(payload.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            PackageHeader header = PackageHeader.ReadFrom(fs);

            WriteJson(context.Response, HttpStatusCode.OK, new
            {
                ok = true,
                fileName = Path.GetFileName(payload.PackagePath),
                originalFileName = header.OriginalFileName,
                hasOriginalFileName = !string.IsNullOrWhiteSpace(header.OriginalFileName)
            });
        }
        catch (FileNotFoundException ex)
        {
            WriteJson(context.Response, HttpStatusCode.NotFound, new { ok = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = ex.Message });
        }
    }

    private static bool TryPickFilePath(string kind, out string? pickedPath, out string? error)
    {
        pickedPath = null;
        error = null;

        if (!OperatingSystem.IsMacOS())
        {
            error = "Native picker is currently supported on macOS only.";
            return false;
        }

        string script = "set f to choose file\nPOSIX path of f";
        ProcessStartInfo psi = new()
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start osascript.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            if (stderr.Contains("User canceled", StringComparison.OrdinalIgnoreCase))
            {
                error = "Dosya secimi iptal edildi.";
            }
            else
            {
                error = string.IsNullOrWhiteSpace(stderr) ? "Dosya secilemedi." : stderr.Trim();
            }

            return false;
        }

        pickedPath = stdout.Trim();
        if (string.IsNullOrWhiteSpace(pickedPath))
        {
            error = "Dosya secilemedi.";
            return false;
        }

        if (kind == "mtaf" && !pickedPath.EndsWith(".mtaf", StringComparison.OrdinalIgnoreCase))
        {
            error = "Lutfen .mtaf uzantili bir dosya sec.";
            pickedPath = null;
            return false;
        }

        return true;
    }

    private static bool TryPickFilePaths(string kind, out List<string>? pickedPaths, out string? error)
    {
        pickedPaths = null;
        error = null;

        if (!OperatingSystem.IsMacOS())
        {
            error = "Native picker is currently supported on macOS only.";
            return false;
        }

        string script = "set fList to choose file with multiple selections allowed\nset out to \"\"\nrepeat with f in fList\nset out to out & POSIX path of f & linefeed\nend repeat\nreturn out";
        ProcessStartInfo psi = new()
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start osascript.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            if (stderr.Contains("User canceled", StringComparison.OrdinalIgnoreCase))
            {
                error = "Dosya secimi iptal edildi.";
            }
            else
            {
                error = string.IsNullOrWhiteSpace(stderr) ? "Dosyalar secilemedi." : stderr.Trim();
            }

            return false;
        }

        List<string> items = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (items.Count == 0)
        {
            error = "Dosya secilemedi.";
            return false;
        }

        if (kind == "mtaf")
        {
            items = items.Where(static p => p.EndsWith(".mtaf", StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count == 0)
            {
                error = "Lutfen en az bir .mtaf dosyasi sec.";
                return false;
            }
        }

        pickedPaths = items;
        return true;
    }

        private static void WriteHomePage(HttpListenerResponse response)
        {
                string html = """
<!doctype html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>MtVid</title>
    <style>
        body { margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: "Avenir Next", "Segoe UI", sans-serif; background: #0f1b22; color: #f2eadc; }
        .card { width: min(680px, 92%); background: #18252c; border-radius: 16px; padding: 24px; box-shadow: 0 20px 60px rgba(0,0,0,.35); }
        h1 { margin: 0 0 8px; }
        p { margin: 0 0 16px; color: #c8d1d6; }
        .row { display: flex; gap: 10px; flex-wrap: wrap; }
        a { text-decoration: none; color: #fff; padding: 10px 14px; border-radius: 999px; background: #2f7f72; }
        a.alt { background: #d95d39; }
    </style>
</head>
<body>
    <main class="card">
        <h1>MtVid</h1>
        <p>Sifreleme ve oynatma sayfalari ayrildi.</p>
        <div class="row">
            <a class="alt" href="/encrypt">Sifreleme Sayfasi</a>
            <a href="/play">Oynatma Sayfasi</a>
        </div>
    </main>
</body>
</html>
""";

                byte[] payload = Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = payload.Length;
                response.OutputStream.Write(payload, 0, payload.Length);
                response.OutputStream.Close();
        }

                private static void WriteUiPage(HttpListenerResponse response, string mode)
        {
                string html = """
<!doctype html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>MtVid Player</title>
    <style>
        :root {
            --bg-a: #0e141b;
            --bg-b: #132a2f;
            --card: #f4efe4;
            --ink: #1b1f24;
            --accent: #d95d39;
            --accent-2: #2f7f72;
            --muted: #5f656d;
        }

        * { box-sizing: border-box; }

        body {
            margin: 0;
            min-height: 100vh;
            font-family: "Avenir Next", "Segoe UI", "Noto Sans", sans-serif;
            color: var(--ink);
            background:
                radial-gradient(1000px 520px at -8% -12%, #2e9f8e55 0%, transparent 65%),
                radial-gradient(800px 450px at 110% 105%, #f07f5a66 0%, transparent 62%),
                linear-gradient(130deg, var(--bg-a), var(--bg-b));
            display: grid;
            place-items: center;
            padding: 20px;
        }

        .shell {
            width: min(980px, 100%);
            border-radius: 22px;
            overflow: hidden;
            background: var(--card);
            box-shadow: 0 28px 80px rgba(0, 0, 0, 0.38);
            transform: translateY(14px);
            opacity: 0;
            animation: rise 560ms cubic-bezier(.19,.9,.2,1) forwards;
        }

        body.page-app {
            display: block;
            padding: 0;
        }

        body.page-app .shell {
            width: 100vw;
            min-height: 100vh;
            border-radius: 0;
            box-shadow: none;
            transform: none;
            opacity: 1;
            animation: none;
        }

        body.page-app .body {
            padding: 20px;
        }

        .top {
            padding: 16px 20px;
            background: linear-gradient(90deg, #ece5d6, #f7f2e8);
            border-bottom: 1px solid #d9d2c5;
            display: flex;
            gap: 12px;
            align-items: center;
            justify-content: space-between;
            flex-wrap: wrap;
        }

        .brand {
            font-family: "Iowan Old Style", "Palatino Linotype", serif;
            letter-spacing: .3px;
            font-size: clamp(18px, 2.3vw, 24px);
        }

        .dot {
            width: 9px;
            height: 9px;
            border-radius: 99px;
            background: var(--accent);
            display: inline-block;
            margin-right: 8px;
            box-shadow: 0 0 0 6px #d95d3922;
        }

        .meta {
            color: var(--muted);
            font-size: 13px;
        }

        .body {
            padding: 18px;
        }

        .panel {
            margin-bottom: 14px;
            padding: 12px;
            border: 1px solid #d8d0c3;
            border-radius: 12px;
            background: #fbf8f1;
            display: grid;
            gap: 8px;
        }

        .row {
            display: grid;
            grid-template-columns: 1fr;
            gap: 8px;
        }

        .row-grid {
            display: grid;
            grid-template-columns: 1fr 140px;
            gap: 8px;
        }

        .label {
            font-size: 13px;
            color: var(--muted);
        }

        input[type="text"],
        input[type="password"] {
            width: 100%;
            border: 1px solid #cfc5b3;
            border-radius: 10px;
            padding: 11px 12px;
            font-size: 14px;
            background: #fff;
            color: var(--ink);
        }

        button {
            width: fit-content;
            border: 0;
            border-radius: 999px;
            padding: 10px 16px;
            font-size: 14px;
            background: linear-gradient(135deg, var(--accent), #ea7b59);
            color: #fff;
            cursor: pointer;
        }

        button.alt {
            background: linear-gradient(135deg, var(--accent-2), #49a592);
        }

        .status {
            font-size: 13px;
            color: var(--muted);
            min-height: 18px;
        }

        .progress-wrap {
            width: 100%;
            height: 10px;
            border-radius: 999px;
            background: #ddd3c0;
            overflow: hidden;
        }

        .progress-bar {
            width: 0%;
            height: 100%;
            background: linear-gradient(90deg, var(--accent-2), #78bcae);
            transition: width .2s ease;
        }

        .play-layout {
            display: grid;
            grid-template-columns: minmax(280px, 340px) minmax(0, 1fr) minmax(260px, 320px);
            gap: 14px;
            align-items: start;
        }

        .play-main {
            display: grid;
            gap: 14px;
        }

        .player-surface {
            border-radius: 16px;
            overflow: hidden;
            background: #000;
        }

        .player-surface video {
            width: 100%;
            aspect-ratio: 16 / 9;
            height: auto;
            max-height: none;
            border-radius: 0;
            outline: none;
            box-shadow: none;
        }

        .video-meta {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 12px;
            flex-wrap: wrap;
        }

        .video-title {
            font-size: clamp(16px, 2.2vw, 22px);
            font-weight: 700;
            letter-spacing: .2px;
        }

        .playlist-panel {
            position: sticky;
            top: 12px;
            align-self: start;
        }

        .playlist-items {
            margin: 0;
            padding-left: 0;
            max-height: 58vh;
            overflow: auto;
            list-style: none;
        }

        .playlist-items li {
            margin-bottom: 8px;
        }

        .playlist-btn {
            width: 100%;
            text-align: left;
            border-radius: 10px;
            padding: 10px;
            border: 1px solid #2a323d;
            background: linear-gradient(180deg, #1a2430, #121a23);
            color: #dfe8f3;
            font-size: 13px;
            letter-spacing: .2px;
            display: grid;
            grid-template-columns: 36px 1fr;
            gap: 10px;
            align-items: center;
        }

        .playlist-idx {
            width: 36px;
            height: 36px;
            border-radius: 8px;
            display: grid;
            place-items: center;
            background: #223041;
            color: #9fb3c8;
            font-weight: 700;
            font-size: 12px;
        }

        .playlist-name {
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        .playlist-btn.current {
            border-color: #57c2ad;
            box-shadow: 0 0 0 2px #57c2ad33;
            color: #fff;
        }

        .playlist-btn.current .playlist-idx {
            background: linear-gradient(135deg, #49b9a3, #78e0c8);
            color: #032019;
        }

        .check-row {
            display: flex;
            gap: 8px;
            align-items: center;
            color: var(--muted);
            font-size: 13px;
        }

        .now-playing {
            margin-bottom: 10px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 10px;
        }

        .now-playing-title {
            font-size: 14px;
            font-weight: 600;
            color: #f3f7ff;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        .now-playing-meta {
            font-size: 12px;
            color: #9eb0c2;
        }

        video {
            width: 100%;
            max-height: 68vh;
            border-radius: 14px;
            background: #000;
            outline: 3px solid #ded6c9;
            outline-offset: 0;
        }

        .hint {
            margin-top: 12px;
            font-size: 14px;
            color: var(--muted);
        }

        .hint strong {
            color: var(--accent-2);
        }

        body.mode-play {
            background:
                radial-gradient(1200px 600px at 80% -10%, #2a385655 0%, transparent 64%),
                radial-gradient(1200px 600px at -10% 110%, #1f5b5755 0%, transparent 66%),
                linear-gradient(140deg, #0a0f16, #101722 40%, #0f1722 100%);
            color: #e8eef8;
        }

        body.mode-play .top {
            position: sticky;
            top: 0;
            z-index: 8;
            background: linear-gradient(90deg, #131b27f0, #151f2df0);
            border-bottom: 1px solid #273246;
            backdrop-filter: blur(10px);
            padding: 14px 22px;
        }

        body.mode-play .brand {
            font-family: "Avenir Next Condensed", "Avenir Next", "Segoe UI", sans-serif;
            font-size: clamp(22px, 2.5vw, 30px);
            color: #f3f8ff;
            letter-spacing: .5px;
        }

        body.mode-play .meta {
            color: #9caec2;
        }

        body.mode-play .meta a {
            color: #7fe2cb;
            text-decoration: none;
            margin-left: 6px;
        }

        body.mode-play .panel {
            background: linear-gradient(180deg, #121a24ef, #0e151eef);
            border: 1px solid #253245;
            color: #d6dfeb;
            box-shadow: 0 18px 32px rgba(0, 0, 0, .24);
            border-radius: 14px;
        }

        body.mode-play .label,
        body.mode-play .status,
        body.mode-play .hint {
            color: #9fb0c4;
        }

        body.mode-play input[type="text"],
        body.mode-play input[type="password"] {
            border: 1px solid #32445d;
            background: #0f1621;
            color: #edf3ff;
        }

        body.mode-play .progress-wrap {
            background: #253243;
        }

        body.mode-play .progress-bar {
            background: linear-gradient(90deg, #4dbca5, #7be0cc);
        }

        body.mode-play .play-layout {
            grid-template-columns: minmax(300px, 360px) minmax(0, 1fr) minmax(280px, 340px);
            gap: 20px;
        }

        body.mode-play .player-surface {
            border: 1px solid #2d3d54;
            box-shadow: 0 30px 60px rgba(0, 0, 0, .38);
            border-radius: 16px;
        }

        body.mode-play .video-title {
            color: #f3f8ff;
        }

        body.mode-play button {
            background: linear-gradient(135deg, #1f8f7d, #36b59e);
            color: #f7fffd;
            border: 1px solid #2db59f55;
        }

        body.mode-play button.alt {
            background: linear-gradient(135deg, #36455c, #2a3444);
            border-color: #4f637f55;
            color: #dfe8f5;
        }

        @keyframes rise {
            to {
                transform: translateY(0);
                opacity: 1;
            }
        }

        @media (max-width: 640px) {
            .top { padding: 14px 14px; }
            .body { padding: 12px; }
            video { max-height: 52vh; border-radius: 10px; }
            .hint { font-size: 13px; }
            .row-grid { grid-template-columns: 1fr; }
            .play-layout { grid-template-columns: 1fr; }
            .playlist-panel { position: static; }
            .playlist-items { max-height: 28vh; }
            body.page-app .body { padding: 12px; }
            .video-meta { align-items: flex-start; }
        }
    </style>
</head>
<body class="__BODY_CLASS__">
    <main class="shell">
        <section class="top">
            <div class="brand"><span class="dot"></span>MtVid Secure Player</div>
            <div class="meta">Protected in-memory playback | <a href="/encrypt">Sifreleme</a> | <a href="/play">Play</a></div>
        </section>
        <section class="body">
            <div id="encryptSection">
                <div class="panel">
                    <div class="row">
                        <label class="label" for="videoInput">Kaynak video dosyasi</label>
                        <input id="videoInput" type="file" accept="video/*" />
                    </div>
                    <div class="row">
                        <label class="label" for="mtafOutput">Cikti dosya adi (.mtaf)</label>
                        <input id="mtafOutput" type="text" placeholder="movie.mtaf" />
                    </div>
                    <div class="row-grid">
                        <div class="row">
                            <label class="label" for="packPassword">Sifre (sifreleme ve acma icin)</label>
                            <input id="packPassword" type="password" placeholder="Strong password" />
                        </div>
                        <div class="row">
                            <label class="label" for="chunkMb">Chunk MB</label>
                            <input id="chunkMb" type="text" value="2" />
                        </div>
                    </div>
                    <button id="packBtn" class="alt" type="button">Videoyu Sifrele (.mtaf)</button>
                    <div class="progress-wrap"><div id="packProgressBar" class="progress-bar"></div></div>
                    <div id="packStatus" class="status"></div>
                </div>

                <div class="panel">
                    <div class="row-grid">
                        <div class="row">
                            <label class="label" for="batchPassword">Toplu sifre</label>
                            <input id="batchPassword" type="password" placeholder="Strong password" />
                        </div>
                        <div class="row">
                            <label class="label" for="batchChunkMb">Chunk MB</label>
                            <input id="batchChunkMb" type="text" value="2" />
                        </div>
                    </div>
                    <label class="check-row" for="batchDeleteOriginal">
                        <input id="batchDeleteOriginal" type="checkbox" checked />
                        <span>Orijinal dosyayi sifreleme sonrasi sil</span>
                    </label>
                    <button id="batchPackBtn" class="alt" type="button">Klasor Sec ve Toplu Sifrele</button>
                    <div class="progress-wrap"><div id="batchProgressBar" class="progress-bar"></div></div>
                    <div id="batchStatus" class="status"></div>
                </div>
            </div>

            <div id="playSection">
                <div class="play-layout">
                    <div>
                        <div class="panel">
                            <div class="row">
                                <label class="label" for="pkgFile">.mtaf dosyasi</label>
                                <input id="pkgFile" type="file" accept=".mtaf,application/octet-stream" multiple />
                            </div>
                            <div class="row">
                                <label class="label" for="pkgPathQuick">Buyuk dosya icin hizli acilis (disk yolu)</label>
                                <input id="pkgPathQuick" type="text" placeholder="/Users/you/Videos/movie.mtaf" />
                            </div>
                            <button id="pickFastPathBtn" class="alt" type="button">Sistemden dosya sec (native)</button>
                            <div class="row">
                                <label class="label" for="pkgPassword">Sifre</label>
                                <input id="pkgPassword" type="password" placeholder="Playback sifresi" />
                            </div>
                            <div class="row-grid">
                                <button id="openBtn" type="button">Dosyayi Ac (Yukle)</button>
                                <button id="openFastBtn" type="button">Hizli Ac (Yol)</button>
                            </div>
                            <div class="row-grid">
                                <button id="queueBtn" class="alt" type="button">Listeye Ekle</button>
                                <button id="pickPlaylistNativeBtn" class="alt" type="button">Native'den Listeye Ekle</button>
                            </div>
                            <div class="progress-wrap"><div id="openProgressBar" class="progress-bar"></div></div>
                            <div id="status" class="status"></div>
                        </div>
                    </div>

                    <div>
                        <div class="play-main">
                            <div class="player-surface">
                                <video controls preload="metadata" src="/stream"></video>
                            </div>

                            <div class="panel video-meta">
                                <div id="nowPlayingTitle" class="video-title">Secili parca yok</div>
                                <div id="nowPlayingMeta" class="now-playing-meta">0 parca</div>
                            </div>

                            <p class="hint">
                                Bu ekran sadece calisan MtVid uygulamasi ile erisilebilir.
                                Stream kaynagi <strong>/stream</strong> uzerinden anlik cozulur.
                            </p>
                        </div>
                    </div>

                    <div class="panel playlist-panel">
                        <div class="label">Playlist</div>
                        <ol id="playlistList" class="playlist-items"></ol>
                        <div class="row-grid" style="margin-top:8px;">
                            <button id="prevBtn" type="button">Onceki</button>
                            <button id="nextBtn" type="button">Sonraki</button>
                        </div>
                        <button id="clearPlaylistBtn" type="button" style="margin-top:8px;">Listeyi Temizle</button>
                    </div>
                </div>
            </div>
        </section>
    </main>
    <script>
        const pageMode = '__MODE__';
        const video = document.querySelector('video');
        const videoInput = document.getElementById('videoInput');
        const mtafOutput = document.getElementById('mtafOutput');
        const packPassword = document.getElementById('packPassword');
        const chunkMb = document.getElementById('chunkMb');
        const packBtn = document.getElementById('packBtn');
        const packStatus = document.getElementById('packStatus');
        const packProgressBar = document.getElementById('packProgressBar');

        const batchPassword = document.getElementById('batchPassword');
        const batchChunkMb = document.getElementById('batchChunkMb');
        const batchDeleteOriginal = document.getElementById('batchDeleteOriginal');
        const batchPackBtn = document.getElementById('batchPackBtn');
        const batchStatus = document.getElementById('batchStatus');
        const batchProgressBar = document.getElementById('batchProgressBar');

        const pkgFile = document.getElementById('pkgFile');
        const pkgPathQuick = document.getElementById('pkgPathQuick');
        const pickFastPathBtn = document.getElementById('pickFastPathBtn');
        const passwordInput = document.getElementById('pkgPassword');
        const openBtn = document.getElementById('openBtn');
        const openFastBtn = document.getElementById('openFastBtn');
        const queueBtn = document.getElementById('queueBtn');
        const pickPlaylistNativeBtn = document.getElementById('pickPlaylistNativeBtn');
        const clearPlaylistBtn = document.getElementById('clearPlaylistBtn');
        const prevBtn = document.getElementById('prevBtn');
        const nextBtn = document.getElementById('nextBtn');
        const playlistList = document.getElementById('playlistList');
        const openProgressBar = document.getElementById('openProgressBar');
        const status = document.getElementById('status');
        const nowPlayingTitle = document.getElementById('nowPlayingTitle');
        const nowPlayingMeta = document.getElementById('nowPlayingMeta');

        const LARGE_OPEN_THRESHOLD_BYTES = 512 * 1024 * 1024;

        const encryptSection = document.getElementById('encryptSection');
        const playSection = document.getElementById('playSection');
        const playlist = [];
        let currentPlaylistIndex = -1;

        if (pageMode === 'encrypt') {
            if (playSection) playSection.style.display = 'none';
        } else if (pageMode === 'play') {
            if (encryptSection) encryptSection.style.display = 'none';
        }
        document.body.classList.add(`mode-${pageMode}`);

        function updateNowPlaying() {
            if (!nowPlayingTitle || !nowPlayingMeta) {
                return;
            }

            if (currentPlaylistIndex < 0 || currentPlaylistIndex >= playlist.length) {
                nowPlayingTitle.textContent = 'Secili parca yok';
                nowPlayingMeta.textContent = `${playlist.length} parca`;
                return;
            }

            nowPlayingTitle.textContent = playlist[currentPlaylistIndex].name;
            nowPlayingMeta.textContent = `${currentPlaylistIndex + 1}/${playlist.length}`;
        }

        function renderPlaylist() {
            if (!playlistList) {
                return;
            }

            playlistList.innerHTML = '';
            for (let i = 0; i < playlist.length; i++) {
                const item = playlist[i];
                const li = document.createElement('li');
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'playlist-btn' + (i === currentPlaylistIndex ? ' current' : '');

                const idx = document.createElement('span');
                idx.className = 'playlist-idx';
                idx.textContent = i === currentPlaylistIndex ? '▶' : String(i + 1);

                const name = document.createElement('span');
                name.className = 'playlist-name';
                name.textContent = item.name;

                btn.appendChild(idx);
                btn.appendChild(name);
                btn.onclick = async () => {
                    try {
                        await playPlaylistIndex(i);
                    } catch (err) {
                        status.textContent = err?.message || 'Playlist dosyasi acilamadi.';
                    }
                };
                li.appendChild(btn);
                playlistList.appendChild(li);
            }

            updateNowPlaying();
        }

        async function playPlaylistIndex(index) {
            if (index < 0 || index >= playlist.length) {
                return;
            }

            const password = passwordInput.value;
            if (!password) {
                throw new Error('Playlist oynatmak icin sifre gerekli.');
            }

            currentPlaylistIndex = index;
            renderPlaylist();
            const item = playlist[index];
            if (item.file) {
                await openByUpload(item.file, password);
                return;
            }

            if (item.path) {
                await openByPath(item.path, password, 'hizli');
                return;
            }

            throw new Error('Playlist ogesi gecersiz.');
        }

        function setPackProgress(value) {
            const v = Math.max(0, Math.min(100, Number(value) || 0));
            packProgressBar.style.width = `${v}%`;
        }

        function setBatchProgress(value) {
            const v = Math.max(0, Math.min(100, Number(value) || 0));
            batchProgressBar.style.width = `${v}%`;
        }

        function setOpenProgress(value) {
            const v = Math.max(0, Math.min(100, Number(value) || 0));
            openProgressBar.style.width = `${v}%`;
        }

        function abbreviateFileBaseName(baseName) {
            return baseName.replace(/[^.\s_]+/g, (token) => {
                const chars = [...token];
                if (chars.length <= 2) {
                    return token;
                }

                return `${chars[0]}${chars[chars.length - 1]}`;
            });
        }

        function toDefaultOutputName(fileName) {
            const dot = fileName.lastIndexOf('.');
            const baseName = dot > 0 ? fileName.slice(0, dot) : fileName;
            const abbreviated = abbreviateFileBaseName(baseName);
            return `${abbreviated}.mtaf`;
        }

        async function tryGetOriginalNameFromPackageFile(file) {
            try {
                const buffer = await file.slice(0, 8192).arrayBuffer();
                const view = new DataView(buffer);
                const bytes = new Uint8Array(buffer);
                let offset = 0;

                if (bytes.length < 4 || bytes[0] !== 0x4d || bytes[1] !== 0x54 || bytes[2] !== 0x41 || bytes[3] !== 0x46) {
                    return null;
                }

                offset += 4;
                if (offset >= view.byteLength) return null;
                const version = view.getUint8(offset);
                offset += 1;

                if (version !== 1 && version !== 2) {
                    return null;
                }

                offset += 4; // chunk size
                offset += 8; // original length
                offset += 4; // chunk count
                offset += 4; // iterations
                offset += 16; // salt
                offset += 4; // nonce prefix
                offset += 16; // verifier
                if (offset >= view.byteLength) return null;

                const ctLen = view.getUint8(offset);
                offset += 1 + ctLen;
                if (offset > view.byteLength) return null;

                if (version < 2) {
                    return null;
                }

                if (offset + 2 > view.byteLength) return null;
                const nameLen = view.getUint16(offset, true);
                offset += 2;
                if (nameLen === 0 || offset + nameLen > view.byteLength) return null;

                const nameBytes = bytes.slice(offset, offset + nameLen);
                const original = new TextDecoder('utf-8').decode(nameBytes).trim();
                return original || null;
            } catch {
                return null;
            }
        }

        async function tryGetOriginalNameByPath(path) {
            try {
                const res = await fetch('/api/package-info', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ packagePath: path })
                });

                const data = await res.json().catch(() => ({}));
                if (!res.ok || !data?.ok) {
                    return null;
                }

                const value = typeof data.originalFileName === 'string' ? data.originalFileName.trim() : '';
                return value || null;
            } catch {
                return null;
            }
        }

        async function startPackJobForFile(file, outputName, password, parsedChunk, onUploadProgress) {
            const result = await new Promise((resolve, reject) => {
                const xhr = new XMLHttpRequest();
                xhr.open('POST', '/api/pack-upload');
                xhr.setRequestHeader('X-Password', password);
                xhr.setRequestHeader('X-Chunk-Mb', String(parsedChunk));
                xhr.setRequestHeader('X-Output-Name', outputName);
                xhr.setRequestHeader('X-File-Name', file.name);
                xhr.setRequestHeader('Content-Type', 'application/octet-stream');

                xhr.upload.onprogress = (evt) => {
                    if (evt.lengthComputable && evt.total > 0 && onUploadProgress) {
                        onUploadProgress((evt.loaded / evt.total) * 100);
                    }
                };

                xhr.onerror = () => reject(new Error('Upload failed.'));
                xhr.onload = () => {
                    try {
                        const data = JSON.parse(xhr.responseText || '{}');
                        if (xhr.status < 200 || xhr.status >= 300 || !data.ok) {
                            reject(new Error(data.message || 'Sifreleme baslatilamadi.'));
                            return;
                        }

                        resolve(data);
                    } catch {
                        reject(new Error('Gecersiz sunucu cevabi.'));
                    }
                };

                xhr.send(file);
            });

            return result.jobId;
        }

        async function waitPackJob(jobId, onEncryptProgress) {
            let jobState = 'processing';
            while (jobState === 'processing') {
                await new Promise((r) => setTimeout(r, 350));
                const poll = await fetch(`/api/pack-jobs/${jobId}`);
                const pollData = await poll.json();
                if (!poll.ok || !pollData.ok) {
                    throw new Error(pollData.message || 'Progress alinamadi.');
                }

                jobState = pollData.state || 'failed';
                const encPercent = Number(pollData.progressPercent) || 0;
                if (onEncryptProgress) {
                    onEncryptProgress(encPercent);
                }

                if (jobState === 'failed') {
                    throw new Error(pollData.error || 'Sifreleme basarisiz.');
                }
            }
        }

        async function downloadJobToHandle(jobId, targetHandle) {
            const res = await fetch(`/api/pack-jobs/${jobId}/download`);
            if (!res.ok) {
                let message = 'Sifrelenen dosya indirilemedi.';
                try {
                    const err = await res.json();
                    message = err.message || message;
                } catch {}
                throw new Error(message);
            }

            const writable = await targetHandle.createWritable();
            if (res.body && writable) {
                await res.body.pipeTo(writable);
                return;
            }

            const blob = await res.blob();
            await writable.write(blob);
            await writable.close();
        }

        async function refreshStatus() {
            try {
                const res = await fetch('/api/status');
                const data = await res.json();
                if (data.loaded) {
                    status.textContent = `Aktif: ${data.fileName}`;
                } else {
                    status.textContent = 'Henüz dosya açılmadı.';
                }
            } catch {
                status.textContent = 'Durum bilgisi alınamadı.';
            }
        }

        async function openByPath(packagePath, password, modeLabel) {
            setOpenProgress(0);
            status.textContent = `Aciliyor (${modeLabel})...`;
            const res = await fetch('/api/open', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ packagePath, password })
            });

            const data = await res.json();
            if (!res.ok || !data.ok) {
                throw new Error(data.message || 'Dosya acilamadi.');
            }

            setOpenProgress(100);
            status.textContent = `Acildi (${modeLabel}): ${data.fileName}`;
            video.pause();
            video.src = '/stream?ts=' + Date.now();
            await video.play().catch(() => {});
        }

        async function openByUpload(file, password) {
            setOpenProgress(0);
            status.textContent = 'Yukleniyor...';

            const data = await new Promise((resolve, reject) => {
                const xhr = new XMLHttpRequest();
                xhr.open('POST', '/api/open-upload');
                xhr.setRequestHeader('X-Password', password);
                xhr.setRequestHeader('X-File-Name', file.name);
                xhr.setRequestHeader('Content-Type', 'application/octet-stream');

                xhr.upload.onprogress = (evt) => {
                    if (evt.lengthComputable && evt.total > 0) {
                        const p = Math.floor((evt.loaded / evt.total) * 100);
                        setOpenProgress(p);
                        status.textContent = `Yukleniyor... ${p}%`;
                    }
                };

                xhr.onerror = () => reject(new Error('Sunucuya baglanilamadi.'));
                xhr.onload = () => {
                    try {
                        const parsed = JSON.parse(xhr.responseText || '{}');
                        if (xhr.status < 200 || xhr.status >= 300 || !parsed.ok) {
                            reject(new Error(parsed.message || 'Dosya acilamadi.'));
                            return;
                        }

                        resolve(parsed);
                    } catch {
                        reject(new Error('Gecersiz sunucu cevabi.'));
                    }
                };

                xhr.send(file);
            });

            setOpenProgress(100);
            status.textContent = `Acildi (yukleme): ${data.fileName}`;
            video.pause();
            video.src = '/stream?ts=' + Date.now();
            await video.play().catch(() => {});
        }

        openBtn.addEventListener('click', async () => {
            const file = pkgFile.files && pkgFile.files[0];
            const password = passwordInput.value;
            if (!file || !password) {
                status.textContent = 'Dosya secimi ve sifre gerekli.';
                return;
            }

            try {
                if (file.size >= LARGE_OPEN_THRESHOLD_BYTES && pkgPathQuick.value.trim()) {
                    await openByPath(pkgPathQuick.value.trim(), password, 'hizli');
                    return;
                }

                await openByUpload(file, password);
            } catch (err) {
                setOpenProgress(0);
                status.textContent = err?.message || 'Sunucuya baglanilamadi.';
            }
        });

        queueBtn.addEventListener('click', async () => {
            const files = pkgFile.files ? Array.from(pkgFile.files) : [];
            if (files.length === 0) {
                status.textContent = 'Playlist icin en az bir .mtaf dosyasi sec.';
                return;
            }

            let added = 0;
            for (const file of files) {
                if (!file.name.toLowerCase().endsWith('.mtaf')) {
                    continue;
                }

                const originalName = await tryGetOriginalNameFromPackageFile(file);
                playlist.push({ file, name: originalName || file.name });
                added++;
            }

            if (added === 0) {
                status.textContent = 'Secilen dosyalar icinde .mtaf bulunamadi.';
                return;
            }

            if (currentPlaylistIndex < 0 && playlist.length > 0) {
                currentPlaylistIndex = 0;
            }

            renderPlaylist();
            status.textContent = `Playlist guncellendi. Toplam ${playlist.length} dosya.`;
        });

        pickPlaylistNativeBtn.addEventListener('click', async () => {
            status.textContent = 'Native coklu secici aciliyor...';
            try {
                const res = await fetch('/api/pick-file?kind=mtaf&multi=true');
                const data = await res.json();
                if (!res.ok || !data.ok || !Array.isArray(data.paths)) {
                    status.textContent = data.message || 'Dosyalar secilemedi.';
                    return;
                }

                let added = 0;
                for (const p of data.paths) {
                    if (typeof p !== 'string' || !p.toLowerCase().endsWith('.mtaf')) {
                        continue;
                    }

                    const fallbackName = p.split('/').pop() || p;
                    const originalName = await tryGetOriginalNameByPath(p);
                    const name = originalName || fallbackName;
                    playlist.push({ path: p, name });
                    added++;
                }

                if (added === 0) {
                    status.textContent = 'Secimden listeye eklenecek .mtaf bulunamadi.';
                    return;
                }

                if (currentPlaylistIndex < 0 && playlist.length > 0) {
                    currentPlaylistIndex = 0;
                }

                renderPlaylist();
                status.textContent = `Native secimden ${added} dosya eklendi. Toplam ${playlist.length}.`;
            } catch (err) {
                status.textContent = err?.message || 'Native secici acilamadi.';
            }
        });

        clearPlaylistBtn.addEventListener('click', () => {
            playlist.length = 0;
            currentPlaylistIndex = -1;
            renderPlaylist();
            status.textContent = 'Playlist temizlendi.';
            updateNowPlaying();
        });

        prevBtn.addEventListener('click', async () => {
            if (playlist.length === 0) {
                status.textContent = 'Playlist bos.';
                return;
            }

            const target = currentPlaylistIndex <= 0 ? 0 : currentPlaylistIndex - 1;
            try {
                await playPlaylistIndex(target);
            } catch (err) {
                status.textContent = err?.message || 'Onceki parca acilamadi.';
            }
        });

        nextBtn.addEventListener('click', async () => {
            if (playlist.length === 0) {
                status.textContent = 'Playlist bos.';
                return;
            }

            const target = currentPlaylistIndex < 0 ? 0 : Math.min(currentPlaylistIndex + 1, playlist.length - 1);
            try {
                await playPlaylistIndex(target);
            } catch (err) {
                status.textContent = err?.message || 'Sonraki parca acilamadi.';
            }
        });

        openFastBtn.addEventListener('click', async () => {
            const packagePath = pkgPathQuick.value.trim();
            const password = passwordInput.value;
            if (!packagePath || !password) {
                status.textContent = 'Disk yolu ve sifre gerekli.';
                return;
            }

            try {
                await openByPath(packagePath, password, 'hizli');
            } catch (err) {
                setOpenProgress(0);
                status.textContent = err?.message || 'Sunucuya baglanilamadi.';
            }
        });

        pickFastPathBtn.addEventListener('click', async () => {
            status.textContent = 'Sistem secici aciliyor...';
            try {
                const res = await fetch('/api/pick-file?kind=mtaf');
                const data = await res.json();
                if (!res.ok || !data.ok || !data.path) {
                    status.textContent = data.message || 'Dosya secilemedi.';
                    return;
                }

                pkgPathQuick.value = data.path;
                status.textContent = 'Dosya yolu otomatik dolduruldu.';
            } catch {
                status.textContent = 'Native secici acilamadi.';
            }
        });

        pkgFile.addEventListener('change', () => {
            const file = pkgFile.files && pkgFile.files[0];
            if (!file) {
                return;
            }

            const sizeGb = (file.size / (1024 * 1024 * 1024)).toFixed(2);
            if (file.size >= LARGE_OPEN_THRESHOLD_BYTES) {
                status.textContent = `Secilen dosya buyuk (${sizeGb} GB). Hizli acilis icin disk yolunu da girersen otomatik hizli moda gecer.`;
            } else {
                status.textContent = `Secilen dosya: ${file.name} (${sizeGb} GB)`;
            }
        });

        video.addEventListener('ended', async () => {
            if (currentPlaylistIndex < 0) {
                return;
            }

            const nextIndex = currentPlaylistIndex + 1;
            if (nextIndex >= playlist.length) {
                status.textContent = 'Playlist bitti.';
                return;
            }

            try {
                await playPlaylistIndex(nextIndex);
            } catch (err) {
                status.textContent = err?.message || 'Otomatik sonraki dosya acilamadi.';
            }
        });

        videoInput.addEventListener('change', () => {
            const file = videoInput.files && videoInput.files[0];
            if (!file) {
                return;
            }

            mtafOutput.value = toDefaultOutputName(file.name);
        });

        packBtn.addEventListener('click', async () => {
            const file = videoInput.files && videoInput.files[0];
            const outputPath = mtafOutput.value.trim() || (file ? toDefaultOutputName(file.name) : '');
            const password = packPassword.value;
            const parsedChunk = Number.parseInt(chunkMb.value, 10);

            if (!file || !outputPath || !password) {
                packStatus.textContent = 'Video secimi, cikti adi ve sifre gerekli.';
                return;
            }

            mtafOutput.value = outputPath;

            if (!Number.isInteger(parsedChunk) || parsedChunk <= 0 || parsedChunk > 32) {
                packStatus.textContent = 'Chunk MB 1-32 araliginda olmali.';
                return;
            }

            setPackProgress(0);
            packStatus.textContent = 'Yukleniyor...';
            try {
                const jobId = await startPackJobForFile(file, outputPath, password, parsedChunk, (uploadPercent) => {
                    const p = Math.floor(uploadPercent * 0.35);
                    setPackProgress(p);
                    packStatus.textContent = `Yukleniyor... ${Math.floor(uploadPercent)}%`;
                });
                if (!jobId) {
                    throw new Error('Job id alinamadi.');
                }

                packStatus.textContent = 'Sifreleniyor...';
                await waitPackJob(jobId, (encPercent) => {
                    const totalPercent = 35 + Math.floor(encPercent * 0.65);
                    setPackProgress(totalPercent);
                    packStatus.textContent = `Sifreleniyor... ${encPercent}%`;
                });

                const safeName = outputPath.toLowerCase().endsWith('.mtaf') ? outputPath : `${outputPath}.mtaf`;
                const link = document.createElement('a');
                link.href = `/api/pack-jobs/${jobId}/download`;
                link.download = safeName;
                link.click();

                setPackProgress(100);
                packStatus.textContent = `Olustu ve indirildi: ${safeName}`;
                passwordInput.value = password;
            } catch (err) {
                setPackProgress(0);
                packStatus.textContent = err?.message || 'Sunucuya baglanilamadi.';
            }
        });

        batchPackBtn.addEventListener('click', async () => {
            if (!window.showDirectoryPicker) {
                batchStatus.textContent = 'Tarayici folder secimini desteklemiyor.';
                return;
            }

            const password = batchPassword.value;
            const parsedChunk = Number.parseInt(batchChunkMb.value, 10);
            if (!password) {
                batchStatus.textContent = 'Toplu sifre gerekli.';
                return;
            }

            if (!Number.isInteger(parsedChunk) || parsedChunk <= 0 || parsedChunk > 32) {
                batchStatus.textContent = 'Chunk MB 1-32 araliginda olmali.';
                return;
            }

            setBatchProgress(0);
            batchStatus.textContent = 'Klasor secimi bekleniyor...';

            try {
                const dirHandle = await window.showDirectoryPicker({ mode: 'readwrite' });
                const files = [];
                for await (const [name, handle] of dirHandle.entries()) {
                    if (handle.kind !== 'file') {
                        continue;
                    }

                    if (name.toLowerCase().endsWith('.mtaf')) {
                        continue;
                    }

                    files.push({ name, handle });
                }

                files.sort((a, b) => a.name.localeCompare(b.name, 'tr'));
                if (files.length === 0) {
                    batchStatus.textContent = 'Sifrelenecek dosya bulunamadi (mtaf disi).';
                    setBatchProgress(0);
                    return;
                }

                let deletedCount = 0;
                for (let i = 0; i < files.length; i++) {
                    const current = files[i];
                    const file = await current.handle.getFile();
                    const outName = toDefaultOutputName(current.name);

                    batchStatus.textContent = `${i + 1}/${files.length} isleniyor: ${current.name} (yukleniyor)`;
                    const jobId = await startPackJobForFile(file, outName, password, parsedChunk, (uploadPercent) => {
                        const within = Math.floor(uploadPercent * 0.35);
                        const total = ((i + (within / 100)) / files.length) * 100;
                        setBatchProgress(total);
                    });

                    await waitPackJob(jobId, (encPercent) => {
                        const within = 35 + Math.floor(encPercent * 0.65);
                        const total = ((i + (within / 100)) / files.length) * 100;
                        setBatchProgress(total);
                        batchStatus.textContent = `${i + 1}/${files.length} sifreleniyor: ${current.name} (${encPercent}%)`;
                    });

                    const outHandle = await dirHandle.getFileHandle(outName, { create: true });
                    await downloadJobToHandle(jobId, outHandle);

                    if (batchDeleteOriginal.checked) {
                        try {
                            await dirHandle.removeEntry(current.name);
                            deletedCount++;
                        } catch {
                        }
                    }
                }

                setBatchProgress(100);
                const deleteMsg = batchDeleteOriginal.checked
                    ? `, silinen orijinal: ${deletedCount}`
                    : '';
                batchStatus.textContent = `Tamamlandi. Toplam: ${files.length}${deleteMsg}`;
                passwordInput.value = password;
            } catch (err) {
                setBatchProgress(0);
                const raw = err?.message || '';
                if (err?.name === 'AbortError' || raw.includes('aborted a request')) {
                    batchStatus.textContent = 'Klasor secimi iptal edildi veya tarayici klasor erisimini engelledi. Tekrar dene; olmazsa Chrome/Edge gibi dis tarayicida ac.';
                    return;
                }

                if (err?.name === 'NotAllowedError') {
                    batchStatus.textContent = 'Klasore yazma izni verilmedi. Klasoru tekrar secip izin ver.';
                    return;
                }

                batchStatus.textContent = raw || 'Toplu sifreleme iptal edildi veya basarisiz.';
            }
        });

        refreshStatus();
    </script>
</body>
</html>
""";

                html = html.Replace("__MODE__", mode);
                html = html.Replace("__BODY_CLASS__", "page-app");

                byte[] payload = System.Text.Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = payload.Length;
                response.OutputStream.Write(payload, 0, payload.Length);
                response.OutputStream.Close();
        }

            private static void WriteJson(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
            {
                byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                response.StatusCode = (int)statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = json.Length;
                response.OutputStream.Write(json, 0, json.Length);
                response.OutputStream.Close();
            }

            private static LoadedPackageSession CreateSession(string packagePath, string password, bool deleteOnDispose = false, string? preferredDisplayName = null)
            {
                if (!File.Exists(packagePath))
                {
                    throw new FileNotFoundException("Package file not found.", packagePath);
                }

                using FileStream file = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                PackageHeader header = PackageHeader.ReadFrom(file);

                if (!CryptoHelpers.IsPasswordValid(password, header, out byte[] encryptionKey))
                {
                    throw new UnauthorizedAccessException("Wrong password. Package could not be unlocked.");
                }

                string displayFileName = ResolveDisplayFileName(header, packagePath, preferredDisplayName);
                return new LoadedPackageSession(packagePath, displayFileName, header, encryptionKey, deleteOnDispose);
            }

            private static string ResolveDisplayFileName(PackageHeader header, string packagePath, string? preferredDisplayName)
            {
                if (!string.IsNullOrWhiteSpace(header.OriginalFileName))
                {
                    return header.OriginalFileName.Trim();
                }

                if (!string.IsNullOrWhiteSpace(preferredDisplayName))
                {
                    return Path.GetFileName(preferredDisplayName.Trim());
                }

                return Path.GetFileName(packagePath);
            }

            private static void TryDelete(string? path)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }

            private static string SanitizeFileName(string name)
            {
                string input = string.IsNullOrWhiteSpace(name) ? "video.mtaf" : name;
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    input = input.Replace(c, '_');
                }

                if (!input.EndsWith(".mtaf", StringComparison.OrdinalIgnoreCase))
                {
                    input += ".mtaf";
                }

                return input;
            }

            private static string BuildAbbreviatedMtafName(string sourceFileName)
            {
                string baseName = Path.GetFileNameWithoutExtension(sourceFileName);
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = "video";
                }

                string abbreviated = Regex.Replace(baseName, @"[^.\s_]+", static match =>
                {
                    string token = match.Value;
                    if (token.Length <= 2)
                    {
                        return token;
                    }

                    return string.Concat(token[0], token[^1]);
                });

                return SanitizeFileName($"{abbreviated}.mtaf");
            }

            private static string GuessContentType(string inputPath)
            {
                string extension = Path.GetExtension(inputPath).ToLowerInvariant();
                return extension switch
                {
                    ".mp4" => "video/mp4",
                    ".webm" => "video/webm",
                    ".mkv" => "video/x-matroska",
                    ".mov" => "video/quicktime",
                    _ => "application/octet-stream"
                };
            }

            private sealed class LoadedPackageSession : IDisposable
            {
                public LoadedPackageSession(string packagePath, string displayFileName, PackageHeader header, byte[] encryptionKey, bool deleteOnDispose)
                {
                    PackagePath = packagePath;
                    DisplayFileName = displayFileName;
                    Header = header;
                    EncryptionKey = encryptionKey;
                    DeleteOnDispose = deleteOnDispose;
                }

                public string PackagePath { get; }
                public string DisplayFileName { get; }
                public PackageHeader Header { get; }
                public byte[] EncryptionKey { get; }
                public bool DeleteOnDispose { get; }

                public void Dispose()
                {
                    CryptographicOperations.ZeroMemory(EncryptionKey);
                    if (DeleteOnDispose)
                    {
                        TryDelete(PackagePath);
                    }
                }
            }

            private sealed class OpenRequest
            {
                public string? PackagePath { get; set; }
                public string? Password { get; set; }
            }

            private sealed class PackRequest
            {
                public string? InputPath { get; set; }
                public string? OutputPath { get; set; }
                public string? Password { get; set; }
                public int? ChunkMb { get; set; }
                public int? Iterations { get; set; }
            }

            private sealed class PackageInfoRequest
            {
                public string? PackagePath { get; set; }
            }

            private sealed class PackJobState
            {
                public required string JobId { get; init; }
                public required string InputPath { get; init; }
                public required string OutputPath { get; init; }
                public required string OutputFileName { get; init; }
                public required string SourceContentType { get; init; }
                public volatile int ProgressPercent;
                public volatile string State = "processing";
                public string? ErrorMessage;
            }

    private static bool TryParseRange(string? rangeHeader, long totalLength, out long start, out long end)
    {
        start = 0;
        end = 0;

        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return false;
        }

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string raw = rangeHeader[6..].Trim();
        int dashIndex = raw.IndexOf('-');
        if (dashIndex < 0)
        {
            return false;
        }

        string left = raw[..dashIndex].Trim();
        string right = raw[(dashIndex + 1)..].Trim();

        if (left.Length == 0)
        {
            if (!long.TryParse(right, out long suffixLength) || suffixLength <= 0)
            {
                return false;
            }

            if (totalLength == 0)
            {
                start = 0;
                end = 0;
                return true;
            }

            suffixLength = Math.Min(suffixLength, totalLength);
            start = totalLength - suffixLength;
            end = totalLength - 1;
            return true;
        }

        if (!long.TryParse(left, out start) || start < 0 || start >= totalLength)
        {
            return false;
        }

        if (right.Length == 0)
        {
            end = totalLength - 1;
            return true;
        }

        if (!long.TryParse(right, out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, totalLength - 1);
        return true;
    }

    private static void CopyExactly(Stream source, Stream destination, long bytesToCopy)
    {
        byte[] buffer = new byte[128 * 1024];
        long remaining = bytesToCopy;

        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, requested);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end while streaming decrypted data.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
