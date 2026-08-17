namespace TVHeadEnd.Playback
{
    /// <summary>
    /// Supplies the client context of the request being served.
    /// </summary>
    /// <remarks>
    /// An interface so the playback policy can be exercised without a web request, and so the
    /// one implementation that knows about Jellyfin's request pipeline stays at the adapter
    /// boundary.
    /// </remarks>
    public interface IPlaybackClientContextAccessor
    {
        /// <summary>
        /// Gets the context of the request in flight, or <see cref="PlaybackClientContext.None"/>
        /// when there is no request -- a scheduled task, or an internal call.
        /// </summary>
        PlaybackClientContext Current { get; }
    }
}
