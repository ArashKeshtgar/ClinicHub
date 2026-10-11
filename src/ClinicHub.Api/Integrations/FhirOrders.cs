using System.Security.Claims;
using Claim = System.Security.Claims.Claim;
using System.Text.Json.Nodes;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using ClinicHub.Api.Sso;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

namespace ClinicHub.Api.Integrations;

public class IntegrationOptions
{
    public class Target
    {
        /// <summary>The application's FHIR endpoint, e.g. http://localhost:5180/fhir. Empty: orders wait until it is set.</summary>
        public string? FhirBaseUrl { get; set; }
    }

    public Target LabFlow { get; set; } = new();
    public Target ClinicReport { get; set; } = new();

    /// <summary>How often the outbox is looked at. 0 turns the background worker off (tests send on demand).</summary>
    public int PollSeconds { get; set; } = 5;

    public Target For(string system) => system == TargetSystems.LabFlow ? LabFlow : ClinicReport;

    /// <summary>The application's client id in ClinicHub, which is also the audience of the tokens sent to it.</summary>
    public static string Audience(string system) => system == TargetSystems.LabFlow ? "labflow" : "clinicreport";
}

/// <summary>
/// Short-lived tokens with which ClinicHub itself calls an application's FHIR endpoint. They are signed with the
/// sign-in server's key, so the application checks them against ClinicHub's published keys (/.well-known/jwks)
/// like any token from ClinicHub. They name the clinic, which picks the clinic's database on the other side.
/// </summary>
public class ServiceTokens(IOptionsMonitor<OpenIddictServerOptions> server, IOptions<SsoOptions> sso, TimeProvider clock)
{
    public const string OrdersScope = "orders";

    public string Create(string audience, Clinic clinic)
    {
        var issuer = sso.Value.Issuer ?? server.CurrentValue.Issuer?.AbsoluteUri
            ?? throw new InvalidOperationException("Set Sso:Issuer: tokens ClinicHub sends on its own need its public address.");
        var now = clock.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = server.CurrentValue.SigningCredentials.First(),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "clinichub"), new Claim("scope", OrdersScope),
                new Claim(SsoClaims.ClinicSlug, clinic.Slug), new Claim(SsoClaims.ClinicName, clinic.Name),
            ]),
        });
    }
}

/// <summary>An order sent to one application as one FHIR R4 transaction Bundle: the patient, the requester and the ServiceRequests.</summary>
public static class FhirOrderBundle
{
    public static JsonObject Build(Clinic clinic, Patient patient, Provider requester, IReadOnlyList<ServiceRequest> orders, DateTime nowUtc)
    {
        var requisition = $"R{orders.Min(o => o.Id)}";
        var patientIds = new JsonArray(Identifier(FhirSystems.Clinic(clinic.Slug, "mrn"), patient.Mrn));
        if (patient.HealthCardNumber is { } hcn)
        {
            var card = Identifier(FhirSystems.OntarioHealthCard, hcn);
            if (patient.HealthCardVersion is { } version)
                card["extension"] = new JsonArray(new JsonObject
                {
                    // ClinicHub's own extension for the card's version code; confirm the provincial profile's before production.
                    ["url"] = "urn:clinichub:extension:health-card-version", ["valueString"] = version,
                });
            patientIds.Add(card);
        }
        var practitionerIds = new JsonArray();
        if (requester.RegistrationNumber is { } reg)
            practitionerIds.Add(Identifier(FhirSystems.Cpso, reg));
        if (requester.OhipBillingNumber is { } billing)
            practitionerIds.Add(Identifier(FhirSystems.OhipBillingNumber, billing));

        var entries = new JsonArray
        {
            Entry("urn:uuid:patient", new JsonObject
            {
                ["resourceType"] = "Patient", ["identifier"] = patientIds,
                ["name"] = new JsonArray(new JsonObject { ["family"] = patient.LastName, ["given"] = new JsonArray(patient.FirstName) }),
                ["gender"] = patient.Sex switch { "M" => "male", "F" => "female", "X" => "other", _ => "unknown" },
                ["birthDate"] = patient.DateOfBirth.ToString("yyyy-MM-dd"),
            }),
            Entry("urn:uuid:requester", new JsonObject
            {
                ["resourceType"] = "Practitioner", ["identifier"] = practitionerIds,
                ["name"] = new JsonArray(new JsonObject
                {
                    ["family"] = requester.LastName, ["given"] = new JsonArray(requester.FirstName),
                    ["prefix"] = requester.Title is { } t ? new JsonArray(t) : null,
                }),
            }),
        };
        foreach (var o in orders)
        {
            var item = OrderCatalog.Find(o.TargetSystem, o.Code)!;
            entries.Add(Entry($"urn:uuid:order-{o.Id}", new JsonObject
            {
                ["resourceType"] = "ServiceRequest",
                ["identifier"] = new JsonArray(Identifier(FhirSystems.Clinic(clinic.Slug, "service-request"), o.Id.ToString())),
                ["requisition"] = Identifier(FhirSystems.Clinic(clinic.Slug, "requisition"), requisition),
                ["status"] = "active", ["intent"] = "order", ["priority"] = o.Priority == "Stat" ? "stat" : "routine",
                ["code"] = new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject { ["system"] = item.System, ["code"] = item.Code, ["display"] = item.Display }),
                    ["text"] = item.Display,
                },
                ["subject"] = new JsonObject { ["reference"] = "urn:uuid:patient" },
                ["requester"] = new JsonObject { ["reference"] = "urn:uuid:requester", ["display"] = requester.DisplayName },
                ["authoredOn"] = o.CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["note"] = o.ClinicalNotes is { } n ? new JsonArray(new JsonObject { ["text"] = n }) : null,
            }));
        }
        return new JsonObject
        {
            ["resourceType"] = "Bundle", ["type"] = "transaction",
            ["identifier"] = Identifier(FhirSystems.Clinic(clinic.Slug, "requisition"), requisition),
            ["timestamp"] = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["entry"] = entries,
        };
    }

    private static JsonObject Identifier(string system, string value) => new() { ["system"] = system, ["value"] = value };

    private static JsonObject Entry(string fullUrl, JsonObject resource) => new()
    {
        ["fullUrl"] = fullUrl, ["resource"] = resource,
        ["request"] = new JsonObject { ["method"] = "POST", ["url"] = (string)resource["resourceType"]! },
    };
}
