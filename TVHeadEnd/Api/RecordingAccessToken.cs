using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TVHeadEnd.Api
{
    /// <summary>
    /// Names a recording in a way that cannot be guessed.
    /// </summary>
    /// <remarks>
    /// The endpoint that serves recordings has to be reachable without a session, because FFmpeg
    /// fetches from it and Jellyfin stores the address on the item; its own live stream endpoint
    /// works the same way. What protects that one is the address itself -- an unguessable
    /// identifier. A TVHeadend recording identifier is a small number, so it is accompanied by a
    /// tag derived from a secret only the server knows, which turns enumeration back into
    /// guessing.
    /// </remarks>
    internal static class RecordingAccessToken
    {
        private const int TagLength = 16;

        /// <summary>
        /// Builds the token naming a recording.
        /// </summary>
        /// <param name="recordingId">The TVHeadend recording identifier.</param>
        /// <param name="secret">The server's secret.</param>
        /// <returns>The token.</returns>
        public static string Create(string recordingId, string secret)
        {
            ArgumentException.ThrowIfNullOrEmpty(recordingId);
            ArgumentException.ThrowIfNullOrEmpty(secret);

            return recordingId + "-" + Tag(recordingId, secret);
        }

        /// <summary>
        /// Reads the recording out of a token, refusing one that was not built from the secret.
        /// </summary>
        /// <param name="token">The token from the request.</param>
        /// <param name="secret">The server's secret.</param>
        /// <param name="recordingId">The recording named by the token.</param>
        /// <returns>Whether the token was genuine.</returns>
        public static bool TryRead(string? token, string secret, out string recordingId)
        {
            recordingId = string.Empty;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
            {
                return false;
            }

            var separator = token.LastIndexOf('-');
            if (separator <= 0 || separator == token.Length - 1)
            {
                return false;
            }

            var id = token[..separator];
            var tag = token[(separator + 1)..];

            // Compared in constant time so the tag cannot be recovered one character at a time.
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(tag),
                    Encoding.ASCII.GetBytes(Tag(id, secret))))
            {
                return false;
            }

            recordingId = id;
            return true;
        }

        /// <summary>
        /// Creates a secret for a server that has none yet.
        /// </summary>
        /// <returns>The secret.</returns>
        public static string CreateSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        private static string Tag(string recordingId, string secret)
        {
            var hash = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes(recordingId));

            return Convert.ToHexString(hash)[..TagLength].ToLower(CultureInfo.InvariantCulture);
        }
    }
}
