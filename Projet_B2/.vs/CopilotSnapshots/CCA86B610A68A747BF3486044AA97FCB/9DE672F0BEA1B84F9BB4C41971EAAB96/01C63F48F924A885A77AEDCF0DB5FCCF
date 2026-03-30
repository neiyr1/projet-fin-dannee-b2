using System.IO;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Security.Cryptography;

 

// Use `wwwroot` inside the project as the web root (static files)
var websitePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));

var options = new WebApplicationOptions
{
	Args = args,
	WebRootPath = websitePath
};

var builder = WebApplication.CreateBuilder(options);

// Add Razor Pages
builder.Services.AddRazorPages();

// Configure Kestrel to listen on HTTPS localhost:5001 using the development certificate
builder.WebHost.ConfigureKestrel(serverOptions =>
{
	serverOptions.ListenLocalhost(5001, listenOptions =>
	{
		listenOptions.UseHttps();
	});
});

// Add cookie authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
	.AddCookie(optionsCookie =>
	{
		optionsCookie.LoginPath = "/Login";
		optionsCookie.Cookie.HttpOnly = true;
		optionsCookie.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
	});
builder.Services.AddAuthorization();

// In-memory users (kept for quick login) — main data stored in SQLite
var users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
	["admin"] = "password"
};

// Helper: compute DB path (data/app.db next to project root)
// Use DbHelpers for DB path
string GetDbPath() => DbHelpers.GetDbPath(websitePath);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Serve `login.html` as the default document when visiting the site root
var defaultFilesOptions = new DefaultFilesOptions();
defaultFilesOptions.DefaultFileNames.Clear();
defaultFilesOptions.DefaultFileNames.Add("login.html");
app.UseDefaultFiles(defaultFilesOptions);
app.UseStaticFiles();

// Map Razor Pages (Pages/*.cshtml)
app.MapRazorPages();

// legacy `website` folder no longer served; assets copied into wwwroot

app.MapPost("/api/login", async (HttpContext http) =>
{
	var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(http.Request.Body);
	if (body == null || !body.TryGetValue("username", out var username) || !body.TryGetValue("password", out var password))
		return Results.BadRequest(new { error = "Missing credentials" });

	// Try DB-backed auth first
	try
	{
		var csb = new SqliteConnectionStringBuilder { DataSource = GetDbPath() };
		using var conn = new SqliteConnection(csb.ConnectionString);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT Name, Email, Role, PasswordHash FROM Users WHERE Email = $u OR Name = $u LIMIT 1";
		cmd.Parameters.AddWithValue("$u", username);
		using var rdr = cmd.ExecuteReader();
		if (rdr.Read())
		{
			var name = rdr.IsDBNull(0) ? username : rdr.GetString(0);
			var email = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1);
			var role = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2);
			var ph = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3);
			if (!string.IsNullOrEmpty(ph) && DbHelpers.VerifyPassword(password, ph))
			{
				var claims = new[] { new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role ?? "") };
				var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
				var principal = new ClaimsPrincipal(identity);
				await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
				return Results.Ok(new { user = name });
			}
		}
		conn.Close();
	}

	catch { /* ignore DB errors and fallback */ }

	// Fallback to in-memory users for legacy testing
	if (!users.TryGetValue(username, out var pw) || pw != password)
		return Results.Unauthorized();

	var fbClaims = new[] { new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, "Admin") };
	var fbIdentity = new ClaimsIdentity(fbClaims, CookieAuthenticationDefaults.AuthenticationScheme);
	var fbPrincipal = new ClaimsPrincipal(fbIdentity);
	await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, fbPrincipal);
	return Results.Ok(new { user = username });
});

app.MapPost("/api/logout", async (HttpContext http) =>
{
	await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
	return Results.Ok();
});

app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
	if (user?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
	return Results.Ok(new { user = user.Identity?.Name });
});

