using ClinicHub.Api.Data;

namespace ClinicHub.Api.Integrations;

/// <summary>Identifier and code systems used in the FHIR messages between ClinicHub, LabFlow and ClinicReport.</summary>
public static class FhirSystems
{
    public const string Loinc = "http://loinc.org";

    /// <summary>LabFlow's own test codes, for panels that have no single LOINC code in its catalog (CBC, lipids, electrolytes).</summary>
    public const string LabFlowTest = "urn:labflow:test-code";

    /// <summary>ClinicReport's template keys (echo-tte, ecg-12-lead).</summary>
    public const string ClinicReportTemplate = "urn:clinicreport:template";

    /// <summary>Ontario health card number, as named in the Canadian FHIR baseline (Canada Health Infoway).</summary>
    public const string OntarioHealthCard = "https://fhir.infoway-inforoute.ca/NamingSystem/ca-on-patient-hcn";

    /// <summary>College of Physicians and Surgeons of Ontario registration number (Canadian FHIR baseline).</summary>
    public const string Cpso = "https://fhir.infoway-inforoute.ca/NamingSystem/ca-on-license-physician";

    /// <summary>Health Canada's Drug Identification Number, as HL7 registers its naming system.</summary>
    public const string Din = "http://hl7.org/fhir/NamingSystem/ca-hc-din";

    /// <summary>OHIP billing number. ClinicHub's own system URI: confirm against the provincial naming system before production.</summary>
    public const string OhipBillingNumber = "urn:clinichub:ohip-billing-number";

    /// <summary>A clinic's own identifiers: its MRNs, its orders, its requisitions.</summary>
    public static string Clinic(string slug, string kind) => $"urn:clinichub:{slug}:{kind}";
}

/// <summary>What a clinician can order from ClinicHub, and where each order goes.</summary>
public static class OrderCatalog
{
    public record Item(string Target, string System, string Code, string Display, string Group);

    public static readonly Item[] All =
    [
        Lab(FhirSystems.LabFlowTest, "CBC", "Complete blood count", "Hematology"),
        Lab(FhirSystems.Loinc, "4548-4", "Hemoglobin A1c", "Diabetes"),
        Lab(FhirSystems.Loinc, "14771-0", "Glucose, fasting", "Diabetes"),
        Lab(FhirSystems.LabFlowTest, "LIPID", "Lipid profile", "Chemistry"),
        Lab(FhirSystems.LabFlowTest, "LYTES", "Electrolytes (sodium, potassium)", "Chemistry"),
        Lab(FhirSystems.Loinc, "14682-9", "Creatinine", "Chemistry"),
        Lab(FhirSystems.Loinc, "1742-6", "ALT", "Chemistry"),
        Lab(FhirSystems.Loinc, "2276-4", "Ferritin", "Chemistry"),
        Lab(FhirSystems.Loinc, "14635-7", "25-hydroxyvitamin D", "Chemistry"),
        Lab(FhirSystems.Loinc, "3016-3", "TSH", "Endocrine"),
        Lab(FhirSystems.Loinc, "2857-1", "PSA", "Chemistry"),
        new(TargetSystems.ClinicReport, FhirSystems.ClinicReportTemplate, "echo-tte", "Transthoracic echocardiogram", "Cardiology"),
        new(TargetSystems.ClinicReport, FhirSystems.ClinicReportTemplate, "ecg-12-lead", "12-lead ECG", "Cardiology"),
    ];

    private static Item Lab(string system, string code, string display, string group) => new(TargetSystems.LabFlow, system, code, display, group);

    public static Item? Find(string target, string code) => All.FirstOrDefault(i => i.Target == target && i.Code == code);
}
