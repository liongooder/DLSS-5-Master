using System.Diagnostics;
using System.Text;

namespace DLSS5Master.Core;

/// <summary>Minimal Portable Executable reader: bitness, imported DLL names, version text, byte markers.</summary>
public static class Pe
{
    /// <summary>32, 64, or 0 when the file is not a readable PE image.</summary>
    public static int GetBitness(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return 0;
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > fs.Length) return 0;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return 0;
            return br.ReadUInt16() switch
            {
                0x014C => 32,
                0x8664 or 0xAA64 => 64,
                _ => 0
            };
        }
        catch { return 0; }
    }

    /// <summary>Lower-case names of the DLLs this image imports (normal and delay-load).</summary>
    public static HashSet<string> GetImports(string file)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return result;
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            fs.Position = peOffset + 4;
            br.ReadUInt16(); // machine
            int sectionCount = br.ReadUInt16();
            fs.Position += 12;
            int optionalSize = br.ReadUInt16();
            fs.Position += 2;
            long optionalStart = fs.Position;
            ushort magic = br.ReadUInt16();
            bool pe32Plus = magic == 0x20B;
            long dataDirStart = optionalStart + (pe32Plus ? 112 : 96);

            var sections = new List<(uint va, uint vsize, uint raw, uint rawSize)>();
            fs.Position = optionalStart + optionalSize;
            for (int i = 0; i < sectionCount; i++)
            {
                fs.Position += 8;
                uint vsize = br.ReadUInt32(), va = br.ReadUInt32(), rawSize = br.ReadUInt32(), raw = br.ReadUInt32();
                fs.Position += 16;
                sections.Add((va, vsize, raw, rawSize));
            }
            long ToOffset(uint rva)
            {
                foreach (var s in sections)
                    if (rva >= s.va && rva < s.va + Math.Max(s.vsize, s.rawSize)) return rva - s.va + s.raw;
                return -1;
            }
            string ReadCString(long pos)
            {
                fs.Position = pos;
                var sb = new StringBuilder();
                for (int b; (b = fs.ReadByte()) > 0 && sb.Length < 260;) sb.Append((char)b);
                return sb.ToString();
            }
            // Directory 1 = imports (20-byte descriptors, name at +12); 13 = delay imports (32-byte, name at +4).
            foreach (var (index, stride, nameField) in new[] { (1, 20, 12), (13, 32, 4) })
            {
                fs.Position = dataDirStart + index * 8;
                uint rva = br.ReadUInt32();
                if (rva == 0) continue;
                long pos = ToOffset(rva);
                if (pos < 0) continue;
                for (int i = 0; i < 4096; i++, pos += stride)
                {
                    fs.Position = pos + nameField;
                    uint nameRva = br.ReadUInt32();
                    if (nameRva == 0) break;
                    long namePos = ToOffset(nameRva);
                    if (namePos < 0) break;
                    result.Add(ReadCString(namePos).ToLowerInvariant());
                }
            }
        }
        catch { }
        return result;
    }

    public static string? GetFileVersion(string file)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            if (info.FileMajorPart + info.FileMinorPart + info.FileBuildPart + info.FilePrivatePart > 0)
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion.Trim();
        }
        catch { return null; }
    }

    /// <summary>True when the version resource (product, description, company...) mentions <paramref name="text"/>.</summary>
    public static bool VersionMentions(string file, string text)
    {
        try
        {
            var i = FileVersionInfo.GetVersionInfo(file);
            return new[] { i.ProductName, i.FileDescription, i.CompanyName, i.InternalName, i.OriginalFilename, i.LegalCopyright }
                .Any(v => v?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        }
        catch { return false; }
    }

    /// <summary>Which of the ASCII markers appear anywhere in the file. Streams the file in chunks.</summary>
    public static HashSet<string> FindMarkers(string file, IEnumerable<string> markers, long maxBytes = 512L * 1024 * 1024)
    {
        var wanted = markers.Select(m => (m, bytes: Encoding.ASCII.GetBytes(m))).ToList();
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (wanted.Count == 0) return found;
        int overlap = wanted.Max(w => w.bytes.Length);
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var buffer = new byte[(1 << 20) + overlap];
            int carry = 0;
            long total = 0;
            while (total < maxBytes && found.Count < wanted.Count)
            {
                int read = fs.Read(buffer, carry, buffer.Length - carry);
                if (read <= 0) break;
                total += read;
                var span = new ReadOnlySpan<byte>(buffer, 0, carry + read);
                foreach (var (m, bytes) in wanted)
                    if (!found.Contains(m) && span.IndexOf(bytes) >= 0) found.Add(m);
                carry = Math.Min(overlap, span.Length);
                span[^carry..].CopyTo(buffer);
            }
        }
        catch { }
        return found;
    }

    public static bool Contains(string file, string marker) => FindMarkers(file, new[] { marker }).Count > 0;
}