app.MapGet("/api/spaces", () =>
{
	var list = new List<object>();
	using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GetDbPath() }.ConnectionString);
	conn.Open();
	using var cmd = conn.CreateCommand();
	cmd.CommandText = "SELECT ID, Name, Capacity FROM Spaces ORDER BY ID";
	using var rdr = cmd.ExecuteReader();
	while (rdr.Read())
	{
		list.Add(new { id = rdr.GetInt32(0), name = rdr.GetString(1), capacity = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2) });
	}
	conn.Close();
	return Results.Ok(list);
}).RequireAuthorization();

// Rooms endpoints (persisted in Rooms table)
app.MapGet("/api/rooms", () =>
{
	var list = new List<object>();
	using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GetDbPath() }.ConnectionString);
	conn.Open();
	using var cmd = conn.CreateCommand();
	cmd.CommandText = "SELECT ID, Name, Capacity, Location FROM Rooms ORDER BY ID";
	using var rdr = cmd.ExecuteReader();
	while (rdr.Read())
	{
		list.Add(new { id = rdr.GetInt32(0), name = rdr.GetString(1), capacity = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2), location = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3) });
	}
	conn.Close();
	return Results.Ok(list);
});

app.MapPost("/api/rooms", async (HttpContext http) =>
{
	if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
	if (!http.User.IsInRole("Admin")) return Results.Forbid();
	var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, object>>(http.Request.Body);
	if (body == null || !body.TryGetValue("name", out var nameObj)) return Results.BadRequest(new { error = "Name required" });
	var name = nameObj?.ToString() ?? string.Empty;
	var capacity = 0;
	if (body.TryGetValue("capacity", out var capObj) && int.TryParse(capObj?.ToString(), out var cap)) capacity = cap;
	var location = body.TryGetValue("location", out var locObj) ? locObj?.ToString() ?? string.Empty : string.Empty;

	using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GetDbPath() }.ConnectionString);
	conn.Open();
	using var cmd = conn.CreateCommand();
	cmd.CommandText = "INSERT INTO Rooms (Name, Capacity, Location) VALUES ($name, $cap, $loc); SELECT last_insert_rowid();";
	cmd.Parameters.AddWithValue("$name", name);
	cmd.Parameters.AddWithValue("$cap", capacity);
	cmd.Parameters.AddWithValue("$loc", location);
	var id = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
	conn.Close();
	return Results.Created($"/api/rooms/{id}", new { id = id, name = name, capacity = capacity, location = location });
}).RequireAuthorization();

app.MapDelete("/api/rooms/{id:int}", (HttpContext http, int id) =>
{
	if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
	if (!http.User.IsInRole("Admin")) return Results.Forbid();
	using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GetDbPath() }.ConnectionString);
	conn.Open();
	using var cmd = conn.CreateCommand();
	cmd.CommandText = "DELETE FROM Rooms WHERE ID = $id";
	cmd.Parameters.AddWithValue("$id", id);
	var changed = cmd.ExecuteNonQuery();
	conn.Close();
	if (changed == 0) return Results.NotFound();
	return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/spaces", async (HttpContext http) =>
{
	if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
	if (!http.User.IsInRole("Admin")) return Results.Forbid();

	var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, object>>(http.Request.Body);
	if (body == null || !body.TryGetValue("name", out var nameObj)) return Results.BadRequest(new { error = "Name required" });
	var name = nameObj?.ToString() ?? string.Empty;
	var capacity = 0;
	if (body.TryGetValue("capacity", out var capObj) && int.TryParse(capObj?.ToString(), out var cap)) capacity = cap;

	using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GetDbPath() }.ConnectionString);
	conn.Open();
	using var cmd = conn.CreateCommand();
	cmd.CommandText = "INSERT INTO Spaces (Name, Capacity) VALUES ($name, $cap); SELECT last_insert_rowid();";
	cmd.Parameters.AddWithValue("$name", name);
	cmd.Parameters.AddWithValue("$cap", capacity);
	var id = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
	conn.Close();
	var created = new { id = id, name = name, capacity = capacity };
	return Results.Created($"/api/spaces/{id}", created);
}).RequireAuthorization();

