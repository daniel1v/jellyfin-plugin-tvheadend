namespace TVHeadEnd.Configuration;

/// <summary>
/// The settings that say how this plugin should behave, rather than which server to talk to.
/// </summary>
/// <remarks>
/// Kept apart from the TVHeadend settings because they answer different questions and change for
/// different reasons: one says where the recordings live, this says how a viewer wants them
/// presented. Folding them together would mean a viewer adjusting their padding by a minute
/// looked, to everything downstream, exactly like the server having moved.
/// </remarks>
/// <param name="PrePaddingSeconds">How long before a programme a new recording starts.</param>
/// <param name="PostPaddingSeconds">How long after a programme a new recording runs on.</param>
/// <param name="HideRecordingsChannel">Whether the recordings channel is offered at all.</param>
/// <param name="UseChannelLogoWhereArtworkIsMissing">
/// Whether a programme or recording with no picture of its own borrows its channel's logo.
/// </param>
public sealed record PluginPreferences(
    int PrePaddingSeconds,
    int PostPaddingSeconds,
    bool HideRecordingsChannel,
    bool UseChannelLogoWhereArtworkIsMissing);
