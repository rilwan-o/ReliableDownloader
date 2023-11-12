using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ReliableDownloader;

internal class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                // Register your services here
                services.AddTransient<IWebSystemCalls, WebSystemCalls>();
                services.AddTransient<IFileDownloader, FileDownloader>();
                services.AddLogging(loggingBuilder =>
                {
                    //loggingBuilder.ClearProviders(); 
                    loggingBuilder.SetMinimumLevel(LogLevel.Trace);
                    loggingBuilder.AddNLog();
                });
            })
            .Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("Downloader App started ...");

        var fileDownloader = host.Services.GetRequiredService<IFileDownloader>();

        // If this url 404's, you can get a live one from https://installer.demo.accurx.com/chain/latest.json.
        //"https://installer.demo.accurx.com/chain/4.22.50587.0/accuRx.Installer.Local.msi";
        var exampleUrl = "https://installer.demo.accurx.com/chain/latest.json";
        var exampleFilePath = Path.Combine(Directory.GetCurrentDirectory(), "downloads/myfirstdownload.msi");
        var didDownloadSuccessfully = await fileDownloader.TryDownloadFile(
            exampleUrl,
            exampleFilePath,
            progress => Console.WriteLine($"Percent progress is {progress.ProgressPercent}"),
            CancellationToken.None);
        Console.WriteLine($"File download ended! Success: {didDownloadSuccessfully}");
    }
}