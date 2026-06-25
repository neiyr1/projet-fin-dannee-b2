using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using QuestPDF.Infrastructure;

// --- App setup ---

QuestPDF.Settings.License = LicenseType.Community;

var websitePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
var dataRoot = Path.GetFullPath(Path.Combine(websitePath, "..", "data"));
Directory.CreateDirectory(dataRoot);
var invoicesDir = Path.Combine(dataRoot, "invoices");
var outboxDir = Path.Combine(dataRoot, "outbox");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = websitePath
});

builder.Services.AddRazorPages(opts =>
{
    opts.Conventions.AuthorizeFolder("/");
    opts.Conventions.AllowAnonymousToPage("/Login");
    opts.Conventions.AllowAnonymousToPage("/Signup");
});

builder.Services.AddSingleton(sp => new InvoiceService(DbHelpers.GetDbPath(websitePath), invoicesDir, sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => new EmailService(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<EmailService>>(), outboxDir));
builder.Services.AddSingleton<ActiveDirectoryService>();
builder.Services.AddHostedService(sp => new ReminderService(DbHelpers.GetDbPath(websitePath), sp.GetRequiredService<EmailService>(), sp.GetRequiredService<ILogger<ReminderService>>()));

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ListenLocalhost(5001, listen => listen.UseHttps());
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opts =>
    {
        opts.LoginPath = "/Login";
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        opts.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = async context =>
            {
                var idClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!int.TryParse(idClaim, out var userId)) return;

                try
                {
                    using var conn = DbHelpers.OpenConnection(DbHelpers.GetDbPath(websitePath));
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COALESCE(AccountEnabled, 1) FROM Users WHERE Id = $id";
                    cmd.Parameters.AddWithValue("$id", userId);
                    var enabled = Convert.ToInt32(cmd.ExecuteScalar() ?? 0) == 1;
                    if (!enabled)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                }
                catch
                {
                    // Do not reject cookies because of a transient database read error.
                }
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

var defaultFilesOptions = new DefaultFilesOptions();
defaultFilesOptions.DefaultFileNames.Clear();
defaultFilesOptions.DefaultFileNames.Add("login.html");
app.UseDefaultFiles(defaultFilesOptions);
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// --- Database init ---

string GetDbPath() => DbHelpers.GetDbPath(websitePath);

static Claim[] BuildAuthClaims(int userId, string name, string role, string? email = null, string? adObjectGuid = null)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new(ClaimTypes.Name, name),
        new(ClaimTypes.Role, role ?? string.Empty)
    };
    if (!string.IsNullOrWhiteSpace(email)) claims.Add(new Claim(ClaimTypes.Email, email));
    if (!string.IsNullOrWhiteSpace(adObjectGuid)) claims.Add(new Claim("ad_object_guid", adObjectGuid));
    return claims.ToArray();
}

static IResult ActiveDirectoryErrorResult(ActiveDirectoryOperationException ex)
{
    return ex.Kind switch
    {
        ActiveDirectoryErrorKind.Duplicate => Results.Conflict(new { error = ex.Message }),
        ActiveDirectoryErrorKind.NotFound => Results.NotFound(new { error = ex.Message }),
        ActiveDirectoryErrorKind.Validation => Results.BadRequest(new { error = ex.Message }),
        ActiveDirectoryErrorKind.Configuration => Results.BadRequest(new { error = ex.Message }),
        _ => Results.Problem(title: "Active Directory", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable)
    };
}

static ActiveDirectoryUserLink ProvisionAdAccountOrThrow(ActiveDirectoryService adService, string name, string email, string password)
{
    if (!adService.IsEnabled) return ActiveDirectoryUserLink.Disabled;
    return adService.CreateUser(name, email, password);
}

static void SaveAdLink(Microsoft.Data.Sqlite.SqliteConnection conn, int userId, ActiveDirectoryUserLink adLink, bool clearLocalPassword)
{
    if (!adLink.Created) return;
    using var upd = conn.CreateCommand();
    upd.CommandText = @"UPDATE Users
                        SET ADSamAccountName = $sam,
                            ADUserPrincipalName = $upn,
                            ADObjectGuid = $guid,
                            PasswordHash = CASE WHEN $clear = 1 THEN NULL ELSE PasswordHash END
                        WHERE Id = $id";
    upd.Parameters.AddWithValue("$sam", (object?)adLink.SamAccountName ?? DBNull.Value);
    upd.Parameters.AddWithValue("$upn", (object?)adLink.UserPrincipalName ?? DBNull.Value);
    upd.Parameters.AddWithValue("$guid", (object?)adLink.ObjectGuid ?? DBNull.Value);
    upd.Parameters.AddWithValue("$clear", clearLocalPassword ? 1 : 0);
    upd.Parameters.AddWithValue("$id", userId);
    upd.ExecuteNonQuery();
}

static IResult? EnsureAdLinkedForBooking(HttpContext http, Microsoft.Data.Sqlite.SqliteConnection conn, ActiveDirectoryService adService)
{
    if (!adService.IsEnabled) return null;
    if (http.User.IsInRole("Admin")) return null;

    var idClaim = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
    using var cmd = conn.CreateCommand();
    if (int.TryParse(idClaim, out var userId))
    {
        cmd.CommandText = "SELECT ADSamAccountName, ADUserPrincipalName FROM Users WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
    }
    else
    {
        var name = http.User?.Identity?.Name ?? string.Empty;
        cmd.CommandText = "SELECT ADSamAccountName, ADUserPrincipalName FROM Users WHERE Name = $n OR Email = $n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", name);
    }

    using var rdr = cmd.ExecuteReader();
    if (!rdr.Read()) return Results.Unauthorized();
    var sam = rdr.IsDBNull(0) ? null : rdr.GetString(0);
    var upn = rdr.IsDBNull(1) ? null : rdr.GetString(1);
    if (!string.IsNullOrWhiteSpace(sam) || !string.IsNullOrWhiteSpace(upn)) return null;

    return Results.Json(new { error = "Compte Active Directory manquant. Deconnectez-vous puis reconnectez-vous pour creer le compte AD avant de reserver." }, statusCode: StatusCodes.Status409Conflict);
}

var dbPath = GetDbPath();
DbHelpers.InitializeDatabase(dbPath);
DbHelpers.SeedAdminUser(dbPath);
DbHelpers.SeedDefaultSpaces(dbPath);

// --- Auth endpoints ---

app.MapPost("/api/login", async (HttpContext http, ActiveDirectoryService adService, ILogger<Program> logger) =>
{
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(http.Request.Body);
    if (body == null || !body.TryGetValue("username", out var username) || !body.TryGetValue("password", out var password))
        return Results.BadRequest(new { error = "Missing credentials" });

    try
    {
        using var conn = DbHelpers.OpenConnection(GetDbPath());
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT Id, Name, Email, Role, PasswordHash, ADSamAccountName, ADUserPrincipalName, ADObjectGuid, COALESCE(AccountEnabled, 1)
                            FROM Users
                            WHERE Email = $u OR Name = $u OR ADSamAccountName = $u OR ADUserPrincipalName = $u
                            LIMIT 1";
        cmd.Parameters.AddWithValue("$u", username);
        using var rdr = cmd.ExecuteReader();
        if (rdr.Read())
        {
            var id = rdr.GetInt32(0);
            var name = rdr.IsDBNull(1) ? username : rdr.GetString(1);
            var email = rdr.IsDBNull(2) ? null : rdr.GetString(2);
            var role = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3);
            var ph = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4);
            var adSam = rdr.IsDBNull(5) ? null : rdr.GetString(5);
            var adUpn = rdr.IsDBNull(6) ? null : rdr.GetString(6);
            var adGuid = rdr.IsDBNull(7) ? null : rdr.GetString(7);
            var accountEnabled = rdr.IsDBNull(8) || rdr.GetInt32(8) == 1;
            if (!accountEnabled)
                return Results.Json(new { error = "Account disabled" }, statusCode: StatusCodes.Status403Forbidden);
            var isAdLinked = !string.IsNullOrWhiteSpace(adSam) || !string.IsNullOrWhiteSpace(adUpn);

            if (isAdLinked && adService.IsEnabled)
            {
                try
                {
                    if (adService.ValidateCredentials(username, password, adSam, adUpn))
                    {
                        var identity = new ClaimsIdentity(BuildAuthClaims(id, name, role ?? "", email, adGuid), CookieAuthenticationDefaults.AuthenticationScheme);
                        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                        return Results.Ok(new { user = name });
                    }
                }
                catch (ActiveDirectoryOperationException ex)
                {
                    logger.LogWarning(ex, "Active Directory login failed for {Username}", username);
                    return ActiveDirectoryErrorResult(ex);
                }

                return Results.Unauthorized();
            }

            if (!string.IsNullOrEmpty(ph) && DbHelpers.VerifyPassword(password, ph))
            {
                if (adService.IsEnabled && !isAdLinked && !string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(email))
                        return Results.BadRequest(new { error = "Email requis pour creer le compte Active Directory." });

                    try
                    {
                        var adLink = ProvisionAdAccountOrThrow(adService, name, email, password);
                        SaveAdLink(conn, id, adLink, clearLocalPassword: true);
                        adSam = adLink.SamAccountName;
                        adUpn = adLink.UserPrincipalName;
                        adGuid = adLink.ObjectGuid;
                    }
                    catch (ActiveDirectoryOperationException ex)
                    {
                        logger.LogWarning(ex, "Active Directory provisioning on login failed for {Username}", username);
                        return ActiveDirectoryErrorResult(ex);
                    }
                }

                var identity = new ClaimsIdentity(BuildAuthClaims(id, name, role ?? "", email, adGuid), CookieAuthenticationDefaults.AuthenticationScheme);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Ok(new { user = name });
            }
        }
    }
    catch { /* DB errors fall through to unauthorized */ }

    return Results.Unauthorized();
});

