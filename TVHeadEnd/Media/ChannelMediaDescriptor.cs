using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Media
{
    /// <summary>
    /// What a channel was observed to be. Facts only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here expresses a preference, a client capability or a delivery decision, and
    /// there is deliberately no "needs transcoding" flag: what to do about a fact is playback
    /// policy, and mixing the two is what produced a re-encode verdict for MPEG-2 that happened
    /// to be right for the wrong reason.
    /// </para>
    /// <para>
    /// The stream order is the order FFprobe reported and is never rearranged. Jellyfin's
    /// <c>EncodingHelper.GetMapArgs</c> addresses the stream it wants FFmpeg to copy by its
    /// position in this list, so reordering makes <c>-map</c> point at a different track than
    /// the one described in the manifest.
    /// </para>
    /// </remarks>
    public sealed record ChannelMediaDescriptor
    {
        /// <summary>
        /// The layout this record is written in. Raised whenever the analysis changes in a way
        /// that makes older results untrustworthy, which invalidates every stored descriptor.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// Gets the TVHeadend channel identifier.
        /// </summary>
        public required string ChannelId { get; init; }

        /// <summary>
        /// Gets which delivery form this describes, as a role name, or <see langword="null"/>
        /// for the native broadcast.
        /// </summary>
        /// <remarks>
        /// A string rather than the playback enumeration, so that observing what a compatibility
        /// stream actually turned out to be does not make this factual record depend on playback
        /// policy.
        /// </remarks>
        public string? VariantRole { get; init; }

        /// <summary>
        /// Gets the key this descriptor is stored under.
        /// </summary>
        public string StorageKey => Key(ChannelId, VariantRole);

        /// <summary>
        /// Gets the layout this record was written in.
        /// </summary>
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        /// <summary>
        /// Gets the TVHeadend stream profile the analysis read through. A descriptor says
        /// nothing about a channel delivered through a different profile, because the profile
        /// decides the container and may re-code the elementary streams.
        /// </summary>
        public string? NativeProfile { get; init; }

        /// <summary>
        /// Gets the PMT fingerprint, as "streamtype:pid" in PMT order.
        /// </summary>
        /// <remarks>
        /// Proof that the broadcast still announces the same elementary streams in the same
        /// order, which is what makes a stored descriptor safe to reuse. It proves nothing about
        /// the random access properties of the video: two broadcasts with identical PMTs can
        /// differ there, and one of them will not start on a device decoder.
        /// </remarks>
        public string? ProgramSignature { get; init; }

        /// <summary>
        /// Gets the container, as reported by the analysis.
        /// </summary>
        public string? Container { get; init; }

        /// <summary>
        /// Gets the elementary streams, in the order the analysis reported them.
        /// </summary>
        public IReadOnlyList<MediaStream> Streams { get; init; } = [];

        /// <summary>
        /// Gets the PMT stream type of the video, or zero when none was established.
        /// </summary>
        public byte VideoStreamType { get; init; }

        /// <summary>
        /// Gets how the video offers a decoder a place to start.
        /// </summary>
        public H264RandomAccessKind RandomAccess { get; init; } = H264RandomAccessKind.Unknown;

        /// <summary>
        /// Gets a value indicating whether the source arrived as an MPEG transport stream.
        /// </summary>
        public bool IsTransportStream { get; init; }

        /// <summary>
        /// Gets the overall bitrate, if the analysis established one.
        /// </summary>
        public int? Bitrate { get; init; }

        /// <summary>
        /// Gets the transport stream timestamp form.
        /// </summary>
        public TransportStreamTimestamp? Timestamp { get; init; }

        /// <summary>
        /// Gets the video type.
        /// </summary>
        public VideoType? VideoType { get; init; }

        /// <summary>
        /// Gets the stereoscopic format.
        /// </summary>
        public Video3DFormat? Video3DFormat { get; init; }

        /// <summary>
        /// Gets when the analysis was made.
        /// </summary>
        public DateTime ObservedUtc { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the first video stream, or <see langword="null"/> when there is none.
        /// </summary>
        public MediaStream? Video => Streams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);

        /// <summary>
        /// Gets the codec of the first video stream.
        /// </summary>
        public string? VideoCodec => Video?.Codec;

        /// <summary>
        /// Gets a value indicating whether the video is interlaced. A fact about the source, not
        /// an instruction: what to do about it belongs to whoever encodes it.
        /// </summary>
        public bool IsInterlaced => Video?.IsInterlaced ?? false;

        /// <summary>
        /// Gets a value indicating whether the video is MPEG-2, which many clients cannot decode.
        /// </summary>
        public bool IsMpeg2Video
            => VideoStreamType == 0x02
                || string.Equals(VideoCodec, "mpeg2video", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets a value indicating whether the descriptor names at least one video stream, the
        /// minimum Jellyfin needs: it dereferences the video stream while preparing playback and
        /// throws before any fallback could take effect.
        /// </summary>
        public bool IsUsable => Streams.Count > 0 && Video is not null;

        /// <summary>
        /// Reports whether this descriptor may still be trusted.
        /// </summary>
        /// <param name="nativeProfile">The TVHeadend stream profile now configured for native playback.</param>
        /// <returns>Whether the stored analysis still applies.</returns>
        public bool IsCurrentFor(string? nativeProfile)
            => SchemaVersion == CurrentSchemaVersion
                && IsUsable
                && string.Equals(NativeProfile ?? string.Empty, nativeProfile ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reports whether the broadcast now being received is still the one this describes.
        /// </summary>
        /// <param name="programSignature">The PMT fingerprint of the stream being received.</param>
        /// <returns>Whether the stored streams still describe it.</returns>
        public bool MatchesProgram(string? programSignature)
            => ProgramSignature is not null
                && programSignature is not null
                && string.Equals(ProgramSignature, programSignature, StringComparison.Ordinal);

        /// <summary>
        /// Copies the facts onto a media source.
        /// </summary>
        /// <param name="target">The media source to fill in.</param>
        public void ApplyTo(MediaSourceInfo target)
        {
            ArgumentNullException.ThrowIfNull(target);

            // Verbatim, in analysis order. Every audio and subtitle track is preserved; which
            // one a viewer gets is the client's choice to make, not this plugin's.
            target.MediaStreams = [.. Streams];
            target.Container = Container;
            target.Bitrate = Bitrate;
            target.Timestamp = Timestamp;
            target.VideoType = VideoType;
            target.Video3DFormat = Video3DFormat;

            // The full result is in hand, including real stream indices. Without this Jellyfin
            // replaces it with its own cached live TV view -- first video, first audio, indices
            // unknown -- which is exactly the description that makes its "-map" arguments land
            // on the wrong tracks.
            target.SupportsProbing = false;
        }

        /// <summary>
        /// Builds the key a descriptor is stored under.
        /// </summary>
        /// <param name="channelId">The TVHeadend channel identifier.</param>
        /// <param name="variantRole">The role name, or <see langword="null"/> for the broadcast.</param>
        /// <returns>The key.</returns>
        public static string Key(string channelId, string? variantRole)
            => string.IsNullOrEmpty(variantRole) ? channelId : channelId + "|" + variantRole;

        /// <summary>
        /// Summarises the streams for a log line.
        /// </summary>
        /// <returns>A short description.</returns>
        public string Summarise()
            => string.Join(
                ", ",
                Streams.Select(stream => $"{stream.Index}:{stream.Type.ToString().ToLowerInvariant()}/{stream.Codec}"));
    }
}