app.MapDelete("/api/spaces/{id:int}", (HttpContext http, int id) =>
{
	if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
	if (!http.User.IsInRole("Admin")) return Results.Forbid();

	using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GetDbPath() }.ConnectionString);
	conn.Open();
	using var cmd = conn.CreateCommand();
	cmd.CommandText = "DELETE FROM Spaces WHERE ID = $id";
	cmd.Parameters.AddWithValue("$id", id);
	var changed = cmd.ExecuteNonQuery();
	conn.Close();
	if (changed == 0) return Results.NotFound();
	return Results.NoContent();
}).RequireAuthorization();


app.MapGet("/health", () => Results.Ok("OK"));

// Initialize SQLite database file and schema inside the project folder (next to `website`)
var projectDir = Path.GetFullPath(Path.Combine(websitePath, ".."));
var dataDir = Path.Combine(projectDir, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.GetFullPath(Path.Combine(dataDir, "app.db"));
DbHelpers.InitializeDatabase(dbPath);
// Seed an admin user record in the database (if missing)
DbHelpers.SeedAdminUser(dbPath);
// Seed default spaces (5 rooms) if none exist
DbHelpers.SeedDefaultSpaces(dbPath);
// Seed default rooms (separate Rooms table)
DbHelpers.SeedDefaultRooms(dbPath);

// Create reservation endpoint
app.MapPost("/api/reservations", async (HttpContext http) =>
{
	if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
	var body = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, object>>(http.Request.Body);
	if (body == null) return Results.BadRequest(new { error = "Invalid payload" });

	int ownerId = body.TryGetValue("ownerId", out var o) ? Convert.ToInt32(o) : 0;
	int spaceId = body.TryGetValue("spaceId", out var sp) ? Convert.ToInt32(sp) : 0;
	var dateStr = body.TryGetValue("date", out var d) ? d?.ToString() ?? DateTime.UtcNow.ToString("yyyy-MM-dd") : DateTime.UtcNow.ToString("yyyy-MM-dd");
	int startHour = body.TryGetValue("startHour", out var sh) ? Convert.ToInt32(sh) : 0;
	int hours = body.TryGetValue("hours", out var h) ? Convert.ToInt32(h) : 1;

	var date = DateTime.Parse(dateStr).Date;
	if (startHour < 0 || startHour > 23) return Results.BadRequest(new { error = "startHour must be 0-23" });
	var start = DateTime.SpecifyKind(date.AddHours(startHour), DateTimeKind.Utc);
	var end = start.AddHours(hours);

	using var conn2 = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GetDbPath() }.ConnectionString);
	conn2.Open();

	using (var chk = conn2.CreateCommand())
	{
		chk.CommandText = @"SELECT COUNT(1) FROM Reservation WHERE SpaceId = $sp AND Status = 'Booked' AND NOT (Ending_Date <= $s OR Starting_Date >= $e);";
		chk.Parameters.AddWithValue("$sp", spaceId);
		chk.Parameters.AddWithValue("$s", start.ToString("o"));
		chk.Parameters.AddWithValue("$e", end.ToString("o"));
		var conflict = Convert.ToInt32(chk.ExecuteScalar() ?? 0);
		if (conflict > 0)
		{
			conn2.Close();
			return Results.Conflict(new { error = "Time slot already booked for this space" });
		}
	}

	using var cmd2 = conn2.CreateCommand();
	cmd2.CommandText = "INSERT INTO Reservation (OwnerId, SpaceId, Starting_Date, Ending_Date, Date, StartHour, Hours, Status, Total_Amount) VALUES ($o,$sp,$s,$e,$d,$sh,$h,$st,$t); SELECT last_insert_rowid();";
	cmd2.Parameters.AddWithValue("$o", ownerId);
	cmd2.Parameters.AddWithValue("$sp", spaceId);
	cmd2.Parameters.AddWithValue("$s", start.ToString("o"));
	cmd2.Parameters.AddWithValue("$e", end.ToString("o"));
	cmd2.Parameters.AddWithValue("$d", date.ToString("yyyy-MM-dd"));
	cmd2.Parameters.AddWithValue("$sh", startHour);
	cmd2.Parameters.AddWithValue("$h", hours);
	cmd2.Parameters.AddWithValue("$st", "Booked");
	cmd2.Parameters.AddWithValue("$t", 0);
	var id = Convert.ToInt32(cmd2.ExecuteScalar() ?? 0);
	conn2.Close();
	return Results.Created($"/api/reservations/{id}", new { id = id, ownerId = ownerId, start = start.ToString("o"), end = end.ToString("o"), date = date.ToString("yyyy-MM-dd"), startHour = startHour, hours = hours, spaceId = spaceId });
}).RequireAuthorization();

