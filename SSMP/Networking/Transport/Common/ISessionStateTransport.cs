namespace SSMP.Networking.Transport.Common;

/// <summary>
/// A transport that can be asked whether it still has a link to the other side, separately from whether it can
/// currently put a number on how far away they are.
///
/// These are two different questions and answering them with one value cost a player their game: a relay that has
/// just rebuilt a broken session has a session and no measured round trip yet, which read as "there is no link" and
/// ended a game that was about to carry on.
/// </summary>
internal interface ISessionStateTransport {
    /// <summary>
    /// Whether the link to the other side is there or is being built, as opposed to closed, refused or given up on.
    /// </summary>
    bool SessionUp { get; }
}
