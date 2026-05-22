using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

public class EmailService
{
    readonly IConfiguration _config;
    readonly ILogger<EmailService> _logger;
    readonly string _outboxDir;

    public EmailService(IConfiguration config, ILogger<EmailService> logger, string outboxDir)
    {
        _config = config;
        _logger = logger;
        _outboxDir = outboxDir;
        Directory.CreateDirectory(_outboxDir);
    }

    public Task SendWelcomeAsync(string toEmail, string toName, string? verifyUrl)
    {
        var safeName = System.Net.WebUtility.HtmlEncode(toName ?? "");
        var verifyBlock = string.IsNullOrEmpty(verifyUrl) ? "" : $@"
  <p>To confirm your email address (optional), click the button below:</p>
  <p><a href='{verifyUrl}' style='display:inline-block;padding:10px 16px;background:#1e40af;color:#fff;text-decoration:none;border-radius:4px'>Confirm my email</a></p>
  <p style='color:#888;font-size:12px'>Or copy this link: {verifyUrl}</p>";
        var html = $@"<div style='font-family:Segoe UI,Arial,sans-serif;color:#222'>
  <h2 style='color:#1e40af'>Welcome to CoWork Manager</h2>
  <p>Hello {safeName},</p>
  <p>Your account has been created successfully. You can now sign in and book workspaces, meeting rooms and conference rooms.</p>{verifyBlock}
  <p style='color:#888;font-size:12px;margin-top:24px'>This is an automated message from CoWork Manager.</p>
</div>";
        var text = $"Welcome {toName}!\nYour account is ready." + (string.IsNullOrEmpty(verifyUrl) ? "" : $"\nConfirm your email: {verifyUrl}");
        var slug = $"WELCOME-{toEmail}";
        return SendAsync(toEmail, toName, "Welcome to CoWork Manager", html, text, null, slug);
    }

    public Task SendBookingConfirmationAsync(string toEmail, string toName, InvoiceData invoice, string pdfAttachmentPath)
    {
        var slots = string.Join("", invoice.Lines.Select(l =>
            $"<tr><td style='padding:4px 12px;color:#666'>{l.SpaceName}</td><td style='padding:4px 12px'>{l.SlotStart:yyyy-MM-dd HH:mm} ({l.Hours}h)</td></tr>"));
        var html = $@"<div style='font-family:Segoe UI,Arial,sans-serif;color:#222'>
  <h2 style='color:#1e40af'>Your booking is confirmed</h2>
  <p>Hello {System.Net.WebUtility.HtmlEncode(toName ?? "")},</p>
  <p>Here are the details of your reservation at <strong>{System.Net.WebUtility.HtmlEncode(invoice.CompanyName)}</strong>:</p>
  <table style='border-collapse:collapse'>{slots}
    <tr><td style='padding:4px 12px;color:#666'>Total (TTC)</td><td style='padding:4px 12px'><strong>{invoice.AmountTTC:F2} €</strong></td></tr>
    <tr><td style='padding:4px 12px;color:#666'>Invoice</td><td style='padding:4px 12px'>{invoice.Number}</td></tr>
  </table>
  <p>Your invoice (with QR code entry passes) is attached to this email.</p>
  <p style='color:#888;font-size:12px;margin-top:24px'>This is an automated message from CoWork Manager.</p>
</div>";
        var text = $"Booking confirmed\nInvoice: {invoice.Number}\nTotal TTC: {invoice.AmountTTC:F2} €";
        var subject = invoice.Lines.Count > 1
            ? $"Booking confirmation — {invoice.Lines.Count} slots ({invoice.Number})"
            : $"Booking confirmation — {invoice.SpaceName} ({invoice.SlotStart:yyyy-MM-dd HH:mm})";
        return SendAsync(toEmail, toName, subject, html, text, pdfAttachmentPath, invoice.Number);
    }

    public Task SendBookingCancellationAsync(string toEmail, string toName, string spaceName, DateTime slotStart, int hours, string? invoiceNumber)
    {
        var html = $@"<div style='font-family:Segoe UI,Arial,sans-serif;color:#222'>
  <h2 style='color:#b91c1c'>Booking cancelled</h2>
  <p>Hello {System.Net.WebUtility.HtmlEncode(toName ?? "")},</p>
  <p>Your reservation for <strong>{System.Net.WebUtility.HtmlEncode(spaceName)}</strong> on
  <strong>{slotStart:yyyy-MM-dd HH:mm}</strong> ({hours}h) has been cancelled.</p>
  <p>Invoice: {invoiceNumber ?? "—"}</p>
</div>";
        var slug = $"CANCEL-{invoiceNumber ?? slotStart.ToString("yyyyMMddHHmm")}";
        return SendAsync(toEmail, toName, $"Booking cancelled — {spaceName} ({slotStart:yyyy-MM-dd HH:mm})", html, $"Cancelled: {spaceName} {slotStart}", null, slug);
    }