app.MapPost("/api/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    if (user?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var role = user.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    return Results.Ok(new { user = user.Identity?.Name, role });
});

app.MapPost("/api/signup", async (HttpContext http, EmailService emailSvc, ActiveDirectoryService adService, ILogger<Program> logger) =>
{
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(http.Request.Body);
    if (body == null) return Results.BadRequest(new { error = "Invalid payload" });
    body.TryGetValue("name", out var name);
    body.TryGetValue("email", out var email);
    body.TryGetValue("password", out var password);

    name = (name ?? string.Empty).Trim();
    email = (email ?? string.Empty).Trim();
    password = password ?? string.Empty;

    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return Results.BadRequest(new { error = "Name, email and password are required" });
    if (password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters" });
    if (!email.Contains('@'))
        return Results.BadRequest(new { error = "Invalid email" });

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using (var chk = conn.CreateCommand())
    {
        chk.CommandText = "SELECT COUNT(1) FROM Users WHERE Email = $e";
        chk.Parameters.AddWithValue("$e", email);
        if (Convert.ToInt32(chk.ExecuteScalar() ?? 0) > 0)
            return Results.Conflict(new { error = "Email already registered" });
    }

    ActiveDirectoryUserLink adLink;
    try
    {
        adLink = ProvisionAdAccountOrThrow(adService, name, email, password);
    }
    catch (ActiveDirectoryOperationException ex)
    {
        logger.LogWarning(ex, "Active Directory provisioning failed during signup for {Email}", email);
        return ActiveDirectoryErrorResult(ex);
    }

    var verifyToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    using var ins = conn.CreateCommand();
    ins.CommandText = @"INSERT INTO Users (Name, Email, Role, PasswordHash, EmailVerified, EmailVerifyToken, ADSamAccountName, ADUserPrincipalName, ADObjectGuid)
                        VALUES ($n,$e,$r,$ph,0,$tok,$sam,$upn,$guid);
                        SELECT last_insert_rowid();";
    ins.Parameters.AddWithValue("$n", name);
    ins.Parameters.AddWithValue("$e", email);
    ins.Parameters.AddWithValue("$r", "User");
    ins.Parameters.AddWithValue("$ph", adLink.Created ? DBNull.Value : DbHelpers.CreatePasswordHash(password));
    ins.Parameters.AddWithValue("$tok", verifyToken);
    ins.Parameters.AddWithValue("$sam", (object?)adLink.SamAccountName ?? DBNull.Value);
    ins.Parameters.AddWithValue("$upn", (object?)adLink.UserPrincipalName ?? DBNull.Value);
    ins.Parameters.AddWithValue("$guid", (object?)adLink.ObjectGuid ?? DBNull.Value);
    var id = Convert.ToInt32(ins.ExecuteScalar() ?? 0);

    var claims = BuildAuthClaims(id, name, "User", email, adLink.ObjectGuid);
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    var verifyUrl = $"{http.Request.Scheme}://{http.Request.Host}/api/verify-email?token={verifyToken}";
    try { await emailSvc.SendWelcomeAsync(email, name, verifyUrl); }
    catch (Exception ex) { logger.LogWarning(ex, "Welcome email failed for {Email}", email); }

    return Results.Created($"/api/users/{id}", new {
        id, name, email, role = "User",
        adSamAccountName = adLink.SamAccountName,
        adUserPrincipalName = adLink.UserPrincipalName,
        adObjectGuid = adLink.ObjectGuid
    });
});

app.MapGet("/api/verify-email", (string? token) =>
{
    if (string.IsNullOrWhiteSpace(token))
        return Results.Content("<html><body style='font-family:sans-serif;padding:40px'><h2>Invalid verification link</h2></body></html>", "text/html");

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE Users SET EmailVerified = 1, EmailVerifyToken = NULL WHERE EmailVerifyToken = $tok";
    cmd.Parameters.AddWithValue("$tok", token);
    var updated = cmd.ExecuteNonQuery();

    if (updated == 0)
        return Results.Content("<html><body style='font-family:sans-serif;padding:40px'><h2>This verification link is invalid or has already been used.</h2><p><a href='/'>Continue</a></p></body></html>", "text/html");

    return Results.Content("<html><body style='font-family:sans-serif;padding:40px'><h2 style='color:#1e40af'>Your email has been verified.</h2><p>Thank you!</p><p><a href='/'>Continue to the app</a></p></body></html>", "text/html");
}).AllowAnonymous();

// --- Admin: Users management ---

app.MapGet("/api/users", (HttpContext http) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    var list = new List<object>();
    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT u.Id, u.Name, u.Last_Name, u.Email, u.Role,
                               (SELECT COUNT(1) FROM Reservation r WHERE r.OwnerId = u.Id) AS BookingsCount,
                               COALESCE(u.EmailVerified, 0) AS EmailVerified,
                               COALESCE(u.AccountEnabled, 1) AS AccountEnabled,
                               u.ADSamAccountName, u.ADUserPrincipalName, u.ADObjectGuid
                        FROM Users u ORDER BY u.Id";
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
    {
        list.Add(new {
            id = rdr.GetInt32(0),
            name = rdr.IsDBNull(1) ? null : rdr.GetString(1),
            lastName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            email = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            role = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            bookings = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5),
            emailVerified = !rdr.IsDBNull(6) && rdr.GetInt32(6) == 1,
            accountEnabled = rdr.IsDBNull(7) || rdr.GetInt32(7) == 1,
            adSamAccountName = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            adUserPrincipalName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            adObjectGuid = rdr.IsDBNull(10) ? null : rdr.GetString(10)
        });
    }
    return Results.Ok(list);
}).RequireAuthorization();

app.MapPost("/api/users/{id:int}/verify-email", (HttpContext http, int id) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE Users SET EmailVerified = 1, EmailVerifyToken = NULL WHERE Id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    var rows = cmd.ExecuteNonQuery();
    return rows == 0 ? Results.NotFound() : Results.Ok(new { id, emailVerified = true });
}).RequireAuthorization();

app.MapPost("/api/users/{id:int}/resend-welcome", async (HttpContext http, int id, EmailService emailSvc, ILogger<Program> logger) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    using var conn = DbHelpers.OpenConnection(GetDbPath());

    string? email = null, name = null;
    using (var sel = conn.CreateCommand())
    {
        sel.CommandText = "SELECT Name, Email FROM Users WHERE Id = $id";
        sel.Parameters.AddWithValue("$id", id);
        using var r = sel.ExecuteReader();
        if (!r.Read()) return Results.NotFound();
        name = r.IsDBNull(0) ? null : r.GetString(0);
        email = r.IsDBNull(1) ? null : r.GetString(1);
    }
    if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest(new { error = "User has no email" });

    var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    using (var upd = conn.CreateCommand())
    {
        upd.CommandText = "UPDATE Users SET EmailVerifyToken = $tok WHERE Id = $id";
        upd.Parameters.AddWithValue("$tok", token);
        upd.Parameters.AddWithValue("$id", id);
        upd.ExecuteNonQuery();
    }

    var verifyUrl = $"{http.Request.Scheme}://{http.Request.Host}/api/verify-email?token={token}";
    try { await emailSvc.SendWelcomeAsync(email, name ?? "", verifyUrl); }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Resend welcome failed for {Email}", email);
        return Results.Problem("Email could not be sent");
    }
    return Results.Ok(new { id });
}).RequireAuthorization();

