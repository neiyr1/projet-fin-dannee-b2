public class ReminderService : BackgroundService
{
    readonly string _dbPath;
    readonly EmailService _email;
    readonly ILogger<ReminderService> _logger;

    public ReminderService(string dbPath, EmailService email, ILogger<ReminderService> logger)
    {
        _dbPath = dbPath;
        _email = email;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReminderService started — polls every 60s for H-1 bookings.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Reminder tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddMinutes(55);
        var windowEnd = now.AddMinutes(65);

        using var conn = DbHelpers.OpenConnection(_dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.ID, r.Date, r.StartHour, r.Hours, u.Email, u.Name, s.Name
                            FROM Reservation r
                            LEFT JOIN Users u ON u.Id = r.OwnerId
                            LEFT JOIN Spaces s ON s.ID = r.SpaceId
                            WHERE r.Status = 'Booked'
                              AND NOT EXISTS (SELECT 1 FROM Reminders rm WHERE rm.ReservationId = r.ID)";
        using var rdr = cmd.ExecuteReader();
        var due = new List<(int id, DateTime start, int hours, string email, string name, string space)>();
        while (rdr.Read())
        {
            var dateStr = rdr.IsDBNull(1) ? null : rdr.GetString(1);
            if (string.IsNullOrEmpty(dateStr)) continue;
            var sh = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
            var hr = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3);
            if (!DateTime.TryParse(dateStr, out var date)) continue;
            var start = date.AddHours(sh);
            if (start < windowStart || start > windowEnd) continue;
            var email = rdr.IsDBNull(4) ? null : rdr.GetString(4);
            if (string.IsNullOrEmpty(email)) continue;
            due.Add((rdr.GetInt32(0), start, hr,
                email!,
                rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                rdr.IsDBNull(6) ? "" : rdr.GetString(6)));
        }
        rdr.Close();

        foreach (var d in due)
        {
            try
            {
                await _email.SendReminderAsync(d.email, d.name, d.space, d.start, d.hours);
                using var mark = conn.CreateCommand();
                mark.CommandText = "INSERT INTO Reminders (ReservationId, SentAt) VALUES ($id, $ts)";
                mark.Parameters.AddWithValue("$id", d.id);
                mark.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                mark.ExecuteNonQuery();
                DbHelpers.WriteAudit(_dbPath, "system", "ReminderSent", $"Reservation#{d.id}");
                _logger.LogInformation("Sent H-1 reminder for reservation {Id} to {Email}", d.id, d.email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reminder send failed for reservation {Id}", d.id);
            }
        }
    }
}
