using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Domain;

/// <summary>
/// What one person may see of one patient's clinical record right now, given the patient's PHIPA consent
/// directives ("lockbox") and that person's emergency overrides. Directives withhold visits, the clinical part of
/// the record (notes, vitals, diagnoses, orders, results); who the patient is, their appointments and their billing
/// are not withheld, since the clinic needs them to book, check in and be paid.
///
/// <list type="bullet">
/// <item><b>Entire record:</b> each visit only for the provider who saw the patient, and for the team while the
/// visit is still open (rooming, vitals); past visits are hidden from everyone else.</item>
/// <item><b>One visit:</b> that visit only for its provider.</item>
/// <item><b>One provider:</b> the whole record hidden from that provider, except the visits they saw themselves.</item>
/// </list>
/// A clinician with <c>consent.override</c> can open the record in an emergency, giving a reason: for an hour,
/// logged, and listed for the privacy officer.
/// </summary>
public sealed class ConsentView
{
    public static readonly TimeSpan OverrideLength = TimeSpan.FromHours(1);

    public int? ProviderId { get; private init; }
    public IReadOnlyList<ConsentDirective> Directives { get; private init; } = [];
    public IReadOnlyList<ConsentOverride> Overrides { get; private init; } = [];

    public record Decision(bool Withheld, int? OverrideId);

    public static readonly ConsentView None = new();

    /// <summary>A view from given rows (tests, and callers that already loaded them).</summary>
    public static ConsentView Of(int? providerId, IReadOnlyList<ConsentDirective> directives, IReadOnlyList<ConsentOverride> overrides) =>
        new() { ProviderId = providerId, Directives = directives, Overrides = overrides };

    public static async Task<ConsentView> LoadAsync(ClinicHubDbContext db, int patientId, ClaimsPrincipal user, DateTime now)
    {
        var directives = await db.ConsentDirectives.AsNoTracking().Where(d => d.PatientId == patientId && d.RevokedUtc == null).ToListAsync();
        if (directives.Count == 0)
            return None;
        var userId = user.RequiredUserId();
        var ids = directives.Select(d => d.Id).ToList();
        return new ConsentView
        {
            ProviderId = int.TryParse(user.FindFirstValue(AppClaims.ProviderId), out var p) ? p : null,
            Directives = directives,
            Overrides = await db.ConsentOverrides.AsNoTracking()
                .Where(o => ids.Contains(o.ConsentDirectiveId) && o.UserId == userId && o.ExpiresUtc > now).ToListAsync(),
        };
    }

    /// <summary>May this person see the visit? If only thanks to an override, which one (for the access log).</summary>
    public Decision Decide(int encounterId, int encounterProviderId, string encounterStatus)
    {
        int? used = null;
        foreach (var d in Directives.Where(d => Applies(d, encounterId, encounterProviderId, encounterStatus)))
        {
            var o = Overrides.FirstOrDefault(o => o.ConsentDirectiveId == d.Id);
            if (o is null)
                return new(true, null);
            used ??= o.Id;
        }
        return new(false, used);
    }

    public bool HasDirectives => Directives.Count > 0;

    private bool Applies(ConsentDirective d, int encounterId, int encounterProviderId, string status)
    {
        var ownVisit = ProviderId == encounterProviderId;
        return d.Scope switch
        {
            ConsentScopes.Encounter => d.EncounterId == encounterId && !ownVisit,
            ConsentScopes.Provider => d.ExcludedProviderId is { } excluded && excluded == ProviderId && !ownVisit,
            _ => !ownVisit && status != EncounterStatus.Open,
        };
    }
}