app.MapPost("/api/users", async (HttpContext http, ActiveDirectoryService adService, ILogger<Program> logger) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(http.Request.Body);
    if (body == null) return Results.BadRequest(new { error = "Invalid payload" });
    body.TryGetValue("name", out var name);
    body.TryGetValue("email", out var email);
    body.TryGetValue("role", out var role);
    body.TryGetValue("password", out var password);

    name = (name ?? string.Empty).Trim();
    email = (email ?? string.Empty).Trim();
    role = string.IsNullOrWhiteSpace(role) ? "User" : role.Trim();
    password = string.IsNullOrEmpty(password) ? "changeme123" : password;

    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        return Results.BadRequest(new { error = "Name and email required" });

    var allowedRoles = new[] { "User", "Admin", "Member", "Accueil", "Comptabilite" };
    if (!allowedRoles.Contains(role)) return Results.BadRequest(new { error = "Invalid role" });

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using (var chk = conn.CreateCommand())
    {
        chk.CommandText = "SELECT COUNT(1) FROM Users WHERE Email = $e";
        chk.Parameters.AddWithValue("$e", email);
        if (Convert.ToInt32(chk.ExecuteScalar() ?? 0) > 0)
            return Results.Conflict(new { error = "Email already in use" });
    }

    ActiveDirectoryUserLink adLink;
    try
    {
        adLink = adService.CreateUser(name, email, password);
    }
    catch (ActiveDirectoryOperationException ex)
    {
        logger.LogWarning(ex, "Active Directory provisioning failed for {Email}", email);
        return ActiveDirectoryErrorResult(ex);
    }

    using var ins = conn.CreateCommand();
    ins.CommandText = @"INSERT INTO Users (Name, Email, Role, PasswordHash, ADSamAccountName, ADUserPrincipalName, ADObjectGuid)
                        VALUES ($n,$e,$r,$ph,$sam,$upn,$guid);
                        SELECT last_insert_rowid();";
    ins.Parameters.AddWithValue("$n", name);
    ins.Parameters.AddWithValue("$e", email);
    ins.Parameters.AddWithValue("$r", role);
    ins.Parameters.AddWithValue("$ph", adLink.Created ? DBNull.Value : DbHelpers.CreatePasswordHash(password));
    ins.Parameters.AddWithValue("$sam", (object?)adLink.SamAccountName ?? DBNull.Value);
    ins.Parameters.AddWithValue("$upn", (object?)adLink.UserPrincipalName ?? DBNull.Value);
    ins.Parameters.AddWithValue("$guid", (object?)adLink.ObjectGuid ?? DBNull.Value);
    var id = Convert.ToInt32(ins.ExecuteScalar() ?? 0);
    return Results.Created($"/api/users/{id}", new {
        id, name, email, role,
        adSamAccountName = adLink.SamAccountName,
        adUserPrincipalName = adLink.UserPrincipalName,
        adObjectGuid = adLink.ObjectGuid
    });
}).RequireAuthorization();

app.MapPut("/api/users/{id:int}", async (HttpContext http, int id) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(http.Request.Body);
    if (body == null) return Results.BadRequest(new { error = "Invalid payload" });

    body.TryGetValue("name", out var name);
    body.TryGetValue("email", out var email);
    body.TryGetValue("role", out var role);

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    var sets = new List<string>();
    using var cmd = conn.CreateCommand();
    if (!string.IsNullOrWhiteSpace(name)) { sets.Add("Name = $n"); cmd.Parameters.AddWithValue("$n", name); }
    if (!string.IsNullOrWhiteSpace(email)) { sets.Add("Email = $e"); cmd.Parameters.AddWithValue("$e", email); }
    if (!string.IsNullOrWhiteSpace(role))
    {
        var allowed = new[] { "User", "Admin", "Member", "Accueil", "Comptabilite" };
        if (!allowed.Contains(role)) return Results.BadRequest(new { error = "Invalid role" });
        sets.Add("Role = $r"); cmd.Parameters.AddWithValue("$r", role);
    }
    if (sets.Count == 0) return Results.BadRequest(new { error = "Nothing to update" });

    cmd.CommandText = $"UPDATE Users SET {string.Join(", ", sets)} WHERE Id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    var rows = cmd.ExecuteNonQuery();
    return rows == 0 ? Results.NotFound() : Results.Ok(new { id });
}).RequireAuthorization();

app.MapPost("/api/users/{id:int}/reset-password", async (HttpContext http, int id, ActiveDirectoryService adService, ILogger<Program> logger) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(http.Request.Body);
    var password = body != null && body.TryGetValue("password", out var p) ? p : null;
    if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters" });

    using var conn = DbHelpers.OpenConnection(GetDbPath());

    string? adSam = null;
    string? adUpn = null;
    using (var sel = conn.CreateCommand())
    {
        sel.CommandText = "SELECT ADSamAccountName, ADUserPrincipalName FROM Users WHERE Id = $id";
        sel.Parameters.AddWithValue("$id", id);
        using var rdr = sel.ExecuteReader();
        if (!rdr.Read()) return Results.NotFound();
        adSam = rdr.IsDBNull(0) ? null : rdr.GetString(0);
        adUpn = rdr.IsDBNull(1) ? null : rdr.GetString(1);
    }

    var isAdLinked = !string.IsNullOrWhiteSpace(adSam) || !string.IsNullOrWhiteSpace(adUpn);
    if (isAdLinked)
    {
        try
        {
            adService.SetPassword(adSam ?? "", adUpn, password);
        }
        catch (ActiveDirectoryOperationException ex)
        {
            logger.LogWarning(ex, "Active Directory password reset failed for user {UserId}", id);
            return ActiveDirectoryErrorResult(ex);
        }

        using var clear = conn.CreateCommand();
        clear.CommandText = "UPDATE Users SET PasswordHash = NULL WHERE Id = $id";
        clear.Parameters.AddWithValue("$id", id);
        clear.ExecuteNonQuery();
        return Results.Ok(new { id, activeDirectory = true });
    }

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE Users SET PasswordHash = $ph WHERE Id = $id";
    cmd.Parameters.AddWithValue("$ph", DbHelpers.CreatePasswordHash(password));
    cmd.Parameters.AddWithValue("$id", id);
    var rows = cmd.ExecuteNonQuery();
    return rows == 0 ? Results.NotFound() : Results.Ok(new { id, activeDirectory = false });
}).RequireAuthorization();

app.MapPost("/api/users/{id:int}/status", async (HttpContext http, int id, ActiveDirectoryService adService, ILogger<Program> logger) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();

    System.Text.Json.JsonElement body;
    try { body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(http.Request.Body); }
    catch { return Results.BadRequest(new { error = "Invalid payload" }); }

    if (!body.TryGetProperty("enabled", out var enabledEl) || enabledEl.ValueKind is not System.Text.Json.JsonValueKind.True and not System.Text.Json.JsonValueKind.False)
        return Results.BadRequest(new { error = "enabled is required" });

    var enabled = enabledEl.GetBoolean();

    if (!enabled)
    {
        var currentIdClaim = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(currentIdClaim, out var currentId) && currentId == id)
            return Results.BadRequest(new { error = "Vous ne pouvez pas desactiver votre propre compte." });
    }

    using var conn = DbHelpers.OpenConnection(GetDbPath());

    string? name;
    string? email;
    string? role;
    string? adSam;
    string? adUpn;
    using (var sel = conn.CreateCommand())
    {
        sel.CommandText = "SELECT Name, Email, Role, ADSamAccountName, ADUserPrincipalName FROM Users WHERE Id = $id";
        sel.Parameters.AddWithValue("$id", id);
        using var rdr = sel.ExecuteReader();
        if (!rdr.Read()) return Results.NotFound();
        name = rdr.IsDBNull(0) ? null : rdr.GetString(0);
        email = rdr.IsDBNull(1) ? null : rdr.GetString(1);
        role = rdr.IsDBNull(2) ? null : rdr.GetString(2);
        adSam = rdr.IsDBNull(3) ? null : rdr.GetString(3);
        adUpn = rdr.IsDBNull(4) ? null : rdr.GetString(4);
    }

    if (!enabled && role == "Admin")
    {
        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(1) FROM Users WHERE Role = 'Admin' AND COALESCE(AccountEnabled, 1) = 1 AND Id <> $id";
        count.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt32(count.ExecuteScalar() ?? 0) <= 0)
            return Results.BadRequest(new { error = "Impossible de desactiver le dernier admin actif." });
    }

    var isAdLinked = !string.IsNullOrWhiteSpace(adSam) || !string.IsNullOrWhiteSpace(adUpn);
    if (isAdLinked)
    {
        if (!adService.IsEnabled)
            return Results.BadRequest(new { error = "Active Directory n'est pas active; impossible de modifier un compte AD lie." });

        try
        {
            adService.SetAccountEnabled(adSam ?? "", adUpn, enabled);
        }
        catch (ActiveDirectoryOperationException ex)
        {
            logger.LogWarning(ex, "Active Directory status update failed for user {UserId}", id);
            return ActiveDirectoryErrorResult(ex);
        }
    }

    using (var upd = conn.CreateCommand())
    {
        upd.CommandText = "UPDATE Users SET AccountEnabled = $enabled WHERE Id = $id";
        upd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        upd.Parameters.AddWithValue("$id", id);
        upd.ExecuteNonQuery();
    }

    DbHelpers.WriteAudit(GetDbPath(), http.User?.Identity?.Name, enabled ? "UserEnable" : "UserDisable", $"User#{id}", $"{name ?? email ?? id.ToString()}");
    return Results.Ok(new { id, accountEnabled = enabled, activeDirectory = isAdLinked });
}).RequireAuthorization();

