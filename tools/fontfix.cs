// Normalizes TTF name tables so all weights of a family share one family name
// (name ID 1) with per-file subfamily (ID 2). Static instances exported from
// variable fonts often carry per-weight family names ("Chivo SemiBold"), which
// breaks weight-based font matching in Avalonia/Skia. Run once:
//   dotnet run tools/fontfix.cs -- <fonts-dir>
// Usage is idempotent. Prints before/after name records and OS/2 weight class.

using System.Buffers.Binary;
using System.Text;

var dir = args.Length > 0 ? args[0] : "Klippy/Assets/Fonts";

// file name prefix -> (family, subfamily, expected usWeightClass)
var plan = new Dictionary<string, (string Family, string Sub, ushort Weight)>(StringComparer.OrdinalIgnoreCase)
{
    ["Chivo-Regular"] = ("Chivo", "Regular", 400),
    ["Chivo-Medium"] = ("Chivo", "Medium", 500),
    ["Chivo-SemiBold"] = ("Chivo", "SemiBold", 600),
    ["Chivo-Bold"] = ("Chivo", "Bold", 700),
    ["ChivoMono-Regular"] = ("Chivo Mono", "Regular", 400),
    ["ChivoMono-Medium"] = ("Chivo Mono", "Medium", 500),
};

foreach (var path in Directory.GetFiles(dir, "*.ttf").OrderBy(p => p))
{
    var stem = Path.GetFileNameWithoutExtension(path);
    if (!plan.TryGetValue(stem, out var target))
    {
        Console.WriteLine($"{stem}: no plan entry, skipped");
        continue;
    }

    var data = File.ReadAllBytes(path);
    var font = new Font(data);

    Console.WriteLine($"== {stem} ==");
    Console.WriteLine($"  before: {font.DescribeNames()}  usWeightClass={font.WeightClass()}");

    font.ReplaceNameTable(target.Family, target.Sub);
    if (font.WeightClass() != target.Weight)
        font.SetWeightClass(target.Weight);
    var patched = font.Rebuild();
    File.WriteAllBytes(path, patched);

    var check = new Font(patched);
    Console.WriteLine($"  after:  {check.DescribeNames()}  usWeightClass={check.WeightClass()}");
}

sealed class Font
{
    private readonly byte[] _data;
    private readonly List<(string Tag, byte[] Table)> _tables = new();

    public Font(byte[] data)
    {
        _data = data;
        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        for (int i = 0; i < numTables; i++)
        {
            var rec = data.AsSpan(12 + 16 * i);
            string tag = Encoding.ASCII.GetString(rec[..4]);
            uint off = BinaryPrimitives.ReadUInt32BigEndian(rec[8..]);
            uint len = BinaryPrimitives.ReadUInt32BigEndian(rec[12..]);
            _tables.Add((tag, data.AsSpan((int)off, (int)len).ToArray()));
        }
    }

    private byte[] Table(string tag) => _tables.First(t => t.Tag == tag).Table;

    public ushort WeightClass() => BinaryPrimitives.ReadUInt16BigEndian(Table("OS/2").AsSpan(4));

    public void SetWeightClass(ushort weight) =>
        BinaryPrimitives.WriteUInt16BigEndian(Table("OS/2").AsSpan(4), weight);

    public string DescribeNames()
    {
        var names = ReadNames();
        string Get(int id) => names.TryGetValue(id, out var v) ? v : "-";
        return $"id1='{Get(1)}' id2='{Get(2)}' id16='{Get(16)}' id17='{Get(17)}'";
    }

