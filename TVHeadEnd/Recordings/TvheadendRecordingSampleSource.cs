using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// Fetches the opening of a recording straight from TVHeadend.
    /// </summary>
    /// <remarks>
    /// Straight from TVHeadend, not through the endpoint this plugin serves clients from: that one
    /// exists to make FFmpeg's seeking work, and going through it here would only route the request
    /// back out through Jellyfin and in again.
    /// </remarks>
    public sealed class TvheadendRecordingSampleSource : IRecordingSampleSource
    {
        /// <summary>
        /// How much of a recording is fetched to analyse it. The program tables and a sample of
        /// every elementary stream sit at the very front; this is generous for that and still a
        /// tenth of a second over a local network.
        /// </summary>
        public const int SampleLength = 8 * 1024 * 1024;

        private readonly ITvheadendHttpEndpointSource _endpoints;
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvheadendRecordingSampleSource"/> class.
        /// </summary>
        /// <param name="endpoints">Where TVHeadend's HTTP interface is.</param>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        public TvheadendRecordingSampleSource(ITvheadendHttpEndpointSource endpoints, IHttpClientFactory httpClientFactory)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentNullException.ThrowIfNull(httpClientFactory);

            _endpoints = endpoints;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Copies at most <paramref name="limit"/> bytes, whatever the source offers.
        /// </summary>
        /// <param name="source">The stream to read.</param>
        /// <param name="destination">The stream to write.</param>
        /// <param name="limit">The most that may be copied.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of bytes copied.</returns>
        public static async Task<long> CopyAtMost(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long copied = 0;
                while (copied < limit)
                {
                    var wanted = (int)Math.Min(buffer.Length, limit - copied);
                    var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                }

                return copied;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The range request states how much is wanted, but a server is free to ignore it: a
        /// TVHeadend without range support, or a proxy in between, answers 200 with the whole
        /// recording. Copying that to the end would pull gigabytes across for an analysis that
        /// needs megabytes, so the limit is enforced while reading rather than assumed from the
        /// response. A short answer is equally fine -- whatever arrived is what gets analysed.
        /// </remarks>
        public async Task<RecordingSample> FetchAsync(string recordingId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(recordingId);

            var endpoint = await _endpoints.GetHttpEndpointAsync(cancellationToken).ConfigureAwait(false);
            var url = endpoint.CreateApiUrl("dvrfile/" + recordingId);

            // The file is this method's until it is handed over, and the caller's from then on.
            // Nothing partial is handed out and nothing partial is left behind.
            var path = RecordingSample.CreatePath();
            try
            {
                return new RecordingSample(path, await Fetch(endpoint, url, path, cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                RecordingSample.Discard(path);
                throw;
            }
        }

        private async Task<long> Fetch(TvheadendHttpEndpoint endpoint, string url, string destination, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, SampleLength - 1);
            foreach (var header in endpoint.CreateHeaders())
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // A server that cannot satisfy the range says so rather than failing outright. It is
            // reported as the HTTP failure it is, so that the analysis treats it as an operational
            // problem with this recording rather than as a defect.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new HttpRequestException(
                    $"TVHeadend rejected the range request for the analysis sample of {url}.",
                    null,
                    response.StatusCode);
            }

            response.EnsureSuccessStatusCode();

            var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (target.ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    return await CopyAtMost(body, target, SampleLength, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