app.MapDelete("/api/users/{id:int}", (HttpContext http, int id) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    using var conn = DbHelpers.OpenConnection(GetDbPath());

    using (var chk = conn.CreateCommand())
    {
        chk.CommandText = "SELECT Role, COALESCE(AccountEnabled, 1) FROM Users WHERE Id = $id";
        chk.Parameters.AddWithValue("$id", id);
        using var rdr = chk.ExecuteReader();
        if (!rdr.Read()) return Results.NotFound();
        var r = rdr.IsDBNull(0) ? null : rdr.GetString(0);
        var isEnabled = rdr.IsDBNull(1) || rdr.GetInt32(1) == 1;
        if (r == "Admin" && isEnabled)
        {
            using var cnt = conn.CreateCommand();
            cnt.CommandText = "SELECT COUNT(1) FROM Users WHERE Role = 'Admin' AND COALESCE(AccountEnabled, 1) = 1 AND Id <> $id";
            cnt.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt32(cnt.ExecuteScalar() ?? 0) <= 0)
                return Results.BadRequest(new { error = "Cannot delete the last admin" });
        }
    }

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM Users WHERE Id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    var rows = cmd.ExecuteNonQuery();
    return rows == 0 ? Results.NotFound() : Results.NoContent();
}).RequireAuthorization();

// --- Admin: Reservations overview ---

app.MapGet("/api/reservations/all", (HttpContext http, string? status, string? from, string? to) =>
{
    if (!(http.User.IsInRole("Admin") || http.User.IsInRole("Comptabilite"))) return Results.Forbid();
    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    var sql = @"SELECT r.ID, r.Date, r.StartHour, r.Hours, r.Status, r.Total_Amount,
                       u.Id, u.Name, u.Email,
                       s.Name,
                       f.Num_facture, f.Amount_TTC, f.Payment_Status
                FROM Reservation r
                LEFT JOIN Users u ON u.Id = r.OwnerId
                LEFT JOIN Spaces s ON s.ID = r.SpaceId
                LEFT JOIN Facture f ON f.ReservationId = r.ID
                WHERE 1=1";
    if (!string.IsNullOrWhiteSpace(status)) { sql += " AND r.Status = $st"; cmd.Parameters.AddWithValue("$st", status); }
    if (!string.IsNullOrWhiteSpace(from)) { sql += " AND r.Date >= $from"; cmd.Parameters.AddWithValue("$from", from); }
    if (!string.IsNullOrWhiteSpace(to)) { sql += " AND r.Date <= $to"; cmd.Parameters.AddWithValue("$to", to); }
    sql += " ORDER BY r.Date DESC, r.StartHour DESC";
    cmd.CommandText = sql;

    var list = new List<object>();
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
    {
        list.Add(new {
            id = rdr.GetInt32(0),
            date = rdr.IsDBNull(1) ? null : rdr.GetString(1),
            startHour = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2),
            hours = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3),
            status = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            totalHT = rdr.IsDBNull(5) ? 0.0 : rdr.GetDouble(5),
            ownerId = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
            ownerName = rdr.IsDBNull(7) ? null : rdr.GetString(7),
            ownerEmail = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            spaceName = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            invoiceNumber = rdr.IsDBNull(10) ? null : rdr.GetString(10),
            totalTTC = rdr.IsDBNull(11) ? 0.0 : rdr.GetDouble(11),
            paymentStatus = rdr.IsDBNull(12) ? null : rdr.GetString(12)
        });
    }
    return Results.Ok(list);
}).RequireAuthorization();

// --- Spaces endpoints ---

app.MapGet("/api/spaces", () =>
{
    var list = new List<object>();
    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ID, Name, Capacity, PricePerHour, Type FROM Spaces ORDER BY ID";
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
        list.Add(new {
            id = rdr.GetInt32(0),
            name = rdr.GetString(1),
            capacity = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2),
            pricePerHour = rdr.IsDBNull(3) ? 0.0 : rdr.GetDouble(3),
            type = rdr.IsDBNull(4) ? "Nomad" : rdr.GetString(4)
        });
    return Results.Ok(list);
}).RequireAuthorization();

app.MapPost("/api/spaces", async (HttpContext http) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();

    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, object>>(http.Request.Body);
    if (body == null || !body.TryGetValue("name", out var nameObj)) return Results.BadRequest(new { error = "Name required" });
    var name = nameObj?.ToString() ?? string.Empty;
    var capacity = body.TryGetValue("capacity", out var capObj) && int.TryParse(capObj?.ToString(), out var cap) ? cap : 0;
    var pricePerHour = body.TryGetValue("pricePerHour", out var pObj) && double.TryParse(pObj?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 5.0;
    var type = body.TryGetValue("type", out var tObj) ? (tObj?.ToString() ?? "Nomad") : "Nomad";
    var allowed = new[] { "Nomad", "Office", "Meeting", "Conference" };
    if (!allowed.Contains(type)) type = "Nomad";

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO Spaces (Name, Capacity, PricePerHour, Type) VALUES ($name, $cap, $price, $type); SELECT last_insert_rowid();";
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$cap", capacity);
    cmd.Parameters.AddWithValue("$price", pricePerHour);
    cmd.Parameters.AddWithValue("$type", type);
    var id = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    DbHelpers.WriteAudit(GetDbPath(), http.User?.Identity?.Name, "SpaceCreate", $"Space#{id}", $"name={name},type={type}");
    return Results.Created($"/api/spaces/{id}", new { id, name, capacity, pricePerHour, type });
}).RequireAuthorization();

app.MapDelete("/api/spaces/{id:int}", (HttpContext http, int id) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM Spaces WHERE ID = $id";
    cmd.Parameters.AddWithValue("$id", id);
    var rows = cmd.ExecuteNonQuery();
    if (rows == 0) return Results.NotFound();
    DbHelpers.WriteAudit(GetDbPath(), http.User?.Identity?.Name, "SpaceDelete", $"Space#{id}");
    return Results.NoContent();
}).RequireAuthorization();

// --- Resources (equipment per space) ---

app.MapGet("/api/spaces/{id:int}/resources", (HttpContext http, int id) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var list = new List<object>();
    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ID, Name_ressource, Type_ressources, Capacity, Price FROM Ressources WHERE SpaceId = $id ORDER BY ID";
    cmd.Parameters.AddWithValue("$id", id);
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
        list.Add(new {
            id = rdr.GetInt32(0),
            name = rdr.IsDBNull(1) ? null : rdr.GetString(1),
            type = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            quantity = rdr.IsDBNull(3) ? 1 : rdr.GetInt32(3),
            price = rdr.IsDBNull(4) ? 0.0 : rdr.GetDouble(4)
        });
    return Results.Ok(list);
}).RequireAuthorization();

app.MapPost("/api/spaces/{id:int}/resources", async (HttpContext http, int id) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, object>>(http.Request.Body);
    if (body == null) return Results.BadRequest(new { error = "Invalid payload" });
    var name = body.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
    var type = body.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "";
    var qty = body.TryGetValue("quantity", out var q) && int.TryParse(q?.ToString(), out var qi) ? qi : 1;
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "name required" });

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO Ressources (Name_ressource, Type_ressources, Capacity, Price, SpaceId) VALUES ($n,$t,$q,$p,$s); SELECT last_insert_rowid();";
    cmd.Parameters.AddWithValue("$n", name);
    cmd.Parameters.AddWithValue("$t", type);
    cmd.Parameters.AddWithValue("$q", qty);
    cmd.Parameters.AddWithValue("$p", 0.0);
    cmd.Parameters.AddWithValue("$s", id);
    var rid = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    DbHelpers.WriteAudit(GetDbPath(), http.User?.Identity?.Name, "ResourceCreate", $"Resource#{rid}", $"space={id},name={name}");
    return Results.Created($"/api/resources/{rid}", new { id = rid, name, type, quantity = qty });
}).RequireAuthorization();

app.MapDelete("/api/resources/{id:int}", (HttpContext http, int id) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM Ressources WHERE ID = $id";
    cmd.Parameters.AddWithValue("$id", id);
    var rows = cmd.ExecuteNonQuery();
    if (rows == 0) return Results.NotFound();
    DbHelpers.WriteAudit(GetDbPath(), http.User?.Identity?.Name, "ResourceDelete", $"Resource#{id}");
    return Results.NoContent();
}).RequireAuthorization();

// --- Reservation endpoints ---

