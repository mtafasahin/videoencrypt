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
    private readonly ConcurrentDictionary<string, string> _uploadedThumbnails = new(StringComparer.Ordinal);
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

        foreach ((_, string thumbPath) in _uploadedThumbnails)
        {
            TryDelete(thumbPath);
        }

        _packJobs.Clear();
        _uploadedThumbnails.Clear();
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

            if (_enableUi && path.Equals("/api/migrate-package", StringComparison.OrdinalIgnoreCase))
            {
                ProcessMigratePackageRequest(context);
                return;
            }

            if (_enableUi && path.Equals("/api/thumbnail-upload", StringComparison.OrdinalIgnoreCase))
            {
                ProcessThumbnailUploadRequest(context);
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
            VideoPackager.EncryptVideo(
                payload.InputPath,
                payload.OutputPath,
                payload.Password,
                chunkMb * 1024 * 1024,
                contentType,
                iterations,
                originalFileName: Path.GetFileName(payload.InputPath));

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
            string outputName = context.Request.Headers["X-Output-Name"] ?? BuildGuidMtafName();
            string thumbnailId = context.Request.Headers["X-Thumbnail-Id"] ?? string.Empty;
            double? durationSeconds = null;
            string? durationHeader = context.Request.Headers["X-Duration-Seconds"];
            if (!string.IsNullOrWhiteSpace(durationHeader)
                && double.TryParse(durationHeader, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedDuration)
                && double.IsFinite(parsedDuration)
                && parsedDuration >= 0)
            {
                durationSeconds = parsedDuration;
            }
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
            byte[]? thumbnailJpeg = TryTakeUploadedThumbnail(thumbnailId);
            string jobId = Guid.NewGuid().ToString("N");
            PackJobState job = new()
            {
                JobId = jobId,
                InputPath = tempInput,
                OutputPath = tempOutput,
                OutputFileName = SanitizeFileName(outputName),
                SourceContentType = contentType,
                SourceFileName = Path.GetFileName(sourceName),
                SourceThumbnailJpeg = thumbnailJpeg,
                SourceDurationSeconds = durationSeconds,
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
                },
                job.SourceFileName,
                job.SourceThumbnailJpeg,
                job.SourceDurationSeconds);

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
            long fileSizeBytes = fs.Length;

            WriteJson(context.Response, HttpStatusCode.OK, new
            {
                ok = true,
                fileName = Path.GetFileName(payload.PackagePath),
                originalFileName = header.OriginalFileName,
                hasOriginalFileName = !string.IsNullOrWhiteSpace(header.OriginalFileName),
                thumbnailJpegBase64 = header.ThumbnailJpeg is { Length: > 0 } ? Convert.ToBase64String(header.ThumbnailJpeg) : null,
                hasThumbnail = header.ThumbnailJpeg is { Length: > 0 },
                durationSeconds = header.DurationSeconds,
                hasDuration = header.DurationSeconds.HasValue,
                version = header.Version,
                currentVersion = PackageHeader.CurrentVersion,
                isCurrentVersion = header.Version == PackageHeader.CurrentVersion,
                contentType = header.ContentType,
                headerSize = header.HeaderSize,
                chunkSize = header.ChunkSize,
                originalLength = header.OriginalLength,
                chunkCount = header.ChunkCount,
                kdfIterations = header.KdfIterations,
                saltBytes = header.Salt?.Length ?? 0,
                noncePrefixBytes = header.NoncePrefix?.Length ?? 0,
                passwordVerifierBytes = header.PasswordVerifier?.Length ?? 0,
                thumbnailBytes = header.ThumbnailJpeg?.Length ?? 0,
                fileSizeBytes
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

    private void ProcessMigratePackageRequest(HttpListenerContext context)
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
            MigratePackageRequest? payload = JsonSerializer.Deserialize<MigratePackageRequest>(body, JsonBodyOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.PackagePath))
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "packagePath is required." });
                return;
            }

            bool inPlace = payload.InPlace.GetValueOrDefault(true);
            byte[]? thumbnailJpeg = ParseThumbnailDataUrl(payload.ThumbnailDataUrl);
            string migratedPath = MigratePackageToCurrentVersion(
                payload.PackagePath,
                payload.OutputPath,
                inPlace,
                payload.OriginalFileName,
                thumbnailJpeg,
                payload.DurationSeconds,
                out byte previousVersion,
                out bool migrated);

            WriteJson(context.Response, HttpStatusCode.OK, new
            {
                ok = true,
                migrated,
                packagePath = migratedPath,
                previousVersion,
                currentVersion = PackageHeader.CurrentVersion
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

    private static string MigratePackageToCurrentVersion(
        string packagePath,
        string? outputPath,
        bool inPlace,
        string? originalFileName,
        byte[]? thumbnailJpeg,
        double? durationSeconds,
        out byte previousVersion,
        out bool migrated)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Package file not found.", packagePath);
        }

        using FileStream input = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        PackageHeader header = PackageHeader.ReadFrom(input);
        previousVersion = header.Version;

        string? resolvedOriginal = !string.IsNullOrWhiteSpace(header.OriginalFileName)
            ? header.OriginalFileName
            : (string.IsNullOrWhiteSpace(originalFileName) ? null : Path.GetFileName(originalFileName));

        byte[]? resolvedThumbnail = header.ThumbnailJpeg is { Length: > 0 }
            ? header.ThumbnailJpeg
            : (thumbnailJpeg is { Length: > 0 } ? thumbnailJpeg : null);

        double? resolvedDuration = header.DurationSeconds.HasValue && header.DurationSeconds.Value > 0
            ? header.DurationSeconds
            : (durationSeconds.HasValue && durationSeconds.Value > 0 ? durationSeconds : null);

        bool metadataEnriched =
            !string.IsNullOrWhiteSpace(resolvedOriginal) && string.IsNullOrWhiteSpace(header.OriginalFileName)
            || resolvedThumbnail is { Length: > 0 } && header.ThumbnailJpeg is not { Length: > 0 }
            || resolvedDuration.HasValue && (!header.DurationSeconds.HasValue || header.DurationSeconds.Value <= 0);

        migrated = header.Version != PackageHeader.CurrentVersion || metadataEnriched;

        string targetPath = inPlace
            ? packagePath
            : string.IsNullOrWhiteSpace(outputPath)
                ? packagePath
                : outputPath;

        if (!migrated)
        {
            if (!inPlace && !string.Equals(targetPath, packagePath, StringComparison.OrdinalIgnoreCase))
            {
                string? targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory) && !Directory.Exists(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Copy(packagePath, targetPath, overwrite: true);
            }

            return targetPath;
        }

        string tempOutput = Path.Combine(Path.GetDirectoryName(targetPath) ?? Path.GetTempPath(), $"mtvid-migrate-{Guid.NewGuid():N}.mtaf");
        try
        {
            using FileStream output = new(tempOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            PackageHeader migratedHeader = new()
            {
                Version = PackageHeader.CurrentVersion,
                ChunkSize = header.ChunkSize,
                OriginalLength = header.OriginalLength,
                ChunkCount = header.ChunkCount,
                KdfIterations = header.KdfIterations,
                Salt = header.Salt,
                NoncePrefix = header.NoncePrefix,
                PasswordVerifier = header.PasswordVerifier,
                ContentType = header.ContentType,
                OriginalFileName = resolvedOriginal,
                ThumbnailJpeg = resolvedThumbnail,
                DurationSeconds = resolvedDuration
            };
            migratedHeader.WriteTo(output);

            input.Position = header.HeaderSize;
            input.CopyTo(output);
        }
        catch
        {
            TryDelete(tempOutput);
            throw;
        }

        if (!inPlace && !string.Equals(targetPath, packagePath, StringComparison.OrdinalIgnoreCase))
        {
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Move(tempOutput, targetPath, overwrite: true);
            return targetPath;
        }

        File.Move(tempOutput, packagePath, overwrite: true);
        return packagePath;
    }

    private static byte[]? ParseThumbnailDataUrl(string? thumbnailDataUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailDataUrl))
        {
            return null;
        }

        string raw = thumbnailDataUrl.Trim();
        int commaIndex = raw.IndexOf(',');
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex > 0)
        {
            raw = raw[(commaIndex + 1)..];
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(raw);
            if (bytes.Length == 0 || bytes.Length > PackageHeader.MaxThumbnailBytes)
            {
                return null;
            }

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private void ProcessThumbnailUploadRequest(HttpListenerContext context)
    {
        string? tempThumb = null;
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            if (context.Request.ContentLength64 <= 0 || context.Request.ContentLength64 > PackageHeader.MaxThumbnailBytes)
            {
                WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = "Thumbnail size is invalid." });
                return;
            }

            string thumbId = Guid.NewGuid().ToString("N");
            tempThumb = Path.Combine(Path.GetTempPath(), $"mtvid-thumb-{thumbId}.jpg");
            using (FileStream outFile = new(tempThumb, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                context.Request.InputStream.CopyTo(outFile);
            }

            _uploadedThumbnails[thumbId] = tempThumb;
            tempThumb = null;

            WriteJson(context.Response, HttpStatusCode.OK, new { ok = true, thumbnailId = thumbId });
        }
        catch (Exception ex)
        {
            WriteJson(context.Response, HttpStatusCode.BadRequest, new { ok = false, message = ex.Message });
        }
        finally
        {
            TryDelete(tempThumb);
        }
    }

    private byte[]? TryTakeUploadedThumbnail(string thumbnailId)
    {
        if (string.IsNullOrWhiteSpace(thumbnailId))
        {
            return null;
        }

        if (!_uploadedThumbnails.TryRemove(thumbnailId, out string? thumbPath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(thumbPath))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(thumbPath);
            return bytes.Length == 0 || bytes.Length > PackageHeader.MaxThumbnailBytes ? null : bytes;
        }
        finally
        {
            TryDelete(thumbPath);
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

        .top-controls {
            display: flex;
            gap: 8px;
            align-items: center;
            flex-wrap: nowrap;
            white-space: nowrap;
        }

        .top-password-input {
            border: 1px solid #cfc5b3;
            border-radius: 8px;
            padding: 8px 12px;
            font-size: 13px;
            background: #fff;
            color: var(--ink);
            width: 160px;
            min-width: 140px;
            flex-shrink: 0;
        }

        .top-open-btn {
            padding: 8px 12px;
            font-size: 13px;
            border-radius: 8px;
            border: 0;
            background: #2f7f72;
            color: #fff;
            cursor: pointer;
            white-space: nowrap;
            flex-shrink: 0;
        }

        .top-open-btn:hover {
            background: #409886;
        }

        body.mode-play .top-password-input {
            background: #1a1a1a;
            color: #f1f1f1;
            border-color: #3f3f3f;
        }

        body.mode-play .top-open-btn {
            background: #1f1f1f;
            border: 1px solid #4a4a4a;
            color: #e6e6e6;
        }

        body.mode-play .top-open-btn:hover {
            background: #2a2a2a;
        }

        .advanced-section {
            max-height: 0;
            overflow: hidden;
            transition: max-height 300ms ease;
        }

        .advanced-section.open {
            max-height: 1200px;
            transition: max-height 300ms ease;
        }

        .advanced-toggle {
            width: fit-content;
            border: 1px solid #ccc;
            border-radius: 8px;
            padding: 8px 12px;
            font-size: 13px;
            background: #f5f5f5;
            color: var(--ink);
            cursor: pointer;
            margin-bottom: 8px;
            transition: background-color 200ms ease;
        }

        .advanced-toggle:hover {
            background: #e8e8e8;
        }

        body.mode-play .advanced-toggle {
            background: #2a2a2a;
            border-color: #404040;
            color: #e6e6e6;
        }

        body.mode-play .advanced-toggle:hover {
            background: #333333;
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
            grid-template-columns: minmax(280px, 340px) minmax(0, 1fr) minmax(300px, 390px);
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

        .player-controls {
            display: flex;
            align-items: center;
            gap: 8px;
            padding: 10px 12px;
            background: linear-gradient(180deg, #161f2b, #121a24);
            border-top: 1px solid #2a3647;
            overflow: visible;
            flex-wrap: nowrap;
            scrollbar-width: thin;
            opacity: 0;
            transform: translateY(10px);
            pointer-events: none;
            transition: opacity .2s ease, transform .2s ease;
        }

        .player-surface.controls-visible .player-controls,
        .player-surface:hover .player-controls,
        .player-surface:focus-within .player-controls {
            opacity: 1;
            transform: translateY(0);
            pointer-events: auto;
        }

        .player-controls .control-btn {
            border: 1px solid #3a495f;
            border-radius: 8px;
            background: #1c2837;
            color: #e7eef8;
            padding: 7px 11px;
            font-size: 12px;
            line-height: 1.1;
            white-space: nowrap;
            flex: 0 0 auto;
        }

        .player-controls .control-btn:hover {
            background: #243348;
        }

        .control-volume {
            width: 96px;
            flex: 0 0 auto;
            accent-color: #78bcae;
        }

        .control-speed-wrap {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            color: #c9d6e7;
            font-size: 12px;
            white-space: nowrap;
            flex: 0 0 auto;
        }

        .control-speed {
            border: 1px solid #3a495f;
            border-radius: 8px;
            background: #1c2837;
            color: #e7eef8;
            padding: 5px 8px;
            font-size: 12px;
        }

        .control-seek-wrap {
            position: relative;
            flex: 1 1 280px;
            min-width: 180px;
            display: flex;
            align-items: center;
            margin: 0 4px;
        }

        .control-seek {
            width: 100%;
            accent-color: #78bcae;
            cursor: pointer;
        }

        .seek-preview {
            position: absolute;
            left: 0;
            bottom: calc(100% + 10px);
            transform: translateX(-50%);
            background: #0f1722f4;
            border: 1px solid #38506e;
            border-radius: 8px;
            padding: 5px;
            display: grid;
            gap: 4px;
            width: 164px;
            pointer-events: none;
            box-shadow: 0 12px 28px rgba(0, 0, 0, 0.38);
            z-index: 5;
        }

        .seek-preview[hidden] {
            display: none;
        }

        .seek-preview img {
            width: 100%;
            aspect-ratio: 16 / 9;
            object-fit: cover;
            border-radius: 6px;
            background: #0b0f16;
        }

        .seek-preview-time {
            text-align: center;
            color: #d7e4f7;
            font-size: 11px;
            font-variant-numeric: tabular-nums;
            letter-spacing: .2px;
        }

        .control-time {
            margin-left: auto;
            color: #c7d5e8;
            font-size: 12px;
            font-variant-numeric: tabular-nums;
            white-space: nowrap;
            flex: 0 0 auto;
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
            min-width: 320px;
        }

        .playlist-items {
            margin: 0;
            padding-left: 0;
            max-height: 58vh;
            overflow: auto;
            list-style: none;
        }

        .playlist-items li {
            margin-bottom: 4px;
        }

        .playlist-row {
            display: grid;
            grid-template-columns: minmax(0, 1fr) auto;
            gap: 6px;
            align-items: stretch;
        }

        .playlist-btn {
            width: 100%;
            text-align: left;
            border-radius: 8px;
            padding: 6px 8px;
            border: 1px solid transparent;
            background: transparent;
            color: #dfe8f3;
            font-family: "Roboto", "Avenir Next", "Segoe UI", sans-serif;
            font-size: 12px;
            display: grid;
            grid-template-columns: 24px 100px minmax(0, 1fr);
            gap: 8px;
            align-items: center;
        }

        .playlist-btn:hover {
            background: #ffffff0d;
        }

        .playlist-info-btn {
            width: 24px;
            height: 24px;
            min-width: 24px;
            border-radius: 999px;
            padding: 0;
            font-size: 16px;
            font-weight: 700;
            line-height: 1;
            display: grid;
            place-items: center;
            background: transparent;
            border: 1px solid transparent;
            color: #a6a6a6;
            font-family: "Roboto", "Avenir Next", "Segoe UI", sans-serif;
            opacity: 0;
            pointer-events: none;
            transition: opacity .16s ease, background-color .16s ease, color .16s ease;
        }

        .playlist-row:hover .playlist-info-btn,
        .playlist-row:focus-within .playlist-info-btn,
        .playlist-row.current-row .playlist-info-btn {
            opacity: 1;
            pointer-events: auto;
        }

        .playlist-info-btn:hover,
        .playlist-info-btn:focus-visible {
            background: #ffffff1f;
            border-color: #ffffff24;
            color: #f0f0f0;
            outline: none;
        }

        .playlist-idx {
            width: 24px;
            height: 24px;
            border-radius: 4px;
            display: grid;
            place-items: center;
            background: #1d2938;
            color: #93a8bf;
            font-weight: 500;
            font-size: 12px;
        }

        .playlist-name {
            display: -webkit-box;
            -webkit-line-clamp: 2;
            -webkit-box-orient: vertical;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: normal;
            line-height: 1.32;
            font-size: 14px;
            font-weight: 500;
        }

        .playlist-thumb {
            position: relative;
            width: 100px;
            aspect-ratio: 16 / 9;
            border-radius: 8px;
            background: linear-gradient(135deg, #2a2a2a, #1b1b1b);
            border: 1px solid #3a3a3a;
            overflow: hidden;
            display: grid;
            place-items: center;
            color: #b4b4b4;
            font-size: 10px;
            letter-spacing: .3px;
        }

        .playlist-thumb img {
            width: 100%;
            height: 100%;
            object-fit: cover;
            display: block;
        }

        .playlist-btn.current {
            border-color: transparent;
            background: #ffffff14;
            box-shadow: none;
            color: #fff;
        }

        .playlist-btn.current:hover {
            background: #ffffff1a;
        }

        .playlist-btn.current .playlist-idx {
            background: #ffffff1f;
            color: #f4f4f4;
        }

        .playlist-btn.current .playlist-meta {
            color: #c8c8c8;
        }

        .playlist-row.current-row .playlist-info-btn {
            border-color: transparent;
            background: transparent;
            color: #f1f1f1;
        }

        @media (any-pointer: coarse) {
            .playlist-info-btn {
                opacity: 1;
                pointer-events: auto;
            }
        }

        .check-row {
            display: flex;
            gap: 8px;
            align-items: center;
            white-space: nowrap;
            font-size: 13px;
        }

        .check-row span {
            font-size: 13px;
            font-weight: 700;
            color: var(--ink);
        }

        .playlist-duration {
            position: absolute;
            right: 4px;
            bottom: 4px;
            padding: 3px 4px;
            border-radius: 4px;
            background: rgba(0, 0, 0, 0.82);
            color: #fff;
            font-size: 12px;
            font-weight: 500;
            line-height: 1.1;
            letter-spacing: .3px;
            z-index: 2;
        }

        .playlist-text {
            min-width: 0;
            display: grid;
            gap: 4px;
        }

        .playlist-meta {
            color: #9eb0c5;
            font-size: 12px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            line-height: 1.2;
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

        .info-modal {
            position: fixed;
            inset: 0;
            background: rgba(3, 9, 17, 0.72);
            display: none;
            align-items: center;
            justify-content: center;
            padding: 18px;
            z-index: 50;
        }

        .info-modal.open {
            display: flex;
        }

        .info-card {
            width: min(820px, 100%);
            max-height: min(84vh, 900px);
            overflow: hidden;
            background: linear-gradient(180deg, #101822, #0c141d);
            border: 1px solid #2a394f;
            border-radius: 14px;
            box-shadow: 0 28px 80px rgba(0, 0, 0, 0.45);
            display: grid;
            grid-template-rows: auto minmax(0, 1fr) auto;
        }

        .info-head {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 10px;
            padding: 12px 14px;
            border-bottom: 1px solid #253449;
            color: #eef5ff;
            font-size: 14px;
            font-weight: 700;
        }

        .info-body {
            margin: 0;
            padding: 12px 14px;
            overflow: auto;
            color: #d4e2f5;
            font-size: 12px;
            line-height: 1.45;
            white-space: pre-wrap;
            word-break: break-word;
            font-family: ui-monospace, Menlo, Consolas, "Liberation Mono", monospace;
        }

        .info-foot {
            display: flex;
            justify-content: flex-end;
            padding: 10px 14px 14px;
            border-top: 1px solid #253449;
        }

        body.mode-play {
            background:
                radial-gradient(1200px 600px at 80% -10%, #2b2b2b55 0%, transparent 64%),
                radial-gradient(1200px 600px at -10% 110%, #1c1c1c66 0%, transparent 66%),
                linear-gradient(140deg, #0f0f0f, #171717 42%, #111111 100%);
            color: #f1f1f1;
        }

        body.mode-play .top {
            position: sticky;
            top: 0;
            z-index: 8;
            background: linear-gradient(90deg, #181818f0, #202020f0);
            border-bottom: 1px solid #2f2f2f;
            backdrop-filter: blur(10px);
            padding: 14px 22px;
        }

        body.mode-play .brand {
            font-family: "Avenir Next Condensed", "Avenir Next", "Segoe UI", sans-serif;
            font-size: clamp(22px, 2.5vw, 30px);
            color: #f1f1f1;
            letter-spacing: .5px;
        }

        body.mode-play .meta {
            color: #b0b0b0;
        }

        body.mode-play .meta a {
            color: #d0d0d0;
            text-decoration: none;
            margin-left: 6px;
        }

        body.mode-play .panel {
            background: linear-gradient(180deg, #1f1f1fef, #181818ef);
            border: 1px solid #303030;
            color: #e4e4e4;
            box-shadow: 0 18px 32px rgba(0, 0, 0, .24);
            border-radius: 14px;
        }

        body.mode-play .label,
        body.mode-play .status,
        body.mode-play .hint {
            color: #b3b3b3;
        }

        body.mode-play input[type="text"],
        body.mode-play input[type="password"] {
            border: 1px solid #3f3f3f;
            background: #121212;
            color: #f1f1f1;
        }

        body.mode-play .progress-wrap {
            background: #2a2a2a;
        }

        body.mode-play .progress-bar {
            background: linear-gradient(90deg, #707070, #a8a8a8);
        }

        body.mode-play .play-layout {
            grid-template-columns: minmax(300px, 360px) minmax(0, 1fr) minmax(330px, 420px);
            gap: 20px;
        }

        body.mode-play .player-surface {
            border: 1px solid #363636;
            box-shadow: 0 30px 60px rgba(0, 0, 0, .38);
            border-radius: 16px;
        }

        body.mode-play .player-controls {
            background: linear-gradient(180deg, #171717, #121212);
            border-top: 1px solid #2f2f2f;
        }

        body.mode-play .player-controls .control-btn,
        body.mode-play .control-speed {
            background: #232323;
            border-color: #4a4a4a;
            color: #efefef;
        }

        body.mode-play .player-controls .control-btn:hover {
            background: #2b2b2b;
        }

        body.mode-play .control-speed-wrap,
        body.mode-play .control-time {
            color: #cdcdcd;
        }

        body.mode-play .seek-preview {
            background: #151515f4;
            border-color: #4a4a4a;
        }

        body.mode-play .video-title {
            color: #f1f1f1;
        }

        body.mode-play button {
            background: linear-gradient(135deg, #2f2f2f, #3d3d3d);
            color: #f1f1f1;
            border: 1px solid #575757;
        }

        body.mode-play button.alt {
            background: linear-gradient(135deg, #262626, #343434);
            border-color: #4a4a4a;
            color: #e6e6e6;
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
            .player-controls { gap: 6px; padding: 8px 10px; }
            .control-volume { width: 78px; }
            .control-time { font-size: 11px; }
            .control-seek-wrap { min-width: 140px; }
            .seek-preview { width: 132px; }
        }

        @media (any-pointer: coarse) {
            .player-controls {
                opacity: 1;
                transform: translateY(0);
                pointer-events: auto;
            }
        }
    </style>
</head>
<body class="__BODY_CLASS__">
    <main class="shell">
        <section class="top">
            <div class="brand"><span class="dot"></span>MtVid Secure Player</div>
            <div id="topControls" class="top-controls" style="display: none;">
                <input id="topPassword" type="password" class="top-password-input" placeholder="Sifre" />
                <button id="topOpenBtn" type="button" class="top-open-btn">Aç</button>
            </div>
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
                    <div class="row">
                        <label class="label" for="packThumbSecond">Thumbnail saniyesi (varsayilan 10)</label>
                        <input id="packThumbSecond" type="text" value="10" />
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
                    <div class="row">
                        <label class="label" for="batchThumbSecond">Thumbnail saniyesi (varsayilan 10)</label>
                        <input id="batchThumbSecond" type="text" value="10" />
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
                            <button id="advancedToggle" class="advanced-toggle">⊕ Advanced</button>
                            <div id="advancedSection" class="advanced-section">
                                <div class="row">
                                    <label class="label" for="pkgFile">.mtaf dosyasi</label>
                                    <input id="pkgFile" type="file" accept=".mtaf,application/octet-stream" multiple />
                                </div>
                                <div class="row">
                                    <label class="label" for="pkgPathQuick">Buyuk dosya icin hizli acilis (disk yolu)</label>
                                    <input id="pkgPathQuick" type="text" placeholder="/Users/you/Videos/movie.mtaf" />
                                </div>
                                <button id="pickFastPathBtn" class="alt" type="button">Sistemden dosya sec (native)</button>
                                <div class="row-grid">
                                    <button id="openBtn" type="button">Dosyayi Ac (Yukle)</button>
                                    <button id="openFastBtn" type="button">Hizli Ac (Yol)</button>
                                </div>
                                <div class="row-grid">
                                    <button id="queueBtn" class="alt" type="button">Listeye Ekle</button>
                                </div>
                            </div>
                            <div class="progress-wrap"><div id="openProgressBar" class="progress-bar"></div></div>
                            <div id="status" class="status"></div>
                        </div>
                    </div>

                    <div>
                        <div class="play-main">
                            <div id="playerSurface" class="player-surface">
                                <video preload="metadata" src="/stream"></video>
                                <div id="playerControls" class="player-controls" role="group" aria-label="Video controls">
                                    <button id="playPauseBtn" class="control-btn" type="button">Play</button>
                                    <button id="seekBackBtn" class="control-btn" type="button">-10s</button>
                                    <button id="seekForwardBtn" class="control-btn" type="button">+10s</button>
                                    <div id="seekWrap" class="control-seek-wrap">
                                        <input id="seekSlider" class="control-seek" type="range" min="0" max="1000" value="0" step="1" aria-label="Video zaman cubugu" />
                                        <div id="seekPreview" class="seek-preview" hidden>
                                            <img id="seekPreviewImage" alt="Seek preview" />
                                            <div id="seekPreviewTime" class="seek-preview-time">00:00</div>
                                        </div>
                                    </div>
                                    <button id="muteBtn" class="control-btn" type="button">Ses</button>
                                    <input id="volumeSlider" class="control-volume" type="range" min="0" max="100" value="100" step="1" aria-label="Ses seviyesi" />
                                    <label class="control-speed-wrap" for="speedSelect">Hiz
                                        <select id="speedSelect" class="control-speed">
                                            <option value="0.5">0.5x</option>
                                            <option value="0.75">0.75x</option>
                                            <option value="1" selected>1x</option>
                                            <option value="1.25">1.25x</option>
                                            <option value="1.5">1.5x</option>
                                            <option value="2">2x</option>
                                        </select>
                                    </label>
                                    <span id="controlTime" class="control-time">00:00 / 00:00</span>
                                    <button id="pipBtn" class="control-btn" type="button">PiP</button>
                                    <button id="fullscreenBtn" class="control-btn" type="button">Tam Ekran</button>
                                </div>
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
                        <button id="migratePlaylistBtn" class="alt" type="button" style="margin-top:8px;">Eski Surumleri Guncelle</button>
                    </div>
                </div>
            </div>
        </section>
    </main>

    <div id="infoModal" class="info-modal" aria-hidden="true">
        <div class="info-card" role="dialog" aria-modal="true" aria-labelledby="infoTitle">
            <div class="info-head">
                <span id="infoTitle">Paket Bilgisi</span>
                <button id="infoCloseBtn" type="button" class="alt">Kapat</button>
            </div>
            <pre id="infoBody" class="info-body"></pre>
            <div class="info-foot">
                <button id="infoCloseBtn2" type="button">Tamam</button>
            </div>
        </div>
    </div>

    <script>
        const pageMode = '__MODE__';
        const CURRENT_HEADER_VERSION = __CURRENT_HEADER_VERSION__;
        const video = document.querySelector('video');
        const videoInput = document.getElementById('videoInput');
        const mtafOutput = document.getElementById('mtafOutput');
        const packPassword = document.getElementById('packPassword');
        const chunkMb = document.getElementById('chunkMb');
        const packThumbSecond = document.getElementById('packThumbSecond');
        const packBtn = document.getElementById('packBtn');
        const packStatus = document.getElementById('packStatus');
        const packProgressBar = document.getElementById('packProgressBar');

        const batchPassword = document.getElementById('batchPassword');
        const batchChunkMb = document.getElementById('batchChunkMb');
        const batchThumbSecond = document.getElementById('batchThumbSecond');
        const batchDeleteOriginal = document.getElementById('batchDeleteOriginal');
        const batchPackBtn = document.getElementById('batchPackBtn');
        const batchStatus = document.getElementById('batchStatus');
        const batchProgressBar = document.getElementById('batchProgressBar');

        const pkgFile = document.getElementById('pkgFile');
        const pkgPathQuick = document.getElementById('pkgPathQuick');
        const pickFastPathBtn = document.getElementById('pickFastPathBtn');
        const openBtn = document.getElementById('openBtn');
        const openFastBtn = document.getElementById('openFastBtn');
        const queueBtn = document.getElementById('queueBtn');
        const clearPlaylistBtn = document.getElementById('clearPlaylistBtn');
        const topPassword = document.getElementById('topPassword');
        const topOpenBtn = document.getElementById('topOpenBtn');
        const topControls = document.getElementById('topControls');
        const advancedToggle = document.getElementById('advancedToggle');
        const advancedSection = document.getElementById('advancedSection');
        const migratePlaylistBtn = document.getElementById('migratePlaylistBtn');
        const prevBtn = document.getElementById('prevBtn');
        const nextBtn = document.getElementById('nextBtn');
        const playlistList = document.getElementById('playlistList');
        const openProgressBar = document.getElementById('openProgressBar');
        const status = document.getElementById('status');
        const nowPlayingTitle = document.getElementById('nowPlayingTitle');
        const nowPlayingMeta = document.getElementById('nowPlayingMeta');
        const infoModal = document.getElementById('infoModal');
        const infoTitle = document.getElementById('infoTitle');
        const infoBody = document.getElementById('infoBody');
        const infoCloseBtn = document.getElementById('infoCloseBtn');
        const infoCloseBtn2 = document.getElementById('infoCloseBtn2');
        const playerControls = document.getElementById('playerControls');
        const playPauseBtn = document.getElementById('playPauseBtn');
        const seekBackBtn = document.getElementById('seekBackBtn');
        const seekForwardBtn = document.getElementById('seekForwardBtn');
        const seekWrap = document.getElementById('seekWrap');
        const seekSlider = document.getElementById('seekSlider');
        const seekPreview = document.getElementById('seekPreview');
        const seekPreviewImage = document.getElementById('seekPreviewImage');
        const seekPreviewTime = document.getElementById('seekPreviewTime');
        const muteBtn = document.getElementById('muteBtn');
        const volumeSlider = document.getElementById('volumeSlider');
        const speedSelect = document.getElementById('speedSelect');
        const pipBtn = document.getElementById('pipBtn');
        const fullscreenBtn = document.getElementById('fullscreenBtn');
        const controlTime = document.getElementById('controlTime');
        const playerSurface = document.getElementById('playerSurface');

        const LARGE_OPEN_THRESHOLD_BYTES = 512 * 1024 * 1024;

        const encryptSection = document.getElementById('encryptSection');
        const playSection = document.getElementById('playSection');
        const playlist = [];
        let currentPlaylistIndex = -1;
        let isSeeking = false;
        const previewVideo = document.createElement('video');
        previewVideo.muted = true;
        previewVideo.playsInline = true;
        previewVideo.preload = 'metadata';
        let previewSource = '';
        let previewTimer = null;
        let previewBusy = false;
        let pendingPreviewTime = null;
        const previewCanvas = document.createElement('canvas');
        previewCanvas.width = 160;
        previewCanvas.height = 90;
        const CONTROL_AUTOHIDE_MS = 1400;
        let controlsHideTimer = null;

        if (pageMode === 'encrypt') {
            if (playSection) playSection.style.display = 'none';
        } else if (pageMode === 'play') {
            if (encryptSection) encryptSection.style.display = 'none';
            if (topControls) topControls.style.display = 'flex';
        }
        document.body.classList.add(`mode-${pageMode}`);

        // Advanced section toggle
        advancedToggle?.addEventListener('click', () => {
            if (advancedSection) {
                advancedSection.classList.toggle('open');
                const isOpen = advancedSection.classList.contains('open');
                advancedToggle.textContent = isOpen ? '⊖ Advanced' : '⊕ Advanced';
            }
        });

        function formatControlTime(seconds) {
            if (!Number.isFinite(seconds) || seconds < 0) {
                return '00:00';
            }

            const total = Math.floor(seconds);
            const h = Math.floor(total / 3600);
            const m = Math.floor((total % 3600) / 60);
            const s = total % 60;
            if (h > 0) {
                return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
            }

            return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
        }

        function updatePlayerControlState() {
            if (!video) {
                return;
            }

            if (playPauseBtn) {
                playPauseBtn.textContent = video.paused ? 'Play' : 'Pause';
            }

            if (muteBtn) {
                muteBtn.textContent = video.muted || video.volume <= 0 ? 'Sessiz' : 'Ses';
            }

            if (volumeSlider) {
                const currentVol = video.muted ? 0 : Math.round((video.volume || 0) * 100);
                volumeSlider.value = String(currentVol);
            }

            if (speedSelect) {
                speedSelect.value = String(video.playbackRate || 1);
            }

            if (seekSlider && Number.isFinite(video.duration) && video.duration > 0) {
                seekSlider.max = String(video.duration);
                seekSlider.step = '0.05';
                if (!isSeeking) {
                    seekSlider.value = String(Math.min(video.duration, Math.max(0, video.currentTime || 0)));
                }
            }

            if (controlTime) {
                const current = formatControlTime(video.currentTime || 0);
                const total = formatControlTime(video.duration || 0);
                controlTime.textContent = `${current} / ${total}`;
            }

            if (pipBtn) {
                const pipSupported = !!document.pictureInPictureEnabled && !video.disablePictureInPicture;
                pipBtn.disabled = !pipSupported;
                pipBtn.textContent = document.pictureInPictureElement ? 'PiP Cik' : 'PiP';
            }
        }

        function getSeekSecondsFromSlider() {
            if (!seekSlider || !Number.isFinite(video.duration) || video.duration <= 0) {
                return 0;
            }

            const value = Number(seekSlider.value);
            if (!Number.isFinite(value)) {
                return 0;
            }

            return Math.min(video.duration, Math.max(0, value));
        }

        function syncPreviewSource() {
            const source = video.currentSrc || video.src || '';
            if (!source || source === previewSource) {
                return;
            }

            previewSource = source;
            previewVideo.src = source;
            previewVideo.load();
        }

        async function seekPreviewVideoTo(targetSeconds) {
            if (!Number.isFinite(targetSeconds) || targetSeconds < 0) {
                return false;
            }

            syncPreviewSource();
            if (!previewSource) {
                return false;
            }

            if (previewVideo.readyState < 1) {
                await new Promise((resolve, reject) => {
                    const onLoaded = () => {
                        cleanup();
                        resolve();
                    };
                    const onErr = () => {
                        cleanup();
                        reject(new Error('Preview metadata okunamadi.'));
                    };
                    const cleanup = () => {
                        previewVideo.removeEventListener('loadedmetadata', onLoaded);
                        previewVideo.removeEventListener('error', onErr);
                    };

                    previewVideo.addEventListener('loadedmetadata', onLoaded, { once: true });
                    previewVideo.addEventListener('error', onErr, { once: true });
                });
            }

            const safeTarget = Number.isFinite(previewVideo.duration) && previewVideo.duration > 0
                ? Math.min(Math.max(0, targetSeconds), Math.max(0, previewVideo.duration - 0.05))
                : Math.max(0, targetSeconds);

            if (Math.abs((previewVideo.currentTime || 0) - safeTarget) > 0.03) {
                await new Promise((resolve, reject) => {
                    const onSeeked = () => {
                        cleanup();
                        resolve();
                    };
                    const onErr = () => {
                        cleanup();
                        reject(new Error('Preview seek hatasi.'));
                    };
                    const cleanup = () => {
                        previewVideo.removeEventListener('seeked', onSeeked);
                        previewVideo.removeEventListener('error', onErr);
                    };

                    previewVideo.addEventListener('seeked', onSeeked, { once: true });
                    previewVideo.addEventListener('error', onErr, { once: true });
                    previewVideo.currentTime = safeTarget;
                });
            }

            return previewVideo.readyState >= 2;
        }

        async function renderSeekPreview(targetSeconds) {
            if (!seekPreview || !seekPreviewImage || !seekPreviewTime) {
                return;
            }

            if (previewBusy) {
                pendingPreviewTime = targetSeconds;
                return;
            }

            previewBusy = true;
            try {
                const ready = await seekPreviewVideoTo(targetSeconds);
                if (ready && previewVideo.videoWidth > 0 && previewVideo.videoHeight > 0) {
                    const ctx = previewCanvas.getContext('2d');
                    if (ctx) {
                        ctx.clearRect(0, 0, previewCanvas.width, previewCanvas.height);
                        ctx.drawImage(previewVideo, 0, 0, previewCanvas.width, previewCanvas.height);
                        seekPreviewImage.src = previewCanvas.toDataURL('image/jpeg', 0.76);
                    }
                }
                seekPreviewTime.textContent = formatControlTime(targetSeconds);
            } catch {
                seekPreviewTime.textContent = formatControlTime(targetSeconds);
            } finally {
                previewBusy = false;
            }

            if (pendingPreviewTime !== null) {
                const nextTarget = pendingPreviewTime;
                pendingPreviewTime = null;
                void renderSeekPreview(nextTarget);
            }
        }

        function scheduleSeekPreview(targetSeconds) {
            if (previewTimer) {
                clearTimeout(previewTimer);
                previewTimer = null;
            }

            previewTimer = setTimeout(() => {
                previewTimer = null;
                void renderSeekPreview(targetSeconds);
            }, 70);
        }

        function updateSeekPreviewPosition(clientX) {
            if (!seekWrap || !seekPreview) {
                return;
            }

            const rect = seekWrap.getBoundingClientRect();
            const localX = Math.max(0, Math.min(rect.width, clientX - rect.left));
            seekPreview.style.left = `${localX}px`;
        }

        function setControlsVisible(visible) {
            if (!playerSurface) {
                return;
            }

            playerSurface.classList.toggle('controls-visible', !!visible);
        }

        function clearControlsHideTimer() {
            if (!controlsHideTimer) {
                return;
            }

            clearTimeout(controlsHideTimer);
            controlsHideTimer = null;
        }

        function scheduleControlsAutoHide() {
            if (!playerSurface || window.matchMedia('(any-pointer: coarse)').matches) {
                return;
            }

            clearControlsHideTimer();
            controlsHideTimer = setTimeout(() => {
                controlsHideTimer = null;
                if (!playerSurface.matches(':hover') && !playerSurface.matches(':focus-within') && !isSeeking) {
                    setControlsVisible(false);
                }
            }, CONTROL_AUTOHIDE_MS);
        }

        function bumpControlsVisibility() {
            setControlsVisible(true);
            scheduleControlsAutoHide();
        }

        function setupPlayerControls() {
            if (!video || !playerControls || !playerSurface) {
                return;
            }

            video.controls = false;
            bumpControlsVisibility();
            video.addEventListener('play', updatePlayerControlState);
            video.addEventListener('pause', updatePlayerControlState);
            video.addEventListener('timeupdate', updatePlayerControlState);
            video.addEventListener('loadedmetadata', updatePlayerControlState);
            video.addEventListener('durationchange', updatePlayerControlState);
            video.addEventListener('volumechange', updatePlayerControlState);
            video.addEventListener('ratechange', updatePlayerControlState);

            playerSurface.addEventListener('mouseenter', bumpControlsVisibility);
            playerSurface.addEventListener('mousemove', bumpControlsVisibility);
            playerSurface.addEventListener('pointermove', bumpControlsVisibility);
            playerSurface.addEventListener('mouseleave', () => {
                if (!isSeeking) {
                    setControlsVisible(false);
                }
            });
            playerSurface.addEventListener('focusin', bumpControlsVisibility);
            playerSurface.addEventListener('focusout', () => {
                scheduleControlsAutoHide();
            });

            playPauseBtn?.addEventListener('click', async () => {
                bumpControlsVisibility();
                if (video.paused) {
                    await video.play().catch(() => {});
                } else {
                    video.pause();
                }
                updatePlayerControlState();
            });

            seekBackBtn?.addEventListener('click', () => {
                bumpControlsVisibility();
                const next = Math.max(0, (video.currentTime || 0) - 10);
                video.currentTime = next;
                updatePlayerControlState();
            });

            seekForwardBtn?.addEventListener('click', () => {
                bumpControlsVisibility();
                const max = Number.isFinite(video.duration) ? video.duration : (video.currentTime || 0) + 10;
                const next = Math.min(max, (video.currentTime || 0) + 10);
                video.currentTime = next;
                updatePlayerControlState();
            });

            seekSlider?.addEventListener('input', () => {
                bumpControlsVisibility();
                isSeeking = true;
                const target = getSeekSecondsFromSlider();
                video.currentTime = target;
                if (controlTime) {
                    const total = formatControlTime(video.duration || 0);
                    controlTime.textContent = `${formatControlTime(target)} / ${total}`;
                }
            });

            seekSlider?.addEventListener('change', () => {
                bumpControlsVisibility();
                const target = getSeekSecondsFromSlider();
                video.currentTime = target;
                isSeeking = false;
                updatePlayerControlState();
            });

            seekSlider?.addEventListener('pointerdown', () => {
                bumpControlsVisibility();
                isSeeking = true;
            });

            const stopSeeking = () => {
                const target = getSeekSecondsFromSlider();
                video.currentTime = target;
                isSeeking = false;
                updatePlayerControlState();
                scheduleControlsAutoHide();
            };
            seekSlider?.addEventListener('pointerup', stopSeeking);
            seekSlider?.addEventListener('keyup', (evt) => {
                if (evt.key === 'ArrowLeft' || evt.key === 'ArrowRight' || evt.key === 'Home' || evt.key === 'End') {
                    const target = getSeekSecondsFromSlider();
                    video.currentTime = target;
                }
                stopSeeking();
            });

            const handleSeekPreviewPointer = (clientX) => {
                if (!seekSlider || !seekPreview || !Number.isFinite(video.duration) || video.duration <= 0) {
                    return;
                }

                bumpControlsVisibility();

                const rect = seekSlider.getBoundingClientRect();
                if (rect.width <= 0) {
                    return;
                }

                const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
                const target = ratio * video.duration;
                seekPreview.hidden = false;
                updateSeekPreviewPosition(clientX);
                scheduleSeekPreview(target);
            };

            seekSlider?.addEventListener('mousemove', (evt) => {
                handleSeekPreviewPointer(evt.clientX);
            });
            seekSlider?.addEventListener('pointermove', (evt) => {
                handleSeekPreviewPointer(evt.clientX);
            });

            seekWrap?.addEventListener('mouseenter', (evt) => {
                handleSeekPreviewPointer(evt.clientX);
            });
            seekWrap?.addEventListener('pointermove', (evt) => {
                handleSeekPreviewPointer(evt.clientX);
            });
            seekWrap?.addEventListener('mouseleave', () => {
                if (seekPreview) {
                    seekPreview.hidden = true;
                }
                scheduleControlsAutoHide();
            });

            seekSlider?.addEventListener('mouseleave', () => {
                if (seekPreview) {
                    seekPreview.hidden = true;
                }
            });

            seekSlider?.addEventListener('touchstart', () => {
                if (seekPreview) {
                    seekPreview.hidden = true;
                }
            }, { passive: true });

            muteBtn?.addEventListener('click', () => {
                bumpControlsVisibility();
                video.muted = !video.muted;
                updatePlayerControlState();
            });

            volumeSlider?.addEventListener('input', () => {
                bumpControlsVisibility();
                const value = Number(volumeSlider.value);
                if (!Number.isFinite(value)) {
                    return;
                }

                video.muted = value <= 0;
                video.volume = Math.min(1, Math.max(0, value / 100));
                updatePlayerControlState();
            });

            speedSelect?.addEventListener('change', () => {
                bumpControlsVisibility();
                const value = Number(speedSelect.value);
                if (!Number.isFinite(value) || value <= 0) {
                    return;
                }

                video.playbackRate = value;
                updatePlayerControlState();
            });

            pipBtn?.addEventListener('click', async () => {
                bumpControlsVisibility();
                if (!document.pictureInPictureEnabled || video.disablePictureInPicture) {
                    return;
                }

                try {
                    if (document.pictureInPictureElement) {
                        await document.exitPictureInPicture();
                    } else {
                        await video.requestPictureInPicture();
                    }
                } catch {
                }

                updatePlayerControlState();
            });

            document.addEventListener('enterpictureinpicture', updatePlayerControlState);
            document.addEventListener('leavepictureinpicture', updatePlayerControlState);

            fullscreenBtn?.addEventListener('click', async () => {
                bumpControlsVisibility();
                const host = video.parentElement;
                if (!host) {
                    return;
                }

                try {
                    if (document.fullscreenElement) {
                        await document.exitFullscreen();
                        return;
                    }

                    if (host.requestFullscreen) {
                        await host.requestFullscreen();
                    } else if (host.webkitRequestFullscreen) {
                        host.webkitRequestFullscreen();
                    }
                } catch {
                }
            });

            video.addEventListener('emptied', () => {
                previewSource = '';
                if (seekPreviewImage) {
                    seekPreviewImage.removeAttribute('src');
                }
            });

            updatePlayerControlState();
        }

        if (pageMode === 'play') {
            setupPlayerControls();
        }

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

        function formatDurationLabel(value) {
            if (!Number.isFinite(value) || value <= 0) {
                return '--:--';
            }

            const total = Math.floor(value);
            const h = Math.floor(total / 3600);
            const m = Math.floor((total % 3600) / 60);
            const s = total % 60;
            if (h > 0) {
                return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
            }

            return `${m}:${String(s).padStart(2, '0')}`;
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

                const thumb = document.createElement('span');
                thumb.className = 'playlist-thumb';
                if (item.thumbData) {
                    const img = document.createElement('img');
                    img.src = item.thumbData;
                    img.alt = item.name;
                    thumb.appendChild(img);
                } else {
                    thumb.textContent = 'FRAME';
                }

                const duration = document.createElement('span');
                duration.className = 'playlist-duration';
                duration.textContent = item.durationLabel || '--:--';
                thumb.appendChild(duration);

                const textWrap = document.createElement('span');
                textWrap.className = 'playlist-text';

                const name = document.createElement('span');
                name.className = 'playlist-name';
                name.textContent = item.name;

                const meta = document.createElement('span');
                meta.className = 'playlist-meta';
                const stateText = i === currentPlaylistIndex ? 'Simdi oynatiliyor' : `Parca ${i + 1}`;
                const versionText = Number.isFinite(item.version) ? `v${item.version}` : 'v?';
                meta.textContent = `${stateText} • ${versionText}`;
                textWrap.appendChild(name);
                textWrap.appendChild(meta);

                btn.appendChild(idx);
                btn.appendChild(thumb);
                btn.appendChild(textWrap);
                btn.onclick = async () => {
                    try {
                        await playPlaylistIndex(i);
                    } catch (err) {
                        status.textContent = err?.message || 'Playlist dosyasi acilamadi.';
                    }
                };

                const row = document.createElement('div');
                row.className = 'playlist-row' + (i === currentPlaylistIndex ? ' current-row' : '');
                row.appendChild(btn);

                const infoBtn = document.createElement('button');
                infoBtn.type = 'button';
                infoBtn.className = 'playlist-info-btn';
                infoBtn.textContent = '⋮';
                infoBtn.title = 'Header bilgisi';
                infoBtn.setAttribute('aria-label', 'Header bilgisi');
                infoBtn.onclick = async (evt) => {
                    evt.preventDefault();
                    evt.stopPropagation();
                    try {
                        const details = await loadPlaylistItemDebugInfo(item, i);
                        renderPlaylist();
                        openInfoModal(`Header Bilgisi • ${details.displayName || item.name}`, details);
                    } catch (err) {
                        status.textContent = err?.message || 'Paket bilgisi alinamadi.';
                    }
                };
                row.appendChild(infoBtn);

                li.appendChild(row);
                playlistList.appendChild(li);
            }

            updateNowPlaying();
        }

        function formatSize(value) {
            const bytes = Number(value);
            if (!Number.isFinite(bytes) || bytes < 0) {
                return null;
            }

            const units = ['B', 'KB', 'MB', 'GB', 'TB'];
            let size = bytes;
            let idx = 0;
            while (size >= 1024 && idx < units.length - 1) {
                size /= 1024;
                idx += 1;
            }

            const digits = idx === 0 ? 0 : 2;
            return `${size.toFixed(digits)} ${units[idx]}`;
        }

        function closeInfoModal() {
            if (!infoModal) {
                return;
            }

            infoModal.classList.remove('open');
            infoModal.setAttribute('aria-hidden', 'true');
        }

        function openInfoModal(title, payload) {
            if (!infoModal || !infoTitle || !infoBody) {
                return;
            }

            infoTitle.textContent = title || 'Paket Bilgisi';
            infoBody.textContent = JSON.stringify(payload, null, 2);
            infoModal.classList.add('open');
            infoModal.setAttribute('aria-hidden', 'false');
        }

        async function loadPlaylistItemDebugInfo(item, index) {
            if (item.path) {
                const res = await fetch('/api/package-info', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ packagePath: item.path })
                });

                const data = await res.json().catch(() => ({}));
                if (!res.ok || !data?.ok) {
                    throw new Error(data.message || 'Paket bilgisi alinamadi.');
                }

                const thumbBase64 = typeof data.thumbnailJpegBase64 === 'string' ? data.thumbnailJpegBase64.trim() : '';
                const thumbData = thumbBase64 ? `data:image/jpeg;base64,${thumbBase64}` : null;
                const durationSeconds = Number(data.durationSeconds);
                const durationLabel = Number.isFinite(durationSeconds) && durationSeconds > 0
                    ? formatDurationLabel(durationSeconds)
                    : '--:--';
                const originalName = typeof data.originalFileName === 'string' ? data.originalFileName.trim() : '';
                const version = Number(data.version);

                if (originalName) {
                    item.name = originalName;
                }
                if (thumbData) {
                    item.thumbData = thumbData;
                }
                if (durationLabel) {
                    item.durationLabel = durationLabel;
                }
                if (Number.isFinite(version)) {
                    item.version = version;
                }

                return {
                    playlistIndex: index + 1,
                    displayName: item.name,
                    packagePath: item.path,
                    fileName: data.fileName,
                    version: data.version,
                    currentVersion: data.currentVersion,
                    isCurrentVersion: data.isCurrentVersion,
                    contentType: data.contentType,
                    originalFileName: data.originalFileName,
                    hasOriginalFileName: data.hasOriginalFileName,
                    durationSeconds,
                    durationLabel,
                    hasDuration: data.hasDuration,
                    hasThumbnail: data.hasThumbnail,
                    thumbnailData: thumbData,
                    thumbnailBytes: data.thumbnailBytes,
                    chunkSize: data.chunkSize,
                    chunkCount: data.chunkCount,
                    originalLength: data.originalLength,
                    originalLengthText: formatSize(data.originalLength),
                    headerSize: data.headerSize,
                    headerSizeText: formatSize(data.headerSize),
                    fileSizeBytes: data.fileSizeBytes,
                    fileSizeText: formatSize(data.fileSizeBytes),
                    kdfIterations: data.kdfIterations,
                    saltBytes: data.saltBytes,
                    noncePrefixBytes: data.noncePrefixBytes,
                    passwordVerifierBytes: data.passwordVerifierBytes
                };
            }

            if (item.file) {
                const local = await tryGetPackageMetaFromPackageFile(item.file);
                if (!local?.headerDebug) {
                    throw new Error('Lokal dosya header bilgisi okunamadi.');
                }

                if (local.originalName) {
                    item.name = local.originalName;
                }
                if (local.thumbData) {
                    item.thumbData = local.thumbData;
                }
                if (local.durationLabel) {
                    item.durationLabel = local.durationLabel;
                }
                if (Number.isFinite(local.version)) {
                    item.version = local.version;
                }

                return {
                    playlistIndex: index + 1,
                    displayName: item.name,
                    packagePath: '(upload secimi)',
                    ...local.headerDebug
                };
            }

            throw new Error('Bilgi alinacak playlist dosyasi bulunamadi.');
        }

        function captureThumbnailForCurrentItem() {
            if (currentPlaylistIndex < 0 || currentPlaylistIndex >= playlist.length) {
                return;
            }

            if (!video.videoWidth || !video.videoHeight) {
                return;
            }

            const item = playlist[currentPlaylistIndex];
            if (item.thumbData) {
                return;
            }
            const canvas = document.createElement('canvas');
            canvas.width = 160;
            canvas.height = Math.max(90, Math.floor((canvas.width * video.videoHeight) / video.videoWidth));
            const ctx = canvas.getContext('2d');
            if (!ctx) {
                return;
            }

            try {
                ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                const data = canvas.toDataURL('image/jpeg', 0.72);
                if (item.thumbData !== data) {
                    item.thumbData = data;
                    renderPlaylist();
                }
            } catch {
            }
        }

        async function playPlaylistIndex(index) {
            if (index < 0 || index >= playlist.length) {
                return;
            }

            const password = topPassword.value;
            if (!password) {
                throw new Error('Playlist oynatmak icin sifre gerekli.');
            }

            currentPlaylistIndex = index;
            const playingItem = playlist[index];
            if (!playingItem.durationLabel) {
                playingItem.durationLabel = '--:--';
            }
            renderPlaylist();
            const item = playingItem;
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

        function generateGuid() {
            if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
                return crypto.randomUUID();
            }

            return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
                const r = Math.random() * 16 | 0;
                const v = c === 'x' ? r : (r & 0x3 | 0x8);
                return v.toString(16);
            });
        }

        function toDefaultOutputName() {
            return `${generateGuid()}.mtaf`;
        }

        const PackageHeaderMaxBytes = 2 * 1024 * 1024;

        function bytesToBase64(bytes) {
            let binary = '';
            const chunkSize = 0x8000;
            for (let i = 0; i < bytes.length; i += chunkSize) {
                const chunk = bytes.subarray(i, i + chunkSize);
                binary += String.fromCharCode(...chunk);
            }

            return btoa(binary);
        }

        function parseThumbnailSecond(raw, fallback = 10) {
            if (raw === undefined || raw === null) {
                return fallback;
            }

            const normalized = String(raw).trim().replace(',', '.');
            if (!normalized) {
                return fallback;
            }

            const parsed = Number.parseFloat(normalized);
            if (!Number.isFinite(parsed) || parsed < 0) {
                return fallback;
            }

            return Math.min(parsed, 24 * 60 * 60);
        }

        async function tryGetPackageMetaFromPackageFile(file) {
            const empty = { originalName: null, thumbData: null, durationLabel: '--:--', version: null, headerDebug: null };
            try {
                const buffer = await file.slice(0, PackageHeaderMaxBytes).arrayBuffer();
                const view = new DataView(buffer);
                const bytes = new Uint8Array(buffer);
                let offset = 0;

                if (bytes.length < 4 || bytes[0] !== 0x4d || bytes[1] !== 0x54 || bytes[2] !== 0x41 || bytes[3] !== 0x46) {
                    return empty;
                }

                offset += 4;
                if (offset >= view.byteLength) return empty;
                const version = view.getUint8(offset);
                offset += 1;

                if (version !== 1 && version !== 2 && version !== 3 && version !== 4) {
                    return empty;
                }

                if (offset + 4 + 8 + 4 + 4 + 16 + 4 + 16 > view.byteLength) {
                    return empty;
                }

                const chunkSizeValue = view.getInt32(offset, true);
                offset += 4;
                const originalLengthValue = Number(view.getBigInt64(offset, true));
                offset += 8;
                const chunkCountValue = view.getInt32(offset, true);
                offset += 4;
                const kdfIterationsValue = view.getInt32(offset, true);
                offset += 4;
                offset += 16; // salt
                offset += 4; // nonce prefix
                offset += 16; // verifier
                if (offset >= view.byteLength) return empty;

                const ctLen = view.getUint8(offset);
                offset += 1 + ctLen;
                if (offset > view.byteLength) return empty;

                let contentType = null;
                if (ctLen > 0) {
                    const ctStart = offset - ctLen;
                    const ctBytes = bytes.slice(ctStart, offset);
                    contentType = new TextDecoder('utf-8').decode(ctBytes).trim() || null;
                }

                let originalName = null;
                if (version >= 2) {
                    if (offset + 2 > view.byteLength) return empty;
                    const nameLen = view.getUint16(offset, true);
                    offset += 2;
                    if (nameLen > 0 && offset + nameLen <= view.byteLength) {
                        const nameBytes = bytes.slice(offset, offset + nameLen);
                        const parsed = new TextDecoder('utf-8').decode(nameBytes).trim();
                        originalName = parsed || null;
                    }

                    offset += nameLen;
                }

                let thumbData = null;
                let thumbnailBytes = 0;
                if (version >= 3) {
                    if (offset + 4 > view.byteLength) return empty;
                    const thumbLen = view.getInt32(offset, true);
                    offset += 4;
                    if (thumbLen > 0 && offset + thumbLen <= view.byteLength) {
                        const thumbBytes = bytes.slice(offset, offset + thumbLen);
                        thumbnailBytes = thumbLen;
                        thumbData = `data:image/jpeg;base64,${bytesToBase64(thumbBytes)}`;
                    }

                    offset += Math.max(0, thumbLen);
                }

                let durationLabel = '--:--';
                let durationSeconds = null;
                if (version >= 4 && offset + 8 <= view.byteLength) {
                    const parsedDuration = view.getFloat64(offset, true);
                    if (Number.isFinite(parsedDuration) && parsedDuration > 0) {
                        durationSeconds = parsedDuration;
                        durationLabel = formatDurationLabel(parsedDuration);
                    }

                    offset += 8;
                }

                return {
                    originalName,
                    thumbData,
                    durationLabel,
                    version,
                    headerDebug: {
                        fileName: file.name,
                        version,
                        currentVersion: CURRENT_HEADER_VERSION,
                        isCurrentVersion: version === CURRENT_HEADER_VERSION,
                        contentType,
                        originalFileName: originalName,
                        hasOriginalFileName: !!originalName,
                        durationSeconds,
                        hasDuration: Number.isFinite(durationSeconds) && durationSeconds > 0,
                        hasThumbnail: thumbnailBytes > 0,
                        thumbnailBytes,
                        chunkSize: chunkSizeValue,
                        chunkCount: chunkCountValue,
                        originalLength: originalLengthValue,
                        originalLengthText: formatSize(originalLengthValue),
                        headerSize: offset,
                        headerSizeText: formatSize(offset),
                        fileSizeBytes: file.size,
                        fileSizeText: formatSize(file.size),
                        kdfIterations: kdfIterationsValue,
                        saltBytes: 16,
                        noncePrefixBytes: 4,
                        passwordVerifierBytes: 16
                    }
                };
            } catch {
                return empty;
            }
        }

        async function tryGetPackageMetaByPath(path) {
            try {
                const res = await fetch('/api/package-info', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ packagePath: path })
                });

                const data = await res.json().catch(() => ({}));
                if (!res.ok || !data?.ok) {
                    return { originalName: null, thumbData: null, durationLabel: '--:--', version: null };
                }

                const value = typeof data.originalFileName === 'string' ? data.originalFileName.trim() : '';
                const thumbBase64 = typeof data.thumbnailJpegBase64 === 'string' ? data.thumbnailJpegBase64.trim() : '';
                const durationSeconds = Number(data.durationSeconds);
                const version = Number(data.version);
                return {
                    originalName: value || null,
                    thumbData: thumbBase64 ? `data:image/jpeg;base64,${thumbBase64}` : null,
                    durationLabel: Number.isFinite(durationSeconds) && durationSeconds > 0 ? formatDurationLabel(durationSeconds) : '--:--',
                    version: Number.isFinite(version) ? version : null
                };
            } catch {
                return { originalName: null, thumbData: null, durationLabel: '--:--', version: null };
            }
        }

        async function captureThumbnailJpegFromVideoFile(file, targetSecond) {
            if (!file) {
                return null;
            }

            const objectUrl = URL.createObjectURL(file);
            try {
                const probe = document.createElement('video');
                probe.preload = 'metadata';
                probe.muted = true;
                probe.playsInline = true;

                const waitFor = (eventName) => new Promise((resolve, reject) => {
                    const onOk = () => {
                        cleanup();
                        resolve();
                    };
                    const onErr = () => {
                        cleanup();
                        reject(new Error('Videodan thumbnail alinamadi.'));
                    };
                    const cleanup = () => {
                        probe.removeEventListener(eventName, onOk);
                        probe.removeEventListener('error', onErr);
                    };

                    probe.addEventListener(eventName, onOk, { once: true });
                    probe.addEventListener('error', onErr, { once: true });
                });

                probe.src = objectUrl;
                await waitFor('loadedmetadata');

                const duration = Number.isFinite(probe.duration) ? probe.duration : 0;
                const seekSecond = duration > 0
                    ? Math.min(Math.max(0, targetSecond), Math.max(0, duration - 0.15))
                    : 0;

                if (duration > 0 && Math.abs(probe.currentTime - seekSecond) > 0.05) {
                    probe.currentTime = seekSecond;
                    await waitFor('seeked');
                }

                if (probe.readyState < 2) {
                    await waitFor('loadeddata');
                }

                if (!probe.videoWidth || !probe.videoHeight) {
                    return null;
                }

                const maxWidth = 320;
                const scale = Math.min(1, maxWidth / probe.videoWidth);
                const width = Math.max(32, Math.floor(probe.videoWidth * scale));
                const height = Math.max(18, Math.floor(probe.videoHeight * scale));
                const canvas = document.createElement('canvas');
                canvas.width = width;
                canvas.height = height;
                const ctx = canvas.getContext('2d');
                if (!ctx) {
                    throw new Error('Thumbnail islenemedi.');
                }

                ctx.drawImage(probe, 0, 0, width, height);
                const blob = await new Promise((resolve, reject) => {
                    canvas.toBlob((b) => b ? resolve(b) : reject(new Error('Thumbnail JPEG olusturulamadi.')), 'image/jpeg', 0.82);
                });

                const arrayBuffer = await blob.arrayBuffer();
                return new Uint8Array(arrayBuffer);
            } finally {
                URL.revokeObjectURL(objectUrl);
            }
        }

        async function readDurationFromVideoFile(file) {
            if (!file) {
                return null;
            }

            const objectUrl = URL.createObjectURL(file);
            try {
                const probe = document.createElement('video');
                probe.preload = 'metadata';
                probe.muted = true;
                probe.playsInline = true;

                const duration = await new Promise((resolve) => {
                    const onLoaded = () => {
                        cleanup();
                        const value = Number.isFinite(probe.duration) && probe.duration > 0 ? probe.duration : null;
                        resolve(value);
                    };

                    const onError = () => {
                        cleanup();
                        resolve(null);
                    };

                    const cleanup = () => {
                        probe.removeEventListener('loadedmetadata', onLoaded);
                        probe.removeEventListener('error', onError);
                    };

                    probe.addEventListener('loadedmetadata', onLoaded, { once: true });
                    probe.addEventListener('error', onError, { once: true });
                    probe.src = objectUrl;
                    probe.load();
                });

                return duration;
            } finally {
                URL.revokeObjectURL(objectUrl);
            }
        }

        async function openPackageForMetadata(path, password) {
            const res = await fetch('/api/open', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ packagePath: path, password })
            });

            const data = await res.json().catch(() => ({}));
            if (!res.ok || !data?.ok) {
                throw new Error(data.message || 'Metadata icin dosya acilamadi.');
            }

            const loaded = new Promise((resolve, reject) => {
                const onLoaded = () => {
                    cleanup();
                    resolve();
                };
                const onError = () => {
                    cleanup();
                    reject(new Error('Video metadata okunamadi.'));
                };
                const cleanup = () => {
                    video.removeEventListener('loadedmetadata', onLoaded);
                    video.removeEventListener('error', onError);
                };
                video.addEventListener('loadedmetadata', onLoaded, { once: true });
                video.addEventListener('error', onError, { once: true });
            });

            video.pause();
            video.src = '/stream?ts=' + Date.now();
            video.load();
            await loaded;
            return data;
        }

        async function captureThumbnailDataFromActiveVideo(targetSecond) {
            if (!video.videoWidth || !video.videoHeight) {
                return null;
            }

            const duration = Number.isFinite(video.duration) ? video.duration : 0;
            if (duration > 0) {
                const seekSecond = Math.min(Math.max(0, targetSecond), Math.max(0, duration - 0.15));
                if (Math.abs(video.currentTime - seekSecond) > 0.05) {
                    await new Promise((resolve, reject) => {
                        const onSeeked = () => {
                            cleanup();
                            resolve();
                        };
                        const onError = () => {
                            cleanup();
                            reject(new Error('Thumbnail seek basarisiz.'));
                        };
                        const cleanup = () => {
                            video.removeEventListener('seeked', onSeeked);
                            video.removeEventListener('error', onError);
                        };

                        video.addEventListener('seeked', onSeeked, { once: true });
                        video.addEventListener('error', onError, { once: true });
                        video.currentTime = seekSecond;
                    });
                }
            }

            const canvas = document.createElement('canvas');
            canvas.width = 320;
            canvas.height = Math.max(180, Math.floor((canvas.width * video.videoHeight) / video.videoWidth));
            const ctx = canvas.getContext('2d');
            if (!ctx) {
                return null;
            }

            ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
            return canvas.toDataURL('image/jpeg', 0.82);
        }

        async function uploadThumbnailBytes(thumbBytes) {
            const res = await fetch('/api/thumbnail-upload', {
                method: 'POST',
                headers: { 'Content-Type': 'application/octet-stream' },
                body: thumbBytes
            });

            const data = await res.json().catch(() => ({}));
            if (!res.ok || !data?.ok || !data.thumbnailId) {
                throw new Error(data.message || 'Thumbnail yuklenemedi.');
            }

            return String(data.thumbnailId);
        }

        async function startPackJobForFile(file, outputName, password, parsedChunk, onUploadProgress, thumbnailId = null, durationSeconds = null) {
            const result = await new Promise((resolve, reject) => {
                const xhr = new XMLHttpRequest();
                xhr.open('POST', '/api/pack-upload');
                xhr.setRequestHeader('X-Password', password);
                xhr.setRequestHeader('X-Chunk-Mb', String(parsedChunk));
                xhr.setRequestHeader('X-Output-Name', outputName);
                xhr.setRequestHeader('X-File-Name', file.name);
                if (thumbnailId) {
                    xhr.setRequestHeader('X-Thumbnail-Id', thumbnailId);
                }
                if (Number.isFinite(durationSeconds) && durationSeconds > 0) {
                    xhr.setRequestHeader('X-Duration-Seconds', String(durationSeconds));
                }
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
            const password = topPassword.value;
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

                const meta = await tryGetPackageMetaFromPackageFile(file);
                playlist.push({ file, name: meta.originalName || file.name, thumbData: meta.thumbData || null, durationLabel: meta.durationLabel || '--:--', version: meta.version });
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

        topOpenBtn?.addEventListener('click', async () => {
            status.textContent = 'Native dosya secici aciliyor...';
            try {
                const res = await fetch('/api/pick-file?kind=mtaf&multi=true');
                const data = await res.json();
                if (!res.ok || !data.ok) {
                    status.textContent = data.message || 'Dosya secilemedi.';
                    return;
                }

                const picked = Array.isArray(data.paths)
                    ? data.paths
                    : (typeof data.path === 'string' ? [data.path] : []);
                const validPaths = picked
                    .filter((p) => typeof p === 'string')
                    .map((p) => p.trim())
                    .filter((p) => p.length > 0 && p.toLowerCase().endsWith('.mtaf'));

                if (validPaths.length === 0) {
                    status.textContent = 'Secimden listeye eklenecek .mtaf bulunamadi.';
                    return;
                }

                const firstAddedIndex = playlist.length;
                for (const p of validPaths) {
                    const fallbackName = p.split('/').pop() || p;
                    const meta = await tryGetPackageMetaByPath(p);
                    const name = meta.originalName || fallbackName;
                    playlist.push({ path: p, name, thumbData: meta.thumbData || null, durationLabel: meta.durationLabel || '--:--', version: meta.version });
                }

                if (currentPlaylistIndex < 0 && playlist.length > 0) {
                    currentPlaylistIndex = firstAddedIndex;
                }

                renderPlaylist();

                const password = topPassword.value;
                if (!password) {
                    status.textContent = `${validPaths.length} dosya playlist'e eklendi. Acmak icin sifre gir.`;
                    return;
                }

                const firstPath = validPaths[0];
                await openByPath(firstPath, password, 'native');
                currentPlaylistIndex = firstAddedIndex;
                renderPlaylist();
            } catch (err) {
                setOpenProgress(0);
                status.textContent = err?.message || 'Dosya acilamadi.';
            }
        });

        migratePlaylistBtn?.addEventListener('click', async () => {
            const targets = playlist.filter((item) => {
                if (!item.path) {
                    return false;
                }

                const hasKnownVersion = Number.isFinite(item.version);
                const needsVersionUpgrade = !hasKnownVersion || item.version < CURRENT_HEADER_VERSION;
                const needsDuration = !item.durationLabel || item.durationLabel === '--:--';
                const needsThumbnail = !item.thumbData;
                return needsVersionUpgrade || needsDuration || needsThumbnail;
            });
            if (targets.length === 0) {
                status.textContent = `Guncellenecek surum/metadata eksigi bulunamadi (v${CURRENT_HEADER_VERSION}).`;
                return;
            }

            const password = topPassword.value;
            if (!password) {
                status.textContent = 'Migration icin once sifreyi gir.';
                return;
            }

            let migratedCount = 0;
            let failedCount = 0;

            for (let i = 0; i < targets.length; i++) {
                const item = targets[i];
                status.textContent = `Surum/metadata guncelleniyor ${i + 1}/${targets.length}: ${item.name}`;
                try {
                    let durationSeconds = null;
                    let thumbnailDataUrl = null;
                    const needsDuration = !item.durationLabel || item.durationLabel === '--:--';
                    const needsThumbnail = !item.thumbData;
                    if (needsDuration || needsThumbnail) {
                        await openPackageForMetadata(item.path, password);
                        if (needsDuration && Number.isFinite(video.duration) && video.duration > 0) {
                            durationSeconds = video.duration;
                        }

                        if (needsThumbnail) {
                            thumbnailDataUrl = await captureThumbnailDataFromActiveVideo(10);
                        }
                    }

                    const res = await fetch('/api/migrate-package', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            packagePath: item.path,
                            inPlace: true,
                            originalFileName: item.name,
                            durationSeconds,
                            thumbnailDataUrl
                        })
                    });

                    const data = await res.json().catch(() => ({}));
                    if (!res.ok || !data?.ok) {
                        failedCount++;
                        continue;
                    }

                    const refreshed = await tryGetPackageMetaByPath(item.path);
                    item.version = refreshed.version ?? CURRENT_HEADER_VERSION;
                    item.durationLabel = refreshed.durationLabel || item.durationLabel || '--:--';
                    item.thumbData = refreshed.thumbData || item.thumbData || null;
                    if (refreshed.originalName) {
                        item.name = refreshed.originalName;
                    }

                    migratedCount++;
                } catch {
                    failedCount++;
                }
            }

            renderPlaylist();
            status.textContent = `Guncelleme tamamlandi. Basarili: ${migratedCount}, hatali: ${failedCount}.`;
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
            const password = topPassword.value;
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

        video.addEventListener('loadeddata', () => {
            captureThumbnailForCurrentItem();
        });

        video.addEventListener('loadedmetadata', () => {
            if (currentPlaylistIndex < 0 || currentPlaylistIndex >= playlist.length) {
                return;
            }

            const item = playlist[currentPlaylistIndex];
            item.durationLabel = formatDurationLabel(video.duration);
            renderPlaylist();
        });

        videoInput.addEventListener('change', () => {
            const file = videoInput.files && videoInput.files[0];
            if (!file) {
                return;
            }

            mtafOutput.value = toDefaultOutputName();
        });

        infoCloseBtn?.addEventListener('click', closeInfoModal);
        infoCloseBtn2?.addEventListener('click', closeInfoModal);
        infoModal?.addEventListener('click', (evt) => {
            if (evt.target === infoModal) {
                closeInfoModal();
            }
        });
        document.addEventListener('keydown', (evt) => {
            if (evt.key === 'Escape') {
                closeInfoModal();
            }
        });

        packBtn.addEventListener('click', async () => {
            const file = videoInput.files && videoInput.files[0];
            const outputPath = mtafOutput.value.trim() || (file ? toDefaultOutputName() : '');
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
                let thumbnailId = null;
                const thumbSecond = parseThumbnailSecond(packThumbSecond?.value, 10);
                const sourceDuration = await readDurationFromVideoFile(file);
                packStatus.textContent = `Thumbnail aliniyor (${thumbSecond.toFixed(1)}s)...`;
                const thumbBytes = await captureThumbnailJpegFromVideoFile(file, thumbSecond);
                if (thumbBytes) {
                    packStatus.textContent = 'Thumbnail yukleniyor...';
                    thumbnailId = await uploadThumbnailBytes(thumbBytes);
                }

                const jobId = await startPackJobForFile(file, outputPath, password, parsedChunk, (uploadPercent) => {
                    const p = Math.floor(uploadPercent * 0.35);
                    setPackProgress(p);
                    packStatus.textContent = `Yukleniyor... ${Math.floor(uploadPercent)}%`;
                }, thumbnailId, sourceDuration);
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

            const batchThumbAtSecond = parseThumbnailSecond(batchThumbSecond?.value, 10);

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
                    const outName = toDefaultOutputName();

                    batchStatus.textContent = `${i + 1}/${files.length} isleniyor: ${current.name} (yukleniyor)`;
                    const sourceDuration = await readDurationFromVideoFile(file);
                    let thumbnailId = null;
                    try {
                        const thumbBytes = await captureThumbnailJpegFromVideoFile(file, batchThumbAtSecond);
                        if (thumbBytes) {
                            thumbnailId = await uploadThumbnailBytes(thumbBytes);
                        }
                    } catch {
                    }

                    const jobId = await startPackJobForFile(file, outName, password, parsedChunk, (uploadPercent) => {
                        const within = Math.floor(uploadPercent * 0.35);
                        const total = ((i + (within / 100)) / files.length) * 100;
                        setBatchProgress(total);
                    }, thumbnailId, sourceDuration);

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
                html = html.Replace("__CURRENT_HEADER_VERSION__", PackageHeader.CurrentVersion.ToString());

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

            private static string BuildGuidMtafName()
            {
                return SanitizeFileName($"{Guid.NewGuid():D}.mtaf");
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

            private sealed class MigratePackageRequest
            {
                public string? PackagePath { get; set; }
                public string? OutputPath { get; set; }
                public bool? InPlace { get; set; }
                public string? OriginalFileName { get; set; }
                public string? ThumbnailDataUrl { get; set; }
                public double? DurationSeconds { get; set; }
            }

            private sealed class PackJobState
            {
                public required string JobId { get; init; }
                public required string InputPath { get; init; }
                public required string OutputPath { get; init; }
                public required string OutputFileName { get; init; }
                public required string SourceContentType { get; init; }
                public required string SourceFileName { get; init; }
                public byte[]? SourceThumbnailJpeg { get; init; }
                public double? SourceDurationSeconds { get; init; }
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
