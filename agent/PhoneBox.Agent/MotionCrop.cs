namespace PhoneBox.Agent;

/// <summary>
/// Finding the video in a captured window without help: the part of the picture that keeps changing. Used for players
/// the browser extension cannot see (VLC, Media Player, a browser without the extension, a video in another site's
/// frame). The window is read as a coarse grid of brightness; every cell that changed recently is "hot", and the largest
/// solid block of hot cells is the video.
/// </summary>
/// <remarks>
/// The heat fades slowly (about 15 s), so a still scene does not shrink the crop to the one actor who moves; a block that
/// grows is taken quickly, one that shrinks only after it stays put for longer. No platform code: tested everywhere.
/// </remarks>
internal sealed class MotionCrop
{
    internal const int Cols = 160, Rows = 90;
    /// <summary>Per update, at about 10 a second: half of the heat is gone after some 14 s.</summary>
    internal const float Decay = 0.995f;
    /// <summary>Heat a cell gets when its brightness moves by more than <see cref="Diff"/>.</summary>
    internal const float Kick = 0.25f;
    internal const float Hot = 0.12f;
    internal const int Diff = 6;
    /// <summary>Updates a larger block must stay put before it is used (about 1.2 s).</summary>
    internal const int GrowAfter = 12;
    /// <summary>Updates a smaller block must stay put before the crop shrinks to it (about 4 s).</summary>
    internal const int ShrinkAfter = 40;
    /// <summary>A block smaller than this part of the window is not taken for a video.</summary>
    internal const double MinShare = 0.02;
    /// <summary>The block must fill this much of its bounding box: a video is a rectangle, scattered changes are not.</summary>
    internal const double MinFill = 0.5;

    private readonly float[] _heat = new float[Cols * Rows];
    private readonly byte[] _prev = new byte[Cols * Rows];
    private readonly int[] _queue = new int[Cols * Rows];
    private readonly int[] _label = new int[Cols * Rows];
    private bool _hasPrev;
    private int _width, _height;
    private PixelRect? _candidate;
    private int _stable;

    /// <summary>The video's rectangle in window pixels, or null while nothing has been found.</summary>
    public PixelRect? Crop { get; private set; }

    public void Reset()
    {
        Array.Clear(_heat);
        _hasPrev = false;
        _candidate = null;
        _stable = 0;
        Crop = null;
    }

    /// <summary>The brightness of each cell of a BGRA picture (four points of it, averaged), into <paramref name="grid"/>.</summary>
    public static void Sample(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> grid)
    {
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var sum = 0;
                for (var k = 0; k < 4; k++)
                {
                    // points at 1/4 and 3/4 of the cell, both ways
                    var x = (int)((c + 0.25 + (k & 1) * 0.5) * width / Cols);
                    var y = (int)((r + 0.25 + (k >> 1) * 0.5) * height / Rows);
                    var i = y * stride + x * 4;
                    sum += (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;
                }
                grid[r * Cols + c] = (byte)(sum >> 2);
            }
        }
    }

    /// <summary>One more look at the window (a grid from <see cref="Sample"/> of a <paramref name="width"/>-wide picture).</summary>
    public void Update(ReadOnlySpan<byte> grid, int width, int height)
    {
        if (width != _width || height != _height)
        {
            Reset();
            _width = width;
            _height = height;
        }
        for (var i = 0; i < _heat.Length; i++)
        {
            var h = _heat[i] * Decay;
            if (_hasPrev && Math.Abs(grid[i] - _prev[i]) > Diff) h = Math.Min(1f, h + Kick);
            _heat[i] = h;
            _prev[i] = grid[i];
        }
        _hasPrev = true;

        var block = LargestBlock();
        if (block is not { } b) { _candidate = null; _stable = 0; return; }
        var px = ToPixels(b);
        var slack = 2 * Math.Max(width / Cols, height / Rows) + 2;
        if (_candidate is { } c && c.Near(px, slack)) _stable++;
        else { _candidate = px; _stable = 0; }

        if (Crop is not { } now) { if (_stable >= GrowAfter) Crop = px; return; }
        if (now.Near(px, slack)) return;
        var grows = px.Area >= now.Area;
        if (_stable >= (grows ? GrowAfter : ShrinkAfter)) Crop = px;
    }

    /// <summary>How hot a rectangle of the window is, 0 to 1: the mean heat of the cells whose centre is inside it.</summary>
    public double Score(PixelRect r)
    {
        if (_width == 0 || _height == 0) return 0;
        double sum = 0;
        var n = 0;
        for (var row = 0; row < Rows; row++)
        {
            var cy = (row + 0.5) * _height / Rows;
            if (cy < r.Y || cy >= r.Bottom) continue;
            for (var col = 0; col < Cols; col++)
            {
                var cx = (col + 0.5) * _width / Cols;
                if (cx < r.X || cx >= r.Right) continue;
                sum += _heat[row * Cols + col];
                n++;
            }
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>The bounding box, in cells, of the largest 4-connected block of hot cells that looks like a rectangle.</summary>
    private (int C0, int R0, int C1, int R1)? LargestBlock()
    {
        Array.Clear(_label);
        var bestCount = 0;
        (int, int, int, int) best = default;
        var next = 0;
        for (var start = 0; start < _heat.Length; start++)
        {
            if (_label[start] != 0 || _heat[start] < Hot) continue;
            next++;
            int head = 0, tail = 0, count = 0;
            int c0 = Cols, r0 = Rows, c1 = -1, r1 = -1;
            _queue[tail++] = start;
            _label[start] = next;
            while (head < tail)
            {
                var i = _queue[head++];
                count++;
                int col = i % Cols, row = i / Cols;
                if (col < c0) c0 = col;
                if (col > c1) c1 = col;
                if (row < r0) r0 = row;
                if (row > r1) r1 = row;
                void Visit(int j)
                {
                    if (_label[j] != 0 || _heat[j] < Hot) return;
                    _label[j] = next;
                    _queue[tail++] = j;
                }
                if (col > 0) Visit(i - 1);
                if (col < Cols - 1) Visit(i + 1);
                if (row > 0) Visit(i - Cols);
                if (row < Rows - 1) Visit(i + Cols);
            }
            if (count > bestCount) { bestCount = count; best = (c0, r0, c1, r1); }
        }
        if (bestCount < MinShare * Cols * Rows) return null;
        var (bc0, br0, bc1, br1) = best;
        var boxCells = (bc1 - bc0 + 1) * (br1 - br0 + 1);
        return bestCount >= MinFill * boxCells ? best : null;
    }

    private PixelRect ToPixels((int C0, int R0, int C1, int R1) b)
    {
        var x0 = b.C0 * _width / Cols;
        var y0 = b.R0 * _height / Rows;
        var x1 = (b.C1 + 1) * _width / Cols;
        var y1 = (b.R1 + 1) * _height / Rows;
        return new PixelRect(x0, y0, x1 - x0, y1 - y0).Clamp(_width, _height).Even();
    }
}
