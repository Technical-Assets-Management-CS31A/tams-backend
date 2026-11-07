using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Students;

public sealed record SectionBody(string? Code, string? Program, short? YearLevel, string? SchoolYear, Guid? AdviserUserId);
public sealed record StudentBody(string? StudentNo, string? FirstName, string? LastName, Guid? SectionId, RecordStatus? Status);

public static class StudentEndpoints
{
    static readonly string[] Writers = [Roles.Admin, Roles.TechStaff];
    static readonly string[] Readers = [Roles.Admin, Roles.TechStaff, Roles.Custodian];

    public static IEndpointRouteBuilder MapStudentEndpoints(this IEndpointRouteBuilder app)
    {
        var sections = app.MapGroup("/sections").WithTags("Sections");
        sections.MapGet("/", ListSections).RequireRoles(Readers);
        sections.MapGet("/{id:guid}", GetSection).RequireRoles(Readers);
        sections.MapPost("/", CreateSection).RequireRoles(Writers);
        sections.MapPut("/{id:guid}", UpdateSection).RequireRoles(Writers);

        var students = app.MapGroup("/students").WithTags("Students");
        students.MapGet("/", ListStudents).RequireRoles(Readers);
        students.MapGet("/{id:guid}", GetStudent).RequireRoles(Readers);
        students.MapPost("/", CreateStudent).RequireRoles(Writers);
        students.MapPut("/{id:guid}", UpdateStudent).RequireRoles(Writers);
        students.MapDelete("/{id:guid}", DeleteStudent).RequireRoles(Writers);
        students.MapPost("/{id:guid}/restore", RestoreStudent).RequireRoles(Roles.Admin);
        // PRD 6.3 / TAMS-35: the literal route is POST /students:import (a group prefix would insert a slash).
        app.MapPost("/students:import", ImportRoster).RequireRoles(Writers)
            .WithMetadata(new RequestSizeLimitAttribute(RosterImport.MaxBytes + 64 * 1024))
            .Accepts<IFormFile>("multipart/form-data")
            .WithTags("Students")
            .WithSummary("Import a student roster CSV (multipart field 'file').");
        return app;
    }

    // ---- helpers ----

    static Guid? ActorId(ClaimsPrincipal u) =>
        Guid.TryParse(u.FindFirstValue(ClaimTypes.NameIdentifier) ?? u.FindFirstValue("sub"), out var id) ? id : null;