// Reservations for a given space and date
app.MapGet("/api/reservations/space", (HttpContext http, int spaceId, string? date) =>
{
	if (http.User?.Identity?.IsAuthenticated != true) return Results.Unauthorized();
	var list = new List<object>();
	using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = GetDbPath() }.ConnectionString);
	conn.Open();
	using var cmd = conn.CreateCommand();
	var d = string.IsNullOrEmpty(date) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : date;
	cmd.CommandText = "SELECT ID, Starting_Date, Ending_Date, Date, StartHour, Hours, Status, Total_Amount, OwnerId FROM Reservation WHERE SpaceId = $sp AND Date = $d ORDER BY StartHour";
	cmd.Parameters.AddWithValue("$sp", spaceId);
	cmd.Parameters.AddWithValue("$d", d);
	using var rdr = cmd.ExecuteReader();
	while (rdr.Read())
	{
		list.Add(new
		{
			id = rdr.GetInt32(0),
			start = rdr.IsDBNull(1) ? null : rdr.GetString(1),
			end = rdr.IsDBNull(2) ? null : rdr.GetString(2),
			date = rdr.IsDBNull(3) ? null : rdr.GetString(3),
			startHour = rdr.IsDBNull(4) ? (int?)null : rdr.GetInt32(4),
			hours = rdr.IsDBNull(5) ? (int?)null : rdr.GetInt32(5),
			status = rdr.IsDBNull(6) ? null : rdr.GetString(6),
			total = rdr.IsDBNull(7) ? 0 : rdr.GetDouble(7),
			ownerId = rdr.IsDBNull(8) ? 0 : rdr.GetInt32(8)
		});
	}
	conn.Close();
	return Results.Ok(list);
}).RequireAuthorization();

// Legacy HTML routes -> redirect to new Razor pages
app.MapGet("/spaces-map.html", (HttpContext http) => Results.Redirect("/SpacesMap", false));
app.MapGet("/spaces.html", (HttpContext http) => Results.Redirect("/Spaces", false));
app.MapGet("/login.html", (HttpContext http) => Results.Redirect("/Login", false));
app.MapGet("/index.html", (HttpContext http) => Results.Redirect("/", false));

app.Run();


    

static class DbHelpers
{
		public static string GetDbPath(string websitePath)
		{
				var projectDir = Path.GetFullPath(Path.Combine(websitePath, ".."));
				var dataDir = Path.Combine(projectDir, "data");
				Directory.CreateDirectory(dataDir);
				var dbPath = Path.GetFullPath(Path.Combine(dataDir, "app.db"));
				return dbPath;
		}

