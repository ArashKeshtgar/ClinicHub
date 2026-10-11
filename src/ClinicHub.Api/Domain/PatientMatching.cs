using System.Globalization;
using System.Text;
using ClinicHub.Api.Data;

namespace ClinicHub.Api.Domain;

/// <summary>
/// How alike two patient records are, for the master patient index. A score out of 100 built from the
/// fields a front desk gets wrong when the same person is registered twice: a card left out, a typo in the
/// last name, a nickname, day and month swapped in the birth date. Two different health cards count
/// strongly against: that is almost always two people.
/// </summary>
public static class PatientMatching
{
    /// <summary>At or above this, the pair waits in the duplicates queue for a person to decide.</summary>
    public const int Candidate = 60;

    /// <summary>At or above this, registering the new record is stopped until the clerk confirms.</summary>
    public const int Likely = 80;

    public record Match(int Score, string[] Reasons);

    public static Match Score(Patient a, Patient b)
    {
        if (a.HealthCardNumber is not null && a.HealthCardNumber == b.HealthCardNumber)
            return new(100, ["Same health card"]);

        var score = 0;
        var reasons = new List<string>();
        void Add(int points, string reason) { score += points; reasons.Add(reason); }

        if (a.DateOfBirth == b.DateOfBirth) Add(35, "Same date of birth");
        else if (OneDateTypo(a.DateOfBirth, b.DateOfBirth)) Add(20, "Date of birth differs by one typo");

        var (lastA, lastB) = (Name(a.LastName), Name(b.LastName));
        if (lastA == lastB) Add(25, "Same last name");
        else if (Distance(lastA, lastB) == 1) Add(18, "Last name differs by one letter");
        else if (Soundex(lastA) == Soundex(lastB)) Add(12, "Last name sounds the same");

        var (firstA, firstB) = (Name(a.FirstName), Name(b.FirstName));
        var preferredMatch = Name(a.PreferredName) is { Length: > 0 } pa && (pa == firstB || pa == Name(b.PreferredName))
            || Name(b.PreferredName) is { Length: > 0 } pb && pb == firstA;
        if (firstA == firstB) Add(20, "Same first name");
        else if (preferredMatch) Add(15, "First name matches a preferred name");
        else if (Distance(firstA, firstB) == 1) Add(15, "First name differs by one letter");
        else if (firstA.Length >= 3 && firstB.Length >= 3 && (firstA.StartsWith(firstB) || firstB.StartsWith(firstA))) Add(12, "Short form of the first name");

        if (Digits(a.Phone) is { Length: >= 7 } phone && phone == Digits(b.Phone)) Add(10, "Same phone");
        if (!string.IsNullOrWhiteSpace(a.Email) && string.Equals(a.Email.Trim(), b.Email?.Trim(), StringComparison.OrdinalIgnoreCase)) Add(10, "Same email");
        if (Postal(a.PostalCode) is { Length: 6 } postal && postal == Postal(b.PostalCode)) Add(5, "Same postal code");

        if (a.Sex is "M" or "F" or "X" && b.Sex is "M" or "F" or "X" && a.Sex != b.Sex) Add(-10, "Different sex");
        if (a.HealthCardNumber is not null && b.HealthCardNumber is not null) Add(-40, "Different health cards");

        return new(Math.Clamp(score, 0, 100), [.. reasons]);
    }

    /// <summary>
    /// The live records <paramref name="patient"/> may duplicate, best first. Only records sharing a birth date, a
    /// birth year and a sound-alike last name, a phone or an email are compared, so this stays fast in a large clinic.
    /// </summary>
    public static List<(Patient Other, Match Match)> FindMatches(Patient patient, IEnumerable<Patient> live, int threshold = Candidate) =>
        live.Where(o => o.Id != patient.Id && o.MergedIntoPatientId == null && Keys(o).Overlaps(Keys(patient)))
            .Select(o => (Other: o, Match: Score(patient, o)))
            .Where(x => x.Match.Score >= threshold)
            .OrderByDescending(x => x.Match.Score).ToList();

