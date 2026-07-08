using System.Security.Cryptography;
using Npgsql;

// FONCTIONNALITE: acces centralise a la base PostgreSQL du projet.
public static class DbHelpers
{
    public static string GetConnectionString(IConfiguration config)
    {
        var connectionString = config["ConnectionStrings:Default"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:Default is not configured. Set it in appsettings.json or via the " +
                "ConnectionStrings__Default environment variable, e.g. " +
                "\"Host=192.168.10.30;Port=5432;Database=coworking;Username=coworking_app;Password=...\"");
        return connectionString;
    }

    public static void InitializeDatabase(string connectionString)
    {
        using var conn = OpenConnection(connectionString);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Users (
    Id SERIAL PRIMARY KEY,
    Name TEXT,
    Last_Name TEXT,
    Email TEXT UNIQUE,
    Role TEXT,
    PasswordHash TEXT,
    AccountEnabled INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Reservation (
    ID SERIAL PRIMARY KEY,
    OwnerId INTEGER NOT NULL,
    Starting_Date TEXT,
    Ending_Date TEXT,
    Status TEXT,
    Total_Amount DOUBLE PRECISION,
    FOREIGN KEY(OwnerId) REFERENCES Users(Id)
);

CREATE TABLE IF NOT EXISTS Facture (
    ID SERIAL PRIMARY KEY,
    Num_facture TEXT,
    date_facture TEXT,
    Amount_TTC DOUBLE PRECISION,
    Payment_Status TEXT,
    ReservationId INTEGER UNIQUE,
    FOREIGN KEY(ReservationId) REFERENCES Reservation(ID)
);

CREATE TABLE IF NOT EXISTS Ressources (
    ID SERIAL PRIMARY KEY,
    Name_ressource TEXT,
    Type_ressources TEXT,
    Capacity INTEGER,
    Price DOUBLE PRECISION,
    ReservationId INTEGER,
    FOREIGN KEY(ReservationId) REFERENCES Reservation(ID)
);

CREATE TABLE IF NOT EXISTS Spaces (
    ID SERIAL PRIMARY KEY,
    Name TEXT NOT NULL,
    Capacity INTEGER,
    PricePerHour DOUBLE PRECISION NOT NULL DEFAULT 5.0,
    Type TEXT NOT NULL DEFAULT 'Nomad'
);

CREATE TABLE IF NOT EXISTS Rooms (
    ID SERIAL PRIMARY KEY,
    Name TEXT NOT NULL,
    Capacity INTEGER,
    Location TEXT
);

CREATE TABLE IF NOT EXISTS AuditLog (
    Id SERIAL PRIMARY KEY,
    Timestamp TEXT NOT NULL,
    UserName TEXT,
    Action TEXT NOT NULL,
    Target TEXT,
    Details TEXT
);

CREATE TABLE IF NOT EXISTS Reminders (
    ID SERIAL PRIMARY KEY,
    ReservationId INTEGER NOT NULL UNIQUE,
    SentAt TEXT NOT NULL
);";

        cmd.ExecuteNonQuery();

        MigrateUsersTable(conn);
        MigrateReservationTable(conn);
        MigrateSpacesTable(conn);
        MigrateFactureTable(conn);
        MigrateRessourcesTable(conn);
    }

    public static void SeedAdminUser(string connectionString, string dataDir)
    {
        using var conn = OpenConnection(connectionString);

        // Never touch a *working* admin's password: only intervene when literally no admin
        // account can log in at all (none exist yet, or every existing admin row is missing both
        // a local password hash and an AD link). A previous version of this method matched on a
        // hardcoded admin@example.com email and reset that row's password on every restart,
        // silently undoing any password change the real admin made — and if the admin ever
        // changed their own email, it would seed a second, duplicate admin account. Matching on
        // Role = 'Admin' instead avoids both problems while still guaranteeing "someone can log
        // in" never becomes permanently false.
        var admins = new List<(int Id, string? PasswordHash, string? AdSam, string? AdUpn)>();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT Id, PasswordHash, ADSamAccountName, ADUserPrincipalName FROM Users WHERE Role = 'Admin' ORDER BY Id";
            using var rdr = sel.ExecuteReader();
            while (rdr.Read())
                admins.Add((
                    rdr.GetInt32(0),
                    rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    rdr.IsDBNull(3) ? null : rdr.GetString(3)));
        }

        bool CanLogIn((int Id, string? PasswordHash, string? AdSam, string? AdUpn) a) =>
            !string.IsNullOrEmpty(a.PasswordHash) || !string.IsNullOrWhiteSpace(a.AdSam) || !string.IsNullOrWhiteSpace(a.AdUpn);

        if (admins.Count > 0 && admins.Any(CanLogIn)) return;

        var initialPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        int targetId;
        if (admins.Count == 0)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = @"INSERT INTO Users (Name, Last_Name, Email, Role, PasswordHash) VALUES (@n,@ln,@email,@role,@ph)
                                    RETURNING Id;";
            insert.Parameters.AddWithValue("@n", "Admin");
            insert.Parameters.AddWithValue("@ln", "User");
            insert.Parameters.AddWithValue("@email", "admin@example.com");
            insert.Parameters.AddWithValue("@role", "Admin");
            insert.Parameters.AddWithValue("@ph", CreatePasswordHash(initialPassword));
            targetId = Convert.ToInt32(insert.ExecuteScalar() ?? 0);
        }
        else
        {
            // Every existing Admin row is locked out (no password hash, no AD link) — repair the
            // first one instead of seeding a duplicate admin account.
            targetId = admins[0].Id;
            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE Users SET PasswordHash = @ph WHERE Id = @id";
            update.Parameters.AddWithValue("@ph", CreatePasswordHash(initialPassword));
            update.Parameters.AddWithValue("@id", targetId);
            update.ExecuteNonQuery();
        }

