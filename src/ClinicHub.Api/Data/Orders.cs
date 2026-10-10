using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// orders schema: work sent to LabFlow and ClinicReport, results coming back, and the message queues.

public static class TargetSystems
{
    public const string LabFlow = "LabFlow";
    public const string ClinicReport = "ClinicReport";
}

public static class ServiceRequestStatus
{
    public const string Draft = "Draft";
    public const string Sent = "Sent";
    public const string Accepted = "Accepted";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Failed = "Failed";
}

/// <summary>An order: a lab test for LabFlow, or an ECG / echo for ClinicReport (FHIR ServiceRequest).</summary>
public class ServiceRequest
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int RequesterProviderId { get; set; }
    public Provider RequesterProvider { get; set; } = null!;
    [MaxLength(20)] public string TargetSystem { get; set; } = TargetSystems.LabFlow;

    /// <summary>LOINC code for lab orders, ClinicReport template key (e.g. "echo-tte") for reports.</summary>
    [MaxLength(40)] public string Code { get; set; } = "";
    [MaxLength(200)] public string Display { get; set; } = "";
    [MaxLength(10)] public string Priority { get; set; } = "Routine";
    [MaxLength(20)] public string Status { get; set; } = ServiceRequestStatus.Draft;
    [MaxLength(500)] public string? ClinicalNotes { get; set; }

    /// <summary>The order's id in the other system once it has accepted it.</summary>
    [MaxLength(64)] public string? ExternalId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public List<DiagnosticResult> Results { get; set; } = [];
}

/// <summary>A result or report that came back for an order (FHIR DiagnosticReport). A clinician must acknowledge it.</summary>
public class DiagnosticResult
{
    public int Id { get; set; }
    public int ServiceRequestId { get; set; }
    public ServiceRequest ServiceRequest { get; set; } = null!;
    public DateTime ReceivedUtc { get; set; }

    /// <summary>Preliminary, Final or Amended.</summary>
    [MaxLength(20)] public string Status { get; set; } = "Final";
    public string? Summary { get; set; }
    public bool IsAbnormal { get; set; }
    [MaxLength(64)] public string? ExternalReportId { get; set; }
    [MaxLength(300)] public string? ExternalUrl { get; set; }
    public int? ReviewedByUserId { get; set; }
    public DateTime? ReviewedUtc { get; set; }
}

/// <summary>A FHIR message written in the same transaction as the change; a worker delivers it.</summary>
public class OutboxMessage
{
    public long Id { get; set; }
    [MaxLength(20)] public string Destination { get; set; } = "";
    [MaxLength(60)] public string Kind { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public int? ServiceRequestId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
}

public class InboundMessage
{
    public long Id { get; set; }
    [MaxLength(20)] public string Source { get; set; } = "";
    [MaxLength(60)] public string Kind { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTime ReceivedUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    [MaxLength(1000)] public string? Error { get; set; }
}
