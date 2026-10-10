using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// clinic schema: where care happens and who provides it.

public class Location
{
    public int Id { get; set; }
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(200)] public string AddressLine1 { get; set; } = "";
    [MaxLength(80)] public string City { get; set; } = "";
    [MaxLength(2)] public string Province { get; set; } = "ON";
    [MaxLength(7)] public string PostalCode { get; set; } = "";
    [MaxLength(20)] public string? Phone { get; set; }

    /// <summary>4-character OHIP group number put on claims from this location.</summary>
    [MaxLength(4)] public string? OhipGroupNumber { get; set; }

    public bool IsActive { get; set; } = true;
    public List<Room> Rooms { get; set; } = [];
}

public static class RoomKinds
{
    public const string Exam = "Exam";
    public const string Procedure = "Procedure";
    public const string LabCollection = "LabCollection";
    public const string Diagnostics = "Diagnostics";
}

public class Room
{
    public int Id { get; set; }
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    [MaxLength(40)] public string Name { get; set; } = "";
    [MaxLength(20)] public string Kind { get; set; } = RoomKinds.Exam;
    public bool IsActive { get; set; } = true;
}

public static class ProviderKinds
{
    public const string Physician = "Physician";
    public const string NursePractitioner = "NursePractitioner";
    public const string Nurse = "Nurse";
    public const string Technologist = "Technologist";
}

public class Provider
{
    public int Id { get; set; }
    [MaxLength(10)] public string? Title { get; set; }
    [MaxLength(60)] public string FirstName { get; set; } = "";
    [MaxLength(60)] public string LastName { get; set; } = "";
    [MaxLength(20)] public string Kind { get; set; } = ProviderKinds.Physician;

    /// <summary>College registration number (CPSO for physicians, CNO for nurses).</summary>
    [MaxLength(10)] public string? RegistrationNumber { get; set; }

    /// <summary>6-digit OHIP billing number; null for staff who do not bill.</summary>
    [MaxLength(6)] public string? OhipBillingNumber { get; set; }

    /// <summary>2-digit OHIP specialty code, e.g. 00 family practice, 60 cardiology.</summary>
    [MaxLength(2)] public string? SpecialtyCode { get; set; }
    [MaxLength(60)] public string? SpecialtyName { get; set; }

    /// <summary>Share of OHIP payments the clinic keeps for rooms, staff and supplies.</summary>
    public decimal OverheadPercent { get; set; }

    [MaxLength(120)] public string? Email { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public bool IsActive { get; set; } = true;

    public string DisplayName => $"{(Title is null ? "" : Title + " ")}{FirstName} {LastName}";
}
