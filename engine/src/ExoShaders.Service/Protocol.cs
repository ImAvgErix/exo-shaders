// Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller (https://github.com/BlueHeisenberg/SCSKiller).
// Copyright (C) 2026 The SCSKiller authors
// Copyright (C) 2026 Exo Shaders contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version, with the additional permission in LICENSE-EXCEPTION.txt. It is distributed WITHOUT ANY WARRANTY; see LICENSE.
//
// Newline-delimited JSON over stdin/stdout (CONTRACT.md). stdout carries protocol lines only.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExoShaders.Service;

/// <summary>An error whose message is shown to the user as-is.</summary>
internal sealed class ServiceError(string message) : Exception(message);

internal sealed partial class Protocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    private readonly Stream _out;
    private readonly Lock _gate = new();
    private bool _closed;

    public Protocol()
    {
        _out = Console.OpenStandardOutput();
    }

    /// <summary>
    /// The engine's own sentences name SCSKiller, the app it is based on; the user
    /// sees this program, so prose says Exo Shaders. A name inside a path or a file
    /// name (after a slash, or before a dot or underscore) is left as it is.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\\/\w])SCSKiller(?![\\/.\w])", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ProductName();

    /// <summary>Most lines don't name it at all: those skip the decode, the regex and the encode.</summary>
    internal static byte[] Brand(byte[] json) =>
        json.AsSpan().IndexOf("SCSKiller"u8) < 0 ? json
            : System.Text.Encoding.UTF8.GetBytes(ProductName().Replace(System.Text.Encoding.UTF8.GetString(json), "Exo Shaders"));

    public void Send(object message, bool brand = true)
    {
        byte[] line;
        try
        {
            line = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            if (brand) line = Brand(line);
        }
        catch (Exception e)
        {
            line = JsonSerializer.SerializeToUtf8Bytes(new { @event = "notice", data = new { kind = "error", text = "couldn't serialize a message: " + e.Message } }, Json);
        }
        using (_gate.EnterScope())
        {
            if (_closed) return;
            try
            {
                _out.Write(line);
                _out.WriteByte((byte)'\n');
                _out.Flush();
            }
            catch (IOException)
            {
                _closed = true;   // the host went away; stdin closing ends the service
            }
        }
    }

    public void Event(string name, object? data) => Send(new { @event = name, data });

    /// <summary>An event whose data is JSON already serialized (and branded).</summary>
    public void EventRaw(string name, byte[] data)
    {
        byte[] head = JsonSerializer.SerializeToUtf8Bytes(new { @event = name }, Json);   // {"event":"name"}
        using (_gate.EnterScope())
        {
            if (_closed) return;
            try
            {
                _out.Write(head.AsSpan(0, head.Length - 1));
                _out.Write(",\"data\":"u8);
                _out.Write(data);
                _out.Write("}\n"u8);
                _out.Flush();
            }
            catch (IOException)
            {
                _closed = true;
            }
        }
    }

    public void Result(JsonElement? id, object? result, bool brand = true) => Send(new ResultLine(id, result ?? new { ok = true }), brand);

    public void Error(JsonElement? id, string error) => Send(new ErrorLine(id, error));

    // id is echoed as given (number or string); null for unparseable lines
    private sealed record ResultLine([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Id, object Result);

    private sealed record ErrorLine([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Id, string Error);

    public static async IAsyncEnumerable<string> Lines()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync();
            }
            catch (IOException)
            {
                yield break;
            }
            if (line is null) yield break;
            if (line.Trim().Length > 0) yield return line;
        }
    }
}

/// <summary>Typed access to a request's params.</summary>
internal sealed class Params(JsonElement? p)
{
    public JsonElement? Raw => p;

    private JsonElement? Get(string name) =>
        p is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public bool Has(string name) => p is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out _);

    public bool IsNull(string name) => p is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Null;

    public string? Str(string name) => Get(name) is { } v ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    public string ReqStr(string name) => Str(name) is { Length: > 0 } s ? s : throw new ServiceError($"missing parameter '{name}'");

    public bool? Bool(string name) => Get(name) is { } v ? v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ServiceError($"parameter '{name}' must be true or false"),
    } : null;

    public bool ReqBool(string name) => Bool(name) ?? throw new ServiceError($"missing parameter '{name}'");

    public int? Int(string name) => Get(name) is { } v ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : throw new ServiceError($"parameter '{name}' must be a whole number") : null;

    public int ReqInt(string name) => Int(name) ?? throw new ServiceError($"missing parameter '{name}'");

    public List<string>? Strs(string name) => Get(name) is { ValueKind: JsonValueKind.Array } v
        ? v.EnumerateArray().Select(e => e.GetString() ?? throw new ServiceError($"parameter '{name}' must be a list of strings")).ToList()
        : Get(name) is null ? null : throw new ServiceError($"parameter '{name}' must be a list");

    /// <summary>`id` or `ids`.</summary>
    public List<string> Ids() => Strs("ids") ?? (Str("id") is { } id ? [id] : throw new ServiceError("missing parameter 'id'"));

    public IEnumerable<string> Names() => p is { ValueKind: JsonValueKind.Object } o ? o.EnumerateObject().Select(x => x.Name) : [];

    public JsonElement? Element(string name) => p is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out JsonElement v) ? v : null;
}
