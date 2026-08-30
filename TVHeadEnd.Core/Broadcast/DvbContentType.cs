using System;
using System.Collections.Generic;

namespace TVHeadEnd.Core.Broadcast;

/// <summary>
/// Reads the DVB <c>content_descriptor</c> byte TVHeadend forwards as <c>contentType</c>.
/// </summary>
/// <remarks>
/// The byte is two nibbles, defined by ETSI EN 300 468 table 28: the high one names the content
/// group and the low one refines it. Reading it as two nibbles is both what the standard says and
/// what keeps this to a table -- the shape it replaced enumerated all 256 combinations by hand,
/// which is the same information written out 256 times and wrong in the places somebody mistyped.
/// </remarks>
public static class DvbContentType
{
    private static readonly string[] MovieDrama =
    [
        "Movie", "Detective", "Adventure", "Science Fiction", "Comedy", "Soap",
        "Romance", "Historical", "Adult",
    ];

    private static readonly string[] NewsCurrentAffairs =
    [
        "News", "Weather", "News Magazine", "Documentary", "Discussion",
    ];

    private static readonly string[] Show = ["Show", "Game Show", "Variety", "Talk Show"];

    private static readonly string[] Sports =
    [
        "Sports", "Special Event", "Sports Magazine", "Football", "Tennis", "Team Sports",
        "Athletics", "Motor Sport", "Water Sport", "Winter Sports", "Equestrian",
        "Martial Sports",
    ];

    private static readonly string[] Children =
    [
        "Children", "Pre-school", "Entertainment (6 to 14)", "Entertainment (10 to 16)",
        "Informational", "Cartoons",
    ];

    private static readonly string[] Music = ["Music", "Rock/Pop", "Classical Music", "Folk Music", "Jazz", "Musical", "Ballet"];

    private static readonly string[] Arts =
    [
        "Arts", "Performing Arts", "Fine Arts", "Religion", "Popular Culture", "Literature",
        "Film", "Experimental Film", "Broadcasting", "Press", "New Media", "Arts Magazine",
        "Fashion",
    ];

    private static readonly string[] Social =
    [
        "Social", "Magazine", "Economics", "Remarkable People",
    ];

    private static readonly string[] Education =
    [
        "Education", "Nature", "Technology", "Medicine", "Expeditions", "Social/Spiritual",
        "Further Education", "Languages",
    ];

    private static readonly string[] Leisure =
    [
        "Leisure", "Tourism", "Handicraft", "Motoring", "Fitness", "Cooking", "Shopping",
        "Gardening",
    ];

    /// <summary>
    /// Describes a content type byte.
    /// </summary>
    /// <param name="contentType">The byte TVHeadend reported.</param>
    /// <returns>What it says about the programme.</returns>
    public static DvbContentDescription Describe(int contentType)
    {
        var major = (contentType & 0xF0) >> 4;
        var minor = contentType & 0x0F;

        var table = major switch
        {
            0x1 => MovieDrama,
            0x2 => NewsCurrentAffairs,
            0x3 => Show,
            0x4 => Sports,
            0x5 => Children,
            0x6 => Music,
            0x7 => Arts,
            0x8 => Social,
            0x9 => Education,
            0xA => Leisure,
            _ => null,
        };

        if (table is null)
        {
            return new DvbContentDescription([], false, false, false, false);
        }

        var genres = new List<string> { table[0] };

        // The low nibble refines the group. 0x0 is the group itself and 0xF means "user
        // defined", which says nothing beyond the group.
        if (minor > 0 && minor < table.Length)
        {
            genres.Add(table[minor]);
        }

        return new DvbContentDescription(
            genres,
            major == 0x1,
            major == 0x4,
            major == 0x2,
            major == 0x5);
    }
}
