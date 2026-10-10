using System.Globalization;
using System.Text;

namespace ClinicHub.Api.Domain;

/// <summary>
/// Fixed-width 79-character records, the shape of Ontario's MC EDT claim and remittance files. The field
/// layouts below follow the published record structure (batch header, claim header, reciprocal
/// registration, item, trailer; remittance header, claim, item, message) closely enough to show the
/// work, but ClinicHub has not been certified by the Ministry: treat the files as realistic samples,
/// not as conformant submissions. Writer and reader share the same layout table, and
/// docs/ohip-billing.md prints it.
/// </summary>
public static class FixedWidth
{
    public const int RecordLength = 79;

    public enum Kind { Alpha, Numeric }

    public record Field(string Name, int Length, Kind Kind = Kind.Alpha);

    public static string Write(string recordId, Field[] layout, IReadOnlyDictionary<string, object?> values)
    {
        var sb = new StringBuilder(recordId, RecordLength);
        foreach (var f in layout)
        {
            values.TryGetValue(f.Name, out var v);
            sb.Append(f.Kind == Kind.Numeric ? Numeric(v, f) : Alpha(v?.ToString(), f.Length));
        }
        if (sb.Length != RecordLength)
            throw new InvalidOperationException($"{recordId} layout is {sb.Length} characters, not {RecordLength}.");
        return sb.ToString();
    }

    public static Dictionary<string, string> Read(string line, Field[] layout)
    {
        var padded = line.PadRight(RecordLength);
        var result = new Dictionary<string, string>();
        var at = 3;
        foreach (var f in layout)
        {
            result[f.Name] = padded.Substring(at, f.Length).Trim();
            at += f.Length;
        }
        return result;
    }

    /// <summary>Left-justified, space-filled, upper case, plain ASCII.</summary>
    private static string Alpha(string? value, int length)
    {
        var ascii = new string((value ?? "").Normalize(NormalizationForm.FormD).Where(c => c is >= ' ' and <= '~').ToArray()).ToUpperInvariant();
        return (ascii.Length > length ? ascii[..length] : ascii).PadRight(length);
    }

    /// <summary>Right-justified, zero-filled. Money is written in cents.</summary>
    private static string Numeric(object? value, Field f)
    {
        var n = value switch
        {
            null => 0L,
            decimal d => (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero),
            DateOnly date => long.Parse(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        };
        var text = Math.Abs(n).ToString(CultureInfo.InvariantCulture);
        if (text.Length > f.Length)
            throw new InvalidOperationException($"{f.Name} value {n} does not fit in {f.Length} digits.");
        return text.PadLeft(f.Length, '0');
    }

    public static decimal Cents(string digits) => string.IsNullOrEmpty(digits) ? 0m : long.Parse(digits, CultureInfo.InvariantCulture) / 100m;

    public static DateOnly Date(string yyyymmdd) => DateOnly.ParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture);
}

/// <summary>The claims file a clinic uploads: one per billing number per batch.</summary>
public static class ClaimFile
{
    private const FixedWidth.Kind N = FixedWidth.Kind.Numeric;

    public static readonly FixedWidth.Field[] Heb =
    [
        new("SpecVersion", 3), new("OfficeCode", 1), new("BatchDate", 8, N), new("BatchSequence", 4, N), new("OperatorNumber", 6, N),
        new("GroupNumber", 4), new("ProviderNumber", 6, N), new("Specialty", 2, N), new("Reserved", 42),
    ];

    public static readonly FixedWidth.Field[] Heh =
    [
        new("HealthNumber", 10), new("VersionCode", 2), new("BirthDate", 8, N), new("AccountingNumber", 8), new("PaymentProgram", 3),
        new("Payee", 1), new("ReferringProvider", 6), new("MasterNumber", 4), new("AdmissionDate", 8), new("ReferringLab", 4),
        new("ManualReview", 1), new("ServiceLocation", 4), new("Reserved", 17),
    ];

