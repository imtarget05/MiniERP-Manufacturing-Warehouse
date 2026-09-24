using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MiniERP.Api.Models;
using MiniERP.Api.Services;
using Xunit;

namespace MiniERP.Api.Tests;

public class HelpdeskIntegrationTests
{
    [Theory]
    [InlineData("Safe error message", "Safe error message")]
    [InlineData("Database error with password=Secret123 occurred", "Database error with [REDACTED] occurred")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9 token expired", "[REDACTED] expired")]
    [InlineData("Oracle connection user id=erp_user;password=secret; failed", "[REDACTED];[REDACTED]; failed")]
    [InlineData("Failed during select * from app_user", "Failed during [REDACTED]app_user")]
    public void Sanitize_RedactsSensitiveData(string input, string expectedSubstring)
    {
        var sanitized = HelpdeskSecurity.Sanitize(input);
        Assert.DoesNotContain("Secret123", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJhbGci", sanitized);
        Assert.Contains(expectedSubstring.Split('[')[0].Trim(), sanitized);
    }

    [Fact]
    public void HelpdeskOptions_ParsesAndClampsConfiguration()
    {
        var dict = new Dictionary<string, string?>
        {
            ["Helpdesk:Enabled"] = "true",
            ["Helpdesk:BaseUrl"] = "https://helpdesk.corp.internal/",
            ["Helpdesk:IntegrationKey"] = "sec-key-12345",
            ["Helpdesk:TimeoutSeconds"] = "120", // should clamp to 60
            ["Helpdesk:MaxAttempts"] = "10",     // should clamp to 5
            ["Helpdesk:RetryDelayMs"] = "100",
            ["Helpdesk:IncidentPath"] = "/api/v1/incidents"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var options = HelpdeskOptions.FromConfiguration(config);

        Assert.True(options.Enabled);
        Assert.Equal("https://helpdesk.corp.internal", options.BaseUrl);
        Assert.Equal("sec-key-12345", options.IntegrationKey);
        Assert.Equal(60, options.TimeoutSeconds);
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(100, options.RetryDelayMs);
        Assert.Equal("/api/v1/incidents", options.IncidentPath);
    }

    [Fact]
    public void HelpdeskOptions_EnvironmentOverridesFileConfiguration()
    {
        const string variable = "HELPDESK_INTEGRATION_ENABLED";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "true");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Helpdesk:Enabled"] = "false"
            }).Build();
            Assert.True(HelpdeskOptions.FromConfiguration(config).Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task SendIncident_WhenDisabled_ReturnsNotEnabledWithoutHttpCall()
    {
        var options = new HelpdeskOptions { Enabled = false };
        var fakeDb = new FakeErpDbService();
        var handler = new TrackingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var service = new HelpdeskIntegrationService(httpClient, options, fakeDb,
            NullLogger<HelpdeskIntegrationService>.Instance);

        var request = SampleRequest("ERP-INC-DISABLED-01");
        var result = await service.SendIncidentAsync(request, "admin");

        Assert.Equal("NOT_ENABLED", result.Status);
        Assert.False(result.Sent);
        Assert.True(result.Duplicate);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("ERP-INC-DISABLED-01", fakeDb.RecordedDeliveries.Keys);
    }

    [Fact]
    public async Task SendIncident_WhenSuccess_SendsKeyAndIdempotencyHeaders()
    {
        var options = new HelpdeskOptions
        {
            Enabled = true,
            BaseUrl = "https://helpdesk.local",
            IntegrationKey = "test-integration-key",
            IncidentPath = "/api/incidents",
            MaxAttempts = 1
        };
        var fakeDb = new FakeErpDbService();
        var handler = new TrackingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"incidentId\":\"HD-1001\"}", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var service = new HelpdeskIntegrationService(httpClient, options, fakeDb,
            NullLogger<HelpdeskIntegrationService>.Instance);

        var request = SampleRequest("ERP-INC-SUCCESS-01");
        var result = await service.SendIncidentAsync(request, "admin");

        Assert.Equal("SENT", result.Status);
        Assert.True(result.Sent);
        Assert.False(result.Duplicate);
        Assert.Equal(1, handler.CallCount);

        var sentRequest = handler.LastRequest;
        Assert.NotNull(sentRequest);
        Assert.Equal("test-integration-key", sentRequest!.Headers.GetValues("X-Integration-Key").First());
        Assert.Equal("ERP-INC-SUCCESS-01", sentRequest.Headers.GetValues("Idempotency-Key").First());
    }

    [Fact]
    public async Task SendIncident_Replay_ReturnsSentWithoutReinvokingHttp()
    {
        var options = new HelpdeskOptions
        {
            Enabled = true,
            BaseUrl = "https://helpdesk.local",
            IntegrationKey = "test-integration-key",
            MaxAttempts = 1
        };
        var fakeDb = new FakeErpDbService();
        // Seed an already delivered record
        var request = SampleRequest("ERP-INC-REPLAY-01");
        var (_, hash) = HelpdeskIntegrationService.FormatPayload(request);
        fakeDb.RecordedDeliveries[request.ExternalRef] = new HelpdeskDeliveryRecord(
            request.ExternalRef, hash, "SENT", 1, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);

        var handler = new TrackingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var service = new HelpdeskIntegrationService(httpClient, options, fakeDb,
            NullLogger<HelpdeskIntegrationService>.Instance);

        var result = await service.SendIncidentAsync(request, "admin");

        Assert.Equal("SENT", result.Status);
        Assert.True(result.Sent);
        Assert.True(result.Duplicate);
        Assert.Equal(0, handler.CallCount); // Did not make an HTTP call
    }

    [Fact]
    public async Task SendIncident_PayloadConflict_ReturnsConflictWithoutHttpCall()
    {
        var options = new HelpdeskOptions
        {
            Enabled = true,
            BaseUrl = "https://helpdesk.local",
            IntegrationKey = "test-integration-key",
            MaxAttempts = 1
        };
        var fakeDb = new FakeErpDbService();
        // Seed record with different hash
        fakeDb.RecordedDeliveries["ERP-INC-CONFLICT-01"] = new HelpdeskDeliveryRecord(
            "ERP-INC-CONFLICT-01", "old-different-hash-value", "SENT", 1, null,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);

        var handler = new TrackingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var service = new HelpdeskIntegrationService(httpClient, options, fakeDb,
            NullLogger<HelpdeskIntegrationService>.Instance);

        var request = SampleRequest("ERP-INC-CONFLICT-01");
        var result = await service.SendIncidentAsync(request, "admin");

        Assert.Equal("CONFLICT", result.Status);
        Assert.False(result.Sent);
        Assert.Equal("IDEMPOTENCY_CONFLICT", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SendIncident_WhenHelpdeskOffline_IsFailSoftAndPreservesState()
    {
        var options = new HelpdeskOptions
        {
            Enabled = true,
            BaseUrl = "https://unreachable-helpdesk.local",
            IntegrationKey = "test-integration-key",
            MaxAttempts = 2,
            RetryDelayMs = 10
        };
        var fakeDb = new FakeErpDbService();
        var handler = new FailingHandler(new HttpRequestException("Connection refused"));
        using var httpClient = new HttpClient(handler);
        var service = new HelpdeskIntegrationService(httpClient, options, fakeDb,
            NullLogger<HelpdeskIntegrationService>.Instance);

        var request = SampleRequest("ERP-INC-FAILSOFT-01");
        // Must NOT throw exception; fail-soft requirement
        var result = await service.SendIncidentAsync(request, "admin");

        Assert.Equal("FAILED", result.Status);
        Assert.False(result.Sent);
        Assert.Equal("DELIVERY_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(2, handler.CallCount); // Retried max attempts
        Assert.True(fakeDb.RecordedDeliveries.ContainsKey("ERP-INC-FAILSOFT-01"));
        Assert.Equal("FAILED", fakeDb.RecordedDeliveries["ERP-INC-FAILSOFT-01"].Status);
    }

    private static HelpdeskIncidentRequest SampleRequest(string externalRef) =>
        new(externalRef, "ERP", "Warehouse/Manufacturing", "HIGH",
            "Production completion failed",
            "Batch PO001 material issue validation failure",
            "PRODUCTION_ORDER", "PO001", "corr-test-12345", DateTimeOffset.UtcNow);

    private sealed class TrackingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public TrackingHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public int CallCount { get; private set; }

        public FailingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw _exception;
        }
    }

    private sealed class FakeErpDbService : ErpDbService
    {
        public readonly Dictionary<string, HelpdeskDeliveryRecord> RecordedDeliveries = new(StringComparer.OrdinalIgnoreCase);

        public FakeErpDbService() : base(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OracleDb"] = "User Id=fake;Password=fake;Data Source=localhost:1521/FREEPDB1;"
            }).Build(),
            NullLogger<ErpDbService>.Instance,
            new HttpContextAccessor())
        {
        }

        public override Task<HelpdeskDeliveryRecord?> GetHelpdeskDeliveryAsync(string externalRef)
        {
            RecordedDeliveries.TryGetValue(externalRef, out var record);
            return Task.FromResult(record);
        }

        public override Task RecordHelpdeskDeliveryAsync(
            string externalRef,
            HelpdeskIncidentRequest request,
            string status,
            int attempts,
            string? error,
            string actor,
            string? correlationId,
            string? requestHash = null)
        {
            var hash = requestHash ?? "computed-hash";
            RecordedDeliveries[externalRef] = new HelpdeskDeliveryRecord(
                externalRef, hash, status, attempts, error, DateTime.UtcNow, DateTime.UtcNow,
                status == "SENT" ? DateTime.UtcNow : null);
            return Task.CompletedTask;
        }
    }
}
