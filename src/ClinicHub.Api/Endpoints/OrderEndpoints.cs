using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using ClinicHub.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// Orders from a visit: lab tests go to LabFlow, echo and ECG to ClinicReport, as FHIR ServiceRequests. Placing an
/// order saves it with its outgoing message in one transaction; the outbox worker delivers the message.
/// </summary>
public static class OrderEndpoints
{
    public record CatalogItem(string Target, string Code, string Display, string Group, bool Connected);
    public record OrderLine(string Target, string Code);
    public record PlaceOrders(OrderLine[] Items, string? Priority, string? Notes);
    public record OrderDto(int Id, string Target, string Code, string Display, string Priority, string Status, string? Notes,
        DateTime CreatedUtc, DateTime? SentUtc, string? ExternalId, string Requester, int Attempts, string? LastError);

    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/orders/catalog", (IOptionsMonitor<IntegrationOptions> integrations) =>
            OrderCatalog.All.Select(i => new CatalogItem(i.Target, i.Code, i.Display, i.Group,
                !string.IsNullOrWhiteSpace(integrations.CurrentValue.For(i.Target).FhirBaseUrl))))
            .WithTags("Orders").RequireAuthorization(Permissions.EncountersView);

        app.MapGet("/api/encounters/{id:int}/orders", async (int id, ClinicHubDbContext db) => await Orders(db, id))
            .WithTags("Orders").RequireAuthorization(Permissions.EncountersView);

        app.MapPost("/api/encounters/{id:int}/orders", async (int id, PlaceOrders req, ClaimsPrincipal user, ClinicHubDbContext db,
            CatalogDbContext catalog, TimeProvider clock, HttpContext http) =>
        {
            var encounter = await db.Encounters.Include(e => e.Patient).SingleOrDefaultAsync(e => e.Id == id);
            if (encounter is null)
                return Results.NotFound();
            if (encounter.Status == EncounterStatus.Voided)
                return Results.Problem("This visit was voided.", statusCode: StatusCodes.Status409Conflict);
            // Orders are signed by a clinician: the person ordering must be a provider here.
            if (!int.TryParse(user.FindFirstValue(AppClaims.ProviderId), out var providerId))
                return Results.Problem("Only a clinician with a provider record at this clinic can order.", statusCode: StatusCodes.Status403Forbidden);

            var errors = new Dictionary<string, string[]>();
            var lines = (req.Items ?? []).DistinctBy(l => (l.Target, l.Code)).ToList();
            var items = lines.Select(l => OrderCatalog.Find(l.Target, l.Code)).ToList();
            if (lines.Count == 0)
                errors["items"] = ["Choose at least one test or report."];
            else if (items.Any(i => i is null))
                errors["items"] = ["Unknown test or report."];
            var priority = req.Priority is "Stat" ? "Stat" : "Routine";
            var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
            if (notes?.Length > 500)
                errors["notes"] = ["At most 500 characters."];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var now = clock.GetUtcNow().UtcDateTime;
            var clinic = await catalog.Clinics.AsNoTracking().SingleAsync(c => c.Id == user.RequiredClinicId());
            var requester = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId);
            // One transaction, run by the database's retrying strategy (it repeats the whole unit on a transient failure).
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync();
                var placed = new List<ServiceRequest>();
                foreach (var group in items.GroupBy(i => i!.Target))
                {
                    var message = new OutboxMessage { Destination = group.Key, Kind = "ServiceRequest", CreatedUtc = now };
                    db.Outbox.Add(message);
                    await db.SaveChangesAsync();
                    var orders = group.Select(i => new ServiceRequest
                    {
                        EncounterId = encounter.Id, PatientId = encounter.PatientId, RequesterProviderId = providerId, TargetSystem = group.Key,
                        Code = i!.Code, Display = i.Display, Priority = priority, ClinicalNotes = notes, Status = ServiceRequestStatus.Draft,
                        CreatedUtc = now, OutboxMessageId = message.Id,
                    }).ToList();
                    db.ServiceRequests.AddRange(orders);
                    await db.SaveChangesAsync();
                    // The message is written now, with the order, so what was ordered is what gets sent.
                    message.ServiceRequestId = orders[0].Id;
                    message.PayloadJson = FhirOrderBundle.Build(clinic, encounter.Patient, requester, orders, now).ToJsonString();
                    placed.AddRange(orders);
                }
                db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = encounter.PatientId,
                    Action = AccessActions.Edit, Context = $"Ordered {placed.Count} from encounter {id}", IpAddress = AuthEndpoints.Ip(http) });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            });
            return Results.Ok(await Orders(db, id));
        }).WithTags("Orders").RequireAuthorization(Permissions.OrdersManage);

        // After a failure the order can be tried again, e.g. once the application is back.
        app.MapPost("/api/orders/{id:int}/resend", async (int id, ClinicHubDbContext db) =>
        {
            var order = await db.ServiceRequests.SingleOrDefaultAsync(s => s.Id == id);
            var message = order?.OutboxMessageId is { } m ? await db.Outbox.SingleOrDefaultAsync(x => x.Id == m) : null;
            if (order is null || message is null)
                return Results.NotFound();
            if (message.SentUtc is not null)
                return Results.Problem("This order was already delivered.", statusCode: StatusCodes.Status409Conflict);
            message.Attempts = 0;
            message.NextAttemptUtc = null;
            foreach (var o in await db.ServiceRequests.Where(s => s.OutboxMessageId == message.Id).ToListAsync())
                o.Status = ServiceRequestStatus.Draft;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).WithTags("Orders").RequireAuthorization(Permissions.OrdersManage);
    }

    private static async Task<OrderDto[]> Orders(ClinicHubDbContext db, int encounterId) =>
        await db.ServiceRequests.AsNoTracking().Where(s => s.EncounterId == encounterId).OrderByDescending(s => s.Id)
            .Select(s => new OrderDto(s.Id, s.TargetSystem, s.Code, s.Display, s.Priority, s.Status, s.ClinicalNotes, s.CreatedUtc, s.SentUtc,
                s.ExternalId, s.RequesterProvider.LastName,
                db.Outbox.Where(m => m.Id == s.OutboxMessageId).Select(m => m.Attempts).FirstOrDefault(),
                db.Outbox.Where(m => m.Id == s.OutboxMessageId).Select(m => m.LastError).FirstOrDefault()))
            .ToArrayAsync();
}