    /// <summary>Only for reciprocal (RMB) claims: the patient as their own province registered them.</summary>
    public static readonly FixedWidth.Field[] Her =
    [
        new("RegistrationNumber", 12), new("LastName", 9), new("FirstName", 5), new("Sex", 1), new("Province", 2), new("Reserved", 47),
    ];

    public static readonly FixedWidth.Field[] Het =
    [
        new("ServiceCode", 5), new("Reserved1", 2), new("FeeSubmitted", 6, N), new("NumberOfServices", 2, N), new("ServiceDate", 8, N),
        new("DiagnosticCode", 4), new("Reserved", 49),
    ];

    public static readonly FixedWidth.Field[] Hee =
    [
        new("ClaimHeaderCount", 4, N), new("RegistrationCount", 4, N), new("ItemCount", 4, N), new("Reserved", 64),
    ];

    public record Item(string ServiceCode, decimal Fee, int Units, DateOnly ServiceDate, string? DiagnosticCode);
    public record ClaimEntry(
        string AccountingNumber, string PaymentProgram, string? HealthNumber, string? VersionCode, DateOnly BirthDate,
        string? ReferringProvider, Item[] Items, (string LastName, string FirstName, string Sex, string Province)? Reciprocal);
    public record Batch(DateOnly Date, int Sequence, string? GroupNumber, string ProviderNumber, string Specialty, ClaimEntry[] Claims);

    /// <summary>H + month letter (A = January) + billing number + "." + batch sequence, e.g. HJ123456.004.</summary>
    public static string FileName(Batch b) => $"H{(char)('A' + b.Date.Month - 1)}{b.ProviderNumber}.{b.Sequence % 1000:000}";

    public static string Build(Batch b)
    {
        var lines = new List<string>
        {
            FixedWidth.Write("HEB", Heb, new Dictionary<string, object?>
            {
                ["SpecVersion"] = "V03", ["OfficeCode"] = "S", ["BatchDate"] = b.Date, ["BatchSequence"] = b.Sequence % 10000,
                ["GroupNumber"] = b.GroupNumber ?? "0000", ["ProviderNumber"] = b.ProviderNumber, ["Specialty"] = b.Specialty,
            }),
        };
        int heh = 0, her = 0, het = 0;
        foreach (var c in b.Claims)
        {
            var reciprocal = c.Reciprocal is not null;
            lines.Add(FixedWidth.Write("HEH", Heh, new Dictionary<string, object?>
            {
                // A reciprocal claim leaves the Ontario number blank and carries the patient on an HER record.
                ["HealthNumber"] = reciprocal ? null : c.HealthNumber, ["VersionCode"] = reciprocal ? null : c.VersionCode,
                ["BirthDate"] = c.BirthDate, ["AccountingNumber"] = c.AccountingNumber, ["PaymentProgram"] = c.PaymentProgram,
                ["Payee"] = "P", ["ReferringProvider"] = c.ReferringProvider,
            }));
            heh++;
            if (c.Reciprocal is { } r)
            {
                lines.Add(FixedWidth.Write("HER", Her, new Dictionary<string, object?>
                {
                    ["RegistrationNumber"] = c.HealthNumber, ["LastName"] = r.LastName, ["FirstName"] = r.FirstName,
                    ["Sex"] = r.Sex == "M" ? "1" : "2", ["Province"] = r.Province,
                }));
                her++;
            }
            foreach (var i in c.Items)
            {
                lines.Add(FixedWidth.Write("HET", Het, new Dictionary<string, object?>
                {
                    ["ServiceCode"] = i.ServiceCode, ["FeeSubmitted"] = i.Fee, ["NumberOfServices"] = i.Units,
                    ["ServiceDate"] = i.ServiceDate, ["DiagnosticCode"] = i.DiagnosticCode,
                }));
                het++;
            }
        }
        lines.Add(FixedWidth.Write("HEE", Hee, new Dictionary<string, object?> { ["ClaimHeaderCount"] = heh, ["RegistrationCount"] = her, ["ItemCount"] = het }));
        return string.Join("\r\n", lines) + "\r\n";
    }
}

