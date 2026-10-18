using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EntLimit.Online;

public sealed class ApiSettings
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
}

public static class ApiEndpoint
{
    public static Uri Validate(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("端点必须是完整 URL，不能包含用户名、查询参数或片段。");
        bool local = uri.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host.Trim('[',']'),out var ip) && IPAddress.IsLoopback(ip);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && local)) throw new InvalidOperationException("远程端点必须使用 HTTPS；HTTP 仅允许本机 localhost/回环地址。");
        return uri;
    }
    public static string Canonical(string value) => Validate(value).AbsoluteUri.TrimEnd('/');
    public static Uri Completion(string value)
    {
        string canonical = Canonical(value);
        return new Uri(canonical.EndsWith("/chat/completions",StringComparison.OrdinalIgnoreCase) ? canonical : canonical + "/chat/completions");
    }
    public static void ValidateModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl)) throw new InvalidOperationException("请填写有效的模型名称（最多 200 字符）。");
    }
}

public sealed class CompatibleApiClient : IDisposable
{
    public const int MaxResponseBytes = 256 * 1024;
    private readonly HttpClient _http;
    public CompatibleApiClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false,UseProxy=false }) : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }
    public async Task<string> CompleteAsync(ApiSettings settings,string key,string system,string prompt,CancellationToken cancellationToken=default)
    {
        key=key.Trim();
        Uri endpoint = ApiEndpoint.Completion(settings.Endpoint); ApiEndpoint.ValidateModel(settings.Model);
        if (key.Length > 16000 || key.Any(c=>c<'!' || c>'~')) throw new InvalidOperationException("密钥必须为可见 ASCII 字符，不能包含空白或控制字符。");
        if (endpoint.Scheme=="http" && key.Length>0) throw new InvalidOperationException("本机 HTTP 仅支持无密钥服务；携带密钥请使用 HTTPS。");
        if (prompt.Length > 100000 || system.Length > 20000) throw new InvalidOperationException("发送内容过大，请缩短代码或生成要求。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Post,endpoint);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",key.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(new { model=settings.Model,stream=false,messages=new[] { new { role="system",content=system },new { role="user",content=prompt } } }),Encoding.UTF8,"application/json");
        try
        {
            using var response = await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if (!response.IsSuccessStatusCode) throw new SafeApiException($"API 请求未成功（HTTP {(int)response.StatusCode}）。请检查端点、模型和密钥；服务端详情不会显示。");
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new SafeApiException("API 回复超过大小限制，已停止接收。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var data = new MemoryStream(); byte[] buffer = new byte[8192];
            for (int count; (count=await stream.ReadAsync(buffer,deadline.Token))>0;)
            {
                if (data.Length + count > MaxResponseBytes) throw new SafeApiException("API 回复超过大小限制，已停止接收。");
                data.Write(buffer,0,count);
            }
            using var json = JsonDocument.Parse(data.ToArray());
            string? answer = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(answer)) throw new SafeApiException("API 未返回可显示的文本。");
            // Never render a server echo of the bearer secret in the application.
            return string.IsNullOrEmpty(key) ? answer : answer.Replace(key,"[已隐藏密钥]",StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("API 请求超时（60 秒），已取消。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接 API。请检查网络和端点；未显示底层错误详情。"); }
        catch (SafeApiException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException or FormatException or ArgumentException)
        { throw new InvalidOperationException("API 回复不符合兼容 Chat Completions 文本格式。"); }
    }
    private sealed class SafeApiException(string message):InvalidOperationException(message);
    public void Dispose()=>_http.Dispose();
}
