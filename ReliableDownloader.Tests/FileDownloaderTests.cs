using Microsoft.Extensions.Logging;
using Moq;
using System.Net;

namespace ReliableDownloader.Tests
{
    public class FileDownloaderTests
    {
        private readonly FileDownloader _fileDownloader;
        private readonly Mock<IWebSystemCalls> _webSystemCalls;
        private readonly Mock<ILogger<FileDownloader>> _logger;

        public FileDownloaderTests()
        {
            _webSystemCalls = new Mock<IWebSystemCalls>();
            _logger = new Mock<ILogger<FileDownloader>>();
            _fileDownloader = new FileDownloader(_webSystemCalls.Object, _logger.Object);
        }

        [Fact]
        public async Task DownloadInstaller_WithInternetDisconnections_DoesNotExit()
        {
            // Arrange
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StreamContent(new MemoryStream(new byte[] { 1, 2, 3 }));

            // Mock the necessary dependencies.
            _webSystemCalls.Setup(w => w.GetHeadersAsync("testUrl", CancellationToken.None))
                .ReturnsAsync(response);
            _webSystemCalls.Setup(w => w.DownloadContentAsync("testUrl", CancellationToken.None))
                .ReturnsAsync(response);

            // Act
            var result = await _fileDownloader.TryDownloadFile("testUrl", "testPath", progress => { }, CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task DownloadInstaller_WithPartialDownloading_PartialDownload()
        {
            // Arrange
            var response = new HttpResponseMessage(HttpStatusCode.OK);

            // Simulate a response that supports range requests (partial content)
            response.Headers.Add("Accept-Ranges", "bytes");
            // Set the response content to simulate a partial download
            response.Content = new StreamContent(new MemoryStream(new byte[] { 1, 2, 3 }));

            _webSystemCalls.Setup(w => w.GetHeadersAsync("testUrl", CancellationToken.None))
                .ReturnsAsync(response);
            _webSystemCalls.Setup(w => w.DownloadPartialContentAsync("testUrl", 0, 2, CancellationToken.None))
                .ReturnsAsync(response);

            // Act
            var result = await _fileDownloader.TryDownloadFile("testUrl", "testPath", progress => { }, CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task DownloadInstaller_WithoutPartialDownloading_DownloadInOneGo()
        {
            // Arrange
            var response = new HttpResponseMessage(HttpStatusCode.OK);

            // Simulate a response that does not support range requests (download in one go)
            response.Headers.Remove("Accept-Ranges");

            // Set the response content to simulate a full download
            response.Content = new StreamContent(new MemoryStream(new byte[] { 1, 2, 3 }));

            _webSystemCalls.Setup(w => w.GetHeadersAsync("testUrl", CancellationToken.None))
                .ReturnsAsync(response);
            _webSystemCalls.Setup(w => w.DownloadContentAsync("testUrl", CancellationToken.None))
                .ReturnsAsync(response);

            // Act
            var result = await _fileDownloader.TryDownloadFile("testUrl", "testPath", progress => { }, CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task DownloadInstaller_RecoverFromFailures_UntilSuccessfulDownload()
        {
            // Arrange
            var webSystemCallsMock = new Mock<IWebSystemCalls>();
            var response = new HttpResponseMessage();

            // Simulate a failure on the first call and success on the second call
            bool firstCall = true;
            webSystemCallsMock.Setup(w => w.GetHeadersAsync("testUrl", CancellationToken.None))
                .ReturnsAsync(() =>
                {
                    if (firstCall)
                    {
                        firstCall = false;
                        throw new HttpRequestException("Simulated failure");
                    }
                    // return new HttpResponseMessage(HttpStatusCode.OK);
                    response = new HttpResponseMessage(HttpStatusCode.OK);
                    return response;
                });

            // Simulate a response that does not support range requests (download in one go)
            response.Headers.Remove("Accept-Ranges");

            // Set the response content to simulate a full download
            response.Content = new StreamContent(new MemoryStream(new byte[] { 1, 2, 3 }));

            _webSystemCalls.Setup(w => w.GetHeadersAsync("testUrl", CancellationToken.None))
                .ReturnsAsync(response);
            _webSystemCalls.Setup(w => w.DownloadContentAsync("testUrl", CancellationToken.None))
                .ReturnsAsync(response);

            // Act
            var result = await _fileDownloader.TryDownloadFile("testUrl", "testPath", progress => { }, CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task DownloadInstaller_CheckIntegrityAndDeleteFileIfFails()
        {
            var testPath = Path.Combine(Directory.GetCurrentDirectory(), "myfirstdownload.msi");
            // Arrange
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Remove("Accept-Ranges");

            var content = new byte[] { 1, 2, 3 };
            response.Content = new StreamContent(new MemoryStream(content));
            // Set up incorrect Content-MD5 for integrity check failure
            var incorrectMd5 = new byte[] { 4, 5, 6 };
            response.Content.Headers.ContentMD5 = incorrectMd5;
            _webSystemCalls.Setup(w => w.GetHeadersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
            _webSystemCalls.Setup(w => w.DownloadContentAsync("testUrl", CancellationToken.None))
             .ReturnsAsync(response);


            // Act
            var result = await _fileDownloader.TryDownloadFile("testUrl", testPath, progress => { }, CancellationToken.None);

            // Assert
            Assert.False(result);
            // Assert that the file is deleted
            Assert.False(File.Exists(testPath));
        }

        [Fact]
        public async Task DownloadInstaller_ReportProgressToUser()
        {
            // Arrange
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StreamContent(new MemoryStream(new byte[] { 1, 2, 3 }));

            _webSystemCalls.Setup(w => w.GetHeadersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(response);
            _webSystemCalls.Setup(w => w.DownloadContentAsync("testUrl", CancellationToken.None))
             .ReturnsAsync(response);
            var progressReported = false;
            Action<FileProgress> onProgressChanged = progress =>
            {
                progressReported = true;
            };

            // Act
            await _fileDownloader.TryDownloadFile("testUrl", "testPath", onProgressChanged, CancellationToken.None);

            // Assert
            Assert.True(progressReported);
        }

        [Fact]
        public async Task DownloadInstaller_AbilityToCancelDownload()
        {
            // Arrange
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            var content = new byte[] { 1, 2, 3 };
            response.Content = new StreamContent(new MemoryStream(content));

            _webSystemCalls.Setup(w => w.GetHeadersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(response);
            _webSystemCalls.Setup(w => w.DownloadContentAsync("testUrl", CancellationToken.None))
             .ReturnsAsync(response);
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            await Task.Run(async () =>
            {
                await Task.Delay(1000); // Delay to simulate user action
                cancellationTokenSource.Cancel();
            });

            // Act
            var result = await _fileDownloader.TryDownloadFile("testUrl", "testPath", progress => { }, cancellationToken);

            // Assert
            Assert.False(result);
        }
    }
}
