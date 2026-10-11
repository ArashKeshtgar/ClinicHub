using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicHub.Api.Integrations;

/// <summary>
/// The clinic's money, sent to FinancePowerApp (the group's accounting) as financial documents. Built from the
/// billing tables as they are, each with an id that never changes (claim:12:raised), so a document is sent once and a
/// resent one is recognised there. No health information leaves: no patient names, health cards, diagnoses or fee
/// codes. Patients are one party; invoices are known by their number.
///
/// <list type="bullet">
/// <item>A claim ready for OHIP (or WSIB): an invoice to the payer, OHIP professional fees (reciprocal billing for
/// another province's patient: OHIP pays it and recovers it).</item>
/// <item>A remittance advice: a receipt from OHIP, one line per claim paid; a claim paid less than billed, once final,
/// adjusts the rest (short-paid). A refused claim stays owed until it is resubmitted and paid, or written off there.</item>
/// <item>Each physician's share of a remittance, net of the clinic's overhead: a bill from the physician.</item>
/// <item>An invoice for uninsured services: owed by the patient, with its HST; a payment against it: a receipt.</item>
/// </list>
/// </summary>
public class FinanceFeed(IHttpClientFactory http, IOptionsMonitor<IntegrationOptions> options, TimeProvider clock, ILogger<FinanceFeed> logger)
{
    public const string HttpClientName = "finance";
    public const string Audience = "finance";
    public const string Scope = "finance";

    public bool Connected => !string.IsNullOrWhiteSpace(options.CurrentValue.Finance.BaseUrl);

    /// <summary>Sends what the clinic's books have that FinancePowerApp hasn't had yet. Returns how many were accepted.</summary>
    public async Task<int> SendAsync(ClinicHubDbContext db, Clinic clinic, ServiceTokens tokens, CancellationToken ct = default)
    {
        if (!Connected)
            return 0;
        var sent = (await db.FinanceExports.AsNoTracking().Select(x => x.ExternalId).ToListAsync(ct)).ToHashSet();
        var pending = (await BuildAsync(db, clinic, ct)).Where(d => !sent.Contains((string)d["externalId"]!)).ToList();
        var accepted = 0;
        foreach (var chunk in pending.Chunk(100))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.CurrentValue.Finance.BaseUrl!.TrimEnd('/') + "/api/integration/documents")
            {
                Content = JsonContent.Create(new JsonArray(chunk.Select(d => (JsonNode)d.DeepClone()).ToArray())),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Create(Audience, clinic, Scope));
            try
            {
                using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("FinancePowerApp refused {Count} documents for {Clinic}: {Status} {Body}", chunk.Length, clinic.Slug,
                        (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
                    return accepted;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning("FinancePowerApp could not be reached for {Clinic}: {Error}", clinic.Slug, ex.Message);
                return accepted;
            }
            var now = clock.GetUtcNow().UtcDateTime;
            var marks = chunk.Select(d => new FinanceExport { ExternalId = (string)d["externalId"]!, SentUtc = now }).ToList();
            db.FinanceExports.AddRange(marks);
            await db.SaveChangesAsync(ct);
            foreach (var m in marks) db.Entry(m).State = EntityState.Detached;   // the set is re-read on every pass
            accepted += chunk.Length;
        }
        if (accepted > 0)
            logger.LogInformation("Sent {Count} financial documents for {Clinic} to FinancePowerApp", accepted, clinic.Slug);
        return accepted;
    }

