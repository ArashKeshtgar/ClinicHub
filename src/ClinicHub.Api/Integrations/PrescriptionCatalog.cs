namespace ClinicHub.Api.Integrations;

/// <summary>
/// What a clinician can prescribe electronically. <b>A demo catalog</b>: the DINs are the made-up ones of PharmaFlow's
/// demo drug catalog (99…), never to be dispensed from; a production catalog comes from Health Canada's Drug Product
/// Database. Drugs under the Controlled Drugs and Substances Act (narcotics, controlled and targeted substances such
/// as oxycodone, tramadol, lorazepam, methylphenidate) are left out on purpose: they need more than this module does
/// (no refills, a monitoring submission, stricter rules for electronic transmission).
/// </summary>
public static class PrescriptionCatalog
{
    /// <param name="Unit">What a quantity counts: "capsule", "tablet", "inhaler", "pen", "gram".</param>
    public record Drug(string Din, string Display, string Unit, string Group);

    public const string Antibiotics = "Antibiotics";
    public const string Heart = "Heart and blood pressure";
    public const string Diabetes = "Diabetes and thyroid";
    public const string Pain = "Pain and inflammation";
    public const string Mind = "Mental health";
    public const string Lungs = "Lungs";
    public const string Other = "Other";

    public static readonly Drug[] All =
    [
        new("99000011", "Amoxicillin 500 mg capsule", "capsule", Antibiotics),
        new("99000021", "Cephalexin 500 mg capsule", "capsule", Antibiotics),
        new("99000031", "Clarithromycin 500 mg tablet", "tablet", Antibiotics),
        new("99000041", "Ciprofloxacin 500 mg tablet", "tablet", Antibiotics),
        new("99000051", "Nitrofurantoin 100 mg capsule", "capsule", Antibiotics),
        new("99000071", "Atorvastatin 20 mg tablet", "tablet", Heart),
        new("99000081", "Rosuvastatin 10 mg tablet", "tablet", Heart),
        new("99000091", "Ramipril 5 mg capsule", "capsule", Heart),
        new("99000101", "Amlodipine 5 mg tablet", "tablet", Heart),
        new("99000111", "Hydrochlorothiazide 25 mg tablet", "tablet", Heart),
        new("99000121", "Spironolactone 25 mg tablet", "tablet", Heart),
        new("99000131", "Metoprolol 50 mg tablet", "tablet", Heart),
        new("99000141", "Warfarin 5 mg tablet", "tablet", Heart),
        new("99000151", "Apixaban 5 mg tablet", "tablet", Heart),
        new("99000161", "ASA (acetylsalicylic acid) 81 mg tablet", "tablet", Heart),
        new("99000061", "Metformin 500 mg tablet", "tablet", Diabetes),
        new("99000311", "Insulin glargine 100 units/mL pen", "pen", Diabetes),
        new("99000261", "Levothyroxine 100 mcg tablet", "tablet", Diabetes),
        new("99000171", "Ibuprofen 400 mg tablet", "tablet", Pain),
        new("99000181", "Naproxen 500 mg tablet", "tablet", Pain),
        new("99000191", "Acetaminophen 500 mg tablet", "tablet", Pain),
        new("99000291", "Prednisone 5 mg tablet", "tablet", Pain),
        new("99000241", "Sertraline 50 mg capsule", "capsule", Mind),
        new("99000251", "Citalopram 20 mg tablet", "tablet", Mind),
        new("99000271", "Salbutamol 100 mcg/dose inhaler", "inhaler", Lungs),
        new("99000281", "Fluticasone 125 mcg/dose inhaler", "inhaler", Lungs),
        new("99000301", "Pantoprazole 40 mg tablet", "tablet", Other),
        new("99000331", "Calcium carbonate 500 mg tablet", "tablet", Other),
        new("99000341", "Hydrocortisone 1% cream", "gram", Other),
    ];

    public static Drug? Find(string din) => All.FirstOrDefault(d => d.Din == din);
}
