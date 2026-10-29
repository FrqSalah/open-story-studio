using System.Buffers.Binary;
using SkiaSharp;
using Studio.Formats.Assets;

namespace Studio.Media.Images;

/// <summary>How a picture is fitted to the 320x240 screen.</summary>
public enum FitMode
{
    /// <summary>Keep the whole picture, add black borders.</summary>
    Pad,
    /// <summary>Fill the screen, crop what overflows.</summary>
    Crop,
}

/// <summary>Converts any picture (PNG, JPEG, BMP, GIF, WebP…) to the device screen formats.</summary>
public static class ImageConverter
{
    /// <summary>
    /// Converts to the v2/v3 format: 320x240, 16 colors (median cut + Floyd–Steinberg dithering), BI_RLE4.
    /// Returns the input unchanged when it is already compliant.
    /// </summary>
    public static byte[] ToDeviceBmp(byte[] input, FitMode fit = FitMode.Pad)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (AssetInspector.CheckDeviceImage(input) is null) return input;

        var pixels = LoadFitted(input, fit);
        var (palette, indices) = Quantizer.Quantize(pixels, AssetInspector.ScreenWidth, AssetInspector.ScreenHeight, maxColors: 16);
        return Rle4BmpEncoder.Encode(AssetInspector.ScreenWidth, AssetInspector.ScreenHeight, palette, indices);
    }

    /// <summary>Converts to the v1 format: 320x240 uncompressed 24-bit BMP.</summary>
    public static byte[] ToBmp24(byte[] input, FitMode fit = FitMode.Pad)
    {
        ArgumentNullException.ThrowIfNull(input);
        return EncodeBmp24(LoadFitted(input, fit), AssetInspector.ScreenWidth, AssetInspector.ScreenHeight);
    }

    /// <summary>Converts any supported picture to PNG (for thumbnails and previews).</summary>
    public static byte[] ToPng(byte[] input, int? maxWidth = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var bitmap = Decode(input);
        var width = maxWidth is { } w && bitmap.Width > w ? w : bitmap.Width;
        var height = (int)Math.Round(bitmap.Height * (width / (double)bitmap.Width));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var image = SKImage.FromBitmap(bitmap);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static SKBitmap Decode(byte[] input)
    {
        // SKBitmap.Decode throws on unknown content: probe with a codec first.
        using var data = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(data) ?? throw new MediaConversionException("Format d'image non reconnu.");
        return SKBitmap.Decode(codec) ?? throw new MediaConversionException("Image illisible.");
    }

    /// <summary>Decodes, fits to 320x240 on a black background and returns RGB pixels (top-down).</summary>
    private static Rgb[] LoadFitted(byte[] input, FitMode fit)
    {
        using var bitmap = Decode(input);
        const int w = AssetInspector.ScreenWidth, h = AssetInspector.ScreenHeight;

        var scale = fit == FitMode.Crop
            ? Math.Max(w / (float)bitmap.Width, h / (float)bitmap.Height)
            : Math.Min(w / (float)bitmap.Width, h / (float)bitmap.Height);
        var dw = bitmap.Width * scale;
        var dh = bitmap.Height * scale;
        var dest = SKRect.Create((w - dw) / 2, (h - dh) / 2, dw, dh);

        using var target = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(bitmap))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(image, dest, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        var buffer = target.GetPixelSpan();
        var pixels = new Rgb[w * h];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgb(buffer[i * 4], buffer[i * 4 + 1], buffer[i * 4 + 2]);
        }
        return pixels;
    }

    private static byte[] EncodeBmp24(Rgb[] pixels, int width, int height)
    {
        var stride = (width * 3 + 3) & ~3;
        var bmp = new byte[54 + stride * height];
        var s = bmp.AsSpan();
        s[0] = (byte)'B';
        s[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(s[2..], bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[10..], 54);
        BinaryPrimitives.WriteInt32LittleEndian(s[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(s[22..], height);
        BinaryPrimitives.WriteInt16LittleEndian(s[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[28..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(s[34..], stride * height);
        for (var y = 0; y < height; y++)
        {
            var row = s[(54 + (height - 1 - y) * stride)..];
            for (var x = 0; x < width; x++)
            {
                var p = pixels[y * width + x];
                row[x * 3] = p.B;
                row[x * 3 + 1] = p.G;
                row[x * 3 + 2] = p.R;
            }
        }
        return bmp;
    }
}

/// <summary>24-bit color.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>Median-cut color quantizer with Floyd–Steinberg error diffusion.</summary>
internal static class Quantizer
{
    public static (int[] Palette, byte[] Indices) Quantize(Rgb[] pixels, int width, int height, int maxColors)
    {
        var palette = MedianCut(pixels, maxColors);
        var indices = new byte[pixels.Length];

        // Error diffusion on a float copy of the image.
        var r = new float[pixels.Length];
        var g = new float[pixels.Length];
        var b = new float[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            r[i] = pixels[i].R;
            g[i] = pixels[i].G;
            b[i] = pixels[i].B;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var cr = Clamp(r[i]);
                var cg = Clamp(g[i]);
                var cb = Clamp(b[i]);
                var best = Nearest(palette, cr, cg, cb);
                indices[i] = (byte)best;

                var p = palette[best];
                var er = cr - ((p >> 16) & 0xFF);
                var eg = cg - ((p >> 8) & 0xFF);
                var eb = cb - (p & 0xFF);
                Spread(x + 1, y, 7f / 16);
                Spread(x - 1, y + 1, 3f / 16);
                Spread(x, y + 1, 5f / 16);
                Spread(x + 1, y + 1, 1f / 16);

                void Spread(int sx, int sy, float f)
                {
                    if (sx < 0 || sx >= width || sy >= height) return;
                    var j = sy * width + sx;
                    r[j] += er * f;
                    g[j] += eg * f;
                    b[j] += eb * f;
                }
            }
        }
        return (palette, indices);
    }

    private static int[] MedianCut(Rgb[] pixels, int maxColors)
    {
        var boxes = new List<List<Rgb>> { new(pixels) };
        while (boxes.Count < maxColors)
        {
            // Split the box with the widest channel range.
            var (index, channel, range) = (-1, 0, 0);
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                var (c, rg) = WidestChannel(boxes[i]);
                if (rg > range) (index, channel, range) = (i, c, rg);
            }
            if (index < 0) break;

            var box = boxes[index];
            box.Sort((a, b) => Channel(a, channel).CompareTo(Channel(b, channel)));
            var half = box.Count / 2;
            boxes[index] = box[..half];
            boxes.Add(box[half..]);
        }

        return [.. boxes.Where(bx => bx.Count > 0).Select(Average).Distinct()];
    }

    private static (int Channel, int Range) WidestChannel(List<Rgb> box)
    {
        byte minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        foreach (var p in box)
        {
            minR = Math.Min(minR, p.R); maxR = Math.Max(maxR, p.R);
            minG = Math.Min(minG, p.G); maxG = Math.Max(maxG, p.G);
            minB = Math.Min(minB, p.B); maxB = Math.Max(maxB, p.B);
        }
        int rr = maxR - minR, rg = maxG - minG, rb = maxB - minB;
        return rr >= rg && rr >= rb ? (0, rr) : rg >= rb ? (1, rg) : (2, rb);
    }

    private static byte Channel(Rgb p, int channel) => channel switch { 0 => p.R, 1 => p.G, _ => p.B };

    private static int Average(List<Rgb> box)
    {
        long r = 0, g = 0, b = 0;
        foreach (var p in box) { r += p.R; g += p.G; b += p.B; }
        var n = box.Count;
        return (int)(((r / n) << 16) | ((g / n) << 8) | (b / n));
    }

    private static int Nearest(int[] palette, int r, int g, int b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < palette.Length; i++)
        {
            var p = palette[i];
            int dr = r - ((p >> 16) & 0xFF), dg = g - ((p >> 8) & 0xFF), db = b - (p & 0xFF);
            // Perceptual weighting (approximate luma contribution).
            var d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (d < bestDistance) (best, bestDistance) = (i, d);
        }
        return best;
    }

    private static int Clamp(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;
}
