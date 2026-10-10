using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Api.Data;

// sched schema: when providers work and who is booked.

/// <summary>A recurring session: provider × location × weekday × hours.</summary>
public class ScheduleTemplate
{
    public int Id { get; set; }
    public int ProviderId { get; set; }
    public Provider Provider { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int SlotMinutes { get; set; } = 15;
    public bool AcceptsWalkIns { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

public static class ScheduleExceptionKinds
{
    public const string Off = "Off";
    public const string ExtraHours = "ExtraHours";
}

public class ScheduleException
{
    public int Id { get; set; }
    public int ProviderId { get; set; }
    public Provider Provider { get; set; } = null!;
    public DateOnly Date { get; set; }
    [MaxLength(20)] public string Kind { get; set; } = ScheduleExceptionKinds.Off;
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    [MaxLength(200)] public string? Reason { get; set; }
}

public static class AppointmentStatus
{
    public const string Booked = "Booked";
    public const string Arrived = "Arrived";
    public const string Roomed = "Roomed";
    public const string WithProvider = "WithProvider";
    public const string Completed = "Completed";
    public const string NoShow = "NoShow";
    public const string Cancelled = "Cancelled";

    public static readonly string[] Flow = [Booked, Arrived, Roomed, WithProvider, Completed];
    public static readonly string[] All = [.. Flow, NoShow, Cancelled];
}

public static class AppointmentSources
{
    public const string Phone = "Phone";
    public const string Online = "Online";
    public const string WalkIn = "WalkIn";
    public const string Referral = "Referral";
}

public class Appointment
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int ProviderId { get; set; }
    public Provider Provider { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public int AppointmentTypeId { get; set; }
    public AppointmentType AppointmentType { get; set; } = null!;

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    [MaxLength(20)] public string Status { get; set; } = AppointmentStatus.Booked;
    [MaxLength(20)] public string Source { get; set; } = AppointmentSources.Phone;

    /// <summary>Ticket number for walk-ins, per location and day.</summary>
    public int? QueueNumber { get; set; }

    [MaxLength(200)] public string? Reason { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    [MaxLength(200)] public string? CancelReason { get; set; }

    public int CreatedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];

    public List<AppointmentStatusChange> StatusChanges { get; set; } = [];
}

/// <summary>Every step of a visit with its time: wait and door-to-doctor times come from here.</summary>
public class AppointmentStatusChange
{
    public long Id { get; set; }
    public int AppointmentId { get; set; }
    public Appointment Appointment { get; set; } = null!;
    [MaxLength(20)] public string Status { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public int UserId { get; set; }
}
