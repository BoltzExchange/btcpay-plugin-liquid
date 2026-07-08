#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Models;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Reporting;
using BTCPayServer.Tests;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Boltz.Tests
{
    [Trait("Integration", "Integration")]
    [Trait("Lightning", "Lightning")]
    public class BoltzInvoiceFlowTests : BoltzTestBase
    {
        public BoltzInvoiceFlowTests(ITestOutputHelper helper) : base(helper)
        {
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        public async Task CanCreateAndPayInvoiceWithBoltz()
        {
            using var tester = CreateServerTesterWithBoltz();
            var (client, storeId, invoiceId, lightningPaymentMethodId) = await CreateAndPayInvoiceWithBoltz(tester);
            await AssertInvoiceSettled(client, storeId, invoiceId, lightningPaymentMethodId);
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        public async Task GetsBoltzSettlementDataForSettledLightningPayment()
        {
            using var tester = CreateServerTesterWithBoltz();
            var (_, storeId, invoiceId, paymentMethodId) = await CreateAndPayInvoiceWithBoltz(tester);
            await AssertBoltzSettlementData(tester, storeId, invoiceId, paymentMethodId);
        }

        [Fact(Timeout = TestUtils.TestTimeout)]
        public async Task IncludesBoltzSettlementDataForSettledLnurlPaymentInPaymentsReport()
        {
            using var tester = CreateServerTesterWithBoltz();
            var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC").ToString();
            var (_, storeId, invoiceId, paymentMethodId) =
                await CreateAndPayInvoiceWithBoltz(tester, lnurlPaymentMethodId);

            await AssertBoltzSettlementData(tester, storeId, invoiceId, paymentMethodId);
            await AssertPaymentsReportContainsBoltzData(tester, storeId, invoiceId, paymentMethodId);
        }

        private static async Task<BoltzSettlementData> AssertBoltzSettlementData(
            ServerTester tester,
            string storeId,
            string invoiceId,
            string paymentMethodId)
        {
            var boltzService = await tester.GetBoltzService();
            var handlers = tester.PayTester.GetService<PaymentMethodHandlerDictionary>();
            var parsedPaymentMethodId = PaymentMethodId.Parse(paymentMethodId);
            BoltzSettlementData? settlementData = null;

            await TestUtils.EventuallyAsync(async () =>
            {
                var invoice = await tester.PayTester.InvoiceRepository.GetInvoice(invoiceId);
                var payment = Assert.Single(
                    invoice.GetPayments(false),
                    p => p.PaymentMethodId == parsedPaymentMethodId && p.Status == PaymentStatus.Settled);

                Assert.True(handlers.TryGetValue(payment.PaymentMethodId, out var handler));
                var prompt = invoice.GetPaymentPrompt(payment.PaymentMethodId);
                Assert.NotNull(prompt);
                var promptDetails = Assert.IsAssignableFrom<LigthningPaymentPromptDetails>(
                    handler.ParsePaymentPromptDetails(prompt!.Details));

                settlementData = await boltzService.GetBoltzSettlementData(storeId, payment, invoice);
                Assert.NotNull(settlementData);
                Assert.Equal(promptDetails.InvoiceId, settlementData!.SwapId);
                Assert.Equal("LBTC", settlementData.SettlementCurrency);
                Assert.False(string.IsNullOrWhiteSpace(settlementData.SettlementTransactionId));
            });

            return settlementData!;
        }

        private static async Task<(BTCPayServerClient Client, string StoreId, string InvoiceId, string LightningPaymentMethodId)>
            CreateAndPayInvoiceWithBoltz(ServerTester tester, string? paymentMethodId = null)
        {
            paymentMethodId ??= PaymentTypes.LN.GetPaymentMethodId("BTC").ToString();
            var account = await tester.CreateTestStore();
            await tester.SetupBoltzForStore(account.StoreId);
            if (paymentMethodId == PaymentTypes.LNURL.GetPaymentMethodId("BTC").ToString())
            {
                await EnableLnurlForStore(account);
            }

            var client = await account.CreateClient();
            var invoiceId = (await client.CreateInvoice(account.StoreId, new CreateInvoiceRequest
            {
                Amount = 5m,
                Currency = "USD"
            })).Id;

            var lightningMethod = await WaitForLightningMethod(client, invoiceId, paymentMethodId);
            var bolt11 = await GetBolt11(tester, lightningMethod, paymentMethodId);
            await tester.PayWithBoltzRegtestLnd(bolt11);

            return (client, account.StoreId, invoiceId, paymentMethodId);
        }

        private static async Task EnableLnurlForStore(TestAccount account)
        {
            var client = await account.CreateClient();
            await client.UpdateStorePaymentMethod(account.StoreId,
                PaymentTypes.LNURL.GetPaymentMethodId("BTC").ToString(),
                new UpdatePaymentMethodRequest
                {
                    Enabled = true,
                    Config = new JObject
                    {
                        ["useBech32Scheme"] = true,
                        ["lud12Enabled"] = false
                    }
                });
        }

        private static async Task<InvoicePaymentMethodDataModel> WaitForLightningMethod(
            BTCPayServerClient client,
            string invoiceId,
            string lightningPaymentMethodId)
        {
            InvoicePaymentMethodDataModel? lightningMethod = null;
            await TestUtils.EventuallyAsync(async () =>
            {
                var methods = await client.GetInvoicePaymentMethods(invoiceId);
                lightningMethod = Assert.Single(methods, m => m.PaymentMethodId == lightningPaymentMethodId);
                Assert.True(lightningMethod.Activated);
                if (lightningPaymentMethodId == PaymentTypes.LNURL.GetPaymentMethodId("BTC").ToString())
                {
                    Assert.False(string.IsNullOrWhiteSpace(lightningMethod.PaymentLink));
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(lightningMethod.Destination));
                }
            });
            return lightningMethod!;
        }

        private static async Task<string> GetBolt11(
            ServerTester tester,
            InvoicePaymentMethodDataModel paymentMethod,
            string paymentMethodId)
        {
            if (paymentMethodId == PaymentTypes.LN.GetPaymentMethodId("BTC").ToString())
            {
                Assert.False(string.IsNullOrWhiteSpace(paymentMethod.Destination));
                return paymentMethod.Destination!;
            }

            if (paymentMethodId == PaymentTypes.LNURL.GetPaymentMethodId("BTC").ToString())
            {
                using var httpClient = new HttpClient();
                Assert.False(string.IsNullOrWhiteSpace(paymentMethod.PaymentLink));
                var lnurl = LNURL.LNURL.Parse(paymentMethod.PaymentLink!, out var tag);
                var payRequest =
                    Assert.IsType<LNURL.LNURLPayRequest>(await LNURL.LNURL.FetchInformation(lnurl, tag, httpClient));
                var btcNetwork = tester.NetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
                Assert.NotNull(btcNetwork);
                var response = await payRequest.SendRequest(payRequest.MinSendable, btcNetwork.NBitcoinNetwork, httpClient);
                Assert.False(string.IsNullOrWhiteSpace(response.Pr));
                return response.Pr;
            }

            throw new ArgumentOutOfRangeException(nameof(paymentMethodId), paymentMethodId, "Unsupported payment method");
        }

        private static async Task AssertPaymentsReportContainsBoltzData(
            ServerTester tester,
            string storeId,
            string invoiceId,
            string paymentMethodId)
        {
            await TestUtils.EventuallyAsync(async () =>
            {
                var reportService = tester.PayTester.GetService<ReportService>();
                var report = Assert.IsType<BoltzPaymentsReportProvider>(reportService.ReportProviders["Payments"]);
                var queryContext = new QueryContext(
                    storeId,
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(1));

                await report.Query(queryContext, CancellationToken.None);
                var invoiceIdIndex = GetFieldIndex(queryContext, "InvoiceId");
                var paymentMethodIdIndex = GetFieldIndex(queryContext, "PaymentMethodId");
                var categoryIndex = GetFieldIndex(queryContext, "Category");
                var swapIdIndex = GetFieldIndex(queryContext, "BoltzSwapId");
                var settlementCurrencyIndex = GetFieldIndex(queryContext, "SettlementCurrency");
                var settlementTransactionIdIndex = GetFieldIndex(queryContext, "SettlementTransactionId");

                var row = Assert.Single(queryContext.Data,
                    data => data[invoiceIdIndex]?.ToString() == invoiceId &&
                            data[paymentMethodIdIndex]?.ToString() == paymentMethodId);

                Assert.Equal("Lightning via Boltz", row[categoryIndex]);
                Assert.False(string.IsNullOrWhiteSpace(row[swapIdIndex]?.ToString()));
                Assert.Equal("LBTC", row[settlementCurrencyIndex]);
                Assert.False(string.IsNullOrWhiteSpace(row[settlementTransactionIdIndex]?.ToString()));
            });
        }

        private static int GetFieldIndex(QueryContext queryContext, string fieldName)
        {
            Assert.NotNull(queryContext.ViewDefinition);
            for (var i = 0; i < queryContext.ViewDefinition!.Fields.Count; i++)
            {
                if (queryContext.ViewDefinition.Fields[i].Name == fieldName)
                {
                    return i;
                }
            }

            throw new InvalidOperationException($"Report field {fieldName} was not found");
        }

        private static async Task AssertInvoiceSettled(
            BTCPayServerClient client,
            string storeId,
            string invoiceId,
            string lightningPaymentMethodId)
        {
            await TestUtils.EventuallyAsync(async () =>
            {
                var paidInvoice = await client.GetInvoice(invoiceId);
                Assert.Equal(InvoiceStatus.Settled, paidInvoice.Status);
                Assert.Equal(InvoiceExceptionStatus.None, paidInvoice.AdditionalStatus);

                var methods = await client.GetInvoicePaymentMethods(invoiceId);
                var paidLightningMethod = Assert.Single(methods, m => m.PaymentMethodId == lightningPaymentMethodId);
                Assert.Contains(
                    paidLightningMethod.Payments,
                    payment => payment.Status == InvoicePaymentMethodDataModel.Payment.PaymentStatus.Settled);
            });
        }
    }
}
