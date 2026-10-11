using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicHub.Api.Integrations;

/// <summary>
/// Delivers the FHIR messages each clinic's database has waiting in its outbox. An order is saved together with
/// its message, so nothing is lost if an application is down: the message waits and is retried with growing
/// pauses (10 s, 20 s, 40 s… up to 30 min). After <see cref="MaxAttempts"/> failures the order is marked Failed.
/// A cancellation goes the same way; the application may refuse it (work already done), and then the order
/// carries on with the application's answer as its note.
/// </summary>
public class OutboxDispatcher(IServiceScopeFactory scopes, IHttpClientFactory http, IOptionsMonitor<IntegrationOptions> options,
    TimeProvider clock, ILogger<OutboxDispatcher> logger, FinanceFeed finance)
{
    public const string HttpClientName = "fhir";
    public const int MaxAttempts = 12;

    /// <summary>One pass over every active clinic. Returns how many messages were delivered.</summary>
    public async Task<int> DispatchAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var databases = scope.ServiceProvider.GetRequiredService<ClinicDatabases>();
        var tokens = scope.ServiceProvider.GetRequiredService<ServiceTokens>();
        var delivered = 0;
        foreach (var clinic in await catalog.Clinics.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct))
        {
            await using var db = databases.Open(clinic);
            var now = clock.GetUtcNow().UtcDateTime;
            var due = await db.Outbox.Where(m => m.SentUtc == null && m.Attempts < MaxAttempts && (m.NextAttemptUtc == null || m.NextAttemptUtc <= now))
                .OrderBy(m => m.Id).Take(20).ToListAsync(ct);
            foreach (var message in due)
                if (await SendAsync(db, clinic, message, tokens, ct))
                    delivered++;
            // The clinic's money to the group's accounting, on the same pass.
            await finance.SendAsync(db, clinic, tokens, ct);
        }
        return delivered;
    }

    private async Task<bool> SendAsync(ClinicHubDbContext db, Clinic clinic, OutboxMessage message, ServiceTokens tokens, CancellationToken ct)
    {
        var cancel = message.Kind == OutboxKinds.Cancel;
        var orders = cancel
            ? await db.ServiceRequests.Include(s => s.Results).Where(s => s.Id == message.ServiceRequestId).ToListAsync(ct)
            : await db.ServiceRequests.Where(s => s.OutboxMessageId == message.Id).OrderBy(s => s.Id).ToListAsync(ct);
        var target = options.CurrentValue.For(message.Destination);
        var now = clock.GetUtcNow().UtcDateTime;
        message.Attempts++;
        try
        {
            if (string.IsNullOrWhiteSpace(target.FhirBaseUrl))
                throw new InvalidOperationException($"{message.Destination} is not connected (Integrations:{message.Destination}:FhirBaseUrl).");
            using var request = new HttpRequestMessage(HttpMethod.Post, target.FhirBaseUrl.TrimEnd('/'))
            {
                Content = new StringContent(message.PayloadJson, Encoding.UTF8, "application/fhir+json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Create(IntegrationOptions.Audience(message.Destination), clinic));
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (cancel && (int)response.StatusCode is 400 or 404 or 409 or 422)
            {
                // An answer, not a failure: the application will not cancel (e.g. the specimen is already resulted).
                foreach (var o in orders)
                    Refused(o, $"{message.Destination} did not cancel: {Problem(body)}");
                message.SentUtc = now;
                message.LastError = Problem(body);
                message.NextAttemptUtc = null;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("{Destination} refused to cancel order {Id} for {Clinic}", message.Destination, message.ServiceRequestId, clinic.Slug);
                return true;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode}: {Problem(body)}");


            if (cancel)
                foreach (var o in orders)
                {
                    o.Status = ServiceRequestStatus.Cancelled;
                    o.CancelledUtc = now;
                }
            else
            {
                // A transaction-response lists one entry per request entry, in order; the orders come after the patient and requester.
                var locations = (JsonNode.Parse(body)?["entry"]?.AsArray() ?? []).Select(e => (string?)e?["response"]?["location"]).ToList();
                for (var i = 0; i < orders.Count; i++)
                {
                    orders[i].Status = ServiceRequestStatus.Accepted;
                    orders[i].SentUtc = now;
                    orders[i].ExternalId = locations.ElementAtOrDefault(i + 2)?.Split('/').ElementAtOrDefault(1);
                }
            }
            message.SentUtc = now;
            message.LastError = null;
            message.NextAttemptUtc = null;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Delivered {Kind} {Id} for {Clinic} to {Destination}", message.Kind, message.Id, clinic.Slug, message.Destination);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            message.NextAttemptUtc = now.AddSeconds(Math.Min(10 * Math.Pow(2, message.Attempts - 1), 1800));
            if (message.Attempts >= MaxAttempts)
                foreach (var o in orders)
                    if (cancel)
                        Refused(o, $"{message.Destination} could not be reached to cancel. Cancel again to retry.");
                    else
                        o.Status = ServiceRequestStatus.Failed;
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Could not deliver {Kind} {Id} for {Clinic} to {Destination} (attempt {Attempt}): {Error}",
                message.Kind, message.Id, clinic.Slug, message.Destination, message.Attempts, message.LastError);
            return false;
        }
    }

    /// <summary>The cancellation did not happen: the order goes on where it was.</summary>
    private static void Refused(ServiceRequest order, string note)
    {
        if (order.Status != ServiceRequestStatus.Cancelling)
            return;   // a result arrived meanwhile and moved it on
        order.Status = order.Results.Count > 0 ? ServiceRequestStatus.InProgress : ServiceRequestStatus.Accepted;
        order.CancelReason = null;
        order.StatusNote = note.Length > 500 ? note[..500] : note;
    }

    /// <summary>The useful part of an OperationOutcome or problem response.</summary>
    private static string Problem(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var issues = node?["issue"]?.AsArray().Select(i => (string?)i?["diagnostics"]).Where(d => d is not null);
            return issues?.Any() == true ? string.Join("; ", issues) : (string?)node?["title"] ?? body;
        }
        catch (System.Text.Json.JsonException)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }
}

/// <summary>Runs the dispatcher every Integrations:PollSeconds.</summary>
public class OutboxWorker(OutboxDispatcher dispatcher, IOptionsMonitor<IntegrationOptions> options, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && options.CurrentValue.PollSeconds > 0)
        {
            try
            {
                await dispatcher.DispatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The outbox pass failed; trying again on the next one.");
            }
            await Task.Delay(TimeSpan.FromSeconds(options.CurrentValue.PollSeconds), stoppingToken);
        }
    }
}
