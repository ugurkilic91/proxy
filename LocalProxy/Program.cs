
using LocalProxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// 1. Serilog Yapılandırmasını Başlat
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        path: "logs/proxy-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        buffered: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1),
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Proxy Konsol Uygulaması başlatılıyor...");

    var builder = Host.CreateApplicationBuilder(args);

    // Microsoft Logging altyapısını Serilog'a yönlendir
    builder.Services.AddSerilog();

    // Appsettings yapılandırması
    builder.Services.Configure<ProxyConfig>(builder.Configuration.GetSection("ProxyConfig"));

    // Proxy Sunucu Arka Plan Servisi
    builder.Services.AddHostedService<ProxyServer>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Uygulama beklenmeyen bir hata nedeniyle durduruldu!");
}
finally
{
    // Uygulama kapanırken tamponlanmış (buffered) tüm logların dosyaya yazılmasını sağla
    await Log.CloseAndFlushAsync();
}