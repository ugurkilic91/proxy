using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalProxy;

public class ProxyServer : BackgroundService
{
    private readonly ProxyConfig _config;
    private readonly ILogger<ProxyServer> _logger;

    public ProxyServer(IOptions<ProxyConfig> config, ILogger<ProxyServer> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        listenSocket.NoDelay = true;
        listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listenSocket.Bind(new IPEndPoint(IPAddress.Any, _config.Port));
        listenSocket.Listen(_config.BacklogSize);

        _logger.LogInformation("Proxy Sunucusu {Port} portunda dinlemede. Timeout: {Timeout}s", _config.Port, _config.TimeoutSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await listenSocket.AcceptAsync(stoppingToken);
                clientSocket.NoDelay = true;

                _ = Task.Run(() => HandleClientAsync(clientSocket, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Yeni bağlantı kabul edilirkan bir hata oluştu.");
            }
        }
    }

    private async Task HandleClientAsync(Socket clientSocket, CancellationToken cancellationToken)
    {
        string clientIp = clientSocket.RemoteEndPoint?.ToString() ?? "Bilinmiyor";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        using var clientStream = new NetworkStream(clientSocket, ownsSocket: true);

        try
        {
            using var reader = new StreamReader(clientStream, Encoding.ASCII, leaveOpen: true);
            string? requestLine = await reader.ReadLineAsync(cts.Token);

            if (string.IsNullOrEmpty(requestLine)) return;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? headerLine;
            while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync(cts.Token)))
            {
                int separatorIndex = headerLine.IndexOf(':');
                if (separatorIndex > 0)
                {
                    headers[headerLine[..separatorIndex].Trim()] = headerLine[(separatorIndex + 1)..].Trim();
                }
            }

            // Kimlik Doğrulama Kontrolü
            if (!Authenticate(headers, out string username))
            {
                _logger.LogWarning("Başarısız kimlik doğrulama denemesi. IP: {ClientIp}", clientIp);
                byte[] authRequiredResponse = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                    "Proxy-Authenticate: Basic realm=\"DotNetProxy\"\r\n" +
                    "Content-Length: 0\r\n\r\n");
                await clientStream.WriteAsync(authRequiredResponse, cts.Token);
                return;
            }

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;

            string method = parts[0];
            string target = parts[1];

            string host;
            int port;

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                var hostPort = target.Split(':');
                host = hostPort[0];
                port = hostPort.Length > 1 ? int.Parse(hostPort[1]) : 443;
            }
            else
            {
                var uri = new Uri(target);
                host = uri.Host;
                port = uri.Port;
            }

            _logger.LogInformation("Kullanıcı '{User}' ({ClientIp}) -> {Host}:{Port} hedefine bağlanıyor.", username, clientIp, host, port);

            using var targetSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            targetSocket.NoDelay = true;
            await targetSocket.ConnectAsync(host, port, cts.Token);
            using var targetStream = new NetworkStream(targetSocket, ownsSocket: true);

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                byte[] okResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await clientStream.WriteAsync(okResponse, cts.Token);
            }

            // Çift Yönlü Veri Aktarımı
            var clientToTarget = RelayStreamAsync(clientStream, targetStream, cts.Token);
            var targetToClient = RelayStreamAsync(targetStream, clientStream, cts.Token);

            await Task.WhenAll(clientToTarget, targetToClient);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Bağlantı zaman aşımına uğradı (Timeout). IP: {ClientIp}", clientIp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "İstek işlenirken hata oluştu. Client IP: {ClientIp}", clientIp);
        }
    }

    private bool Authenticate(Dictionary<string, string> headers, out string username)
    {
        username = string.Empty;

        if (!headers.TryGetValue("Proxy-Authorization", out var authHeader) ||
            !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string token = authHeader["Basic ".Length..].Trim();
            string credentialString = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var parts = credentialString.Split(':', 2);

            if (parts.Length == 2 && _config.Users.TryGetValue(parts[0], out var password))
            {
                if (password == parts[1])
                {
                    username = parts[0];
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static async Task RelayStreamAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        var writing = FillPipeAsync(source, pipe.Writer, cancellationToken);
        var reading = ReadPipeAsync(destination, pipe.Reader, cancellationToken);

        await Task.WhenAll(writing, reading);
    }

    private static async Task FillPipeAsync(Stream source, PipeWriter writer, CancellationToken cancellationToken)
    {
        const int minimumBufferSize = 8192;

        while (!cancellationToken.IsCancellationRequested)
        {
            Memory<byte> memory = writer.GetMemory(minimumBufferSize);
            int bytesRead = await source.ReadAsync(memory, cancellationToken);
            if (bytesRead == 0) break;

            writer.Advance(bytesRead);
            FlushResult result = await writer.FlushAsync(cancellationToken);

            if (result.IsCompleted || result.IsCanceled) break;
        }

        await writer.CompleteAsync();
    }

    private static async Task ReadPipeAsync(Stream destination, PipeReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length > 0)
            {
                foreach (var segment in buffer)
                {
                    await destination.WriteAsync(segment, cancellationToken);
                }
            }

            reader.AdvanceTo(buffer.End);

            if (result.IsCompleted || result.IsCanceled) break;
        }

        await reader.CompleteAsync();
    }
}