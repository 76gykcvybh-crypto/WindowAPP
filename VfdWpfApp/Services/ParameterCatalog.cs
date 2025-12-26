using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VfdWpfApp.Models;

namespace VfdWpfApp.Services;

public sealed class ParameterCatalog
{
    public IReadOnlyList<ParameterDefinition> Parameters { get; }

    private ParameterCatalog(IReadOnlyList<ParameterDefinition> parameters)
    {
        Parameters = parameters;
    }

    public static ParameterCatalog LoadFromJson(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("Parameter JSON not found.", jsonPath);

        using var fs = File.OpenRead(jsonPath);
        using var doc = JsonDocument.Parse(fs);

        var list = new List<ParameterDefinition>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string addrHex = el.GetProperty("address_hex").GetString() ?? "0x0000";
            int addr = el.GetProperty("address").GetInt32();
            string name = el.GetProperty("name").GetString() ?? "";
            string access = el.GetProperty("access").GetString() ?? "RW";
            string? unit = el.TryGetProperty("unit", out var unitEl) && unitEl.ValueKind != JsonValueKind.Null ? unitEl.GetString() : null;
            string desc = el.GetProperty("description").GetString() ?? "";
            string? note = el.TryGetProperty("note", out var noteEl) && noteEl.ValueKind != JsonValueKind.Null ? noteEl.GetString() : null;

            var bits = new List<BitFieldDefinition>();
            if (el.TryGetProperty("bitfields", out var bitEl) && bitEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in bitEl.EnumerateArray())
                {
                    int bit = b.GetProperty("bit").GetInt32();
                    string meaning = b.GetProperty("meaning").GetString() ?? "";
                    bits.Add(new BitFieldDefinition(bit, meaning));
                }
            }

            list.Add(new ParameterDefinition(addrHex, addr, name, access, unit, desc, note, bits));
        }

        // Sort by numeric address
        list.Sort((a,b) => a.Address.CompareTo(b.Address));
        return new ParameterCatalog(list);
    }

    public ParameterDefinition? FindByAddress(ushort address)
        => Parameters.FirstOrDefault(p => p.Address == address);
}
