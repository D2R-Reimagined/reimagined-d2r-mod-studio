using System.Buffers.Binary;
using System.Text;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>One serialized object of a baked effect: its class (an index into the string pool) and its field payload.</summary>
public sealed class ParticleChunk(int classIndex, byte[] payload, byte marker = 0x20)
{
    public int ClassIndex { get; } = classIndex;
    public byte[] Payload { get; set; } = payload;
    /// <summary>The byte between a chunk's length and its class; 0x20 in every file seen, kept as read.</summary>
    public byte Marker { get; } = marker;
}

/// <summary>
/// A D2R .particles file: a PopcornFX 2 baked effect. The layout (worked out from the game's own files, after Retera's
/// PKBlaster) is a header, a table of chunk counts per class, the chunks, then a pool of length-prefixed strings.
/// Every chunk is one object: a u16 field count, then (u16 field id, value) pairs where fields equal to their default
/// are left out. Values carry no type tag, so <see cref="Schema"/> gives the size of each class's fields. Chunks refer
/// to each other by 1-based chunk index and to names, paths and class names by string-pool index.
/// The effect's simulation is compiled PopcornFX bytecode (CCompilerBlobCache) and is kept as opaque bytes.
/// </summary>
public sealed class ParticleFile
{
    public const uint Magic = 0xca000b11;
    private static readonly Encoding Bytes = Encoding.Latin1;
    /// <summary>The PopcornFX version bytes (major, minor, patch, 1) and build the effect was baked with.</summary>
    public uint Version { get; private init; }
    public uint Build { get; private init; }
    /// <summary>The u32 after the string offset; zero in every vanilla file, kept as read.</summary>
    public uint Reserved { get; private init; }
    /// <summary>The class table: string-pool index of each class, in file order. Counts are recomputed on write.</summary>
    public List<int> Classes { get; } = [];
    public List<string> Strings { get; } = [];
    public List<ParticleChunk> Chunks { get; } = [];
    public string VersionText => $"{Version & 0xff}.{(Version >> 8) & 0xff}.{(Version >> 16) & 0xff} (build {Build})";

    /// <summary>An empty effect with the version and build of D2R's own files.</summary>
    public ParticleFile(uint version = 0x01050902, uint build = 8000) { Version = version; Build = build; }

