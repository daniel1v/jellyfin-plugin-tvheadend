using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Media;
using TVHeadEnd.Tvheadend;

namespace TVHeadEnd.Api
{
    /// <summary>
    /// What the settings page needs beyond the stored configuration: the state of the TVHeadend
    /// stream profiles, and a way to discard the analyses so they are rebuilt.
    /// </summary>
    [ApiController]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("TVHeadend")]
    public class TvHeadendConfigurationController : ControllerBase
    {
        private readonly HTSConnectionHandler _connectionHandler;
        private readonly ChannelMediaDescriptorStore _descriptors;
        private readonly ILogger<TvHeadendConfigurationController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvHeadendConfigurationController"/> class.
        /// </summary>
        /// <param name="connectionHandler">The TVHeadend connection.</param>
        /// <param name="descriptors">The channel descriptor store.</param>
        /// <param name="logger">The logger.</param>
        public TvHeadendConfigurationController(
            HTSConnectionHandler connectionHandler,
            ChannelMediaDescriptorStore descriptors,
            ILogger<TvHeadendConfigurationController> logger)
        {
            _connectionHandler = connectionHandler;
            _descriptors = descriptors;
            _logger = logger;
        }

        /// <summary>
        /// Reports which profile serves which role and how far each has been established.
        /// </summary>
        /// <returns>The status of every role.</returns>
        [HttpGet("StreamProfiles")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyList<StreamProfileStatusDto>> GetStreamProfiles()
        {
            var profiles = _connectionHandler.GetStreamProfiles();

            return Ok(profiles.GetStatus()
                .Select(status => new StreamProfileStatusDto
                {
                    Role = status.Role.ToString(),
                    ProfileName = status.ProfileName,
                    State = status.State.ToString(),
                    Detail = status.Detail,
                })
                .ToList());
        }

        /// <summary>
        /// Lists the stream profiles TVHeadend reports, for the settings page to offer.
        /// </summary>
        /// <returns>The names, empty when the server could not be asked.</returns>
        [HttpGet("StreamProfiles/Available")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyCollection<string>> GetAvailableProfiles()
            => Ok(_connectionHandler.GetStreamProfiles().GetDiscoveredProfiles() ?? []);

        /// <summary>
        /// Discards every stored channel description so they are established again.
        /// </summary>
        /// <remarks>
        /// The deliberate counterpart to the automatic invalidation. Schema changes and a changed
        /// native profile are noticed on their own; a change inside a TVHeadend profile is not,
        /// and this is how an administrator says so.
        /// </remarks>
        /// <returns>How many descriptions were discarded.</returns>
        [HttpPost("Channels/Reanalyze")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<int> ReanalyzeChannels()
        {
            var discarded = _descriptors.InvalidateAll();
            _logger.LogInformation(
                "TVHeadend channel descriptors: {Count} discarded on request; they are established again on the next refresh or playback",
                discarded);
            return Ok(discarded);
        }
    }
}
