using System.Collections.Generic;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What a creature let go of into the room, said again to a player who walks in after it did. A creature that hides
/// under a crust of coral puts the crust into the room as it settles in to wait ('Hiding', which is none of the states
/// that the copy runs as it is made, see EntityInitializer) and breaks out of it when a player comes near. The game
/// that runs the creature sends what its actions do as they do it, and nothing of it is kept for later; and what a
/// creature does in the first frames of its room, before the games have settled which of them runs it, went nowhere at
/// all. The copy of a player who walked in, or who was there before that was settled, kept the crust under it and went
/// about with it once it woke (USER 11-13 "有个藏在一堆红色的刺团的怪…在我这边表现不太对劲", and of the friend's game
/// "我朋友看到它身上沾了一堆场景物体在走").
///
/// Each part that the creature let go of and that is loose in the room now goes out again as the action that let go of
/// it, with where the part is in the room by now (see LetGoParts.SayAgain): the other game's copy lets go of its own
/// part as it does when the action comes as it happens, and puts it there. As this game takes a creature over, the
/// room's own creature, which slept here and never ran the action either, lets go of each part of its own that the copy
/// let go of, where the copy's is (see <see cref="HandOverWhatTheCopyLetGo"/>). And the room's own creature of a game
/// that expected to run the room, and so ran its first states, takes back what it let go of meanwhile as it is put to
/// sleep (see <see cref="TakeBackWhatItLetGo"/>).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// Sends again how the creature let go of its parts into the room (see the summary of this part), for a player who
    /// walked into the room of this game, which runs it. Each part goes once, by the first of the creature's actions
    /// that lets go of it into the room as its variables hold it now.
    /// </summary>
    internal void SendLetGoPartsAgain() {
        if (_isControlled || Object.Host == null || !Object.Host.activeInHierarchy) {
            return;
        }

        // Not what the creature grew from a pool and let go of, like a spine that a floater shot: the copy has a
        // thing at that place only if it grew one again (SendGrownPartsAgain), which is not the one let go of here
        var loose = new List<GameObject>();
        LetGoParts.PartsOf(Object.Host, loose);
        loose.RemoveAll(part => part.transform.parent != null || part.name.Contains("(Clone)"));
        if (loose.Count == 0) {
            return;
        }

        using (LetGoParts.SayAgain()) {
            foreach (var hooked in _hookedActions.Values) {
                if (EntityFsmActions.LetGoIntoTheRoomBy(hooked.Action.Fsm, hooked.Action) is not { } part ||
                    !loose.Remove(part)) {
                    continue;
                }

                SendReplay(hooked, null);
                Logger.Info(
                    $"[Let go] Said again that '{Object.Host.name}' ({Id}) let go of its part '{part.name}' into the " +
                    $"room through '{hooked.Action.State?.Name}' of '{hooked.Action.Fsm.Name}', with where it is now"
                );
            }
        }
    }

    /// <summary>
    /// Has the room's own creature take back each part it let go of while this game expected to run the room, as it is
    /// put to sleep because the other game runs it after all: back at its place under it, where it is as it would have
    /// been had the creature slept from the start, it sleeps with it, and the copy's part shows the other game's.
    /// Left in the room, the crust of a creature that hides in coral stayed where the room's own creature had put it as
    /// it settled in to wait, to be seen and to hurt there after the other game's creature broke out of its own crust,
    /// whose copy here broke with it. It is found there too as this game takes the creature over (see
    /// <see cref="HandOverWhatTheCopyLetGo"/>). The shallowest places first: a part let go of from under another such
    /// part goes back under that one once it is back.
    /// </summary>
    private void TakeBackWhatItLetGo() {
        var letGo = new List<GameObject>();
        LetGoParts.PartsOf(Object.Host, letGo);
        var places = new List<(Transform Part, string Place)>();
        foreach (var part in letGo) {
            // Not what it grew from a pool and let go of, like a spine that a floater shot, which ends by itself and
            // goes back to its pool, as it is left out of what goes out again (SendLetGoPartsAgain): taken back, it
            // stayed under the creature where nothing lets it go again at a takeover
            if (!part.name.Contains("(Clone)") && LetGoParts.PlaceOf(Object.Host, part) is { } place) {
                places.Add((part.transform, place));
            }
        }

        places.Sort((a, b) => a.Place.Split('/').Length.CompareTo(b.Place.Split('/').Length));
        foreach (var (part, place) in places) {
            var cut = place.LastIndexOf('/');
            var under = cut < 0 ? Object.Host.transform : Object.Host.transform.Find(place.Substring(0, cut));
            if (under == null) {
                continue;
            }

            part.SetParent(under, true);
            Logger.Info(
                $"[Let go] The room's own '{Object.Host.name}' ({Id}) takes back its part '{place}', which it let go " +
                "of while this game expected to run the room, as it is put to sleep"
            );
        }
    }

    /// <summary>
    /// Has the room's own creature let go of each of its parts that the copy let go of into the room, as this game
    /// takes the creature over (see the summary of this part): it slept here while the other game ran the creature and
    /// never ran the action that let go of it, so it went on with the part under it. Each is put where the copy's is,
    /// and switched on or off as that is; the copy's goes off with the copy (see
    /// EntityFsmActions.SwitchOffWhatTheCopyLetGo). Only what was made with the copy, whose twin the room's own
    /// creature was built with, and the deepest places first: a part let go of from under another such part is found
    /// under the room's own twin of that part before that twin is let go of too.
    /// </summary>
    private void HandOverWhatTheCopyLetGo() {
        var letGo = new List<GameObject>();
        LetGoParts.PartsOf(Object.Client, letGo);
        var places = new List<(Transform Part, string Place)>();
        foreach (var part in letGo) {
            if (part.transform.parent == null && System.Array.IndexOf(_madeWithCopy, part.transform) >= 0 &&
                LetGoParts.PlaceOf(Object.Client, part) is { } place) {
                places.Add((part.transform, place));
            }
        }

        places.Sort((a, b) => b.Place.Split('/').Length.CompareTo(a.Place.Split('/').Length));
        foreach (var (part, place) in places) {
            if (Object.Host.transform.Find(place) is not { } own) {
                continue;
            }

            LetGoParts.NoteLetGo(own.gameObject, null);
            own.SetParent(null, true);
            own.SetPositionAndRotation(part.position, part.rotation);
            own.localScale = part.localScale;
            own.gameObject.SetActive(part.gameObject.activeSelf);
            Logger.Info(
                $"[Let go] The room's own '{Object.Host.name}' ({Id}) lets go of its part '{place}' where the copy " +
                "had let go of its own, as this game takes it over"
            );
        }
    }
}
