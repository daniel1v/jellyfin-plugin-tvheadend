using TVHeadEnd.Streaming;
using Xunit;

namespace TVHeadEnd.Tests.Streaming;

/// <summary>
/// The single reading of what an H.264 access point opens on, exercised on its own.
/// </summary>
/// <remarks>
/// Both callers -- the live conditioner and the recording probe -- get their answer from here, so
/// this is where the two statements it makes are pinned down: how the stream opened, which is
/// bounded and settled once, and what everything read so far adds up to, which is not.
/// </remarks>
public class H264AccessPointClassifierTests
{
    [Fact]
    public void AnIdrInTheFirstAccessPointSettlesItAtOnce()
    {
        var classifier = new H264AccessPointClassifier();

        Examine(classifier, 0, IdrPicture());

        Assert.True(classifier.HasIdrEntryPoint);
        Assert.Equal(H264EntryPointEvidence.IdrObserved, classifier.Evidence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FewerThanThreeRecoveryPointsSayNothingEitherWay(int accessPoints)
    {
        // A channel may mark random access on an open-GOP picture and on an IDR alternately, as
        // ZDF does. Concluding from one or two that there is no IDR-safe entry would apply the
        // workaround to about a third of its opens.
        var classifier = new H264AccessPointClassifier();

        for (var index = 0; index < accessPoints; index++)
        {
            Examine(classifier, index * 1000, RecoveryPicture());
        }

        Assert.Null(classifier.HasIdrEntryPoint);
        Assert.Equal(H264EntryPointEvidence.Insufficient, classifier.Evidence);
    }

    [Fact]
    public void ThreeRecoveryPointsAndNoIdrSettleIt()
    {
        // The Das Erste case, measured: signalled access points throughout and no IDR among them.
        var classifier = ThreeRecoveryPoints();

        Assert.False(classifier.HasIdrEntryPoint);
        Assert.Equal(H264EntryPointEvidence.RecoveryOnlyObserved, classifier.Evidence);
    }

    [Fact]
    public void AnIdrArrivingLaterImprovesTheEvidenceWithoutRewritingTheOpening()
    {
        // The two statements are different questions. How the stream opened is a fact about the
        // start that a reader has already acted on; what the stream contains keeps improving, and
        // one IDR anywhere is enough to say the material is not recovery-only.
        var classifier = ThreeRecoveryPoints();

        Examine(classifier, 4000, IdrPicture());

        Assert.False(classifier.HasIdrEntryPoint);
        Assert.Equal(H264EntryPointEvidence.IdrObserved, classifier.Evidence);
    }

    [Fact]
    public void RecoveryPointsArrivingAfterAnIdrDoNotUnsettleTheOpening()
    {
        // The opening is answered once. A reader told the stream opens on an IDR has started on
        // it, and an answer that turned over afterwards would describe a decision nobody made.
        var classifier = new H264AccessPointClassifier();

        Examine(classifier, 0, IdrPicture());
        Examine(classifier, 1000, RecoveryPicture());
        Examine(classifier, 2000, RecoveryPicture());

        Assert.True(classifier.HasIdrEntryPoint);
        Assert.Equal(H264EntryPointEvidence.IdrObserved, classifier.Evidence);
    }

    [Fact]
    public void APictureIsFollowedAcrossThePacketsItSpans()
    {
        // On the broadcasts measured the picture ends in the packet after the one the access point
        // is in, so a reading that stopped at the first packet would find no IDR anywhere.
        var classifier = new H264AccessPointClassifier();

        classifier.BeginPicture(700);
        Assert.Null(classifier.Read([0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x67, 0x42]));

        var point = classifier.Read([0x00, 0x00, 0x01, 0x65, 0x88]);

        Assert.Equal(new ExaminedAccessPoint(700, true), point);
        Assert.True(classifier.HasIdrEntryPoint);
    }

    [Fact]
    public void TheNextAccessPointEndsThePictureBeforeIt()
    {
        // A new picture has begun, so the one before it is as fully seen as it is going to be.
        var classifier = new H264AccessPointClassifier();

        classifier.BeginPicture(300);
        Assert.Null(classifier.Read(RecoveryPicture()));
        Assert.True(classifier.IsReadingPicture);

        var ended = classifier.EndPicture();

        Assert.Equal(new ExaminedAccessPoint(300, false), ended);
        Assert.False(classifier.IsReadingPicture);
        Assert.Null(classifier.EndPicture());
    }

    [Fact]
    public void AbandoningAPictureDropsThatPictureAndNothingElse()
    {
        // What a program layout change costs. The bytes being read no longer describe what the
        // reader thought, but the access points already read whole were read under the old tables
        // and were whole when they were counted.
        var classifier = new H264AccessPointClassifier();
        Examine(classifier, 0, RecoveryPicture());
        Examine(classifier, 1000, RecoveryPicture());

        classifier.BeginPicture(9000);
        classifier.Read(RecoveryPicture());
        classifier.AbandonPicture();

        // Half read is not read. Counting it would have been the third access point, and the
        // stream would have been called recovery-only on two whole pictures and one guess.
        Assert.False(classifier.IsReadingPicture);
        Assert.Null(classifier.HasIdrEntryPoint);
        Assert.Equal(H264EntryPointEvidence.Insufficient, classifier.Evidence);
    }

    [Fact]
    public void AbandoningAPictureLeavesASettledOpeningAlone()
    {
        var classifier = ThreeRecoveryPoints();

        classifier.BeginPicture(9000);
        classifier.Read(RecoveryPicture());
        classifier.AbandonPicture();

        Assert.False(classifier.HasIdrEntryPoint);
        Assert.Equal(H264EntryPointEvidence.RecoveryOnlyObserved, classifier.Evidence);
    }

    [Fact]
    public void BytesOfferedBeforeAnyAccessPointAreNotReadIntoTheNextOne()
    {
        var classifier = new H264AccessPointClassifier();

        Assert.Null(classifier.Read(IdrPicture()));
        Assert.Null(classifier.HasIdrEntryPoint);
        Assert.Equal(H264EntryPointEvidence.Insufficient, classifier.Evidence);

        Examine(classifier, 0, RecoveryPicture());

        Assert.Equal(H264EntryPointEvidence.Insufficient, classifier.Evidence);
    }

    private static H264AccessPointClassifier ThreeRecoveryPoints()
    {
        var classifier = new H264AccessPointClassifier();

        Examine(classifier, 0, RecoveryPicture());
        Examine(classifier, 1000, RecoveryPicture());
        Examine(classifier, 2000, RecoveryPicture());

        return classifier;
    }

    /// <summary>
    /// Reads one access point the way a caller walking transport stream packets does: the picture
    /// at the point, then the start of the picture after it, which is what ends the reading.
    /// </summary>
    private static void Examine(H264AccessPointClassifier classifier, long position, byte[] picture)
    {
        classifier.BeginPicture(position);
        classifier.Read(picture);

        if (classifier.IsReadingPicture)
        {
            classifier.NotePayloadUnitStart();
            classifier.Read(NextPicture());
        }
    }

    /// <summary>
    /// An access unit delimiter, a sequence parameter set and an IDR slice.
    /// </summary>
    private static byte[] IdrPicture()
        => [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x00, 0x00, 0x01, 0x65, 0x88];

    /// <summary>
    /// What an open GOP broadcast sends at a signalled access point instead: a recovery point
    /// message and an ordinary slice.
    /// </summary>
    private static byte[] RecoveryPicture()
        => [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x06, 0x06, 0x02, 0x00, 0x00, 0x00, 0x01, 0x61];

    private static byte[] NextPicture()
        => [0x00, 0x00, 0x01, 0x09, 0x10, 0x00, 0x00, 0x01, 0x61, 0x88];
}
