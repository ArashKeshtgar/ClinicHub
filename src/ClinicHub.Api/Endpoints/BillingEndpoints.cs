using System.Security.Claims;
using System.Text;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static ClinicHub.Api.Endpoints.FrontDeskEndpoints;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// OHIP billing as a clinic's billing clerk does it: signed visits wait as Ready claims, go out in one
/// MC EDT file per billing number, and come back weeks later on the remittance advice, which is matched
/// line by line to what was claimed. Submission and the remittance are simulated (both need Ministry
/// onboarding); the files have the real shape and the import reads any file of that shape.
/// </summary>
public static class BillingEndpoints
{
    public record Kpi(int Count, decimal Amount);
    public record Overview(Kpi Ready, Kpi Unbatched, Kpi Submitted, Kpi PaidLast30Days, Kpi Rejected, Kpi PartiallyPaid, Kpi InvoicesOwing,
        ProviderBrief[] Providers, IReadOnlyDictionary<string, string> ExplanatoryCodes);

    public record ClaimRow(int Id, string AccountingNumber, string Status, string PaymentProgram, int PatientId, string Patient, string Mrn,
        int ProviderId, string Provider, DateOnly ServiceDate, string Services, decimal AmountSubmitted, decimal AmountPaid,
        string? BatchFile, int EncounterId, string[] ExplanatoryCodes);
    public record ClaimItemDto(string ServiceCode, string? Description, DateOnly ServiceDate, int Units, string? DiagnosticCode,
        decimal FeeSubmitted, decimal? FeePaid, string? ExplanatoryCode, string? Explanation);
    public record ClaimDetail(ClaimRow Claim, string? HealthCard, string? Province, ClaimItemDto[] Items);

    public record BatchRow(int Id, int ProviderId, string Provider, string? BillingNumber, int BatchNumber, string FileName, string Status,
        int ClaimCount, decimal Total, DateTime CreatedUtc, DateTime? SubmittedUtc);

    public record RemittanceRow(int Id, string FileName, DateOnly PaymentDate, decimal TotalPaid, int Items, int Exceptions, int Unmatched,
        bool Simulated, DateTime ImportedUtc);
    public record ProviderPayment(int ProviderId, string Provider, string? BillingNumber, int Claims, decimal Submitted, decimal Paid,
        decimal OverheadPercent, decimal Overhead, decimal Net);
    public record ExceptionRow(string AccountingNumber, int? ClaimId, string? Patient, string ServiceCode, DateOnly ServiceDate,
        decimal Submitted, decimal Paid, string? ExplanatoryCode, string Explanation);
    public record RemittanceDetail(RemittanceRow Remittance, string[] Messages, ProviderPayment[] Providers, ExceptionRow[] Exceptions);

    public record ImportRequest(string FileName, string Content);

    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/billing").WithTags("Billing");

        api.MapGet("/overview", async (ClinicHubDbContext db, TimeProvider clock) =>
        {
            var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
            var byStatus = await db.Claims.GroupBy(c => c.Status)
                .Select(g => new { g.Key, Count = g.Count(), Submitted = g.Sum(c => c.AmountSubmitted) }).ToDictionaryAsync(x => x.Key);
            Kpi K(string s) => byStatus.TryGetValue(s, out var x) ? new Kpi(x.Count, x.Submitted) : new Kpi(0, 0);
            var unbatched = await db.Claims.Where(c => c.Status == ClaimStatus.Ready && c.ClaimBatchId == null)
                .GroupBy(_ => 1).Select(g => new Kpi(g.Count(), g.Sum(c => c.AmountSubmitted))).FirstOrDefaultAsync() ?? new Kpi(0, 0);
            var paid = await db.Remittances.Where(r => r.ImportedUtc >= since)
                .GroupBy(_ => 1).Select(g => new Kpi(g.Count(), g.Sum(r => r.TotalPaid))).FirstOrDefaultAsync() ?? new Kpi(0, 0);
            var owing = await db.Invoices.Where(i => i.Status == InvoiceStatus.Issued && i.Balance > 0)
                .GroupBy(_ => 1).Select(g => new Kpi(g.Count(), g.Sum(i => i.Balance))).FirstOrDefaultAsync() ?? new Kpi(0, 0);
            var providers = await db.Providers.AsNoTracking().Where(p => p.OhipBillingNumber != null).OrderBy(p => p.LastName).ToListAsync();
            return new Overview(K(ClaimStatus.Ready), unbatched, K(ClaimStatus.Submitted), paid, K(ClaimStatus.Rejected), K(ClaimStatus.PartiallyPaid), owing,
                providers.Select(p => new ProviderBrief(p.Id, p.DisplayName, p.Kind, p.SpecialtyName)).ToArray(), OhipSimulator.ExplanatoryCodes);
        }).RequireAuthorization(Permissions.BillingView);

