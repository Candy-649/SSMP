using System.Collections.Generic;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Game.Client;

/// <summary>
/// Who is hidden while a round is played. The local player sees nobody else while they play a round, nor anyone else's
/// attacks, and a player who plays a round is seen by nobody but those who watch them. A player who watches sees
/// neither their own hero nor their own game's parts of the watched place, which the picture of the round shows in
/// their stead (see <see cref="MinigameMirror"/>).
///
/// What is hidden is kept from drawing, rather than switched off: the rest of the mod switches the parts of the other
/// players' characters on and off as they play, and the game does the same to its own, so hiding only adds to what
/// they decide and takes nothing away from it when it ends.
/// </summary>
internal partial class MinigameCoop {
    /// <summary>
    /// The renderers that are kept from drawing here, which draw again once nothing keeps them from it.
    /// </summary>
    private readonly HashSet<Renderer> _keptOff = [];

    /// <summary>
    /// The renderers that are to be kept from drawing this frame.
    /// </summary>
    private readonly HashSet<Renderer> _keepOff = [];

    /// <summary>
    /// The renderers of one object, kept to be filled again.
    /// </summary>
    private readonly List<Renderer> _foundRenderers = [];

    /// <summary>
    /// The renderers that draw again this frame, kept to be filled again.
    /// </summary>
    private readonly List<Renderer> _drawAgain = [];

    /// <summary>
    /// Hides who should not be seen this frame, and shows again what no longer needs hiding.
    /// </summary>
    private void UpdateHiding(HeroController hero, Game? local) {
        _keepOff.Clear();

        var playing = local != null;
        foreach (var player in _playerData.Values) {
            if (!player.IsInLocalScene || player.PlayerContainer is not { } container || container == null) {
                continue;
            }

            var inRound = _memberGames.TryGetValue(player.Id, out var member) && member.Game != null;
            if (playing || inRound && player.Id != _watchedId) {
                KeepOff(container, true);
            }
        }

        if (playing) {
            foreach (var attack in RemoteAttackComponent.Active) {
                if (attack != null) {
                    KeepOff(attack.gameObject, true);
                }
            }
        }

        if (_watchedId != null) {
            KeepOff(hero.gameObject, true);

            if (_mirror.ViewedParts is { } parts) {
                for (var index = 0; index < parts.Count; index++) {
                    if (parts[index] is { } part && part != null) {
                        KeepOff(part, false);
                    }
                }
            }
        }

        foreach (var renderer in _keepOff) {
            if (renderer != null && !renderer.forceRenderingOff) {
                renderer.forceRenderingOff = true;
                _keptOff.Add(renderer);
            }
        }

        _drawAgain.Clear();
        foreach (var renderer in _keptOff) {
            if (!_keepOff.Contains(renderer)) {
                _drawAgain.Add(renderer);
            }
        }

        foreach (var renderer in _drawAgain) {
            _keptOff.Remove(renderer);
            if (renderer != null) {
                renderer.forceRenderingOff = false;
            }
        }
    }

    /// <summary>
    /// Keeps the renderers of an object from drawing this frame, and those of everything under it if asked to.
    /// </summary>
    private void KeepOff(GameObject gameObject, bool withChildren) {
        if (withChildren) {
            gameObject.GetComponentsInChildren(true, _foundRenderers);
        } else {
            gameObject.GetComponents(_foundRenderers);
        }

        foreach (var renderer in _foundRenderers) {
            _keepOff.Add(renderer);
        }
    }

    /// <summary>
    /// Lets everything that was kept from drawing here draw again.
    /// </summary>
    private void ShowEverything() {
        foreach (var renderer in _keptOff) {
            if (renderer != null) {
                renderer.forceRenderingOff = false;
            }
        }

        _keptOff.Clear();
        _keepOff.Clear();
    }
}
