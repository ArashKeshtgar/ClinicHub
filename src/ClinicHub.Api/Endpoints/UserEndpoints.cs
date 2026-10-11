using System.Security.Claims;
using ClinicHub.Api.Auth;
using ClinicHub.Api.Catalog;
using ClinicHub.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Role = ClinicHub.Api.Catalog.Role;

namespace ClinicHub.Api.Endpoints;

/// <summary>
/// A clinic's own staff and roles (permission users.manage) and its security log (audit.view). An account
/// can work at several clinics: a clinic administrator manages its roles there, and changes the account
/// itself (name, password, two-factor) only when the person works at no other clinic.
/// </summary>
public static class UserEndpoints
{
    public record UserDto(
        int Id, string UserName, string DisplayName, string? Credentials, bool IsActive, int[] RoleIds, string[] Roles, string[] Permissions,
        string[] OtherClinics, bool TwoFactorEnabled, bool TwoFactorRequired, bool MustChangePassword, bool IsLockedOut,
        DateTime? LastLoginUtc, DateTime CreatedUtc);
    public record CreateUser(string UserName, string? DisplayName, string? Credentials, int[] RoleIds, string? Password);
    public record UpdateUser(string DisplayName, string? Credentials, int[] RoleIds);
    public record ResetPassword(string Password);
    public record RoleDto(int Id, string Name, string? Description, string[] Permissions, bool RequireTwoFactor, bool IsSystem, int UserCount);
    public record SaveRole(string Name, string? Description, string[] Permissions, bool RequireTwoFactor);
    public record AuditRow(long Id, DateTime AtUtc, string Type, string? Actor, string? Target, string? Detail, string? IpAddress);
    public record AccessRow(long Id, DateTime AtUtc, string User, string Patient, string Mrn, string Action, string Context, string? IpAddress, bool Override);
    public record ClinicDto(int Id, string Slug, string Name, string DatabaseName, bool IsActive, int Members, DateTime CreatedUtc);
    public record CreateClinic(string Slug, string Name);

    public const int MinPasswordLength = 10;

    public static string? PasswordProblem(string? password, string? userName = null) =>
        password is null || password.Length < MinPasswordLength ? $"Use at least {MinPasswordLength} characters."
        : password.Length > 128 ? "Use at most 128 characters."
        : password.Distinct().Count() < 4 ? "Use more different characters."
        : userName is { Length: >= 3 } && password.Contains(userName, StringComparison.OrdinalIgnoreCase) ? "Don't include your user name."
        : null;

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api").WithTags("Users").RequireAuthorization(Permissions.UsersManage).AddEndpointFilter(DemoReadOnly);

        admin.MapGet("/permissions", () => Permissions.Catalog);

        admin.MapGet("/users", async (ClaimsPrincipal me, CatalogDbContext catalog, TimeProvider clock) =>
        {
            var clinicId = me.RequiredClinicId();
            var members = await Members(catalog, clinicId).AsNoTracking().OrderBy(a => a.DisplayName).ToListAsync();
            return members.Select(a => ToDto(a, clinicId, clock.GetUtcNow().UtcDateTime));
        });

