using QRCoder;

// FONCTIONNALITE: generation des QR codes et jetons d'acces des reservations.
public static class QrService
{
    public static byte[] GeneratePng(string content, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    public static string NewToken()
        => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant() + "-" + Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant().Substring(0, 16);
}
