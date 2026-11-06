namespace NhlWorkbench.Core.Dds;

/// <summary>Plain bilinear resize of an RGBA8 image -- used when a replacement picture isn't the size of the texture it replaces.</summary>
public static class ImageResampler
{
    public static Rgba32Image Resize(Rgba32Image source, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Target size must be positive.");
        }

        if (source.Width == width && source.Height == height)
        {
            return source;
        }

        var pixels = new byte[width * height * 4];
        float xScale = (float)source.Width / width;
        float yScale = (float)source.Height / height;

        for (int y = 0; y < height; y++)
        {
            float sy = Math.Clamp(((y + 0.5f) * yScale) - 0.5f, 0, source.Height - 1);
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, source.Height - 1);
            float fy = sy - y0;

            for (int x = 0; x < width; x++)
            {
                float sx = Math.Clamp(((x + 0.5f) * xScale) - 0.5f, 0, source.Width - 1);
                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, source.Width - 1);
                float fx = sx - x0;

                for (int c = 0; c < 4; c++)
                {
                    float top = Lerp(source.Pixels[(((y0 * source.Width) + x0) * 4) + c], source.Pixels[(((y0 * source.Width) + x1) * 4) + c], fx);
                    float bottom = Lerp(source.Pixels[(((y1 * source.Width) + x0) * 4) + c], source.Pixels[(((y1 * source.Width) + x1) * 4) + c], fx);
                    pixels[(((y * width) + x) * 4) + c] = (byte)Math.Clamp(Lerp(top, bottom, fy) + 0.5f, 0f, 255f);
                }
            }
        }

        return new Rgba32Image { Width = width, Height = height, Pixels = pixels };
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
