namespace TVHeadEnd.Tvheadend
{
    /// <summary>
    /// What a TVHeadend stream profile is used for by this plugin.
    /// </summary>
    public enum StreamProfileRole
    {
        /// <summary>
        /// The broadcast as received. Required; defaults to TVHeadend's "pass" profile.
        /// </summary>
        Native = 0,

        /// <summary>
        /// An H.264 rendering of broadcasts whose codec many clients cannot decode.
        /// </summary>
        Mpeg2H264Compatibility = 1,

        /// <summary>
        /// An H.264 re-encode with genuine IDR access points.
        /// </summary>
        H264IdrNormalization = 2,
    }

    /// <summary>
    /// How far a role has been established.
    /// </summary>
    public enum StreamProfileState
    {
        /// <summary>
        /// No profile name is configured for the role.
        /// </summary>
        NotConfigured = 0,

        /// <summary>
        /// A name is configured but TVHeadend does not report a profile of that name. Discovery
        /// needs permission to read the API, so this is also what an unreadable server looks
        /// like; the role is not used in either case.
        /// </summary>
        NotFound = 1,

        /// <summary>
        /// Configured, and either found or not yet checked, but no output has been observed.
        /// </summary>
        NotValidated = 2,

        /// <summary>
        /// An opened stream of this role was observed to satisfy its contract.
        /// </summary>
        Validated = 3,

        /// <summary>
        /// An opened stream of this role violated its contract, so the role is not used.
        /// </summary>
        Invalid = 4,
    }
}
