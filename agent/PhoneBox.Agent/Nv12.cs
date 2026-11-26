namespace PhoneBox.Agent;

/// <summary>
/// A part of a BGRA picture, scaled, as NV12 (the 4:2:0 layout H.264 encoders take): a full-size plane of brightness,
/// then one of colour at half the size each way, U and V interleaved. BT.709, studio range, which is what a decoder
/// assumes for HD video that does not say otherwise. Bilinear scaling with integer weights; no platform code.
/// </summary>
internal static class Nv12
{
    public static int Size(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// <paramref name="crop"/> of the BGRA picture at <paramref name="src"/> (rows <paramref name="stride"/> bytes apart),
    /// scaled to <paramref name="width"/> × <paramref name="height"/> (both even), written to <paramref name="dst"/>.
    /// </summary>
    public static unsafe void FromBgra(byte* src, int stride, PixelRect crop, byte* dst, int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("even size needed");
        if (crop.IsEmpty) throw new ArgumentException("empty crop");

        // where each output column and row samples the source: the left/top pixel and the weight (0..256) of the next
        var x0 = new int[width];
        var xw = new int[width];
        var x1 = new int[width];
        Axis(crop.X, crop.Width, width, x0, x1, xw);
        var y0 = new int[height];
        var yw = new int[height];
        var y1 = new int[height];
        Axis(crop.Y, crop.Height, height, y0, y1, yw);

        var yPlane = dst;
        var uv = dst + width * height;
        var r = stackalloc int[4];
        var g = stackalloc int[4];
        var b = stackalloc int[4];
        for (var oy = 0; oy < height; oy += 2)
        {
            for (var ox = 0; ox < width; ox += 2)
            {
                for (var k = 0; k < 4; k++)
                {
                    var px = ox + (k & 1);
                    var py = oy + (k >> 1);
                    var row0 = src + (long)y0[py] * stride;
                    var row1 = src + (long)y1[py] * stride;
                    int wx = xw[px], wy = yw[py];
                    var a = row0 + x0[px] * 4;
                    var bb = row0 + x1[px] * 4;
                    var c = row1 + x0[px] * 4;
                    var d = row1 + x1[px] * 4;
                    // bilinear: across the row first, then down (weights out of 256 each, so the sum is out of 65536)
                    b[k] = Mix(a[0], bb[0], c[0], d[0], wx, wy);
                    g[k] = Mix(a[1], bb[1], c[1], d[1], wx, wy);
                    r[k] = Mix(a[2], bb[2], c[2], d[2], wx, wy);
                    yPlane[py * width + px] = (byte)(((47 * r[k] + 157 * g[k] + 16 * b[k] + 128) >> 8) + 16);
                }
                var ar = (r[0] + r[1] + r[2] + r[3] + 2) >> 2;
                var ag = (g[0] + g[1] + g[2] + g[3] + 2) >> 2;
                var ab = (b[0] + b[1] + b[2] + b[3] + 2) >> 2;
                var at = uv + (oy >> 1) * width + ox;
                at[0] = (byte)Math.Clamp(((-26 * ar - 87 * ag + 112 * ab + 128) >> 8) + 128, 16, 240);
                at[1] = (byte)Math.Clamp(((112 * ar - 102 * ag - 10 * ab + 128) >> 8) + 128, 16, 240);
            }
        }
    }

    private static int Mix(int a, int b, int c, int d, int wx, int wy)
    {
        var top = a * (256 - wx) + b * wx;
        var bottom = c * (256 - wx) + d * wx;
        return (top * (256 - wy) + bottom * wy + 32768) >> 16;
    }

    private static void Axis(int start, int length, int size, int[] lo, int[] hi, int[] weight)
    {
        var step = length / (double)size;
        var last = start + length - 1;
        for (var i = 0; i < size; i++)
        {
            var at = Math.Clamp(start + (i + 0.5) * step - 0.5, start, last);
            var whole = (int)Math.Floor(at);
            lo[i] = whole;
            hi[i] = Math.Min(whole + 1, last);
            weight[i] = (int)Math.Round((at - whole) * 256);
        }
    }
}
