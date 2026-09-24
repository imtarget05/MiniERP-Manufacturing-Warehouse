namespace MiniERP.Api.Models;

// ------------------------------------------------------------- helpdesk

public record HelpdeskIncidentRequest(
    string ExternalRef,
    string Category,
    string Service,
    string Severity,
    string Title,
    string Description,
    string? ReferenceType,
    string? ReferenceNo,
    string CorrelationId,
    DateTimeOffset OccurredAt
);

public record HelpdeskDeliveryResult(
    string ExternalRef,
    string Status,
    bool Sent,
    bool Duplicate,
    int Attempts,
    string? ErrorCode,
    string? Message
);

public record HelpdeskDeliveryDto(
    string ExternalRef,
    string Status,
    int AttemptCount,
    string? LastError,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SentAt
);
