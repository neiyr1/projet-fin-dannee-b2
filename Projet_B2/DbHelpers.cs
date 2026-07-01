using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

// FONCTIONNALITE: acces centralise a la base SQLite locale du projet.
public static class DbHelpers
{
    public static string GetDbPath(string websitePath)
    {
        var projectDir = Path.GetFullPath(Path.Combine(websitePath, ".."));
        var dataDir = Path.Combine(projectDir, "data");
        Directory.CreateDirectory(dataDir);
        return Path.GetFullPath(Path.Combine(dataDir, "app.db"));
    }

    public static void InitializeDatabase(string dbPath)
    {
        using var conn = OpenConnection(dbPath);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Users (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT,
    Last_Name TEXT,
    Email TEXT UNIQUE,
    Role TEXT,
    PasswordHash TEXT,
    AccountEnabled INTEGER NOT NULL DEFAULT 1
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
    Capacity INTEGER,
    PricePerHour REAL NOT NULL DEFAULT 5.0,
    Type TEXT NOT NULL DEFAULT 'Nomad'
);

CREATE TABLE IF NOT EXISTS Rooms (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Capacity INTEGER,
    Location TEXT
);

CREATE TABLE IF NOT EXISTS AuditLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,
    UserName TEXT,
    Action TEXT NOT NULL,
    Target TEXT,
    Details TEXT
);

CREATE TABLE IF NOT EXISTS Reminders (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
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

    public static void SeedAdminUser(string dbPath)
    {
        using var conn = OpenConnection(dbPath);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COUNT(1) FROM Users WHERE Email = $email";
        cmd.Parameters.AddWithValue("$email", "admin@example.com");
        var exists = Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
        var hash = CreatePasswordHash("admin123");

        if (!exists)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO Users (Name, Last_Name, Email, Role, PasswordHash) VALUES ($n,$ln,$email,$role,$ph)";
            insert.Parameters.AddWithValue("$n", "Admin");
            insert.Parameters.AddWithValue("$ln", "User");
            insert.Parameters.AddWithValue("$email", "admin@example.com");
            insert.Parameters.AddWithValue("$role", "Admin");
            insert.Parameters.AddWithValue("$ph", hash);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE Users SET PasswordHash = $ph WHERE Email = $email";
            update.Parameters.AddWithValue("$ph", hash);
            update.Parameters.AddWithValue("$email", "admin@example.com");
            update.ExecuteNonQuery();
        }
    }

    public static void SeedDefaultSpaces(string dbPath)
    {
        using var conn = OpenConnection(dbPath);
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
            ins.CommandText = "INSERT INTO Spaces (Name, Capacity, PricePerHour, Type) VALUES ($n, $c, $p, $t)";
            ins.Parameters.AddWithValue("$n", s.Name);
            ins.Parameters.AddWithValue("$c", s.Capacity);
            ins.Parameters.AddWithValue("$p", s.Price);
            ins.Parameters.AddWithValue("$t", s.Type);
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

    public static void WriteAudit(string dbPath, string? userName, string action, string? target = null, string? details = null)
    {
        try
        {
            using var conn = OpenConnection(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO AuditLog (Timestamp, UserName, Action, Target, Details) VALUES ($ts, $u, $a, $t, $d)";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$u", (object?)userName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$a", action);
            cmd.Parameters.AddWithValue("$t", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$d", (object?)details ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch { /* never break the request because of audit */ }
    }

    // --- Private helpers ---

    public static SqliteConnection OpenConnection(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        conn.Open();
        return conn;
    }

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

    static void MigrateUsersTable(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Users);";
        using var rdr = pragma.ExecuteReader();
        var has = new Dictionary<string, bool>
        {
            ["passwordhash"] = false,
            ["emailverified"] = false,
            ["emailverifytoken"] = false,
            ["adsamaccountname"] = false,
            ["aduserprincipalname"] = false,
            ["adobjectguid"] = false,
            ["accountenabled"] = false
        };
        while (rdr.Read())
        {
            var col = (rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1)).ToLowerInvariant();
            if (has.ContainsKey(col)) has[col] = true;
        }
        rdr.Close();

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
    }

    static void MigrateReservationTable(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Reservation);";
        using var rdr = pragma.ExecuteReader();
        var has = new Dictionary<string, bool>
        {
            ["spaceid"] = false, ["date"] = false, ["starthour"] = false, ["hours"] = false,
            ["attendees"] = false, ["accesstoken"] = false
        };
        while (rdr.Read())
        {
            var col = (rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1)).ToLowerInvariant();
            if (has.ContainsKey(col)) has[col] = true;
        }
        rdr.Close();

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

    static void MigrateSpacesTable(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Spaces);";
        using var rdr = pragma.ExecuteReader();
        var has = new Dictionary<string, bool> { ["priceperhour"] = false, ["type"] = false };
        while (rdr.Read())
        {
            var col = (rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1)).ToLowerInvariant();
            if (has.ContainsKey(col)) has[col] = true;
        }
        rdr.Close();
        if (!has["priceperhour"])
        {
            using var a = conn.CreateCommand();
            a.CommandText = "ALTER TABLE Spaces ADD COLUMN PricePerHour REAL NOT NULL DEFAULT 5.0;";
            a.ExecuteNonQuery();
        }
        if (!has["type"])
        {
            using var a = conn.CreateCommand();
            a.CommandText = "ALTER TABLE Spaces ADD COLUMN Type TEXT NOT NULL DEFAULT 'Nomad';";
            a.ExecuteNonQuery();
        }
    }

    static void MigrateRessourcesTable(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Ressources);";
        using var rdr = pragma.ExecuteReader();
        var has = new Dictionary<string, bool> { ["spaceid"] = false };
        while (rdr.Read())
        {
            var col = (rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1)).ToLowerInvariant();
            if (has.ContainsKey(col)) has[col] = true;
        }
        rdr.Close();
        if (!has["spaceid"])
        {
            using var a = conn.CreateCommand();
            a.CommandText = "ALTER TABLE Ressources ADD COLUMN SpaceId INTEGER;";
            a.ExecuteNonQuery();
        }
    }

    static void MigrateFactureTable(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Facture);";
        using var rdr = pragma.ExecuteReader();
        var has = new Dictionary<string, bool>
        {
            ["pdfpath"] = false, ["amount_ht"] = false, ["amount_tva"] = false
        };
        while (rdr.Read())
        {
            var col = (rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1)).ToLowerInvariant();
            if (has.ContainsKey(col)) has[col] = true;
        }
        rdr.Close();

        var migrations = new Dictionary<string, string>
        {
            ["pdfpath"] = "ALTER TABLE Facture ADD COLUMN PdfPath TEXT;",
            ["amount_ht"] = "ALTER TABLE Facture ADD COLUMN Amount_HT REAL;",
            ["amount_tva"] = "ALTER TABLE Facture ADD COLUMN Amount_TVA REAL;"
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
