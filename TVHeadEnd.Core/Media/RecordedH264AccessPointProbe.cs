using System;
using System.IO;

namespace TVHeadEnd.Core.Media;

/// <summary>
/// Reads a sample of a recording for what its H.264 access points open on.
/// </summary>
/// <remarks>
/// <para>
/// The recording half of the same question the live conditioner asks while a channel is playing,
/// and it is answered by the same classifier. What differs is only how the packets arrive: a
/// recording is a file that can be walked from the front, so the whole sample is read rather than
/// the first few access points, and the answer is correspondingly better.
/// </para>
/// <para>
/// It says something about the sample and nothing more. A recording whose opening minutes carry
/// no IDR may well carry one later, past the end of what was fetched -- which is why the only
/// verdict that leads anywhere is the one drawn from access points that were read whole.
/// </para>
/// <para>
/// The stream type gate is the whole reason this is safe to run over unknown material. MPEG-2
/// video signals a slice with the very byte pattern that means IDR in H.264 -- measured at 205
/// coincidental matches in eight megabytes of RTL -- so a broadcast that is not H.264 is never
/// scanned at all rather than being scanned and found wanting.
/// </para>
/// </remarks>
public static class RecordedH264AccessPointProbe
{
    /// <summary>
    /// The PMT stream type of H.264 video. The only one this question applies to.
    /// </summary>
    private const byte H264StreamType = 0x1B;

    /// <summary>
    /// Reads a recording sample for what a decoder joining at its access points would find.
    /// </summary>
    /// <param name="sample">The opening of the recording, from its first packet.</param>
    /// <param name="programMap">The recording's program map, as already read from the same sample.</param>
    /// <returns>
    /// What the sample shows. <see cref="H264EntryPointEvidence.Insufficient"/> whenever the
    /// question does not arise: no program map, no video, video that is not H.264, a file that is
    /// not a transport stream, or a sample too short to hold three whole access points.
    /// </returns>
    public static H264EntryPointEvidence Examine(Stream sample, ProgramMapTable? programMap)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (programMap is null || programMap.VideoStreamType != H264StreamType)
        {
            return H264EntryPointEvidence.Insufficient;
        }

        var videoPid = programMap.VideoPid;
        if (videoPid < 0)
        {
            return H264EntryPointEvidence.Insufficient;
        }

        var classifier = new H264AccessPointClassifier();
        var packet = new byte[TransportStreamPacket.Length];
        long position = 0;

        while (TransportStreamPacket.ReadFrom(sample, packet))
        {
            if (packet[0] != TransportStreamPacket.SyncByte)
            {
                // No longer aligned to a packet boundary, so nothing after this point can be
                // read as one. Whatever was learned before it still stands.
                break;
            }

            if (TransportStreamPacket.ReadPid(packet) == videoPid)
            {
                Read(classifier, packet, position);
            }

            position += TransportStreamPacket.Length;
        }

        // The picture still being followed when the sample ran out is a picture half seen. It is
        // left uncounted: a verdict of "no IDR here" drawn partly from bytes that were never
        // fetched is exactly the guess this is meant to avoid.
        return classifier.Evidence;
    }

    /// <summary>
    /// Offers one packet of the video stream to the classifier, as the live path does.
    /// </summary>
    private static void Read(H264AccessPointClassifier classifier, ReadOnlySpan<byte> packet, long position)
    {
        if (TransportStreamPacket.SignalsRandomAccess(packet))
        {
            classifier.EndPicture();
            classifier.BeginPicture(position);
        }
        else if (!classifier.IsReadingPicture)
        {
            return;
        }
        else if (TransportStreamPacket.StartsPayloadUnit(packet))
        {
            classifier.NotePayloadUnitStart();
        }

        classifier.Read(TransportStreamPacket.ReadPayload(packet));
    }
}