        // Written into the app's local data directory (same trust boundary/ACLs as invoices/outbox)
        // instead of stdout, since stdout is commonly redirected to a log file with broader read
        // access than intended when running as a service — that would otherwise leak the password
        // to anyone with log access.
        Directory.CreateDirectory(dataDir);
        var passwordFilePath = Path.Combine(dataDir, "ADMIN_INITIAL_PASSWORD.txt");
        File.WriteAllText(passwordFilePath,
            $"Admin user Id {targetId} / password: {initialPassword}\r\nLog in, change this password, then delete this file. It will not be regenerated.\r\n");

        Console.WriteLine("======================================================================");
        Console.WriteLine($" Admin account ready (Id {targetId}). Initial password written to:");
        Console.WriteLine($" {passwordFilePath}");
        Console.WriteLine(" Log in, change the password, then delete that file. It will not be");
        Console.WriteLine(" regenerated or reset on later restarts unless every admin is locked out again.");
        Console.WriteLine("======================================================================");
    }

    public static void SeedDefaultSpaces(string connectionString)
    {
        using var conn = OpenConnection(connectionString);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Spaces";
        if (Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0) return;

        var seed = new (string Name, int Capacity, double Price, string Type)[]
        {
            ("Nomad Desk A", 1, 5.0, "Nomad"),
            ("Nomad Desk B", 1, 5.0, "Nomad"),
            ("Private Office C", 4, 12.0, "Office"),
            ("Meeting Room D", 8, 20.0, "Meeting"),
            ("Conference Room E", 16, 35.0, "Conference")
        };
        foreach (var s in seed)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO Spaces (Name, Capacity, PricePerHour, Type) VALUES (@n, @c, @p, @t)";
            ins.Parameters.AddWithValue("@n", s.Name);
            ins.Parameters.AddWithValue("@c", s.Capacity);
            ins.Parameters.AddWithValue("@p", s.Price);
            ins.Parameters.AddWithValue("@t", s.Type);
            ins.ExecuteNonQuery();
        }
    }

    public static bool VerifyPassword(string password, string storedBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(storedBase64);
            if (bytes.Length < 1 + 16 + 32) return false;
            if (bytes[0] != 0) return false;

            var salt = new byte[16];
            Buffer.BlockCopy(bytes, 1, salt, 0, salt.Length);
            var hash = new byte[32];
            Buffer.BlockCopy(bytes, 1 + salt.Length, hash, 0, hash.Length);

            var test = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(test, hash);
        }
        catch { return false; }
    }

    public static void WriteAudit(string connectionString, string? userName, string action, string? target = null, string? details = null)
    {
        try
        {
            using var conn = OpenConnection(connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO AuditLog (Timestamp, UserName, Action, Target, Details) VALUES (@ts, @u, @a, @t, @d)";
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@u", (object?)userName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@a", action);
            cmd.Parameters.AddWithValue("@t", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@d", (object?)details ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch { /* never break the request because of audit */ }
    }

    // --- Private helpers ---

    public static NpgsqlConnection OpenConnection(string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        return conn;
    }

    // SQLSTATE 23505 == unique_violation. Used to turn a raced-past-the-app-level-check unique
    // violation (Email or Name) into a friendly 409 instead of an unhandled 500.
    public static bool IsUniqueConstraintViolation(PostgresException ex) => ex.SqlState == PostgresErrorCodes.UniqueViolation;

    public static string CreatePasswordHash(string password)
    {
        var salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        var outBytes = new byte[1 + salt.Length + hash.Length];
        outBytes[0] = 0; // version
        Buffer.BlockCopy(salt, 0, outBytes, 1, salt.Length);
        Buffer.BlockCopy(hash, 0, outBytes, 1 + salt.Length, hash.Length);
        return Convert.ToBase64String(outBytes);
    }

    static Dictionary<string, bool> GetExistingColumns(NpgsqlConnection conn, string tableName, IEnumerable<string> columnsToCheck)
    {
        var has = columnsToCheck.ToDictionary(c => c, _ => false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t";
        cmd.Parameters.AddWithValue("@t", tableName.ToLowerInvariant());
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var col = rdr.GetString(0).ToLowerInvariant();
            if (has.ContainsKey(col)) has[col] = true;
        }
        return has;
    }

    static void MigrateUsersTable(NpgsqlConnection conn)
    {
        var has = GetExistingColumns(conn, "Users", new[]
        {
            "passwordhash", "emailverified", "emailverifytoken",
            "adsamaccountname", "aduserprincipalname", "adobjectguid", "accountenabled"
        });

        var migrations = new Dictionary<string, string>
        {
            ["passwordhash"] = "ALTER TABLE Users ADD COLUMN PasswordHash TEXT;",
            ["emailverified"] = "ALTER TABLE Users ADD COLUMN EmailVerified INTEGER NOT NULL DEFAULT 0;",
            ["emailverifytoken"] = "ALTER TABLE Users ADD COLUMN EmailVerifyToken TEXT;",
            ["adsamaccountname"] = "ALTER TABLE Users ADD COLUMN ADSamAccountName TEXT;",
            ["aduserprincipalname"] = "ALTER TABLE Users ADD COLUMN ADUserPrincipalName TEXT;",
            ["adobjectguid"] = "ALTER TABLE Users ADD COLUMN ADObjectGuid TEXT;",
            ["accountenabled"] = "ALTER TABLE Users ADD COLUMN AccountEnabled INTEGER NOT NULL DEFAULT 1;"
        };

        foreach (var (key, sql) in migrations)
        {
            if (!has[key])
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = sql;
                alter.ExecuteNonQuery();
            }
        }

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_ADSamAccountName ON Users(ADSamAccountName) WHERE ADSamAccountName IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_ADUserPrincipalName ON Users(ADUserPrincipalName) WHERE ADUserPrincipalName IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_ADObjectGuid ON Users(ADObjectGuid) WHERE ADObjectGuid IS NOT NULL;";
            idx.ExecuteNonQuery();
        }

        // Name doubles as a login identifier alongside Email, so it must be unique too — enforced
        // here at the DB level (not just an app-level check-then-insert, which races under concurrent
        // signups) so every write path is protected, not only the ones that remember to pre-check.
        try
        {
            using var nameIdx = conn.CreateCommand();
            nameIdx.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_Name ON Users (LOWER(Name)) WHERE Name IS NOT NULL;";
            nameIdx.ExecuteNonQuery();
        }
        catch (PostgresException)
        {
            // Pre-existing duplicate Names from before this constraint was introduced block index
            // creation; the app-level uniqueness checks still stop new duplicates. Deduplicate the
            // offending rows manually (e.g. `SELECT LOWER(Name), COUNT(*) FROM Users GROUP BY
            // LOWER(Name) HAVING COUNT(*) > 1`) and restart to get the DB-level guarantee back.
        }
    }

    static void MigrateReservationTable(NpgsqlConnection conn)
    {
        var has = GetExistingColumns(conn, "Reservation", new[]
        {
            "spaceid", "date", "starthour", "hours", "attendees", "accesstoken"
        });

        var migrations = new Dictionary<string, string>
        {
            ["spaceid"] = "ALTER TABLE Reservation ADD COLUMN SpaceId INTEGER;",
            ["date"] = "ALTER TABLE Reservation ADD COLUMN Date TEXT;",
            ["starthour"] = "ALTER TABLE Reservation ADD COLUMN StartHour INTEGER;",
            ["hours"] = "ALTER TABLE Reservation ADD COLUMN Hours INTEGER;",
            ["attendees"] = "ALTER TABLE Reservation ADD COLUMN Attendees TEXT;",
            ["accesstoken"] = "ALTER TABLE Reservation ADD COLUMN AccessToken TEXT;"
        };

        foreach (var (key, sql) in migrations)
        {
            if (!has[key])
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = sql;
                alter.ExecuteNonQuery();
            }
        }
    }

    static void MigrateSpacesTable(NpgsqlConnection conn)
    {
        var has = GetExistingColumns(conn, "Spaces", new[] { "priceperhour", "type" });
        if (!has["priceperhour"])
        {
            using var a = conn.CreateCommand();
            a.CommandText = "ALTER TABLE Spaces ADD COLUMN PricePerHour DOUBLE PRECISION NOT NULL DEFAULT 5.0;";
            a.ExecuteNonQuery();
        }
        if (!has["type"])
        {
            using var a = conn.CreateCommand();
            a.CommandText = "ALTER TABLE Spaces ADD COLUMN Type TEXT NOT NULL DEFAULT 'Nomad';";
            a.ExecuteNonQuery();
        }
    }

    static void MigrateRessourcesTable(NpgsqlConnection conn)
    {
        var has = GetExistingColumns(conn, "Ressources", new[] { "spaceid" });
        if (!has["spaceid"])
        {
            using var a = conn.CreateCommand();
            a.CommandText = "ALTER TABLE Ressources ADD COLUMN SpaceId INTEGER;";
            a.ExecuteNonQuery();
        }
    }

    static void MigrateFactureTable(NpgsqlConnection conn)
    {
        var has = GetExistingColumns(conn, "Facture", new[] { "pdfpath", "amount_ht", "amount_tva" });

        var migrations = new Dictionary<string, string>
        {
            ["pdfpath"] = "ALTER TABLE Facture ADD COLUMN PdfPath TEXT;",
            ["amount_ht"] = "ALTER TABLE Facture ADD COLUMN Amount_HT DOUBLE PRECISION;",
            ["amount_tva"] = "ALTER TABLE Facture ADD COLUMN Amount_TVA DOUBLE PRECISION;"
        };

        foreach (var (k, sql) in migrations)
        {
            if (!has[k])
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = sql;
                alter.ExecuteNonQuery();
            }
        }
    }
}