    public static ParticleFile Read(byte[] b)
    {
        Require(b.Length >= 28, "Too short for a baked PopcornFX effect.");
        uint U(int offset) { Require(offset >= 0 && offset + 4 <= b.Length, "Truncated baked effect."); return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4)); }
        Require(U(0) == Magic, U(0) == 0xc9000b11 ? "This is an older PopcornFX (Warcraft III 1.32) layout, not D2R's." : "Not a D2R baked PopcornFX effect (.particles).");
        int chunkCount = (int)U(12), classCount = (int)U(16), stringOffset = (int)U(20);
        Require(classCount is >= 0 and <= 4096 && chunkCount >= 0 && chunkCount <= b.Length / 9 && stringOffset >= 28 + classCount * 8 && stringOffset <= b.Length - 4, "Invalid baked effect header.");
        var file = new ParticleFile { Version = U(4), Build = U(8), Reserved = U(24) };
        int expected = 0;
        for (int i = 0; i < classCount; i++) { file.Classes.Add((int)U(28 + i * 8)); expected += (int)U(32 + i * 8); }
        Require(expected == chunkCount, "Class table does not add up to the chunk count.");
        int stringCount = (int)U(stringOffset), p = stringOffset + 4;
        Require(stringCount >= 0 && stringCount <= b.Length - p, "Invalid string pool size.");
        for (int i = 0; i < stringCount; i++)
        {
            Require(p < b.Length && p + 1 + b[p] <= b.Length, "Truncated string pool.");
            file.Strings.Add(Bytes.GetString(b, p + 1, b[p])); p += 1 + b[p];
        }
        Require(p == b.Length, "Unexpected data after the string pool.");
        p = 28 + classCount * 8;
        while (p < stringOffset)
        {
            int length = (int)U(p);
            Require(length >= 5 && p + 4 + length <= stringOffset, $"Chunk {file.Chunks.Count + 1} overruns the chunk area.");
            int type = (int)U(p + 5);
            Require(type >= 0 && type < stringCount, $"Chunk {file.Chunks.Count + 1} names class {type}, outside the string pool.");
            file.Chunks.Add(new ParticleChunk(type, b.AsSpan(p + 9, length - 5).ToArray(), b[p + 4]));
            p += 4 + length;
        }
        Require(p == stringOffset && file.Chunks.Count == chunkCount, "Chunk area does not match the header.");
        foreach (var c in file.Classes) Require(c >= 0 && c < stringCount, "Class table names a string outside the pool.");
        return file;
    }

    public byte[] Write()
    {
        foreach (var s in Strings) Require(Bytes.GetByteCount(s) <= 255, $"String longer than 255 bytes cannot be stored: {s}");
        foreach (var c in Chunks) Require(Classes.Contains(c.ClassIndex), $"Chunk class {c.ClassIndex} is not in the class table.");
        int chunkBytes = Chunks.Sum(c => 9 + c.Payload.Length), stringOffset = 28 + Classes.Count * 8 + chunkBytes;
        var b = new byte[stringOffset + 4 + Strings.Sum(s => 1 + Bytes.GetByteCount(s))];
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset, 4), value);
        Put(0, Magic); Put(4, Version); Put(8, Build); Put(12, (uint)Chunks.Count); Put(16, (uint)Classes.Count); Put(20, (uint)stringOffset); Put(24, Reserved);
        for (int i = 0; i < Classes.Count; i++) { Put(28 + i * 8, (uint)Classes[i]); Put(32 + i * 8, (uint)Chunks.Count(c => c.ClassIndex == Classes[i])); }
        int p = 28 + Classes.Count * 8;
        foreach (var c in Chunks) { Put(p, (uint)(5 + c.Payload.Length)); b[p + 4] = c.Marker; Put(p + 5, (uint)c.ClassIndex); c.Payload.CopyTo(b, p + 9); p += 9 + c.Payload.Length; }
        Put(p, (uint)Strings.Count); p += 4;
        foreach (var s in Strings) { var bytes = Bytes.GetBytes(s); b[p] = (byte)bytes.Length; bytes.CopyTo(b, p + 1); p += 1 + bytes.Length; }
        return b;
    }

    /// <summary>Writes an object back into the chunk a 1-based reference names.</summary>
    public void Update(int reference, ParticleObject value) { Require(reference >= 1 && reference <= Chunks.Count, "No such chunk."); Chunks[reference - 1].Payload = value.Encode(); }
    /// <summary>The string-pool index of a string, adding it when the pool does not have it yet.</summary>
    public uint Intern(string value)
    {
        Require(Bytes.GetByteCount(value) <= 255, "Names and paths are limited to 255 characters.");
        int index = Strings.IndexOf(value); if (index >= 0) return (uint)index;
        Strings.Add(value); return (uint)(Strings.Count - 1);
    }

    public string ClassName(ParticleChunk chunk) => Strings[chunk.ClassIndex];
    public string ClassName(int chunk) => ClassName(Chunks[chunk - 1]);
    /// <summary>A string-pool entry, or null for an index outside the pool.</summary>
    public string? String(uint index) => index < (uint)Strings.Count ? Strings[(int)index] : null;
    /// <summary>The chunk a 1-based reference names, or null when it is out of range.</summary>
    public ParticleChunk? Chunk(uint reference) => reference >= 1 && reference <= (uint)Chunks.Count ? Chunks[(int)reference - 1] : null;
    /// <summary>The fields of the chunk a 1-based reference names; null when out of range or of a class the schema lacks.</summary>
    public ParticleObject? Object(uint reference) => Chunk(reference) is { } chunk && Schema.TryGetValue(ClassName(chunk), out var fields) ? ParticleObject.Decode(chunk.Payload, fields, (int)reference) : null;
    public IEnumerable<(int Reference, ParticleObject Fields)> Objects(string className)
    {
        for (int i = 0; i < Chunks.Count; i++)
            if (ClassName(Chunks[i]) == className && Object((uint)i + 1) is { } o) yield return (i + 1, o);
    }

    /// <summary>
    /// Field sizes per class: a number is a fixed-size value, "aN" a u32 count followed by that many N-byte elements.
    /// Checked against every payload of every vanilla effect (see ParticleFileTests). Samplers' 8-byte array elements
    /// are localized strings: a language code ("eng") and a string-pool index.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> Schema = Parse(new Dictionary<string, string>
    {
        ["CParticleEffect"] = "1:a4 3:4 10:1 11:4",
        ["CParticleAttributeList"] = "0:a4 1:a4",
        ["CParticleAttributeDeclaration"] = "0:4 1:4 2:a8 3:a8 4:4 5:1 6:1 7:1 14:16 21:16 25:4 32:16 44:16 50:16 51:1",
        ["CParticleAttributeSamplerDeclaration"] = "0:4 1:4 3:a8 5:4 6:4",
        ["CLayerGraphCompileCache"] = "0:4 2:a4 3:a4 4:a4 6:16",
        ["CLayerGraphCompileCache_EntrySlot"] = "0:4 1:4",
        ["CLayerGraphCompileCache_EventSlot"] = "0:4 1:4 2:a4",
        ["CLayerGraphCompileCache_LayerSlot"] = "0:4 1:a4 2:a4",
        ["CLayerCompileCache"] = "0:a16 1:a16 2:a4 3:a4 4:a4 5:a4 6:a4 7:a4 8:a4 9:a4 10:a4 11:4 12:4 13:1 14:1 15:1 16:4 17:4 18:4 19:16 20:4 21:4 22:4 23:4 24:4 25:4 26:4 27:4 28:4 29:4 30:4 31:4",
        ["CLayerCompileCacheField"] = "0:4 1:4 2:4 3:4 4:4 5:4",
        ["CLayerCompileCacheSampler"] = "0:4 1:4 2:4 3:4 4:4",
        ["CLayerCompileCacheAttrib"] = "0:4 1:a8 2:4 3:4 4:4 5:4 6:16 7:16 8:16 9:16 10:16",
        ["CLayerCompileCacheAttribSampler"] = "0:4 1:a8 2:4 4:4 5:4",
        ["CLayerCompileCacheEvent"] = "0:4 1:4 2:a4 3:4",
        ["CLayerCompileCacheEventPayload"] = "0:4 1:4 2:4 3:4 4:4 5:4",
        ["CLayerCompileCacheRenderer"] = "0:4 1:a4 2:a4 3:4 4:4",
        ["CLayerCompileCacheRendererProperty"] = "0:4 1:4 2:16 3:4",
        ["CLayerCompileCacheRendererParticleInput"] = "0:4 1:4 2:4 3:4",
        ["CCompilerBlobCache"] = "0:4 1:4 2:a4 3:a4 4:a4 5:a4 6:a4",
        ["CParticleNodePinIn"] = "0:4 1:4 2:1 5:4 7:16 8:16 9:4 10:4",
        ["CParticleNodePinOut"] = "0:4 1:4 4:4 5:4",
        ["CParticleNodeSamplerData_Curve"] = "0:a8 1:a8 3:4 7:8 9:4 10:4 11:4 12:16 13:16 14:4 16:1 17:a4 18:a4 19:a4",
        ["CParticleNodeSamplerData_Shape"] = "0:a8 1:a8 3:4 6:a4 7:8 9:4 10:4 11:4 12:12 13:4 14:4 16:4 17:1 18:12 19:4 20:12 27:1 28:1 29:12 30:12",
        ["CParticleNodeSamplerData_Turbulence"] = "0:a8 1:a8 3:4 7:8 9:4 10:4 11:4 19:4 20:4 21:4 22:4 23:4 24:4 25:4 26:4 27:4 28:4 29:4 30:1",
        ["CParticleNodeSamplerData_EventStream"] = "0:a8 1:a8 3:4 7:8 9:4 10:4 11:a4",
        ["CParticleNodeSamplerData_Text"] = "0:a8 1:a8 3:4 6:a4 7:8 9:4 10:4 11:4",
    });
    private static Dictionary<string, IReadOnlyDictionary<int, int>> Parse(Dictionary<string, string> source) =>
        source.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<int, int>)p.Value.Split(' ').Select(f => f.Split(':')).ToDictionary(f => int.Parse(f[0]), f => f[1][0] == 'a' ? -int.Parse(f[1][1..]) : int.Parse(f[1])));
}

