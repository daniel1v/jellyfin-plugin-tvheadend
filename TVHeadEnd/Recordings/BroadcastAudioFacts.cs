using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;
using TVHeadEnd.Streaming;

namespace TVHeadEnd.Recordings;

/// <summary>
/// Carries what the broadcast said about its audio tracks onto what FFprobe found.
/// </summary>
/// <remarks>
/// <para>
/// FFprobe reads a file; it does not read the DVB descriptors that say which track is the
/// programme's own sound and which is an addition to it. For a recording made with TVHeadend's
/// <c>pass</c> profile those descriptors are in the file, in the same program map the live path
/// already reads, so the two routes can describe the same broadcast the same way.
/// </para>
/// <para>
/// This is why it matters. Jellyfin narrows its audio candidates to the tracks marked default
/// whenever the viewer prefers default tracks -- the setting a new account is created with. With
/// no track marked at all the narrowing yields nothing, and Jellyfin then skips the codec check
/// rather than failing it: direct play is granted and labelled with whichever track came first,
/// MP2 included, to a client whose profile does not list MP2 at all. The client is handed audio
/// it cannot decode and gives up without an error anybody can see.
/// </para>
/// </remarks>
public static class BroadcastAudioFacts
{
    /// <summary>
    /// Marks the audio streams a broadcast offers as its own sound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order is the key, and nothing is moved by it. FFmpeg's transport stream demuxer creates
    /// streams as it walks the program map, so the audio tracks it reports and the audio entries
    /// of the map are the same tracks in the same sequence. The <em>n</em>th of one is the
    /// <em>n</em>th of the other; no stream is sorted, removed or renumbered, because Jellyfin
    /// addresses them by position.
    /// </para>
    /// <para>
    /// When the two disagree about how many audio tracks there are, nothing is claimed. A guess
    /// there would attach one track's descriptors to another, which is worse than the silence
    /// FFprobe left behind -- see <see cref="AudioPurposeExtensions.BelongsInTheDefaultSet"/> for
    /// what is being decided.
    /// </para>
    /// </remarks>
    /// <param name="streams">The streams as FFprobe reported them, in analysis order.</param>
    /// <param name="programMap">The program map read from the recording, if there is one.</param>
    /// <returns>Whether the broadcast's own account of its audio was applied.</returns>
    public static bool Apply(IReadOnlyList<MediaStream> streams, ProgramMapTable? programMap)
    {
        ArgumentNullException.ThrowIfNull(streams);

        if (programMap is null)
        {
            return false;
        }

        var probed = new List<MediaStream>();
        foreach (var stream in streams)
        {
            if (stream.Type == MediaStreamType.Audio)
            {
                probed.Add(stream);
            }
        }

        var announced = new List<ProgramMapEntry>();
        foreach (var entry in programMap.Entries)
        {
            if (entry.Kind == ElementaryStreamKind.Audio)
            {
                announced.Add(entry);
            }
        }

        if (probed.Count == 0 || probed.Count != announced.Count)
        {
            return false;
        }

        for (var i = 0; i < probed.Count; i++)
        {
            probed[i].IsDefault = announced[i].AudioPurpose.BelongsInTheDefaultSet();
        }

        return true;
    }
}