    private Dictionary<int, string> ReadNames()
    {
        var name = Table("name");
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2));
        ushort storage = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
        var result = new Dictionary<int, string>();
        for (int i = 0; i < count; i++)
        {
            var rec = name.AsSpan(6 + 12 * i);
            ushort platform = BinaryPrimitives.ReadUInt16BigEndian(rec);
            ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(rec[6..]);
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(rec[8..]);
            ushort off = BinaryPrimitives.ReadUInt16BigEndian(rec[10..]);
            if (platform == 3 && !result.ContainsKey(nameId))
                result[nameId] = Encoding.BigEndianUnicode.GetString(name, storage + off, len);
        }
        return result;
    }

    public void ReplaceNameTable(string family, string subfamily)
    {
        var old = ReadNames();
        old.TryGetValue(0, out var copyright);
        old.TryGetValue(5, out var version);
        old.TryGetValue(13, out var license);
        old.TryGetValue(14, out var licenseUrl);

        var entries = new List<(ushort Id, string Value)>();
        if (copyright is not null) entries.Add((0, copyright));
        entries.Add((1, family));
        entries.Add((2, subfamily));
        entries.Add((3, $"{family} {subfamily}"));
        entries.Add((4, $"{family} {subfamily}"));
        if (version is not null) entries.Add((5, version));
        entries.Add((6, $"{family.Replace(" ", "")}-{subfamily}"));
        if (license is not null) entries.Add((13, license));
        if (licenseUrl is not null) entries.Add((14, licenseUrl));

        var storage = new MemoryStream();
        var records = new MemoryStream();
        Span<byte> rec = stackalloc byte[12];
        foreach (var (id, value) in entries.OrderBy(e => e.Id))
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(value);
            BinaryPrimitives.WriteUInt16BigEndian(rec, 3);           // platform: Windows
            BinaryPrimitives.WriteUInt16BigEndian(rec[2..], 1);      // encoding: Unicode BMP
            BinaryPrimitives.WriteUInt16BigEndian(rec[4..], 0x409);  // language: en-US
            BinaryPrimitives.WriteUInt16BigEndian(rec[6..], id);
            BinaryPrimitives.WriteUInt16BigEndian(rec[8..], (ushort)bytes.Length);
            BinaryPrimitives.WriteUInt16BigEndian(rec[10..], (ushort)storage.Position);
            records.Write(rec);
            storage.Write(bytes);
        }

        var table = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header, 0);                              // format
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)entries.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)(6 + records.Length));
        table.Write(header);
        records.WriteTo(table);
        storage.WriteTo(table);

        int idx = _tables.FindIndex(t => t.Tag == "name");
        _tables[idx] = ("name", table.ToArray());
    }

    public byte[] Rebuild()
    {
        // Zero head.checkSumAdjustment before computing checksums, per spec.
        var head = Table("head");
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);

        int numTables = _tables.Count;
        int entrySelector = (int)Math.Log2(numTables);
        int searchRange = (1 << entrySelector) * 16;

        var ordered = _tables.OrderBy(t => t.Tag, StringComparer.Ordinal).ToList();
        var output = new MemoryStream();
        Span<byte> sfnt = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(sfnt, BinaryPrimitives.ReadUInt32BigEndian(_data)); // keep sfnt version
        BinaryPrimitives.WriteUInt16BigEndian(sfnt[4..], (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(sfnt[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(sfnt[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(sfnt[10..], (ushort)(numTables * 16 - searchRange));
        output.Write(sfnt);

        uint offset = (uint)(12 + numTables * 16);
        var dir = new byte[numTables * 16];
        var body = new MemoryStream();
        for (int i = 0; i < numTables; i++)
        {
            var (tag, table) = ordered[i];
            var rec = dir.AsSpan(i * 16);
            Encoding.ASCII.GetBytes(tag).CopyTo(rec);
            BinaryPrimitives.WriteUInt32BigEndian(rec[4..], Checksum(table));
            BinaryPrimitives.WriteUInt32BigEndian(rec[8..], offset);
            BinaryPrimitives.WriteUInt32BigEndian(rec[12..], (uint)table.Length);
            body.Write(table);
            int pad = (4 - table.Length % 4) % 4;
            for (int p = 0; p < pad; p++) body.WriteByte(0);
            offset += (uint)(table.Length + pad);
        }
        output.Write(dir);
        body.WriteTo(output);

        var whole = output.ToArray();
        uint total = Checksum(whole);
        uint adjustment = unchecked(0xB1B0AFBA - total);
        // Locate head table in output to write the adjustment.
        for (int i = 0; i < numTables; i++)
        {
            if (ordered[i].Tag == "head")
            {
                uint headOff = BinaryPrimitives.ReadUInt32BigEndian(whole.AsSpan(12 + i * 16 + 8));
                BinaryPrimitives.WriteUInt32BigEndian(whole.AsSpan((int)headOff + 8), adjustment);
            }
        }
        return whole;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        int full = data.Length / 4 * 4;
        for (int i = 0; i < full; i += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i)));
        if (full < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data.AsSpan(full).CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }
        return sum;
    }
}
