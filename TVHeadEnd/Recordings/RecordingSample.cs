using System;
using System.IO;

namespace TVHeadEnd.Recordings
{
    /// <summary>
    /// The opening of one recording, on local disk, for as long as whoever holds this keeps it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A type rather than a path, because a path says nothing about whose job it is to delete the
    /// file. Whoever is handed one of these owns it and disposing it removes the file; nothing
    /// else deletes it, and nothing else is entitled to assume it still exists afterwards.
    /// </para>
    /// <para>
    /// It is a file rather than a stream on purpose. The analysis reads it three times over -- once
    /// by FFprobe, once for the broadcast's own tables, once for its access points -- and each of
    /// those wants to start at the beginning. A recording fetched over HTTP is not seekable; a copy
    /// of its first few megabytes is.
    /// </para>
    /// </remarks>
    public sealed class RecordingSample : IDisposable
    {
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingSample"/> class.
        /// </summary>
        /// <param name="path">The local file, which this instance now owns.</param>
        /// <param name="length">How many bytes were written to it.</param>
        public RecordingSample(string path, long length)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            Path = path;
            Length = length;
        }

        /// <summary>
        /// Gets the local file holding the sample.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets how many bytes of the recording were fetched.
        /// </summary>
        public long Length { get; }

        /// <summary>
        /// Creates an empty sample file nobody else can be holding.
        /// </summary>
        /// <remarks>
        /// Named for this run and nothing else, so two analyses of the same recording -- which the
        /// service is built to avoid but cannot make impossible -- never write to one file.
        /// </remarks>
        /// <returns>A path in the temporary directory.</returns>
        public static string CreatePath()
            => System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tvheadend-analysis-{Guid.NewGuid():N}.ts");

        /// <summary>
        /// Removes a sample file nobody is going to be handed.
        /// </summary>
        /// <remarks>
        /// For whoever is filling the file when the fetch fails part way: until it is handed over
        /// it is still theirs, and a half-written sample must not be left behind.
        /// </remarks>
        /// <param name="path">The file to remove.</param>
        public static void Discard(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Left behind in the temporary directory; harmless. It is a few megabytes the
                // operating system will clear, and failing a playback over it would be the worse
                // outcome by far.
            }
            catch (UnauthorizedAccessException)
            {
                // Left behind; harmless, for the same reason.
            }
        }

        /// <summary>
        /// Removes the file.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Discard(Path);
        }
    }
}
