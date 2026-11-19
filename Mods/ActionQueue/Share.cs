using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ActionQueue;

/// <summary>
/// Turns a queue into a short piece of text ("AQ1:...") that can be pasted into a chat, and back.
/// A code holds only names, ids and numbers. Reading one never runs anything: it is unpacked with size
/// limits, checked field by field, and whatever does not exist in the reader's game is simply skipped
/// by the queue later, like any step that cannot be started.
/// </summary>
public static class Share
{
    public const string Prefix = "AQ1:";
    public const int MaxCodeLength = 20_000;
    public const int MaxSteps = 200;
    const int MaxText = 120;
    const int MaxUnpackedBytes = 400_000;

    // Short property names keep the code small.
    sealed class Packed
    {
        public string N { get; set; } = "";
        public bool R { get; set; }
        public List<PackedStep> S { get; set; } = new();
    }

    sealed class PackedStep
    {
        public string E { get; set; } = "";   // skill
        public string T { get; set; } = "";   // target
        public string X { get; set; } = "";   // extra (combat area)
        public string N { get; set; } = "";   // shown name
        public int U { get; set; }            // until
        public double A { get; set; }         // amount
        public string J { get; set; } = "";   // goal subject (item id / skill name)
        public string K { get; set; } = "";   // its shown name
    }

    public static string Export(string name, IEnumerable<Step> steps, bool repeat)
    {
        var packed = new Packed
        {
            N = name, R = repeat,
            S = steps.Select(s => new PackedStep { E = s.Entity, T = s.TargetId, X = s.Extra, N = s.Name, U = (int)s.Until, A = s.Amount, J = s.Subject, K = s.SubjectName }).ToList(),
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(packed);
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(json, 0, json.Length);
        return Prefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryImport(string? code, out string name, out List<Step> steps, out bool repeat, out string problem)
    {
        name = "";
        steps = new List<Step>();
        repeat = false;
        problem = "";
        code = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (code.Length == 0) { problem = "There is no code to read. Paste one into the box first."; return false; }
        if (code.Length > MaxCodeLength) { problem = "That text is too long to be a share code."; return false; }
        if (!code.StartsWith(Prefix, StringComparison.Ordinal)) { problem = $"That is not an Action Queue share code (it must start with {Prefix})."; return false; }

        Packed? packed;
        try
        {
            string body = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=');
            using var source = new MemoryStream(Convert.FromBase64String(body));
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = deflate.Read(chunk, 0, chunk.Length)) > 0)
            {
                unpacked.Write(chunk, 0, read);
                if (unpacked.Length > MaxUnpackedBytes) { problem = "That code unpacks to far more than a queue can be."; return false; }
            }
            packed = JsonSerializer.Deserialize<Packed>(unpacked.ToArray());
        }
        catch (Exception)
        {
            problem = "That code is damaged (part of it may be missing). Copy it again in full.";
            return false;
        }
        if (packed?.S == null || packed.S.Count == 0) { problem = "That code holds no steps."; return false; }
        if (packed.S.Count > MaxSteps) { problem = $"That code holds more than {MaxSteps} steps."; return false; }

        foreach (PackedStep s in packed.S)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.E) || string.IsNullOrWhiteSpace(s.T) || s.U < 0 || s.U > (int)Until.Made
                || double.IsNaN(s.A) || double.IsInfinity(s.A) || s.A < 0 || s.A > 1_000_000_000)
            {
                problem = "That code holds a step that makes no sense, so none of it was taken.";
                steps.Clear();
                return false;
            }
            steps.Add(new Step
            {
                Entity = Clip(s.E), TargetId = Clip(s.T), Extra = Clip(s.X), Name = Clip(s.N),
                Until = (Until)s.U, Amount = s.A, Subject = Clip(s.J), SubjectName = Clip(s.K),
            });
        }
        name = CleanName(packed.N);
        repeat = packed.R;
        return true;
    }

    static string Clip(string? text)
    {
        text = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= MaxText ? text : text[..MaxText];
    }

    /// <summary>A name a queue may be saved under: no line breaks, not too long.</summary>
    public static string CleanName(string? name)
    {
        name = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length <= 40 ? name : name[..40].Trim();
    }
}
