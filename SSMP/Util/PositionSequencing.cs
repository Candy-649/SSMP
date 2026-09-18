namespace SSMP.Util;

/// <summary>
/// Whether a position that has just arrived is newer than the last one taken, by the sequence number of the packet
/// each came in.
///
/// Nothing under this decides that for us. Steam's relay carries reliable and unreliable messages on one channel and
/// orders neither against the other, so a message held back while something before it was being sent again arrives
/// after messages that were sent later - and applying it puts whatever it describes back where it was a fifth of a
/// second earlier.
///
/// Kept in one place because getting it wrong does not look like a wrong position. It looks like the world stopping:
/// once the number to compare against is wrong, every position after it is thrown away and nothing moves again until
/// the room is left. That happened, so the two ways it can go wrong are both answered here rather than being left to
/// each caller to remember.
/// </summary>
internal static class PositionSequencing {
    /// <summary>
    /// How far behind the newest one a position may be and still be taken for one that merely arrived late.
    ///
    /// A reordering is a handful of packets at the very most. Anything further behind than this is not a late
    /// position at all, it is a sign that what is being compared against was never a real sequence number - so the
    /// numbering starts again from what just arrived, and one room's worth of standing still is the worst that any
    /// such mistake can cost.
    /// </summary>
    private const int ReorderWindow = 1024;

    /// <summary>
    /// Whether to take this position, keeping the number to compare the next one against.
    /// </summary>
    /// <param name="sequence">The sequence number of the packet the position arrived in.</param>
    /// <param name="last">The sequence number of the packet the last position taken arrived in.</param>
    /// <param name="has">Whether a position has been taken at all yet.</param>
    /// <returns>Whether the position is newer than the last one taken.</returns>
    public static bool IsNewer(ushort sequence, ref ushort last, ref bool has) {
        // Zero is what a position carries when nothing stamped it with the packet it came in - one that travelled
        // inside another kind of update, or one out of the pool that was never written to. There is nothing to
        // compare, so it is taken and nothing is remembered from it. The one real packet in every sixty-five
        // thousand that is numbered zero costs this nothing: the next one sets the number again.
        if (sequence == 0) {
            return true;
        }

        if (!has) {
            has = true;
            last = sequence;

            return true;
        }

        // Compared by the sign of the difference in the size the numbers are kept in, so that the step from the
        // largest back to zero reads as one forward rather than as sixty-five thousand back
        var difference = (short) (sequence - last);
        if (difference <= 0 && difference > -ReorderWindow) {
            return false;
        }

        last = sequence;

        return true;
    }
}
