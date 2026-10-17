using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using ChargeGlobe.Providers.Abstractions;

namespace ChargeGlobe.Providers.BNetzA;

/// <summary>
/// HTTP client of the Bundesnetzagentur register (docs/endpunkte/bnetza.md). The CSV has no fixed address: its
/// link (with the date of the update in the file name) is read from the page of the "Ladesäulenkarte", then the file
/// is streamed. No key, no registration; the rate limit lives in <see cref="RateLimitingHandler"/>. An error status
/// becomes a <see cref="ProviderHttpException"/> with the <c>Retry-After</c> of the source, a page without the link an
/// <see cref="InvalidDataException"/> (unexpected format, docs/05).
/// </summary>
public sealed partial class BNetzAClient(HttpClient http, TimeProvider? timeProvider = null)
{
    public static readonly Uri BaseAddress = new("https://www.bundesnetzagentur.de/");

    /// <summary>Page with the download links of the register (CSV and XLSX).</summary>
    public const string MapPage = "DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Ladesaeulenkarte/start.html";

    /// <summary>Bytes of the head of the CSV the probe reads: preamble and header row.</summary>
    public const int HeadBytes = 4096;

    /// <summary>The link of the register page to the CSV, e.g. <c>…/Ladesaeulenregister_BNetzA_2026-09-01.csv</c>.</summary>
    public async Task<Uri> FindCsvAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MapPage);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureSuccess(response, "register page");
        var page = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = CsvLink().Match(page);
        return match.Success
            ? new Uri(match.Value.Replace("&amp;", "&", StringComparison.Ordinal))
            : throw new InvalidDataException("The register page of the Bundesnetzagentur has no link to the CSV.");
    }

    /// <summary>The CSV as a stream (the response stays open until the stream is disposed).</summary>
    public async Task<Stream> OpenCsvAsync(Uri csv, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync(csv, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            EnsureSuccess(response, "CSV");
            return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>The first <see cref="HeadBytes"/> of the CSV (range request), for the probe.</summary>
    public async Task<string> ReadHeadAsync(Uri csv, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, csv);
        request.Headers.Range = new RangeHeaderValue(0, HeadBytes - 1);
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureSuccess(response, "CSV");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, HeadBytes));
    }

    private void EnsureSuccess(HttpResponseMessage response, string name)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderHttpException(
                $"Bundesnetzagentur {name} returned {(int)response.StatusCode}.", response.StatusCode, RetryAfter(response));
        }
    }

    /// <summary>The announced wait of a <c>Retry-After</c> header (seconds or date), never negative.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - (timeProvider ?? TimeProvider.System).GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null,
        };

    [GeneratedRegex(@"https://data\.bundesnetzagentur\.de/[^""'\s<>]*Ladesaeulenregister[^""'\s<>]*\.csv", RegexOptions.IgnoreCase)]
    private static partial Regex CsvLink();

    /// <summary>Keeps the response alive while its body is read.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