app.MapPost("/api/reservations", async (HttpContext http, InvoiceService invoiceSvc, EmailService emailSvc, ActiveDirectoryService adService) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();

    System.Text.Json.JsonElement root;
    try { root = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(http.Request.Body); }
    catch { return Results.BadRequest(new { error = "Invalid payload" }); }

    if (root.ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null)
        return Results.BadRequest(new { error = "Invalid payload" });

    int spaceId = root.TryGetProperty("spaceId", out var spEl) && spEl.ValueKind == System.Text.Json.JsonValueKind.Number ? spEl.GetInt32() : 0;
    var dateStr = root.TryGetProperty("date", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String ? dEl.GetString() ?? "" : DateTime.UtcNow.ToString("yyyy-MM-dd");
    int startHour = root.TryGetProperty("startHour", out var shEl) && shEl.ValueKind == System.Text.Json.JsonValueKind.Number ? shEl.GetInt32() : 0;
    int hours = root.TryGetProperty("hours", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.Number ? hEl.GetInt32() : 1;

    var attendees = new List<string>();
    if (root.TryGetProperty("attendees", out var atEl) && atEl.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var x in atEl.EnumerateArray())
        {
            var s = x.GetString();
            if (!string.IsNullOrWhiteSpace(s) && s.Contains('@')) attendees.Add(s.Trim());
        }
    }
    var attendeesStr = string.Join(",", attendees);

    if (spaceId <= 0) return Results.BadRequest(new { error = "spaceId required" });
    if (hours <= 0 || hours > 12) return Results.BadRequest(new { error = "hours must be 1-12" });

    var date = DateTime.Parse(dateStr).Date;
    if (startHour < 0 || startHour > 23) return Results.BadRequest(new { error = "startHour must be 0-23" });
    var start = DateTime.SpecifyKind(date.AddHours(startHour), DateTimeKind.Utc);
    var end = start.AddHours(hours);

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    var adCheck = EnsureAdLinkedForBooking(http, conn, adService);
    if (adCheck != null) return adCheck;

    // Lookup space + price + capacity
    double pricePerHour = 0;
    string spaceName = string.Empty;
    int spaceCapacity = 0;
    using (var sc = conn.CreateCommand())
    {
        sc.CommandText = "SELECT Name, PricePerHour, Capacity FROM Spaces WHERE ID = $id LIMIT 1";
        sc.Parameters.AddWithValue("$id", spaceId);
        using var sr = sc.ExecuteReader();
        if (!sr.Read()) return Results.BadRequest(new { error = "Unknown space" });
        spaceName = sr.IsDBNull(0) ? string.Empty : sr.GetString(0);
        pricePerHour = sr.IsDBNull(1) ? 0.0 : sr.GetDouble(1);
        spaceCapacity = sr.IsDBNull(2) ? 0 : sr.GetInt32(2);
    }
    var totalHT = Math.Round(pricePerHour * hours, 2);

    var partySize = 1 + attendees.Count; // owner + attendees
    if (spaceCapacity > 0 && partySize > spaceCapacity)
        return Results.BadRequest(new { error = $"This space only fits {spaceCapacity} ({partySize} requested)" });

    // Resolve current user to DB OwnerId
    var currentName = http.User?.Identity?.Name ?? string.Empty;
    int ownerId;
    using (var ucmd = conn.CreateCommand())
    {
        ucmd.CommandText = "SELECT Id FROM Users WHERE Name = $n OR Email = $n LIMIT 1";
        ucmd.Parameters.AddWithValue("$n", currentName);
        ownerId = Convert.ToInt32(ucmd.ExecuteScalar() ?? 0);
    }

    if (ownerId == 0)
    {
        using var create = conn.CreateCommand();
        create.CommandText = "INSERT INTO Users (Name, Email, Role, PasswordHash) VALUES ($n,$e,$r,$ph); SELECT last_insert_rowid();";
        create.Parameters.AddWithValue("$n", currentName);
        var email = currentName.Contains("@") ? currentName : string.Empty;
        create.Parameters.AddWithValue("$e", string.IsNullOrEmpty(email) ? (object)DBNull.Value : email);
        create.Parameters.AddWithValue("$r", "User");
        create.Parameters.AddWithValue("$ph", DBNull.Value);
        ownerId = Convert.ToInt32(create.ExecuteScalar() ?? 0);
    }

    // Check for conflicts
    using (var chk = conn.CreateCommand())
    {
        chk.CommandText = "SELECT COUNT(1) FROM Reservation WHERE SpaceId = $sp AND Status = 'Booked' AND NOT (Ending_Date <= $s OR Starting_Date >= $e)";
        chk.Parameters.AddWithValue("$sp", spaceId);
        chk.Parameters.AddWithValue("$s", start.ToString("o"));
        chk.Parameters.AddWithValue("$e", end.ToString("o"));
        if (Convert.ToInt32(chk.ExecuteScalar() ?? 0) > 0)
            return Results.Conflict(new { error = "Time slot already booked for this space" });
    }

    int id;
    var token = QrService.NewToken();
    using (var ins = conn.CreateCommand())
    {
        ins.CommandText = "INSERT INTO Reservation (OwnerId, SpaceId, Starting_Date, Ending_Date, Date, StartHour, Hours, Status, Total_Amount, Attendees, AccessToken) VALUES ($o,$sp,$s,$e,$d,$sh,$h,$st,$t,$at,$tok); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$o", ownerId);
        ins.Parameters.AddWithValue("$sp", spaceId);
        ins.Parameters.AddWithValue("$s", start.ToString("o"));
        ins.Parameters.AddWithValue("$e", end.ToString("o"));
        ins.Parameters.AddWithValue("$d", date.ToString("yyyy-MM-dd"));
        ins.Parameters.AddWithValue("$sh", startHour);
        ins.Parameters.AddWithValue("$h", hours);
        ins.Parameters.AddWithValue("$st", "Booked");
        ins.Parameters.AddWithValue("$t", totalHT);
        ins.Parameters.AddWithValue("$at", string.IsNullOrEmpty(attendeesStr) ? (object)DBNull.Value : attendeesStr);
        ins.Parameters.AddWithValue("$tok", token);
        id = Convert.ToInt32(ins.ExecuteScalar() ?? 0);
    }
    DbHelpers.WriteAudit(GetDbPath(), http.User?.Identity?.Name, "BookingCreate", $"Reservation#{id}", $"space={spaceName},date={date:yyyy-MM-dd},start={startHour},hours={hours}");

    // Generate invoice + send email (failures don't roll back the booking — log only)
    string invoiceNumber = string.Empty;
    double totalTtc = 0;
    try
    {
        var invoice = invoiceSvc.BuildForReservation(id);
        if (invoice != null)
        {
            var pdfPath = invoiceSvc.GeneratePdf(invoice);
            invoiceSvc.SaveFactureRow(invoice, pdfPath);
            invoiceNumber = invoice.Number;
            totalTtc = invoice.AmountTTC;
            _ = Task.Run(() => emailSvc.SendBookingConfirmationAsync(invoice.OwnerEmail, invoice.OwnerName, invoice, pdfPath));
            foreach (var att in attendees)
                _ = Task.Run(() => emailSvc.SendInviteAsync(att, invoice.OwnerName, spaceName, start, hours));
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to generate invoice for reservation {Id}", id);
    }

    return Results.Created($"/api/reservations/{id}", new {
        id, ownerId, spaceId, spaceName,
        start = start.ToString("o"), end = end.ToString("o"),
        date = date.ToString("yyyy-MM-dd"), startHour, hours,
        pricePerHour, totalHT, totalTtc,
        invoiceNumber, attendees
    });
}).RequireAuthorization();

app.MapGet("/api/reservations/mine", (HttpContext http) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var name = http.User?.Identity?.Name ?? string.Empty;

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var uc = conn.CreateCommand();
    uc.CommandText = "SELECT Id FROM Users WHERE Name = $n OR Email = $n LIMIT 1";
    uc.Parameters.AddWithValue("$n", name);
    var ownerId = Convert.ToInt32(uc.ExecuteScalar() ?? 0);
    if (ownerId == 0) return Results.Ok(Array.Empty<object>());

    var list = new List<object>();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT r.ID, r.Date, r.StartHour, r.Hours, r.Status, r.Total_Amount,
                               s.Name, s.PricePerHour, s.Type,
                               f.Num_facture, f.Amount_TTC, f.Payment_Status,
                               r.Attendees, r.AccessToken
                        FROM Reservation r
                        LEFT JOIN Spaces s ON r.SpaceId = s.ID
                        LEFT JOIN Facture f ON f.ReservationId = r.ID
                        WHERE r.OwnerId = $oid
                        ORDER BY r.Date DESC, r.StartHour DESC";
    cmd.Parameters.AddWithValue("$oid", ownerId);
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
    {
        list.Add(new {
            id = rdr.GetInt32(0),
            date = rdr.IsDBNull(1) ? null : rdr.GetString(1),
            startHour = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2),
            hours = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3),
            status = rdr.IsDBNull(4) ? null : rdr.GetString(4),
            totalHT = rdr.IsDBNull(5) ? 0.0 : rdr.GetDouble(5),
            spaceName = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            pricePerHour = rdr.IsDBNull(7) ? 0.0 : rdr.GetDouble(7),
            spaceType = rdr.IsDBNull(8) ? null : rdr.GetString(8),
            invoiceNumber = rdr.IsDBNull(9) ? null : rdr.GetString(9),
            totalTTC = rdr.IsDBNull(10) ? 0.0 : rdr.GetDouble(10),
            paymentStatus = rdr.IsDBNull(11) ? null : rdr.GetString(11),
            attendees = rdr.IsDBNull(12) ? null : rdr.GetString(12),
            accessToken = rdr.IsDBNull(13) ? null : rdr.GetString(13)
        });
    }
    return Results.Ok(list);
}).RequireAuthorization();

