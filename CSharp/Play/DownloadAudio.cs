using Godot;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Godot.Collections;

public partial class DownloadAudio : Node
{
    public static DownloadAudio Instance { get; private set; }

    public override void _Ready()
    {
        Instance = this;
    }

    #region 音频参数
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int BytesPerFrame = 4;
    #endregion
    private static string GetUserAgent()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        }
        else if (OperatingSystem.IsMacOS())
        {
            return "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        }
        else if (OperatingSystem.IsLinux())
        {
            return "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        }
        // 默认 fallback
        return "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    }

    // ======================== 异步方法 ========================

    public static async Task StreamAudioToStreamAsync(string url, string referer, Stream targetStream, CancellationToken cancellationToken = default)
    {
        using var client = new System.Net.Http.HttpClient();
        client.DefaultRequestHeaders.Add("Referer", referer ?? "");
        // 🔧 修复：使用平台自适应的 User-Agent
        client.DefaultRequestHeaders.Add("User-Agent", GetUserAgent());
        client.DefaultRequestHeaders.Add("Accept-Encoding", "identity");
        client.DefaultRequestHeaders.Add("Accept", "*/*");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public async Task<string> DownloadAudioAsync(string url, string referer, CancellationToken cancellationToken = default)
    {
        var tempPath = CSharpFunc.NormalizePathSimple(Path.Combine(OS.GetUserDataDir(), $"temp_audio_{Guid.NewGuid()}.m4s"), true);
        var http = new HttpRequest();
        AddChild(http);
        try
        {
            // 🔧 修复：使用平台自适应的 User-Agent
            var headers = new[]
            {
                $"Referer: {referer ?? ""}",
                $"User-Agent: {GetUserAgent()}",
                "Accept-Encoding: identity",
                "Accept: */*",
                "Accept-Language: zh-CN,zh;q=0.9,en;q=0.8"
            };

            http.SetDownloadFile(tempPath);
            Error err = http.Request(url, headers);
            if (err != Error.Ok)
                throw new Exception($"请求发送失败: {err}");

            var result = await ToSignal(http, HttpRequest.SignalName.RequestCompleted);
            long responseCode = (long)result[1];
            byte[] responseBody = (byte[])result[3];

            if (responseCode != 200)
            {
                string errorMsg = responseBody?.Length > 0 ? Encoding.UTF8.GetString(responseBody) : "无响应内容";
                throw new Exception($"HTTP 错误: {responseCode} - {errorMsg}");
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                throw new Exception("下载的文件无效或为空");
        }
        finally
        {
            RemoveChild(http);
            http.QueueFree();
        }
        return tempPath;
    }

    public async Task<(string audioUrl, string referer, string title, string coverUrl)> GetAudioInfoByBvAsync(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid))
            throw new ArgumentException("BV 号不能为空。", nameof(bvid));

        string viewUrl = $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";
        using var viewHttp = new HttpRequest();
        AddChild(viewHttp);
        try
        {
            Error err = viewHttp.Request(viewUrl);
            if (err != Error.Ok)
                throw new Exception($"视频详情请求失败: {err}");

            var viewResult = await ToSignal(viewHttp, HttpRequest.SignalName.RequestCompleted);
            long viewCode = (long)viewResult[1];
            byte[] viewBody = (byte[])viewResult[3];
            if (viewCode != 200)
                throw new Exception($"视频详情 HTTP 错误: {viewCode}");

            string viewJson = Encoding.UTF8.GetString(viewBody);
            using var viewDoc = JsonDocument.Parse(viewJson);
            JsonElement data = viewDoc.RootElement.GetProperty("data");
            string title = data.GetProperty("title").GetString();
            string coverUrl = data.GetProperty("pic").GetString();
            JsonElement pages = data.GetProperty("pages");
            JsonElement firstPage = pages[0];
            long cid = firstPage.GetProperty("cid").GetInt64();

            string playUrl = $"https://api.bilibili.com/x/player/playurl?fnval=80&qn=80&fourk=0&otype=json&bvid={bvid}&cid={cid}";
            using var playHttp = new HttpRequest();
            AddChild(playHttp);
            try
            {
                err = playHttp.Request(playUrl);
                if (err != Error.Ok)
                    throw new Exception($"播放地址请求失败: {err}");

                var playResult = await ToSignal(playHttp, HttpRequest.SignalName.RequestCompleted);
                long playCode = (long)playResult[1];
                byte[] playBody = (byte[])playResult[3];
                if (playCode != 200)
                    throw new Exception($"播放地址 HTTP 错误: {playCode}");

                string playJson = Encoding.UTF8.GetString(playBody);
                using var playDoc = JsonDocument.Parse(playJson);
                JsonElement playData = playDoc.RootElement.GetProperty("data");
                JsonElement dash = playData.GetProperty("dash");
                JsonElement audioArray = dash.GetProperty("audio");
                JsonElement firstAudio = audioArray[0];
                string audioBaseUrl = firstAudio.GetProperty("baseUrl").GetString();

                string referer = BuildVideoPageUrl(bvid);
                return (audioBaseUrl, referer, title, coverUrl);
            }
            finally
            {
                RemoveChild(playHttp);
                playHttp.QueueFree();
            }
        }
        finally
        {
            RemoveChild(viewHttp);
            viewHttp.QueueFree();
        }
    }

    public async Task<(string audioUrl, string referer, string title, string coverUrl)> GetAudioInfoByAuIdAsync(string auId)
    {
        if (string.IsNullOrWhiteSpace(auId))
            throw new ArgumentException("AU 号不能为空。", nameof(auId));

        string sidStr = auId.Trim().ToLower().Replace("au", "");
        if (!long.TryParse(sidStr, out long sid))
            throw new ArgumentException("AU 号格式不正确，应为数字。", nameof(auId));

        // 🔧 修复：使用平台自适应的 User-Agent
        string userAgent = GetUserAgent();
        string referer = BuildAudioPageUrl(auId);
        string[] headers = [$"User-Agent: {userAgent}", $"Referer: {referer}"];

        string infoUrl = $"https://www.bilibili.com/audio/music-service-c/web/song/info?sid={sid}";
        using var infoHttp = new HttpRequest();
        AddChild(infoHttp);
        try
        {
            Error err = infoHttp.Request(infoUrl, headers);
            if (err != Error.Ok)
                throw new Exception($"音频详情请求失败: {err}");

            var infoResult = await ToSignal(infoHttp, HttpRequest.SignalName.RequestCompleted);
            long infoCode = (long)infoResult[1];
            byte[] infoBody = (byte[])infoResult[3];
            if (infoCode != 200)
                throw new Exception($"音频详情 HTTP 错误: {infoCode}");

            string infoJson = Encoding.UTF8.GetString(infoBody);
            using var infoDoc = JsonDocument.Parse(infoJson);
            if (infoDoc.RootElement.TryGetProperty("code", out JsonElement codeEl) && codeEl.GetInt32() != 0)
            {
                string msg = infoDoc.RootElement.TryGetProperty("msg", out JsonElement msgEl) ? msgEl.GetString() : "未知错误";
                throw new Exception($"音频详情 API 错误: {msg}");
            }

            JsonElement infoData = infoDoc.RootElement.GetProperty("data");
            string title = infoData.GetProperty("title").GetString();
            string coverUrl = infoData.GetProperty("cover").GetString();

            string playUrl = $"https://www.bilibili.com/audio/music-service-c/web/url?sid={sid}&privilege=2&quality=2";
            using var playHttp = new HttpRequest();
            AddChild(playHttp);
            try
            {
                err = playHttp.Request(playUrl, headers);
                if (err != Error.Ok)
                    throw new Exception($"播放地址请求失败: {err}");

                var playResult = await ToSignal(playHttp, HttpRequest.SignalName.RequestCompleted);
                long playCode = (long)playResult[1];
                byte[] playBody = (byte[])playResult[3];
                if (playCode != 200)
                    throw new Exception($"播放地址 HTTP 错误: {playCode}");

                string playJson = Encoding.UTF8.GetString(playBody);
                using var playDoc = JsonDocument.Parse(playJson);
                if (playDoc.RootElement.TryGetProperty("code", out JsonElement playCodeEl) && playCodeEl.GetInt32() != 0)
                {
                    string msg = playDoc.RootElement.TryGetProperty("msg", out JsonElement msgEl) ? msgEl.GetString() : "未知错误";
                    throw new Exception($"音频流 API 错误: {msg}");
                }

                JsonElement playData = playDoc.RootElement.GetProperty("data");
                if (playData.TryGetProperty("type", out JsonElement typeEl) && typeEl.GetInt32() == -1)
                    GD.PrintErr("警告：该音频为付费歌曲，可能仅返回试听版本（30秒）");

                JsonElement cdns = playData.GetProperty("cdns");
                string audioBaseUrl = cdns[0].GetString();
                return (audioBaseUrl, referer, title, coverUrl);
            }
            finally
            {
                RemoveChild(playHttp);
                playHttp.QueueFree();
            }
        }
        finally
        {
            RemoveChild(infoHttp);
            infoHttp.QueueFree();
        }
    }

    public static string BuildVideoPageUrl(string bvid) => $"https://www.bilibili.com/video/{bvid}";
    public static string BuildAudioPageUrl(string auId) => $"https://www.bilibili.com/audio/{(auId.StartsWith("au", StringComparison.OrdinalIgnoreCase) ? auId : "au" + auId)}";

    // ======================== 同步阻塞方法 ========================

    /// <summary>
    /// 同步下载音频文件，返回临时文件路径。会阻塞直到下载完成。
    /// </summary>
    public static string DownloadAudioSync(string url, string referer)
    {
        try
        {
            // 使用 HttpClient 同步下载
            string tempPath = CSharpFunc.NormalizePathSimple(Path.Combine(OS.GetUserDataDir(), $"temp_audio_{Guid.NewGuid()}.m4s"), true);

            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.Add("Referer", referer ?? "");
                // 🔧 修复：使用平台自适应的 User-Agent
                client.DefaultRequestHeaders.Add("User-Agent", GetUserAgent());
                client.DefaultRequestHeaders.Add("Accept-Encoding", "identity");
                client.DefaultRequestHeaders.Add("Accept", "*/*");

                using (var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var fileStream = File.Create(tempPath))
                    {
                        stream.CopyTo(fileStream);
                    }
                }
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                throw new Exception("下载的文件无效或为空");
            return tempPath;
        }
        catch (Exception ex)
        {
            throw new Exception($"同步下载失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 通过 BV 号同步获取音频信息，返回 Dictionary 包含 audioUrl, referer, title, coverUrl。
    /// </summary>
    public static Dictionary GetAudioInfoByBvSync(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid))
            throw new ArgumentException("BV 号不能为空。", nameof(bvid));

        try
        {
            // 1. 获取视频详情（cid, title, cover）
            string viewUrl = $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";
            string viewJson = RequestStringSync(viewUrl);
            using var viewDoc = JsonDocument.Parse(viewJson);
            JsonElement viewData = viewDoc.RootElement.GetProperty("data");
            string title = viewData.GetProperty("title").GetString();
            string coverUrl = viewData.GetProperty("pic").GetString();
            long cid = viewData.GetProperty("pages")[0].GetProperty("cid").GetInt64();

            // 2. 获取播放地址
            string playUrl = $"https://api.bilibili.com/x/player/playurl?fnval=80&qn=80&fourk=0&otype=json&bvid={bvid}&cid={cid}";
            string playJson = RequestStringSync(playUrl);
            using var playDoc = JsonDocument.Parse(playJson);
            JsonElement playData = playDoc.RootElement.GetProperty("data");
            JsonElement dash = playData.GetProperty("dash");
            string audioBaseUrl = dash.GetProperty("audio")[0].GetProperty("baseUrl").GetString();
            string referer = BuildVideoPageUrl(bvid);

            var dict = new Dictionary
            {
                ["audioUrl"] = audioBaseUrl,
                ["referer"] = referer,
                ["title"] = title,
                ["coverUrl"] = coverUrl
            };
            return dict;
        }
        catch (Exception ex)
        {
            throw new Exception($"同步获取视频音频信息失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 通过 AU 号同步获取音频信息，返回 Dictionary 包含 audioUrl, referer, title, coverUrl。
    /// </summary>
    public static Dictionary GetAudioInfoByAuSync(string auId)
    {
        if (string.IsNullOrWhiteSpace(auId))
            throw new ArgumentException("AU 号不能为空。", nameof(auId));

        string sidStr = auId.Trim().ToLower().Replace("au", "");
        if (!long.TryParse(sidStr, out long sid))
            throw new ArgumentException("AU 号格式不正确，应为数字。", nameof(auId));

        try
        {
            string referer = BuildAudioPageUrl(auId);
            // 🔧 修复：使用平台自适应的 User-Agent
            string userAgent = GetUserAgent();

            // 1. 获取音频详情（title, cover）
            string infoUrl = $"https://www.bilibili.com/audio/music-service-c/web/song/info?sid={sid}";
            string infoJson = RequestStringSync(infoUrl, userAgent, referer);
            using var infoDoc = JsonDocument.Parse(infoJson);
            if (infoDoc.RootElement.TryGetProperty("code", out JsonElement codeEl) && codeEl.GetInt32() != 0)
            {
                string msg = infoDoc.RootElement.TryGetProperty("msg", out JsonElement msgEl) ? msgEl.GetString() : "未知错误";
                throw new Exception($"音频详情 API 错误: {msg}");
            }
            JsonElement infoData = infoDoc.RootElement.GetProperty("data");
            string title = infoData.GetProperty("title").GetString();
            string coverUrl = infoData.GetProperty("cover").GetString();

            // 2. 获取播放地址
            string playUrl = $"https://www.bilibili.com/audio/music-service-c/web/url?sid={sid}&privilege=2&quality=2";
            string playJson = RequestStringSync(playUrl, userAgent, referer);
            using var playDoc = JsonDocument.Parse(playJson);
            if (playDoc.RootElement.TryGetProperty("code", out JsonElement playCodeEl) && playCodeEl.GetInt32() != 0)
            {
                string msg = playDoc.RootElement.TryGetProperty("msg", out JsonElement msgEl) ? msgEl.GetString() : "未知错误";
                throw new Exception($"音频流 API 错误: {msg}");
            }
            JsonElement playData = playDoc.RootElement.GetProperty("data");
            if (playData.TryGetProperty("type", out JsonElement typeEl) && typeEl.GetInt32() == -1)
                GD.PrintErr("警告：该音频为付费歌曲，可能仅返回试听版本（30秒）");
            string audioBaseUrl = playData.GetProperty("cdns")[0].GetString();

            var dict = new Dictionary
            {
                ["audioUrl"] = audioBaseUrl,
                ["referer"] = referer,
                ["title"] = title,
                ["coverUrl"] = coverUrl
            };
            return dict;
        }
        catch (Exception ex)
        {
            throw new Exception($"同步获取音频信息失败: {ex.Message}", ex);
        }
    }

    private static string RequestStringSync(string url, string userAgent = null, string referer = null)
    {
        using (var client = new System.Net.Http.HttpClient())
        {
            // 🔧 修复：使用平台自适应或提供的 User-Agent
            client.DefaultRequestHeaders.Add("User-Agent", userAgent ?? GetUserAgent());
            client.DefaultRequestHeaders.Add("Accept", "*/*");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "identity");
            if (!string.IsNullOrEmpty(referer))
                client.DefaultRequestHeaders.Add("Referer", referer);

            using (var response = client.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"HTTP 错误: {(int)response.StatusCode} {response.StatusCode}");
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }
    }
}