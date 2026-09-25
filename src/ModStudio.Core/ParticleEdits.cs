using System.Buffers.Binary;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// A colour the layer's compiled script uses as a constant. The compiler stores each constant as eight SIMD lanes (a
/// float3 repeats x y z x y z x y) in the layer's constant table (field 0, two 16-byte entries), again in its value
/// ranges (field 1) and inside its compiled code (CCompilerBlobCache). Every vanilla colour constant appears in all
/// three, so an edit rewrites all three; equal constants in one layer cannot be told apart and change together.
/// </summary>
public sealed record ParticleColorConstant(int Layer, int Index, int Width, float[] Value);

/// <summary>
/// A recolour: hue turned in degrees, saturation and brightness as multipliers (1 keeps them). Colours shift in HSV, so a
/// turned colour keeps its saturation and HDR intensity; curve tangents, which have no colour of their own, go through
/// the matching linear RGB transform so the curve keeps its shape.
/// </summary>
public sealed record ColorShift(double Hue = 0, double Saturation = 1, double Brightness = 1)
{
    public float[] Apply(float[] rgb)
    {
        var (h, s, v) = ParticleEdits.ToHsv(rgb);
        return ParticleEdits.FromHsv(((h + Hue) % 360 + 360) % 360, Math.Clamp(s * Saturation, 0, 1), v * Brightness);
    }
    public float[,] Linear => ParticleEdits.ColorMatrix(Hue, Saturation, Brightness);
}

/// <summary>
/// Edits to a baked effect that keep its compiled script valid: renderer texture and mesh paths, renderer settings, curve
/// keys, and colour constants. Nothing here changes the effect's structure (layers, renderers, spawning), which lives in
/// compiled bytecode.
/// </summary>
public static partial class ParticleEdits
{
    /// <summary>Points a texture, mesh or other resource setting at another data-relative path.</summary>
    public static void SetPath(ParticleFile file, int property, string path)
    {
        path = path.Trim().Replace('\\', '/');
        Require(path.Length > 0 && !path.Contains(".."), "Enter a data-relative path such as data/hd/vfx/textures/….texture.");
        var o = Property(file, property); Require(o.Has(3), "This setting does not hold a file path.");
        o.SetU32(3, file.Intern(path)); file.Update(property, o);
    }