/// <summary>The fields of one chunk. A field's bytes are either its fixed-size value or, for arrays, the elements after the count.</summary>
public sealed class ParticleObject
{
    private readonly Dictionary<int, (byte[] Data, int Element)> fields = [];
    public int Reference { get; private init; }
    public IEnumerable<int> Fields => fields.Keys;

    public static ParticleObject Decode(byte[] d, IReadOnlyDictionary<int, int> schema, int reference = 0)
    {
        Require(d.Length >= 2, "Truncated chunk.");
        var o = new ParticleObject { Reference = reference };
        int count = BinaryPrimitives.ReadUInt16LittleEndian(d), p = 2, last = -1;
        for (int i = 0; i < count; i++)
        {
            Require(p + 2 <= d.Length, "Truncated chunk field.");
            int id = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)); p += 2;
            Require(id > last, "Chunk fields are out of order."); Require(schema.TryGetValue(id, out var size), $"Unknown field {id}."); last = id;
            if (size > 0) { Require(p + size <= d.Length, "Truncated chunk value."); o.fields[id] = (d.AsSpan(p, size).ToArray(), 0); p += size; continue; }
            Require(p + 4 <= d.Length, "Truncated chunk array.");
            long n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); Require(p + 4 + n * -size <= d.Length, "Chunk array overruns its chunk.");
            o.fields[id] = (d.AsSpan(p + 4, (int)n * -size).ToArray(), -size); p += 4 + (int)n * -size;
        }
        Require(p == d.Length, "Chunk has bytes after its last field.");
        return o;
    }

    /// <summary>A chunk payload from (field id, value) pairs; an array value includes its u32 count.</summary>
    public static byte[] Encode(params (int Id, byte[] Value)[] values)
    {
        var b = new byte[2 + values.Sum(v => 2 + v.Value.Length)]; int p = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)values.Length);
        foreach (var (id, value) in values.OrderBy(v => v.Id)) { BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), (ushort)id); value.CopyTo(b, p + 2); p += 2 + value.Length; }
        return b;
    }

    /// <summary>This object's payload: fields in id order, arrays with their u32 count. Decoding then encoding is byte-identical.</summary>
    public byte[] Encode()
    {
        var ordered = fields.OrderBy(f => f.Key).ToList();
        var b = new byte[2 + ordered.Sum(f => 2 + (f.Value.Element > 0 ? 4 : 0) + f.Value.Data.Length)]; int p = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)ordered.Count);
        foreach (var (id, (data, element)) in ordered)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), (ushort)id); p += 2;
            if (element > 0) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), (uint)(data.Length / element)); p += 4; }
            data.CopyTo(b, p); p += data.Length;
        }
        return b;
    }
    /// <summary>Sets a fixed-size field. Fields are left out when they hold their default, so pass null to reset one.</summary>
    public void Set(int id, byte[]? value) { if (value == null) fields.Remove(id); else fields[id] = (value, 0); }
    public void SetU32(int id, uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); Set(id, b); }
    public void SetArray(int id, byte[] elements, int elementSize) { Require(elementSize > 0 && elements.Length % elementSize == 0, "Array data does not fit its element size."); fields[id] = (elements, elementSize); }
    public void SetFloats(int id, float[] values) => SetArray(id, [.. values.SelectMany(v => BitConverter.GetBytes(BitConverter.SingleToUInt32Bits(v)))], 4);
    /// <summary>The elements of an array field of any element size.</summary>
    public byte[][] Elements(int id) => fields.TryGetValue(id, out var f) && f.Element > 0 ? [.. f.Data.Chunk(f.Element)] : [];

    public bool Has(int id) => fields.ContainsKey(id);
    public byte[]? Raw(int id) => fields.TryGetValue(id, out var f) ? f.Data : null;
    public uint U32(int id, uint fallback = 0) => Raw(id) is { Length: >= 4 } b ? BinaryPrimitives.ReadUInt32LittleEndian(b) : Raw(id) is { Length: 1 } one ? one[0] : fallback;
    public float F32(int id, float fallback = 0) => Raw(id) is { Length: >= 4 } b ? BinaryPrimitives.ReadSingleLittleEndian(b) : fallback;
    /// <summary>A 16-byte value as four floats (zeros when the field was left at its default).</summary>
    public float[] Vector(int id) => Raw(id) is { Length: 16 } b ? [.. Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 4)))] : new float[4];
    public int[] Ints(int id) => Raw(id) is { Length: 16 } b ? [.. Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(i * 4)))] : new int[4];
    /// <summary>A u32 array (chunk references, string indices or floats' bits).</summary>
    public uint[] Array(int id) => fields.TryGetValue(id, out var f) && f.Element == 4 ? [.. Enumerable.Range(0, f.Data.Length / 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(f.Data.AsSpan(i * 4)))] : [];
    public float[] Floats(int id) => [.. Array(id).Select(BitConverter.UInt32BitsToSingle)];
    /// <summary>The string-pool indices of a localized-string array (8-byte elements: language code, string index).</summary>
    public uint[] Localized(int id) => fields.TryGetValue(id, out var f) && f.Element == 8 ? [.. Enumerable.Range(0, f.Data.Length / 8).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(f.Data.AsSpan(i * 8 + 4)))] : [];
}
