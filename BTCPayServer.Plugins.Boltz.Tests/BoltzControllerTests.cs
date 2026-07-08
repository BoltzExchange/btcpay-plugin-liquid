using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Boltz;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Integration", "Integration")]
    public class BoltzControllerTests : BoltzTestBase
    {
        public BoltzControllerTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact]
        public async Task CanAccessStatusPage()
        {
            using var serverTester = CreateServerTesterWithBoltz();
            var account = await serverTester.CreateTestStore();
            await serverTester.SetupBoltzForStore(account.StoreId);
            var boltzService = await serverTester.GetBoltzService();
            Assert.NotNull(boltzService.GetSettings(account.StoreId));

            var controller = serverTester.PayTester.GetController<BoltzController>(account.UserId, account.StoreId, account.IsAdmin);
            Assert.NotNull(controller);
            Assert.NotNull(controller.Settings);
            var result = await controller.Status(account.StoreId);

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<BoltzInfo>(viewResult.Model);
            Assert.NotNull(model.Info);
            Assert.NotNull(model.Stats);
            Assert.NotNull(model.StandaloneWallet);
        }

        [Fact]
        public void AdminShowsNewestLogFilesFirst()
        {
            var logDirectory = Directory.CreateTempSubdirectory("boltz-log-order-");
            try
            {
                var oldest = WriteLogFile(logDirectory, "boltz.log", DateTimeOffset.UtcNow.AddMinutes(-3));
                var newest = WriteLogFile(logDirectory, "boltz-2.log", DateTimeOffset.UtcNow.AddMinutes(-1));
                var middle = WriteLogFile(logDirectory, "boltz-1.log", DateTimeOffset.UtcNow.AddMinutes(-2));

                var logFiles = BoltzController.GetLogFilesPage(logDirectory.GetFiles("boltz*.log"), 0);

                Assert.Equal(new[] { newest.Name, middle.Name, oldest.Name }, logFiles.Select(file => file.Name));
            }
            finally
            {
                logDirectory.Delete(true);
            }
        }

        private static FileInfo WriteLogFile(DirectoryInfo directory, string fileName, DateTimeOffset lastWriteTime)
        {
            var file = new FileInfo(Path.Combine(directory.FullName, fileName));
            File.WriteAllText(file.FullName, fileName);
            file.LastWriteTimeUtc = lastWriteTime.UtcDateTime;
            file.Refresh();
            return file;
        }
    }
}
