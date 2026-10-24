using ClinicHub.Api.Data;
using ClinicHub.Api.Integrations;

namespace ClinicHub.Api.Domain;

/// <summary>
/// What is checked before a prescription is sent: the patient's recorded allergies, interactions with the medicines the
/// patient is already taking (and with each other, when several are written together) and duplicate therapy.
/// <b>A demo knowledge base</b> for the demo drug catalog, written by hand to show how the check works. A production
/// system would use a licensed drug database (such as Health Canada's DPD with a clinical interaction source), never
/// this table. It knows only ClinicHub's own prescriptions: what the patient takes from elsewhere is not here.
/// </summary>
public static class DrugSafety
{
    public static class Severities
    {
        public const string Severe = "Severe";
        public const string Major = "Major";
        public const string Moderate = "Moderate";

        /// <summary>Severe and major alerts need a written reason to go ahead; a moderate one is acknowledged.</summary>
        public static bool NeedsReason(string severity) => severity is Severe or Major;
    }

    public static class Kinds
    {
        public const string Allergy = "Allergy";
        public const string Interaction = "Interaction";
        public const string Duplicate = "Duplicate";
    }

    /// <summary>What a drug is, by DIN: its ingredient, and the classes that allergies, interactions and duplicates are written against.</summary>
    private static readonly Dictionary<string, string[]> Tags = new()
    {
        ["99000011"] = ["amoxicillin", "penicillin", "beta-lactam"],
        ["99000021"] = ["cephalexin", "cephalosporin", "beta-lactam"],
        ["99000031"] = ["clarithromycin", "macrolide"],
        ["99000041"] = ["ciprofloxacin", "fluoroquinolone"],
        ["99000051"] = ["nitrofurantoin"],
        ["99000061"] = ["metformin"],
        ["99000071"] = ["atorvastatin", "statin"],
        ["99000081"] = ["rosuvastatin", "statin"],
        ["99000091"] = ["ramipril", "ace-inhibitor"],
        ["99000101"] = ["amlodipine", "calcium-channel-blocker"],
        ["99000111"] = ["hydrochlorothiazide", "thiazide"],
        ["99000121"] = ["spironolactone"],
        ["99000131"] = ["metoprolol", "beta-blocker"],
        ["99000141"] = ["warfarin", "anticoagulant"],
        ["99000151"] = ["apixaban", "anticoagulant"],
        ["99000161"] = ["aspirin", "nsaid", "antiplatelet"],
        ["99000171"] = ["ibuprofen", "nsaid"],
        ["99000181"] = ["naproxen", "nsaid"],
        ["99000191"] = ["acetaminophen"],
        ["99000241"] = ["sertraline", "ssri"],
        ["99000251"] = ["citalopram", "ssri"],
        ["99000261"] = ["levothyroxine"],
        ["99000271"] = ["salbutamol"],
        ["99000281"] = ["fluticasone"],
        ["99000291"] = ["prednisone", "corticosteroid"],
        ["99000301"] = ["pantoprazole", "ppi"],
        ["99000311"] = ["insulin"],
        ["99000331"] = ["calcium", "polyvalent-cation"],
        ["99000341"] = ["hydrocortisone"],
    };

    /// <summary>What an allergy can be recorded against: a class or an ingredient from the catalog. Anything else is free text.</summary>
    public static readonly (string Key, string Label)[] Allergens =
    [
        ("penicillin", "Penicillins (amoxicillin…)"), ("cephalosporin", "Cephalosporins (cephalexin…)"), ("macrolide", "Macrolides (clarithromycin…)"),
        ("fluoroquinolone", "Fluoroquinolones (ciprofloxacin…)"), ("nsaid", "NSAIDs (ibuprofen, naproxen, ASA)"), ("aspirin", "ASA (acetylsalicylic acid)"),
        ("statin", "Statins"), ("ace-inhibitor", "ACE inhibitors (ramipril…)"), ("ssri", "SSRIs (sertraline, citalopram)"),
        ("beta-blocker", "Beta blockers"), ("corticosteroid", "Corticosteroids (prednisone…)"), ("nitrofurantoin", "Nitrofurantoin"),
        ("metformin", "Metformin"), ("acetaminophen", "Acetaminophen"), ("warfarin", "Warfarin"), ("apixaban", "Apixaban"),
    ];

