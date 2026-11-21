using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SententiamConverte.Core.Ai.LocalMt;

/// <summary>Ein Tensor als zusammenhängende Gleitkommazahlen (Zeilen zuerst).</summary>
internal sealed record Tensor(int[] Shape, float[] Data)
{
    public int Rows => Shape[0];
    public int Columns => Shape.Length > 1 ? Shape[^1] : 1;
}

/// <summary>
/// Liest Gewichte im safetensors-Format: 8 Byte Länge, JSON-Kopf mit Typ, Form und Lage jedes Tensors, dann die
/// Rohdaten. Das Format enthält nichts Ausführbares (anders als die älteren Pickle-Dateien von PyTorch).
/// </summary>
internal static class SafeTensors
{
    public static Dictionary<string, Tensor> Read(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[8];
        file.ReadExactly(prefix);
        var headerLength = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (headerLength is 0 or > 100_000_000 || (long)headerLength > file.Length - 8)
            throw new InvalidDataException("Keine gültige safetensors-Datei: " + Path.GetFileName(path));
        var header = new byte[headerLength];
        file.ReadExactly(header);
        var dataStart = 8 + (long)headerLength;

        var entries = new List<(string Name, string Type, int[] Shape, long Begin, long End)>();
        foreach (var (name, node) in JsonNode.Parse(header)!.AsObject())
        {
            if (name == "__metadata__" || node is not JsonObject o) continue;
            var shape = o["shape"]!.AsArray().Select(n => (int)n!).ToArray();
            var offsets = o["data_offsets"]!.AsArray();
            entries.Add((name, (string)o["dtype"]!, shape, (long)offsets[0]!, (long)offsets[1]!));
        }

        var result = new Dictionary<string, Tensor>();
        foreach (var e in entries.OrderBy(e => e.Begin))                       // der Reihe nach lesen
        {
            var count = e.Shape.Aggregate(1L, (a, b) => a * b);
            var size = e.Type switch
            {
                "F32" => 4,
                "F16" or "BF16" => 2,
                _ => throw new InvalidDataException($"Tensor „{e.Name}“: Typ {e.Type} wird nicht unterstützt."),
            };
            if (e.End - e.Begin != count * size || dataStart + e.End > file.Length)
                throw new InvalidDataException($"Tensor „{e.Name}“: Größe passt nicht zum Kopf.");
            var data = new float[count];
            file.Position = dataStart + e.Begin;
            if (size == 4 && BitConverter.IsLittleEndian)
                file.ReadExactly(MemoryMarshal.AsBytes(data.AsSpan()));
            else
            {
                var raw = new byte[e.End - e.Begin];
                file.ReadExactly(raw);
                for (var i = 0; i < count; i++)
                    data[i] = e.Type switch
                    {
                        "F32" => BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4)),
                        "F16" => (float)BinaryPrimitives.ReadHalfLittleEndian(raw.AsSpan(i * 2)),
                        _ => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2)) << 16),
                    };
            }
            result[e.Name] = new Tensor(e.Shape, data);
        }
        return result;
    }
}
