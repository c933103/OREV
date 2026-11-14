using System.Text;
using System.Text.Json;

namespace ExportViewer;

// The index contains source byte ranges, never the contents of JSON string values.
public sealed class JNode
{
    public long Start { get; internal set; }
    public long Length { get; internal set; }
    public char Kind { get; internal set; }
    public Dictionary<string, JNode>? Fields { get; internal set; }
    public List<JNode>? Values { get; internal set; }
    public JNode? this[string key] => Fields?.GetValueOrDefault(key);
    public IEnumerable<JNode> Children => Fields?.Values ?? Values?.AsEnumerable() ?? Enumerable.Empty<JNode>();
}

public sealed class JsonIndex
{
    readonly FileStream input;
    readonly byte[] buffer = new byte[1024 * 1024];
    int at, count;
    long basis;
    long nodes;
    readonly CancellationToken cancel;
    readonly IProgress<int>? progress;
    int lastPercent = -1;
    public long Position => basis + at;
    public JsonIndex(FileStream file, CancellationToken token, IProgress<int>? progress = null)
    { input = file; cancel = token; this.progress = progress; }
    bool Fill()
    {
        cancel.ThrowIfCancellationRequested();
        basis += count; at = 0; count = input.Read(buffer);
        int pct = (int)(100 * Math.Min(basis, input.Length) / Math.Max(1, input.Length));
        if (pct != lastPercent) { progress?.Report(pct); lastPercent = pct; }
        return count > 0;
    }
    int Peek() => at < count || Fill() ? buffer[at] : -1;
    int Take() { int c = Peek(); if (c >= 0) at++; return c; }
    void White() { while (Peek() is 32 or 9 or 10 or 13) at++; }
    FormatException Error(string text) => new($"{text} (byte {Position:N0})");
    void Expect(int c) { if (Take() != c) throw Error($"Expected '{(char)c}'"); }
    public JNode Parse()
    {
        // Optional UTF-8 BOM.
        if (Peek() == 0xEF) { Take(); Expect(0xBB); Expect(0xBF); }
        White(); var root = Value(0); White();
        if (Peek() != -1) throw Error("Unexpected data after JSON");
        progress?.Report(100); return root;
    }
    JNode Value(int depth)
    {
        cancel.ThrowIfCancellationRequested();
        if (depth > 128) throw Error("JSON nesting exceeds 128 levels");
        if (++nodes > 8_000_000) throw Error("Too many JSON nodes for this viewer");
        White(); int c = Peek();
        var n = new JNode { Start = Position, Kind = (char)c };
        if (c == '{')
        {
            Take(); n.Fields = new(StringComparer.Ordinal); White();
            if (Peek() != '}') while (true)
            {
                White(); if (Peek() != '"') throw Error("Object key must be a string");
                var keyNode = String();
                if (keyNode.Length > 65536) throw Error("Object key exceeds 64 KiB");
                string key = ReadKey(keyNode);
                White(); Expect(':'); var child = Value(depth + 1);
                if (!n.Fields.TryAdd(key, child)) throw Error("Duplicate JSON object key");
                White(); if (Peek() != ',') break; Take();
            }
            Expect('}');
        }
        else if (c == '[')
        {
            Take(); n.Values = new(); White();
            if (Peek() != ']') while (true)
            {
                n.Values.Add(Value(depth + 1)); White();
                if (Peek() != ',') break; Take();
            }
            Expect(']');
        }
        else if (c == '"') return String();
        else if (c is 't' or 'f' or 'n')
        {
            string literal = c == 't' ? "true" : c == 'f' ? "false" : "null";
            foreach (char ch in literal) Expect(ch);
        }
        else
        {
            if (c == '-') Take();
            if (Peek() == '0') Take();
            else { if (Peek() < '1' || Peek() > '9') throw Error("Invalid JSON value"); Digits(); }
            if (Peek() == '.') { Take(); RequireDigit(); Digits(); }
            if (Peek() is 'e' or 'E') { Take(); if (Peek() is '+' or '-') Take(); RequireDigit(); Digits(); }
        }
        n.Length = Position - n.Start; return n;
    }
    void RequireDigit() { if (Peek() < '0' || Peek() > '9') throw Error("Invalid number"); }
    void Digits() { while (Peek() >= '0' && Peek() <= '9') Take(); }
    JNode String()
    {
        long start = Position; Expect('"');
        while (true)
        {
            if (Peek() < 0) throw Error("Unterminated string");
            // SIMD-assisted scan: payloads, including giant base64 strings, are never allocated.
            var span = buffer.AsSpan(at, count - at);
            int i = span.IndexOfAnyInRange((byte)0, (byte)31);
            int q = span.IndexOfAny((byte)'"', (byte)'\\');
            int stop = i < 0 ? q : q < 0 ? i : Math.Min(i, q);
            if (stop < 0) { at = count; continue; }
            at += stop; int c = Take();
            if (c == '"') break;
            if (c < 32) throw Error("Unescaped control character in string");
            int e = Take();
            if (e == 'u') for (int j = 0; j < 4; j++)
            {
                int h = Take(); if (!(h >= '0' && h <= '9' || h >= 'a' && h <= 'f' || h >= 'A' && h <= 'F')) throw Error("Invalid Unicode escape");
            }
            else if (e is not ('"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')) throw Error("Invalid string escape");
        }
        return new JNode { Start = start, Length = Position - start, Kind = '"' };
    }
    string ReadKey(JNode n)
    {
        byte[] bytes = new byte[(int)n.Length];
        int total = 0;
        while (total < bytes.Length)
        {
            int read = RandomAccess.Read(input.SafeFileHandle, bytes.AsSpan(total), n.Start + total);
            if (read == 0) throw new EndOfStreamException(); total += read;
        }
        return JsonSerializer.Deserialize<string>(bytes) ?? "";
    }
}

// Independent position over a shared, read-locked file; safe for concurrent previews/exports.
public sealed class SliceStream : Stream
{
    readonly FileStream source; readonly long start, size; long position;
    public SliceStream(FileStream source, long start, long size) { this.source = source; this.start = start; this.size = size; }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => size;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));
    public override int Read(Span<byte> b)
    {
        int wanted = (int)Math.Min(b.Length, size - position); if (wanted == 0) return 0;
        int n = RandomAccess.Read(source.SafeFileHandle, b[..wanted], start + position);
        if (n == 0) throw new EndOfStreamException("Source file became shorter"); position += n; return n;
    }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}

public sealed class JsonStringReader : TextReader
{
    readonly StreamReader raw;
    public JsonStringReader(Stream stream) => raw = new(stream, new UTF8Encoding(false, true), false, 8192);
    public override int Read()
    {
        int c = raw.Read(); if (c != '\\') return c;
        return raw.Read() switch
        {
            '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
            'u' => Unicode(), _ => throw new FormatException("Invalid JSON escape")
        };
    }
    int Unicode()
    {
        int value = 0;
        for (int i = 0; i < 4; i++) { int c = raw.Read(); value = value * 16 + (c <= '9' ? c - '0' : (c | 32) - 'a' + 10); }
        return value;
    }
    protected override void Dispose(bool disposing) { if (disposing) raw.Dispose(); base.Dispose(disposing); }
}