		public static void InitializeDatabase(string dbPath)
		{
				var csb = new SqliteConnectionStringBuilder { DataSource = dbPath };
				using var conn = new SqliteConnection(csb.ConnectionString);
				conn.Open();
				using var cmd = conn.CreateCommand();

				cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Users (
	Id INTEGER PRIMARY KEY AUTOINCREMENT,
	Name TEXT,
	Last_Name TEXT,
	Email TEXT UNIQUE,
	Role TEXT,
	PasswordHash TEXT
);

CREATE TABLE IF NOT EXISTS Reservation (
	ID INTEGER PRIMARY KEY AUTOINCREMENT,
	OwnerId INTEGER NOT NULL,
	Starting_Date TEXT,
	Ending_Date TEXT,
	Status TEXT,
	Total_Amount REAL,
	FOREIGN KEY(OwnerId) REFERENCES Users(Id)
);

CREATE TABLE IF NOT EXISTS Facture (
	ID INTEGER PRIMARY KEY AUTOINCREMENT,
	Num_facture TEXT,
	date_facture TEXT,
	Amount_TTC REAL,
	Payment_Status TEXT,
	ReservationId INTEGER UNIQUE,
	FOREIGN KEY(ReservationId) REFERENCES Reservation(ID)
);

CREATE TABLE IF NOT EXISTS Ressources (
	ID INTEGER PRIMARY KEY AUTOINCREMENT,
	Name_ressource TEXT,
	Type_ressources TEXT,
	Capacity INTEGER,
	Price REAL,
	ReservationId INTEGER,
	FOREIGN KEY(ReservationId) REFERENCES Reservation(ID)
);

CREATE TABLE IF NOT EXISTS Spaces (
	ID INTEGER PRIMARY KEY AUTOINCREMENT,
	Name TEXT NOT NULL,
	Capacity INTEGER
);
 
CREATE TABLE IF NOT EXISTS Rooms (
	ID INTEGER PRIMARY KEY AUTOINCREMENT,
	Name TEXT NOT NULL,
	Capacity INTEGER,
	Location TEXT
);
";

				cmd.ExecuteNonQuery();

				// Migrate older DBs: ensure Users.PasswordHash column exists
				using (var pragma = conn.CreateCommand())
				{
					pragma.CommandText = "PRAGMA table_info(Users);";
					using var rdr = pragma.ExecuteReader();
					var hasPasswordHash = false;
					while (rdr.Read())
					{
						var colName = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1);
						if (string.Equals(colName, "PasswordHash", StringComparison.OrdinalIgnoreCase))
						{
							hasPasswordHash = true;
							break;
						}
					}
					rdr.Close();
					if (!hasPasswordHash)
					{
						using var alter = conn.CreateCommand();
						alter.CommandText = "ALTER TABLE Users ADD COLUMN PasswordHash TEXT;";
						alter.ExecuteNonQuery();
					}
				}

				// Migrate Reservation table: ensure SpaceId, Date, StartHour, Hours columns exist
				using (var pragmaR = conn.CreateCommand())
				{
					pragmaR.CommandText = "PRAGMA table_info(Reservation);";
					using var rdrR = pragmaR.ExecuteReader();
					var hasSpaceId = false; var hasDateCol = false; var hasStartHour = false; var hasHours = false;
					while (rdrR.Read())
					{
						var col = rdrR.IsDBNull(1) ? string.Empty : rdrR.GetString(1);
						switch (col.ToLowerInvariant())
						{
							case "spaceid": hasSpaceId = true; break;
							case "date": hasDateCol = true; break;
							case "starthour": hasStartHour = true; break;
							case "hours": hasHours = true; break;
						}
					}
					rdrR.Close();
					if (!hasSpaceId)
					{
						using var a = conn.CreateCommand();
						a.CommandText = "ALTER TABLE Reservation ADD COLUMN SpaceId INTEGER;";
						a.ExecuteNonQuery();
					}
					if (!hasDateCol)
					{
						using var a = conn.CreateCommand();
						a.CommandText = "ALTER TABLE Reservation ADD COLUMN Date TEXT;";
						a.ExecuteNonQuery();
					}
					if (!hasStartHour)
					{
						using var a = conn.CreateCommand();
						a.CommandText = "ALTER TABLE Reservation ADD COLUMN StartHour INTEGER;";
						a.ExecuteNonQuery();
					}
					if (!hasHours)
					{
						using var a = conn.CreateCommand();
						a.CommandText = "ALTER TABLE Reservation ADD COLUMN Hours INTEGER;";
						a.ExecuteNonQuery();
					}
				}