    /// <summary>Class allergies that reach another class: (allergy, the class it may reach, what to tell the prescriber).</summary>
    private static readonly (string Allergy, string Reaches, string Text)[] CrossReactions =
    [
        ("penicillin", "cephalosporin", "A cross-reaction between penicillins and cephalosporins is possible."),
        ("aspirin", "nsaid", "People allergic to ASA often react to other NSAIDs."),
    ];

    private record Interaction(string A, string B, string Severity, string Text);

    private static readonly Interaction[] Interactions =
    [
        new("warfarin", "nsaid", Severities.Major, "An NSAID with warfarin raises the risk of serious bleeding."),
        new("apixaban", "nsaid", Severities.Major, "An NSAID with apixaban raises the risk of serious bleeding."),
        new("warfarin", "clarithromycin", Severities.Major, "Clarithromycin raises warfarin's effect (INR); bleeding risk."),
        new("warfarin", "ciprofloxacin", Severities.Major, "Ciprofloxacin raises warfarin's effect (INR); bleeding risk."),
        new("warfarin", "ssri", Severities.Moderate, "An SSRI with warfarin raises the risk of bleeding."),
        new("clarithromycin", "atorvastatin", Severities.Major, "Clarithromycin raises atorvastatin levels; risk of muscle damage."),
        new("ramipril", "spironolactone", Severities.Major, "Together they can raise potassium to a dangerous level."),
        new("ace-inhibitor", "nsaid", Severities.Moderate, "An NSAID can weaken an ACE inhibitor and strain the kidneys."),
        new("ssri", "nsaid", Severities.Moderate, "An SSRI with an NSAID raises the risk of bleeding."),
        new("aspirin", "ibuprofen", Severities.Moderate, "Ibuprofen can reduce the heart protection of low-dose ASA."),
        new("ciprofloxacin", "polyvalent-cation", Severities.Moderate, "Calcium reduces ciprofloxacin's absorption: take it 2 hours before or 6 hours after."),
        new("levothyroxine", "calcium", Severities.Moderate, "Calcium reduces levothyroxine's absorption: take them 4 hours apart."),
        new("clarithromycin", "amlodipine", Severities.Moderate, "Clarithromycin raises amlodipine levels; low blood pressure."),
        new("prednisone", "nsaid", Severities.Moderate, "A corticosteroid with an NSAID raises the risk of stomach bleeding."),
    ];

    /// <summary>Classes of which one drug is enough; "nsaid" counts the analgesics only (ASA at 81 mg is a different use).</summary>
    private static readonly string[] DuplicateClasses = ["statin", "ssri", "anticoagulant", "ace-inhibitor", "beta-blocker", "ppi", "nsaid"];

    public record Alert(string Key, string Kind, string Severity, string Message, string[] Dins);

    /// <summary>A medicine the patient is taking, from an earlier prescription here.</summary>
    public record Current(string Din, string Display, DateTime CreatedUtc);

    public static string[] TagsOf(string din) => Tags.GetValueOrDefault(din) ?? [];

    /// <summary>
    /// Is this earlier prescription still being taken? Cancelled and failed ones are not; one runs for its days of supply
    /// times its fills (30 days a fill when it says nothing).
    /// </summary>
    public static bool IsCurrent(Prescription p, DateTime nowUtc) =>
        p.Status is PrescriptionStatus.Draft or PrescriptionStatus.Accepted or PrescriptionStatus.Cancelling
        && p.CreatedUtc.AddDays((p.DaysSupply ?? 30) * (p.Repeats + 1)) > nowUtc;

