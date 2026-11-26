using System.Collections.Generic;
using UnityEngine;

namespace SSMP.Util;

/// <summary>
/// Renderers that one part of the mod keeps from drawing, rather than switching them off: the rest of the mod switches
/// the parts of the other players' characters on and off as they play, and the game does the same to its own, so
/// keeping them from drawing only adds to what they decide and takes nothing away from it when it ends. What is to be
/// kept from drawing is gathered anew each frame, and what is no longer gathered draws again. A renderer that something
/// else keeps from drawing already is left to it.
/// </summary>
internal class KeptFromDrawing {
    /// <summary>
    /// The renderers that are kept from drawing, which draw again once they are no longer gathered.
    /// </summary>
    private readonly HashSet<Renderer> _keptOff = [];

    /// <summary>
    /// The renderers gathered to be kept from drawing this frame.
    /// </summary>
    private readonly HashSet<Renderer> _keepOff = [];

    /// <summary>
    /// The renderers of one object, kept to be filled again.
    /// </summary>
    private readonly List<Renderer> _found = [];

    /// <summary>
    /// The renderers that draw again this frame, kept to be filled again.
    /// </summary>
    private readonly List<Renderer> _drawAgain = [];

    /// <summary>
    /// Whether anything is kept from drawing.
    /// </summary>
    public bool Any => _keptOff.Count > 0;

    /// <summary>
    /// Starts gathering what is to be kept from drawing this frame.
    /// </summary>
    public void Gather() {
        _keepOff.Clear();
    }

    /// <summary>
    /// Gathers the renderers of an object to be kept from drawing this frame, and those of everything under it if asked
    /// to.
    /// </summary>
    public void KeepOff(GameObject gameObject, bool withChildren) {
        if (withChildren) {
            gameObject.GetComponentsInChildren(true, _found);
        } else {
            gameObject.GetComponents(_found);
        }

        foreach (var renderer in _found) {
            _keepOff.Add(renderer);
        }
    }

    /// <summary>
    /// Keeps what was gathered from drawing, and lets what was kept and is no longer gathered draw again.
    /// </summary>
    public void Apply() {
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
    /// Lets everything that was kept from drawing draw again.
    /// </summary>
    public void ShowEverything() {
        foreach (var renderer in _keptOff) {
            if (renderer != null) {
                renderer.forceRenderingOff = false;
            }
        }

        _keptOff.Clear();
        _keepOff.Clear();
    }
}
