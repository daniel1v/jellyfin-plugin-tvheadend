using System;

namespace TVHeadEnd.Streaming;

/// <summary>
/// Reads the pictures at signalled access points of an H.264 stream and says what they open on.
/// </summary>
/// <remarks>
/// <para>
/// The one place in this plugin that decides what an H.264 access point is worth. A live stream
/// being conditioned and a recording being sampled arrive here by different routes and hand over
/// different bytes, but they ask the same question and get it answered by the same reading, so
/// there is no second opinion to keep in step.
/// </para>
/// <para>
/// It knows nothing about program tables, PIDs, clients or policy. The caller decides which
/// packets belong to the video stream and whether that stream is H.264 at all -- the scan below
/// is only meaningful for stream type 0x1B, because in MPEG-2 the very start code that means IDR
/// here means an ordinary slice.
/// </para>
/// <para>
/// One picture is followed at a time. A further access point arriving while one is still being
/// read ends that reading, which is correct: a new picture has begun, so the one before it is as
/// fully seen as it is going to be.
/// </para>
/// </remarks>
public sealed class H264AccessPointClassifier
{
    /// <summary>
    /// How many access points decide how a stream opens.
    /// </summary>
    public const int MaximumExaminedAccessPoints = 3;

    /// <summary>
    /// How many payload unit starts a picture may span before it is taken as read. Only a bound:
    /// the syntax normally ends the access unit first.
    /// </summary>
    private const int MaximumPayloadUnitsPerPicture = 1;

    private readonly H264AccessUnitScanner _accessUnitScanner = new();

    private long _pendingPosition = -1;
    private int _pendingUnits;
    private int _openingAccessPoints;
    private int _examinedAccessPoints;
    private bool _idrObserved;

    /// <summary>
    /// Gets a value indicating whether this stream opens on an IDR, as far as its first few
    /// access points go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded, and the bound is the whole meaning: the first few signalled access points are
    /// read, and if none of them opens on an IDR the answer is no. That is a statement about this
    /// open, not about the broadcaster -- a channel that mixes IDR and open-GOP access points, as
    /// ZDF does, may well offer one a moment later, and the points that arrive afterwards are
    /// still classified and published for readers that join later.
    /// </para>
    /// <para>
    /// Once answered it stays answered. A reader that has already been told how the stream opens
    /// has acted on it, and an answer that changed underneath would describe a decision nobody
    /// made. What comes later is carried by <see cref="Evidence"/> instead, which is free to
    /// improve.
    /// </para>
    /// <para>
    /// Null until the question has been settled, and left null for a stream that never reaches a
    /// signalled access point at all.
    /// </para>
    /// </remarks>
    public bool? HasIdrEntryPoint { get; private set; }

    /// <summary>
    /// Gets what every access point read so far, not merely the opening ones, adds up to.
    /// </summary>
    public H264EntryPointEvidence Evidence
    {
        get
        {
            if (_idrObserved)
            {
                return H264EntryPointEvidence.IdrObserved;
            }

            return _examinedAccessPoints >= MaximumExaminedAccessPoints
                ? H264EntryPointEvidence.RecoveryOnlyObserved
                : H264EntryPointEvidence.Insufficient;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a picture is currently being followed.
    /// </summary>
    public bool IsReadingPicture => _pendingPosition >= 0;

    /// <summary>
    /// Begins following the picture at a signalled access point.
    /// </summary>
    /// <remarks>
    /// Whatever was being read before must be concluded first, by <see cref="EndPicture"/> or
    /// <see cref="AbandonPicture"/>; this starts afresh and does not classify it.
    /// </remarks>
    /// <param name="position">Where the access point is.</param>
    public void BeginPicture(long position)
    {
        _pendingPosition = position;
        _pendingUnits = 0;
        _accessUnitScanner.Reset();
    }

    /// <summary>
    /// Notes that the packet about to be read starts a new payload unit, which bounds how far a
    /// picture whose syntax cannot be followed is pursued.
    /// </summary>
    public void NotePayloadUnitStart()
    {
        if (_pendingPosition >= 0)
        {
            _pendingUnits++;
        }
    }

    /// <summary>
    /// Reads more of the picture being followed.
    /// </summary>
    /// <param name="payload">The elementary stream bytes of one packet of the video PID.</param>
    /// <returns>
    /// The access point, if this reading concluded it; otherwise <see langword="null"/>.
    /// </returns>
    public ExaminedAccessPoint? Read(ReadOnlySpan<byte> payload)
    {
        // Bytes offered before any access point has been signalled are bytes from the middle of a
        // picture nobody may begin at. Reading them would let the next access point inherit them.
        if (_pendingPosition < 0)
        {
            return null;
        }

        _accessUnitScanner.Scan(payload);

        // An IDR settles it at once. Its absence has to be read to the end of the picture, and the
        // payload unit count bounds that for a stream whose syntax cannot be followed.
        if (_accessUnitScanner.Completed || _accessUnitScanner.CarriesIdr || _pendingUnits >= MaximumPayloadUnitsPerPicture)
        {
            return EndPicture();
        }

        return null;
    }

    /// <summary>
    /// Concludes the picture being followed and records what it opened on.
    /// </summary>
    /// <returns>
    /// The access point just concluded, or <see langword="null"/> if none was being followed.
    /// </returns>
    public ExaminedAccessPoint? EndPicture()
    {
        if (_pendingPosition < 0)
        {
            return null;
        }

        var point = new ExaminedAccessPoint(_pendingPosition, _accessUnitScanner.CarriesIdr);
        _pendingPosition = -1;

        _examinedAccessPoints++;
        _idrObserved |= point.CarriesIdr;

        // Only the first few decide how this stream opens. Everything after them is still read and
        // still counted towards the evidence, for the readers that join later.
        if (HasIdrEntryPoint is null && _openingAccessPoints < MaximumExaminedAccessPoints)
        {
            _openingAccessPoints++;

            if (point.CarriesIdr)
            {
                HasIdrEntryPoint = true;
            }
            else if (_openingAccessPoints >= MaximumExaminedAccessPoints)
            {
                HasIdrEntryPoint = false;
            }
        }

        return point;
    }

    /// <summary>
    /// Stops following the current picture without classifying it.
    /// </summary>
    /// <remarks>
    /// For the caller that has learned the bytes it was reading no longer describe what it thought
    /// -- a program layout change, above all. It drops the half-read picture and nothing else: an
    /// opening decision already reached was reached on pictures that were whole, and the evidence
    /// gathered so far was gathered the same way.
    /// </remarks>
    public void AbandonPicture() => _pendingPosition = -1;
}
