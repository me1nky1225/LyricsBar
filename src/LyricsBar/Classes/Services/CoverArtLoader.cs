using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 把媒体会话里的封面缩略图读成可直接塞进 CSS 的 data URI。
/// Reads the media session's artwork thumbnail into a data URI that can be dropped straight into CSS.
///
/// 为什么不直接把原始字节转 base64：播放器给的封面动辄 500×500、上百 KB，转成 base64 再涨三分之一，
/// 而任务栏上的封面只有 34 像素。每换一首歌就往 WebView 推一百多 KB 的字符串是纯粹的浪费，
/// 因此在 C# 侧先缩到 128 像素并转成 JPEG，最终通常只有几 KB。
/// Why not base64 the raw bytes: players hand out artwork at 500×500 and hundreds of kilobytes, base64 adds another third,
/// while the taskbar cover is 34 pixels. Pushing a hundred-plus kilobytes of string into WebView2 on every track change is
/// pure waste, so the image is downscaled to 128 pixels and re-encoded as JPEG here, which usually lands at a few kilobytes.
/// </summary>
internal static class CoverArtLoader
{
    /// <summary>缩放后的最长边像素数。任务栏封面是 34 DIP，128 已经能在 2 倍缩放下保持清晰。</summary>
    public const int DefaultMaxEdgePixels = 128;

    /// <summary>JPEG 质量。封面是照片类内容，88 在肉眼无差别的前提下能把体积压到很小。</summary>
    private const int JpegQuality = 88;

    /// <summary>
    /// 读取并转换封面。任何一步失败都返回空串而不是抛异常——封面是锦上添花，不能因为它让歌词栏出问题。
    /// Reads and converts the artwork. Any failure returns an empty string rather than throwing: artwork is a bonus and must
    /// never be allowed to break the lyric bar.
    /// </summary>
    /// <param name="reference">媒体会话给出的缩略图引用；可为 null。/ Thumbnail reference from the media session; may be null.</param>
    /// <param name="maxEdgePixels">缩放后的最长边像素数。/ Longest-edge pixel count after downscaling.</param>
    public static async Task<string> LoadDataUriAsync(
        IRandomAccessStreamReference? reference,
        int maxEdgePixels = DefaultMaxEdgePixels)
    {
        if (reference is null)
            return string.Empty;

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream is null || stream.Size == 0)
                return string.Empty;

            var bytes = await ReadAllBytesAsync(stream);
            return bytes.Length == 0 ? string.Empty : EncodeJpegDataUri(bytes, maxEdgePixels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 播放器退出、缩略图被回收、图片格式损坏都会走到这里，属于正常现象。
            Utils.Log.Warn("Cover", $"读取封面失败: {ex.GetType().Name} {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 把 WinRT 流整个读进字节数组。用 <see cref="DataReader"/> 而不是 <c>AsStreamForRead()</c>：
    /// 后者依赖 .NET 5 起已不再随框架提供的 WindowsRuntime 流扩展，引用它会给项目多加一个过时的包。
    /// Reads the whole WinRT stream into a byte array. <see cref="DataReader"/> is used instead of <c>AsStreamForRead()</c>
    /// because the latter comes from the WindowsRuntime stream extensions that are no longer shipped with the framework as of
    /// .NET 5, and depending on them would add an obsolete package to the project.
    /// </summary>
    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        var size = (uint)stream.Size;
        if (size == 0)
            return [];

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size);

        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// 解码、按需缩小、再编码为 JPEG 的 data URI。只缩小不放大：小封面放大只会变糊，还白白增大体积。
    /// Decodes, downscales when needed, and re-encodes as a JPEG data URI. It only ever shrinks: enlarging a small cover just
    /// blurs it and inflates the payload for nothing.
    /// </summary>
    private static string EncodeJpegDataUri(byte[] bytes, int maxEdgePixels)
    {
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        decoded.StreamSource = new MemoryStream(bytes);
        decoded.EndInit();
        decoded.Freeze();

        BitmapSource source = decoded;
        if (maxEdgePixels > 0 && decoded.PixelWidth > maxEdgePixels)
        {
            var scale = maxEdgePixels / (double)decoded.PixelWidth;
            var scaled = new TransformedBitmap(decoded, new ScaleTransform(scale, scale));
            scaled.Freeze();
            source = scaled;
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);

        return "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
    }
}
