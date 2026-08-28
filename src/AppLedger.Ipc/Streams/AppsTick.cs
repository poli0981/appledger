using System.Text.Json;
using AppLedger.Core.Metrics;

namespace AppLedger.Ipc.Streams;

/// <summary>One app's row in an <see cref="AppsTick"/> frame.</summary>
/// <remarks>
/// A struct the reader fills in place, into a list the UI keeps between ticks. The grid updates existing row
/// view-models by <c>appId</c> rather than rebinding (docs/22_WPFUI_SYNTAX.md §Gotchas), so nothing here
/// should be allocating a row object per app per second.
/// </remarks>
public struct AppRow
{
    /// <summary>The app.</summary>
    public string AppId { get; set; }

    /// <summary>Live process instances.</summary>
    public int Procs { get; set; }

    /// <summary>CPU percentage, 0-100.</summary>
    public double CpuPct { get; set; }

    /// <summary>Private working set, bytes.</summary>
    public long WsPrivate { get; set; }

    /// <summary>GPU percentage, 0-100.</summary>
    public double GpuPct { get; set; }

    /// <summary>Real device read bytes this second.</summary>
    public long DiskRead { get; set; }

    /// <summary>Real device write bytes this second.</summary>
    public long DiskWrite { get; set; }

    /// <summary>Network payload bytes received this second.</summary>
    public long NetIn { get; set; }

    /// <summary>Network payload bytes sent this second.</summary>
    public long NetOut { get; set; }
}

/// <summary>
/// The 1 Hz table of every running app, in the compact column form of docs/07_IPC.md §Streams.
/// </summary>
/// <remarks>
/// <b>Not a DTO, deliberately.</b> The rows hold four different cell types, so a serializable shape means
/// either <c>object[]</c> — boxing every number of every app, every second — or a per-column array set that
/// no longer matches the documented wire format. A hand-written writer and reader over
/// <see cref="Utf8JsonWriter"/> and <see cref="Utf8JsonReader"/> have typed overloads for all four and box
/// nothing.
/// </remarks>
public static class AppsTick
{
    /// <summary>
    /// The v1 column set, as one pre-encoded JSON array.
    /// </summary>
    /// <remarks>
    /// Written with <c>WriteRawValue</c> rather than nine string writes. It is a compile-time constant we
    /// authored, so skipping validation is safe here in a way it would never be for peer data.
    /// </remarks>
    public static ReadOnlySpan<byte> ColumnsJson =>
        """["appId","procs","cpu","wsPrivate","gpu","diskR","diskW","netIn","netOut"]"""u8;

    /// <summary>The column names, in order, for a reader building an index map.</summary>
    public static IReadOnlyList<string> Columns { get; } =
        ["appId", "procs", "cpu", "wsPrivate", "gpu", "diskR", "diskW", "netIn", "netOut"];

    /// <summary>Writes one tick's payload.</summary>
    public static void Write(Utf8JsonWriter writer, long tsUtc, IReadOnlyList<AppSample> samples)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(samples);

        writer.WriteStartObject();
        writer.WriteNumber("ts"u8, tsUtc);

        writer.WritePropertyName("cols"u8);
        writer.WriteRawValue(ColumnsJson, skipInputValidation: true);

        writer.WriteStartArray("rows"u8);
        foreach (var sample in samples)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(sample.AppId.Value);
            writer.WriteNumberValue(sample.Procs);
            writer.WriteNumberValue(Round1(sample.CpuPct));
            writer.WriteNumberValue(sample.WsPrivate);
            writer.WriteNumberValue(Round1(sample.GpuPct));
            writer.WriteNumberValue(sample.DiskRead);
            writer.WriteNumberValue(sample.DiskWrite);
            writer.WriteNumberValue(sample.NetIn);
            writer.WriteNumberValue(sample.NetOut);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads one tick's payload into <paramref name="rows"/>, which is cleared first and reused across ticks.
    /// </summary>
    /// <remarks>
    /// The column header is parsed rather than assumed. The server always emits the same nine in the same
    /// order — which is what makes writing them as a constant legal — but a later server may <i>append</i> a
    /// tenth, an additive change docs/07 §Versioning allows, and an older client has to keep working.
    /// </remarks>
    /// <returns>The tick's timestamp, or -1 when the payload was not a well-formed tick.</returns>
    /// <remarks>
    /// A tick with no <c>rows</c> is an empty second, not an error — that is a legitimate state at logon,
    /// before anything has been sampled. Rows <i>without</i> a column header cannot be interpreted at all,
    /// and that is a rejection.
    /// <para>
    /// <b>Nothing in here may throw.</b> These bytes come from the peer, and both
    /// <see cref="Utf8JsonReader.Read"/> and the <c>TryGet*</c> family throw on input that does not fit —
    /// <c>TryGetInt64</c> raises <see cref="InvalidOperationException"/> when the token is a string, which
    /// the name does not suggest. A parser over peer data has to be total, or the first malformed frame any
    /// same-user process cares to send takes the connection loop with it.
    /// </para>
    /// </remarks>
    public static long Read(ReadOnlySpan<byte> payload, List<AppRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        try
        {
            return ReadCore(payload, rows);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            rows.Clear();
            return -1;
        }
    }

