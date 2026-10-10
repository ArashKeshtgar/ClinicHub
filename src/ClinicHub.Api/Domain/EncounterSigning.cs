using System.Globalization;
using ClinicHub.Api.Data;

namespace ClinicHub.Api.Domain;

/// <summary>
/// What signing a visit does, besides locking it: OHIP-insured services become a claim ready for the
/// next batch, and services the patient pays for become an invoice. Used by the sign endpoint and by
/// the demo seeder, so seeded history bills exactly like a real signature.
/// </summary>
public static class EncounterSigning
{
    /// <summary>
    /// Signs <paramref name="e"/>, which must be loaded with its patient, provider, diagnoses and services
    /// (with their uninsured service), and adds the claim and invoice to <paramref name="db"/>.
    /// </summary>
    public static void Sign(ClinicHubDbContext db, Encounter e, int userId, DateTime now)
    {
        e.Status = EncounterStatus.Signed;
        e.SignedUtc = now;
        e.SignedByUserId = userId;
        e.EndedUtc ??= now;

        var primary = e.Diagnoses.Single(d => d.IsPrimary).DiagnosticCodeCode;
        var insured = e.Services.Where(s => s.FeeCodeServiceCode is not null).ToList();
        // Salaried providers (most nurse practitioners) have no billing number: their insured services are recorded, not claimed.
        if (insured.Count > 0 && e.PaymentProgram != PaymentPrograms.Uninsured && e.Provider.OhipBillingNumber is not null)
            db.Claims.Add(new Claim
            {
                EncounterId = e.Id, PatientId = e.PatientId, ProviderId = e.ProviderId, PaymentProgram = e.PaymentProgram,
                HealthCardNumber = e.Patient.HealthCardNumber ?? e.Patient.OutOfProvinceNumber, HealthCardVersion = e.Patient.HealthCardVersion,
                Province = e.PaymentProgram == PaymentPrograms.Rmb ? e.Patient.CoverageProvince : "ON",
                // One claim per encounter and program, so the encounter number is unique and readable on the remittance.
                AccountingNumber = e.Id.ToString("D8", CultureInfo.InvariantCulture),
                Status = ClaimStatus.Ready, AmountSubmitted = insured.Sum(s => s.Fee), CreatedUtc = now,
                Items = insured.Select(s => new ClaimItem { ServiceCode = s.FeeCodeServiceCode!, ServiceDate = s.ServiceDate, Units = s.Units,
                    DiagnosticCode = primary, FeeSubmitted = s.Fee }).ToList(),
            });

        var privatelyPaid = e.Services.Where(s => s.UninsuredService is not null).ToList();
        if (privatelyPaid.Count > 0)
        {
            var lines = privatelyPaid.Select(s => new InvoiceLine
            {
                UninsuredServiceId = s.UninsuredServiceId, Description = s.UninsuredService!.Name, Quantity = s.Units,
                UnitPrice = s.UninsuredService.Price, TaxRatePercent = s.UninsuredService.TaxRatePercent, LineTotal = s.Fee,
            }).ToList();
            var subtotal = lines.Sum(l => l.LineTotal);
            var tax = Math.Round(lines.Sum(l => l.LineTotal * l.TaxRatePercent / 100m), 2, MidpointRounding.AwayFromZero);
            db.Invoices.Add(new Invoice
            {
                Number = $"INV-{e.Id:D6}", PatientId = e.PatientId, EncounterId = e.Id, Status = InvoiceStatus.Issued, IssuedUtc = now,
                Subtotal = subtotal, Tax = tax, Total = subtotal + tax, Balance = subtotal + tax, CreatedUtc = now, Lines = lines,
            });
        }
    }
}
