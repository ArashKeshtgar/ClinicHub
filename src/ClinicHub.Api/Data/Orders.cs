using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// orders schema: work sent to LabFlow and ClinicReport (or placed with an external lab), results coming back, and the message queues.

public static class TargetSystems
{
    public const string LabFlow = "LabFlow";
    public const string ClinicReport = "ClinicReport";

    /// <summary>
    /// A laboratory outside the group, whose results arrive over HL7 through IntegrationHub. ClinicHub records the order
    /// and accepts the lab's result for it, but sends the lab nothing: there is no outbox message (see <see cref="ServiceRequestStatus.AwaitingResult"/>).
    /// </summary>
    public const string ExternalLab = "ExternalLab";

    /// <summary>The pharmacy: electronic prescriptions go here (not an order target: see <see cref="Prescription"/>).</summary>
    public const string PharmaFlow = "PharmaFlow";
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

    /// <summary>
    /// Recorded for an external lab: ClinicHub sends it nothing, the requisition reaches the lab some other way, and the
    /// order waits here for a result to come back. Not the same as <see cref="Accepted"/>: nobody has acknowledged it.
    /// </summary>
    public const string AwaitingResult = "AwaitingResult";

    /// <summary>Asked the other application to cancel; waiting for its answer.</summary>
    public const string Cancelling = "Cancelling";
}

public static class OutboxKinds
{
    public const string Order = "ServiceRequest";

    /// <summary>The order is withdrawn: the same ServiceRequest with status "revoked".</summary>
    public const string Cancel = "ServiceRequestCancel";

    /// <summary>Electronic prescriptions written together: one Bundle of MedicationRequests.</summary>
    public const string Prescription = "MedicationRequest";

    /// <summary>A prescription is withdrawn: the same MedicationRequest with status "cancelled".</summary>
    public const string PrescriptionCancel = "MedicationRequestCancel";

    /// <summary>Two records were merged: the merged MRN is replaced by the survivor's (IntegrationHub's flow 6).</summary>
    public const string PatientMerge = "PatientMerge";
}

public static class PrescriptionStatus
{
    /// <summary>Saved, waiting to be delivered to the pharmacy.</summary>
    public const string Draft = "Draft";

    /// <summary>The pharmacy has it in reception.</summary>
    public const string Accepted = "Accepted";
    public const string Cancelled = "Cancelled";
    public const string Failed = "Failed";

    /// <summary>Asked the pharmacy to cancel; waiting for its answer (it refuses once dispensing has started).</summary>
    public const string Cancelling = "Cancelling";
}

/// <summary>An order: a lab test for LabFlow or an external lab, or an ECG / echo for ClinicReport (FHIR ServiceRequest).</summary>
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

    /// <summary>The message that carries this order (one per destination for the orders placed together).</summary>
    public long? OutboxMessageId { get; set; }

    /// <summary>The order's id in the other system once it has accepted it.</summary>
    [MaxLength(64)] public string? ExternalId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Why the clinician cancelled (kept while the application is asked, and after).</summary>
    [MaxLength(200)] public string? CancelReason { get; set; }
    public DateTime? CancelledUtc { get; set; }

    /// <summary>The other application's answer when it could not cancel, e.g. results already entered.</summary>
    [MaxLength(500)] public string? StatusNote { get; set; }

    public List<DiagnosticResult> Results { get; set; } = [];
}

/// <summary>
/// An electronic prescription: one drug written at a visit, sent to the pharmacy (PharmaFlow) as a FHIR
/// MedicationRequest. Drugs written together share a <see cref="GroupNumber"/> and one message, like the tests of an order.
/// The prescriber signs for it by writing it: a clinician with a provider record and a college registration number.
/// </summary>
public class Prescription
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int PrescriberProviderId { get; set; }
    public Provider PrescriberProvider { get; set; } = null!;

    /// <summary>The drug as a Drug Identification Number (8 digits), from <c>PrescriptionCatalog</c>.</summary>
    [MaxLength(8)] public string Din { get; set; } = "";

    /// <summary>What the prescriber saw when choosing, kept as written ("Amoxicillin 500 mg capsule").</summary>
    [MaxLength(200)] public string Display { get; set; } = "";

    /// <summary>The directions (sig), e.g. "1 capsule three times daily for 7 days".</summary>
    [MaxLength(200)] public string Directions { get; set; } = "";
    public int Quantity { get; set; }

    /// <summary>"capsule", "tablet", "inhaler", ... from the catalog.</summary>
    [MaxLength(20)] public string QuantityUnit { get; set; } = "";
    public int? DaysSupply { get; set; }

    /// <summary>Refills allowed, 0 to 11.</summary>
    public int Repeats { get; set; }

    /// <summary>The prescriber does not allow the pharmacist to substitute a generic.</summary>
    public bool NoSubstitution { get; set; }

    /// <summary>For the pharmacist.</summary>
    [MaxLength(500)] public string? Note { get; set; }

    /// <summary>The prescriptions written together at one visit sit under one number (FHIR groupIdentifier).</summary>
    public int GroupNumber { get; set; }

    [MaxLength(20)] public string Status { get; set; } = PrescriptionStatus.Draft;
    public long? OutboxMessageId { get; set; }

    /// <summary>The pharmacy's id for it once it has accepted it.</summary>
    [MaxLength(64)] public string? ExternalId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    [MaxLength(200)] public string? CancelReason { get; set; }
    public DateTime? CancelledUtc { get; set; }

    /// <summary>The pharmacy's answer when it could not cancel, e.g. already dispensed.</summary>
    [MaxLength(500)] public string? StatusNote { get; set; }

    /// <summary>The alerts the prescriber went ahead of, with the reasons given, one per line.</summary>
    [MaxLength(1000)] public string? AlertNote { get; set; }

    /// <summary>The earlier prescription this one renews.</summary>
    public int? RenewsPrescriptionId { get; set; }
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

    /// <summary>A prescription's cancel message names the prescription it withdraws.</summary>
    public int? PrescriptionId { get; set; }
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