				conn.Close();
		}

		public static void SeedAdminUser(string dbPath)
		{
				var csb = new SqliteConnectionStringBuilder { DataSource = dbPath };
				using var conn = new SqliteConnection(csb.ConnectionString);
				conn.Open();
				using var cmd = conn.CreateCommand();

				cmd.CommandText = "SELECT COUNT(1) FROM Users WHERE Email = $email";
				cmd.Parameters.AddWithValue("$email", "admin@example.com");
				var exists = Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
				if (!exists)
				{
						var password = "admin"; // default admin password (change in production)
						var hash = CreatePasswordHash(password);
						using var insert = conn.CreateCommand();
						insert.CommandText = "INSERT INTO Users (Name, Last_Name, Email, Role, PasswordHash) VALUES ($n,$ln,$email,$role,$ph)";
						insert.Parameters.AddWithValue("$n", "Admin");
						insert.Parameters.AddWithValue("$ln", "User");
						insert.Parameters.AddWithValue("$email", "admin@example.com");
						insert.Parameters.AddWithValue("$role", "Admin");
						insert.Parameters.AddWithValue("$ph", hash);
						insert.ExecuteNonQuery();
				}

				conn.Close();
		}

			public static void SeedDefaultSpaces(string dbPath)
			{
				var csb = new SqliteConnectionStringBuilder { DataSource = dbPath };
				using var conn = new SqliteConnection(csb.ConnectionString);
				conn.Open();
				using var cmd = conn.CreateCommand();
				cmd.CommandText = "SELECT COUNT(1) FROM Spaces";
				var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
				if (count == 0)
				{
					var names = new[] { "Room A", "Room B", "Room C", "Room D", "Room E" };
					foreach (var n in names)
					{
						using var ins = conn.CreateCommand();
						ins.CommandText = "INSERT INTO Spaces (Name, Capacity) VALUES ($n, $c)";
						ins.Parameters.AddWithValue("$n", n);
						ins.Parameters.AddWithValue("$c", 6);
						ins.ExecuteNonQuery();
					}
				}
				conn.Close();
			}

		public static void SeedDefaultRooms(string dbPath)
		{
			var csb = new SqliteConnectionStringBuilder { DataSource = dbPath };
			using var conn = new SqliteConnection(csb.ConnectionString);
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = "SELECT COUNT(1) FROM Rooms";
			var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
			if (count == 0)
			{
				var names = new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };
				foreach (var n in names)
				{
					using var ins = conn.CreateCommand();
					ins.CommandText = "INSERT INTO Rooms (Name, Capacity, Location) VALUES ($n, $c, $loc)";
					ins.Parameters.AddWithValue("$n", n);
					ins.Parameters.AddWithValue("$c", 6);
					ins.Parameters.AddWithValue("$loc", "");
					ins.ExecuteNonQuery();
				}
			}
			conn.Close();
		}

		static string CreatePasswordHash(string password)
		{
				// PBKDF2 with HMACSHA256
				var salt = new byte[16];
				using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
				var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
				var hash = pbkdf2.GetBytes(32);
				var outBytes = new byte[1 + salt.Length + hash.Length];
				outBytes[0] = 0; // version
				Buffer.BlockCopy(salt, 0, outBytes, 1, salt.Length);
				Buffer.BlockCopy(hash, 0, outBytes, 1 + salt.Length, hash.Length);
				return Convert.ToBase64String(outBytes);
		}

		public static bool VerifyPassword(string password, string storedBase64)
		{
			try
			{
				var bytes = Convert.FromBase64String(storedBase64);
				if (bytes.Length < 1 + 16 + 32) return false;
				var version = bytes[0];
				if (version != 0) return false;
				var salt = new byte[16];
				Buffer.BlockCopy(bytes, 1, salt, 0, salt.Length);
				var hash = new byte[32];
				Buffer.BlockCopy(bytes, 1 + salt.Length, hash, 0, hash.Length);
				var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
				var test = pbkdf2.GetBytes(32);
				return CryptographicOperations.FixedTimeEquals(test, hash);
			}
			catch
			{
				return false;
			}
		}

}


