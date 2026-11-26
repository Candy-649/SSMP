using SSMP.Util;

namespace SSMP.Game.Client;

/// <summary>
/// Who is hidden while a round is played. The local player sees nobody else while they play a round, nor anyone else's
/// attacks, and a player who plays a round is seen by nobody but those who watch them. A player who watches sees
/// neither their own hero nor their own game's parts of the watched place, which the picture of the round shows in
/// their stead (see <see cref="MinigameMirror"/>). What is hidden is kept from drawing (see
/// <see cref="KeptFromDrawing"/>).
/// </summary>
internal partial class MinigameCoop {
    /// <summary>
    /// What is hidden here.
    /// </summary>
    private readonly KeptFromDrawing _hidden = new();

    /// <summary>
    /// Hides who should not be seen this frame, and shows again what no longer needs hiding.
    /// </summary>
    private void UpdateHiding(HeroController hero, Game? local) {
        _hidden.Gather();

        var playing = local != null;
        foreach (var player in _playerData.Values) {
            if (!player.IsInLocalScene || player.PlayerContainer is not { } container || container == null) {
                continue;
            }

            var inRound = _memberGames.TryGetValue(player.Id, out var member) && member.Game != null;
            if (playing || inRound && player.Id != _watchedId) {
                _hidden.KeepOff(container, true);
            }
        }

        if (playing) {
            foreach (var attack in RemoteAttackComponent.Active) {
                if (attack != null) {
                    _hidden.KeepOff(attack.gameObject, true);
                }
            }
        }

        if (_watchedId != null) {
            _hidden.KeepOff(hero.gameObject, true);

            if (_mirror.ViewedParts is { } parts) {
                for (var index = 0; index < parts.Count; index++) {
                    if (parts[index] is { } part && part != null) {
                        _hidden.KeepOff(part, false);
                    }
                }
            }
        }

        _hidden.Apply();
    }

    /// <summary>
    /// Lets everything that was kept from drawing here draw again.
    /// </summary>
    private void ShowEverything() {
        _hidden.ShowEverything();
    }
}
