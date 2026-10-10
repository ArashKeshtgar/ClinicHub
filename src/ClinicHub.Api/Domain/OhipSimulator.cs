namespace ClinicHub.Api.Domain;

/// <summary>
/// Stands in for OHIP's claims adjudication, which a clinic only sees as the monthly remittance advice.
/// It applies a handful of rules a billing clerk meets every month (the Schedule of Benefits fee caps what
/// is paid, duplicates are refused, the card must be valid on the day of service, claims go stale after
/// six months) and pays the rest in full. The explanatory codes are this simulator's own, not the Ministry's.
/// </summary>
public static class OhipSimulator
{
    public static readonly IReadOnlyDictionary<string, string> ExplanatoryCodes = new Dictionary<string, string>
    {
        ["AD"] = "Paid at the Schedule of Benefits fee, less than submitted",
        ["DU"] = "Duplicate: this service was already paid for this patient on this date",
        ["EX"] = "Health card not valid on the date of service",
        ["VC"] = "Version code on the claim does not match the patient's card",
        ["ST"] = "Stale-dated: received more than six months after the service",
        ["RN"] = "Reciprocal claim without the out-of-province health number",
        ["SC"] = "Service code not payable on the date of service",
    };

    public record PatientCard(string? HealthNumber, string? VersionCode, DateOnly? Expiry);
    public record ItemIn(string ServiceCode, DateOnly ServiceDate, int Units, decimal Submitted);
    public record ClaimIn(string AccountingNumber, int PatientId, string PaymentProgram, string? HealthNumber, string? VersionCode, PatientCard Card, ItemIn[] Items);
    public record ItemOut(decimal Paid, string? ExplanatoryCode);

    /// <param name="scheduleFees">Unit fee per service code in force on the service dates.</param>
    /// <param name="alreadyPaid">(patient, service code, date) already paid on earlier remittances.</param>
    public static Dictionary<(string Accounting, int Index), ItemOut> Adjudicate(
        IEnumerable<ClaimIn> claims, IReadOnlyDictionary<string, decimal> scheduleFees,
        ISet<(int PatientId, string ServiceCode, DateOnly Date)> alreadyPaid, DateOnly received)
    {
        var paid = new HashSet<(int, string, DateOnly)>(alreadyPaid);
        var result = new Dictionary<(string, int), ItemOut>();
        foreach (var c in claims)
        {
            var claimRefusal = c.PaymentProgram switch
            {
                "RMB" when string.IsNullOrWhiteSpace(c.HealthNumber) => "RN",
                "HCP" when !HealthCard.IsValidNumber(c.HealthNumber) => "EX",
                "HCP" when !string.Equals(c.VersionCode ?? "", c.Card.VersionCode ?? "", StringComparison.OrdinalIgnoreCase) => "VC",
                _ => null,
            };
            for (var i = 0; i < c.Items.Length; i++)
            {
                var item = c.Items[i];
                var code = claimRefusal
                    ?? (c.PaymentProgram == "HCP" && c.Card.Expiry is { } exp && exp < item.ServiceDate ? "EX" : null)
                    ?? (item.ServiceDate.AddMonths(6) < received ? "ST" : null)
                    ?? (!scheduleFees.ContainsKey(item.ServiceCode) ? "SC" : null)
                    ?? (!paid.Add((c.PatientId, item.ServiceCode, item.ServiceDate)) ? "DU" : null);
                if (code is not null)
                {
                    result[(c.AccountingNumber, i)] = new ItemOut(0m, code);
                    continue;
                }
                var schedule = scheduleFees[item.ServiceCode] * item.Units;
                result[(c.AccountingNumber, i)] = item.Submitted > schedule ? new ItemOut(schedule, "AD") : new ItemOut(item.Submitted, null);
            }
        }
        return result;
    }
}
