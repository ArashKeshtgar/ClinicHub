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
/// order saves it with its outgoing message in one transaction; the outbox worker delivers the message. A test for an
/// external lab goes the same way through IntegrationHub (as HL7) when that route is set up; otherwise it is only
/// recorded, and either way its result comes back through IntegrationHub.
/// </summary>
public static class OrderEndpoints
{
    public record CatalogItem(string Target, string Code, string Display, string Group, bool Connected);
    public record OrderLine(string Target, string Code);
    public record PlaceOrders(OrderLine[] Items, string? Priority, string? Notes);
    public record CancelOrder(string? Reason);
    public record OrderResult(int Id, string Status, bool IsAbnormal, string? Summary, string? ExternalUrl, DateTime ReceivedUtc, DateTime? ReviewedUtc);
    public record OrderDto(int Id, string Target, string Code, string Display, string Priority, string Status, string? Notes,
        DateTime CreatedUtc, DateTime? SentUtc, string? ExternalId, string Requester, int Attempts, string? LastError, OrderResult? Result,
        string? CancelReason, string? StatusNote);

    /// <summary>Said on every external-lab order, so nobody takes "recorded" for "sent".</summary>
    public const string ExternalLabNote = "Recorded in ClinicHub only: the lab is not sent the order from here. Its result is filed when it comes back.";

    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/orders/catalog", (IOptionsMonitor<IntegrationOptions> integrations) =>
            OrderCatalog.All.Select(i => new CatalogItem(i.Target, i.Code, i.Display, i.Group,
                // Nothing to connect for an external lab: ClinicHub sends it nothing.
                i.Target == TargetSystems.ExternalLab || !string.IsNullOrWhiteSpace(integrations.CurrentValue.For(i.Target).FhirBaseUrl))))
            .WithTags("Orders").RequireAuthorization(Permissions.EncountersView);

        app.MapGet("/api/encounters/{id:int}/orders", async (int id, ClinicHubDbContext db) => await Orders(db, id))
            .WithTags("Orders").RequireAuthorization(Permissions.EncountersView).AddEndpointFilter(ConsentEndpoints.EncounterFilter);

        app.MapPost("/api/encounters/{id:int}/orders", async (int id, PlaceOrders req, ClaimsPrincipal user, ClinicHubDbContext db,
            CatalogDbContext catalog, TimeProvider clock, HttpContext http, IOptionsMonitor<IntegrationOptions> integrations) =>
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
                    if (group.Key == TargetSystems.ExternalLab && string.IsNullOrWhiteSpace(integrations.CurrentValue.ExternalLab.FhirBaseUrl))
                    {
                        // No route to the lab is set up: recorded only, no message to send. The lab's result comes back through
                        // IntegrationHub and finds the order by its number. With Integrations:ExternalLab:FhirBaseUrl set, it goes
                        // through the outbox like any other order.
                        var recorded = group.Select(i => new ServiceRequest
                        {
                            EncounterId = encounter.Id, PatientId = encounter.PatientId, RequesterProviderId = providerId, TargetSystem = group.Key,
                            Code = i!.Code, Display = i.Display, Priority = priority, ClinicalNotes = notes, Status = ServiceRequestStatus.AwaitingResult,
                            StatusNote = ExternalLabNote, CreatedUtc = now,
                        }).ToList();
                        db.ServiceRequests.AddRange(recorded);
                        placed.AddRange(recorded);
                        continue;
                    }
                    var message = new OutboxMessage { Destination = group.Key, Kind = OutboxKinds.Order, CreatedUtc = now };
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
        }).WithTags("Orders").RequireAuthorization(Permissions.OrdersManage).AddEndpointFilter(ConsentEndpoints.EncounterFilter);

        // Withdrawing an order. Not delivered yet: it is taken out of its message here. Delivered: the application is
        // asked, and it decides (a lab that has resulted the specimen, a report already started, say no).
        app.MapPost("/api/orders/{id:int}/cancel", async (int id, CancelOrder req, ClaimsPrincipal user, ClinicHubDbContext db,
            CatalogDbContext catalog, TimeProvider clock, HttpContext http) =>
        {
            var order = await db.ServiceRequests.Include(s => s.Patient).Include(s => s.RequesterProvider).SingleOrDefaultAsync(s => s.Id == id);
            if (order is null)
                return Results.NotFound();
            var refusal = order.Status switch
            {
                ServiceRequestStatus.Cancelled => "This order is already cancelled.",
                ServiceRequestStatus.Cancelling => "This order is already being cancelled.",
                ServiceRequestStatus.Completed => "This order has its results; it can no longer be cancelled.",
                _ => null,
            };
            if (refusal is not null)
                return Results.Problem(refusal, statusCode: StatusCodes.Status409Conflict);
            var reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
            if (reason?.Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["At most 200 characters."] });

            var now = clock.GetUtcNow().UtcDateTime;
            var clinic = await catalog.Clinics.AsNoTracking().SingleAsync(c => c.Id == user.RequiredClinicId());
            var message = order.OutboxMessageId is { } m ? await db.Outbox.SingleOrDefaultAsync(x => x.Id == m) : null;
            order.CancelReason = reason;
            order.StatusNote = null;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                if (message is null || message.SentUtc is null)
                {
                    // Never reached the application: nothing to ask. The message goes on with the other orders, or not at all.
                    order.Status = ServiceRequestStatus.Cancelled;
                    order.CancelledUtc = now;
                    order.OutboxMessageId = null;
                    if (message is not null)
                    {
                        var rest = await db.ServiceRequests.Where(s => s.OutboxMessageId == message.Id && s.Id != order.Id).OrderBy(s => s.Id).ToListAsync();
                        if (rest.Count == 0)
                        {
                            await db.SaveChangesAsync();
                            db.Outbox.Remove(message);
                        }
                        else
                        {
                            message.ServiceRequestId = rest[0].Id;
                            message.PayloadJson = FhirOrderBundle.Build(clinic, order.Patient, order.RequesterProvider, rest, now).ToJsonString();
                        }
                    }
                }
                else
                {
                    order.Status = ServiceRequestStatus.Cancelling;
                    db.Outbox.Add(new OutboxMessage
                    {
                        Destination = order.TargetSystem, Kind = OutboxKinds.Cancel, ServiceRequestId = order.Id, CreatedUtc = now,
                        PayloadJson = FhirOrderBundle.Cancel(clinic, order.Patient, order, now).ToJsonString(),
                    });
                }
                db.PatientAccessLog.Add(new PatientAccessLog { AtUtc = now, UserId = user.RequiredUserId(), PatientId = order.PatientId,
                    Action = AccessActions.Edit, Context = $"Cancelled order {order.Id} ({order.Display})", IpAddress = AuthEndpoints.Ip(http) });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            });
            return Results.Ok(await Orders(db, order.EncounterId));
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
                // The message in flight: the cancellation while one is being sent, otherwise the order itself.
                db.Outbox.Where(m => s.Status == ServiceRequestStatus.Cancelling ? m.Kind == OutboxKinds.Cancel && m.ServiceRequestId == s.Id : m.Id == s.OutboxMessageId)
                    .OrderByDescending(m => m.Id).Select(m => m.Attempts).FirstOrDefault(),
                db.Outbox.Where(m => s.Status == ServiceRequestStatus.Cancelling ? m.Kind == OutboxKinds.Cancel && m.ServiceRequestId == s.Id : m.Id == s.OutboxMessageId)
                    .OrderByDescending(m => m.Id).Select(m => m.LastError).FirstOrDefault(),
                s.Results.OrderByDescending(r => r.ReceivedUtc)
                    .Select(r => new OrderResult(r.Id, r.Status, r.IsAbnormal, r.Summary, r.ExternalUrl, r.ReceivedUtc, r.ReviewedUtc)).FirstOrDefault(),
                s.CancelReason, s.StatusNote))
            .ToArrayAsync();
}
