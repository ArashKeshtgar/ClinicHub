using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;
using ClinicHub.Api.Integrations;
using ClinicHub.Api.Sso;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// Results coming back: LabFlow and ClinicReport post FHIR DiagnosticReports (with their Observations) to /fhir,
/// each naming the ClinicHub order it answers in basedOn. They land in the ordering clinician's results inbox,
/// where someone with results.review acknowledges them.
/// </summary>
public static class ResultEndpoints
{
    public const string ResultsPolicy = "results.post";

    public record ResultRow(int Id, DateTime ReceivedUtc, string Status, bool IsAbnormal, string? Summary, string? ExternalUrl,
        int OrderId, string Order, string Target, int PatientId, string Patient, string Mrn, int EncounterId, string Requester,
        DateTime? ReviewedUtc, string? ReviewedBy, bool Withheld = false);

    public static void AddResultsPolicy(this IServiceCollection services) =>
        services.AddAuthorizationBuilder().AddPolicy(ResultsPolicy, p => p
            .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ctx.User.HasScope(SsoClaims.ResultsScope)));

    public static void MapResultEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/fhir", Receive).RequireAuthorization(ResultsPolicy).ExcludeFromDescription();

        // The inbox: results not yet acknowledged, for the signed-in clinician's orders (or the whole clinic's).
        app.MapGet("/api/results", async (bool? all, bool? reviewed, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock) =>
        {
            int? providerId = int.TryParse(user.FindFirstValue(AppClaims.ProviderId), out var p) ? p : null;
            var q = db.DiagnosticResults.AsNoTracking().AsQueryable();
            if (all != true && providerId is not null)
                q = q.Where(r => r.ServiceRequest.RequesterProviderId == providerId);
            q = reviewed == true ? q.Where(r => r.ReviewedUtc != null) : q.Where(r => r.ReviewedUtc == null);
            var rows = await q.OrderByDescending(r => r.IsAbnormal).ThenByDescending(r => r.ReceivedUtc).Take(200).Select(Row(db)).ToListAsync();
            return await HideWithheld(db, rows, user, clock.GetUtcNow().UtcDateTime);
        }).WithTags("Results").RequireAuthorization(Permissions.ResultsReview);

        app.MapPost("/api/results/{id:int}/review", async (int id, ClaimsPrincipal user, ClinicHubDbContext db, TimeProvider clock, HttpContext http) =>
        {
            var result = await db.DiagnosticResults.Include(r => r.ServiceRequest).SingleOrDefaultAsync(r => r.Id == id);
            if (result is null)
                return Results.NotFound();
            var now = clock.GetUtcNow().UtcDateTime;
            var visit = await db.Encounters.AsNoTracking().Where(e => e.Id == result.ServiceRequest.EncounterId).Select(e => new { e.ProviderId, e.Status }).SingleAsync();
            var decision = (await ConsentView.LoadAsync(db, result.ServiceRequest.PatientId, user, now)).Decide(result.ServiceRequest.EncounterId, visit.ProviderId, visit.Status);
            if (decision.Withheld)
                return Results.Problem("Withheld at the patient's request (PHIPA consent directive).", statusCode: StatusCodes.Status403Forbidden);
            result.ReviewedByUserId = user.RequiredUserId();
            result.ReviewedUtc = now;
            db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = result.ServiceRequest.PatientId,
                Action = AccessActions.Edit, Context = $"Acknowledged result {id}", ConsentOverrideId = decision.OverrideId, IpAddress = AuthEndpoints.Ip(http) });
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).WithTags("Results").RequireAuthorization(Permissions.ResultsReview);
    }

    /// <summary>A result from a visit the patient withheld from this person shows only that it exists.</summary>
    private static async Task<List<ResultRow>> HideWithheld(ClinicHubDbContext db, List<ResultRow> rows, ClaimsPrincipal user, DateTime now)
    {
        var patients = rows.Select(r => r.PatientId).Distinct().ToList();
        var guarded = await db.ConsentDirectives.Where(d => patients.Contains(d.PatientId) && d.RevokedUtc == null).Select(d => d.PatientId).Distinct().ToListAsync();
        if (guarded.Count == 0)
            return rows;
        var encounterIds = rows.Where(r => guarded.Contains(r.PatientId)).Select(r => r.EncounterId).Distinct().ToList();
        var visits = await db.Encounters.AsNoTracking().Where(e => encounterIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => new { e.ProviderId, e.Status });
        var views = new Dictionary<int, ConsentView>();
        foreach (var p in guarded)
            views[p] = await ConsentView.LoadAsync(db, p, user, now);
        return rows.Select(r => views.TryGetValue(r.PatientId, out var v) && v.Decide(r.EncounterId, visits[r.EncounterId].ProviderId, visits[r.EncounterId].Status).Withheld
            ? r with { Summary = null, ExternalUrl = null, IsAbnormal = false, Withheld = true }
            : r).ToList();
    }

    private static System.Linq.Expressions.Expression<Func<DiagnosticResult, ResultRow>> Row(ClinicHubDbContext db) => r => new ResultRow(
        r.Id, r.ReceivedUtc, r.Status, r.IsAbnormal, r.Summary, r.ExternalUrl, r.ServiceRequestId, r.ServiceRequest.Display, r.ServiceRequest.TargetSystem,
        r.ServiceRequest.PatientId, r.ServiceRequest.Patient.LastName + ", " + r.ServiceRequest.Patient.FirstName, r.ServiceRequest.Patient.Mrn,
        r.ServiceRequest.EncounterId, r.ServiceRequest.RequesterProvider.LastName, r.ReviewedUtc,
        db.Users.Where(u => u.Id == r.ReviewedByUserId).Select(u => u.DisplayName).FirstOrDefault());

    /// <summary>
    /// A Bundle with one or more DiagnosticReports and their Observations. Each report names its order
    /// (urn:clinichub:{clinic}:service-request), which picks the clinic's database; the sender must be the
    /// application the order went to. A report sent again replaces the earlier version and needs acknowledging again.
    /// </summary>
    private static async Task<IResult> Receive(HttpRequest request, ClaimsPrincipal sender, CatalogDbContext catalog, ClinicDatabases databases, TimeProvider clock)
    {
        JsonNode? bundle;
        try { bundle = await JsonNode.ParseAsync(request.Body); }
        catch (System.Text.Json.JsonException) { return Outcome("The body is not JSON."); }
        var resources = (string?)bundle?["resourceType"] == "Bundle"
            ? bundle["entry"]?.AsArray().Select(e => (Url: (string?)e?["fullUrl"], Resource: e?["resource"])).ToList() ?? []
            : [(null, bundle)];
        var reports = resources.Where(r => (string?)r.Resource?["resourceType"] == "DiagnosticReport").ToList();
        if (reports.Count == 0)
            return Outcome("Expected DiagnosticReports (alone or in a Bundle).");

        var client = sender.GetClaim(OpenIddictConstants.Claims.ClientId) ?? sender.GetClaim(OpenIddictConstants.Claims.Subject);
        var target = client switch { "labflow" => TargetSystems.LabFlow, "clinicreport" => TargetSystems.ClinicReport, _ => null };
        if (target is null)
            return Outcome($"{client} does not send results to ClinicHub.", StatusCodes.Status403Forbidden);

        var now = clock.GetUtcNow().UtcDateTime;
        var locations = new List<string>();
        foreach (var (_, report) in reports)
        {
            var basedOn = report!["basedOn"]?.AsArray().Select(b => b?["identifier"]).FirstOrDefault(i => ((string?)i?["system"])?.EndsWith(":service-request") == true);
            var system = (string?)basedOn?["system"] ?? "";
            var parts = system.Split(':');
            if (parts is not ["urn", "clinichub", var slug, "service-request"] || !int.TryParse((string?)basedOn?["value"], out var orderId))
                return Outcome("Each DiagnosticReport needs basedOn.identifier with ClinicHub's order (urn:clinichub:{clinic}:service-request).");
            var clinic = await catalog.Clinics.AsNoTracking().SingleOrDefaultAsync(c => c.Slug == slug && c.IsActive);
            if (clinic is null)
                return Outcome($"No clinic '{slug}'.");

            await using var db = databases.Open(clinic);
            var order = await db.ServiceRequests.SingleOrDefaultAsync(s => s.Id == orderId);
            if (order is null || order.TargetSystem != target)
                return Outcome($"Order {orderId} at {slug} was not sent to {client}.", StatusCodes.Status403Forbidden);

            var externalId = (string?)report["identifier"]?[0]?["value"] ?? $"{orderId}";
            var status = ((string?)report["status"] ?? "final") switch
            {
                "preliminary" or "partial" or "registered" => "Preliminary",
                "amended" or "corrected" or "appended" => "Amended",
                "cancelled" or "entered-in-error" => "Cancelled",
                _ => "Final",
            };
            var observations = (report["result"]?.AsArray() ?? []).Select(r => (string?)r?["reference"])
                .Select(reference => resources.FirstOrDefault(x => x.Url == reference).Resource).Where(o => o is not null).ToList();
            var lines = observations.Select(o => Line(o!)).ToList();
            var abnormal = observations.Any(o => ((string?)o!["interpretation"]?[0]?["coding"]?[0]?["code"]) is { } code && code != "N");
            var conclusion = (string?)report["conclusion"];
            var summary = string.Join("\n", new[] { conclusion }.Concat(lines).Where(s => !string.IsNullOrWhiteSpace(s)));

            var result = await db.DiagnosticResults.SingleOrDefaultAsync(r => r.ServiceRequestId == orderId && r.ExternalReportId == externalId);
            if (result is null)
                db.DiagnosticResults.Add(result = new DiagnosticResult { ServiceRequestId = orderId, ExternalReportId = externalId });
            else if (result.Summary != summary || result.Status != status)
            {
                // A new version: whoever acknowledged the old one has not seen this one.
                result.ReviewedByUserId = null;
                result.ReviewedUtc = null;
            }
            result.ReceivedUtc = now;
            result.Status = status;
            result.Summary = summary.Length > 0 ? summary : null;
            result.IsAbnormal = abnormal;
            result.ExternalUrl = (string?)report["presentedForm"]?[0]?["url"];
            order.Status = status switch
            {
                "Preliminary" => ServiceRequestStatus.InProgress,
                "Cancelled" => ServiceRequestStatus.Cancelled,
                _ => ServiceRequestStatus.Completed,
            };
            if (order.Status == ServiceRequestStatus.Completed)
                order.CompletedUtc ??= now;
            db.Inbox.Add(new InboundMessage { Source = target, Kind = "DiagnosticReport", PayloadJson = report.ToJsonString(), ReceivedUtc = now, ProcessedUtc = now });
            await db.SaveChangesAsync();
            locations.Add($"DiagnosticReport/{result.Id}");
        }

        var response = new JsonArray(locations.Select(l => (JsonNode)new JsonObject { ["response"] = new JsonObject { ["status"] = "201 Created", ["location"] = l } }).ToArray());
        return Results.Text(new JsonObject { ["resourceType"] = "Bundle", ["type"] = "transaction-response", ["entry"] = response }.ToJsonString(), "application/fhir+json");
    }

    /// <summary>"Hemoglobin: 112 g/L (L) [135 - 175]" from an Observation.</summary>
    private static string Line(JsonNode o)
    {
        var name = (string?)o["code"]?["text"] ?? (string?)o["code"]?["coding"]?[0]?["display"] ?? (string?)o["code"]?["coding"]?[0]?["code"] ?? "Result";
        var value = o["valueQuantity"] is { } q
            ? $"{(decimal?)q["value"]?.GetValue<decimal>():0.##} {(string?)q["unit"]}".Trim()
            : (string?)o["valueString"] ?? "";
        var flag = (string?)o["interpretation"]?[0]?["coding"]?[0]?["code"] is { } f && f != "N" ? $" ({f})" : "";
        var range = (string?)o["referenceRange"]?[0]?["text"] is { Length: > 0 } r ? $" [{r}]" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{name}: {value}{flag}{range}");
    }

    private static IResult Outcome(string diagnostics, int status = StatusCodes.Status400BadRequest) => Results.Text(new JsonObject
    {
        ["resourceType"] = "OperationOutcome",
        ["issue"] = new JsonArray(new JsonObject { ["severity"] = "error", ["code"] = status == 403 ? "forbidden" : "invalid", ["diagnostics"] = diagnostics }),
    }.ToJsonString(), "application/fhir+json", statusCode: status);
}