    /// <summary>
    /// The alerts for drugs about to be prescribed (<paramref name="written"/>, by DIN), given the patient's active allergies
    /// and the medicines already being taken. Only pairs where at least one drug is new are reported.
    /// </summary>
    public static List<Alert> Check(IReadOnlyList<string> written, IReadOnlyList<PatientAllergy> allergies, IReadOnlyList<Current> current)
    {
        var alerts = new List<Alert>();
        foreach (var din in written.Distinct())
        {
            var tags = TagsOf(din);
            var name = PrescriptionCatalog.Find(din)?.Display ?? din;
            foreach (var a in allergies)
            {
                var direct = a.AllergenKey is { } key ? tags.Contains(key) : MatchesFreeText(a.Substance, tags);
                var cross = !direct && a.AllergenKey is { } k
                    ? CrossReactions.FirstOrDefault(c => c.Allergy == k && tags.Contains(c.Reaches))
                    : default;
                if (direct)
                    alerts.Add(new Alert($"allergy:{a.Id}:{din}", Kinds.Allergy,
                        a.Severity == AllergySeverities.Severe ? Severities.Severe : Severities.Major,
                        $"{name}: the patient is allergic to {a.Substance}" + (string.IsNullOrWhiteSpace(a.Reaction) ? "" : $" ({a.Reaction})") + ".", [din]));
                else if (cross.Text is not null)
                    alerts.Add(new Alert($"allergy:{a.Id}:{din}", Kinds.Allergy, Severities.Moderate, $"{name}: the patient is allergic to {a.Substance}. {cross.Text}", [din]));
            }
        }

        // Every pair among what is written and what is already taken, where something in it is new.
        var all = written.Distinct().Select(d => (Din: d, IsNew: true, Taken: (Current?)null))
            .Concat(current.Where(c => !written.Contains(c.Din)).GroupBy(c => c.Din).Select(g => (Din: g.Key, IsNew: false, Taken: g.First())))
            .ToList();
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
            {
                var (x, y) = (all[i], all[j]);
                if (!x.IsNew && !y.IsNew)
                    continue;
                var (tx, ty) = (TagsOf(x.Din), TagsOf(y.Din));
                var (nx, ny) = (Name(x.Din), Name(y.Din));
                var with = (x.IsNew && y.IsNew) ? $"{nx} and {ny}" : $"{(x.IsNew ? nx : ny)} with {(x.IsNew ? ny : nx)}, which the patient is taking";
                foreach (var rule in Interactions)
                    if ((tx.Contains(rule.A) && ty.Contains(rule.B)) || (tx.Contains(rule.B) && ty.Contains(rule.A)))
                        alerts.Add(new Alert(PairKey("interaction", x.Din, y.Din, rule.A + rule.B), Kinds.Interaction, rule.Severity, $"{with}: {rule.Text}", [x.Din, y.Din]));
                foreach (var cls in DuplicateClasses.Where(c => tx.Contains(c) && ty.Contains(c)))
                {
                    if (cls == "nsaid" && (tx.Contains("aspirin") || ty.Contains("aspirin")))
                        continue;
                    var severity = cls == "anticoagulant" ? Severities.Severe : Severities.Major;
                    alerts.Add(new Alert(PairKey("duplicate", x.Din, y.Din, cls), Kinds.Duplicate, severity,
                        $"{with}: two medicines of the same kind ({cls.Replace('-', ' ')}).", [x.Din, y.Din]));
                }
            }
        return alerts.GroupBy(a => a.Key).Select(g => g.First()).OrderByDescending(a => Rank(a.Severity)).ThenBy(a => a.Key).ToList();

        static string Name(string din) => PrescriptionCatalog.Find(din)?.Display ?? din;
        static string PairKey(string kind, string a, string b, string rule) =>
            string.CompareOrdinal(a, b) < 0 ? $"{kind}:{a}+{b}:{rule}" : $"{kind}:{b}+{a}:{rule}";
    }

    /// <summary>Free text matches a drug when it names one of its ingredients (at least 5 letters, so "ASA" alone is not guessed at).</summary>
    private static bool MatchesFreeText(string substance, string[] tags) =>
        tags.Any(t => t.Length >= 5 && substance.Contains(t, StringComparison.OrdinalIgnoreCase));

    public static int Rank(string severity) => severity switch { Severities.Severe => 3, Severities.Major => 2, _ => 1 };
}