app.MapDelete("/api/reservations/{id:int}", (HttpContext http, int id, EmailService emailSvc) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var name = http.User?.Identity?.Name ?? string.Empty;

    using var conn = DbHelpers.OpenConnection(GetDbPath());

    int ownerId;
    using (var uc = conn.CreateCommand())
    {
        uc.CommandText = "SELECT Id FROM Users WHERE Name = $n OR Email = $n LIMIT 1";
        uc.Parameters.AddWithValue("$n", name);
        ownerId = Convert.ToInt32(uc.ExecuteScalar() ?? 0);
    }

    var isAdmin = http.User?.IsInRole("Admin") == true;

    int ownerOfRes; string status; string? ownerEmail = null, ownerName = null, spaceName = null;
    string? dateStr = null; int startHour = 0, hours = 0;
    using (var chk = conn.CreateCommand())
    {
        chk.CommandText = @"SELECT r.OwnerId, r.Status, u.Email, u.Name, s.Name, r.Date, r.StartHour, r.Hours
                            FROM Reservation r
                            LEFT JOIN Users u ON u.Id = r.OwnerId
                            LEFT JOIN Spaces s ON s.ID = r.SpaceId
                            WHERE r.ID = $id LIMIT 1";
        chk.Parameters.AddWithValue("$id", id);
        using var rdr = chk.ExecuteReader();
        if (!rdr.Read()) return Results.NotFound();
        ownerOfRes = rdr.GetInt32(0);
        status = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1);
        ownerEmail = rdr.IsDBNull(2) ? null : rdr.GetString(2);
        ownerName = rdr.IsDBNull(3) ? null : rdr.GetString(3);
        spaceName = rdr.IsDBNull(4) ? null : rdr.GetString(4);
        dateStr = rdr.IsDBNull(5) ? null : rdr.GetString(5);
        startHour = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6);
        hours = rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7);
    }
    if (!isAdmin && ownerOfRes != ownerId) return Results.Forbid();
    if (status == "Cancelled") return Results.Ok(new { id, status = "Cancelled" });

    using (var upd = conn.CreateCommand())
    {
        upd.CommandText = "UPDATE Reservation SET Status = 'Cancelled' WHERE ID = $id";
        upd.Parameters.AddWithValue("$id", id);
        upd.ExecuteNonQuery();
    }

    string? invNum = null;
    using (var fact = conn.CreateCommand())
    {
        fact.CommandText = "UPDATE Facture SET Payment_Status = 'Cancelled' WHERE ReservationId = $id RETURNING Num_facture";
        fact.Parameters.AddWithValue("$id", id);
        invNum = fact.ExecuteScalar() as string;
    }

    DbHelpers.WriteAudit(GetDbPath(), name, "BookingCancel", $"Reservation#{id}");

    if (!string.IsNullOrWhiteSpace(ownerEmail) && !string.IsNullOrWhiteSpace(dateStr))
    {
        var slot = DateTime.Parse(dateStr).AddHours(startHour);
        _ = Task.Run(() => emailSvc.SendBookingCancellationAsync(ownerEmail, ownerName ?? "", spaceName ?? "", slot, hours, invNum));
    }

    return Results.Ok(new { id, status = "Cancelled" });
}).RequireAuthorization();

app.MapPut("/api/reservations/{id:int}", async (HttpContext http, int id, EmailService emailSvc) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var name = http.User?.Identity?.Name ?? string.Empty;
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, object>>(http.Request.Body);
    if (body == null) return Results.BadRequest(new { error = "Invalid payload" });

    using var conn = DbHelpers.OpenConnection(GetDbPath());

    int ownerOfRes, currentSpaceId, currentStartHour, currentHours;
    string currentDate, currentStatus;
    string? ownerEmail, ownerName, spaceName;
    double pricePerHour;
    using (var chk = conn.CreateCommand())
    {
        chk.CommandText = @"SELECT r.OwnerId, r.SpaceId, r.Date, r.StartHour, r.Hours, r.Status,
                                  u.Email, u.Name, s.Name, s.PricePerHour
                            FROM Reservation r
                            LEFT JOIN Users u ON u.Id = r.OwnerId
                            LEFT JOIN Spaces s ON s.ID = r.SpaceId
                            WHERE r.ID = $id LIMIT 1";
        chk.Parameters.AddWithValue("$id", id);
        using var rdr = chk.ExecuteReader();
        if (!rdr.Read()) return Results.NotFound();
        ownerOfRes = rdr.GetInt32(0);
        currentSpaceId = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
        currentDate = rdr.IsDBNull(2) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : rdr.GetString(2);
        currentStartHour = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
        currentHours = rdr.IsDBNull(4) ? 1 : rdr.GetInt32(4);
        currentStatus = rdr.IsDBNull(5) ? "" : rdr.GetString(5);
        ownerEmail = rdr.IsDBNull(6) ? null : rdr.GetString(6);
        ownerName = rdr.IsDBNull(7) ? null : rdr.GetString(7);
        spaceName = rdr.IsDBNull(8) ? null : rdr.GetString(8);
        pricePerHour = rdr.IsDBNull(9) ? 0.0 : rdr.GetDouble(9);
    }

    int ownerId;
    using (var uc = conn.CreateCommand())
    {
        uc.CommandText = "SELECT Id FROM Users WHERE Name = $n OR Email = $n LIMIT 1";
        uc.Parameters.AddWithValue("$n", name);
        ownerId = Convert.ToInt32(uc.ExecuteScalar() ?? 0);
    }
    var isAdmin = http.User?.IsInRole("Admin") == true;
    if (!isAdmin && ownerOfRes != ownerId) return Results.Forbid();
    if (currentStatus == "Cancelled") return Results.BadRequest(new { error = "Cannot modify a cancelled reservation" });

    var newDate = body.TryGetValue("date", out var dObj) && dObj?.ToString() is string ds && !string.IsNullOrWhiteSpace(ds) ? ds : currentDate;
    var newStart = body.TryGetValue("startHour", out var shObj) && int.TryParse(shObj?.ToString(), out var sh) ? sh : currentStartHour;
    var newHours = body.TryGetValue("hours", out var hObj) && int.TryParse(hObj?.ToString(), out var hh) ? hh : currentHours;
    if (newStart < 0 || newStart > 23) return Results.BadRequest(new { error = "startHour must be 0-23" });
    if (newHours < 1 || newHours > 12) return Results.BadRequest(new { error = "hours must be 1-12" });

    var dt = DateTime.SpecifyKind(DateTime.Parse(newDate).Date.AddHours(newStart), DateTimeKind.Utc);
    var endDt = dt.AddHours(newHours);

    // conflict check (exclude self)
    using (var ch = conn.CreateCommand())
    {
        ch.CommandText = "SELECT COUNT(1) FROM Reservation WHERE SpaceId = $sp AND ID <> $id AND Status = 'Booked' AND NOT (Ending_Date <= $s OR Starting_Date >= $e)";
        ch.Parameters.AddWithValue("$sp", currentSpaceId);
        ch.Parameters.AddWithValue("$id", id);
        ch.Parameters.AddWithValue("$s", dt.ToString("o"));
        ch.Parameters.AddWithValue("$e", endDt.ToString("o"));
        if (Convert.ToInt32(ch.ExecuteScalar() ?? 0) > 0) return Results.Conflict(new { error = "Time slot already booked" });
    }

    var totalHT = Math.Round(pricePerHour * newHours, 2);
    using (var upd = conn.CreateCommand())
    {
        upd.CommandText = @"UPDATE Reservation SET Date=$d, StartHour=$sh, Hours=$h, Starting_Date=$sd, Ending_Date=$ed, Total_Amount=$tot
                            WHERE ID = $id";
        upd.Parameters.AddWithValue("$d", newDate);
        upd.Parameters.AddWithValue("$sh", newStart);
        upd.Parameters.AddWithValue("$h", newHours);
        upd.Parameters.AddWithValue("$sd", dt.ToString("o"));
        upd.Parameters.AddWithValue("$ed", endDt.ToString("o"));
        upd.Parameters.AddWithValue("$tot", totalHT);
        upd.Parameters.AddWithValue("$id", id);
        upd.ExecuteNonQuery();
    }

    DbHelpers.WriteAudit(GetDbPath(), name, "BookingModify", $"Reservation#{id}", $"date={newDate},start={newStart},hours={newHours}");

    if (!string.IsNullOrWhiteSpace(ownerEmail))
        _ = Task.Run(() => emailSvc.SendBookingModifiedAsync(ownerEmail, ownerName ?? "", spaceName ?? "", dt, newHours));

    return Results.Ok(new { id, date = newDate, startHour = newStart, hours = newHours, totalHT });
}).RequireAuthorization();

// --- Cart checkout (multi-item booking) ---