        api.MapGet("/claims", async (string? status, int? providerId, ClinicHubDbContext db) =>
        {
            var q = db.Claims.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(status)) q = q.Where(c => c.Status == status);
            if (providerId is not null) q = q.Where(c => c.ProviderId == providerId);
            return await Rows(q.OrderByDescending(c => c.CreatedUtc).Take(500));
        }).RequireAuthorization(Permissions.BillingView);

        api.MapGet("/claims/{id:int}", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var row = (await Rows(db.Claims.AsNoTracking().Where(c => c.Id == id))).SingleOrDefault();
            if (row is null)
                return Results.NotFound();
            var claim = await db.Claims.AsNoTracking().Include(c => c.Items).SingleAsync(c => c.Id == id);
            var descriptions = await db.FeeCodes.AsNoTracking().ToDictionaryAsync(f => f.ServiceCode, f => f.Description);
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = clock.GetUtcNow().UtcDateTime, UserId = user.RequiredUserId(), PatientId = claim.PatientId,
                Action = AccessActions.View, Context = $"Claim {claim.AccountingNumber}", IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            var card = claim.HealthCardNumber is { } hcn && hcn.Length >= 3 ? $"•••• ••• {hcn[^3..]}{(claim.HealthCardVersion is { } v ? " " + v : "")}" : null;
            return Results.Ok(new ClaimDetail(row, card, claim.Province, claim.Items.OrderBy(i => i.Id).Select(i => new ClaimItemDto(i.ServiceCode,
                descriptions.GetValueOrDefault(i.ServiceCode), i.ServiceDate, i.Units, i.DiagnosticCode, i.FeeSubmitted, i.FeePaid, i.ExplanatoryCode,
                i.ExplanatoryCode is { } e ? OhipSimulator.ExplanatoryCodes.GetValueOrDefault(e, "Explanatory code from the remittance") : null)).ToArray()));
        }).RequireAuthorization(Permissions.BillingView);

        // A refused claim goes back to Ready once the cause (usually the health card) is fixed.
        api.MapPost("/claims/{id:int}/resubmit", async (int id, ClinicHubDbContext db) =>
        {
            var claim = await db.Claims.Include(c => c.Items).Include(c => c.Patient).SingleOrDefaultAsync(c => c.Id == id);
            if (claim is null)
                return Results.NotFound();
            if (claim.Status != ClaimStatus.Rejected)
                return Results.Problem("Only refused claims are resubmitted.", statusCode: StatusCodes.Status409Conflict);
            // The claim carries the card as it is now on file.
            claim.HealthCardNumber = claim.PaymentProgram == PaymentPrograms.Rmb ? claim.Patient.OutOfProvinceNumber : claim.Patient.HealthCardNumber;
            claim.HealthCardVersion = claim.Patient.HealthCardVersion;
            claim.Status = ClaimStatus.Ready;
            claim.ClaimBatchId = null;
            claim.AmountPaid = 0;
            foreach (var i in claim.Items) { i.FeePaid = null; i.ExplanatoryCode = null; }
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Permissions.BillingManage);

        api.MapGet("/batches", async (ClinicHubDbContext db) => await BatchRows(db.ClaimBatches.AsNoTracking().OrderByDescending(b => b.CreatedUtc).Take(200)))
            .RequireAuthorization(Permissions.BillingView);

        // One file per billing number with every Ready OHIP or reciprocal claim not yet in a file. WSIB is billed to the WSIB, not here.
        api.MapPost("/batches", async (ClinicHubDbContext db, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var today = Today(clock);
            var location = await db.Locations.AsNoTracking().OrderBy(l => l.Id).FirstAsync();
            var claims = await db.Claims.Include(c => c.Items).Include(c => c.Patient).Include(c => c.Provider)
                .Where(c => c.Status == ClaimStatus.Ready && c.ClaimBatchId == null && c.Provider.OhipBillingNumber != null
                    && (c.PaymentProgram == PaymentPrograms.Hcp || c.PaymentProgram == PaymentPrograms.Rmb))
                .OrderBy(c => c.Id).ToListAsync();
            if (claims.Count == 0)
                return Results.Problem("No claims are waiting for a batch.", statusCode: StatusCodes.Status409Conflict);

            var created = new List<ClaimBatch>();
            foreach (var group in claims.GroupBy(c => c.Provider))
            {
                var p = group.Key;
                var number = (await db.ClaimBatches.Where(b => b.ProviderId == p.Id).MaxAsync(b => (int?)b.BatchNumber) ?? 0) + 1;
                var file = new ClaimFile.Batch(today, number, location.OhipGroupNumber, p.OhipBillingNumber!, p.SpecialtyCode ?? "00",
                    group.Select(c => new ClaimFile.ClaimEntry(c.AccountingNumber, c.PaymentProgram, c.HealthCardNumber, c.HealthCardVersion,
                        c.Patient.DateOfBirth, c.ReferringProviderNumber,
                        c.Items.OrderBy(i => i.Id).Select(i => new ClaimFile.Item(i.ServiceCode, i.FeeSubmitted, i.Units, i.ServiceDate, i.DiagnosticCode)).ToArray(),
                        c.PaymentProgram == PaymentPrograms.Rmb ? (c.Patient.LastName, c.Patient.FirstName, c.Patient.Sex, c.Province ?? "") : null)).ToArray());
                var batch = new ClaimBatch
                {
                    ProviderId = p.Id, GroupNumber = location.OhipGroupNumber, BatchNumber = number, FileName = ClaimFile.FileName(file),
                    FileContent = ClaimFile.Build(file), ClaimCount = group.Count(), Total = group.Sum(c => c.AmountSubmitted), CreatedUtc = now,
                    Claims = group.ToList(),
                };
                db.ClaimBatches.Add(batch);
                created.Add(batch);
            }
            await db.SaveChangesAsync();
            return Results.Ok(await BatchRows(db.ClaimBatches.AsNoTracking().Where(b => created.Select(c => c.Id).Contains(b.Id))));
        }).RequireAuthorization(Permissions.BillingManage);

        api.MapGet("/batches/{id:int}/file", async (int id, ClinicHubDbContext db) =>
            await db.ClaimBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id) is { } b
                ? Results.File(Encoding.ASCII.GetBytes(b.FileContent), "text/plain", b.FileName)
                : Results.NotFound()).RequireAuthorization(Permissions.BillingView);

        // Stands in for the upload to MC EDT: the file is accepted as is.
        api.MapPost("/batches/{id:int}/submit", async (int id, ClinicHubDbContext db, TimeProvider clock) =>
        {
            var batch = await db.ClaimBatches.Include(b => b.Claims).SingleOrDefaultAsync(b => b.Id == id);
            if (batch is null)
                return Results.NotFound();
            if (batch.Status != BatchStatus.Created)
                return Results.Problem("This file was already sent.", statusCode: StatusCodes.Status409Conflict);
            batch.Status = BatchStatus.Submitted;
            batch.SubmittedUtc = clock.GetUtcNow().UtcDateTime;
            foreach (var c in batch.Claims) c.Status = ClaimStatus.Submitted;
            await db.SaveChangesAsync();
            return Results.Ok((await BatchRows(db.ClaimBatches.AsNoTracking().Where(b => b.Id == id))).Single());
        }).RequireAuthorization(Permissions.BillingManage);

        api.MapGet("/remittances", async (ClinicHubDbContext db) => await RemittanceRows(db.Remittances.AsNoTracking().OrderByDescending(r => r.ImportedUtc).Take(100)))
            .RequireAuthorization(Permissions.BillingView);

        api.MapGet("/remittances/{id:int}", async (int id, ClinicHubDbContext db) =>
            await db.Remittances.AsNoTracking().AnyAsync(r => r.Id == id) ? Results.Ok(await RemittanceDetailOf(db, id)) : Results.NotFound())
            .RequireAuthorization(Permissions.BillingView);

        api.MapGet("/remittances/{id:int}/file", async (int id, ClinicHubDbContext db) =>
            await db.Remittances.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id) is { } r
                ? Results.File(Encoding.ASCII.GetBytes(r.FileContent), "text/plain", r.FileName)
                : Results.NotFound()).RequireAuthorization(Permissions.BillingView);

        // What OHIP would send back for everything submitted: adjudicated by the simulator, then imported like any received file.
        api.MapPost("/remittances/simulate", async (ClinicHubDbContext db, TimeProvider clock) =>
        {
            var today = Today(clock);
            var location = await db.Locations.AsNoTracking().OrderBy(l => l.Id).FirstAsync();
            var claims = await db.Claims.AsNoTracking().Include(c => c.Items).Include(c => c.Patient).Include(c => c.Provider)
                .Where(c => c.Status == ClaimStatus.Submitted).OrderBy(c => c.Id).ToListAsync();
            if (claims.Count == 0)
                return Results.Problem("Nothing has been submitted that is waiting for payment.", statusCode: StatusCodes.Status409Conflict);

            var fees = await db.FeeCodes.AsNoTracking().Where(f => f.IsActive).ToDictionaryAsync(f => f.ServiceCode, f => f.Fee);
            var paidBefore = (await db.ClaimItems.AsNoTracking().Where(i => i.FeePaid > 0)
                    .Select(i => new { i.Claim.PatientId, i.ServiceCode, i.ServiceDate }).ToListAsync())
                .Select(x => (x.PatientId, x.ServiceCode, x.ServiceDate)).ToHashSet();
            var outcome = OhipSimulator.Adjudicate(claims.Select(c => new OhipSimulator.ClaimIn(c.AccountingNumber, c.PatientId, c.PaymentProgram,
                    c.HealthCardNumber, c.HealthCardVersion, new OhipSimulator.PatientCard(c.Patient.HealthCardNumber, c.Patient.HealthCardVersion, c.Patient.HealthCardExpiry),
                    c.Items.OrderBy(i => i.Id).Select(i => new OhipSimulator.ItemIn(i.ServiceCode, i.ServiceDate, i.Units, i.FeeSubmitted)).ToArray())),
                fees, paidBefore, today);

            var sequence = await db.Remittances.CountAsync() + 1;
            var advice = new RemittanceFile.Advice(location.OhipGroupNumber, null, today, location.Name, $"SIM{sequence:00000}",
                claims.Select((c, n) => new RemittanceFile.ClaimEntry($"{today:yyMMdd}{n + 1:00000}", c.Provider.OhipBillingNumber!, c.Provider.SpecialtyCode ?? "00",
                    c.AccountingNumber, c.HealthCardNumber, c.HealthCardVersion, c.PaymentProgram,
                    c.Items.OrderBy(i => i.Id).Select((i, k) => new RemittanceFile.Item(i.ServiceDate, i.Units, i.ServiceCode, i.FeeSubmitted,
                        outcome[(c.AccountingNumber, k)].Paid, outcome[(c.AccountingNumber, k)].ExplanatoryCode)).ToArray())).ToArray(),
                ["SIMULATED REMITTANCE ADVICE GENERATED BY CLINICHUB.", "NOT ISSUED BY THE MINISTRY OF HEALTH. NO PAYMENT WAS MADE."]);
            var content = RemittanceFile.Build(advice);
            return await Import(db, clock, RemittanceFile.FileName(advice) is var name && await db.Remittances.AnyAsync(r => r.FileName == name)
                ? name.Replace(".RA", $"-{sequence}.RA") : name, content, simulated: true);
        }).RequireAuthorization(Permissions.BillingManage);

        api.MapPost("/remittances", (ImportRequest req, ClinicHubDbContext db, TimeProvider clock) =>
            Import(db, clock, Path.GetFileName(req.FileName ?? "").Trim(), req.Content ?? "", simulated: false))
            .RequireAuthorization(Permissions.BillingManage);
    }

    /// <summary>Reads a remittance file and matches every paid line to the claim item it pays.</summary>
    private static async Task<IResult> Import(ClinicHubDbContext db, TimeProvider clock, string fileName, string content, bool simulated)
    {
        if (fileName.Length is 0 or > 40)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["fileName"] = ["Give the file's name (up to 40 characters)."] });
        RemittanceFile.Advice advice;
        try
        {
            advice = RemittanceFile.Parse(content);
        }
        catch (RemittanceFile.FormatException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = [ex.Message] });
        }
        if (await db.Remittances.AnyAsync(r => r.FileName == fileName))
            return Results.Problem($"{fileName} was already imported.", statusCode: StatusCodes.Status409Conflict);

        var accounting = advice.Claims.Select(c => c.AccountingNumber).Distinct().ToList();
        var claims = await db.Claims.Include(c => c.Items).Include(c => c.ClaimBatch)
            .Where(c => accounting.Contains(c.AccountingNumber)).ToDictionaryAsync(c => c.AccountingNumber);
        var ra = new RemittanceAdvice
        {
            OhipBillingNumber = advice.ProviderNumber, GroupNumber = advice.GroupNumber, PaymentDate = advice.PaymentDate, FileName = fileName,
            FileContent = content, Simulated = simulated, TotalPaid = advice.TotalPaid, ImportedUtc = clock.GetUtcNow().UtcDateTime,
        };
        foreach (var entry in advice.Claims)
        {
            claims.TryGetValue(entry.AccountingNumber, out var claim);
            var unmatched = claim?.Items.ToList() ?? [];
            foreach (var line in entry.Items)
            {
                var item = unmatched.FirstOrDefault(i => i.ServiceCode == line.ServiceCode && i.ServiceDate == line.ServiceDate);
                if (item is not null)
                {
                    unmatched.Remove(item);
                    item.FeePaid = line.Paid;
                    item.ExplanatoryCode = line.ExplanatoryCode;
                }
                ra.Items.Add(new RemittanceItem
                {
                    AccountingNumber = entry.AccountingNumber, ServiceCode = line.ServiceCode, ServiceDate = line.ServiceDate,
                    AmountSubmitted = line.Submitted, AmountPaid = line.Paid, ExplanatoryCode = line.ExplanatoryCode, ClaimItem = item,
                });
            }
            if (claim is null)
                continue;
            claim.AmountPaid = claim.Items.Sum(i => i.FeePaid ?? 0);
            claim.Status = claim.AmountPaid <= 0 ? ClaimStatus.Rejected
                : claim.Items.All(i => i.FeePaid >= i.FeeSubmitted) ? ClaimStatus.Paid : ClaimStatus.PartiallyPaid;
        }
        db.Remittances.Add(ra);
        // A file is settled once none of its claims is still waiting.
        foreach (var batch in claims.Values.Select(c => c.ClaimBatch).OfType<ClaimBatch>().Distinct())
            if (await db.Claims.Where(c => c.ClaimBatchId == batch.Id).AllAsync(c => c.Status != ClaimStatus.Submitted || accounting.Contains(c.AccountingNumber)))
                batch.Status = BatchStatus.Accepted;
        await db.SaveChangesAsync();
        return Results.Ok(await RemittanceDetailOf(db, ra.Id));
    }

    private static async Task<RemittanceDetail> RemittanceDetailOf(ClinicHubDbContext db, int id)
    {
        var row = (await RemittanceRows(db.Remittances.AsNoTracking().Where(r => r.Id == id))).Single();
        var content = await db.Remittances.Where(r => r.Id == id).Select(r => r.FileContent).SingleAsync();
        var messages = RemittanceFile.Parse(content).Messages;
        var items = await db.RemittanceItems.AsNoTracking().Where(i => i.RemittanceAdviceId == id)
            .Select(i => new
            {
                i.AccountingNumber, i.ServiceCode, i.ServiceDate, i.AmountSubmitted, i.AmountPaid, i.ExplanatoryCode,
                Claim = i.ClaimItem == null ? null : new { i.ClaimItem.Claim.Id, i.ClaimItem.Claim.ProviderId, i.ClaimItem.Claim.Patient.LastName, i.ClaimItem.Claim.Patient.FirstName },
            }).ToListAsync();
        var providerIds = items.Where(i => i.Claim != null).Select(i => i.Claim!.ProviderId).Distinct().ToList();
        var providers = await db.Providers.AsNoTracking().Where(p => providerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var payments = items.Where(i => i.Claim != null).GroupBy(i => i.Claim!.ProviderId).Select(g =>
        {
            var p = providers[g.Key];
            var paid = g.Sum(i => i.AmountPaid);
            var overhead = Math.Round(paid * p.OverheadPercent / 100m, 2, MidpointRounding.AwayFromZero);
            return new ProviderPayment(p.Id, p.DisplayName, p.OhipBillingNumber, g.Select(i => i.AccountingNumber).Distinct().Count(),
                g.Sum(i => i.AmountSubmitted), paid, p.OverheadPercent, overhead, paid - overhead);
        }).OrderBy(p => p.Provider).ToArray();

        var exceptions = items.Where(i => i.Claim == null || i.ExplanatoryCode != null || i.AmountPaid != i.AmountSubmitted)
            .Select(i => new ExceptionRow(i.AccountingNumber, i.Claim?.Id, i.Claim is null ? null : $"{i.Claim.LastName}, {i.Claim.FirstName}", i.ServiceCode,
                i.ServiceDate, i.AmountSubmitted, i.AmountPaid, i.ExplanatoryCode,
                i.Claim is null ? "No claim with this accounting number and service: check the file is this clinic's."
                : i.ExplanatoryCode is { } e ? OhipSimulator.ExplanatoryCodes.GetValueOrDefault(e, "Explanatory code from the remittance")
                : "Paid differently from what was claimed")).ToArray();
        return new RemittanceDetail(row, messages, payments, exceptions);
    }

    private static async Task<ClaimRow[]> Rows(IQueryable<Data.Claim> q)
    {
        var rows = await q.Select(c => new
        {
            c.Id, c.AccountingNumber, c.Status, c.PaymentProgram, c.PatientId, c.Patient.LastName, c.Patient.FirstName, c.Patient.Mrn,
            c.ProviderId, c.Provider.Title, ProviderFirst = c.Provider.FirstName, ProviderLast = c.Provider.LastName,
            ServiceDate = c.Items.Min(i => (DateOnly?)i.ServiceDate), Services = c.Items.Select(i => i.ServiceCode).ToList(),
            c.AmountSubmitted, c.AmountPaid, BatchFile = c.ClaimBatch == null ? null : c.ClaimBatch.FileName, c.EncounterId,
            Codes = c.Items.Where(i => i.ExplanatoryCode != null).Select(i => i.ExplanatoryCode!).ToList(),
        }).ToListAsync();
        return rows.Select(r => new ClaimRow(r.Id, r.AccountingNumber, r.Status, r.PaymentProgram, r.PatientId, $"{r.LastName}, {r.FirstName}", r.Mrn,
            r.ProviderId, $"{(r.Title is null ? "" : r.Title + " ")}{r.ProviderFirst} {r.ProviderLast}", r.ServiceDate ?? default,
            string.Join(", ", r.Services), r.AmountSubmitted, r.AmountPaid, r.BatchFile, r.EncounterId, r.Codes.Distinct().ToArray())).ToArray();
    }

    private static async Task<BatchRow[]> BatchRows(IQueryable<ClaimBatch> q) =>
        (await q.Select(b => new
        {
            b.Id, b.ProviderId, b.Provider.Title, b.Provider.FirstName, b.Provider.LastName, b.Provider.OhipBillingNumber, b.BatchNumber, b.FileName,
            b.Status, b.ClaimCount, b.Total, b.CreatedUtc, b.SubmittedUtc,
        }).ToListAsync())
        .Select(b => new BatchRow(b.Id, b.ProviderId, $"{(b.Title is null ? "" : b.Title + " ")}{b.FirstName} {b.LastName}", b.OhipBillingNumber, b.BatchNumber,
            b.FileName, b.Status, b.ClaimCount, b.Total, b.CreatedUtc, b.SubmittedUtc)).ToArray();

    private static Task<RemittanceRow[]> RemittanceRows(IQueryable<RemittanceAdvice> q) =>
        q.Select(r => new RemittanceRow(r.Id, r.FileName, r.PaymentDate, r.TotalPaid, r.Items.Count(),
            r.Items.Count(i => i.ExplanatoryCode != null || i.AmountPaid != i.AmountSubmitted), r.Items.Count(i => i.ClaimItemId == null),
            r.Simulated, r.ImportedUtc)).ToArrayAsync();
}