    public Task SendBookingModifiedAsync(string toEmail, string toName, string spaceName, DateTime newStart, int newHours)
    {
        var html = $@"<div style='font-family:Segoe UI,Arial,sans-serif;color:#222'>
  <h2 style='color:#1e40af'>Booking modified</h2>
  <p>Hello {System.Net.WebUtility.HtmlEncode(toName ?? "")},</p>
  <p>Your reservation for <strong>{System.Net.WebUtility.HtmlEncode(spaceName)}</strong> has been updated.</p>
  <p>New slot: <strong>{newStart:yyyy-MM-dd HH:mm}</strong> for {newHours}h.</p>
</div>";
        return SendAsync(toEmail, toName, $"Booking modified — {spaceName} ({newStart:yyyy-MM-dd HH:mm})", html, $"Modified: {spaceName} {newStart}", null, $"MOD-{newStart:yyyyMMddHHmm}");
    }

    public Task SendReminderAsync(string toEmail, string toName, string spaceName, DateTime slotStart, int hours)
    {
        var html = $@"<div style='font-family:Segoe UI,Arial,sans-serif;color:#222'>
  <h2 style='color:#1e40af'>Reminder: your booking starts in 1 hour</h2>
  <p>Hello {System.Net.WebUtility.HtmlEncode(toName ?? "")},</p>
  <p>Your reservation at <strong>{System.Net.WebUtility.HtmlEncode(spaceName)}</strong> starts on <strong>{slotStart:yyyy-MM-dd HH:mm}</strong> ({hours}h).</p>
  <p>See you soon!</p>
</div>";
        return SendAsync(toEmail, toName, $"Reminder — your booking starts at {slotStart:HH:mm}", html, $"Reminder: {spaceName} {slotStart}", null, $"REM-{slotStart:yyyyMMddHHmm}");
    }

    public Task SendInviteAsync(string toEmail, string organizerName, string spaceName, DateTime slotStart, int hours)
    {
        var html = $@"<div style='font-family:Segoe UI,Arial,sans-serif;color:#222'>
  <h2 style='color:#1e40af'>You have been invited to a meeting</h2>
  <p>{System.Net.WebUtility.HtmlEncode(organizerName)} has booked <strong>{System.Net.WebUtility.HtmlEncode(spaceName)}</strong> and invited you.</p>
  <p>When: <strong>{slotStart:yyyy-MM-dd HH:mm}</strong> · Duration: {hours}h.</p>
</div>";
        return SendAsync(toEmail, null, $"Invitation — {spaceName} ({slotStart:yyyy-MM-dd HH:mm})", html, $"Invitation: {spaceName} {slotStart}", null, $"INV-{slotStart:yyyyMMddHHmm}");
    }

    async Task SendAsync(string toEmail, string? toName, string subject, string html, string text, string? attachmentPath, string slug)
    {
        var message = new MimeMessage();
        var fromAddr = _config.GetValue<string>("Smtp:From") ?? "noreply@cowork-manager.local";
        var fromName = _config.GetValue<string>("Smtp:FromName") ?? "CoWork Manager";
        message.From.Add(new MailboxAddress(fromName, fromAddr));
        var recipient = string.IsNullOrWhiteSpace(toEmail) ? fromAddr : toEmail;
        message.To.Add(new MailboxAddress(string.IsNullOrWhiteSpace(toName) ? recipient : toName, recipient));
        message.Subject = subject;

        var body = new BodyBuilder { HtmlBody = html, TextBody = text };
        if (!string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
            body.Attachments.Add(attachmentPath);
        message.Body = body.ToMessageBody();

        var host = _config.GetValue<string>("Smtp:Host");
        if (string.IsNullOrWhiteSpace(host))
        {
            var safeSlug = string.Concat(slug.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
            var outPath = Path.Combine(_outboxDir, $"{safeSlug}.eml");
            await using var fs = File.Create(outPath);
            await message.WriteToAsync(fs);
            _logger.LogInformation("SMTP not configured — wrote .eml to {Path}", outPath);
            return;
        }

        var port = _config.GetValue<int?>("Smtp:Port") ?? 587;
        var user = _config.GetValue<string>("Smtp:User");
        var pwd = _config.GetValue<string>("Smtp:Password");
        var useStartTls = _config.GetValue<bool?>("Smtp:UseStartTls") ?? true;

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(host, port, useStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);
            if (!string.IsNullOrEmpty(user))
                await client.AuthenticateAsync(user, pwd ?? string.Empty);
            await client.SendAsync(message);
            _logger.LogInformation("Sent email '{Subject}' to {Email}", subject, recipient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP send failed — falling back to .eml file");
            var safeSlug = string.Concat(slug.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
            var outPath = Path.Combine(_outboxDir, $"{safeSlug}.eml");
            await using var fs = File.Create(outPath);
            await message.WriteToAsync(fs);
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(true);
        }
    }
}