    /// <summary>Sets a renderer setting's 16-byte value; zeros are stored by leaving the field out, as the game's own files do.</summary>
    public static void SetSetting(ParticleFile file, int property, byte[] value)
    {
        Require(value.Length == 16, "Settings hold 16 bytes.");
        var o = Property(file, property); Require(!o.Has(3) && o.U32(1) is not (0 or 12 or 13 or 14 or 15), "Only number and on/off settings can be edited here.");
        o.Set(2, value.All(b => b == 0) ? null : value); file.Update(property, o);
    }
    public static byte[] Floats(params float[] values) { var b = new byte[16]; for (int i = 0; i < values.Length && i < 4; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), values[i]); return b; }
    public static byte[] Ints(params int[] values) { var b = new byte[16]; for (int i = 0; i < values.Length && i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), values[i]); return b; }

    /// <summary>Replaces a curve's key values (and, when given, its tangents). Key times and count stay as baked.</summary>
    public static void SetCurve(ParticleFile file, int curve, float[] values, float[]? tangents = null)
    {
        var o = file.ClassName(curve) == "CParticleNodeSamplerData_Curve" ? file.Object((uint)curve) : null;
        Require(o != null, "Not a curve.");
        var old = o!.Floats(18); Require(values.Length == old.Length && values.All(float.IsFinite), "A curve edit must keep every key and use finite numbers.");
        o.SetFloats(18, values);
        if (tangents != null) { Require(tangents.Length == o.Floats(19).Length && tangents.All(float.IsFinite), "Tangents do not match the curve."); o.SetFloats(19, tangents); }
        file.Update(curve, o);
    }

    /// <summary>The colour-like constants of a layer: float3/float4 lane patterns with differing, non-negative colour channels.</summary>
    public static List<ParticleColorConstant> ColorConstants(ParticleFile file, int layer)
    {
        var found = new List<ParticleColorConstant>();
        if (file.ClassName(layer) != "CLayerCompileCache" || file.Object((uint)layer) is not { } o) return found;
        var entries = o.Elements(0);
        for (int i = 0; i + 1 < entries.Length; i += 2)
        {
            var lanes = Lanes(entries[i], entries[i + 1]);
            foreach (var width in new[] { 3, 4 })
            {
                if (!Enumerable.Range(0, 8).All(k => BitConverter.SingleToUInt32Bits(lanes[k]) == BitConverter.SingleToUInt32Bits(lanes[k % width]))) continue;
                var value = lanes[..width];
                // Not colours: negative or huge numbers, pure 0/1 masks, greys, and (a, b, 0, 0) UV scale/offset pairs.
                if (value.Any(v => !float.IsFinite(v) || v < 0 || v > 64) || value.All(v => v is 0 or 1) || value[..3].Max() - value[..3].Min() < 1e-6f || width == 4 && value[2] == 0 && value[3] == 0) break;
                found.Add(new ParticleColorConstant(layer, i, width, value)); break;
            }
        }
        return found;
    }

    /// <summary>Rewrites one colour constant in the layer's constant table, value ranges and compiled code. Returns how many compiled copies changed.</summary>
    public static int SetColorConstant(ParticleFile file, int layer, int index, float[] value)
    {
        var o = file.ClassName(layer) == "CLayerCompileCache" ? file.Object((uint)layer) : null;
        Require(o != null, "Not a layer.");
        var entries = o!.Elements(0); Require(index >= 0 && index + 1 < entries.Length, "No such constant.");
        var constant = ColorConstants(file, layer).FirstOrDefault(c => c.Index == index) ?? throw new InvalidDataException("That constant is not a colour this editor can change.");
        Require(value.Length == constant.Width && value.All(v => float.IsFinite(v) && v >= 0), "Colours need one non-negative number per channel.");
        byte[] before = [.. entries[index], .. entries[index + 1]], after = new byte[32];
        for (int k = 0; k < 8; k++) BinaryPrimitives.WriteSingleLittleEndian(after.AsSpan(k * 4), value[k % constant.Width]);
        if (before.SequenceEqual(after)) return 0;
        // Compiled code first: an edit the script would not see must not change the tables either.
        int compiled = 0; var blobs = new List<(int Reference, byte[] Payload)>();
        foreach (var blob in o.Array(9).Distinct())
        {
            var payload = file.Chunk(blob)?.Payload.ToArray(); if (payload == null) continue;
            int n = Replace(payload, before, after); if (n > 0) { compiled += n; blobs.Add(((int)blob, payload)); }
        }
        Require(compiled > 0, "The compiled script does not carry this constant where expected, so it was left unchanged.");
        foreach (var (reference, payload) in blobs) file.Chunks[reference - 1].Payload = payload;
        byte[] firstBefore = before[..16], firstAfter = after[..16], secondAfter = after[16..];
        for (int i = 0; i + 1 < entries.Length; i += 2)
            if (entries[i].SequenceEqual(firstBefore) && entries[i + 1].SequenceEqual(before[16..])) { entries[i] = firstAfter; entries[i + 1] = secondAfter; }
        o.SetArray(0, [.. entries.SelectMany(e => e)], 16);
        o.SetArray(1, [.. o.Elements(1).SelectMany(e => e.SequenceEqual(firstBefore) ? firstAfter : e)], 16);
        file.Update(layer, o);
        return compiled;
    }

    /// <summary>
    /// A linear RGB transform: hue rotation (degrees), saturation (1 keeps it), brightness (1 keeps it) and a per-channel
    /// tint. Being linear, it maps a curve's tangents exactly as it maps its values.
    /// </summary>
    public static float[,] ColorMatrix(double hue, double saturation = 1, double brightness = 1, double tintR = 1, double tintG = 1, double tintB = 1)
    {
        double c = Math.Cos(hue * Math.PI / 180), s = Math.Sin(hue * Math.PI / 180), t = saturation;
        double[,] rotate =
        {
            { 0.213 + c * 0.787 - s * 0.213, 0.715 - c * 0.715 - s * 0.715, 0.072 - c * 0.072 + s * 0.928 },
            { 0.213 - c * 0.213 + s * 0.143, 0.715 + c * 0.285 + s * 0.140, 0.072 - c * 0.072 - s * 0.283 },
            { 0.213 - c * 0.213 - s * 0.787, 0.715 - c * 0.715 + s * 0.715, 0.072 + c * 0.928 + s * 0.072 },
        };
        double[,] saturate =
        {
            { 0.213 + 0.787 * t, 0.715 - 0.715 * t, 0.072 - 0.072 * t },
            { 0.213 - 0.213 * t, 0.715 + 0.285 * t, 0.072 - 0.072 * t },
            { 0.213 - 0.213 * t, 0.715 - 0.715 * t, 0.072 + 0.928 * t },
        };
        double[] tint = [tintR * brightness, tintG * brightness, tintB * brightness];
        var m = new float[3, 3];
        for (int r = 0; r < 3; r++) for (int col = 0; col < 3; col++)
            m[r, col] = (float)(tint[r] * Enumerable.Range(0, 3).Sum(k => saturate[r, k] * rotate[k, col]));
        return m;
    }
    public static float[] Apply(float[,] m, float[] rgb, bool clamp)
    {
        var result = (float[])rgb.Clone();
        for (int r = 0; r < 3; r++) { var v = m[r, 0] * rgb[0] + m[r, 1] * rgb[1] + m[r, 2] * rgb[2]; result[r] = clamp ? Math.Max(0, v) : v; }
        return result;
    }

    /// <summary>HSV of an RGB colour; the value is the brightest channel and may exceed 1.</summary>
    public static (double Hue, double Saturation, double Value) ToHsv(float[] c)
    {
        double r = c[0], g = c[1], b = c[2], max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, h = 0;
        if (d > 1e-9) h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, max <= 1e-9 ? 0 : d / max, max);
    }
    public static float[] FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (h % 360) switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return [(float)(r + m), (float)(g + m), (float)(b + m)];
    }

    /// <summary>Recolours curves (the first three components of every key, and their tangents) and colour constants with one shift.</summary>
    public static void Recolor(ParticleFile file, ColorShift shift, IEnumerable<int> curves, IEnumerable<(int Layer, int Index)> constants)
    {
        foreach (var curve in curves.Distinct())
        {
            if (file.ClassName(curve) != "CParticleNodeSamplerData_Curve" || file.Object((uint)curve) is not { } o) continue;
            float[] times = o.Floats(17), values = o.Floats(18), tangents = o.Floats(19);
            if (times.Length == 0 || values.Length % times.Length != 0 || values.Length / times.Length < 3) continue;
            int width = values.Length / times.Length;
            var values2 = (float[])values.Clone();
            for (int k = 0; k + width <= values.Length; k += width) shift.Apply(values[k..(k + 3)]).CopyTo(values2, k);
            SetCurve(file, curve, values2, tangents.Length == values.Length * 2 ? Map(tangents, width, shift.Linear, false) : null);
        }
        foreach (var group in constants.Distinct().GroupBy(c => c.Layer))
            foreach (var constant in ColorConstants(file, group.Key).Where(c => group.Any(g => g.Index == c.Index)))
                SetColorConstant(file, constant.Layer, constant.Index, [.. shift.Apply(constant.Value[..3]), .. constant.Value[3..]]);
    }
    private static float[] Map(float[] data, int width, float[,] m, bool clamp)
    {
        var result = (float[])data.Clone();
        for (int k = 0; k + width <= data.Length; k += width) Apply(m, data[k..(k + 3)], clamp).CopyTo(result, k);
        return result;
    }

    /// <summary>A JSON document's text with every quoted reference to one effect pointed at another (paths compare ignoring case).</summary>
    public static string Retarget(string json, string from, string to, out int count)
    {
        int found = 0;
        var result = Regex.Replace(json, "\"" + Regex.Escape(from.Replace('\\', '/')) + "\"", _ => { found++; return "\"" + to.Replace('\\', '/') + "\""; }, RegexOptions.IgnoreCase);
        count = found; return result;
    }

    private static ParticleObject Property(ParticleFile file, int property) =>
        file.ClassName(property) == "CLayerCompileCacheRendererProperty" && file.Object((uint)property) is { } o ? o : throw new InvalidDataException("Not a renderer setting.");
    private static float[] Lanes(byte[] a, byte[] b) => [.. a.Concat(b).Chunk(4).Select(c => BinaryPrimitives.ReadSingleLittleEndian(c))];
    private static int Replace(byte[] data, byte[] before, byte[] after)
    {
        int count = 0;
        for (int i = data.AsSpan().IndexOf(before); i >= 0; )
        {
            after.CopyTo(data, i); count++;
            int next = data.AsSpan(i + before.Length).IndexOf(before); i = next < 0 ? -1 : i + before.Length + next;
        }
        return count;
    }
}