app.MapPost("/api/cart/checkout", async (HttpContext http, InvoiceService invoiceSvc, EmailService emailSvc, ActiveDirectoryService adService) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    System.Text.Json.JsonElement root;
    try { root = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(http.Request.Body); }
    catch { return Results.BadRequest(new { error = "Invalid payload" }); }
    if (!root.TryGetProperty("items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array || items.GetArrayLength() == 0)
        return Results.BadRequest(new { error = "Cart is empty" });

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    var adCheck = EnsureAdLinkedForBooking(http, conn, adService);
    if (adCheck != null) return adCheck;

    var currentName = http.User?.Identity?.Name ?? string.Empty;
    int ownerId;
    using (var ucmd = conn.CreateCommand())
    {
        ucmd.CommandText = "SELECT Id FROM Users WHERE Name = $n OR Email = $n LIMIT 1";
        ucmd.Parameters.AddWithValue("$n", currentName);
        ownerId = Convert.ToInt32(ucmd.ExecuteScalar() ?? 0);
    }
    if (ownerId == 0)
    {
        using var create = conn.CreateCommand();
        create.CommandText = "INSERT INTO Users (Name, Email, Role, PasswordHash) VALUES ($n,$e,$r,$ph); SELECT last_insert_rowid();";
        create.Parameters.AddWithValue("$n", currentName);
        var em = currentName.Contains('@') ? currentName : string.Empty;
        create.Parameters.AddWithValue("$e", string.IsNullOrEmpty(em) ? (object)DBNull.Value : em);
        create.Parameters.AddWithValue("$r", "User");
        create.Parameters.AddWithValue("$ph", DBNull.Value);
        ownerId = Convert.ToInt32(create.ExecuteScalar() ?? 0);
    }

    var created = new List<int>();
    var spaceNames = new Dictionary<int, string>();
    var spacePrices = new Dictionary<int, double>();

    foreach (var item in items.EnumerateArray())
    {
        var spId = item.TryGetProperty("spaceId", out var s1) ? s1.GetInt32() : 0;
        var dateStr = item.TryGetProperty("date", out var s2) ? s2.GetString() ?? "" : "";
        var sh = item.TryGetProperty("startHour", out var s3) ? s3.GetInt32() : 0;
        var hr = item.TryGetProperty("hours", out var s4) ? s4.GetInt32() : 1;
        if (spId == 0 || string.IsNullOrEmpty(dateStr)) continue;
        if (sh < 0 || sh > 23 || hr < 1 || hr > 12) continue;

        var date = DateTime.Parse(dateStr).Date;
        var start = DateTime.SpecifyKind(date.AddHours(sh), DateTimeKind.Utc);
        var end = start.AddHours(hr);

        if (!spacePrices.ContainsKey(spId))
        {
            using var sc = conn.CreateCommand();
            sc.CommandText = "SELECT Name, PricePerHour FROM Spaces WHERE ID = $id LIMIT 1";
            sc.Parameters.AddWithValue("$id", spId);
            using var sr = sc.ExecuteReader();
            if (!sr.Read()) continue;
            spaceNames[spId] = sr.IsDBNull(0) ? "" : sr.GetString(0);
            spacePrices[spId] = sr.IsDBNull(1) ? 0.0 : sr.GetDouble(1);
        }

        using (var ch = conn.CreateCommand())
        {
            ch.CommandText = "SELECT COUNT(1) FROM Reservation WHERE SpaceId = $sp AND Status = 'Booked' AND NOT (Ending_Date <= $s OR Starting_Date >= $e)";
            ch.Parameters.AddWithValue("$sp", spId);
            ch.Parameters.AddWithValue("$s", start.ToString("o"));
            ch.Parameters.AddWithValue("$e", end.ToString("o"));
            if (Convert.ToInt32(ch.ExecuteScalar() ?? 0) > 0)
                return Results.Conflict(new { error = $"Conflict for {spaceNames[spId]} {dateStr} {sh}:00" });
        }

        var lineHT = Math.Round(spacePrices[spId] * hr, 2);
        var token = QrService.NewToken();
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO Reservation (OwnerId, SpaceId, Starting_Date, Ending_Date, Date, StartHour, Hours, Status, Total_Amount, AccessToken) VALUES ($o,$sp,$s,$e,$d,$sh,$h,'Booked',$t,$tok); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$o", ownerId);
        ins.Parameters.AddWithValue("$sp", spId);
        ins.Parameters.AddWithValue("$s", start.ToString("o"));
        ins.Parameters.AddWithValue("$e", end.ToString("o"));
        ins.Parameters.AddWithValue("$d", dateStr);
        ins.Parameters.AddWithValue("$sh", sh);
        ins.Parameters.AddWithValue("$h", hr);
        ins.Parameters.AddWithValue("$t", lineHT);
        ins.Parameters.AddWithValue("$tok", token);
        var rid = Convert.ToInt32(ins.ExecuteScalar() ?? 0);
        created.Add(rid);
    }

    if (created.Count == 0) return Results.BadRequest(new { error = "No valid items" });
    DbHelpers.WriteAudit(GetDbPath(), currentName, "CartCheckout", $"Reservations={string.Join(",", created)}");

    string invoiceNumber = string.Empty;
    double totalTtc = 0;
    try
    {
        var invoice = invoiceSvc.BuildForReservations(created);
        if (invoice != null)
        {
            var pdfPath = invoiceSvc.GeneratePdf(invoice);
            // one Facture row per reservation, all sharing the same Num_facture/PdfPath but with per-line amounts
            var tva = invoice.TvaRate;
            foreach (var line in invoice.Lines)
            {
                var lineTVA = Math.Round(line.LineHT * tva, 2);
                invoiceSvc.SaveFactureRow(new InvoiceData {
                    ReservationId = line.ReservationId,
                    Number = invoice.Number, Date = invoice.Date,
                    AmountHT = line.LineHT, AmountTVA = lineTVA, AmountTTC = Math.Round(line.LineHT + lineTVA, 2)
                }, pdfPath);
            }
            invoiceNumber = invoice.Number;
            totalTtc = invoice.AmountTTC;
            _ = Task.Run(() => emailSvc.SendBookingConfirmationAsync(invoice.OwnerEmail, invoice.OwnerName, invoice, pdfPath));
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to generate invoice for cart checkout");
    }

    return Results.Ok(new { reservationIds = created, invoiceNumber, totalTtc });
}).RequireAuthorization();

// --- QR code + Access control ---

app.MapGet("/api/reservations/{id:int}/qr", (HttpContext http, int id) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT r.OwnerId, r.AccessToken FROM Reservation r WHERE r.ID = $id";
    cmd.Parameters.AddWithValue("$id", id);
    using var rdr = cmd.ExecuteReader();
    if (!rdr.Read()) return Results.NotFound();
    var owner = rdr.GetInt32(0);
    var token = rdr.IsDBNull(1) ? null : rdr.GetString(1);
    rdr.Close();
    if (string.IsNullOrEmpty(token)) return Results.NotFound();
    var name = http.User?.Identity?.Name ?? "";
    var isAdmin = http.User?.IsInRole("Admin") == true;
    if (!isAdmin)
    {
        using var uc = conn.CreateCommand();
        uc.CommandText = "SELECT Id FROM Users WHERE Name = $n OR Email = $n LIMIT 1";
        uc.Parameters.AddWithValue("$n", name);
        var uid = Convert.ToInt32(uc.ExecuteScalar() ?? 0);
        if (uid != owner) return Results.Forbid();
    }
    var png = QrService.GeneratePng(token, 10);
    return Results.File(png, "image/png");
}).RequireAuthorization();

app.MapPost("/api/access/verify", async (HttpContext http) =>
{
    if (!(http.User.IsInRole("Admin") || http.User.IsInRole("Accueil"))) return Results.Forbid();
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(http.Request.Body);
    var token = body != null && body.TryGetValue("token", out var t) ? t : null;
    if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest(new { error = "token required" });

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT r.ID, r.Date, r.StartHour, r.Hours, r.Status, u.Name, s.Name
                        FROM Reservation r
                        LEFT JOIN Users u ON u.Id = r.OwnerId
                        LEFT JOIN Spaces s ON s.ID = r.SpaceId
                        WHERE r.AccessToken = $tok LIMIT 1";
    cmd.Parameters.AddWithValue("$tok", token);
    using var rdr = cmd.ExecuteReader();
    if (!rdr.Read())
    {
        DbHelpers.WriteAudit(GetDbPath(), http.User?.Identity?.Name, "AccessDenied", null, "unknown token");
        return Results.Ok(new { granted = false, reason = "Unknown QR" });
    }
    var rid = rdr.GetInt32(0);
    var dateStr = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
    var sh = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
    var hr = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
    var st = rdr.IsDBNull(4) ? "" : rdr.GetString(4);
    var owner = rdr.IsDBNull(5) ? "" : rdr.GetString(5);
    var space = rdr.IsDBNull(6) ? "" : rdr.GetString(6);
    rdr.Close();

    var start = DateTime.Parse(dateStr).AddHours(sh);
    var end = start.AddHours(hr);
    var now = DateTime.UtcNow;
    var grantWindow = TimeSpan.FromMinutes(15);
    var granted = st == "Booked" && now >= start.Subtract(grantWindow) && now <= end;
    DbHelpers.WriteAudit(GetDbPath(), http.User?.Identity?.Name, granted ? "AccessGranted" : "AccessDenied", $"Reservation#{rid}");
    return Results.Ok(new { granted, owner, space, start, end, status = st });
}).RequireAuthorization();

// --- Audit log + Backup ---

app.MapGet("/api/admin/audit", (HttpContext http, int? limit) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    var lim = limit.GetValueOrDefault(200);
    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT Timestamp, UserName, Action, Target, Details FROM AuditLog ORDER BY Id DESC LIMIT {Math.Clamp(lim, 1, 1000)}";
    var list = new List<object>();
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
    {
        list.Add(new {
            timestamp = rdr.IsDBNull(0) ? null : rdr.GetString(0),
            user = rdr.IsDBNull(1) ? null : rdr.GetString(1),
            action = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            target = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            details = rdr.IsDBNull(4) ? null : rdr.GetString(4)
        });
    }
    return Results.Ok(list);
}).RequireAuthorization();

