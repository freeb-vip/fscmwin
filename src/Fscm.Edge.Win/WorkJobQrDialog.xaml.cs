using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Fscm.Edge.Win.Models;
using QRCoder;

namespace Fscm.Edge.Win;

public partial class WorkJobQrDialog : Window
{
    public WorkJobQrDialog(OrderScanJob job)
    {
        InitializeComponent();
        string startPayload = WorkJobQrCodePolicy.BuildStartPayload(job.OperationTypeCode);
        DataContext = new ViewModel(
            job.OperationDisplay,
            startPayload,
            WorkJobQrCodePolicy.EndPayload,
            CreateQrImage(startPayload),
            CreateQrImage(WorkJobQrCodePolicy.EndPayload));
    }

    private static BitmapImage CreateQrImage(string payload)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using PngByteQRCode renderer = new(data);
        using MemoryStream stream = new(renderer.GetGraphic(10));
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed record ViewModel(
        string OperationDisplay,
        string StartPayload,
        string EndPayload,
        BitmapImage StartQrImage,
        BitmapImage EndQrImage);
}