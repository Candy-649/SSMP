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

    /// <summary>
    /// Says in words how the link to the other side stands, for the log of a connection that hears nothing.
    /// </summary>
    string DescribeSession();

    /// <summary>
    /// Writes down in the log how the link to the other side has stood each second, for the last
    /// <paramref name="secondsBack"/> seconds and from then on until things arrive from the other side again, so that a
    /// stretch in which nothing arrives can be placed afterwards. Nothing is written by a transport that keeps no
    /// record of its link.
    /// </summary>
    /// <param name="why">Why, as it ends a sentence of the log: "written down as ...".</param>
    /// <param name="secondsBack">How many seconds of the record to write down first.</param>
    void RecordSession(string why, int secondsBack);

    /// <summary>
    /// Whether the link to the other side counts as connected: neither still being built nor gone.
    /// </summary>
    bool SessionConnected { get; }

    /// <summary>
    /// Closes the link to the other side if it counts as connected, so that the next thing sent opens a new one, over
    /// a way to the other side that is found anew. For a link that has carried nothing for a while although it counts
    /// as connected: what was still on its way over it is lost.
    /// </summary>
    /// <returns>Whether it was closed: not a link that is still being built or is gone, nor one that never leaves the
    /// machine.</returns>
    bool RenewStalledSession();
}
