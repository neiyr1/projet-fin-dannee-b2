using System.Globalization;
using Npgsql;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

// FONCTIONNALITE: donnees d'une ligne de facture pour une reservation.
public class InvoiceLine
{
    public int ReservationId { get; set; }
    public string SpaceName { get; set; } = string.Empty;
    public DateTime SlotStart { get; set; }
    public int Hours { get; set; }
    public double PricePerHour { get; set; }
    public double LineHT { get; set; }
    public string? AccessToken { get; set; }
}

public class InvoiceData
{
    public int ReservationId { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string SpaceName { get; set; } = string.Empty;
    public DateTime SlotStart { get; set; }
    public int Hours { get; set; }
    public double PricePerHour { get; set; }
    public double AmountHT { get; set; }
    public double TvaRate { get; set; }
    public double AmountTVA { get; set; }
    public double AmountTTC { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyAddress { get; set; } = string.Empty;
    public string CompanySiret { get; set; } = string.Empty;
    public List<InvoiceLine> Lines { get; set; } = new();
}

// FONCTIONNALITE: creation et stockage des factures PDF associees aux reservations.
public class InvoiceService
{
    readonly string _connectionString;
    readonly string _invoicesDir;
    readonly IConfiguration _config;

    public InvoiceService(string connectionString, string invoicesDir, IConfiguration config)
    {
        _connectionString = connectionString;
        _invoicesDir = invoicesDir;
        _config = config;
        Directory.CreateDirectory(_invoicesDir);
    }

    public InvoiceData? BuildForReservation(int reservationId)
        => BuildForReservations(new[] { reservationId });

    public InvoiceData? BuildForReservations(IList<int> reservationIds)
    {
        if (reservationIds.Count == 0) return null;
        using var conn = DbHelpers.OpenConnection(_connectionString);

        var lines = new List<InvoiceLine>();
        string ownerName = "", ownerEmail = "";
        var inClause = string.Join(",", reservationIds.Select((_, i) => $"@p{i}"));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"SELECT r.ID, r.Hours, r.StartHour, r.Date, r.AccessToken,
                                       u.Name, u.Email,
                                       s.Name, s.PricePerHour
                                 FROM Reservation r
                                 JOIN Users u ON r.OwnerId = u.Id
                                 JOIN Spaces s ON r.SpaceId = s.ID
                                 WHERE r.ID IN ({inClause})
                                 ORDER BY r.Date, r.StartHour";
            for (int i = 0; i < reservationIds.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", reservationIds[i]);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var hours = rdr.IsDBNull(1) ? 1 : rdr.GetInt32(1);
                var startHour = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
                var dateStr = rdr.IsDBNull(3) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : rdr.GetString(3);
                var token = rdr.IsDBNull(4) ? null : rdr.GetString(4);
                ownerName = rdr.IsDBNull(5) ? ownerName : rdr.GetString(5);
                ownerEmail = rdr.IsDBNull(6) ? ownerEmail : rdr.GetString(6);
                var price = rdr.IsDBNull(8) ? 0.0 : rdr.GetDouble(8);
                lines.Add(new InvoiceLine
                {
                    ReservationId = rdr.GetInt32(0),
                    SpaceName = rdr.IsDBNull(7) ? string.Empty : rdr.GetString(7),
                    SlotStart = DateTime.Parse(dateStr, CultureInfo.InvariantCulture).AddHours(startHour),
                    Hours = hours,
                    PricePerHour = price,
                    LineHT = Math.Round(price * hours, 2),
                    AccessToken = token
                });
            }
        }
        if (lines.Count == 0) return null;

        var tvaRate = _config.GetValue<double>("Company:TvaRate", 0.20);
        var amountHT = Math.Round(lines.Sum(l => l.LineHT), 2);
        var amountTVA = Math.Round(amountHT * tvaRate, 2);
        var amountTTC = Math.Round(amountHT + amountTVA, 2);
        var date = DateTime.UtcNow;
        var primaryId = lines[0].ReservationId;
        return new InvoiceData
        {
            ReservationId = primaryId,
            Number = $"INV-{date:yyyyMMdd}-{primaryId:D5}",
            Date = date,
            OwnerName = ownerName,
            OwnerEmail = ownerEmail,
            SpaceName = lines[0].SpaceName,
            SlotStart = lines[0].SlotStart,
            Hours = lines.Sum(l => l.Hours),
            PricePerHour = lines[0].PricePerHour,
            AmountHT = amountHT,
            TvaRate = tvaRate,
            AmountTVA = amountTVA,
            AmountTTC = amountTTC,
            CompanyName = _config.GetValue<string>("Company:Name") ?? "CoWork Manager",
            CompanyAddress = _config.GetValue<string>("Company:Address") ?? "",
            CompanySiret = _config.GetValue<string>("Company:Siret") ?? "",
            Lines = lines
        };
    }

    public string GeneratePdf(InvoiceData data)
    {
        var fileName = $"{data.Number}.pdf";
        var filePath = Path.Combine(_invoicesDir, fileName);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(40);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Grey.Darken3));

                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(data.CompanyName).FontSize(18).Bold().FontColor(Colors.Blue.Darken2);
                        col.Item().Text(data.CompanyAddress).FontSize(9);
                        if (!string.IsNullOrEmpty(data.CompanySiret))
                            col.Item().Text($"SIRET: {data.CompanySiret}").FontSize(9);
                    });
                    row.ConstantItem(180).Column(col =>
                    {
                        col.Item().AlignRight().Text("INVOICE").FontSize(22).Bold().FontColor(Colors.Grey.Darken4);
                        col.Item().AlignRight().Text(data.Number).FontSize(11).SemiBold();
                        col.Item().AlignRight().Text($"Date: {data.Date:yyyy-MM-dd}").FontSize(9);
                    });
                });

                page.Content().PaddingVertical(20).Column(col =>
                {
                    col.Spacing(15);

                    col.Item().Background(Colors.Grey.Lighten3).Padding(12).Column(c =>
                    {
                        c.Item().Text("Billed to").FontColor(Colors.Grey.Darken1).FontSize(9);
                        c.Item().Text(data.OwnerName).Bold().FontSize(12);
                        if (!string.IsNullOrWhiteSpace(data.OwnerEmail))
                            c.Item().Text(data.OwnerEmail).FontSize(9);
                    });

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(4);
                            c.RelativeColumn(2);
                            c.RelativeColumn(2);
                            c.RelativeColumn(2);
                        });
                        table.Header(h =>
                        {
                            static IContainer HeaderStyle(IContainer c) =>
                                c.DefaultTextStyle(t => t.SemiBold()).PaddingVertical(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten1);
                            h.Cell().Element(HeaderStyle).Text("Description");
                            h.Cell().Element(HeaderStyle).AlignRight().Text("Hours");
                            h.Cell().Element(HeaderStyle).AlignRight().Text("Unit (€)");
                            h.Cell().Element(HeaderStyle).AlignRight().Text("Total (€)");
                        });
                        static IContainer CellStyle(IContainer c) => c.PaddingVertical(6).BorderBottom(1).BorderColor(Colors.Grey.Lighten3);
                        var rows = data.Lines.Count > 0 ? data.Lines : new List<InvoiceLine>
                        {
                            new InvoiceLine { SpaceName = data.SpaceName, SlotStart = data.SlotStart, Hours = data.Hours, PricePerHour = data.PricePerHour, LineHT = data.AmountHT }
                        };
                        foreach (var line in rows)
                        {
                            table.Cell().Element(CellStyle).Column(c =>
                            {
                                c.Item().Text(line.SpaceName).SemiBold();
                                c.Item().Text($"Slot: {line.SlotStart:yyyy-MM-dd HH:mm} → {line.SlotStart.AddHours(line.Hours):HH:mm}").FontSize(9).FontColor(Colors.Grey.Darken1);
                            });
                            table.Cell().Element(CellStyle).AlignRight().Text(line.Hours.ToString());
                            table.Cell().Element(CellStyle).AlignRight().Text(line.PricePerHour.ToString("F2", CultureInfo.InvariantCulture));
                            table.Cell().Element(CellStyle).AlignRight().Text(line.LineHT.ToString("F2", CultureInfo.InvariantCulture));
                        }
                    });

                    col.Item().AlignRight().Width(220).Column(c =>
                    {
                        c.Item().Row(r =>
                        {
                            r.RelativeItem().Text("Subtotal (HT)").FontColor(Colors.Grey.Darken1);
                            r.ConstantItem(80).AlignRight().Text($"{data.AmountHT:F2} €");
                        });
                        c.Item().Row(r =>
                        {
                            r.RelativeItem().Text($"VAT ({data.TvaRate * 100:F0}%)").FontColor(Colors.Grey.Darken1);
                            r.ConstantItem(80).AlignRight().Text($"{data.AmountTVA:F2} €");
                        });
                        c.Item().PaddingTop(6).BorderTop(1).BorderColor(Colors.Grey.Darken2).PaddingTop(6).Row(r =>
                        {
                            r.RelativeItem().Text("Total (TTC)").Bold();
                            r.ConstantItem(80).AlignRight().Text($"{data.AmountTTC:F2} €").Bold().FontColor(Colors.Blue.Darken2);
                        });
                    });

                    var qrLines = data.Lines.Where(l => !string.IsNullOrEmpty(l.AccessToken)).ToList();
                    if (qrLines.Count > 0)
                    {
                        col.Item().PaddingTop(14).Text("Entry passes").Bold().FontSize(11).FontColor(Colors.Blue.Darken2);
                        col.Item().PaddingTop(4).Text("Scan the QR code at the entrance to access your reserved space.").FontSize(9).FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingTop(6).Row(r =>
                        {
                            foreach (var l in qrLines.Take(4))
                            {
                                r.RelativeItem().PaddingRight(8).Column(c =>
                                {
                                    var png = QrService.GeneratePng(l.AccessToken!, 6);
                                    c.Item().AlignCenter().Width(110).Image(png);
                                    c.Item().AlignCenter().Text($"{l.SpaceName}").FontSize(9).SemiBold();
                                    c.Item().AlignCenter().Text($"{l.SlotStart:MM-dd HH:mm}").FontSize(8).FontColor(Colors.Grey.Darken1);
                                });
                            }
                        });
                    }

                    col.Item().PaddingTop(20).Text("Thank you for choosing CoWork Manager.").FontSize(9).Italic().FontColor(Colors.Grey.Darken1);
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        });

        document.GeneratePdf(filePath);
        return filePath;
    }

    public int SaveFactureRow(InvoiceData data, string pdfPath)
    {
        using var conn = DbHelpers.OpenConnection(_connectionString);

        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM Facture WHERE ReservationId = @rid";
            del.Parameters.AddWithValue("@rid", data.ReservationId);
            del.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO Facture (Num_facture, date_facture, Amount_HT, Amount_TVA, Amount_TTC, Payment_Status, ReservationId, PdfPath)
                            VALUES (@num, @date, @ht, @tva, @ttc, @status, @rid, @path)
                            RETURNING ID;";
        cmd.Parameters.AddWithValue("@num", data.Number);
        cmd.Parameters.AddWithValue("@date", data.Date.ToString("o"));
        cmd.Parameters.AddWithValue("@ht", data.AmountHT);
        cmd.Parameters.AddWithValue("@tva", data.AmountTVA);
        cmd.Parameters.AddWithValue("@ttc", data.AmountTTC);
        cmd.Parameters.AddWithValue("@status", "Pending");
        cmd.Parameters.AddWithValue("@rid", data.ReservationId);
        cmd.Parameters.AddWithValue("@path", pdfPath);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
