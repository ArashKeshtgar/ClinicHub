using System.Text.Json.Nodes;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using static ClinicHub.Api.Integrations.FhirOrderBundle;

namespace ClinicHub.Api.Integrations;

/// <summary>
/// Electronic prescriptions as one FHIR R4 transaction Bundle for the pharmacy (docs of IntegrationHub, contracts flow 5):
/// the patient, the prescriber and one MedicationRequest per drug. The identifier of each is
/// <c>urn:clinichub:{clinic}:prescription|{id}</c>; the drugs written together share a
/// <c>groupIdentifier</c>. A Bundle sent again is the same prescription: the pharmacy keys on the identifiers.
/// </summary>
public static class FhirPrescriptionBundle
{
    private const string Utc = "yyyy-MM-ddTHH:mm:ssZ";

    public static JsonObject Build(Clinic clinic, Patient patient, Provider prescriber, IReadOnlyList<Prescription> prescriptions, DateTime nowUtc,
        string? label = null)
    {
        var group = Identifier(FhirSystems.Clinic(clinic.Slug, "prescription-group"), $"G{prescriptions.Min(p => p.GroupNumber)}");
        var entries = new JsonArray { PatientEntry(clinic, patient), RequesterEntry(prescriber) };
        foreach (var p in prescriptions)
        {
            var request = new JsonObject
            {
                ["resourceType"] = "MedicationRequest",
                ["meta"] = ConfidentialityLabels.Meta(label),
                ["identifier"] = new JsonArray(Identifier(FhirSystems.Clinic(clinic.Slug, "prescription"), p.Id.ToString())),
                ["groupIdentifier"] = group.DeepClone(),
                ["status"] = "active", ["intent"] = "order",
                ["medicationCodeableConcept"] = new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject { ["system"] = FhirSystems.Din, ["code"] = p.Din, ["display"] = p.Display }),
                    ["text"] = p.Display,
                },
                ["subject"] = new JsonObject { ["reference"] = "urn:uuid:patient" },
                ["authoredOn"] = p.CreatedUtc.ToString(Utc),
                ["requester"] = new JsonObject { ["reference"] = "urn:uuid:requester", ["display"] = prescriber.DisplayName },
                ["dosageInstruction"] = new JsonArray(new JsonObject { ["text"] = p.Directions }),
                ["dispenseRequest"] = DispenseRequest(p),
                ["substitution"] = new JsonObject { ["allowedBoolean"] = !p.NoSubstitution },
                ["note"] = p.Note is { } n ? new JsonArray(new JsonObject { ["text"] = n }) : null,
            };
            entries.Add(Entry($"urn:uuid:prescription-{p.Id}", request));
        }
        return new JsonObject
        {
            ["resourceType"] = "Bundle", ["type"] = "transaction", ["identifier"] = group,
            ["timestamp"] = nowUtc.ToString(Utc), ["entry"] = entries,
        };
    }

    private static JsonObject DispenseRequest(Prescription p)
    {
        var request = new JsonObject
        {
            ["quantity"] = new JsonObject { ["value"] = p.Quantity, ["unit"] = p.QuantityUnit },
            ["numberOfRepeatsAllowed"] = p.Repeats,
        };
        if (p.DaysSupply is { } days)
            request["expectedSupplyDuration"] = new JsonObject
            {
                ["value"] = days, ["unit"] = "days", ["system"] = "http://unitsofmeasure.org", ["code"] = "d",
            };
        return request;
    }

    /// <summary>
    /// Withdrawing a prescription: the MedicationRequest again, status "cancelled", as a conditional update on its
    /// identifier. The reason goes in a note (R4's MedicationRequest has a status reason, but the order cancel does the same).
    /// The pharmacy answers 409 when dispensing has started.
    /// </summary>
    public static JsonObject Cancel(Clinic clinic, Patient patient, Prescription prescription, DateTime nowUtc)
    {
        var system = FhirSystems.Clinic(clinic.Slug, "prescription");
        return new JsonObject
        {
            ["resourceType"] = "Bundle", ["type"] = "transaction", ["timestamp"] = nowUtc.ToString(Utc),
            ["entry"] = new JsonArray(new JsonObject
            {
                ["resource"] = new JsonObject
                {
                    ["resourceType"] = "MedicationRequest",
                    ["identifier"] = new JsonArray(Identifier(system, prescription.Id.ToString())),
                    ["status"] = "cancelled", ["intent"] = "order",
                    ["medicationCodeableConcept"] = new JsonObject
                    {
                        ["coding"] = new JsonArray(new JsonObject { ["system"] = FhirSystems.Din, ["code"] = prescription.Din, ["display"] = prescription.Display }),
                    },
                    ["subject"] = new JsonObject { ["identifier"] = Identifier(FhirSystems.Clinic(clinic.Slug, "mrn"), patient.Mrn) },
                    ["note"] = prescription.CancelReason is { } why ? new JsonArray(new JsonObject { ["text"] = why }) : null,
                },
                ["request"] = new JsonObject { ["method"] = "PUT", ["url"] = $"MedicationRequest?identifier={system}|{prescription.Id}" },
            }),
        };
    }
}
