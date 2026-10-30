using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DLSS5Master.Views;

/// <summary>Turns a picked picture into a JPEG small enough for the community server (≤ 1600 px, under 900 KB).</summary>
public static class ImageShrink
{
    public const int MaxBytes = 880 * 1024;

    public static async Task<byte[]> ToJpegAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        if (w == 0 || h == 0) throw new InvalidOperationException("That file is not a picture.");

        foreach (var (side, quality) in new[] { (1600u, 0.82), (1280u, 0.75), (1024u, 0.7), (800u, 0.65) })
        {
            double scale = Math.Min(1.0, (double)side / Math.Max(w, h));
            uint tw = Math.Max(1, (uint)Math.Round(w * scale)), th = Math.Max(1, (uint)Math.Round(h * scale));
            var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = tw, ScaledHeight = th, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetSoftwareBitmap(pixels);
            await encoder.FlushAsync();

            var bytes = new byte[output.Size];
            output.Seek(0);
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size);
                reader.ReadBytes(bytes);
            }
            if (bytes.Length <= MaxBytes) return bytes;
        }
        throw new InvalidOperationException("That picture is too detailed to send even after shrinking it.");
    }
}
