using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialClinic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sched");

            migrationBuilder.EnsureSchema(
                name: "ref");

            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.EnsureSchema(
                name: "care");

            migrationBuilder.EnsureSchema(
                name: "people");

            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.EnsureSchema(
                name: "clinic");

            migrationBuilder.EnsureSchema(
                name: "sec");

            migrationBuilder.CreateTable(
                name: "AppointmentType",
                schema: "ref",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Color = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BookableOnline = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentType", x => x.Id);
                    table.CheckConstraint("CK_AppointmentType_Duration", "DurationMinutes BETWEEN 5 AND 480");
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticCode",
                schema: "ref",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticCode", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "FeeCode",
                schema: "ref",
                columns: table => new
                {
                    ServiceCode = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsIllustrative = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeCode", x => x.ServiceCode);
                    table.CheckConstraint("CK_FeeCode_Fee", "Fee >= 0");
                });

            migrationBuilder.CreateTable(
                name: "InboundMessage",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Location",
                schema: "clinic",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    AddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Province = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    OhipGroupNumber = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Location", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Patient",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Mrn = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    MiddleName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PreferredName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                    Sex = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false),
                    HealthCardNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    HealthCardVersion = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    HealthCardExpiry = table.Column<DateOnly>(type: "date", nullable: true),
                    CoverageProvince = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    OutOfProvinceNumber = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Province = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                    PreferredLanguage = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    FamilyProviderName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    FamilyProviderOhipNumber = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    IsDeceased = table.Column<bool>(type: "bit", nullable: false),
                    DeceasedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MergedIntoPatientId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patient", x => x.Id);
                    table.CheckConstraint("CK_Patient_HealthCard", "HealthCardNumber IS NULL OR (LEN(HealthCardNumber) = 10 AND HealthCardNumber NOT LIKE '%[^0-9]%')");
                    table.CheckConstraint("CK_Patient_NotSelfMerged", "MergedIntoPatientId IS NULL OR MergedIntoPatientId <> Id");
                    table.CheckConstraint("CK_Patient_Sex", "Sex IN ('M','F','X','U')");
                    table.CheckConstraint("CK_Patient_Version", "HealthCardVersion IS NULL OR HealthCardVersion NOT LIKE '%[^A-Z]%'");
                    table.ForeignKey(
                        name: "FK_Patient_Patient_MergedIntoPatientId",
                        column: x => x.MergedIntoPatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Province",
                schema: "ref",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ReciprocalBilling = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Province", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "RemittanceAdvice",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OhipBillingNumber = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    GroupNumber = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FileContent = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    Simulated = table.Column<bool>(type: "bit", nullable: false),
                    TotalPaid = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    ImportedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemittanceAdvice", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UninsuredService",
                schema: "ref",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UninsuredService", x => x.Id);
                    table.CheckConstraint("CK_UninsuredService_Price", "Price >= 0 AND TaxRatePercent BETWEEN 0 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Credentials = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SyncedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Room",
                schema: "clinic",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LocationId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Room", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Room_Location_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "clinic",
                        principalTable: "Location",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EligibilityCheck",
                schema: "care",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    CheckedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    HealthCardNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    HealthCardVersion = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    ResponseCode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    IsEligible = table.Column<bool>(type: "bit", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Simulated = table.Column<bool>(type: "bit", nullable: false),
                    CheckedByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EligibilityCheck", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EligibilityCheck_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EligibilityCheck_User_CheckedByUserId",
                        column: x => x.CheckedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PatientDuplicateCandidate",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientAId = table.Column<int>(type: "int", nullable: false),
                    PatientBId = table.Column<int>(type: "int", nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false),
                    Reasons = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FoundUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReviewedByUserId = table.Column<int>(type: "int", nullable: true),
                    ReviewedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientDuplicateCandidate", x => x.Id);
                    table.CheckConstraint("CK_Duplicate_Order", "PatientAId < PatientBId");
                    table.CheckConstraint("CK_Duplicate_Score", "Score BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_PatientDuplicateCandidate_Patient_PatientAId",
                        column: x => x.PatientAId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientDuplicateCandidate_Patient_PatientBId",
                        column: x => x.PatientBId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientDuplicateCandidate_User_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Provider",
                schema: "clinic",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RegistrationNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    OhipBillingNumber = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    SpecialtyCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    SpecialtyName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    OverheadPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Provider", x => x.Id);
                    table.CheckConstraint("CK_Provider_Billing", "OhipBillingNumber IS NULL OR (LEN(OhipBillingNumber) = 6 AND OhipBillingNumber NOT LIKE '%[^0-9]%')");
                    table.CheckConstraint("CK_Provider_Overhead", "OverheadPercent BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_Provider_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Appointment",
                schema: "sched",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    ProviderId = table.Column<int>(type: "int", nullable: false),
                    LocationId = table.Column<int>(type: "int", nullable: false),
                    RoomId = table.Column<int>(type: "int", nullable: true),
                    AppointmentTypeId = table.Column<int>(type: "int", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    QueueNumber = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointment", x => x.Id);
                    table.CheckConstraint("CK_Appointment_Status", "Status IN ('Booked','Arrived','Roomed','WithProvider','Completed','NoShow','Cancelled')");
                    table.CheckConstraint("CK_Appointment_Time", "EndUtc > StartUtc");
                    table.ForeignKey(
                        name: "FK_Appointment_AppointmentType_AppointmentTypeId",
                        column: x => x.AppointmentTypeId,
                        principalSchema: "ref",
                        principalTable: "AppointmentType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointment_Location_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "clinic",
                        principalTable: "Location",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointment_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointment_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointment_Room_RoomId",
                        column: x => x.RoomId,
                        principalSchema: "clinic",
                        principalTable: "Room",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Appointment_User_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClaimBatch",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<int>(type: "int", nullable: false),
                    GroupNumber = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    BatchNumber = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FileContent = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ClaimCount = table.Column<int>(type: "int", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimBatch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimBatch_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleException",
                schema: "sched",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleException", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleException_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleTemplate",
                schema: "sched",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<int>(type: "int", nullable: false),
                    LocationId = table.Column<int>(type: "int", nullable: false),
                    DayOfWeek = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    SlotMinutes = table.Column<int>(type: "int", nullable: false),
                    AcceptsWalkIns = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleTemplate", x => x.Id);
                    table.CheckConstraint("CK_ScheduleTemplate_Hours", "EndTime > StartTime");
                    table.CheckConstraint("CK_ScheduleTemplate_Slot", "SlotMinutes BETWEEN 5 AND 120");
                    table.ForeignKey(
                        name: "FK_ScheduleTemplate_Location_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "clinic",
                        principalTable: "Location",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ScheduleTemplate_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentStatusChange",
                schema: "sched",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AppointmentId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentStatusChange", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentStatusChange_Appointment_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "sched",
                        principalTable: "Appointment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentStatusChange_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Encounter",
                schema: "care",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    ProviderId = table.Column<int>(type: "int", nullable: false),
                    LocationId = table.Column<int>(type: "int", nullable: false),
                    AppointmentId = table.Column<int>(type: "int", nullable: true),
                    EligibilityCheckId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PaymentProgram = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ReasonForVisit = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CtasLevel = table.Column<byte>(type: "tinyint", nullable: true),
                    CheckInUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SignedByUserId = table.Column<int>(type: "int", nullable: true),
                    SignedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Encounter", x => x.Id);
                    table.CheckConstraint("CK_Encounter_Ctas", "CtasLevel IS NULL OR CtasLevel BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_Encounter_Program", "PaymentProgram IN ('HCP','RMB','WCB','Uninsured')");
                    table.CheckConstraint("CK_Encounter_Signed", "Status <> 'Signed' OR (SignedUtc IS NOT NULL AND SignedByUserId IS NOT NULL)");
                    table.CheckConstraint("CK_Encounter_Status", "Status IN ('Open','Signed','Voided')");
                    table.ForeignKey(
                        name: "FK_Encounter_Appointment_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "sched",
                        principalTable: "Appointment",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Encounter_EligibilityCheck_EligibilityCheckId",
                        column: x => x.EligibilityCheckId,
                        principalSchema: "care",
                        principalTable: "EligibilityCheck",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Encounter_Location_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "clinic",
                        principalTable: "Location",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Encounter_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Encounter_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Encounter_User_SignedByUserId",
                        column: x => x.SignedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Claim",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EncounterId = table.Column<int>(type: "int", nullable: false),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    ProviderId = table.Column<int>(type: "int", nullable: false),
                    PaymentProgram = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    HealthCardNumber = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    HealthCardVersion = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Province = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    ReferringProviderNumber = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    AccountingNumber = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ClaimBatchId = table.Column<int>(type: "int", nullable: true),
                    AmountSubmitted = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claim", x => x.Id);
                    table.CheckConstraint("CK_Claim_Amounts", "AmountSubmitted >= 0 AND AmountPaid >= 0");
                    table.CheckConstraint("CK_Claim_Program", "PaymentProgram IN ('HCP','RMB','WCB')");
                    table.CheckConstraint("CK_Claim_Status", "Status IN ('Draft','Ready','Submitted','Paid','PartiallyPaid','Rejected')");
                    table.ForeignKey(
                        name: "FK_Claim_ClaimBatch_ClaimBatchId",
                        column: x => x.ClaimBatchId,
                        principalSchema: "billing",
                        principalTable: "ClaimBatch",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Claim_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Claim_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Claim_Provider_ProviderId",
                        column: x => x.ProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClinicalNote",
                schema: "care",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EncounterId = table.Column<int>(type: "int", nullable: false),
                    Subjective = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Objective = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Assessment = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Plan = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    Addendum = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    AuthorUserId = table.Column<int>(type: "int", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicalNote", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClinicalNote_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinicalNote_User_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsentDirective",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EncounterId = table.Column<int>(type: "int", nullable: true),
                    ExcludedProviderId = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RecordedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentDirective", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentDirective_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConsentDirective_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsentDirective_Provider_ExcludedProviderId",
                        column: x => x.ExcludedProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConsentDirective_User_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EncounterDiagnosis",
                schema: "care",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EncounterId = table.Column<int>(type: "int", nullable: false),
                    DiagnosticCodeCode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncounterDiagnosis", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EncounterDiagnosis_DiagnosticCode_DiagnosticCodeCode",
                        column: x => x.DiagnosticCodeCode,
                        principalSchema: "ref",
                        principalTable: "DiagnosticCode",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EncounterDiagnosis_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EncounterService",
                schema: "care",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EncounterId = table.Column<int>(type: "int", nullable: false),
                    FeeCodeServiceCode = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: true),
                    UninsuredServiceId = table.Column<int>(type: "int", nullable: true),
                    Units = table.Column<byte>(type: "tinyint", nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncounterService", x => x.Id);
                    table.CheckConstraint("CK_EncounterService_OneKind", "(FeeCodeServiceCode IS NOT NULL AND UninsuredServiceId IS NULL) OR (FeeCodeServiceCode IS NULL AND UninsuredServiceId IS NOT NULL)");
                    table.CheckConstraint("CK_EncounterService_Units", "Units BETWEEN 1 AND 99 AND Fee >= 0");
                    table.ForeignKey(
                        name: "FK_EncounterService_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EncounterService_FeeCode_FeeCodeServiceCode",
                        column: x => x.FeeCodeServiceCode,
                        principalSchema: "ref",
                        principalTable: "FeeCode",
                        principalColumn: "ServiceCode");
                    table.ForeignKey(
                        name: "FK_EncounterService_UninsuredService_UninsuredServiceId",
                        column: x => x.UninsuredServiceId,
                        principalSchema: "ref",
                        principalTable: "UninsuredService",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Invoice",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PatientId = table.Column<int>(type: "int", nullable: true),
                    EncounterId = table.Column<int>(type: "int", nullable: true),
                    ThirdPartyName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IssuedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Subtotal = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoice", x => x.Id);
                    table.CheckConstraint("CK_Invoice_BillTo", "PatientId IS NOT NULL OR ThirdPartyName IS NOT NULL");
                    table.CheckConstraint("CK_Invoice_Totals", "Total = Subtotal + Tax AND Balance BETWEEN 0 AND Total");
                    table.ForeignKey(
                        name: "FK_Invoice_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Invoice_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Referral",
                schema: "care",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EncounterId = table.Column<int>(type: "int", nullable: false),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    ToSpecialty = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ToProviderName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Urgency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referral", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Referral_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Referral_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceRequest",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EncounterId = table.Column<int>(type: "int", nullable: false),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    RequesterProviderId = table.Column<int>(type: "int", nullable: false),
                    TargetSystem = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Display = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ClinicalNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRequest", x => x.Id);
                    table.CheckConstraint("CK_ServiceRequest_Priority", "Priority IN ('Routine','Stat')");
                    table.CheckConstraint("CK_ServiceRequest_Target", "TargetSystem IN ('LabFlow','ClinicReport')");
                    table.ForeignKey(
                        name: "FK_ServiceRequest_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceRequest_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceRequest_Provider_RequesterProviderId",
                        column: x => x.RequesterProviderId,
                        principalSchema: "clinic",
                        principalTable: "Provider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VitalSigns",
                schema: "care",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EncounterId = table.Column<int>(type: "int", nullable: false),
                    TakenUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SystolicMmHg = table.Column<short>(type: "smallint", nullable: true),
                    DiastolicMmHg = table.Column<short>(type: "smallint", nullable: true),
                    HeartRate = table.Column<short>(type: "smallint", nullable: true),
                    RespiratoryRate = table.Column<short>(type: "smallint", nullable: true),
                    TemperatureC = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    SpO2 = table.Column<byte>(type: "tinyint", nullable: true),
                    WeightKg = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    HeightCm = table.Column<decimal>(type: "decimal(5,1)", precision: 5, scale: 1, nullable: true),
                    TakenByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VitalSigns", x => x.Id);
                    table.CheckConstraint("CK_Vitals_Ranges", "(SpO2 IS NULL OR SpO2 <= 100) AND (TemperatureC IS NULL OR TemperatureC BETWEEN 25 AND 45) AND (HeartRate IS NULL OR HeartRate BETWEEN 10 AND 300)");
                    table.ForeignKey(
                        name: "FK_VitalSigns_Encounter_EncounterId",
                        column: x => x.EncounterId,
                        principalSchema: "care",
                        principalTable: "Encounter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VitalSigns_User_TakenByUserId",
                        column: x => x.TakenByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClaimItem",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClaimId = table.Column<int>(type: "int", nullable: false),
                    ServiceCode = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Units = table.Column<byte>(type: "tinyint", nullable: false),
                    DiagnosticCode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    FeeSubmitted = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    FeePaid = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    ExplanatoryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimItem", x => x.Id);
                    table.CheckConstraint("CK_ClaimItem_Units", "Units BETWEEN 1 AND 99 AND FeeSubmitted >= 0");
                    table.ForeignKey(
                        name: "FK_ClaimItem_Claim_ClaimId",
                        column: x => x.ClaimId,
                        principalSchema: "billing",
                        principalTable: "Claim",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConsentOverride",
                schema: "people",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsentDirectiveId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentOverride", x => x.Id);
                    table.CheckConstraint("CK_ConsentOverride_Expiry", "ExpiresUtc > AtUtc");
                    table.ForeignKey(
                        name: "FK_ConsentOverride_ConsentDirective_ConsentDirectiveId",
                        column: x => x.ConsentDirectiveId,
                        principalSchema: "people",
                        principalTable: "ConsentDirective",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsentOverride_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLine",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvoiceId = table.Column<int>(type: "int", nullable: false),
                    UninsuredServiceId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLine", x => x.Id);
                    table.CheckConstraint("CK_InvoiceLine_Amounts", "Quantity > 0 AND UnitPrice >= 0");
                    table.ForeignKey(
                        name: "FK_InvoiceLine_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "billing",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_UninsuredService_UninsuredServiceId",
                        column: x => x.UninsuredServiceId,
                        principalSchema: "ref",
                        principalTable: "UninsuredService",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Payment",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvoiceId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReceivedByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payment", x => x.Id);
                    table.CheckConstraint("CK_Payment_Amount", "Amount > 0");
                    table.ForeignKey(
                        name: "FK_Payment_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "billing",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payment_User_ReceivedByUserId",
                        column: x => x.ReceivedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticResult",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceRequestId = table.Column<int>(type: "int", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: true),
                    IsAbnormal = table.Column<bool>(type: "bit", nullable: false),
                    ExternalReportId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExternalUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ReviewedByUserId = table.Column<int>(type: "int", nullable: true),
                    ReviewedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticResult", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticResult_ServiceRequest_ServiceRequestId",
                        column: x => x.ServiceRequestId,
                        principalSchema: "orders",
                        principalTable: "ServiceRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiagnosticResult_User_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Destination = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    ServiceRequestId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessage_ServiceRequest_ServiceRequestId",
                        column: x => x.ServiceRequestId,
                        principalSchema: "orders",
                        principalTable: "ServiceRequest",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RemittanceItem",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RemittanceAdviceId = table.Column<int>(type: "int", nullable: false),
                    AccountingNumber = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    ServiceCode = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountSubmitted = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    ExplanatoryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    ClaimItemId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemittanceItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemittanceItem_ClaimItem_ClaimItemId",
                        column: x => x.ClaimItemId,
                        principalSchema: "billing",
                        principalTable: "ClaimItem",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RemittanceItem_RemittanceAdvice_RemittanceAdviceId",
                        column: x => x.RemittanceAdviceId,
                        principalSchema: "billing",
                        principalTable: "RemittanceAdvice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PatientAccessLog",
                schema: "sec",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Context = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ConsentOverrideId = table.Column<int>(type: "int", nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientAccessLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientAccessLog_ConsentOverride_ConsentOverrideId",
                        column: x => x.ConsentOverrideId,
                        principalSchema: "people",
                        principalTable: "ConsentOverride",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PatientAccessLog_Patient_PatientId",
                        column: x => x.PatientId,
                        principalSchema: "people",
                        principalTable: "Patient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientAccessLog_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "sec",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointment_AppointmentTypeId",
                schema: "sched",
                table: "Appointment",
                column: "AppointmentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointment_CreatedByUserId",
                schema: "sched",
                table: "Appointment",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointment_LocationId_StartUtc",
                schema: "sched",
                table: "Appointment",
                columns: new[] { "LocationId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointment_PatientId_StartUtc",
                schema: "sched",
                table: "Appointment",
                columns: new[] { "PatientId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointment_ProviderId_StartUtc",
                schema: "sched",
                table: "Appointment",
                columns: new[] { "ProviderId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointment_RoomId",
                schema: "sched",
                table: "Appointment",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentStatusChange_AppointmentId",
                schema: "sched",
                table: "AppointmentStatusChange",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentStatusChange_UserId",
                schema: "sched",
                table: "AppointmentStatusChange",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentType_Name",
                schema: "ref",
                table: "AppointmentType",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Claim_AccountingNumber",
                schema: "billing",
                table: "Claim",
                column: "AccountingNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Claim_ClaimBatchId",
                schema: "billing",
                table: "Claim",
                column: "ClaimBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Claim_EncounterId_PaymentProgram",
                schema: "billing",
                table: "Claim",
                columns: new[] { "EncounterId", "PaymentProgram" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Claim_PatientId",
                schema: "billing",
                table: "Claim",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Claim_ProviderId",
                schema: "billing",
                table: "Claim",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Claim_Status_ProviderId",
                schema: "billing",
                table: "Claim",
                columns: new[] { "Status", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimBatch_ProviderId_BatchNumber",
                schema: "billing",
                table: "ClaimBatch",
                columns: new[] { "ProviderId", "BatchNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimItem_ClaimId",
                schema: "billing",
                table: "ClaimItem",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNote_AuthorUserId",
                schema: "care",
                table: "ClinicalNote",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNote_EncounterId",
                schema: "care",
                table: "ClinicalNote",
                column: "EncounterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsentDirective_EncounterId",
                schema: "people",
                table: "ConsentDirective",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentDirective_ExcludedProviderId",
                schema: "people",
                table: "ConsentDirective",
                column: "ExcludedProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentDirective_PatientId",
                schema: "people",
                table: "ConsentDirective",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentDirective_RecordedByUserId",
                schema: "people",
                table: "ConsentDirective",
                column: "RecordedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentOverride_ConsentDirectiveId",
                schema: "people",
                table: "ConsentOverride",
                column: "ConsentDirectiveId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentOverride_UserId",
                schema: "people",
                table: "ConsentOverride",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticResult_ReviewedByUserId",
                schema: "orders",
                table: "DiagnosticResult",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticResult_ServiceRequestId",
                schema: "orders",
                table: "DiagnosticResult",
                column: "ServiceRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticResult_Unreviewed",
                schema: "orders",
                table: "DiagnosticResult",
                column: "ReviewedUtc",
                filter: "ReviewedUtc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EligibilityCheck_CheckedByUserId",
                schema: "care",
                table: "EligibilityCheck",
                column: "CheckedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_EligibilityCheck_PatientId_CheckedUtc",
                schema: "care",
                table: "EligibilityCheck",
                columns: new[] { "PatientId", "CheckedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Encounter_AppointmentId",
                schema: "care",
                table: "Encounter",
                column: "AppointmentId",
                unique: true,
                filter: "AppointmentId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Encounter_EligibilityCheckId",
                schema: "care",
                table: "Encounter",
                column: "EligibilityCheckId");

            migrationBuilder.CreateIndex(
                name: "IX_Encounter_LocationId",
                schema: "care",
                table: "Encounter",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Encounter_PatientId_CheckInUtc",
                schema: "care",
                table: "Encounter",
                columns: new[] { "PatientId", "CheckInUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Encounter_ProviderId_CheckInUtc",
                schema: "care",
                table: "Encounter",
                columns: new[] { "ProviderId", "CheckInUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Encounter_SignedByUserId",
                schema: "care",
                table: "Encounter",
                column: "SignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_EncounterDiagnosis_DiagnosticCodeCode",
                schema: "care",
                table: "EncounterDiagnosis",
                column: "DiagnosticCodeCode");

            migrationBuilder.CreateIndex(
                name: "UX_EncounterDiagnosis_Primary",
                schema: "care",
                table: "EncounterDiagnosis",
                column: "EncounterId",
                unique: true,
                filter: "IsPrimary = 1");

            migrationBuilder.CreateIndex(
                name: "IX_EncounterService_EncounterId",
                schema: "care",
                table: "EncounterService",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_EncounterService_FeeCodeServiceCode",
                schema: "care",
                table: "EncounterService",
                column: "FeeCodeServiceCode");

            migrationBuilder.CreateIndex(
                name: "IX_EncounterService_UninsuredServiceId",
                schema: "care",
                table: "EncounterService",
                column: "UninsuredServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_EncounterId",
                schema: "billing",
                table: "Invoice",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_Number",
                schema: "billing",
                table: "Invoice",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_PatientId",
                schema: "billing",
                table: "Invoice",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_InvoiceId",
                schema: "billing",
                table: "InvoiceLine",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_UninsuredServiceId",
                schema: "billing",
                table: "InvoiceLine",
                column: "UninsuredServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Pending",
                schema: "orders",
                table: "OutboxMessage",
                column: "NextAttemptUtc",
                filter: "SentUtc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ServiceRequestId",
                schema: "orders",
                table: "OutboxMessage",
                column: "ServiceRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Patient_HealthCardNumber",
                schema: "people",
                table: "Patient",
                column: "HealthCardNumber",
                unique: true,
                filter: "HealthCardNumber IS NOT NULL AND MergedIntoPatientId IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Patient_LastName_FirstName_DateOfBirth",
                schema: "people",
                table: "Patient",
                columns: new[] { "LastName", "FirstName", "DateOfBirth" });

            migrationBuilder.CreateIndex(
                name: "IX_Patient_MergedIntoPatientId",
                schema: "people",
                table: "Patient",
                column: "MergedIntoPatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Patient_Mrn",
                schema: "people",
                table: "Patient",
                column: "Mrn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientAccessLog_ConsentOverrideId",
                schema: "sec",
                table: "PatientAccessLog",
                column: "ConsentOverrideId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientAccessLog_PatientId_AtUtc",
                schema: "sec",
                table: "PatientAccessLog",
                columns: new[] { "PatientId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientAccessLog_UserId_AtUtc",
                schema: "sec",
                table: "PatientAccessLog",
                columns: new[] { "UserId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientDuplicateCandidate_PatientAId_PatientBId",
                schema: "people",
                table: "PatientDuplicateCandidate",
                columns: new[] { "PatientAId", "PatientBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatientDuplicateCandidate_PatientBId",
                schema: "people",
                table: "PatientDuplicateCandidate",
                column: "PatientBId");

            migrationBuilder.CreateIndex(
                name: "IX_PatientDuplicateCandidate_ReviewedByUserId",
                schema: "people",
                table: "PatientDuplicateCandidate",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_InvoiceId",
                schema: "billing",
                table: "Payment",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_ReceivedByUserId",
                schema: "billing",
                table: "Payment",
                column: "ReceivedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Provider_OhipBillingNumber",
                schema: "clinic",
                table: "Provider",
                column: "OhipBillingNumber",
                unique: true,
                filter: "OhipBillingNumber IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Provider_UserId",
                schema: "clinic",
                table: "Provider",
                column: "UserId",
                unique: true,
                filter: "UserId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Referral_EncounterId",
                schema: "care",
                table: "Referral",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_Referral_PatientId",
                schema: "care",
                table: "Referral",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_RemittanceItem_AccountingNumber",
                schema: "billing",
                table: "RemittanceItem",
                column: "AccountingNumber");

            migrationBuilder.CreateIndex(
                name: "IX_RemittanceItem_ClaimItemId",
                schema: "billing",
                table: "RemittanceItem",
                column: "ClaimItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RemittanceItem_RemittanceAdviceId",
                schema: "billing",
                table: "RemittanceItem",
                column: "RemittanceAdviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Room_LocationId_Name",
                schema: "clinic",
                table: "Room",
                columns: new[] { "LocationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleException_ProviderId",
                schema: "sched",
                table: "ScheduleException",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleTemplate_LocationId",
                schema: "sched",
                table: "ScheduleTemplate",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleTemplate_ProviderId",
                schema: "sched",
                table: "ScheduleTemplate",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequest_EncounterId",
                schema: "orders",
                table: "ServiceRequest",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequest_PatientId_CreatedUtc",
                schema: "orders",
                table: "ServiceRequest",
                columns: new[] { "PatientId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequest_RequesterProviderId",
                schema: "orders",
                table: "ServiceRequest",
                column: "RequesterProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequest_TargetSystem_ExternalId",
                schema: "orders",
                table: "ServiceRequest",
                columns: new[] { "TargetSystem", "ExternalId" },
                unique: true,
                filter: "ExternalId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UninsuredService_Code",
                schema: "ref",
                table: "UninsuredService",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_UserName",
                schema: "sec",
                table: "User",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VitalSigns_EncounterId",
                schema: "care",
                table: "VitalSigns",
                column: "EncounterId");

            migrationBuilder.CreateIndex(
                name: "IX_VitalSigns_TakenByUserId",
                schema: "care",
                table: "VitalSigns",
                column: "TakenByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppointmentStatusChange",
                schema: "sched");

            migrationBuilder.DropTable(
                name: "ClinicalNote",
                schema: "care");

            migrationBuilder.DropTable(
                name: "DiagnosticResult",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "EncounterDiagnosis",
                schema: "care");

            migrationBuilder.DropTable(
                name: "EncounterService",
                schema: "care");

            migrationBuilder.DropTable(
                name: "InboundMessage",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "InvoiceLine",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "PatientAccessLog",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "PatientDuplicateCandidate",
                schema: "people");

            migrationBuilder.DropTable(
                name: "Payment",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "Province",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "Referral",
                schema: "care");

            migrationBuilder.DropTable(
                name: "RemittanceItem",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "ScheduleException",
                schema: "sched");

            migrationBuilder.DropTable(
                name: "ScheduleTemplate",
                schema: "sched");

            migrationBuilder.DropTable(
                name: "VitalSigns",
                schema: "care");

            migrationBuilder.DropTable(
                name: "DiagnosticCode",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "FeeCode",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "UninsuredService",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "ServiceRequest",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "ConsentOverride",
                schema: "people");

            migrationBuilder.DropTable(
                name: "Invoice",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "ClaimItem",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "RemittanceAdvice",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "ConsentDirective",
                schema: "people");

            migrationBuilder.DropTable(
                name: "Claim",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "ClaimBatch",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "Encounter",
                schema: "care");

            migrationBuilder.DropTable(
                name: "Appointment",
                schema: "sched");

            migrationBuilder.DropTable(
                name: "EligibilityCheck",
                schema: "care");

            migrationBuilder.DropTable(
                name: "AppointmentType",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "Provider",
                schema: "clinic");

            migrationBuilder.DropTable(
                name: "Room",
                schema: "clinic");

            migrationBuilder.DropTable(
                name: "Patient",
                schema: "people");

            migrationBuilder.DropTable(
                name: "User",
                schema: "sec");

            migrationBuilder.DropTable(
                name: "Location",
                schema: "clinic");
        }
    }
}
