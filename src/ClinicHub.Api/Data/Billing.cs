using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// billing schema: OHIP claims and the remittance that pays them; invoices for uninsured services.

public static class ClaimStatus
{
    public const string Draft = "Draft";
    public const string Ready = "Ready";
    public const string Submitted = "Submitted";
    public const string Paid = "Paid";
    public const string PartiallyPaid = "PartiallyPaid";
    public const string Rejected = "Rejected";
}

/// <summary>One OHIP claim: an encounter's insured services under one payment program.</summary>
public class Claim
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int ProviderId { get; set; }
    public Provider Provider { get; set; } = null!;
    [MaxLength(10)] public string PaymentProgram { get; set; } = PaymentPrograms.Hcp;

    /// <summary>Health card as billed (a snapshot: the patient's card may change later).</summary>
    [MaxLength(10)] public string? HealthCardNumber { get; set; }
    [MaxLength(2)] public string? HealthCardVersion { get; set; }
    [MaxLength(2)] public string? Province { get; set; }

    /// <summary>Required for consultations: the referring provider's billing number.</summary>
    [MaxLength(6)] public string? ReferringProviderNumber { get; set; }

    /// <summary>8 characters, unique; comes back on the remittance advice to match payments.</summary>
    [MaxLength(8)] public string AccountingNumber { get; set; } = "";

    [MaxLength(20)] public string Status { get; set; } = ClaimStatus.Draft;
    public int? ClaimBatchId { get; set; }
    public ClaimBatch? ClaimBatch { get; set; }
    public decimal AmountSubmitted { get; set; }
    public decimal AmountPaid { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<ClaimItem> Items { get; set; } = [];
}

public class ClaimItem
{
    public int Id { get; set; }
    public int ClaimId { get; set; }
    public Claim Claim { get; set; } = null!;
    [MaxLength(5)] public string ServiceCode { get; set; } = "";
    public DateOnly ServiceDate { get; set; }
    public byte Units { get; set; } = 1;
    [MaxLength(4)] public string? DiagnosticCode { get; set; }
    public decimal FeeSubmitted { get; set; }
    public decimal? FeePaid { get; set; }

    /// <summary>Two-character explanatory code from the remittance when paid differently or refused.</summary>
    [MaxLength(2)] public string? ExplanatoryCode { get; set; }
}

public static class BatchStatus
{
    public const string Created = "Created";
    public const string Submitted = "Submitted";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
}

/// <summary>A claims file for one billing number in the MC EDT layout (header, claims, items, trailer).</summary>
public class ClaimBatch
{
    public int Id { get; set; }
    public int ProviderId { get; set; }
    public Provider Provider { get; set; } = null!;
    [MaxLength(4)] public string? GroupNumber { get; set; }
    public int BatchNumber { get; set; }
    [MaxLength(40)] public string FileName { get; set; } = "";
    public string FileContent { get; set; } = "";
    [MaxLength(20)] public string Status { get; set; } = BatchStatus.Created;
    public int ClaimCount { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? SubmittedUtc { get; set; }
    public List<Claim> Claims { get; set; } = [];
}

/// <summary>The monthly remittance advice: what OHIP paid, line by line.</summary>
public class RemittanceAdvice
{
    public int Id { get; set; }
    [MaxLength(6)] public string? OhipBillingNumber { get; set; }
    [MaxLength(4)] public string? GroupNumber { get; set; }
    public DateOnly PaymentDate { get; set; }
    [MaxLength(40)] public string FileName { get; set; } = "";

    /// <summary>The file as received, kept so a reconciliation can always be re-checked against it.</summary>
    public string FileContent { get; set; } = "";
    public bool Simulated { get; set; }
    public decimal TotalPaid { get; set; }
    public DateTime ImportedUtc { get; set; }
    public List<RemittanceItem> Items { get; set; } = [];
}

public class RemittanceItem
{
    public long Id { get; set; }
    public int RemittanceAdviceId { get; set; }
    public RemittanceAdvice RemittanceAdvice { get; set; } = null!;
    [MaxLength(8)] public string AccountingNumber { get; set; } = "";
    [MaxLength(5)] public string ServiceCode { get; set; } = "";
    public DateOnly ServiceDate { get; set; }
    public decimal AmountSubmitted { get; set; }
    public decimal AmountPaid { get; set; }
    [MaxLength(2)] public string? ExplanatoryCode { get; set; }
    public int? ClaimItemId { get; set; }
    public ClaimItem? ClaimItem { get; set; }
}

public static class InvoiceStatus
{
    public const string Draft = "Draft";
    public const string Issued = "Issued";
    public const string Paid = "Paid";
    public const string Void = "Void";
}

/// <summary>A bill for uninsured services, to the patient or a third party (employer, insurer, lawyer).</summary>
public class Invoice
{
    public int Id { get; set; }
    [MaxLength(20)] public string Number { get; set; } = "";
    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }
    public int? EncounterId { get; set; }
    public Encounter? Encounter { get; set; }
    [MaxLength(120)] public string? ThirdPartyName { get; set; }
    [MaxLength(20)] public string Status { get; set; } = InvoiceStatus.Draft;
    public DateTime? IssuedUtc { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedUtc { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];
    public List<InvoiceLine> Lines { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
}

public class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;
    public int? UninsuredServiceId { get; set; }
    public UninsuredService? UninsuredService { get; set; }
    [MaxLength(200)] public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal LineTotal { get; set; }
}

public static class PaymentMethods
{
    public const string Cash = "Cash";
    public const string Debit = "Debit";
    public const string Credit = "Credit";
    public const string ETransfer = "ETransfer";
    public const string Cheque = "Cheque";
}

public class Payment
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;
    public decimal Amount { get; set; }
    [MaxLength(20)] public string Method { get; set; } = PaymentMethods.Debit;
    [MaxLength(60)] public string? Reference { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public int ReceivedByUserId { get; set; }
}