    /// <summary>Every pair among <paramref name="live"/> at or above the threshold, each pair once (lower id first).</summary>
    public static List<(Patient A, Patient B, Match Match)> FindPairs(IReadOnlyCollection<Patient> live, int threshold = Candidate)
    {
        var blocks = new Dictionary<string, List<Patient>>();
        foreach (var p in live.Where(p => p.MergedIntoPatientId == null))
            foreach (var key in Keys(p))
                (blocks.TryGetValue(key, out var list) ? list : blocks[key] = []).Add(p);

        var seen = new HashSet<(int, int)>();
        var pairs = new List<(Patient, Patient, Match)>();
        foreach (var block in blocks.Values.Where(b => b.Count > 1))
            for (var i = 0; i < block.Count; i++)
                for (var j = i + 1; j < block.Count; j++)
                {
                    var (a, b) = block[i].Id < block[j].Id ? (block[i], block[j]) : (block[j], block[i]);
                    if (!seen.Add((a.Id, b.Id)))
                        continue;
                    var match = Score(a, b);
                    if (match.Score >= threshold)
                        pairs.Add((a, b, match));
                }
        return pairs;
    }

    /// <summary>Blocking keys: two records are only compared when they share one.</summary>
    public static HashSet<string> Keys(Patient p)
    {
        var keys = new HashSet<string>
        {
            $"dob:{p.DateOfBirth:yyyy-MM-dd}",
            // Day and month swapped, or a wrong day in the right month: the year and the sound of the name still agree.
            $"name:{p.DateOfBirth.Year}:{Soundex(Name(p.LastName))}",
        };
        if (Digits(p.Phone) is { Length: >= 7 } phone) keys.Add($"phone:{phone}");
        if (!string.IsNullOrWhiteSpace(p.Email)) keys.Add($"email:{p.Email.Trim().ToLowerInvariant()}");
        if (p.HealthCardNumber is not null) keys.Add($"hcn:{p.HealthCardNumber}");
        return keys;
    }

    private static bool OneDateTypo(DateOnly a, DateOnly b) =>
        (a.Year == b.Year && a.Month == b.Day && a.Day == b.Month)
        || (a.Year == b.Year && a.Month == b.Month)
        || (a.Year == b.Year && a.Day == b.Day)
        || (a.Month == b.Month && a.Day == b.Day && Distance(a.Year.ToString(), b.Year.ToString()) == 1);

    /// <summary>Lower case, accents and punctuation removed: "O'Brien" = "obrien", "Hélène" = "helene".</summary>
    public static string Name(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetter(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string Digits(string? s) => s is null ? "" : new(s.Where(char.IsAsciiDigit).ToArray());
    private static string Postal(string? s) => s is null ? "" : new(s.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Optimal string alignment distance: insertions, deletions, substitutions and swapped neighbours.</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>American Soundex of an already normalised name ("" for an empty one).</summary>
    public static string Soundex(string name)
    {
        if (name.Length == 0) return "";
        static char Code(char c) => c switch
        {
            'b' or 'f' or 'p' or 'v' => '1',
            'c' or 'g' or 'j' or 'k' or 'q' or 's' or 'x' or 'z' => '2',
            'd' or 't' => '3',
            'l' => '4',
            'm' or 'n' => '5',
            'r' => '6',
            'h' or 'w' => '-',
            _ => '0',
        };
        var sb = new StringBuilder().Append(char.ToUpperInvariant(name[0]));
        var last = Code(name[0]);
        foreach (var c in name.Skip(1))
        {
            var code = Code(c);
            if (code == '-') continue;               // h and w don't separate equal codes
            if (code != '0' && code != last) sb.Append(code);
            last = code;
            if (sb.Length == 4) break;
        }
        return sb.ToString().PadRight(4, '0');
    }
}
