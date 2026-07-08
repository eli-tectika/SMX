using System.Text;

namespace Smx.Functions.Reg.Ingestion;

// Minimal RFC-4180-ish CSV reader (quoted fields, doubled quotes, embedded commas/newlines). Sufficient for
// the official regulator datasets we ingest; avoids taking a CSV NuGet dependency for a handful of columns.
public static class CsvReader
{
    public static IReadOnlyList<string[]> Parse(byte[] content)
        => Parse(Encoding.UTF8.GetString(content).TrimStart('﻿'));

    public static IReadOnlyList<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row = new List<string>(); break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows.Where(r => r.Length > 1 || (r.Length == 1 && r[0].Trim().Length > 0)).ToList();
    }
}