        // A new person gets an account with a temporary password; someone who already has a ClinicHub account
        // (they work at another clinic) is added with their own password and two-factor.
        admin.MapPost("/users", async (CreateUser req, ClaimsPrincipal me, CatalogDbContext catalog, ClinicDatabases clinics,
            IPasswordHasher<Account> hasher, SecurityLog log, TimeProvider clock) =>
        {
            var clinicId = me.RequiredClinicId();
            var now = clock.GetUtcNow().UtcDateTime;
            var errors = new Dictionary<string, string[]>();
            var userName = req.UserName?.Trim() ?? "";
            var roles = await ClinicRoles(catalog, clinicId, req.RoleIds, errors);
            var account = await catalog.Accounts.WithRoles().SingleOrDefaultAsync(a => a.UserName == userName);
            if (account is not null)
            {
                if (account.Roles.Any(r => r.Role.ClinicId == clinicId))
                    errors["userName"] = ["Already a member of this clinic."];
            }
            else
            {
                if (userName.Length is < 3 or > 64 || !userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
                    errors["userName"] = ["3–64 letters, digits, dots, dashes or underscores."];
                if (string.IsNullOrWhiteSpace(req.DisplayName) || req.DisplayName.Trim().Length > 128)
                    errors["displayName"] = ["Required, at most 128 characters."];
                if (PasswordProblem(req.Password, userName) is { } p)
                    errors["password"] = [p];
            }
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var created = account is null;
            if (account is null)
            {
                account = new Account
                {
                    UserName = userName, DisplayName = req.DisplayName!.Trim(), Credentials = Blank(req.Credentials), CreatedUtc = now,
                    // The admin knows this password; the person replaces it at first sign-in.
                    MustChangePassword = true,
                };
                account.PasswordHash = hasher.HashPassword(account, req.Password!);
                catalog.Accounts.Add(account);
            }
            account.Roles.AddRange(roles.Select(r => new AccountRole { Role = r }));
            await catalog.SaveChangesAsync();
            log.Add(created ? SecurityEventTypes.UserCreated : SecurityEventTypes.MemberAdded, me.RequiredUserId(), account.Id,
                $"Roles: {string.Join(", ", roles.Select(r => r.Name))}", clinicId);
            await catalog.SaveChangesAsync();
            await using (var clinicDb = await clinics.OpenAsync(clinicId))
                await StaffSync.EnsureAsync(clinicDb, account, member: true, now);
            var saved = await Members(catalog, clinicId).AsNoTracking().SingleAsync(a => a.Id == account.Id);
            return Results.Created($"/api/users/{account.Id}", ToDto(saved, clinicId, now));
        });

        // Roles in this clinic; no roles removes the person from the clinic (their account and history stay).
        admin.MapPut("/users/{id:int}", async (int id, UpdateUser req, ClaimsPrincipal me, CatalogDbContext catalog, ClinicDatabases clinics,
            SecurityLog log, TimeProvider clock) =>
        {
            var clinicId = me.RequiredClinicId();
            var account = await Members(catalog, clinicId).SingleOrDefaultAsync(a => a.Id == id);
            if (account is null)
                return Results.NotFound();
            var errors = new Dictionary<string, string[]>();
            var roleIds = req.RoleIds ?? [];
            var roles = roleIds.Length == 0 ? [] : await ClinicRoles(catalog, clinicId, roleIds, errors);
            var ownAccount = OnlyHere(account, clinicId);
            if (ownAccount && (string.IsNullOrWhiteSpace(req.DisplayName) || req.DisplayName.Trim().Length > 128))
                errors["displayName"] = ["Required, at most 128 characters."];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var before = Describe(account, clinicId);
            if (ownAccount)
            {
                account.DisplayName = req.DisplayName.Trim();
                account.Credentials = Blank(req.Credentials);
            }
            account.Roles.RemoveAll(ar => ar.Role.ClinicId == clinicId && roles.All(r => r.Id != ar.RoleId));
            foreach (var r in roles.Where(r => account.Roles.All(ar => ar.RoleId != r.Id)))
                account.Roles.Add(new AccountRole { AccountId = account.Id, RoleId = r.Id, Role = r });
            if (await LastAdminProblem(catalog, clinicId, account, null) is { } problem)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["roleIds"] = [problem] });

            log.Add(roles.Count == 0 ? SecurityEventTypes.MemberRemoved : SecurityEventTypes.UserUpdated, me.RequiredUserId(), account.Id,
                $"{before} → {Describe(account, clinicId)}", clinicId);
            await catalog.SaveChangesAsync();
            await using (var clinicDb = await clinics.OpenAsync(clinicId))
                await StaffSync.EnsureAsync(clinicDb, account, member: roles.Count > 0, clock.GetUtcNow().UtcDateTime);
            return roles.Count == 0 ? Results.NoContent()
                : Results.Ok(ToDto(await Members(catalog, clinicId).AsNoTracking().SingleAsync(a => a.Id == id), clinicId, clock.GetUtcNow().UtcDateTime));
        });

        admin.MapPost("/users/{id:int}/password", async (int id, ResetPassword req, ClaimsPrincipal me, CatalogDbContext catalog,
            IPasswordHasher<Account> hasher, SecurityLog log) =>
        {
            if (await OwnAccount(catalog, me, id) is not { } account)
                return NotOurs();
            if (PasswordProblem(req.Password, account.UserName) is { } p)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [p] });
            account.PasswordHash = hasher.HashPassword(account, req.Password);
            account.MustChangePassword = true;
            account.FailedLoginCount = 0;
            account.LockoutEndUtc = null;
            account.SecurityStamp = AuthEndpoints.NewStamp();
            log.Add(SecurityEventTypes.PasswordReset, me.RequiredUserId(), id, clinicId: me.RequiredClinicId());
            await catalog.SaveChangesAsync();
            return Results.NoContent();
        });

        admin.MapPost("/users/{id:int}/unlock", async (int id, ClaimsPrincipal me, CatalogDbContext catalog, SecurityLog log) =>
        {
            // Unlocking only ends a lockout early; any administrator of a clinic the person works at may do it.
            var account = await Members(catalog, me.RequiredClinicId()).SingleOrDefaultAsync(a => a.Id == id);
            if (account is null)
                return Results.NotFound();
            account.FailedLoginCount = 0;
            account.LockoutEndUtc = null;
            log.Add(SecurityEventTypes.UserUnlocked, me.RequiredUserId(), id, clinicId: me.RequiredClinicId());
            await catalog.SaveChangesAsync();
            return Results.NoContent();
        });

        // For a lost phone: the person sets two-factor up again at next sign-in if a role requires it.
        admin.MapPost("/users/{id:int}/two-factor/reset", async (int id, ClaimsPrincipal me, CatalogDbContext catalog, SecurityLog log) =>
        {
            if (await OwnAccount(catalog, me, id) is not { } account)
                return NotOurs();
            account.TwoFactorEnabled = false;
            account.TwoFactorSecret = null;
            account.SecurityStamp = AuthEndpoints.NewStamp();
            log.Add(SecurityEventTypes.TwoFactorReset, me.RequiredUserId(), id, clinicId: me.RequiredClinicId());
            await catalog.SaveChangesAsync();
            return Results.NoContent();
        });

        admin.MapPost("/users/{id:int}/sessions/revoke", async (int id, ClaimsPrincipal me, CatalogDbContext catalog, SecurityLog log) =>
        {
            var account = await Members(catalog, me.RequiredClinicId()).SingleOrDefaultAsync(a => a.Id == id);
            if (account is null)
                return Results.NotFound();
            account.SecurityStamp = AuthEndpoints.NewStamp();
            log.Add(SecurityEventTypes.SessionsRevoked, me.RequiredUserId(), id, clinicId: me.RequiredClinicId());
            await catalog.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- roles of this clinic ----

        admin.MapGet("/roles", async (ClaimsPrincipal me, CatalogDbContext catalog) =>
            (await catalog.Roles.AsNoTracking().Include(r => r.Accounts).Where(r => r.ClinicId == me.RequiredClinicId())
                .OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name).ToListAsync()).Select(ToDto));

        admin.MapPost("/roles", async (SaveRole req, ClaimsPrincipal me, CatalogDbContext catalog, SecurityLog log) =>
        {
            var clinicId = me.RequiredClinicId();
            if (await ValidateRole(catalog, clinicId, req, null) is { } problem)
                return problem;
            var role = new Role { ClinicId = clinicId, Name = req.Name.Trim(), Description = Blank(req.Description), Permissions = Permissions.Format(req.Permissions), RequireTwoFactor = req.RequireTwoFactor };
            catalog.Roles.Add(role);
            log.Add(SecurityEventTypes.RoleCreated, me.RequiredUserId(), detail: $"{role.Name}: {role.Permissions}", clinicId: clinicId);
            await catalog.SaveChangesAsync();
            return Results.Created($"/api/roles/{role.Id}", ToDto(role));
        });

        admin.MapPut("/roles/{id:int}", async (int id, SaveRole req, ClaimsPrincipal me, CatalogDbContext catalog, SecurityLog log) =>
        {
            var clinicId = me.RequiredClinicId();
            var role = await catalog.Roles.Include(r => r.Accounts).SingleOrDefaultAsync(r => r.Id == id && r.ClinicId == clinicId);
            if (role is null)
                return Results.NotFound();
            if (await ValidateRole(catalog, clinicId, req, role) is { } problem)
                return problem;
            var isAdministrator = role.IsSystem && role.Name == SystemRoles.Administrator;
            var before = $"{role.Name}: {role.Permissions}{(role.RequireTwoFactor ? " +2FA" : "")}";
            if (!role.IsSystem)
                role.Name = req.Name.Trim();
            role.Description = Blank(req.Description);
            role.Permissions = isAdministrator ? Permissions.Format(Permissions.All) : Permissions.Format(req.Permissions);
            role.RequireTwoFactor = req.RequireTwoFactor;
            if (await LastAdminProblem(catalog, clinicId, null, role) is { } last)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["permissions"] = [last] });
            log.Add(SecurityEventTypes.RoleUpdated, me.RequiredUserId(), detail: $"{before} → {role.Name}: {role.Permissions}{(role.RequireTwoFactor ? " +2FA" : "")}", clinicId: clinicId);
            await catalog.SaveChangesAsync();
            return Results.Ok(ToDto(role));
        });

        admin.MapDelete("/roles/{id:int}", async (int id, ClaimsPrincipal me, CatalogDbContext catalog, SecurityLog log) =>
        {
            var clinicId = me.RequiredClinicId();
            var role = await catalog.Roles.Include(r => r.Accounts).SingleOrDefaultAsync(r => r.Id == id && r.ClinicId == clinicId);
            if (role is null)
                return Results.NotFound();
            if (role.IsSystem)
                return Results.Problem("Built-in roles can't be deleted.", statusCode: StatusCodes.Status409Conflict);
            if (role.Accounts.Count > 0)
                return Results.Problem($"{role.Accounts.Count} user(s) still have this role.", statusCode: StatusCodes.Status409Conflict);
            catalog.Roles.Remove(role);
            log.Add(SecurityEventTypes.RoleDeleted, me.RequiredUserId(), detail: role.Name, clinicId: clinicId);
            await catalog.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- logs ----

        // Sign-ins and account changes of this clinic's people: events in this clinic, and account-wide ones (sign-in, password) of its members.
        app.MapGet("/api/audit", async (string? type, int? userId, long? before, ClaimsPrincipal me, CatalogDbContext catalog) =>
        {
            var clinicId = me.RequiredClinicId();
            var members = catalog.AccountRoles.Where(ar => ar.Role.ClinicId == clinicId).Select(ar => ar.AccountId);
            var q = catalog.SecurityEvents.AsNoTracking().Where(e => e.ClinicId == clinicId
                || (e.ClinicId == null && ((e.ActorAccountId != null && members.Contains(e.ActorAccountId.Value)) || (e.TargetAccountId != null && members.Contains(e.TargetAccountId.Value)))));
            if (!string.IsNullOrEmpty(type)) q = q.Where(e => e.Type == type);
            if (userId is not null) q = q.Where(e => e.ActorAccountId == userId || e.TargetAccountId == userId);
            if (before is not null) q = q.Where(e => e.Id < before);
            return await q.OrderByDescending(e => e.Id).Take(200)
                .Select(e => new AuditRow(e.Id, e.AtUtc, e.Type,
                    catalog.Accounts.Where(a => a.Id == e.ActorAccountId).Select(a => a.DisplayName).FirstOrDefault(),
                    catalog.Accounts.Where(a => a.Id == e.TargetAccountId).Select(a => a.DisplayName).FirstOrDefault(),
                    e.Detail, e.IpAddress)).ToListAsync();
        }).WithTags("Audit").RequireAuthorization(Permissions.AuditView);

        // PHIPA: who opened which patient's record, from the clinic's own database.
        app.MapGet("/api/audit/access", async (int? patientId, int? userId, long? before, ClinicHubDbContext db) =>
        {
            var q = db.PatientAccessLog.AsNoTracking();
            // A patient's log includes the records merged into theirs: same person, opened under an older MRN.
            if (patientId is not null) q = q.Where(l => l.PatientId == patientId || db.Patients.Any(p => p.Id == l.PatientId && p.MergedIntoPatientId == patientId));
            if (userId is not null) q = q.Where(l => l.UserId == userId);
            if (before is not null) q = q.Where(l => l.Id < before);
            return await q.OrderByDescending(l => l.Id).Take(200)
                .Select(l => new AccessRow(l.Id, l.AtUtc,
                    db.Users.Where(u => u.Id == l.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? "",
                    db.Patients.Where(p => p.Id == l.PatientId).Select(p => p.LastName + ", " + p.FirstName).FirstOrDefault() ?? "",
                    db.Patients.Where(p => p.Id == l.PatientId).Select(p => p.Mrn).FirstOrDefault() ?? "",
                    l.Action, l.Context, l.IpAddress, l.ConsentOverrideId != null)).ToListAsync();
        }).WithTags("Audit").RequireAuthorization(Permissions.AuditView);

        // ---- the platform: clinics ----

        var platform = app.MapGroup("/api/platform").WithTags("Platform").RequireAuthorization(AuthSetup.PlatformAdmin).AddEndpointFilter(DemoReadOnly);

        platform.MapGet("/clinics", async (CatalogDbContext catalog) =>
            await catalog.Clinics.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new ClinicDto(c.Id, c.Slug, c.Name, c.DatabaseName, c.IsActive,
                    catalog.AccountRoles.Where(ar => ar.Role.ClinicId == c.Id).Select(ar => ar.AccountId).Distinct().Count(), c.CreatedUtc))
                .ToListAsync());

        // A new clinic: its catalog entry and built-in roles, its own empty database, and the creator as its first administrator.
        platform.MapPost("/clinics", async (CreateClinic req, ClaimsPrincipal me, CatalogDbContext catalog, ClinicProvisioning provisioning) =>
        {
            var errors = new Dictionary<string, string[]>();
            var slug = (req.Slug ?? "").Trim().ToLowerInvariant();
            if (slug.Length is < 2 or > 40 || !slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                errors["slug"] = ["2–40 lower-case letters, digits or dashes."];
            else if (await catalog.Clinics.AnyAsync(c => c.Slug == slug))
                errors["slug"] = ["Another clinic uses this name."];
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 120)
                errors["name"] = ["Required, at most 120 characters."];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);
            var clinic = await provisioning.CreateAsync(slug, req.Name.Trim(), me.RequiredUserId());
            return Results.Created($"/api/platform/clinics/{clinic.Id}", new ClinicDto(clinic.Id, clinic.Slug, clinic.Name, clinic.DatabaseName, true, 1, clinic.CreatedUtc));
        });
    }

    /// <summary>In the public demo every visitor can be an administrator, so accounts, roles, passwords and clinics stay as seeded.</summary>
    public static async ValueTask<object?> DemoReadOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var demo = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value;
        if (demo.Enabled && demo.LockAccounts && !HttpMethods.IsGet(ctx.HttpContext.Request.Method))
            return Results.Problem("Accounts, roles and passwords can't be changed in the public demo.", statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    private static IQueryable<Account> Members(CatalogDbContext catalog, int clinicId) =>
        catalog.Accounts.WithRoles().Where(a => a.Roles.Any(ar => ar.Role.ClinicId == clinicId));

    private static bool OnlyHere(Account a, int clinicId) => a.Roles.All(ar => ar.Role.ClinicId == clinicId);

    /// <summary>The account, if this clinic is the only one it works at, so this clinic's administrator may change it.</summary>
    private static async Task<Account?> OwnAccount(CatalogDbContext catalog, ClaimsPrincipal me, int id)
    {
        var clinicId = me.RequiredClinicId();
        var account = await Members(catalog, clinicId).SingleOrDefaultAsync(a => a.Id == id);
        return account is not null && OnlyHere(account, clinicId) ? account : null;
    }

    private static IResult NotOurs() =>
        Results.Problem("This person also works at another clinic, so only they or a platform administrator can change their account.",
            statusCode: StatusCodes.Status403Forbidden);

    private static UserDto ToDto(Account a, int clinicId, DateTime now)
    {
        var access = Access.For(a, clinicId);
        return new UserDto(a.Id, a.UserName, a.DisplayName, a.Credentials, a.IsActive,
            a.Roles.Where(ar => ar.Role.ClinicId == clinicId).Select(ar => ar.RoleId).ToArray(), access.Roles,
            Access.For(new Account { Roles = a.Roles }, clinicId).Permissions,
            a.Roles.Where(ar => ar.Role.ClinicId != clinicId).Select(ar => ar.Role.Clinic.Name).Distinct().ToArray(),
            a.TwoFactorEnabled, access.TwoFactorRequired, a.MustChangePassword, a.LockoutEndUtc > now, a.LastLoginUtc, a.CreatedUtc);
    }

    private static RoleDto ToDto(Role r) =>
        new(r.Id, r.Name, r.Description, r.IsSystem && r.Name == SystemRoles.Administrator ? Permissions.All : Permissions.Parse(r.Permissions),
            r.RequireTwoFactor, r.IsSystem, r.Accounts.Count);

    private static async Task<List<Role>> ClinicRoles(CatalogDbContext catalog, int clinicId, int[]? roleIds, Dictionary<string, string[]> errors)
    {
        var ids = (roleIds ?? []).Distinct().ToArray();
        var roles = await catalog.Roles.Where(r => ids.Contains(r.Id) && r.ClinicId == clinicId).ToListAsync();
        if (ids.Length == 0)
            errors["roleIds"] = ["Give the person at least one role."];
        else if (roles.Count != ids.Length)
            errors["roleIds"] = ["Unknown role."];
        return roles;
    }

    private static async Task<IResult?> ValidateRole(CatalogDbContext catalog, int clinicId, SaveRole req, Role? existing)
    {
        var errors = new Dictionary<string, string[]>();
        var name = req.Name?.Trim() ?? "";
        if (existing is not { IsSystem: true })
        {
            if (name.Length is < 2 or > 64)
                errors["name"] = ["2–64 characters."];
            else if (await catalog.Roles.AnyAsync(r => r.ClinicId == clinicId && r.Name == name && r.Id != (existing == null ? 0 : existing.Id)))
                errors["name"] = ["Another role has this name."];
        }
        if ((req.Description?.Length ?? 0) > 300)
            errors["description"] = ["At most 300 characters."];
        if ((req.Permissions ?? []).Any(p => !Permissions.All.Contains(p)))
            errors["permissions"] = ["Unknown permission."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    /// <summary>Someone must always be able to manage this clinic's users, or it is locked for good.</summary>
    private static async Task<string?> LastAdminProblem(CatalogDbContext catalog, int clinicId, Account? changedAccount, Role? changedRole)
    {
        var others = await Members(catalog, clinicId).AsNoTracking()
            .Where(a => a.IsActive && (changedAccount == null || a.Id != changedAccount.Id)).ToListAsync();
        if (changedRole is not null)
            foreach (var ar in others.SelectMany(a => a.Roles).Where(ar => ar.RoleId == changedRole.Id))
                ar.Role = changedRole;
        var candidates = changedAccount is { IsActive: true } ? others.Append(changedAccount) : others;
        return candidates.Any(a => Access.For(new Account { Roles = a.Roles }, clinicId).Permissions.Contains(Permissions.UsersManage))
            ? null
            : "At least one person must keep the \"Manage users and roles\" permission in this clinic.";
    }

    private static string Describe(Account a, int clinicId) =>
        $"roles: {string.Join(", ", a.Roles.Where(ar => ar.Role?.ClinicId == clinicId).Select(ar => ar.Role.Name).DefaultIfEmpty("none"))}";

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
