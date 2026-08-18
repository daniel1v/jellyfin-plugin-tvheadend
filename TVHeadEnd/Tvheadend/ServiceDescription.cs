using System.Collections.Generic;
using System.Linq;

namespace TVHeadEnd.Tvheadend;

/// <summary>
/// What TVHeadend's HTTP API says a service contains.
/// </summary>
/// <param name="Uuid">The service's identity.</param>
/// <param name="Name">The service's display name.</param>
/// <param name="Components">
/// The components actually delivered, which is the filtered set where the server offers one.
/// </param>
public sealed record ServiceDescription(string Uuid, string? Name, IReadOnlyList<ServiceComponent> Components)
{
    /// <summary>
    /// Gets the PID of a component by TVHeadend's index for it.
    /// </summary>
    /// <param name="index">The <c>es_index</c>.</param>
    /// <returns>The PID, or <see langword="null"/> when the service has no such component.</returns>
    public int? GetPid(int index)
        => Components.FirstOrDefault(component => component.Index == index)?.Pid;

    /// <summary>
    /// Gets the PIDs the service carries.
    /// </summary>
    /// <returns>The PIDs.</returns>
    public IReadOnlySet<int> GetPids() => Components.Select(component => component.Pid).ToHashSet();
}
