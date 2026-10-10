using ClinicHub.Api.Data;

namespace ClinicHub.Api.Domain;

public record HcvResult(string Code, bool Eligible, string PaymentProgram, string Message);

/// <summary>
/// Stands in for the Ministry's Health Card Validation service, which a real clinic reaches only after
/// onboarding. It applies the checks a front desk relies on: check digit, version code, expiry, and who
/// pays when the patient has no Ontario coverage. The response codes are this simulator's own, not the
/// Ministry's.
/// </summary>
public static class HcvSimulator
{
    public static HcvResult Check(Patient patient, string? presentedVersion, DateOnly today, IReadOnlyDictionary<string, bool> reciprocalByProvince)
    {
        if (patient.HealthCardNumber is null)
        {
            if (patient.CoverageProvince is { } p && p != "ON" && reciprocalByProvince.GetValueOrDefault(p))
                return new("RMB", true, PaymentPrograms.Rmb, $"Insured in {p}: bill OHIP under reciprocal medical billing.");
            if (patient.CoverageProvince == "QC")
                return new("QC", false, PaymentPrograms.Uninsured, "Quebec is not part of reciprocal billing: the patient pays and claims RAMQ.");
            return new("NONE", false, PaymentPrograms.Uninsured, "No provincial coverage on file: uninsured visit, the patient pays.");
        }

        if (!HealthCard.IsValidNumber(patient.HealthCardNumber))
            return new("INV", false, PaymentPrograms.Uninsured, "Health number fails the check digit. Re-enter it from the card.");

        var version = (presentedVersion ?? patient.HealthCardVersion ?? "").Trim().ToUpperInvariant();
        if (!HealthCard.IsValidVersion(version))
            return new("VFMT", false, PaymentPrograms.Uninsured, "Version code must be up to two letters.");
        if (!string.Equals(version, patient.HealthCardVersion ?? "", StringComparison.OrdinalIgnoreCase))
            return new("VER", false, PaymentPrograms.Uninsured, "Version code does not match the card on file. Ask for the current card.");

        if (patient.HealthCardExpiry is { } expiry && expiry < today)
            return new("EXP", false, PaymentPrograms.Uninsured, $"Card expired on {expiry:yyyy-MM-dd}. Ask the patient to renew at ServiceOntario.");

        return new("OK", true, PaymentPrograms.Hcp, "Eligible for OHIP-insured services.");
    }
}
