using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GifCapture;

/// <summary>A published release that is newer than the running app.</summary>
sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string PageUrl,
    string? DownloadUrl, long DownloadSize, string? Sha256);

/// <summary>
/// Looks for new versions on the project's GitHub Releases and installs them in place. The only data sent is the request
/// for the latest release (with the app version in the User-Agent); nothing about the user's recordings leaves the PC.
/// </summary>
static class Updater
{
    const string Repo = "dnwls010728/GIFCapture";
    const string AssetName = "GifCapture.exe";

    // Declared before Http: static initializers run in order, and the client's User-Agent reads it.
    public static Version CurrentVersion { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // per-call timeouts below; downloads can be long
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GifCapture", CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Major.minor.build only, so 1.2.0 and 1.2.0.0 compare equal.</summary>
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static Version? ParseTag(string tag) =>
        Version.TryParse(tag.Trim().TrimStart('v', 'V').Split('-', '+')[0], out var v) ? Normalize(v) : null;

    /// <summary>The latest release if it is newer than this build; null when up to date. Throws on network or API errors.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        // "latest" already skips drafts and pre-releases.
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release published yet
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseTag(tag) is not { } version || version <= CurrentVersion) return null;

        string? url = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                url = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                // GitHub lists "sha256:<hex>" for uploaded assets; older releases may not have it.
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..];
                break;
            }

        return new ReleaseInfo(version, tag, root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases", url, size, sha);
    }

    /// <summary>
    /// The running executable, when this is the packaged single-file app in a folder we may write to. Anything else
    /// (a debug build run through dotnet, a protected install folder) gets the release page instead.
    /// </summary>
    public static string? ReplaceableExe
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !string.Equals(Path.GetFileName(exe), AssetName, StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var probe = Path.Combine(Path.GetDirectoryName(exe)!, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
                return exe;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Downloads the new executable next to the running one and checks its size and SHA-256 (when GitHub lists one).
    /// Returns the path of the verified file.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, string exe, IProgress<float> progress, CancellationToken ct)
    {
        var part = exe + ".download";
        try
        {
            using var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.DownloadSize;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    if (total > 0) progress.Report((float)done / total);
                }
                if (release.DownloadSize > 0 && done != release.DownloadSize)
                    throw new InvalidDataException(L.T("내려받은 파일의 크기가 맞지 않습니다."));
            }
            if (release.Sha256 != null && !Convert.ToHexString(sha.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("내려받은 파일이 손상되었습니다 (SHA-256 불일치)."));
            return part;
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Swaps the downloaded file in and starts it. Windows lets a running executable be renamed but not overwritten,
    /// so the current one steps aside as ".old" and is cleaned up by the next launch.
    /// </summary>
    public static void InstallAndRestart(string exe, string downloaded)
    {
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe); // put the working version back
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Exit();
    }

    /// <summary>Removes what a previous update left behind. Called at startup; failures are harmless.</summary>
    public static void CleanUp()
    {
        if (Environment.ProcessPath is not { } exe) return;
        foreach (var leftover in new[] { exe + ".old", exe + ".download" })
            try { if (File.Exists(leftover)) File.Delete(leftover); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Errors that just mean "could not check right now": offline, timed out, rate-limited, unexpected reply.</summary>
    public static bool IsCheckFailure(Exception e) =>
        e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException;

    public static void OpenPage(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
