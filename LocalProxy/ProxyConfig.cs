namespace LocalProxy;

public class ProxyConfig
{
    public int Port { get; set; } = 1080;
    public int TimeoutSeconds { get; set; } = 30;
    public int BacklogSize { get; set; } = 2048;
    public Dictionary<string, string> Users { get; set; } = new();
}