    static void Audit(TamsDbContext db, HttpContext http, string action, string entityType, Guid entityId,
        object? before = null, object? after = null) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = ActorId(http.User), Action = action, EntityType = entityType, EntityId = entityId,
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = after is null ? null : JsonSerializer.Serialize(after),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });

    static bool CanSeeDeleted(ClaimsPrincipal u) => u.IsInRole(Roles.Admin) || u.IsInRole(Roles.TechStaff);

    static (int Page, int Size) Paging(int? page, int? pageSize) =>
        (Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 50, 1, 200));

    static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    // ---- sections ----

    static object SectionDto(Section s) => new { s.Id, s.Code, s.Program, s.YearLevel, s.SchoolYear, s.AdviserUserId };

    static async Task<IResult> ListSections(TamsDbContext db, string? schoolYear, string? program, string? q,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging(page, pageSize);
        var query = db.Sections.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(schoolYear)) query = query.Where(s => s.SchoolYear == schoolYear);
        if (!string.IsNullOrWhiteSpace(program)) query = query.Where(s => s.Program == program);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => EF.Functions.ILike(s.Code, $"%{q.Trim()}%"));

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(s => s.SchoolYear).ThenBy(s => s.Code)
            .Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new { items = items.Select(SectionDto), page = p, pageSize = size, total });
    }

    static async Task<IResult> GetSection(Guid id, TamsDbContext db, CancellationToken ct) =>
        await db.Sections.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is { } s
            ? Results.Ok(SectionDto(s))
            : Problems.NotFound("SECTION_NOT_FOUND", "Section not found.");

    static Dictionary<string, string[]> ValidateSection(SectionBody b)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(b.Code) || b.Code.Trim().Length > 30) e["code"] = ["Required, up to 30 characters."];
        if (string.IsNullOrWhiteSpace(b.Program) || b.Program.Trim().Length > 20) e["program"] = ["Required, up to 20 characters."];
        if (b.YearLevel is null or < 1 or > 12) e["yearLevel"] = ["Required, 1 to 12."];
        if (b.SchoolYear is null || !System.Text.RegularExpressions.Regex.IsMatch(b.SchoolYear, @"^\d{4}-\d{4}$")
            || int.Parse(b.SchoolYear[5..]) != int.Parse(b.SchoolYear[..4]) + 1)
            e["schoolYear"] = ["Required, format 2025-2026."];
        return e;
    }

    static async Task<IResult> CreateSection(SectionBody b, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        if (ValidateSection(b) is { Count: > 0 } errors) return Problems.Validation(errors);
        var code = b.Code!.Trim();
        if (await db.Sections.AnyAsync(s => s.Code == code && s.SchoolYear == b.SchoolYear, ct))
            return Problems.Conflict("SECTION_EXISTS", "A section with this code already exists for that school year.");

        var s = new Section
        {
            Id = Guid.NewGuid(), Code = code, Program = b.Program!.Trim(), YearLevel = b.YearLevel!.Value,
            SchoolYear = b.SchoolYear!, AdviserUserId = b.AdviserUserId,
        };
        db.Sections.Add(s);
        Audit(db, http, "SECTION_CREATE", "section", s.Id, after: SectionDto(s));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Problems.Conflict("SECTION_EXISTS", "A section with this code already exists for that school year.");
        }
        catch (DbUpdateException)
        {
            return Problems.BadRequest("SECTION_REFERENCE_INVALID", "adviserUserId does not match an existing user.");
        }
        return Results.Created($"/sections/{s.Id}", SectionDto(s));
    }

    static async Task<IResult> UpdateSection(Guid id, SectionBody b, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        if (ValidateSection(b) is { Count: > 0 } errors) return Problems.Validation(errors);
        var s = await db.Sections.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Problems.NotFound("SECTION_NOT_FOUND", "Section not found.");

        var before = SectionDto(s);
        s.Code = b.Code!.Trim();
        s.Program = b.Program!.Trim();
        s.YearLevel = b.YearLevel!.Value;
        s.SchoolYear = b.SchoolYear!;
        s.AdviserUserId = b.AdviserUserId;
        Audit(db, http, "SECTION_UPDATE", "section", s.Id, before, SectionDto(s));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Problems.Conflict("SECTION_EXISTS", "A section with this code already exists for that school year.");
        }
        catch (DbUpdateException)
        {
            return Problems.BadRequest("SECTION_REFERENCE_INVALID", "adviserUserId does not match an existing user.");
        }
        return Results.Ok(SectionDto(s));
    }

    // ---- students ----

    // Projection: no biometric data (fingerprint templates are never loaded or returned).
    static IQueryable<object> StudentRows(TamsDbContext db, IQueryable<Student> q) =>
        from s in q
        join sec in db.Sections on s.SectionId equals sec.Id
        select (object)new
        {
            s.Id, s.StudentNo, s.FirstName, s.LastName, s.SectionId,
            Section = new { sec.Code, sec.Program, sec.YearLevel, sec.SchoolYear },
            s.Standing, s.ActiveWarningCount, s.ActiveMarkCount, s.HasActiveHold,
            s.Status, s.CreatedAt, s.UpdatedAt, s.DeletedAt,
        };

    static async Task<IResult> ListStudents(TamsDbContext db, ClaimsPrincipal user, string? q, Guid? sectionId,
        string? schoolYear, RecordStatus? status, bool? includeDeleted, int? page, int? pageSize, CancellationToken ct)
    {
        if (includeDeleted == true && !CanSeeDeleted(user))
            return Problems.Forbidden("INCLUDE_DELETED_FORBIDDEN", "Only ADMIN and TECH_STAFF can list deleted students.");

        var (p, size) = Paging(page, pageSize);
        var query = db.Students.AsNoTracking().AsQueryable();
        if (includeDeleted != true) query = query.Where(s => s.DeletedAt == null);
        if (sectionId is { } sid) query = query.Where(s => s.SectionId == sid);
        if (status is { } st) query = query.Where(s => s.Status == st);
        if (!string.IsNullOrWhiteSpace(schoolYear))
            query = query.Where(s => db.Sections.Any(x => x.Id == s.SectionId && x.SchoolYear == schoolYear));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(s => EF.Functions.ILike(s.StudentNo, like) || EF.Functions.ILike(s.FirstName, like)
                || EF.Functions.ILike(s.LastName, like));
        }

        var total = await query.CountAsync(ct);
        var items = await StudentRows(db, query.OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.StudentNo)
            .Skip((p - 1) * size).Take(size)).ToListAsync(ct);
        return Results.Ok(new { items, page = p, pageSize = size, total });
    }

    static async Task<IResult> GetStudent(Guid id, TamsDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var showDeleted = CanSeeDeleted(user);
        var row = await StudentRows(db, db.Students.AsNoTracking().Where(s => s.Id == id && (showDeleted || s.DeletedAt == null)))
            .FirstOrDefaultAsync(ct);
        return row is null ? Problems.NotFound("STUDENT_NOT_FOUND", "Student not found.") : Results.Ok(row);
    }

    static object StudentAudit(Student s) => new { s.StudentNo, s.SectionId, s.Status, s.DeletedAt };

    /// <summary>PRD 6.3 POST /students: one student without a roster file (late enrollees).</summary>
    static async Task<IResult> CreateStudent(StudentBody b, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(b.StudentNo) || b.StudentNo.Trim().Length > 20) errors["studentNo"] = ["Required, up to 20 characters."];
        if (string.IsNullOrWhiteSpace(b.FirstName) || b.FirstName.Trim().Length > 80) errors["firstName"] = ["Required, up to 80 characters."];
        if (string.IsNullOrWhiteSpace(b.LastName) || b.LastName.Trim().Length > 80) errors["lastName"] = ["Required, up to 80 characters."];
        if (b.SectionId is null) errors["sectionId"] = ["Required."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var no = b.StudentNo!.Trim();
        if (await db.Students.AnyAsync(x => x.DeletedAt == null && x.StudentNo == no, ct))
            return Problems.Conflict("STUDENT_NO_TAKEN", "Another student already has this student number.");
        if (!await db.Sections.AnyAsync(x => x.Id == b.SectionId, ct))
            return Problems.BadRequest("SECTION_NOT_FOUND", "sectionId does not match an existing section.");

        var now = DateTimeOffset.UtcNow;
        var s = new Student
        {
            Id = Guid.NewGuid(), StudentNo = no, FirstName = b.FirstName!.Trim(), LastName = b.LastName!.Trim(),
            SectionId = b.SectionId!.Value, Status = b.Status ?? RecordStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        db.Students.Add(s);
        Audit(db, http, "STUDENT_CREATE", "student", s.Id, after: StudentAudit(s));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Problems.Conflict("STUDENT_NO_TAKEN", "Another student already has this student number.");
        }
        return Results.Created($"/api/students/{s.Id}", await StudentRows(db, db.Students.AsNoTracking().Where(x => x.Id == s.Id)).FirstAsync(ct));
    }

    static async Task<IResult> UpdateStudent(Guid id, StudentBody b, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(b.FirstName) || b.FirstName.Trim().Length > 80) errors["firstName"] = ["Required, up to 80 characters."];
        if (string.IsNullOrWhiteSpace(b.LastName) || b.LastName.Trim().Length > 80) errors["lastName"] = ["Required, up to 80 characters."];
        if (b.StudentNo is not null && (b.StudentNo.Trim().Length is 0 or > 20)) errors["studentNo"] = ["Up to 20 characters."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (s is null) return Problems.NotFound("STUDENT_NOT_FOUND", "Student not found.");

        var before = StudentAudit(s);
        var no = b.StudentNo?.Trim();
        if (no is not null && no != s.StudentNo)
        {
            if (await db.Students.AnyAsync(x => x.DeletedAt == null && x.StudentNo == no, ct))
                return Problems.Conflict("STUDENT_NO_TAKEN", "Another student already has this student number.");
            s.StudentNo = no;
        }
        if (b.SectionId is { } secId && secId != s.SectionId)
        {
            if (!await db.Sections.AnyAsync(x => x.Id == secId, ct))
                return Problems.BadRequest("SECTION_NOT_FOUND", "sectionId does not match an existing section.");
            s.SectionId = secId;
        }
        s.FirstName = b.FirstName!.Trim();
        s.LastName = b.LastName!.Trim();
        if (b.Status is { } st) s.Status = st;
        s.UpdatedAt = DateTimeOffset.UtcNow;

        Audit(db, http, "STUDENT_UPDATE", "student", s.Id, before, StudentAudit(s));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Problems.Conflict("STUDENT_NO_TAKEN", "Another student already has this student number.");
        }
        return await GetStudent(id, db, http.User, ct);
    }

    static async Task<IResult> DeleteStudent(Guid id, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (s is null) return Problems.NotFound("STUDENT_NOT_FOUND", "Student not found.");
        var before = StudentAudit(s);
        s.DeletedAt = s.UpdatedAt = DateTimeOffset.UtcNow;
        Audit(db, http, "STUDENT_DELETE", "student", s.Id, before, StudentAudit(s));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> RestoreStudent(Guid id, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var s = await db.Students.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Problems.NotFound("STUDENT_NOT_FOUND", "Student not found.");
        if (s.DeletedAt is null) return Problems.Conflict("NOT_DELETED", "Student is not deleted.");

        // student_no is uniquely indexed over live rows only, so a live student may have taken it (decision #7).
        if (await db.Students.AnyAsync(x => x.Id != id && x.StudentNo == s.StudentNo && x.DeletedAt == null, ct))
            return Problems.Conflict("RESTORE_CONFLICT", "Cannot restore: another active student has this student_no (field: student_no).");

        var before = StudentAudit(s);
        s.DeletedAt = null;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        Audit(db, http, "STUDENT_RESTORE", "student", s.Id, before, StudentAudit(s));
        await db.SaveChangesAsync(ct);
        return await GetStudent(id, db, http.User, ct);
    }

    // ---- roster import ----

    static async Task<IResult> ImportRoster(HttpRequest req, TamsDbContext db, HttpContext http,
        bool? dryRun, bool? strict, CancellationToken ct)
    {
        if (!req.HasFormContentType)
            return Problems.BadRequest("CSV_FILE_REQUIRED", "Send multipart/form-data with the CSV in a 'file' field.");

        IFormFile? file;
        try { file = (await req.ReadFormAsync(ct)).Files["file"]; }
        catch (InvalidDataException) { return Problems.BadRequest("CSV_FILE_REQUIRED", "Malformed multipart body."); }
        if (file is null || file.Length == 0)
            return Problems.BadRequest("CSV_FILE_REQUIRED", "Attach a non-empty CSV in the 'file' field.");
        if (file.Length > RosterImport.MaxBytes)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                detail: $"CSV must be at most {RosterImport.MaxBytes / 1024 / 1024} MB.",
                extensions: new Dictionary<string, object?> { ["code"] = "CSV_TOO_LARGE" });

        string text;
        try
        {
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            return Problems.BadRequest("CSV_NOT_UTF8", "The file is not valid UTF-8. Export the sheet as 'CSV UTF-8'.");
        }

        List<CsvReader.Record> records;
        try { records = CsvReader.Parse(text); }
        catch (FormatException ex) { return Problems.BadRequest("CSV_MALFORMED", ex.Message); }

        if (records.Count == 0) return Problems.BadRequest("CSV_EMPTY", "The file has no header row.");
        var header = records[0].Fields;
        if (RosterImport.CheckHeader(header) is { } bad) return Problems.BadRequest(bad.Code, bad.Message);
        var dataRows = records.Skip(1).ToList();
        if (dataRows.Count > RosterImport.MaxRows)
            return Problems.BadRequest("CSV_TOO_MANY_ROWS", $"At most {RosterImport.MaxRows} rows per import.");

        var errors = new List<RowError>();
        var valid = new List<RosterRow>();
        foreach (var rec in dataRows)
        {
            var (row, err) = RosterImport.ValidateRow(rec, header);
            if (err is not null) errors.Add(err); else valid.Add(row!);
        }

        var isDry = dryRun == true;
        var isStrict = strict == true;
        var importId = Guid.NewGuid();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int created = 0, updated = 0, unchanged = 0, sectionsCreated = 0;
        var committed = false;
        try
        {
            if (!(isStrict && errors.Count > 0))
            {
                (created, updated, unchanged, sectionsCreated) = await RosterImport.ApplyAsync(db, valid, errors, ct);
                if (!(isStrict && errors.Count > 0) && !isDry)
                {
                    // Counts only; no student data goes into the log.
                    Audit(db, http, "STUDENT_ROSTER_IMPORT", "student_import", importId, after: new
                    {
                        rows = dataRows.Count, created, updated, unchanged,
                        skipped = errors.Count, sectionsCreated, strict = isStrict,
                    });
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    committed = true;
                }
            }
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Problems.Conflict("IMPORT_CONFLICT", "A concurrent change collided with the import. Nothing was saved; retry.");
        }
        finally
        {
            if (!committed) await tx.RollbackAsync(CancellationToken.None);
        }

        errors.Sort((a, b) => a.Line.CompareTo(b.Line));
        var report = new ImportReport(importId, isDry, isStrict, committed, dataRows.Count,
            committed || isDry ? created : 0, committed || isDry ? updated : 0, committed || isDry ? unchanged : 0,
            errors.Count, committed || isDry ? sectionsCreated : 0, errors);

        // Strict mode with any bad row rejects the whole file; the body still lists every row error.
        return isStrict && errors.Count > 0
            ? Results.Json(report, statusCode: StatusCodes.Status422UnprocessableEntity)
            : Results.Ok(report);
    }
}