app.MapGet("/api/admin/backup", (HttpContext http) =>
{
    if (!http.User.IsInRole("Admin")) return Results.Forbid();
    var dbPath = GetDbPath();
    if (!File.Exists(dbPath)) return Results.NotFound();
    DbHelpers.WriteAudit(dbPath, http.User?.Identity?.Name, "BackupDownload");

    var tmp = Path.Combine(Path.GetTempPath(), $"app-backup-{Guid.NewGuid():N}.db");
    try
    {
        using (var src = DbHelpers.OpenConnection(dbPath))
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp }.ConnectionString))
        {
            dst.Open();
            src.BackupDatabase(dst);
            dst.Close();
        }
        SqliteConnection.ClearAllPools();
        byte[] bytes;
        using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            int read = 0; while (read < bytes.Length) read += fs.Read(bytes, read, bytes.Length - read);
        }
        return Results.File(bytes, "application/octet-stream", $"app-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
    }
    finally
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }
}).RequireAuthorization();

// --- Enhanced dashboard stats ---

app.MapGet("/api/admin/dashboard", (HttpContext http) =>
{
    if (!(http.User.IsInRole("Admin") || http.User.IsInRole("Comptabilite"))) return Results.Forbid();
    using var conn = DbHelpers.OpenConnection(GetDbPath());

    int Count(string sql)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt32(c.ExecuteScalar() ?? 0);
    }
    double Sum(string sql)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        var x = c.ExecuteScalar();
        if (x == null || x is DBNull) return 0.0;
        return Convert.ToDouble(x);
    }

    var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
    var top = new List<object>();
    using (var c = conn.CreateCommand())
    {
        c.CommandText = @"SELECT s.Name, COUNT(r.ID), COALESCE(SUM(r.Hours), 0)
                          FROM Spaces s LEFT JOIN Reservation r ON r.SpaceId = s.ID AND r.Status = 'Booked'
                          GROUP BY s.ID ORDER BY 2 DESC LIMIT 5";
        using var rdr = c.ExecuteReader();
        while (rdr.Read())
            top.Add(new { name = rdr.GetString(0), bookings = rdr.GetInt32(1), hours = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2) });
    }

    var totalSpaces = Count("SELECT COUNT(1) FROM Spaces");
    var totalHoursThisWeek = Sum("SELECT COALESCE(SUM(Hours),0) FROM Reservation WHERE Status='Booked' AND Date >= date('now','-7 days')");
    var weeklyCapacity = totalSpaces * 7 * 12.0; // 12 bookable hours/day rough estimate

    return Results.Ok(new {
        totals = new {
            spaces = totalSpaces,
            users = Count("SELECT COUNT(1) FROM Users"),
            bookingsTotal = Count("SELECT COUNT(1) FROM Reservation"),
            bookingsActive = Count("SELECT COUNT(1) FROM Reservation WHERE Status='Booked'"),
            bookingsCancelled = Count("SELECT COUNT(1) FROM Reservation WHERE Status='Cancelled'"),
            bookingsToday = Count($"SELECT COUNT(1) FROM Reservation WHERE Status='Booked' AND Date='{todayStr}'")
        },
        revenue = new {
            ht = Sum("SELECT COALESCE(SUM(Amount_HT),0) FROM Facture WHERE Payment_Status != 'Cancelled'"),
            ttc = Sum("SELECT COALESCE(SUM(Amount_TTC),0) FROM Facture WHERE Payment_Status != 'Cancelled'"),
            invoicesCount = Count("SELECT COUNT(1) FROM Facture WHERE Payment_Status != 'Cancelled'")
        },
        occupancyWeek = weeklyCapacity > 0 ? Math.Round(totalHoursThisWeek / weeklyCapacity * 100.0, 1) : 0,
        topSpaces = top
    });
}).RequireAuthorization();

app.MapGet("/api/invoices/{reservationId:int}", (HttpContext http, int reservationId) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var name = http.User?.Identity?.Name ?? string.Empty;
    var isAdmin = http.User?.IsInRole("Admin") == true;

    using var conn = DbHelpers.OpenConnection(GetDbPath());

    int ownerId;
    using (var uc = conn.CreateCommand())
    {
        uc.CommandText = "SELECT Id FROM Users WHERE Name = $n OR Email = $n LIMIT 1";
        uc.Parameters.AddWithValue("$n", name);
        ownerId = Convert.ToInt32(uc.ExecuteScalar() ?? 0);
    }

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT r.OwnerId, f.PdfPath, f.Num_facture FROM Reservation r LEFT JOIN Facture f ON f.ReservationId = r.ID WHERE r.ID = $id LIMIT 1";
    cmd.Parameters.AddWithValue("$id", reservationId);
    using var rdr = cmd.ExecuteReader();
    if (!rdr.Read()) return Results.NotFound();
    var resOwner = rdr.GetInt32(0);
    var pdfPath = rdr.IsDBNull(1) ? null : rdr.GetString(1);
    var num = rdr.IsDBNull(2) ? "invoice" : rdr.GetString(2);

    if (!isAdmin && resOwner != ownerId) return Results.Forbid();
    if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath))
        return Results.NotFound(new { error = "Invoice not found" });

    return Results.File(pdfPath, "application/pdf", $"{num}.pdf");
}).RequireAuthorization();

app.MapGet("/api/reservations/space", (HttpContext http, int spaceId, string? date) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var d = string.IsNullOrEmpty(date) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : date;
    var list = new List<object>();

    using var conn = DbHelpers.OpenConnection(GetDbPath());
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT r.ID, r.Starting_Date, r.Ending_Date, r.Date, r.StartHour, r.Hours, r.Status, r.Total_Amount, r.OwnerId, u.Name as OwnerName
                        FROM Reservation r LEFT JOIN Users u ON r.OwnerId = u.Id
                        WHERE r.SpaceId = $sp AND r.Date = $d ORDER BY r.StartHour";
    cmd.Parameters.AddWithValue("$sp", spaceId);
    cmd.Parameters.AddWithValue("$d", d);
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
    {
        list.Add(new
        {
            id = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0),
            start = rdr.IsDBNull(1) ? null : rdr.GetString(1),
            end = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            date = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            startHour = rdr.IsDBNull(4) ? (int?)null : rdr.GetInt32(4),
            hours = rdr.IsDBNull(5) ? (int?)null : rdr.GetInt32(5),
            status = rdr.IsDBNull(6) ? null : rdr.GetString(6),
            total = rdr.IsDBNull(7) ? 0 : rdr.GetDouble(7),
            ownerId = rdr.IsDBNull(8) ? 0 : rdr.GetInt32(8),
            ownerName = rdr.IsDBNull(9) ? null : rdr.GetString(9)
        });
    }
    return Results.Ok(list);
}).RequireAuthorization();

// --- Dashboard stats endpoint ---

app.MapGet("/api/stats", (HttpContext http) =>
{
    if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    using var conn = DbHelpers.OpenConnection(GetDbPath());

    int count(string table) { using var c = conn.CreateCommand(); c.CommandText = $"SELECT COUNT(1) FROM {table}"; return Convert.ToInt32(c.ExecuteScalar() ?? 0); }

    return Results.Ok(new { spaces = count("Spaces"), reservations = count("Reservation"), users = count("Users") });
}).RequireAuthorization();

// --- Utility endpoints ---

app.MapGet("/health", () => Results.Ok("OK"));

// Legacy HTML routes
app.MapGet("/spaces-map.html", () => Results.Redirect("/SpacesMap", false));
app.MapGet("/spaces.html", () => Results.Redirect("/Spaces", false));
app.MapGet("/login.html", () => Results.Redirect("/Login", false));
app.MapGet("/index.html", () => Results.Redirect("/", false));

app.Run();