/// <summary>The remittance advice: what was paid, claim by claim and item by item.</summary>
public static class RemittanceFile
{
    private const FixedWidth.Kind N = FixedWidth.Kind.Numeric;

    public static readonly FixedWidth.Field[] Hr1 =
    [
        new("SpecVersion", 3), new("GroupNumber", 4), new("ProviderNumber", 6), new("PaymentDate", 8, N), new("PayeeName", 30),
        new("TotalPaid", 9, N), new("TotalSign", 1), new("ChequeNumber", 8), new("Reserved", 7),
    ];

    public static readonly FixedWidth.Field[] Hr4 =
    [
        new("ClaimNumber", 11), new("TransactionType", 1), new("ProviderNumber", 6), new("Specialty", 2), new("AccountingNumber", 8),
        new("HealthNumber", 12), new("VersionCode", 2), new("PaymentProgram", 3), new("ServiceLocation", 4), new("GroupNumber", 4),
        new("Reserved", 23),
    ];

    public static readonly FixedWidth.Field[] Hr5 =
    [
        new("ClaimNumber", 11), new("TransactionType", 1), new("ServiceDate", 8, N), new("NumberOfServices", 2, N), new("ServiceCode", 5),
        new("Reserved1", 1), new("AmountSubmitted", 6, N), new("AmountPaid", 6, N), new("PaidSign", 1), new("ExplanatoryCode", 2),
        new("Reserved", 33),
    ];

    public static readonly FixedWidth.Field[] Hr8 = [new("Message", 70), new("Reserved", 6)];

    public record Item(DateOnly ServiceDate, int Units, string ServiceCode, decimal Submitted, decimal Paid, string? ExplanatoryCode);
    public record ClaimEntry(string ClaimNumber, string ProviderNumber, string Specialty, string AccountingNumber, string? HealthNumber,
        string? VersionCode, string PaymentProgram, Item[] Items);
    public record Advice(string? GroupNumber, string? ProviderNumber, DateOnly PaymentDate, string PayeeName, string ChequeNumber,
        ClaimEntry[] Claims, string[] Messages)
    {
        public decimal TotalPaid => Claims.SelectMany(c => c.Items).Sum(i => i.Paid);
    }

    /// <summary>P + month letter + group or billing number + ".RA", e.g. PJAB12.RA (one per payment date).</summary>
    public static string FileName(Advice a) => $"P{(char)('A' + a.PaymentDate.Month - 1)}{a.GroupNumber ?? a.ProviderNumber}{a.PaymentDate:dd}.RA";

