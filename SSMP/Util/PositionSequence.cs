using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// Whether a position that has just arrived is newer than the last one taken, by the sequence number of the packet
/// each came in. One of these belongs to each thing whose position is followed.
///
/// Nothing under this decides that for us. Steam's relay carries reliable and unreliable messages on one channel and
/// orders neither against the other, so a message held back while something before it was being sent again arrives
/// after messages that were sent later - and applying it puts whatever it describes back where it was a fifth of a
/// second earlier.
///
/// Kept in one place because getting it wrong does not look like a wrong position. It looks like the world stopping:
/// once the number being compared against is wrong, every position after it is thrown away and nothing moves again.
/// That happened - a room's worth of enemies stood still for nine minutes at a time, through every attempt to leave
/// and come back - so this errs, three times over, on the side of taking a position it should not rather than
/// refusing one it should.
/// </summary>
internal sealed class PositionSequence {
    /// <summary>
    /// How far behind the newest one a position may be and still be taken for one that merely arrived late.
    ///
    /// A reordering is a handful of packets at the very most. Anything further behind than this is not a late
    /// position at all, it is a sign that what is being compared against was never a real sequence number - so the
    /// count starts again from what just arrived.
    /// </summary>
    private const int ReorderWindow = 1024;

    /// <summary>
    /// How many positions in a row may be refused before one is taken anyway and the count starts again from it.
    ///
    /// Positions arrive about sixty times a second, so this is a second of standing still: the most that any
    /// mistake in the numbering can cost, whatever the mistake turns out to be. Refusing this many in a row is not
    /// something that happens to a connection that is merely disordered - it only happens when the number being
    /// compared against is wrong - so there is nothing here to lose by giving in.
    /// </summary>
    private const int RefusalLimit = 60;

    /// <summary>
    /// The sequence number of the packet the last position taken arrived in.
    /// </summary>
    private ushort _last;

    /// <summary>
    /// Whether a position has been taken at all yet. Zero is a sequence number like any other - it comes round again
    /// every sixty-five thousand packets - so it cannot stand for "none".
    /// </summary>
    private bool _has;

    /// <summary>
    /// How many have been refused since the last one taken.
    /// </summary>
    private int _refused;

    /// <summary>
    /// What this follows the positions of, for the one line it ever writes.
    /// </summary>
    private readonly string _what;

    /// <param name="what">What this follows the positions of, named for the log.</param>
    public PositionSequence(string what) {
        _what = what;
    }

    /// <summary>
    /// Whether to take this position, keeping the number to compare the next one against.
    /// </summary>
    /// <param name="sequence">The sequence number of the packet the position arrived in.</param>
    /// <returns>Whether the position is newer than the last one taken.</returns>
    public bool Accepts(ushort sequence) {
        // Zero is what a position carries when nothing stamped it with the packet it came in - one that travelled
        // inside another kind of update, or one out of the pool that was never written to. There is nothing to
        // compare, so it is taken and nothing is remembered from it. The one real packet in every sixty-five
        // thousand that is numbered zero costs this nothing: the next one sets the number again.
        if (sequence == 0) {
            _refused = 0;

            return true;
        }

        if (_has) {
            // Compared by the sign of the difference in the size the numbers are kept in, so that the step from the
            // largest back to zero reads as one forward rather than as sixty-five thousand back
            var difference = (short) (sequence - _last);
            if (difference <= 0 && difference > -ReorderWindow && _refused < RefusalLimit) {
                _refused++;

                return false;
            }

            // Said out loud, because nothing else can tell afterwards whether something stood still because its
            // positions were being thrown away or because it was never sent any. A connection that is merely
            // disordered cannot reach this: a whole second of positions in a row, every one of them behind the
            // newest seen, only happens when the number being compared against was wrong to begin with.
            if (_refused >= RefusalLimit) {
                Logger.Warn(
                    $"Refused a second of positions of {_what} in a row, so the numbering of them was wrong: " +
                    $"taking {sequence} after {_last} and counting from it again"
                );
            }
        }

        _has = true;
        _last = sequence;
        _refused = 0;

        return true;
    }
}
