namespace AiChat.Services;

/// <summary>Camera capture and photo downscaling (phone photos are often larger than the 5 MB image limit).</summary>
public static class PhotoHelper
{
    public const int MaxSide = 2048;

    /// <summary>Takes a photo with the phone camera. Returns (file name, JPEG bytes) or null if cancelled.</summary>
    public static async Task<(string Name, byte[] Data)?> CapturePhotoAsync()
    {
        if (!MediaPicker.Default.IsCaptureSupported) return null;

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted) throw new InvalidOperationException("Camera permission was denied.");

        var photo = await MediaPicker.Default.CapturePhotoAsync();
        if (photo == null) return null;

        await using var s = await photo.OpenReadAsync();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        var data = Downscale(ms.ToArray());
        var name = $"photo-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
        return (name, data);
    }

    /// <summary>Shrinks big JPEG/PNG photos to at most 2048 px and re-encodes them as JPEG (Android only).</summary>
    public static byte[] Downscale(byte[] data)
    {
#if ANDROID
        try
        {
            if (data.Length < 3 * 1024 * 1024) return data;

            var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
            Android.Graphics.BitmapFactory.DecodeByteArray(data, 0, data.Length, bounds);
            var sample = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > MaxSide * 2) sample *= 2;

            var opts = new Android.Graphics.BitmapFactory.Options { InSampleSize = sample };
            using var bmp = Android.Graphics.BitmapFactory.DecodeByteArray(data, 0, data.Length, opts);
            if (bmp == null) return data;

            var scale = Math.Min(1.0, (double)MaxSide / Math.Max(bmp.Width, bmp.Height));
            using var scaled = scale < 1.0
                ? Android.Graphics.Bitmap.CreateScaledBitmap(bmp, (int)(bmp.Width * scale), (int)(bmp.Height * scale), true)
                : bmp;

            using var outMs = new MemoryStream();
            scaled!.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 85, outMs);
            return outMs.ToArray();
        }
        catch
        {
            return data;
        }
#else
        return data;
#endif
    }
}