    public static string Build(Advice a)
    {
        var lines = new List<string>
        {
            FixedWidth.Write("HR1", Hr1, new Dictionary<string, object?>
            {
                ["SpecVersion"] = "V03", ["GroupNumber"] = a.GroupNumber, ["ProviderNumber"] = a.ProviderNumber, ["PaymentDate"] = a.PaymentDate,
                ["PayeeName"] = a.PayeeName, ["TotalPaid"] = Math.Abs(a.TotalPaid), ["TotalSign"] = a.TotalPaid < 0 ? "-" : null,
                ["ChequeNumber"] = a.ChequeNumber,
            }),
        };
        foreach (var c in a.Claims)
        {
            lines.Add(FixedWidth.Write("HR4", Hr4, new Dictionary<string, object?>
            {
                ["ClaimNumber"] = c.ClaimNumber, ["TransactionType"] = "1", ["ProviderNumber"] = c.ProviderNumber, ["Specialty"] = c.Specialty,
                ["AccountingNumber"] = c.AccountingNumber, ["HealthNumber"] = c.HealthNumber, ["VersionCode"] = c.VersionCode,
                ["PaymentProgram"] = c.PaymentProgram, ["GroupNumber"] = a.GroupNumber,
            }));
            foreach (var i in c.Items)
                lines.Add(FixedWidth.Write("HR5", Hr5, new Dictionary<string, object?>
                {
                    ["ClaimNumber"] = c.ClaimNumber, ["TransactionType"] = "1", ["ServiceDate"] = i.ServiceDate, ["NumberOfServices"] = i.Units,
                    ["ServiceCode"] = i.ServiceCode, ["AmountSubmitted"] = i.Submitted, ["AmountPaid"] = Math.Abs(i.Paid),
                    ["PaidSign"] = i.Paid < 0 ? "-" : null, ["ExplanatoryCode"] = i.ExplanatoryCode,
                }));
        }
        foreach (var m in a.Messages)
            lines.Add(FixedWidth.Write("HR8", Hr8, new Dictionary<string, object?> { ["Message"] = m }));
        return string.Join("\r\n", lines) + "\r\n";
    }

    public class FormatException(string message) : Exception(message);

    /// <summary>Reads a remittance file. Record types it does not use (balance forward, accounting transactions) are skipped.</summary>
    public static Advice Parse(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("HR1"))
            throw new FormatException("Not a remittance advice: the file must start with an HR1 record.");
        if (lines.FirstOrDefault(l => l.Length > FixedWidth.RecordLength) is { } tooLong)
            throw new FormatException($"Records are {FixedWidth.RecordLength} characters; found one of {tooLong.Length}.");

        var h = FixedWidth.Read(lines[0], Hr1);
        var claims = new List<ClaimEntry>();
        var messages = new List<string>();
        foreach (var (line, n) in lines.Skip(1).Select((l, i) => (l, i + 2)))
        {
            try
            {
                switch (line[..3])
                {
                    case "HR4":
                        var c = FixedWidth.Read(line, Hr4);
                        claims.Add(new ClaimEntry(c["ClaimNumber"], c["ProviderNumber"], c["Specialty"], c["AccountingNumber"], Null(c["HealthNumber"]),
                            Null(c["VersionCode"]), c["PaymentProgram"], []));
                        break;
                    case "HR5":
                        var i = FixedWidth.Read(line, Hr5);
                        if (claims.Count == 0 || claims[^1].ClaimNumber != i["ClaimNumber"])
                            throw new FormatException("item without its claim (HR4) record");
                        var paid = FixedWidth.Cents(i["AmountPaid"]) * (i["PaidSign"] == "-" ? -1 : 1);
                        claims[^1] = claims[^1] with
                        {
                            Items = [.. claims[^1].Items, new Item(FixedWidth.Date(i["ServiceDate"]), int.Parse(i["NumberOfServices"], CultureInfo.InvariantCulture),
                                i["ServiceCode"], FixedWidth.Cents(i["AmountSubmitted"]), paid, Null(i["ExplanatoryCode"]))],
                        };
                        break;
                    case "HR8":
                        messages.Add(FixedWidth.Read(line, Hr8)["Message"]);
                        break;
                }
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                throw new FormatException($"Line {n}: {ex.Message}");
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Line {n}: {ex.Message}");
            }
        }
        var advice = new Advice(Null(h["GroupNumber"]), Null(h["ProviderNumber"]), FixedWidth.Date(h["PaymentDate"]), h["PayeeName"], h["ChequeNumber"], [.. claims], [.. messages]);
        var total = FixedWidth.Cents(h["TotalPaid"]) * (h["TotalSign"] == "-" ? -1 : 1);
        if (total != advice.TotalPaid)
            throw new FormatException($"The header total ({total:0.00}) does not match the items ({advice.TotalPaid:0.00}).");
        return advice;
    }

    private static string? Null(string s) => s.Length == 0 ? null : s;
}
