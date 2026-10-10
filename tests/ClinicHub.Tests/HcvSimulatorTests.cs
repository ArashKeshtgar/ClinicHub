using ClinicHub.Api.Data;
using ClinicHub.Api.Domain;

namespace ClinicHub.Tests;

public class HcvSimulatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);
    private static readonly Dictionary<string, bool> Reciprocal = new() { ["ON"] = false, ["BC"] = true, ["QC"] = false };

    private static Patient Card(string? number = "9876543217", string? version = "AB", DateOnly? expiry = null, string? province = "ON") => new()
    {
        HealthCardNumber = number, HealthCardVersion = version, HealthCardExpiry = expiry ?? Today.AddYears(2), CoverageProvince = province,
    };

    [Fact]
    public void Valid_card_is_eligible_for_ohip()
    {
        var r = HcvSimulator.Check(Card(), null, Today, Reciprocal);
        Assert.Equal(("OK", true, PaymentPrograms.Hcp), (r.Code, r.Eligible, r.PaymentProgram));
    }

    [Fact]
    public void Bad_check_digit_is_refused() =>
        Assert.Equal("INV", HcvSimulator.Check(Card("9876543210"), null, Today, Reciprocal).Code);

    [Fact]
    public void Version_in_hand_must_match_the_card_on_file()
    {
        Assert.Equal("VER", HcvSimulator.Check(Card(), "ZZ", Today, Reciprocal).Code);
        Assert.Equal("OK", HcvSimulator.Check(Card(), "ab", Today, Reciprocal).Code);
        Assert.Equal("VFMT", HcvSimulator.Check(Card(), "A1", Today, Reciprocal).Code);
    }

    [Fact]
    public void Expired_card_is_refused() =>
        Assert.Equal("EXP", HcvSimulator.Check(Card(expiry: Today.AddDays(-1)), null, Today, Reciprocal).Code);

    [Fact]
    public void Other_provinces_bill_reciprocally_except_quebec()
    {
        var bc = HcvSimulator.Check(Card(null, null, province: "BC"), null, Today, Reciprocal);
        Assert.Equal((true, PaymentPrograms.Rmb), (bc.Eligible, bc.PaymentProgram));

        var qc = HcvSimulator.Check(Card(null, null, province: "QC"), null, Today, Reciprocal);
        Assert.Equal((false, PaymentPrograms.Uninsured), (qc.Eligible, qc.PaymentProgram));
    }

    [Fact]
    public void No_coverage_means_the_patient_pays() =>
        Assert.Equal(PaymentPrograms.Uninsured, HcvSimulator.Check(Card(null, null, province: null), null, Today, Reciprocal).PaymentProgram);

    [Fact]
    public void Synthetic_health_cards_pass_the_check_digit()
    {
        var rng = new Random(1);
        for (var i = 0; i < 200; i++)
            Assert.True(HealthCard.IsValidNumber(DemoSeeder.NewHealthCard(rng)));
    }
}
