using Dapper;
using MiniERP.Api.Models;

namespace MiniERP.Api.Services;

public sealed record HelpdeskDeliveryRecord(
    string ExternalRef,
    string RequestHash,
    string Status,
    decimal AttemptCount,
    string? LastError,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SentAt);

public partial class ErpDbService
{
    public virtual async Task<HelpdeskDeliveryRecord?> GetHelpdeskDeliveryAsync(string externalRef)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<HelpdeskDeliveryRecord>(@"
            SELECT EXTERNAL_REF AS ExternalRef, REQUEST_HASH AS RequestHash,
                   STATUS AS Status, ATTEMPT_COUNT AS AttemptCount,
                   LAST_ERROR AS LastError, CREATED_AT AS CreatedAt,
                   UPDATED_AT AS UpdatedAt, SENT_AT AS SentAt
              FROM HELPDESK_DELIVERY WHERE EXTERNAL_REF = :ExternalRef",
            new { ExternalRef = externalRef });
    }

    public virtual async Task RecordHelpdeskDeliveryAsync(
        string externalRef,
        HelpdeskIncidentRequest request,
        string status,
        int attempts,
        string? error,
        string actor,
        string? correlationId,
        string? requestHash = null)
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(
            request, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
        var hash = requestHash ?? Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
        try
        {
            using var conn = CreateConnection();
            const string sql = @"
            MERGE INTO HELPDESK_DELIVERY d
            USING (SELECT :ExternalRef AS ExternalRef FROM dual) s
               ON (d.EXTERNAL_REF = s.ExternalRef)
            WHEN MATCHED THEN UPDATE SET
                STATUS = :Status,
                ATTEMPT_COUNT = :Attempts,
                LAST_ERROR = :Error,
                CORRELATION_ID = :CorrelationId,
                UPDATED_AT = SYSDATE,
                SENT_AT = CASE WHEN :Status = 'SENT' THEN SYSDATE ELSE d.SENT_AT END
            WHEN NOT MATCHED THEN INSERT
                (EXTERNAL_REF, OPERATION, REQUEST_HASH, STATUS, ATTEMPT_COUNT,
                 LAST_ERROR, CORRELATION_ID, CREATED_BY, SENT_AT)
            VALUES (:ExternalRef, 'INCIDENT', :RequestHash, :Status, :Attempts,
                    :Error, :CorrelationId, :Actor,
                    CASE WHEN :Status = 'SENT' THEN SYSDATE ELSE NULL END)";
        await conn.ExecuteAsync(sql, new
        {
            ExternalRef = Limit(externalRef, 80),
            Status = Limit(status, 20),
            Attempts = attempts,
            Error = Limit(error, 1000),
            CorrelationId = Limit(correlationId, 60),
            RequestHash = hash,
            Actor = Limit(actor, 50)
        });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist Helpdesk delivery {ExternalRef}", externalRef);
        }
    }
}
