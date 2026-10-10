using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// ref schema: lists the clinic bills and codes against. See docs/schema.md.

/// <summary>An OHIP Schedule of Benefits service code with its suffix, e.g. A007A.</summary>
public class FeeCode
{
    [Key, MaxLength(5)] public string ServiceCode { get; set; } = "";
    [MaxLength(200)] public string Description { get; set; } = "";
    public decimal Fee { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Seed rows are an illustrative subset; the real list is imported from the Ministry file.</summary>
    public bool IsIllustrative { get; set; }
}

/// <summary>A 3-digit OHIP diagnostic code, e.g. 401 hypertension.</summary>
public class DiagnosticCode
{
    [Key, MaxLength(4)] public string Code { get; set; } = "";
    [MaxLength(200)] public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

/// <summary>The clinic's own price list for services OHIP does not pay for.</summary>
public class UninsuredService
{
    public int Id { get; set; }
    [MaxLength(20)] public string Code { get; set; } = "";
    [MaxLength(200)] public string Name { get; set; } = "";
    public decimal Price { get; set; }

    /// <summary>0 for most medical services; 13 (Ontario HST) for e.g. cosmetic services.</summary>
    public decimal TaxRatePercent { get; set; }

    public bool IsActive { get; set; } = true;
}

public class AppointmentType
{
    public int Id { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    public int DurationMinutes { get; set; }

    /// <summary>Colour token used on the schedule (e.g. "blue", "green").</summary>
    [MaxLength(20)] public string Color { get; set; } = "blue";

    public bool BookableOnline { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Province
{
    [Key, MaxLength(2)] public string Code { get; set; } = "";
    [MaxLength(60)] public string Name { get; set; } = "";

    /// <summary>Physician services for its residents can be billed to OHIP under reciprocal billing (RMB).</summary>
    public bool ReciprocalBilling { get; set; }
}
