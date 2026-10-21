using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Integrations;

/// <summary>
/// A patient's consent directives as the label an order or prescription carries to the application that does the work
/// (IntegrationHub's contract, flow 7): <c>meta.security</c> with the HL7 v3 Confidentiality code. The whole record
/// withheld is <c>V</c> (very restricted); one visit withheld, or the record withheld from a named provider, is
/// <c>R</c> (restricted). No active directive: no label. The application limits who may see the item and returns the
/// same label on its result.
/// </summary>
public static class ConfidentialityLabels
{
    public const string System = "http://terminology.hl7.org/CodeSystem/v3-Confidentiality";

    public static async Task<string?> ForAsync(ClinicHubDbContext db, int patientId, int? encounterId, CancellationToken ct = default)
    {
        var active = await db.ConsentDirectives.AsNoTracking()
            .Where(d => d.PatientId == patientId && d.RevokedUtc == null)
            .Select(d => new { d.Scope, d.EncounterId })
            .ToListAsync(ct);
        if (active.Any(d => d.Scope == ConsentScopes.EntireRecord))
            return "V";
        if (active.Any(d => d.Scope == ConsentScopes.Provider || (d.Scope == ConsentScopes.Encounter && d.EncounterId == encounterId)))
            return "R";
        return null;
    }

    /// <summary>The resource's <c>meta</c> with the label, or null for none.</summary>
    public static JsonObject? Meta(string? code) => code is null ? null : new JsonObject
    {
        ["security"] = new JsonArray(new JsonObject
        {
            ["system"] = System, ["code"] = code, ["display"] = code == "V" ? "very restricted" : "restricted",
        }),
    };
}
