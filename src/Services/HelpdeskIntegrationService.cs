using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniERP.Api.Models;

namespace MiniERP.Api.Services;

public sealed class HelpdeskIntegrationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly HelpdeskOptions _options;
    private readonly ErpDbService _db;
    private readonly ILogger<HelpdeskIntegrationService> _logger;

    public HelpdeskIntegrationService(HttpClient http, HelpdeskOptions options,
        ErpDbService db, ILogger<HelpdeskIntegrationService> logger)
    {
        _http = http;
        _options = options;
        _db = db;
        _logger = logger;
    }

    public async Task<HelpdeskDeliveryResult> SendIncidentAsync(
        HelpdeskIncidentRequest request, string actor)
    {
        var (payload, hash) = FormatPayload(request);
        if (!_options.Enabled)
        {
            await _db.RecordHelpdeskDeliveryAsync(request.ExternalRef, request, "NOT_ENABLED", 0,
                null, actor, request.CorrelationId, hash);
            return new HelpdeskDeliveryResult(request.ExternalRef, "NOT_ENABLED", false, true, 0,
                null, "Helpdesk integration is disabled.");
        }
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            string.IsNullOrWhiteSpace(_options.IntegrationKey))
        {
            await _db.RecordHelpdeskDeliveryAsync(request.ExternalRef, request, "FAILED", 0,
                "HELPDESK_CONFIG_INVALID", actor, request.CorrelationId, hash);
            return new HelpdeskDeliveryResult(request.ExternalRef, "FAILED", false, false, 0,
                "HELPDESK_CONFIG_INVALID", "Helpdesk URL/key is not configured.");
        }

        var previous = await GetPreviousDeliverySafelyAsync(request.ExternalRef);
        if (previous is not null && !string.Equals(previous.RequestHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            await _db.RecordHelpdeskDeliveryAsync(request.ExternalRef, request, "CONFLICT", 0,
                "IDEMPOTENCY_CONFLICT", actor, request.CorrelationId, hash);
            return new HelpdeskDeliveryResult(request.ExternalRef, "CONFLICT", false, false, 0,
                "IDEMPOTENCY_CONFLICT", "External reference was reused with a different payload.");
        }

        if (previous is not null && previous.Status == "SENT")
            return new HelpdeskDeliveryResult(request.ExternalRef, "SENT", true, true,
                (int)previous.AttemptCount, null, "Already delivered.");

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post,
                    new Uri(baseUri, _options.IncidentPath.TrimStart('/')));
                message.Headers.Add("X-Integration-Key", _options.IntegrationKey);
                message.Headers.Add("Idempotency-Key", request.ExternalRef);
                message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(message);
                if (response.IsSuccessStatusCode)
                {
                    await _db.RecordHelpdeskDeliveryAsync(request.ExternalRef, request, "SENT", attempt,
                        null, actor, request.CorrelationId, hash);
                    return new HelpdeskDeliveryResult(request.ExternalRef, "SENT", true, false,
                        attempt, null, "Helpdesk accepted the incident.");
                }
                if ((int)response.StatusCode is >= 400 and < 500 and not 408 and not 429)
                {
                    var error = $"HTTP_{(int)response.StatusCode}";
                    await _db.RecordHelpdeskDeliveryAsync(request.ExternalRef, request, "FAILED", attempt,
                        error, actor, request.CorrelationId, hash);
                    return new HelpdeskDeliveryResult(request.ExternalRef, "FAILED", false, false,
                        attempt, error, "Helpdesk rejected the incident.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Helpdesk delivery attempt {Attempt} failed for {ExternalRef}",
                    attempt, request.ExternalRef);
            }
            if (attempt < _options.MaxAttempts && _options.RetryDelayMs > 0)
                await Task.Delay(_options.RetryDelayMs * attempt);
        }

        await _db.RecordHelpdeskDeliveryAsync(request.ExternalRef, request, "FAILED", _options.MaxAttempts,
            "DELIVERY_UNAVAILABLE", actor, request.CorrelationId, hash);
        return new HelpdeskDeliveryResult(request.ExternalRef, "FAILED", false, false,
            _options.MaxAttempts, "DELIVERY_UNAVAILABLE", "Helpdesk unavailable; ERP result is unchanged.");
    }

    private async Task<HelpdeskDeliveryRecord?> GetPreviousDeliverySafelyAsync(string externalRef)
    {
        try
        {
            return await _db.GetHelpdeskDeliveryAsync(externalRef);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Helpdesk delivery lookup failed for {ExternalRef}: {Error}",
                externalRef, HelpdeskSecurity.SafeError(ex));
            return null;
        }
    }

    public static (string Payload, string Hash) FormatPayload(HelpdeskIncidentRequest request)
    {
        var payload = JsonSerializer.Serialize(new
        {
            source = "MiniERP",
            externalRef = request.ExternalRef,
            category = request.Category,
            service = request.Service,
            severity = request.Severity,
            title = HelpdeskSecurity.Sanitize(request.Title, 200),
            description = HelpdeskSecurity.Sanitize(request.Description),
            referenceType = request.ReferenceType,
            referenceNo = request.ReferenceNo,
            correlationId = request.CorrelationId,
            occurredAt = request.OccurredAt
        }, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return (payload, hash);
    }
}
