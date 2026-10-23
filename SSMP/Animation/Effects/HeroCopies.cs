using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// The copies that show what other players do: their slashes, strikes, silk skills, binds, tools and the rest are
/// copies of the local hero's own objects, or of the game's own, as the game has each of those once. A copy drew from
/// the same sheets of sprites as its original, so it showed in the local player's skin and cloak colour (USER 10-23,
/// "攻击之类的不可以联机另一边也看得到变化嘛"). Each copy is now dressed as the character of the player it shows,
/// before anything on it wakes: it draws from that character's own copies of the sheets, which wear what that player
/// wears, as the character itself does. Before anything wakes, as Player Color decides once for each object, the first
/// time it is drawn, whether it is the local player's to colour.
/// </summary>
internal static class HeroCopies {
    /// <summary>
    /// Dresses a copy that is still switched off as the character of another player: the character, and the copy (see
    /// <see cref="Game.Client.PlayerManager.DressHeroCopy"/>).
    /// </summary>
    internal static Action<GameObject, GameObject>? Dress;

    /// <summary>
    /// The character of another player that a part of the world belongs to, or null for none (see
    /// <see cref="Game.Client.PlayerManager.CharacterOf"/>).
    /// </summary>
    internal static Func<Transform, GameObject?>? CharacterOf;

    /// <summary>
    /// Where copies are made, switched off, so that nothing on them wakes before they are dressed.
    /// </summary>
    private static GameObject? _holder;

    /// <summary>
    /// Whether dressing a copy threw, which is only logged once.
    /// </summary>
    private static bool _dressFailed;

    /// <summary>
    /// Makes a copy of an object under a part of the world, dressed as the given character of another player, or else
    /// as the one that the part belongs to, and placed under it as <see cref="Object.Instantiate(Object, Transform)"/>
    /// places a copy. For no character, it is a plain copy.
    /// </summary>
    /// <param name="original">The object of the local hero, or the game's own, to copy.</param>
    /// <param name="parent">Where the copy goes.</param>
    /// <param name="character">The character whose it is, for a copy under a part of the room.</param>
    /// <returns>The copy.</returns>
    public static GameObject Make(GameObject original, Transform parent, GameObject? character = null) {
        var copy = Object.Instantiate(original, Holder(), false);
        var dressedAs = character != null ? character : CharacterOf?.Invoke(parent);
        if (dressedAs != null) {
            DressCopy(dressedAs, copy);
        }

        copy.transform.SetParent(parent, false);
        return copy;
    }

    /// <summary>
    /// Makes a copy of an object for the character of another player that goes into the room by itself, at a place,
    /// dressed as that character, as <see cref="Object.Instantiate(Object, Vector3, Quaternion)"/> places a copy.
    /// </summary>
    /// <param name="original">The object of the local hero, or the game's own, to copy.</param>
    /// <param name="character">The character of the player whose it is.</param>
    /// <param name="position">Where it goes.</param>
    /// <param name="rotation">How it is turned.</param>
    /// <returns>The copy.</returns>
    public static GameObject Make(GameObject original, GameObject character, Vector3 position, Quaternion rotation) {
        var copy = Object.Instantiate(original, position, rotation, Holder());
        DressCopy(character, copy);

        // Into the room that is loaded, where a copy made without a parent goes, before anything on it wakes
        var switchedOn = copy.activeSelf;
        copy.SetActive(false);
        copy.transform.SetParent(null, true);
        SceneManager.MoveGameObjectToScene(copy, SceneManager.GetActiveScene());
        copy.SetActive(switchedOn);
        return copy;
    }

    /// <summary>
    /// Dresses a copy that is still switched off as the character of another player.
    /// </summary>
    /// <param name="character">The character.</param>
    /// <param name="copy">The copy.</param>
    public static void DressCopy(GameObject character, GameObject copy) {
        try {
            Dress?.Invoke(character, copy);
        } catch (Exception e) {
            if (!_dressFailed) {
                _dressFailed = true;
                Logger.Error($"Could not dress '{copy.name}' as the character of another player:\n{e}");
            }
        }
    }

    /// <summary>
    /// The holder that copies are made in, switched off and kept from room to room.
    /// </summary>
    private static Transform Holder() {
        if (_holder == null) {
            _holder = new GameObject("Partner Effect Copies");
            _holder.SetActive(false);
            Object.DontDestroyOnLoad(_holder);
        }

        return _holder.transform;
    }
}