    /// <summary>Every financial document the clinic's books imply today.</summary>
    public static async Task<List<JsonObject>> BuildAsync(ClinicHubDbContext db, Clinic clinic, CancellationToken ct = default)
    {
        var docs = new List<JsonObject>();
        var key = clinic.Slug;

        var claims = await db.Claims.AsNoTracking().Include(c => c.Provider)
            .Where(c => c.Status != ClaimStatus.Draft).OrderBy(c => c.Id).ToListAsync(ct);
        foreach (var c in claims)
        {
            var wsib = c.PaymentProgram == PaymentPrograms.Wcb;
            docs.Add(Doc(key, $"claim:{c.Id}:raised", "Invoice", Local(c.CreatedUtc), c.AccountingNumber,
                wsib ? Party("WSIB", "WSIB") : Party("OHIP", "OHIP"),
                [Line(c.PaymentProgram switch { PaymentPrograms.Rmb => "OHIP.Reciprocal", PaymentPrograms.Wcb => "WSIB.Services", _ => "OHIP.Professional" },
                    c.AmountSubmitted, "EXEMPT", c.Provider.OhipBillingNumber)],
                memo: $"Claim {c.AccountingNumber}" + (c.PaymentProgram == PaymentPrograms.Rmb ? $" (reciprocal, {c.Province})" : "")));
        }

        var remittances = await db.Remittances.AsNoTracking().OrderBy(r => r.Id).Select(r => new
        {
            r.Id, r.PaymentDate, r.FileName,
            Items = r.Items.Where(i => i.ClaimItem != null).Select(i => new
            {
                i.AmountPaid, i.ClaimItem!.Claim.AccountingNumber, i.ClaimItem.Claim.ProviderId,
                i.ClaimItem.Claim.Provider.OhipBillingNumber, i.ClaimItem.Claim.Provider.OverheadPercent,
                Provider = i.ClaimItem.Claim.Provider.LastName + ", " + i.ClaimItem.Claim.Provider.FirstName,
            }).ToList(),
        }).ToListAsync(ct);
        foreach (var r in remittances)
        {
            var paid = r.Items.Where(i => i.AmountPaid > 0).GroupBy(i => (i.AccountingNumber, i.OhipBillingNumber))
                .Select(g => Line(null, g.Sum(i => i.AmountPaid), null, g.Key.OhipBillingNumber, appliesTo: g.Key.AccountingNumber)).ToArray();
            if (paid.Length > 0)
                docs.Add(Doc(key, $"ra:{r.Id}", "Receipt", r.PaymentDate, $"RA-{r.Id}", Party("OHIP", "OHIP"), paid, method: "EFT", memo: $"Remittance advice {r.FileName}"));

            // Each physician's share of what this remittance paid for their claims, net of the clinic's overhead.
            foreach (var p in r.Items.Where(i => i.OhipBillingNumber != null).GroupBy(i => (i.ProviderId, i.OhipBillingNumber, i.Provider, i.OverheadPercent)))
            {
                var total = p.Sum(i => i.AmountPaid);
                var net = total - Math.Round(total * p.Key.OverheadPercent / 100m, 2, MidpointRounding.AwayFromZero);
                if (net > 0)
                    docs.Add(Doc(key, $"ra:{r.Id}:provider:{p.Key.OhipBillingNumber}", "Bill", r.PaymentDate, $"PAY-{r.Id}-{p.Key.OhipBillingNumber}",
                        Party("Provider", p.Key.OhipBillingNumber!, $"Dr. {p.Key.Provider}"),
                        [Line("Expense.PhysicianShare", net, "NONE", p.Key.OhipBillingNumber)],
                        memo: $"Share of remittance {r.FileName}: {total:N2} less {p.Key.OverheadPercent:0.##} % overhead"));
            }
        }

        // A claim paid less than billed is final: the rest is adjusted away, dated with its last remittance.
        var lastRemittance = remittances.SelectMany(r => r.Items.Select(i => (i.AccountingNumber, r.PaymentDate)))
            .GroupBy(x => x.AccountingNumber).ToDictionary(g => g.Key, g => g.Max(x => x.PaymentDate));
        foreach (var c in claims.Where(c => c.Status == ClaimStatus.PartiallyPaid && c.AmountSubmitted > c.AmountPaid))
            docs.Add(Doc(key, $"claim:{c.Id}:shortpaid", "Adjustment", lastRemittance.GetValueOrDefault(c.AccountingNumber, Local(c.CreatedUtc)), c.AccountingNumber,
                c.PaymentProgram == PaymentPrograms.Wcb ? Party("WSIB", "WSIB") : Party("OHIP", "OHIP"),
                [Line("Adjustment.ShortPaid", c.AmountSubmitted - c.AmountPaid, null, c.Provider.OhipBillingNumber)], memo: $"Claim {c.AccountingNumber} paid less than billed"));

        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Payments)
            .Where(i => i.Status != InvoiceStatus.Draft && i.IssuedUtc != null && i.Total > 0).OrderBy(i => i.Id).ToListAsync(ct);
        foreach (var inv in invoices)
        {
            var party = inv.PatientId is null ? Party("Other", "THIRDPARTY", "Third party") : Party("Patient", "PATIENT");
            docs.Add(Doc(key, $"invoice:{inv.Id}", "Invoice", Local(inv.IssuedUtc!.Value), inv.Number, party, InvoiceLines(inv), memo: $"Invoice {inv.Number}"));
            foreach (var pay in inv.Payments.OrderBy(p => p.Id))
                docs.Add(Doc(key, $"payment:{pay.Id}", "Receipt", Local(pay.ReceivedUtc), inv.Number, party,
                    [Line(null, pay.Amount, null, null, appliesTo: inv.Number)], method: Method(pay.Method), memo: $"Payment on {inv.Number}"));
            if (inv.Status == InvoiceStatus.Void)
                docs.Add(Doc(key, $"invoice:{inv.Id}:void", "Reversal", Local(inv.IssuedUtc.Value), inv.Number, null, [], reverses: $"invoice:{inv.Id}"));
        }
        return docs;
    }

    /// <summary>One line per invoice line; the invoice's HST spread over its taxable lines so they add up to the cent.</summary>
    private static JsonObject[] InvoiceLines(Invoice inv)
    {
        var lines = inv.Lines.OrderBy(l => l.Id).ToList();
        var taxable = lines.Where(l => l.TaxRatePercent > 0).ToList();
        var lines2 = new List<JsonObject>();
        decimal allocated = 0;
        foreach (var l in lines)
        {
            decimal? tax = null;
            if (l.TaxRatePercent > 0)
            {
                tax = l == taxable[^1] ? inv.Tax - allocated : Math.Round(l.LineTotal * l.TaxRatePercent / 100m, 2, MidpointRounding.AwayFromZero);
                allocated += tax.Value;
            }
            lines2.Add(Line("Uninsured.Services", l.LineTotal, l.TaxRatePercent > 0 ? "HST13" : "EXEMPT", null, tax: tax));
        }
        return [.. lines2];
    }

    private static string Method(string m) => m switch
    {
        PaymentMethods.Cash => "Cash", PaymentMethods.Credit => "Credit", PaymentMethods.Cheque => "Cheque",
        PaymentMethods.ETransfer => "EFT", _ => "Debit",
    };

    private static DateOnly Local(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, DemoSeeder.Toronto));

    private static JsonObject Party(string type, string code, string? name = null) => new() { ["type"] = type, ["code"] = code, ["name"] = name };

    private static JsonObject Line(string? category, decimal amount, string? taxCode, string? provider, string? appliesTo = null, decimal? tax = null) => new()
    {
        ["category"] = category, ["amount"] = amount, ["taxCode"] = taxCode, ["tax"] = tax, ["department"] = "Clinic",
        ["providerCode"] = provider, ["appliesTo"] = appliesTo,
    };

    private static JsonObject Doc(string sourceKey, string externalId, string kind, DateOnly date, string reference, JsonObject? party, JsonObject[] lines,
        string? method = null, string? memo = null, string? reverses = null) => new()
    {
        ["sourceKey"] = sourceKey, ["externalId"] = externalId, ["kind"] = kind, ["date"] = date.ToString("yyyy-MM-dd"), ["reference"] = reference,
        ["party"] = party, ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()), ["method"] = method, ["memo"] = memo, ["reverses"] = reverses,
    };
}
