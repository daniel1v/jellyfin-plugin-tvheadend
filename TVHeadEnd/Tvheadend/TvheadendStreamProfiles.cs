using System;
using System.Collections.Generic;

namespace TVHeadEnd.Tvheadend
{
    /// <summary>
    /// Which TVHeadend profile serves which role, and how well each of them is established.
    /// </summary>
    /// <remarks>
    /// Profile names are configuration and never appear in playback logic, which speaks only in
    /// roles. The plugin does not create or alter TVHeadend profiles; an administrator does, and
    /// this reports what it finds.
    /// </remarks>
    public sealed class TvheadendStreamProfiles
    {
        /// <summary>
        /// The TVHeadend profile that forwards the broadcast untouched.
        /// </summary>
        public const string DefaultNativeProfile = "pass";

        private readonly Dictionary<StreamProfileRole, StreamProfileStatus> _status = [];
        private readonly object _gate = new();

        private IReadOnlyCollection<string>? _discovered;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvheadendStreamProfiles"/> class.
        /// </summary>
        /// <param name="nativeProfile">The profile serving the native role.</param>
        /// <param name="mpeg2CompatibilityProfile">The profile serving the MPEG-2 compatibility role.</param>
        public TvheadendStreamProfiles(
            string? nativeProfile,
            string? mpeg2CompatibilityProfile)
        {
            Set(StreamProfileRole.Native, string.IsNullOrWhiteSpace(nativeProfile) ? DefaultNativeProfile : nativeProfile.Trim());
            Set(StreamProfileRole.Mpeg2H264Compatibility, mpeg2CompatibilityProfile?.Trim());
        }

        /// <summary>
        /// Gets the profile name serving a role, or <see langword="null"/> when none is configured.
        /// </summary>
        /// <param name="role">The role.</param>
        /// <returns>The profile name.</returns>
        public string? GetProfileName(StreamProfileRole role)
        {
            lock (_gate)
            {
                return _status.TryGetValue(role, out var status) ? status.ProfileName : null;
            }
        }

        /// <summary>
        /// Gets the state of every role, for the settings page.
        /// </summary>
        /// <returns>The statuses.</returns>
        public IReadOnlyList<StreamProfileStatus> GetStatus()
        {
            lock (_gate)
            {
                return [.. _status.Values];
            }
        }

        /// <summary>
        /// Reports whether a role can currently be used to serve a client.
        /// </summary>
        /// <param name="role">The role.</param>
        /// <returns>Whether it is configured and not known to be broken.</returns>
        public bool IsUsable(StreamProfileRole role)
        {
            lock (_gate)
            {
                return _status.TryGetValue(role, out var status) && status.IsUsable;
            }
        }

        /// <summary>
        /// Reports whether an opened stream of a role was proven to keep the role's promise.
        /// </summary>
        /// <remarks>
        /// Stricter than <see cref="IsUsable"/>, and the question to ask before standing down
        /// something that already works: a configured, discovered profile is only a claim until a
        /// stream of it has been opened and inspected.
        /// </remarks>
        /// <param name="role">The role.</param>
        /// <returns>Whether the role has been proven.</returns>
        public bool IsValidated(StreamProfileRole role)
        {
            lock (_gate)
            {
                return _status.TryGetValue(role, out var status) && status.State == StreamProfileState.Validated;
            }
        }

        /// <summary>
        /// Marks a role as proven from what an earlier run recorded.
        /// </summary>
        /// <remarks>
        /// Only honoured while the configured profile is still the one that was proven. Point a
        /// role at a different profile and it starts again as an unproven claim.
        /// </remarks>
        /// <param name="role">The role.</param>
        /// <param name="profileName">The profile the earlier run proved.</param>
        public void RestoreValidation(StreamProfileRole role, string profileName)
        {
            lock (_gate)
            {
                if (!_status.TryGetValue(role, out var status)
                    || !string.Equals(status.ProfileName, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _status[role] = status with { State = StreamProfileState.Validated, Detail = null };
            }
        }

        /// <summary>
        /// Records which profiles TVHeadend reports, so the settings page can say whether a
        /// configured name exists.
        /// </summary>
        /// <param name="profileNames">The names TVHeadend reports, or <see langword="null"/> when discovery failed.</param>
        public void ApplyDiscovery(IReadOnlyCollection<string>? profileNames)
        {
            lock (_gate)
            {
                _discovered = profileNames;
                if (profileNames is null)
                {
                    return;
                }

                foreach (var role in _status.Keys)
                {
                    var status = _status[role];
                    if (string.IsNullOrEmpty(status.ProfileName))
                    {
                        continue;
                    }

                    var found = false;
                    foreach (var name in profileNames)
                    {
                        if (string.Equals(name, status.ProfileName, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }

                    // A role already proven by an opened stream is not demoted by a failed
                    // lookup: the evidence of it working outranks the listing.
                    if (status.State == StreamProfileState.Validated)
                    {
                        continue;
                    }

                    _status[role] = status with
                    {
                        State = found ? StreamProfileState.NotValidated : StreamProfileState.NotFound,
                        Detail = found ? null : "TVHeadend does not report a profile of this name",
                    };
                }
            }
        }

        /// <summary>
        /// Records what an opened stream of a role turned out to be.
        /// </summary>
        /// <param name="role">The role.</param>
        /// <param name="satisfiesContract">Whether the output matched what the role promises.</param>
        /// <param name="detail">What was observed, for the settings page.</param>
        public void RecordValidation(StreamProfileRole role, bool satisfiesContract, string? detail = null)
        {
            lock (_gate)
            {
                if (!_status.TryGetValue(role, out var status) || string.IsNullOrEmpty(status.ProfileName))
                {
                    return;
                }

                _status[role] = status with
                {
                    State = satisfiesContract ? StreamProfileState.Validated : StreamProfileState.Invalid,
                    Detail = detail,
                };
            }
        }

        /// <summary>
        /// Gets the profiles TVHeadend reported, or <see langword="null"/> when discovery has not
        /// succeeded.
        /// </summary>
        /// <returns>The discovered names.</returns>
        public IReadOnlyCollection<string>? GetDiscoveredProfiles()
        {
            lock (_gate)
            {
                return _discovered;
            }
        }

        private void Set(StreamProfileRole role, string? profileName)
        {
            _status[role] = new StreamProfileStatus(
                role,
                profileName,
                string.IsNullOrEmpty(profileName) ? StreamProfileState.NotConfigured : StreamProfileState.NotValidated);
        }
    }
}