    private static long ReadCore(ReadOnlySpan<byte> payload, List<AppRow> rows)
    {
        rows.Clear();

        var reader = new Utf8JsonReader(payload, IpcJson.ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return -1;
        }

        long ts = -1;
        Span<int> map = stackalloc int[ColumnCount];
        map.Fill(-1);
        var haveColumns = false;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("ts"u8))
            {
                if (!reader.Read() || !reader.TryGetInt64(out ts))
                {
                    return -1;
                }
            }
            else if (reader.ValueTextEquals("cols"u8))
            {
                if (!TryReadColumns(ref reader, map))
                {
                    return -1;
                }

                haveColumns = true;
            }
            else if (reader.ValueTextEquals("rows"u8))
            {
                if (!haveColumns || !TryReadRows(ref reader, map, rows))
                {
                    return -1;
                }
            }
            else
            {
                if (!reader.Read())
                {
                    return -1;
                }

                reader.Skip();
            }
        }

        return ts;
    }

    private const int ColumnCount = 9;

    private static bool TryReadColumns(ref Utf8JsonReader reader, scoped Span<int> map)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        var index = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.String)
        {
            // A column this build does not know keeps its slot in the row but no destination, which is what
            // lets an older client read a newer server's appended column without misreading the ones it has.
            if (reader.ValueTextEquals("appId"u8)) { map[0] = index; }
            else if (reader.ValueTextEquals("procs"u8)) { map[1] = index; }
            else if (reader.ValueTextEquals("cpu"u8)) { map[2] = index; }
            else if (reader.ValueTextEquals("wsPrivate"u8)) { map[3] = index; }
            else if (reader.ValueTextEquals("gpu"u8)) { map[4] = index; }
            else if (reader.ValueTextEquals("diskR"u8)) { map[5] = index; }
            else if (reader.ValueTextEquals("diskW"u8)) { map[6] = index; }
            else if (reader.ValueTextEquals("netIn"u8)) { map[7] = index; }
            else if (reader.ValueTextEquals("netOut"u8)) { map[8] = index; }

            index++;
        }

        return reader.TokenType == JsonTokenType.EndArray;
    }

    private static bool TryReadRows(ref Utf8JsonReader reader, scoped ReadOnlySpan<int> map, List<AppRow> rows)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            var row = default(AppRow);
            var cell = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (cell == map[0]) { row.AppId = reader.GetString() ?? string.Empty; }
                else if (cell == map[1]) { row.Procs = reader.GetInt32(); }
                else if (cell == map[2]) { row.CpuPct = reader.GetDouble(); }
                else if (cell == map[3]) { row.WsPrivate = reader.GetInt64(); }
                else if (cell == map[4]) { row.GpuPct = reader.GetDouble(); }
                else if (cell == map[5]) { row.DiskRead = reader.GetInt64(); }
                else if (cell == map[6]) { row.DiskWrite = reader.GetInt64(); }
                else if (cell == map[7]) { row.NetIn = reader.GetInt64(); }
                else if (cell == map[8]) { row.NetOut = reader.GetInt64(); }
                else { reader.Skip(); }

                cell++;
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                return false;
            }

            rows.Add(row);
        }

        return reader.TokenType == JsonTokenType.EndArray;
    }

    /// <summary>
    /// The same rounding <c>Rollup</c> applies before storing.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. An unrounded double serializes to up to seventeen characters, so a hundred apps cost
    /// about 2.4 KB a second in digits nobody reads — and, more importantly, the live number the grid shows
    /// would differ in the last place from the stored number the History page shows for the same minute. A
    /// user who notices that is right to distrust both.
    /// </remarks>
    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
