namespace TVHeadEnd.Tvheadend
{
    /// <summary>
    /// The state of one role.
    /// </summary>
    /// <param name="Role">The role.</param>
    /// <param name="ProfileName">The configured TVHeadend profile name, which may be empty.</param>
    /// <param name="State">How far it has been established.</param>
    /// <param name="Detail">A short explanation for the settings page, if any.</param>
    public sealed record StreamProfileStatus(
        StreamProfileRole Role,
        string? ProfileName,
        StreamProfileState State,
        string? Detail = null)
    {
        /// <summary>
        /// Gets a value indicating whether the role may be used to serve a client.
        /// </summary>
        public bool IsUsable => State is StreamProfileState.NotValidated or StreamProfileState.Validated;
    }
}
