namespace TVHeadEnd.Tvheadend;

/// <summary>
/// One elementary stream of a TVHeadend service, as its HTTP API reports it.
/// </summary>
/// <param name="Index">
/// TVHeadend's <c>es_index</c>, which is what an HTSP stream description is keyed by. Absent for
/// the PCR and PMT entries the API reports alongside the real components.
/// </param>
/// <param name="Pid">The transport stream PID the component is carried on.</param>
/// <param name="Type">The stream type as TVHeadend names it.</param>
public sealed record ServiceComponent(int? Index, int Pid, string? Type);
